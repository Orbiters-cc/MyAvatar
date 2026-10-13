using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;

namespace Orbiters.MyAvatar.Editor
{
    // Everything that makes the avatar lighter, in one frame like face tracking's: a ring with how much lighter the upload
    // downloads, the rank VRChat gives the avatar now, Small, Big or Custom optimization of the uploaded copy (the scene
    // avatar never changes) with what VRChat would rank and download, the polygons, and a pointer to d4rkAvatarOptimizer for
    // what only shader rewriting can do. A finished estimate updates what shows it in place: nothing under the pointer is
    // rebuilt.
    internal sealed class OptimizationSection : AvatarSection
    {
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/UI/optimization.uss";
        private static readonly Color Glow = new Color(0f, .85f, .43f, .18f);

        private readonly MyAvatar avatar;
        private readonly VisualElement hero, estimate, polygons, suggestion;
        private IVisualElementScheduledItem poll, write, polygonWrite;
        private string shown, shownRank, shownEstimate, shownPolygons;
        private float pendingDistance = -1f, ringFill;
        private Label eyebrow;
        private FaceTrackingRing ring;
        private VisualElement badge;
        private Label polygonValue, polygonLine;
        private SliderInt polygonSlider;

        private static readonly (OptimizationLevel Level, string Name, string Sub, IconGlyph Glyph, string Tooltip)[] Levels =
        {
            (OptimizationLevel.Small, "Small", "Looks the same", IconGlyph.Broom,
                "Removes from the uploaded copy what VRChat counts but nothing shows or uses, polygons whose removal moves the surface 4 mm at most, " +
                "and sizes textures to stay sharp from 1 m (never larger than they are). Click again to turn it off."),
            (OptimizationLevel.Big, "Big", "Merges, hides, Quest", IconGlyph.Sparkle,
                "Small, and: meshes always shown (props too) or toggled together become one, the body under clothing always worn goes, polygons go " +
                "within 8 mm, bones that only end a PhysBone chain become its endpoint, names and menu icons shrink, and textures are sized to stay sharp " +
                "from 3 m, crunched, with metallic and roughness maps halved and ASTC for Quest. Click again to turn it off."),
            (OptimizationLevel.Custom, "Custom", "Your choice", IconGlyph.Sliders,
                "Choose each change yourself. Click again to turn it off."),
        };

        private static readonly (OptimizationPasses Pass, string Name, string Detail)[] Passes =
        {
            (OptimizationPasses.HiddenObjects, "Hidden objects", "Objects that start hidden and that no animation shows. VRChat ranks them as if they were on."),
            (OptimizationPasses.DisabledComponents, "Disabled components", "PhysBones, contacts, constraints, lights and renderers that start off and that no animation turns on."),
            (OptimizationPasses.IdlePhysBones, "PhysBones moving nothing", "PhysBones whose bones no mesh follows, nothing sits on and no animation reads."),
            (OptimizationPasses.UnusedColliders, "Unused colliders", "PhysBone colliders that no PhysBone collides with."),
            (OptimizationPasses.StillPhysBones, "Still PhysBones", "PhysBones set to follow animations when nothing animates their bones: VRChat stops reading them every frame."),
            (OptimizationPasses.BlendShapes, "Unused blendshapes", "Shapes nothing animates leave the meshes, baked in at their weight: often most of the download size."),
            (OptimizationPasses.ConstantBlendShapes, "Blendshapes that never change", "With unused blendshapes: shapes animations only ever set to the weight they already have are baked in too."),
            (OptimizationPasses.BoneReferences, "Unweighted bones", "Bones a mesh lists without moving any of its vertices leave its list: VRChat counts every listed bone."),
            (OptimizationPasses.MaterialSlots, "Material slots", "Parts of a mesh that share an opaque material are drawn as one; empty ones go."),
            (OptimizationPasses.DuplicateTextures, "Duplicate textures", "The same image imported twice under two names (a package bringing its own copy of one the avatar has) uploads once."),
            (OptimizationPasses.UnusedTextures, "Unused textures", "Textures a material still points at that its shader doesn't use (after Poiyomi locks its materials)."),
            (OptimizationPasses.MergeMeshes, "Merge meshes", "Meshes that are always shown, props included, join the one animations use: fewer meshes. Toggled meshes stay apart."),
            (OptimizationPasses.MergeToggledMeshes, "Meshes toggled together", "Meshes every animation shows and hides together become one (an outfit's pieces); " +
                "the animations of their blendshapes and materials follow them."),
            (OptimizationPasses.StaticBones, "Bones that never move", "Bones nothing animates, simulates or constrains (clothing armatures, attachment bones) follow the bone they hang from: same look, fewer bones counted."),
            (OptimizationPasses.EndBones, "PhysBone endpoints", "Bones that only mark where a PhysBone chain ends become its endpoint: fewer transforms for VRChat to move."),
            (OptimizationPasses.BodyUnderClothing, "Body under clothing", "Body triangles that opaque clothing worn all the time hides from every side go: fewer polygons. Clothing a toggle hides doesn't count."),
            (OptimizationPasses.FewerPolygons, "Fewer polygons", "Polygons whose removal moves the surface less than the Polygons slider allows go (flat or finely divided areas)."),
            (OptimizationPasses.Bounds, "Avatar-wide bounds", "Every skinned mesh that moves with the hips gets the same culling box, around the whole avatar " +
                "with room for its limbs: no part vanishes at the edge of the view. Props that can leave the avatar (constrained) keep their own."),
            (OptimizationPasses.ResizeTextures, "Textures sized to the distance", "Each texture uploads at the size that keeps it sharp from the distance set below, " +
                "never larger than it is, compressed for the platform (BC1, BC7 with alpha, BC5 for normal maps)."),
            (OptimizationPasses.QuestTextures, "Quest textures", "A Quest (Android) upload gets its textures in ASTC, at half the PC size, 1024 px at most."),
            (OptimizationPasses.CrunchTextures, "Crunched textures", "Colour and mask textures upload crunched: a much smaller download for the same memory, slightly lossy. Normal maps stay as they are."),
            (OptimizationPasses.HalfSurfaceMaps, "Smaller metallic and roughness", "Metallic, roughness and smoothness maps upload at half the size the others get."),
            (OptimizationPasses.MenuIcons, "Menu icons at 64 px", "The menu's icons upload at 64 px at most, as large as VRChat shows them. Your images stay as they are."),
            (OptimizationPasses.ShortNames, "Short names", "Objects and the parameters only animators use upload with one or two letter names. Bones, Body and the parameters " +
                "your menu, OSC and saved settings use keep theirs."),
        };

        internal OptimizationSection(MyAvatar avatar) : base("Optimization", card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("opt");
            hero = new VisualElement(); Body.Add(hero);
            estimate = new VisualElement();
            polygons = new VisualElement();
            suggestion = new VisualElement(); Body.Add(suggestion);
            RegisterCallback<AttachToPanelEvent>(_ => { poll?.Pause(); poll = schedule.Execute(Poll).Every(1500); Undo.undoRedoPerformed += Rebuild; });
            RegisterCallback<DetachFromPanelEvent>(_ => { poll?.Pause(); Undo.undoRedoPerformed -= Rebuild; Flush(); });
            Refresh();
        }

        private UploadOptimization Settings => avatar.uploadOptimization;

        // The estimates follow the platform the project builds for: PC, or Quest (iOS) when it builds for Android (iOS). Tests
        // and captures show either without switching the project.
        internal static bool? ShowQuest;
        private static bool Quest => ShowQuest ?? TextureOptimization.Mobile;
        private static string Mobile => ShowQuest == null && TextureOptimization.PlatformName(true) == TextureOptimization.IPhone ? "iOS" : "Quest";

        private void Rebuild() { shown = shownRank = shownEstimate = shownPolygons = null; view = null; Refresh(); }

        // The poll looks for changes only while the section can be seen with Unity in front: a covered, scrolled-away or
        // folded section costs nothing. Clicks and finished estimates refresh it directly.
        private void Poll()
        {
            if (!InternalEditorUtility.isApplicationActive || panel == null || resolvedStyle.display == DisplayStyle.None ||
                Body.resolvedStyle.display == DisplayStyle.None || !panel.visualTree.worldBound.Overlaps(worldBound)) return;
            Refresh();
        }

        private void Refresh()
        {
            if (!avatar || panel == null && shown != null) return;
            string state = Settings.level + ":" + (int)Settings.custom + ":" + Quest;
            if (state != shown) { shown = state; shownRank = null; ShowHero(); }
            // The estimate, and its key, once per refresh: every part shows the same.
            var result = Measured();
            ShowRank(result);
            ShowEstimate(result);
            ShowPolygons(result);
            ShowSuggestion();
        }

        // ---- The frame: ranks now, the three levels, Custom's switches ----

        private void ShowHero()
        {
            hero.Clear();
            var level = Settings.level;
            string title = level == OptimizationLevel.Off ? "Optimize at upload" : level + " optimization at upload";
            var frame = new ToolHero(hero, Glow, "", null, title,
                "Your avatar stays as it is: VRChat gets a lighter copy when you upload, built from what it shows and does.");
            frame.Frame.name = "optimization-hero";
            eyebrow = frame.Frame.Q<Label>(className: "ft-hero__eyebrow");
            badge = frame.Badge;
            ring = new FaceTrackingRing(); ring.AddToClassList("ft-hero__ring"); badge.Add(ring);

            var tiles = new VisualElement(); tiles.AddToClassList("ft-trackers"); tiles.AddToClassList("opt-levels"); frame.Frame.Add(tiles);
            foreach (var (value, name, sub, glyph, tip) in Levels)
            {
                var chosen = value;
                var tile = MyAvatarEditor.Button("", () => Choose(Settings.level == chosen ? OptimizationLevel.Off : chosen));
                tile.AddToClassList("ft-tracker"); tile.AddToClassList("opt-level");
                tile.EnableInClassList("ft-tracker--on", level == value);
                tile.tooltip = tip;
                var mark = new VectorIcon(glyph); mark.AddToClassList("opt-level__icon"); tile.Add(mark);
                var label = new Label(name); label.AddToClassList("ft-tracker__name"); label.pickingMode = PickingMode.Ignore; tile.Add(label);
                var detail = new Label(sub); detail.AddToClassList("ft-tracker__sub"); detail.pickingMode = PickingMode.Ignore; tile.Add(detail);
                tiles.Add(tile);
            }
            var caption = new Label(Caption(level)); caption.AddToClassList("ft-trackers__caption"); frame.Frame.Add(caption);

            if (level == OptimizationLevel.Custom)
            {
                var custom = new VisualElement(); custom.AddToClassList("ft-custom"); frame.Frame.Add(custom);
                foreach (var (pass, name, detail) in Passes)
                {
                    var row = new VisualElement(); row.AddToClassList("ft-feature");
                    var texts = new VisualElement(); texts.AddToClassList("ft-feature__texts"); row.Add(texts);
                    var label = new Label(name); label.AddToClassList("ft-feature__name"); texts.Add(label);
                    var text = new Label(detail); text.AddToClassList("ft-feature__detail"); texts.Add(text);
                    var flag = pass;
                    row.Add(new ToggleSwitch((Settings.custom & flag) != 0, on => Change("My Avatar: optimization", s => s.custom = on ? s.custom | flag : s.custom & ~flag)));
                    custom.Add(row);
                    if (pass == OptimizationPasses.ResizeTextures && (Settings.custom & pass) != 0) TextureSlider(custom);
                }
            }
            // What VRChat would rank and download, under the choice that makes it; then the polygons.
            frame.Frame.Add(estimate);
            frame.Frame.Add(polygons);
        }

        // The eyebrow (the rank now) and the ring (how much lighter the upload downloads than the avatar as it is, for the
        // platform the project builds for), updated in place. While the upload is measured again, the rank now stays: it
        // doesn't depend on the settings.
        private void ShowRank(OptimizationEstimate.Result result)
        {
            if (eyebrow == null || ring == null) return;
            var now = result?.Now ?? OptimizationEstimate.Latest(avatar)?.Now;
            string state = Quest + ":" + (now == null ? "-" : RuntimeHelpers.GetHashCode(now).ToString()) + ":" +
                           (result == null ? "measuring" : RuntimeHelpers.GetHashCode(result).ToString());
            if (state == shownRank) return;
            shownRank = state;
            eyebrow.text = now != null ? (Quest ? Rank(now.Mobile) + " on " + Mobile : Rank(now.Pc) + " on PC").ToUpperInvariant()
                : result?.Error != null ? "COULDN'T MEASURE" : "MEASURING…";
            eyebrow.EnableInClassList("muted", now == null && result?.Error == null);
            eyebrow.EnableInClassList("warning", now != null ? (Quest ? now.Mobile : now.Pc) >= PerformanceRating.Poor : result?.Error != null);
            if (result == null)
            {
                // The last share stays while measuring: the ring doesn't empty at every change.
                ring.Set(ringFill, "…", "measuring", anyShareIsGood: true);
                badge.tooltip = "Measuring the download with and without the changes…";
                return;
            }
            if (result.Error != null)
            {
                ringFill = 0f;
                ring.Set(0f, "!", "not measured");
                badge.tooltip = "The estimate failed: " + result.Error;
                return;
            }
            long before = Quest ? result.Now.QuestDownload : result.Now.Download, upload = Quest ? result.Upload.QuestDownload : result.Upload.Download;
            ringFill = before > 0 ? Mathf.Clamp01((before - upload) / (float)before) : 0f;
            int percent = Mathf.RoundToInt(ringFill * 100f);
            ring.Set(ringFill, percent.ToString(CultureInfo.InvariantCulture) + "%", "lighter", anyShareIsGood: true);
            badge.tooltip = Settings.level == OptimizationLevel.Off
                ? "Optimization is off: players download the avatar as it is (" + VrcLoadingBar.Format(before) + ")."
                : "Players download " + percent.ToString(CultureInfo.InvariantCulture) + "% less: " + VrcLoadingBar.Format(upload) + " instead of " +
                  VrcLoadingBar.Format(before) + " without optimization.";
        }

        private static string Caption(OptimizationLevel level)
        {
            switch (level)
            {
                case OptimizationLevel.Small: return "At upload, what nothing shows or uses leaves the copy (hidden objects, disabled components, unused blendshapes, " +
                    "bones, material slots and textures), polygons go within 4 mm and textures are sized to stay sharp from 1 m.";
                case OptimizationLevel.Big: return "Small, and meshes always shown or toggled together become one, the body under clothing goes, polygons go within " +
                    "8 mm, names and menu icons shrink, and textures are sized from 3 m, crunched, with metallic and roughness halved and ASTC for Quest.";
                case OptimizationLevel.Custom: return "Only the changes turned on below run at upload.";
                default: return "Off: the avatar uploads as it is. Small changes almost nothing you see or do.";
            }
        }

        private void Choose(OptimizationLevel level) => Change("My Avatar: optimization", s => s.level = level);

        // Settings change at once and with Undo; the frame redraws from them.
        private void Change(string name, Action<UploadOptimization> edit)
        {
            if (!avatar) return;
            Undo.RecordObject(avatar, name);
            edit(Settings);
            EditorUtility.SetDirty(avatar);
            PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
            Refresh();
        }

        // ---- What VRChat would rank ----

        private OptimizationEstimate.Result Measured() =>
            avatar ? OptimizationEstimate.Get(avatar, Settings.Passes, () => { if (panel != null) Refresh(); }) : null;

        private static string Rank(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.Excellent: return "Excellent";
                case PerformanceRating.Good: return "Good";
                case PerformanceRating.Medium: return "Medium";
                case PerformanceRating.Poor: return "Poor";
                case PerformanceRating.VeryPoor: return "Very Poor";
                default: return "Unranked";
            }
        }

        // The card is built once for its layout (in VRChat now or at upload, PC or mobile) and its values change in place: while
        // the upload is measured again it keeps the last figures, dimmed, so nothing under the pointer moves.
        private sealed class EstimateCard
        {
            public string Layout;
            public Label Aside, Platform, Delta, Caption, Why, None, Error;
            public VrcLoadingBar Bar;
            public VisualElement Card, Download, Ranks, Stats;
            public Button Retry;
            public bool Filled;
        }

        private EstimateCard view;

        private static void Display(VisualElement element, bool shown) => element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;

        private void ShowEstimate(OptimizationEstimate.Result result)
        {
            bool off = Settings.level == OptimizationLevel.Off;
            bool quest = Quest;
            string layout = off + ":" + quest;
            if (view == null || view.Layout != layout)
            {
                estimate.Clear();
                view = new EstimateCard { Layout = layout, Card = new VisualElement() };
                view.Card.AddToClassList("opt-estimate"); estimate.Add(view.Card);
                var head = new VisualElement(); head.AddToClassList("opt-estimate__head"); view.Card.Add(head);
                var title = new Label(off ? "In VRChat now" : "At upload"); title.AddToClassList("opt-estimate__title"); head.Add(title);
                view.Aside = new Label(); view.Aside.AddToClassList("opt-estimate__aside"); head.Add(view.Aside);
                view.Aside.tooltip = off ? "Measured with the VRChat SDK on the avatar as it is; the download is estimated from what the upload holds."
                    : "Measured with the VRChat SDK on a hidden copy of the avatar put through these changes. Tools that build at upload (VRCFury, Modular Avatar) haven't run on it.";
                // What players see first: VRChat's loading bar over the avatar while it downloads.
                view.Download = new VisualElement(); view.Download.AddToClassList("opt-download"); view.Card.Add(view.Download);
                view.Download.tooltip = quest
                    ? "What " + Mobile + " players see over the avatar while it downloads: the file's size, compressed as VRChat compresses it, estimated from what the " +
                      "upload holds (textures with their mobile settings, meshes, blendshapes, animations, shaders). Then the time it takes to unpack and set up " +
                      "on a Quest, about four times slower than a PC. Switch the project to PC to see the PC estimates."
                    : "What players see over the avatar while it downloads: the file's size, compressed as VRChat compresses it, estimated from what the upload " +
                      "holds (textures in their PC format, meshes, blendshapes, animations, shaders). Then the time it takes to unpack and set up on a desktop PC. " +
                      "Switch the project to Android to see the Quest estimates.";
                view.Platform = new Label(quest ? Mobile : "PC"); view.Platform.AddToClassList("opt-rank__platform"); view.Download.Add(view.Platform);
                view.Bar = new VrcLoadingBar(); view.Download.Add(view.Bar);
                view.Delta = new Label(); view.Delta.AddToClassList("opt-stat__delta"); view.Delta.AddToClassList("opt-download__delta"); view.Download.Add(view.Delta);
                view.Caption = new Label(); view.Caption.AddToClassList("opt-download__caption"); view.Card.Add(view.Caption);
                view.Ranks = new VisualElement(); view.Ranks.AddToClassList("opt-ranks"); view.Card.Add(view.Ranks);
                // Why the rank stays where it is: what no change here can bring down.
                view.Why = new Label(); view.Why.AddToClassList("opt-estimate__why"); view.Card.Add(view.Why);
                view.Stats = new VisualElement(); view.Stats.AddToClassList("opt-stats"); view.Card.Add(view.Stats);
                view.None = new Label("Nothing VRChat counts changes on this avatar: it already uses everything it uploads.");
                view.None.AddToClassList("opt-estimate__none"); view.Card.Add(view.None);
                // Kept until something changes: no new attempt every few seconds, nothing logged again and again.
                view.Error = new Label(); view.Error.AddToClassList("opt-estimate__error"); view.Card.Add(view.Error);
                view.Retry = MyAvatarEditor.Button("Try again", () => { OptimizationEstimate.Retry(avatar); Refresh(); });
                view.Retry.AddToClassList("opt-button"); view.Retry.AddToClassList("opt-estimate__retry"); view.Card.Add(view.Retry);
                shownEstimate = null;
            }
            string state = Settings.Passes + ":" + (result == null ? "measuring" : RuntimeHelpers.GetHashCode(result).ToString());
            if (state == shownEstimate) return;
            shownEstimate = state;
            view.Aside.text = result == null ? "Measuring…" : result.Error != null ? "not measured" : "estimated";
            // Measuring again: the last figures stay, dimmed.
            view.Card.EnableInClassList("opt-estimate--measuring", result == null && view.Filled);
            bool failed = result?.Error != null, filled = result != null && !failed;
            if (result == null && view.Filled) return;
            view.Filled = filled;
            Display(view.Error, failed); Display(view.Retry, failed);
            if (failed) view.Error.text = "The estimate failed: " + result.Error;
            foreach (var part in new VisualElement[] { view.Download, view.Caption, view.Ranks, view.Why, view.Stats, view.None }) Display(part, filled);
            if (!filled) return;

            var now = result.Now; var upload = result.Upload;
            var shownSnapshot = off ? now : upload;
            long before = quest ? now.QuestDownload : now.Download, after = quest ? upload.QuestDownload : upload.Download;
            view.Bar.SetSize(off ? before : after);
            // VRChat's limits apply to the bundle the SDK uploads (compressed, and unpacked), not to the slightly smaller size it shows.
            long bundle = quest ? shownSnapshot.QuestBundle : shownSnapshot.Bundle, unpacked = quest ? shownSnapshot.QuestUnpacked : shownSnapshot.Unpacked;
            long limit = quest ? DownloadSize.MobileLimit : DownloadSize.PcLimit, rawLimit = quest ? DownloadSize.MobileRawLimit : DownloadSize.PcRawLimit;
            bool saves = !off && Math.Abs(before - after) >= 52429;
            Display(view.Delta, saves);
            view.Delta.text = (after < before ? "−" : "+") + DownloadSize.Megabytes(Math.Abs(before - after));
            view.Delta.EnableInClassList("opt-stat__delta--worse", after > before);
            string text = "Shows about " + Seconds(quest ? shownSnapshot.QuestLoad : shownSnapshot.Load) + " after it downloads";
            if (saves) text += " · " + VrcLoadingBar.Format(before) + " now";
            if (bundle > limit) text += " · over VRChat's " + DownloadSize.Megabytes(limit) + " download limit: the upload would be refused";
            else if (unpacked > rawLimit) text += " · " + DownloadSize.Megabytes(unpacked) + " unpacked, over VRChat's " + DownloadSize.Megabytes(rawLimit) + " limit: the upload would be refused";
            view.Caption.text = text;
            view.Caption.EnableInClassList("warning", bundle > limit || unpacked > rawLimit);

            view.Ranks.Clear();
            if (quest) RankRow(view.Ranks, now.Mobile, upload.Mobile);
            else RankRow(view.Ranks, now.Pc, upload.Pc);
            var limits = quest ? upload.MobileLimits : upload.PcLimits;
            Display(view.Why, limits.Count > 0);
            view.Why.text = limits.Count == 0 ? "" : Rank(quest ? upload.Mobile : upload.Pc) + " from " + string.Join(", ", limits.Take(3)) + (limits.Count > 3 ? "…" : "");

            view.Stats.Clear();
            if (!off)
            {
                var rows = view.Stats;
                Stat(rows, "Skinned meshes", now.Skinned, upload.Skinned);
                Stat(rows, "Material slots", now.Materials, upload.Materials);
                Stat(rows, "Bones", now.Bones, upload.Bones);
                Stat(rows, "PhysBones", now.PhysBones, upload.PhysBones);
                Stat(rows, "PhysBone transforms", now.PhysBoneTransforms, upload.PhysBoneTransforms);
                Stat(rows, "PhysBone colliders", now.Colliders, upload.Colliders);
                Stat(rows, "Contacts", now.Contacts, upload.Contacts);
                Stat(rows, "Constraints", now.Constraints, upload.Constraints);
                if (quest) Stat(rows, "Texture memory", now.QuestTextureMegabytes, upload.QuestTextureMegabytes, " MB", "With the textures' mobile settings, as VRChat counts it: every texture the materials point at.");
                else Stat(rows, "Texture memory", now.TextureMegabytes, upload.TextureMegabytes, " MB", "In the formats the PC build will have, as VRChat counts it: every texture the materials point at.");
                Stat(rows, "Blendshapes", now.BlendShapes, upload.BlendShapes, null, "Not ranked, but unused blendshapes are often most of the download size.");
            }
            Display(view.Stats, !off && view.Stats.childCount > 0);
            Display(view.None, !off && view.Stats.childCount == 0);
        }

        private static string Seconds(float seconds) => seconds.ToString(seconds < 10f ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

        private static void RankRow(VisualElement parent, PerformanceRating before, PerformanceRating after)
        {
            var row = new VisualElement(); row.AddToClassList("opt-rank"); parent.Add(row);
            var name = new Label("Rank"); name.AddToClassList("opt-rank__platform"); row.Add(name);
            row.Add(Chip(before));
            if (after == before) return;
            var arrow = new Label("→"); arrow.AddToClassList("opt-rank__arrow"); row.Add(arrow);
            row.Add(Chip(after));
        }

        private static Label Chip(PerformanceRating rating)
        {
            var chip = new Label(Rank(rating)); chip.AddToClassList("opt-chip");
            chip.AddToClassList(rating <= PerformanceRating.Good ? "opt-chip--good" : rating == PerformanceRating.Medium ? "opt-chip--medium" : "opt-chip--poor");
            return chip;
        }

        // Only what changes is listed.
        private static void Stat(VisualElement parent, string name, float before, float after, string unit = null, string tooltip = null)
        {
            if (Mathf.Abs(before - after) < .05f) return;
            var row = new VisualElement(); row.AddToClassList("opt-stat"); parent.Add(row);
            if (tooltip != null) row.tooltip = tooltip;
            var label = new Label(name); label.AddToClassList("opt-stat__name"); row.Add(label);
            string Text(float v) => unit == null ? Count(v) : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + unit;
            var values = new Label(Text(before) + " → " + Text(after)); values.AddToClassList("opt-stat__values"); row.Add(values);
            var delta = new Label((after < before ? "−" : "+") + (unit == null ? Count(Mathf.Abs(before - after)) : Mathf.Abs(before - after).ToString("0.#", CultureInfo.InvariantCulture) + unit));
            delta.AddToClassList("opt-stat__delta"); delta.EnableInClassList("opt-stat__delta--worse", after > before); row.Add(delta);
        }

        private static string Count(float value) => Mathf.RoundToInt(value).ToString("N0", CultureInfo.InvariantCulture);

        // ---- Custom's texture distance ----

        // Under the "Textures sized to the distance" switch: from how close textures stay sharp in game.
        private void TextureSlider(VisualElement parent)
        {
            float distance = pendingDistance > 0 ? pendingDistance : TextureOptimization.SharpFrom(avatar);
            var row = new VisualElement(); row.AddToClassList("opt-slider-row"); row.AddToClassList("opt-custom-slider"); parent.Add(row);
            var near = new Label("Closer"); near.AddToClassList("opt-slider__end"); row.Add(near);
            var slider = new SliderInt(0, TextureDensity.Distances.Length - 1) { value = Nearest(distance) };
            slider.AddToClassList("opt-slider");
            slider.tooltip = "From how close textures stay sharp in VRChat: a texture keeps a texel for every screen pixel from there (about " +
                             TextureDensity.PixelsPerMeter.ToString("N0", CultureInfo.InvariantCulture) + " pixels across a meter of surface seen from one meter, in today's headsets). " +
                             "Each texture's size comes from how much of the avatar it covers; none gets larger than it is.";
            row.Add(slider);
            var far = new Label("Lighter"); far.AddToClassList("opt-slider__end"); row.Add(far);
            var value = new Label("Sharp from " + Distance(distance)); value.AddToClassList("opt-panel__value"); value.AddToClassList("opt-custom-slider__value"); row.Add(value);
            slider.RegisterValueChangedCallback(evt =>
            {
                float chosen = TextureDensity.Distances[evt.newValue];
                value.text = "Sharp from " + Distance(chosen);
                pendingDistance = chosen;
                // Written once the handle rests: each write measures the upload again.
                write?.Pause();
                write = schedule.Execute(Flush).StartingIn(250);
            });
            slider.RegisterCallback<PointerUpEvent>(_ => Flush(), TrickleDown.TrickleDown);
        }

        private void Flush()
        {
            write?.Pause();
            if (pendingDistance <= 0f || !avatar) return;
            float chosen = pendingDistance;
            pendingDistance = -1f;
            if (Mathf.Approximately(chosen, Settings.sharpFrom)) return;
            Change("My Avatar: texture sharpness", s => s.sharpFrom = chosen);
        }

        private static int Nearest(float distance)
        {
            int best = 0;
            for (int i = 1; i < TextureDensity.Distances.Length; i++)
                if (Mathf.Abs(TextureDensity.Distances[i] - distance) < Mathf.Abs(TextureDensity.Distances[best] - distance)) best = i;
            return best;
        }

        private static string Distance(float meters) => meters < 1f ? Mathf.RoundToInt(meters * 100f).ToString(CultureInfo.InvariantCulture) + " cm"
            : meters.ToString("0.#", CultureInfo.InvariantCulture) + " m";

        // ---- Polygons ----

        // How close to the original the meshes stay, from removing only what no one sees to small visible changes.
        private static readonly (float Quality, string Name, string Budget)[] Looks =
            { (1f, "Identical", "½ mm"), (.75f, "Nearly identical", "1 mm"), (.5f, "Very close", "2 mm"), (.25f, "Close", "4 mm"), (0f, "Lighter", "8 mm") };

        // What the upload does to the polygons; the slider under Custom. The panel is built when it shows or hides the slider;
        // a new estimate or quality only changes its texts (a drag on the slider is never interrupted).
        private void ShowPolygons(OptimizationEstimate.Result result)
        {
            bool on = (Settings.Passes & OptimizationPasses.FewerPolygons) != 0, custom = Settings.level == OptimizationLevel.Custom;
            string state = on + ":" + custom;
            if (state != shownPolygons)
            {
                shownPolygons = state;
                polygons.Clear();
                polygonValue = polygonLine = null; polygonSlider = null;
                if (!on) return;
                var card = new VisualElement(); card.AddToClassList("opt-panel"); polygons.Add(card);
                var head = new VisualElement(); head.AddToClassList("opt-panel__head"); card.Add(head);
                var title = new Label("Polygons"); title.AddToClassList("opt-estimate__title"); head.Add(title);
                polygonValue = new Label(); polygonValue.AddToClassList("opt-panel__value"); head.Add(polygonValue);
                if (custom)
                {
                    var row = new VisualElement(); row.AddToClassList("opt-slider-row"); card.Add(row);
                    var near = new Label("Identical"); near.AddToClassList("opt-slider__end"); row.Add(near);
                    polygonSlider = new SliderInt(0, Looks.Length - 1) { value = Stop(Settings.PolygonQuality) };
                    polygonSlider.AddToClassList("opt-slider");
                    polygonSlider.tooltip = "How far the surface may move, measured on each mesh: Identical half a millimetre (half a pixel seen from a meter away), " +
                                            "Nearly identical 1 mm, Very close 2 mm, Close 4 mm, Lighter 8 mm. What expressions, visemes and toggles' blendshapes move " +
                                            "stays exactly as it is, so do borders, seams and material slots.";
                    row.Add(polygonSlider);
                    var far = new Label("Lighter"); far.AddToClassList("opt-slider__end"); row.Add(far);
                    polygonSlider.RegisterValueChangedCallback(evt =>
                    {
                        if (polygonValue != null) polygonValue.text = Looks[evt.newValue].Name + " · " + Looks[evt.newValue].Budget;
                        // Written once the handle rests: each write measures the avatar again.
                        float chosen = Looks[evt.newValue].Quality;
                        polygonWrite?.Pause();
                        polygonWrite = schedule.Execute(() => { if (!Mathf.Approximately(chosen, Settings.polygonQuality)) Change("My Avatar: polygon quality", s => s.polygonQuality = chosen); }).StartingIn(250);
                    });
                }
                polygonLine = new Label(); polygonLine.AddToClassList("opt-panel__text"); card.Add(polygonLine);
            }
            if (polygonLine == null) return;
            int stop = Stop(Settings.PolygonQuality);
            // The slider shows the saved quality unless its handle is somewhere else, waiting to be written.
            bool pending = polygonSlider != null && polygonSlider.value != stop;
            if (!pending) polygonValue.text = Looks[stop].Name + " · " + Looks[stop].Budget;
            polygonLine.text = result == null || PolygonReduction.Measuring ? "Measuring the polygons…"
                : result.Error != null ? "The polygons couldn't be measured."
                : result.Report.Polygons == 0 ? "No polygon to remove within " + Looks[stop].Budget + ": every one shapes the avatar."
                : Count(result.Report.Polygons) + " polygons fewer at upload · " + Count(result.Now.Polys) + " → " + Count(result.Upload.Polys);
            polygonLine.tooltip = "Each mesh is simplified as far as its surface stays within " + Looks[stop].Budget + " of where it was, at rest, as the avatar stands " +
                                  "and with each body-wide blendshape, and shades the same. The estimate can't see what VRCFury and Modular Avatar build: it counts " +
                                  "every blendshape as used, so the upload can lose fewer polygons than it says.";
        }

        private static int Stop(float quality) => Enumerable.Range(0, Looks.Length).OrderBy(i => Mathf.Abs(Looks[i].Quality - quality)).First();

        // ---- What only d4rkAvatarOptimizer does ----

        private const string Disclaimer = "My Avatar is not associated with d4rkAvatarOptimizer.";
        private const string OptimizerPackage = "d4rkpl4y3r.d4rkavataroptimizer";
        private string shownSuggestion;

        private void ShowSuggestion()
        {
            var type = TextureOptimization.OptimizerType;
            var root = TextureOptimization.AvatarRoot(avatar);
            var present = type != null ? root.GetComponentsInChildren(type, true).FirstOrDefault() : null;
            bool active = present is Behaviour behaviour && behaviour.enabled && behaviour.gameObject.activeInHierarchy;
            string state = type == null ? "missing" : active ? "on" : present ? "off" : "installed";
            if (state == shownSuggestion) return;
            shownSuggestion = state;
            suggestion.Clear();
            if (active) return;
            var row = new VisualElement { tooltip = "d4rkAvatarOptimizer rewrites shaders at upload to merge meshes with different materials into one, which needs knowing each shader's code." };
            row.AddToClassList("optimize-suggestion"); row.AddToClassList("opt-more"); suggestion.Add(row);
            var text = new Label(); text.AddToClassList("optimize-suggestion__text"); row.Add(text);
            if (present) { text.text = "d4rkAvatarOptimizer is on this avatar but disabled, so it will not optimize it at upload."; return; }
            var link = MyAvatarEditor.Button("d4rkAvatarOptimizer ↗", () => Application.OpenURL(TextureOptimization.OptimizerUrl));
            link.tooltip = TextureOptimization.OptimizerUrl; link.AddToClassList("avatar-link"); link.AddToClassList("optimize-suggestion__link");
            const string Pitch = "Merge meshes with different materials too";
            if (type == null)
            {
#if MYAVATAR_VPM
                text.RemoveFromHierarchy();
                var prompt = new Orbiters.Toolkit.Editor.Vpm.DependencyPrompt(OptimizerPackage, Pitch, "orbiters.myavatar");
                prompt.AddToClassList("optimize-suggestion__prompt"); row.Add(prompt);
                prompt.Q(className: "orb-dependency__texts")?.Add(Note());
                var actions = new VisualElement(); actions.AddToClassList("optimize-suggestion__actions"); prompt.Add(actions);
                var install = prompt.Q(className: "orb-dependency__install");
                if (install != null) actions.Add(install);
                actions.Add(link);
#else
                text.text = Pitch;
                row.Add(Note());
                var actions = new VisualElement(); actions.AddToClassList("optimize-suggestion__actions"); row.Add(actions);
                actions.Add(link);
#endif
                return;
            }
            text.text = "d4rkAvatarOptimizer is installed: add it to the avatar to also merge meshes that use different materials at upload.";
            row.Add(Note());
            var buttons = new VisualElement(); buttons.AddToClassList("optimize-suggestion__actions"); row.Add(buttons);
            var add = MyAvatarEditor.Button("Add to avatar", () => { suggestion.Clear(); shownSuggestion = null; Undo.AddComponent(root, type); });
            add.AddToClassList("optimize-suggestion__button"); buttons.Add(add);
            buttons.Add(link);
        }

        private static Label Note()
        {
            var note = new Label(Disclaimer); note.AddToClassList("optimize-suggestion__disclaimer");
            return note;
        }
    }
}
