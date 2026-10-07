using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.FaceTracking;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Face tracking with Adjerry91's templates. The section shows only when the face has most of the blendshapes face
    // tracking needs (then it offers the one-click setup) or when face tracking is set up (then what and how, the quick
    // settings, and the live test with an iPhone or the simulator).
    internal sealed partial class FaceTrackingSection : AvatarSection
    {
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/UI/face-tracking.uss";
        private static readonly string[] SmoothingCaptions =
        {
            "Snappy: every value follows at once, the mouth as fast as the eyes. Best on a steady 90 fps.",
            "The template's smoothing, with the mouth a little slower than the eyes like the Ultirex. Recommended.",
            "Calm: everything glides, the mouth most. Hides tracking jitter, at the cost of a slight lag.",
        };

        private readonly MyAvatar avatar;
        private readonly VisualElement hero, live, settings;
        private readonly Label note;
        private IVisualElementScheduledItem pending;
        private string shown;
        private GameObject root;
        private FaceTrackingReport report;

        internal FaceTrackingSection(MyAvatar avatar) : base("Face tracking", card: false)
        {
            this.avatar = avatar;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("ft");
            hero = new VisualElement(); Body.Add(hero);
            live = new VisualElement(); Body.Add(live);
            settings = new VisualElement(); Body.Add(settings);
            note = new Label(); note.AddToClassList("ft-note"); note.style.display = DisplayStyle.None; Body.Add(note);
            var credit = new VisualElement(); credit.AddToClassList("ft-credit"); Body.Add(credit);
            var text = new Label("Face tracking blendshapes are animated by"); text.AddToClassList("avatar-caption"); credit.Add(text);
            var link = MyAvatarEditor.Button("Adjerry91’s Face Tracking Templates", () => Application.OpenURL(FaceTrackingDetection.Repository));
            link.AddToClassList("avatar-link"); link.AddToClassList("ft-credit__link");
            link.tooltip = FaceTrackingDetection.Repository + "\nCredit this on your store or product page when you sell an avatar that uses it.";
            credit.Add(link);
            EditorApplication.hierarchyChanged += Schedule;
            Undo.undoRedoPerformed += Schedule;
            FaceTrackingTest.Changed += TestChanged;
            FaceTrackingTest.Rendered += Repaint;
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule;
                FaceTrackingTest.Changed -= TestChanged; FaceTrackingTest.Rendered -= Repaint;
                FaceTrackingTest.Current?.Unwatch(this);
            });
            RegisterCallback<AttachToPanelEvent>(_ => { if (Test != null) Test.Watch(this); });
            Refresh();
        }

        private MyAvatarFaceTracking Marker => report?.Ours;
        private FaceTrackingTest Test => FaceTrackingTest.Current != null && root != null && FaceTrackingTest.Current.Root == root ? FaceTrackingTest.Current : null;

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(400);
        }

        private void Refresh()
        {
            root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            report = FaceTrackingDetection.Inspect(root != null ? root.transform : null);
            string state = report.Ours != null ? "ours:" + report.Ours.GetInstanceID() + ":" + report.Found + ":" + report.Standard?.Id
                : report.Existing != null ? "other:" + report.Existing
                : report.Ready ? "ready:" + report.Standard.Id + ":" + report.Found + ":" + FaceTrackingDetection.TemplatesInstalled : "";
            style.display = state.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (state != shown)
            {
                shown = state;
                hero.Clear(); settings.Clear(); Actions.Clear();
                if (report.Ours != null) { ShowOurs(); BuildSettings(); }
                else if (report.Existing != null) ShowOther();
                else if (report.Ready) ShowSuggestion();
            }
            else UpdateSettings();
            if (Test != null && report.Ours != null) { Test.Watch(this); Test.Apply(report.Ours); }
            ShowTest();
        }

        // ---- Hero ----

        private (VisualElement texts, VisualElement actions) Hero(string eyebrow, string eyebrowClass, string title, string subtitle)
        {
            var frame = new VisualElement(); frame.AddToClassList("ft-hero"); hero.Add(frame);
            // The glow sits behind the ring, inside the frame.
            var badge = new VisualElement(); badge.AddToClassList("ft-hero__badge"); frame.Add(badge);
            var glow = new FaceTrackingGlow(new Color(0f, .85f, .43f, .18f)); glow.AddToClassList("ft-hero__glow"); badge.Add(glow);
            var ring = new FaceTrackingRing(); ring.AddToClassList("ft-hero__ring"); badge.Add(ring);
            if (report.Standard != null && report.Needed > 0)
            {
                float fraction = report.Found / (float)report.Needed;
                ring.schedule.Execute(() => ring.Set(fraction, Mathf.RoundToInt(fraction * 100f) + "%"));
                ring.tooltip = report.Found + " of the " + report.Needed + " main " + report.Standard.Label + " shapes are on the face" +
                               (report.Missing.Count > 0 ? " (not: " + string.Join(", ", report.Missing) + ")." : ".");
            }
            else ring.schedule.Execute(() => ring.Set(1f, "✓", "set up"));
            var texts = new VisualElement(); texts.AddToClassList("ft-hero__texts"); frame.Add(texts);
            var eye = new Label(eyebrow); eye.AddToClassList("ft-hero__eyebrow"); if (eyebrowClass != null) eye.AddToClassList(eyebrowClass); texts.Add(eye);
            var head = new Label(title); head.AddToClassList("ft-hero__title"); texts.Add(head);
            if (!string.IsNullOrEmpty(subtitle)) { var sub = new Label(subtitle); sub.AddToClassList("ft-hero__subtitle"); texts.Add(sub); }
            var actions = new VisualElement(); actions.AddToClassList("ft-hero__actions"); texts.Add(actions);
            return (texts, actions);
        }

        private void ShowSuggestion()
        {
            var standard = report.Standard;
            var (_, actions) = Hero("READY FOR FACE TRACKING", null, "This face can be tracked",
                (report.Found == report.Needed ? "All " : report.Found + " of the ") + report.Needed + " " + standard.Label + " shapes are on “" + report.Face.name +
                "”. Adjerry91’s template goes on with VRCFury; VRCFaceTracking drives it in VRChat. About " + standard.Bits + " parameter bits, fewer with the quick settings." +
                (FaceTrackingDetection.TemplatesInstalled ? "" : " The templates are downloaded first (Creator Companion)."));
            var setUp = MyAvatarEditor.Button("Set up face tracking", null);
            setUp.AddToClassList("ft-primary");
            if (report.Missing.Count > 0) setUp.tooltip = "Not on this face: " + string.Join(", ", report.Missing) + ".";
            setUp.clicked += () =>
            {
                setUp.SetEnabled(false);
                SetNote(FaceTrackingDetection.TemplatesInstalled ? "Setting up face tracking…" : "Downloading Adjerry91’s Face Tracking Templates…", false);
                // Paint the immediate response before the download or the prefab and Undo work.
                schedule.Execute(() =>
                {
                    string result = FaceTrackingSetup.Start(avatar, root.transform);
                    SetNote(result, result != null && !result.StartsWith("Downloaded"));
                    setUp.SetEnabled(true);
                    Refresh();
                });
            };
            actions.Add(setUp);
        }

        private void ShowOurs()
        {
            var marker = report.Ours;
            string standard = report.Standard != null ? report.Standard.Label : marker.standard;
            string coverage = report.Standard == null ? "" : (report.Found == report.Needed ? "All " : report.Found + " of the ") + report.Needed + " tracking shapes are on “" +
                (marker.face != null ? marker.face.name : "the face") + "”; names that differ are matched at upload.";
            var (_, actions) = Hero("FACE TRACKING ON", null, "Adjerry91’s " + standard + " template", coverage + " Turn on VRCFaceTracking in VRChat to use it.");
            var test = MyAvatarEditor.Button("Test live", null);
            test.AddToClassList("ft-primary");
            test.tooltip = "Drive the uploaded version of this avatar with your iPhone (iFacialMocap or Live Link Face) or the simulator, without Play Mode." +
                           (FaceTrackingTestBuild.Kept(root) ? "" : "\nThe first test builds the avatar like an upload (about a minute).");
            test.clicked += StartTest;
            actions.Add(test);
            var select = MyAvatarEditor.Button("Select", () => { Selection.activeGameObject = marker.gameObject; EditorGUIUtility.PingObject(marker.gameObject); });
            select.AddToClassList("avatar-link");
            select.tooltip = "Select the template on the avatar.";
            Actions.Add(select);
            var remove = MyAvatarEditor.Button("Remove", null);
            remove.AddToClassList("avatar-link");
            remove.tooltip = "Take the template off this avatar. Undo puts it back; the downloaded templates stay installed.";
            remove.clicked += () =>
            {
                FaceTrackingTest.StopAll();
                SetEnabled(false);
                schedule.Execute(() => { FaceTrackingSetup.Remove(marker); SetEnabled(true); SetNote(null, false); Refresh(); });
            };
            Actions.Add(remove);
        }

        private void ShowOther()
        {
            var (_, actions) = Hero("FACE TRACKING", "muted", "Already set up", "Face tracking is already set up: " + report.Existing + ". The quick settings and the live test work with the template My Avatar sets up.");
            if (report.ExistingObject == null) return;
            var target = report.ExistingObject;
            var select = MyAvatarEditor.Button("Select", () => { Selection.activeGameObject = target; EditorGUIUtility.PingObject(target); });
            select.AddToClassList("ft-ghost");
            actions.Add(select);
        }

        private void SetNote(string text, bool warning)
        {
            note.text = text;
            note.EnableInClassList("warning", warning);
            note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ---- Quick settings ----

        private SegmentedControl smoothing;
        private Label smoothingCaption, budgetText;
        private readonly List<(FaceTrackingFeature feature, VisualElement row, ToggleSwitch toggle, Label bits)> featureRows = new List<(FaceTrackingFeature, VisualElement, ToggleSwitch, Label)>();
        private readonly List<(FaceTrackingFeatures features, Button button)> presetButtons = new List<(FaceTrackingFeatures, Button)>();
        private ToggleSwitch expressive;
        private BudgetBar budget;
        private FaceTrackingFeatureSet.Costs costs;
        private IVisualElementScheduledItem budgetPending;

        private void BuildSettings()
        {
            var marker = Marker;
            if (marker == null) return;
            featureRows.Clear(); presetButtons.Clear();
            costs = FaceTrackingFeatureSet.CostsOf(marker.gameObject);
            var card = new VisualElement(); card.AddToClassList("ft-card"); settings.Add(card);
            var header = new VisualElement(); header.AddToClassList("ft-card__header"); card.Add(header);
            var title = new Label("Quick settings"); title.AddToClassList("ft-card__title"); header.Add(title);
            var aside = new Label("Applied when you upload"); aside.AddToClassList("ft-card__aside"); header.Add(aside);

            // Smoothing
            var smoothRow = Setting(card, "Smoothing", null, true);
            smoothing = new SegmentedControl(new[]
            {
                new SegmentedControl.Option("Responsive", null, "Local smoothing 0: values follow at once."),
                new SegmentedControl.Option("Balanced", null, "Local smoothing 0.25 (the template's), the mouth 2.35× slower."),
                new SegmentedControl.Option("Smooth", null, "Local smoothing 0.6, the mouth 2.35× slower."),
            }, index => Change("Face tracking smoothing", m => m.smoothing = (FaceTrackingSmoothing)index));
            smoothRow.Add(smoothing);
            smoothingCaption = new Label(); smoothingCaption.AddToClassList("ft-setting__caption"); smoothRow.Add(smoothingCaption);

            // Features others see
            var featureSetting = Setting(card, "Others see", out var featureHeader);
            var presets = new VisualElement(); presets.AddToClassList("ft-presets"); featureHeader.Add(presets);
            foreach (var (label, features) in FaceTrackingFeature.Presets)
            {
                var button = MyAvatarEditor.Button(label, () => Change("Face tracking features", m => m.synced = features));
                button.AddToClassList("ft-preset");
                button.tooltip = costs.Total(features) + " bits";
                presets.Add(button);
                presetButtons.Add((features, button));
            }
            foreach (var feature in FaceTrackingFeature.All.Where(f => costs.Has(f.Flag)))
            {
                var row = new VisualElement(); row.AddToClassList("ft-feature");
                if (feature.Parent != FaceTrackingFeatures.None) row.AddToClassList("ft-feature--child");
                var texts = new VisualElement(); texts.AddToClassList("ft-feature__texts"); row.Add(texts);
                var name = new Label(feature.Label); name.AddToClassList("ft-feature__name"); texts.Add(name);
                var detail = new Label(feature.Detail); detail.AddToClassList("ft-feature__detail"); texts.Add(detail);
                var bits = new Label(costs.Features[feature.Flag] + " bits"); bits.AddToClassList("ft-bits"); row.Add(bits);
                var flag = feature.Flag;
                var toggle = new ToggleSwitch((marker.synced & flag) != 0, on => Change("Face tracking features", m => m.synced = on ? m.synced | flag : m.synced & ~flag));
                row.Add(toggle);
                featureSetting.Add(row);
                featureRows.Add((feature, row, toggle, bits));
            }
            var budgetFrame = new VisualElement(); budgetFrame.AddToClassList("ft-budget"); featureSetting.Add(budgetFrame);
            budget = new BudgetBar(); budget.AddToClassList("orb-budget--inline"); budgetFrame.Add(budget);
            budgetText = new Label(); budgetText.AddToClassList("ft-budget__text"); budgetFrame.Add(budgetText);

            // Expressive mouth
            var mouth = Setting(card, "Expressive mouth", out _);
            var mouthRow = new VisualElement(); mouthRow.AddToClassList("ft-switch-row"); mouth.Add(mouthRow);
            bool grin = marker.face != null && marker.face.sharedMesh != null && marker.face.sharedMesh.GetBlendShapeIndex("Grin_L") >= 0;
            var mouthText = new Label("Smiles and frowns reach full at 80 %, as VRCFaceTracking seldom sends more" +
                                      (grin ? "; a smile with the lip raised becomes this face's own grin, with the Ultirex's shape weights." : ", like the Ultirex's hand-made tracking."));
            mouthText.AddToClassList("ft-feature__detail"); mouthText.style.flexGrow = 1; mouthText.style.flexShrink = 1; mouthText.style.marginRight = 10;
            mouthRow.Add(mouthText);
            expressive = new ToggleSwitch(marker.expressiveMouth, on => Change("Face tracking expressive mouth", m => m.expressiveMouth = on));
            mouthRow.Add(expressive);
            UpdateSettings();
        }

        private VisualElement Setting(VisualElement card, string title, string aside, bool first = false)
        {
            var setting = Setting(card, title, out var header);
            if (first) setting.AddToClassList("ft-setting--first");
            if (aside != null) { var label = new Label(aside); label.AddToClassList("ft-setting__aside"); header.Add(label); }
            return setting;
        }

        private VisualElement Setting(VisualElement card, string title, out VisualElement header)
        {
            var setting = new VisualElement(); setting.AddToClassList("ft-setting"); card.Add(setting);
            header = new VisualElement(); header.AddToClassList("ft-setting__header"); setting.Add(header);
            var label = new Label(title); label.AddToClassList("ft-setting__title"); header.Add(label);
            return setting;
        }

        // The features this template has: a preset is chosen when it keeps the same ones.
        private FaceTrackingFeatures Mask => FaceTrackingFeature.All.Where(f => costs.Has(f.Flag)).Aggregate(FaceTrackingFeatures.None, (a, f) => a | f.Flag);

        // One Undo step on the marker; the controls show the change at once, the budget and the live test follow.
        private void Change(string undo, System.Action<MyAvatarFaceTracking> change)
        {
            var marker = Marker;
            if (marker == null) return;
            Undo.RecordObject(marker, undo);
            change(marker);
            EditorUtility.SetDirty(marker);
            PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
            UpdateSettings(budgetNow: false);
            Test?.Apply(marker);
            budgetPending?.Pause();
            budgetPending = schedule.Execute(UpdateBudget).StartingIn(60);
        }

        private void UpdateSettings() => UpdateSettings(true);

        private void UpdateSettings(bool budgetNow)
        {
            var marker = Marker;
            if (marker == null || smoothing == null) return;
            smoothing.SetIndex((int)marker.smoothing);
            smoothingCaption.text = SmoothingCaptions[(int)marker.smoothing];
            var effective = FaceTrackingFeatureSet.Effective(marker.synced);
            foreach (var (feature, row, toggle, bits) in featureRows)
            {
                toggle.SetValueWithoutNotify((marker.synced & feature.Flag) != 0);
                bool parentOn = feature.Parent == FaceTrackingFeatures.None || (marker.synced & feature.Parent) != 0;
                row.SetEnabled(parentOn);
                bits.EnableInClassList("ft-bits--off", (effective & feature.Flag) == 0);
            }
            var mask = Mask;
            foreach (var (features, button) in presetButtons)
                button.EnableInClassList("ft-preset--on", (FaceTrackingFeatureSet.Effective(features) & mask) == (effective & mask));
            expressive.SetValueWithoutNotify(marker.expressiveMouth);
            if (budgetNow) UpdateBudget();
        }

        // VRChat's 256 synced bits: this avatar's own, the face tracking it keeps, and what is left.
        private void UpdateBudget()
        {
            var marker = Marker;
            if (marker == null || budget == null || root == null) return;
            var estimate = AvatarParameterBudget.Estimate(root);
            int face = costs.Total(marker.synced), all = estimate.TotalBeforeCompression, own = Mathf.Max(0, all - face);
            int built = estimate.BuiltBits, free = Mathf.Max(0, ParameterBudget.MaxSyncedBits - built);
            budget.SetSegments(new[]
            {
                new BudgetBar.Segment("Avatar " + own, own, new Color32(90, 94, 104, 255)),
                new BudgetBar.Segment("Face tracking " + face + " of " + costs.All, face, new Color32(138, 125, 255, 255)),
                new BudgetBar.Segment("Free " + free, free, new Color32(0, 218, 109, 255)),
            });
            string text = all + " of " + ParameterBudget.MaxSyncedBits + " synced bits";
            if (estimate.OverBudget) text += ": over VRChat's limit even after VRCFury's compression. Leave out a feature, or other synced parameters.";
            else if (estimate.Compresses) text += " before VRCFury compresses " + estimate.CompressedParameters + " menu parameters to fit (≈" + built + " bits, a full sync every " + estimate.SyncSeconds.ToString("0.0") + " s). Face tracking itself is never compressed.";
            else text += ".";
            budgetText.text = text;
            budgetText.EnableInClassList("warning", estimate.OverBudget);
        }
    }
}
