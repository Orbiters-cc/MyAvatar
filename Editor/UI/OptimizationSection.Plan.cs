using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;

namespace Orbiters.MyAvatar.Editor
{
    // "Reach a rank": a target rank, how far each category VRChat ranks is from it at upload (PC and Quest side by side when
    // the avatar has or plans a Quest version), what costs most in each, and the steps that close the gap, best first. All
    // from the estimate the card has already: nothing is measured for it. A setting's step applies at once; its savings
    // show once the estimate of the new settings is in.
    internal sealed partial class OptimizationSection
    {
        private const string BothPlatformsPref = "Orbiters.MyAvatar.RankPlan.BothPlatforms";
        private const string TargetKey = "Orbiters.MyAvatar.RankPlan.Target.";
        private const int ShownCostliest = 8;

        private readonly VisualElement plan = new VisualElement();
        private string shownPlan;
        // The category whose costliest things show under the columns (and on which platform), or none.
        private (bool Mobile, AvatarPerformanceCategory Category)? openGap;
        // Whether the avatar has a version for the other platform, found once per estimate.
        private OptimizationEstimate.Result counterpartOf;
        private bool hasCounterpart;
        private IVisualElementScheduledItem planRetry;

        private string TargetPref => TargetKey + avatar.GetInstanceID();

        private PerformanceRating? ChosenTarget
        {
            get { int value = SessionState.GetInt(TargetPref, -1); return value < 0 ? (PerformanceRating?)null : (PerformanceRating)value; }
            set { if (value == null) SessionState.EraseInt(TargetPref); else SessionState.SetInt(TargetPref, (int)value.Value); }
        }

        private void ShowPlan(OptimizationEstimate.Result result)
        {
            // Measured again: the last plan stays, dimmed, so nothing under the pointer moves.
            plan.EnableInClassList("opt-plan--measuring", result == null && plan.childCount > 0);
            if (result == null) return;
            if (result.Error != null || result.Now?.PcRank == null || result.Upload?.PcRank == null)
            {
                if (shownPlan != null) { plan.Clear(); shownPlan = null; }
                return;
            }
            // The SDK loads its rank levels on the first editor frame after a reload.
            if (!AvatarBudget.Ready)
            {
                planRetry?.Pause();
                planRetry = schedule.Execute(() => { shownPlan = null; Refresh(); }).StartingIn(500);
                return;
            }
            if (!ReferenceEquals(counterpartOf, result))
            {
                counterpartOf = result;
                try { hasCounterpart = QuestParity.CounterpartOf(TextureOptimization.AvatarRoot(avatar)) != null; }
                catch (Exception) { hasCounterpart = false; }
            }
            bool quest = Quest, both = hasCounterpart || EditorPrefs.GetBool(BothPlatformsPref, false);
            var primaryNow = result.Now.Rank(quest).Overall;
            var target = ChosenTarget ?? RankPlan.DefaultTarget(primaryNow, result.Upload.Rank(quest).Overall);
            string state = RuntimeHelpers.GetHashCode(result) + ":" + quest + ":" + both + ":" + target + ":" + openGap + ":" + Settings.level + ":" + (int)Settings.custom;
            if (state == shownPlan) return;
            shownPlan = state;
            try { BuildPlan(result, target, quest, both); }
            catch (Exception ex)
            {
                plan.Clear();
                Debug.LogException(ex);
            }
        }

        private void BuildPlan(OptimizationEstimate.Result result, PerformanceRating target, bool quest, bool both)
        {
            plan.Clear();
            var card = new VisualElement(); card.AddToClassList("opt-panel"); card.AddToClassList("opt-plan"); plan.Add(card);

            // The platforms planned: PC then Quest, the one the project builds for always.
            var platforms = both ? new[] { false, true } : new[] { quest };
            var plans = platforms.Select(m => RankPlan.For(result.Now.Rank(m), result.Upload.Rank(m), target, m)).ToList();
            var primary = plans.First(p => p.Mobile == quest);

            var head = new VisualElement(); head.AddToClassList("opt-panel__head"); card.Add(head);
            var title = new Label("Reach a rank"); title.AddToClassList("opt-estimate__title"); head.Add(title);
            var picker = new SegmentedControl(RankPlan.Targets.Select(r => new SegmentedControl.Option(RankSteps.RankName(r), null,
                "Plan for " + RankSteps.RankName(r) + ": what holds the avatar below it at upload, and what brings it there.")), index =>
            {
                ChosenTarget = RankPlan.Targets[index];
                openGap = null;
                // The picker moves at once; the plan follows on the next frame.
                schedule.Execute(() => { shownPlan = null; Refresh(); });
            });
            picker.AddToClassList("orbiters-segmented--compact"); picker.AddToClassList("opt-plan__target");
            picker.SetIndex(Array.IndexOf(RankPlan.Targets, target));
            head.Add(picker);

            var steps = RankSteps.For(Settings, result.Upload, plans, s => OptimizationEstimate.Known(avatar, s)?.Upload);
            int needed = RankSteps.Needed(steps, plans, out string shortOf);
            var summary = new Label(Summary(plans, target, needed, shortOf, steps)); summary.AddToClassList("opt-panel__text"); summary.AddToClassList("opt-plan__summary");
            summary.EnableInClassList("opt-plan__summary--done", plans.All(p => p.Reached));
            card.Add(summary);

            var columns = new VisualElement(); columns.AddToClassList("opt-plan__columns"); card.Add(columns);
            foreach (var platform in plans) columns.Add(Column(platform, plans.Count > 1));

            if (openGap.HasValue)
            {
                var (openMobile, openCategory) = openGap.Value;
                var open = plans.FirstOrDefault(p => p.Mobile == openMobile)?.For(openCategory);
                if (open != null) card.Add(Costliest(result.Upload.Rank(openMobile), open, plans.Count > 1 ? (openMobile ? Mobile : "PC") : null));
            }

            if (steps.Count > 0)
            {
                var list = new VisualElement(); list.AddToClassList("opt-plan__steps"); card.Add(list);
                var label = new Label("Steps"); label.AddToClassList("opt-plan__label"); list.Add(label);
                int index = 0;
                foreach (var step in steps.Take(6)) list.Add(StepRow(step, plans, ++index));
            }

            if (!hasCounterpart)
            {
                string other = quest ? "PC" : Mobile;
                var toggle = MyAvatarEditor.PressButton(both ? "Plan for " + (quest ? Mobile : "PC") + " only" : "Plan for " + other + " too", () =>
                {
                    EditorPrefs.SetBool(BothPlatformsPref, !EditorPrefs.GetBool(BothPlatformsPref, false));
                    openGap = null;
                    shownPlan = null;
                    Refresh();
                });
                toggle.AddToClassList("opt-plan__platforms");
                toggle.tooltip = "The other platform shows on its own when this avatar has a version for it (a blueprint uploaded there, or per-platform overrides).";
                card.Add(toggle);
            }
        }

        private string Summary(List<RankPlan> plans, PerformanceRating target, int needed, string shortOf, List<RankStep> steps)
        {
            string name = RankSteps.RankName(target);
            string where = plans.Count > 1 ? "PC and " + Mobile : plans[0].Mobile ? Mobile : "PC";
            if (plans.All(p => p.Reached)) return name + " or better on " + where + " at upload: nothing holds it back.";
            var blocking = plans.SelectMany(p => p.Blocking).Select(g => g.Category).Distinct().Count();
            string held = blocking == 1 ? "One category holds it back" : blocking + " categories hold it back";
            if (needed > 0) return held + ". " + (needed == 1 ? "One step reaches " : needed + " steps reach ") + name + " on " + where + ".";
            bool untried = steps.Any(s => s.Kind == RankStep.Kinds.Setting && !s.Known);
            return held + (shortOf != null ? ". With the steps measured so far, " + shortOf : "") + "." +
                   (untried ? " A setting not tried yet may close the rest: its savings show once it's on." : "");
        }

        // One platform: its rank at upload against the target, and a bar for each category that holds it back (or the
        // closest ones when none does).
        private VisualElement Column(RankPlan plan, bool labelled)
        {
            var column = new VisualElement(); column.AddToClassList("opt-plan__column");
            var head = new VisualElement(); head.AddToClassList("opt-rank"); column.Add(head);
            var platform = new Label(plan.Mobile ? Mobile : "PC"); platform.AddToClassList("opt-rank__platform"); head.Add(platform);
            head.Add(Chip(plan.Upload));
            if (plan.Upload != plan.Target && !plan.Reached)
            {
                var arrow = new Label("→"); arrow.AddToClassList("opt-rank__arrow"); head.Add(arrow);
                var goal = Chip(plan.Target); goal.AddToClassList("opt-chip--target"); head.Add(goal);
            }
            else if (plan.Reached)
            {
                var done = new VectorIcon(IconGlyph.Check); done.AddToClassList("opt-plan__done"); head.Add(done);
            }
            head.tooltip = plan.Mobile ? "As " + Mobile + " ranks this avatar, with the textures its " + Mobile + " upload would get." : "As VRChat on PC ranks the uploaded avatar.";

            var gaps = plan.Blocking.ToList();
            if (gaps.Count == 0) gaps = plan.Gaps.Where(g => g.Counted).Take(3).ToList();
            foreach (var gap in gaps) column.Add(GapRow(plan, gap));
            if (gaps.Count == 0)
            {
                var none = new Label("Every category is Excellent."); none.AddToClassList("opt-panel__text"); column.Add(none);
            }
            return column;
        }

        private VisualElement GapRow(RankPlan plan, RankPlan.Gap gap)
        {
            var row = new VisualElement(); row.AddToClassList("opt-gap");
            bool open = openGap.HasValue && openGap.Value.Mobile == plan.Mobile && openGap.Value.Category == gap.Category;
            row.EnableInClassList("opt-gap--open", open);
            row.EnableInClassList("opt-gap--blocks", gap.Blocks);
            var line = new VisualElement(); line.AddToClassList("opt-gap__line"); row.Add(line);
            var name = new Label(gap.Name); name.AddToClassList("opt-gap__name"); line.Add(name);
            if (gap.Counted)
            {
                var values = new Label(RankSteps.Amount(gap.Category, gap.Upload) + " / " + RankSteps.Amount(gap.Category, gap.Limit));
                values.AddToClassList("opt-gap__values"); line.Add(values);
                if (gap.Blocks)
                {
                    var over = new Label("+" + RankSteps.Amount(gap.Category, gap.Over)); over.AddToClassList("opt-gap__over"); line.Add(over);
                }
                row.Add(Track(gap, plan.Target));
            }
            else
            {
                var rated = new Label(RankSteps.RankName(gap.Rating)); rated.AddToClassList("opt-gap__values"); line.Add(rated);
            }
            row.tooltip = Tooltip(plan, gap);
            // Opens on press: the list under the columns shows what costs most here.
            row.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                row.AddToClassList("opt-gap--pressed");
                openGap = open ? null : (plan.Mobile, gap.Category);
                schedule.Execute(() => { shownPlan = null; Refresh(); });
            });
            return row;
        }

        // The value against every rank's limit: ticks at Excellent, Good, Medium and Poor, the target's lit; the fill coloured
        // by the rank the value gets, the avatar as it is behind it when the upload lowers it.
        private static VisualElement Track(RankPlan.Gap gap, PerformanceRating target)
        {
            float poor = gap.Ticks.Count > 0 ? gap.Ticks[gap.Ticks.Count - 1].Limit : gap.Limit;
            float scale = Mathf.Max(1e-3f, Mathf.Max(gap.Upload, Mathf.Max(gap.Now, poor)) * 1.06f);
            var track = new VisualElement(); track.AddToClassList("opt-gap__track");
            if (gap.Now > gap.Upload)
            {
                var before = new VisualElement(); before.AddToClassList("opt-gap__before");
                before.style.width = new Length(100f * Mathf.Clamp01(gap.Now / scale), LengthUnit.Percent);
                track.Add(before);
            }
            var fill = new VisualElement(); fill.AddToClassList("opt-gap__fill");
            fill.AddToClassList(gap.Rating <= PerformanceRating.Good ? "opt-gap__fill--good" : gap.Rating == PerformanceRating.Medium ? "opt-gap__fill--medium" : "opt-gap__fill--poor");
            fill.style.width = new Length(100f * Mathf.Clamp01(gap.Upload / scale), LengthUnit.Percent);
            track.Add(fill);
            foreach (var (rating, limit) in gap.Ticks)
            {
                if (limit <= 0f || limit >= scale) continue;
                var tick = new VisualElement(); tick.AddToClassList("opt-gap__tick");
                tick.EnableInClassList("opt-gap__tick--target", rating == target);
                tick.style.left = new Length(100f * limit / scale, LengthUnit.Percent);
                track.Add(tick);
            }
            return track;
        }

        private string Tooltip(RankPlan plan, RankPlan.Gap gap)
        {
            string where = plan.Mobile ? Mobile : "PC";
            if (!gap.Counted) return gap.Name + " is rated " + RankSteps.RankName(gap.Rating) + " on " + where + ".";
            string ranks = string.Join(" · ", gap.Ticks.Select(t => RankSteps.RankName(t.Rating) + " ≤ " + RankSteps.Amount(gap.Category, t.Limit)));
            string text = gap.Name + " at upload: " + RankSteps.Amount(gap.Category, gap.Upload) +
                          (gap.Now > gap.Upload ? " (" + RankSteps.Amount(gap.Category, gap.Now) + " as the avatar is)" : "") + ". " + where + " ranks: " + ranks + ".";
            return text + " Click to see what costs most.";
        }

        // What costs most in one category, biggest first, each naming the item it belongs to: a click shows it.
        private VisualElement Costliest(RankStats stats, RankPlan.Gap gap, string platform)
        {
            var box = new VisualElement(); box.AddToClassList("opt-costliest");
            var head = new Label("Costliest · " + gap.Name.ToLowerInvariant() + (platform != null ? " on " + platform : "")); head.AddToClassList("opt-plan__label"); box.Add(head);
            var entries = stats.Costliest.Where(c => c.Category == gap.Category).Take(ShownCostliest).ToList();
            if (entries.Count == 0)
            {
                var none = new Label("Nothing on the avatar itself: what counts here is built at upload (by VRCFury or Modular Avatar)."); none.AddToClassList("opt-panel__text");
                box.Add(none);
                return box;
            }
            float total = Mathf.Max(1e-3f, gap.Upload);
            foreach (var entry in entries)
            {
                var row = new VisualElement(); row.AddToClassList("opt-cost");
                var texts = new VisualElement(); texts.AddToClassList("opt-cost__texts"); row.Add(texts);
                var name = new Label(entry.Name); name.AddToClassList("opt-cost__name"); texts.Add(name);
                string owner = entry.Item != null ? entry.Item : entry.Path.Contains("/") ? entry.Path.Substring(0, entry.Path.LastIndexOf('/')) : null;
                if (!string.IsNullOrEmpty(owner)) { var item = new Label(owner); item.AddToClassList("opt-cost__owner"); item.EnableInClassList("opt-cost__owner--item", entry.Item != null); texts.Add(item); }
                var amount = new Label(RankSteps.Amount(gap.Category, entry.Amount)); amount.AddToClassList("opt-cost__amount"); row.Add(amount);
                var share = new Label(Mathf.RoundToInt(100f * entry.Amount / total).ToString(CultureInfo.InvariantCulture) + "%"); share.AddToClassList("opt-cost__share"); row.Add(share);
                row.tooltip = (entry.Item != null ? "Part of “" + entry.Item + "”. " : "") + (entry.Path.Length > 0 ? entry.Path : avatar.name) + ". Click to show it.";
                var target = entry;
                Pressable(row, () => Show(target.Asset, target.Path));
                box.Add(row);
            }
            return box;
        }

        private VisualElement StepRow(RankStep step, List<RankPlan> plans, int index)
        {
            var row = new VisualElement(); row.AddToClassList("opt-step");
            row.EnableInClassList("opt-step--note", step.Kind == RankStep.Kinds.Note);
            var badge = new Label(step.Kind == RankStep.Kinds.Note ? "!" : index.ToString(CultureInfo.InvariantCulture)); badge.AddToClassList("opt-step__index"); row.Add(badge);
            var texts = new VisualElement(); texts.AddToClassList("opt-step__texts"); row.Add(texts);
            var titleRow = new VisualElement(); titleRow.AddToClassList("opt-step__title-row"); texts.Add(titleRow);
            var title = new Label(step.Title); title.AddToClassList("opt-step__title"); titleRow.Add(title);
            if (step.ReachesAlone)
            {
                var reaches = new Label("reaches " + RankSteps.RankName(plans[0].Target)); reaches.AddToClassList("opt-step__tag"); titleRow.Add(reaches);
            }
            string saves = step.Kind == RankStep.Kinds.Setting && step.Known ? RankSteps.Describe(step, plans) : null;
            string detail = step.Kind == RankStep.Kinds.Setting
                ? (step.Known ? (string.IsNullOrEmpty(saves) ? "Measured: it saves nothing that holds the rank." : saves + " at upload, measured.")
                    : step.Detail + " Not measured yet: it lowers " + string.Join(", ", step.Touches.Take(3).Select(c => RankPlan.NameOf(c).ToLowerInvariant())) + ".")
                : step.Detail;
            var text = new Label(detail); text.AddToClassList("opt-step__detail"); texts.Add(text);
            if (step.Kind == RankStep.Kinds.Setting)
            {
                Button apply = null;
                apply = MyAvatarEditor.PressButton("Apply", () =>
                {
                    // Shown applied at once; the estimate of the new settings follows in the background.
                    row.AddToClassList("opt-step--applied");
                    apply.text = "Applied";
                    apply.SetEnabled(false);
                    var s = step.Settings;
                    Change("My Avatar: " + step.Title.ToLowerInvariant(), o => { o.level = s.level; o.custom = s.custom; o.sharpFrom = s.sharpFrom; o.polygonQuality = s.polygonQuality; });
                });
                apply.AddToClassList("opt-button"); apply.AddToClassList("opt-step__button"); apply.AddToClassList("opt-step__button--apply");
                row.Add(apply);
            }
            else if (!string.IsNullOrEmpty(step.Path))
            {
                var select = MyAvatarEditor.PressButton("Show", () => Show(0, step.Path));
                select.AddToClassList("opt-button"); select.AddToClassList("opt-step__button");
                select.tooltip = "Highlights it in the Hierarchy.";
                row.Add(select);
            }
            return row;
        }

        // Shows an asset, or the avatar's object at a path, highlighted in the Project or Hierarchy window: the selection
        // stays, so this card stays in the Inspector.
        private void Show(int asset, string path)
        {
            if (asset != 0 && EditorUtility.InstanceIDToObject(asset) is UnityEngine.Object found && found)
            {
                EditorGUIUtility.PingObject(found);
                return;
            }
            var root = TextureOptimization.AvatarRoot(avatar);
            var target = string.IsNullOrEmpty(path) ? root.transform : root.transform.Find(path);
            if (target != null) EditorGUIUtility.PingObject(target.gameObject);
        }

        // Pressed at once (pointer down), acting then; a keyboard press acts too.
        private static void Pressable(VisualElement element, Action action)
        {
            element.focusable = true;
            element.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                element.AddToClassList("opt-cost--pressed");
                action();
            });
            element.RegisterCallback<PointerUpEvent>(_ => element.RemoveFromClassList("opt-cost--pressed"));
            element.RegisterCallback<PointerLeaveEvent>(_ => element.RemoveFromClassList("opt-cost--pressed"));
            element.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter || evt.keyCode == KeyCode.Space) action();
            });
        }
    }
}
