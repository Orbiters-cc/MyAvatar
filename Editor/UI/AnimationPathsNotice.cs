using System;
using System.Linq;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.Animations;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// The notice at the top of the card when renaming or moving objects of the avatar (in Unity's Hierarchy or any tool)
    /// broke its animations: what happened, Fix (points them at where the objects are now, one Undo step; a clip other
    /// avatars use too is copied for this one, or fixed everywhere on request) or Leave. Hidden otherwise. It never fixes
    /// anything by itself.
    /// </summary>
    internal sealed class AnimationPathsNotice : VisualElement
    {
        private const string Sheet = "Packages/orbiters.myavatar/Editor/UI/checks.uss";
        private readonly MyAvatar avatar;
        private AnimationPathWatch.Notice shown;

        internal AnimationPathsNotice(MyAvatar avatar)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            if (AssetDatabase.LoadAssetAtPath<StyleSheet>(Sheet) is StyleSheet sheet) styleSheets.Add(sheet);
            AddToClassList("chk");
            AddToClassList("chk-notice");
            style.display = DisplayStyle.None;
            RegisterCallback<AttachToPanelEvent>(_ => { AnimationPathWatch.Changed += Refresh; Refresh(); });
            RegisterCallback<DetachFromPanelEvent>(_ => AnimationPathWatch.Changed -= Refresh);
        }

        private void Refresh()
        {
            var root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            var notice = root != null ? AnimationPathWatch.For(root) : null;
            if (notice == shown && notice?.Plan == shown?.Plan && childCount > 0 == (notice != null)) return;
            shown = notice;
            Clear();
            style.display = notice == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (notice == null) return;
            var row = new VisualElement(); row.AddToClassList("chk-issue"); Add(row);
            var dot = new VisualElement(); dot.AddToClassList("chk-issue__dot"); dot.AddToClassList("chk-issue__dot--warning"); row.Add(dot);
            var texts = new VisualElement(); texts.AddToClassList("chk-issue__texts"); row.Add(texts);
            var title = new Label(notice.Text + "."); title.AddToClassList("chk-issue__title"); texts.Add(title);
            var plan = notice.Plan;
            string detail = "They still point at the old " + (plan.Fixable == 1 ? "path" : "paths") + ": in VRChat those parts would do nothing. " +
                            "Fix points them at where the objects are now, in one Undo step.";
            if (plan.Shared) detail += " Some are used elsewhere too (" + string.Join(", ", plan.Assets.Where(a => a.Fixable && a.Shared).SelectMany(a => a.SharedWith).Distinct().Take(3)) +
                                       "): Fix changes copies for this avatar, Fix everywhere changes them for all.";
            int locked = plan.LockedAssets.Count();
            if (locked > 0) detail += " " + locked + (locked == 1 ? " is" : " are") + " in a package or read-only and can't be changed.";
            var text = new Label(detail); text.AddToClassList("chk-issue__detail"); texts.Add(text);
            var where = new Label(string.Join(", ", plan.Assets.Where(a => a.Fixable).Select(a => a.Asset.name).Take(4)) + (plan.FixableAssets > 4 ? "…" : ""));
            where.AddToClassList("chk-issue__where"); texts.Add(where);
            var actions = new VisualElement(); actions.AddToClassList("chk-notice__actions"); texts.Add(actions);
            var fix = MyAvatarEditor.PressButton("Fix", () => Run(notice, false));
            fix.AddToClassList("ft-primary"); fix.AddToClassList("chk-notice__primary");
            fix.tooltip = "Point the animations at the objects where they are now (Undo takes it back).";
            actions.Add(fix);
            if (plan.Shared)
            {
                var everywhere = MyAvatarEditor.PressButton("Fix everywhere", () => Run(notice, true));
                everywhere.AddToClassList("ft-ghost");
                everywhere.tooltip = "Change the shared animations themselves: the other avatars and objects using them change too.";
                actions.Add(everywhere);
            }
            var leave = MyAvatarEditor.PressButton("Leave", () => AnimationPathWatch.Leave(notice));
            leave.AddToClassList("ft-ghost");
            leave.tooltip = "Keep the animations as they are.";
            actions.Add(leave);
            // The watcher's switch, where its notice shows (also in Orbiters settings, Features, Toolkit).
            var stop = MyAvatarEditor.PressButton("Stop watching renames", () =>
            {
                AnimationPathWatch.Leave(notice);
                AnimationPathWatch.Enabled = false;
                Debug.Log("[My Avatar] Renames that break animations aren't noticed any more: Orbiters settings > Features > Toolkit > “Notice renames that break animations” turns it back on.");
            });
            stop.AddToClassList("ft-ghost");
            stop.tooltip = "Don't notice renames and moves that break animations any more. Orbiters settings (Features, Toolkit) turns it back on; Mixer's own renames still ask.";
            actions.Add(stop);
        }

        // The buttons go at once; the fix runs on the next frame.
        private void Run(AnimationPathWatch.Notice notice, bool everywhere)
        {
            foreach (var button in this.Query<Button>().ToList()) button.SetEnabled(false);
            schedule.Execute(() =>
            {
                try
                {
                    var applied = AnimationPathWatch.Fix(notice, everywhere);
                    Debug.Log("[My Avatar] " + notice.What + ": updated " + applied.Paths + " animation paths in " + applied.Assets + " assets" +
                              (applied.Copies > 0 ? " (" + applied.Copies + " copies for this avatar)" : "") +
                              (applied.Skipped.Count > 0 ? "; left as they are: " + string.Join("; ", applied.Skipped.Take(5)) : "") + ".");
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    foreach (var button in this.Query<Button>().ToList()) button.SetEnabled(true);
                }
            });
        }
    }
}
