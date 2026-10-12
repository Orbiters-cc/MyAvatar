using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.MyAvatar.Editor
{
    // Everything that makes the avatar lighter, in one frame like face tracking's: a ring with how much lighter the upload
    // downloads, the rank VRChat gives the avatar now, Small, Big or Custom optimization of the uploaded copy (the scene
    // avatar never changes) with what VRChat would rank and download, the polygons, and a pointer to d4rkAvatarOptimizer for
    // what only shader rewriting can do.
    internal sealed class OptimizationSection : AvatarSection
    {
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/UI/optimization.uss";
        private static readonly Color Glow = new Color(0f, .85f, .43f, .18f);

        private readonly MyAvatar avatar;
        private readonly VisualElement hero, estimate, polygons, suggestion;
        private IVisualElementScheduledItem poll, write, polygonWrite;
        private string shown, shownEstimate, shownPolygons;
        private float pendingDistance = -1f;

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
            RegisterCallback<AttachToPanelEvent>(_ => { poll?.Pause(); poll = schedule.Execute(Refresh).Every(1500); Undo.undoRedoPerformed += Rebuild; });
            RegisterCallback<DetachFromPanelEvent>(_ => { poll?.Pause(); Undo.undoRedoPerformed -= Rebuild; Flush(); });
            Refresh();
        }

        private UploadOptimization Settings => avatar.uploadOptimization;

        // The estimates follow the platform the project builds for: PC, or Quest when it builds for Android. Tests and
        // captures show either without switching the project.
        internal static bool? ShowQuest;
        private static bool Quest => ShowQuest ?? (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS);

        private void Rebuild() { shown = shownEstimate = shownPolygons = null; Refresh(); }

        private void Refresh()
        {
            if (!avatar || panel == null && shown != null) return;
            string state = Settings.level + ":" + (int)Settings.custom + ":" + Quest;
            if (state != shown) { shown = state; ShowHero(); }
            ShowEstimate();
            ShowPolygons();
            ShowSuggestion();
        }

        // ---- The frame: ranks now, the three levels, Custom's switches ----

        private void ShowHero()
        {
            hero.Clear();
            var level = Settings.level;
            string title = level == OptimizationLevel.Off ? "Optimize at upload" : level + " optimization at upload";
            var frame = new ToolHero(hero, Glow, Eyebrow(Measured()?.Now), EyebrowClass(Measured()?.Now), title,
                "Your avatar stays as it is: VRChat gets a lighter copy when you upload, built from what it shows and does.");
            frame.Frame.name = "optimization-hero";
            Saved(frame.Badge);

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

        // The ring: how much lighter the upload downloads than the avatar as it is, for the platform the project builds for.
        private void Saved(VisualElement badge)
        {
            var ring = new FaceTrackingRing(); ring.AddToClassList("ft-hero__ring"); badge.Add(ring);
            var result = Measured();
            if (result == null)
            {
                ring.schedule.Execute(() => ring.Set(0f, "…", "measuring"));
                badge.tooltip = "Measuring the download with and without the changes…";
                return;
            }
            long now = Quest ? result.Now.QuestDownload : result.Now.Download, upload = Quest ? result.Upload.QuestDownload : result.Upload.Download;
            float saved = now > 0 ? Mathf.Clamp01((now - upload) / (float)now) : 0f;
            int percent = Mathf.RoundToInt(saved * 100f);
            ring.schedule.Execute(() => ring.Set(saved, percent + "%", "lighter", anyShareIsGood: true));
            badge.tooltip = Settings.level == OptimizationLevel.Off
                ? "Optimization is off: players download the avatar as it is (" + VrcLoadingBar.Format(now) + ")."
                : "Players download " + percent + "% less: " + VrcLoadingBar.Format(upload) + " instead of " + VrcLoadingBar.Format(now) + " without optimization.";
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
            avatar ? OptimizationEstimate.Get(avatar, Settings.Passes, () => { if (panel != null) Rebuild(); }) : null;

        private static string Eyebrow(OptimizationEstimate.Snapshot now) => now == null ? "MEASURING…"
            : (Quest ? Rank(now.Mobile) + " ON QUEST" : Rank(now.Pc) + " ON PC").ToUpperInvariant();

        private static string EyebrowClass(OptimizationEstimate.Snapshot now) =>
            now == null ? "muted" : (Quest ? now.Mobile : now.Pc) >= PerformanceRating.Poor ? "warning" : null;

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

        private void ShowEstimate()
        {
            var result = Measured();
            bool off = Settings.level == OptimizationLevel.Off;
            bool quest = Quest;
            string state = Settings.Passes + ":" + quest + ":" + (result == null ? "measuring" : result.GetHashCode().ToString());
            if (state == shownEstimate) return;
            shownEstimate = state;
            estimate.Clear();
            var card = new VisualElement(); card.AddToClassList("opt-estimate"); estimate.Add(card);
            var head = new VisualElement(); head.AddToClassList("opt-estimate__head"); card.Add(head);
            var title = new Label(off ? "In VRChat now" : "At upload"); title.AddToClassList("opt-estimate__title"); head.Add(title);
            var aside = new Label(result == null ? "Measuring…" : "estimated"); aside.AddToClassList("opt-estimate__aside"); head.Add(aside);
            aside.tooltip = off ? "Measured with the VRChat SDK on the avatar as it is; the download is estimated from what the upload holds."
                : "Measured with the VRChat SDK on a hidden copy of the avatar put through these changes. Tools that build at upload (VRCFury, Modular Avatar) haven't run on it.";
            if (result == null) return;
            var now = result.Now; var upload = result.Upload;
            // What players see first: VRChat's loading bar over the avatar while it downloads.
            if (quest)
                Download(card, "Quest", now.QuestDownload, upload.QuestDownload, off ? now.QuestLoad : upload.QuestLoad, off, QuestLimit, "10 MB",
                    "What Quest players see over the avatar while it downloads: the file's size, compressed as VRChat compresses it, estimated from what the " +
                    "upload holds (textures with their Android settings, meshes, blendshapes, animations, shaders). Then the time it takes to unpack and set up " +
                    "on a Quest, about four times slower than a PC. Switch the project to PC to see the PC estimates.");
            else
                Download(card, "PC", now.Download, upload.Download, off ? now.Load : upload.Load, off, PcLimit, "200 MB",
                    "What players see over the avatar while it downloads: the file's size, compressed as VRChat compresses it, estimated from what the upload " +
                    "holds (textures in their PC format, meshes, blendshapes, animations, shaders). Then the time it takes to unpack and set up on a desktop PC. " +
                    "Switch the project to Android to see the Quest estimates.");
            var ranks = new VisualElement(); ranks.AddToClassList("opt-ranks"); card.Add(ranks);
            if (quest) RankRow(ranks, now.Mobile, upload.Mobile);
            else RankRow(ranks, now.Pc, upload.Pc);
            // Why the rank stays where it is: what no change here can bring down.
            var limits = quest ? upload.MobileLimits : upload.PcLimits;
            if (limits.Count > 0)
            {
                var reasons = new Label(Rank(quest ? upload.Mobile : upload.Pc) + " from " + string.Join(", ", limits.Take(3)) + (limits.Count > 3 ? "…" : ""));
                reasons.AddToClassList("opt-estimate__why"); card.Add(reasons);
            }
            if (off) return;
            var rows = new VisualElement(); rows.AddToClassList("opt-stats"); card.Add(rows);
            Stat(rows, "Skinned meshes", now.Skinned, upload.Skinned);
            Stat(rows, "Material slots", now.Materials, upload.Materials);
            Stat(rows, "Bones", now.Bones, upload.Bones);
            Stat(rows, "PhysBones", now.PhysBones, upload.PhysBones);
            Stat(rows, "PhysBone transforms", now.PhysBoneTransforms, upload.PhysBoneTransforms);
            Stat(rows, "PhysBone colliders", now.Colliders, upload.Colliders);
            Stat(rows, "Contacts", now.Contacts, upload.Contacts);
            Stat(rows, "Constraints", now.Constraints, upload.Constraints);
            if (quest) Stat(rows, "Texture memory", now.QuestTextureMegabytes, upload.QuestTextureMegabytes, " MB", "With the textures' Android settings, as VRChat counts it: every texture the materials point at.");
            else Stat(rows, "Texture memory", now.TextureMegabytes, upload.TextureMegabytes, " MB", "In the formats the PC build will have, as VRChat counts it: every texture the materials point at.");
            Stat(rows, "Blendshapes", now.BlendShapes, upload.BlendShapes, null, "Not ranked, but unused blendshapes are often most of the download size.");
            if (rows.childCount == 0)
            {
                var none = new Label("Nothing VRChat counts changes on this avatar: it already uses everything it uploads.");
                none.AddToClassList("opt-estimate__none"); card.Add(none);
            }
        }

        private const long PcLimit = 200L * 1048576, QuestLimit = 10L * 1048576;

        // VRChat's loading bar with the size players will download (at upload when optimization is on, with what it saves),
        // then how long the avatar takes to show once downloaded; over VRChat's upload limit in orange.
        private static void Download(VisualElement parent, string platform, long before, long after, float seconds, bool off, long limit, string limitText, string tooltip)
        {
            long shown = off ? before : after;
            var row = new VisualElement { tooltip = tooltip }; row.AddToClassList("opt-download"); parent.Add(row);
            var label = new Label(platform); label.AddToClassList("opt-rank__platform"); row.Add(label);
            var bar = new VrcLoadingBar(); bar.SetSize(shown); row.Add(bar);
            if (!off && Math.Abs(before - after) >= 52429)
            {
                var delta = new Label((after < before ? "−" : "+") + DownloadSize.Megabytes(Math.Abs(before - after)));
                delta.AddToClassList("opt-stat__delta"); delta.AddToClassList("opt-download__delta"); delta.EnableInClassList("opt-stat__delta--worse", after > before); row.Add(delta);
            }
            string text = "Shows about " + Seconds(seconds) + " after it downloads";
            if (!off && Math.Abs(before - after) >= 52429) text += " · " + VrcLoadingBar.Format(before) + " now";
            if (shown > limit) text += " · over VRChat's " + limitText + " limit: the upload would be refused";
            var caption = new Label(text); caption.AddToClassList("opt-download__caption"); caption.EnableInClassList("warning", shown > limit); parent.Add(caption);
        }

        private static string Seconds(float seconds) => (seconds < 10f ? seconds.ToString("0.0") : seconds.ToString("0")) + " s";

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
            string Text(float v) => unit == null ? Mathf.RoundToInt(v).ToString("N0") : v.ToString(v < 10 ? "0.0" : "0") + unit;
            var values = new Label(Text(before) + " → " + Text(after)); values.AddToClassList("opt-stat__values"); row.Add(values);
            var delta = new Label((after < before ? "−" : "+") + (unit == null ? Mathf.RoundToInt(Mathf.Abs(before - after)).ToString("N0") : Mathf.Abs(before - after).ToString("0.#") + unit));
            delta.AddToClassList("opt-stat__delta"); delta.EnableInClassList("opt-stat__delta--worse", after > before); row.Add(delta);
        }

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
                             TextureDensity.PixelsPerMeter.ToString("N0") + " pixels across a meter of surface seen from one meter, in today's headsets). " +
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

        private static string Distance(float meters) => meters < 1f ? Mathf.RoundToInt(meters * 100f) + " cm" : meters.ToString("0.#") + " m";

        // ---- Polygons ----

        // How close to the original the meshes stay, from removing only what no one sees to small visible changes.
        private static readonly (float Quality, string Name, string Budget)[] Looks =
            { (1f, "Identical", "½ mm"), (.75f, "Nearly identical", "1 mm"), (.5f, "Very close", "2 mm"), (.25f, "Close", "4 mm"), (0f, "Lighter", "8 mm") };

        // What the upload does to the polygons; the slider under Custom.
        private void ShowPolygons()
        {
            bool on = (Settings.Passes & OptimizationPasses.FewerPolygons) != 0, custom = Settings.level == OptimizationLevel.Custom;
            var result = Measured();
            bool measuring = PolygonReduction.Measuring;
            float quality = Settings.PolygonQuality;
            string state = on + ":" + custom + ":" + quality + ":" + measuring + ":" + (result == null ? "-" : result.Report.Polygons + "/" + result.Upload.Polys);
            if (state == shownPolygons) return;
            shownPolygons = state;
            polygons.Clear();
            if (!on) return;
            var card = new VisualElement(); card.AddToClassList("opt-panel"); polygons.Add(card);
            var head = new VisualElement(); head.AddToClassList("opt-panel__head"); card.Add(head);
            var title = new Label("Polygons"); title.AddToClassList("opt-estimate__title"); head.Add(title);
            int stop = Enumerable.Range(0, Looks.Length).OrderBy(i => Mathf.Abs(Looks[i].Quality - quality)).First();
            var value = new Label(Looks[stop].Name + " · " + Looks[stop].Budget); value.AddToClassList("opt-panel__value"); head.Add(value);
            if (custom)
            {
                var row = new VisualElement(); row.AddToClassList("opt-slider-row"); card.Add(row);
                var near = new Label("Identical"); near.AddToClassList("opt-slider__end"); row.Add(near);
                var slider = new SliderInt(0, Looks.Length - 1) { value = stop };
                slider.AddToClassList("opt-slider");
                slider.tooltip = "How far the surface may move, measured on each mesh: Identical half a millimetre (half a pixel seen from a meter away), " +
                                 "Nearly identical 1 mm, Very close 2 mm, Close 4 mm, Lighter 8 mm. What expressions, visemes and toggles' blendshapes move " +
                                 "stays exactly as it is, so do borders, seams and material slots.";
                row.Add(slider);
                var far = new Label("Lighter"); far.AddToClassList("opt-slider__end"); row.Add(far);
                slider.RegisterValueChangedCallback(evt =>
                {
                    value.text = Looks[evt.newValue].Name + " · " + Looks[evt.newValue].Budget;
                    // Written once the handle rests: each write measures the avatar again.
                    float chosen = Looks[evt.newValue].Quality;
                    polygonWrite?.Pause();
                    polygonWrite = schedule.Execute(() => { if (!Mathf.Approximately(chosen, Settings.polygonQuality)) Change("My Avatar: polygon quality", s => s.polygonQuality = chosen); }).StartingIn(250);
                });
            }
            var line = new Label(); line.AddToClassList("opt-panel__text"); card.Add(line);
            line.text = result == null || measuring ? "Measuring the polygons…"
                : result.Report.Polygons == 0 ? "No polygon to remove within " + Looks[stop].Budget + ": every one shapes the avatar."
                : result.Report.Polygons.ToString("N0") + " polygons fewer at upload · " + result.Now.Polys.ToString("N0") + " → " + result.Upload.Polys.ToString("N0");
            line.tooltip = "Each mesh is simplified as far as its surface stays within " + Looks[stop].Budget + " of where it was, at rest, as the avatar stands " +
                           "and with each body-wide blendshape, and shades the same. The estimate can't see what VRCFury and Modular Avatar build: it counts " +
                           "every blendshape as used, so the upload can lose fewer polygons than it says.";
        }

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
