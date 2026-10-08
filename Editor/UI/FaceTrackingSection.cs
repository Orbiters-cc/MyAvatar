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
    // settings folded in the same frame, and the live test with an iPhone or the simulator).
    internal sealed partial class FaceTrackingSection : AvatarSection
    {
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/UI/face-tracking.uss";
        private const string OpenKey = "Orbiters.MyAvatar.FaceTracking.QuickSettingsOpen.";
        private const string LogoPath = "Packages/orbiters.myavatar/Editor/UI/VRCFaceTrackingLogo.svg.txt";
        private static readonly string[] SmoothingCaptions =
        {
            "Snappy: every value follows at once, the mouth as fast as the eyes. Best on a steady 90 fps.",
            "The template's smoothing, with the mouth a little slower than the eyes. Recommended.",
            "Calm: everything glides, the mouth most. Hides tracking jitter, at the cost of a slight lag.",
        };

        private readonly MyAvatar avatar;
        private readonly VisualElement hero, live;
        private readonly Label note;
        private VisualElement heroTop;
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
            // The live test shows above the frame; the frame keeps the quick settings while it runs.
            live = new VisualElement(); Body.Add(live);
            hero = new VisualElement(); Body.Add(hero);
            note = new Label(); note.AddToClassList("ft-note"); note.style.display = DisplayStyle.None; Body.Add(note);
            var credit = new VisualElement(); credit.AddToClassList("ft-credit"); Body.Add(credit);
            var text = new Label("Face tracking blendshapes are animated by"); text.AddToClassList("avatar-caption"); credit.Add(text);
            var link = MyAvatarEditor.Button("Adjerry91’s Face Tracking Templates", () => Application.OpenURL(FaceTrackingDetection.Repository));
            link.AddToClassList("avatar-link"); link.AddToClassList("ft-credit__link");
            link.tooltip = FaceTrackingDetection.Repository + "\nCredit this on your store or product page when you sell an avatar that uses it.";
            credit.Add(link);
            Subscribe();
            RegisterCallback<DetachFromPanelEvent>(_ => { Unsubscribe(); detached = true; FaceTrackingTest.Current?.Unwatch(this); });
            // Back in a panel (the Inspector rebuilt or docked again): listen again, and catch up on what changed meanwhile.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                Subscribe();
                if (Test != null) Test.Watch(this);
                if (detached) { detached = false; Schedule(); }
            });
            Refresh();
        }

        private bool detached;

        private void Subscribe()
        {
            Unsubscribe();
            EditorApplication.hierarchyChanged += Schedule; Undo.undoRedoPerformed += Schedule;
            FaceTrackingTest.Changed += TestChanged; FaceTrackingTest.Rendered += Repaint;
        }

        private void Unsubscribe()
        {
            EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule;
            FaceTrackingTest.Changed -= TestChanged; FaceTrackingTest.Rendered -= Repaint;
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
            CheckFace();
            string state = report.Ours != null ? "ours:" + report.Ours.GetInstanceID() + ":" + report.Found + ":" + report.Standard?.Id + ":" + faceIssue
                : report.Existing != null ? "other:" + report.Existing
                : report.Ready ? "ready:" + report.Standard.Id + ":" + report.Found + ":" + FaceTrackingDetection.TemplatesInstalled : "";
            style.display = state.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (state != shown)
            {
                shown = state;
                hero.Clear(); Actions.Clear(); heroTop = null; smoothing = null;
                if (report.Ours != null) ShowOurs();
                else if (report.Existing != null) ShowOther();
                else if (report.Ready) ShowSuggestion();
            }
            else UpdateSettings();
            if (Test != null && report.Ours != null) { Test.Watch(this); Test.Apply(report.Ours); }
            ShowTest();
        }

        // ---- Hero ----

        private (VisualElement frame, VisualElement actions) Hero(string eyebrow, string eyebrowClass, string title, string subtitle, string version = null)
        {
            var frame = new VisualElement(); frame.AddToClassList("ft-hero"); hero.Add(frame);
            heroTop = new VisualElement(); heroTop.AddToClassList("ft-hero__top"); frame.Add(heroTop);
            // The glow sits behind the ring, inside the frame.
            var badge = new VisualElement(); badge.AddToClassList("ft-hero__badge"); heroTop.Add(badge);
            var glow = new FaceTrackingGlow(new Color(0f, .85f, .43f, .18f)); glow.AddToClassList("ft-hero__glow"); badge.Add(glow);
            var ring = new FaceTrackingRing(); ring.AddToClassList("ft-hero__ring"); badge.Add(ring);
            // VRCFaceTracking's mark inside the ring; the ring fills with the face's share of the tracking shapes.
            var svg = AssetDatabase.LoadAssetAtPath<TextAsset>(LogoPath);
            if (svg != null) { var logo = new OrbitersVectorLogo(svg.text); logo.AddToClassList("ft-hero__logo"); badge.Add(logo); }
            if (report.Standard != null && report.Needed > 0)
            {
                float fraction = report.Found / (float)report.Needed;
                ring.schedule.Execute(() => ring.Set(fraction, svg != null ? "" : Mathf.RoundToInt(fraction * 100f) + "%"));
                badge.tooltip = Mathf.RoundToInt(fraction * 100f) + "%: " + report.Found + " of the " + report.Needed + " main " + report.Standard.Label + " shapes are on the face" +
                                (report.Missing.Count > 0 ? " (not: " + string.Join(", ", report.Missing) + ")." : ".");
            }
            else ring.schedule.Execute(() => ring.Set(1f, svg != null ? "" : "✓", "set up"));
            var texts = new VisualElement(); texts.AddToClassList("ft-hero__texts"); heroTop.Add(texts);
            var eye = new Label(eyebrow); eye.AddToClassList("ft-hero__eyebrow"); if (eyebrowClass != null) eye.AddToClassList(eyebrowClass); texts.Add(eye);
            var titleRow = new VisualElement(); titleRow.AddToClassList("ft-hero__title-row"); texts.Add(titleRow);
            var head = new Label(title); head.AddToClassList("ft-hero__title"); titleRow.Add(head);
            if (!string.IsNullOrEmpty(version)) { var pill = new Label(version); pill.AddToClassList("ft-hero__version"); titleRow.Add(pill); }
            if (!string.IsNullOrEmpty(subtitle)) { var sub = new Label(subtitle); sub.AddToClassList("ft-hero__subtitle"); texts.Add(sub); }
            var actions = new VisualElement(); actions.AddToClassList("ft-hero__actions"); texts.Add(actions);
            return (frame, actions);
        }

        // The ring, title and actions; hidden while the live test shows above the frame.
        private void ShowHeroTop(bool show)
        {
            if (heroTop != null) heroTop.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (hero.childCount > 0) hero[0].EnableInClassList("ft-hero--settings-only", !show);
        }

        private void ShowSuggestion()
        {
            var standard = report.Standard;
            var (_, actions) = Hero("READY FOR FACE TRACKING", null, "Face tracking available",
                (report.Found == report.Needed ? "All " : report.Found + " of the ") + report.Needed + " " + standard.Label + " shapes are on “" + report.Face.name +
                "”. Adjerry91’s template goes on with VRCFury; VRCFaceTracking drives it in VRChat. About " + standard.Bits + " parameter bits, fewer once you pick your tracker." +
                (FaceTrackingDetection.TemplatesInstalled ? "" : " The templates are downloaded first (Creator Companion)."));
            var setUp = MyAvatarEditor.Button("Add", null);
            setUp.AddToClassList("ft-primary");
            setUp.tooltip = "Add face tracking to this avatar (one Undo step)." + (report.Missing.Count > 0 ? "\nNot on this face: " + string.Join(", ", report.Missing) + "." : "");
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
            actions.Add(VrcftLink());
        }

        // VRCFaceTracking runs on the PC beside VRChat and sends it the tracker's data.
        private static Button VrcftLink()
        {
            var link = MyAvatarEditor.Button("Get VRCFaceTracking", () => Application.OpenURL(FaceTrackingDetection.VrcftInstall));
            link.AddToClassList("ft-ghost");
            link.tooltip = "VRCFaceTracking is the free desktop app that sends your tracker's data to VRChat. Opens its install guide:\n" + FaceTrackingDetection.VrcftInstall;
            return link;
        }

        private void ShowOurs()
        {
            var marker = report.Ours;
            string standard = report.Standard != null ? report.Standard.Label : marker.standard;
            string coverage = report.Standard == null ? "" : (report.Found == report.Needed ? "All " : report.Found + " of the ") + report.Needed + " " + standard + " shapes are on “" +
                (marker.face != null ? marker.face.name : "the face") + "”; names that differ are matched at upload.";
            string version = FaceTrackingDetection.TemplatesVersion;
            var (frame, actions) = faceIssue != null
                ? Hero("CHECK THE FACE", "warning", "Adjerry91’s face tracking template", faceIssue, version != null ? "v" + version : null)
                : Hero("FACE TRACKING ON", null, "Adjerry91’s face tracking template", coverage + " Run VRCFaceTracking on your PC with VRChat to use it.",
                    version != null ? "v" + version : null);
            if (faceIssue != null && faceFound != null)
            {
                var repoint = MyAvatarEditor.Button("Point at “" + faceFound.name + "”", null);
                repoint.AddToClassList("ft-primary");
                repoint.tooltip = "Point the template at the face where it is now (one Undo step).";
                repoint.clicked += () =>
                {
                    SetEnabled(false);
                    schedule.Execute(() =>
                    {
                        string problem = root != null ? FaceTrackingSetup.Repoint(marker, root.transform) : null;
                        SetEnabled(true); SetNote(problem, problem != null); Refresh();
                    });
                };
                actions.Add(repoint);
            }
            var test = MyAvatarEditor.Button("Test live", null);
            test.AddToClassList(faceIssue != null && faceFound != null ? "ft-ghost" : "ft-primary");
            test.tooltip = "Drive this avatar's face tracking with your iPhone (iFacialMocap or Live Link Face) or the simulator, without Play Mode. " +
                           "The template, the quick settings and your base's corrective blendshapes are put together as at upload, in a second.";
            test.clicked += StartTest;
            actions.Add(test);
            actions.Add(VrcftLink());
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
            BuildSettings(frame);
        }

        // Why the template does not animate the marker's face where it is now (null when it does), and the face to point it at.
        private string faceIssue;
        private SkinnedMeshRenderer faceFound;

        private void CheckFace()
        {
            faceIssue = null; faceFound = null;
            var marker = report.Ours;
            if (marker == null || root == null) return;
            faceFound = FaceTrackingSetup.CurrentFace(marker, root.transform);
            if (faceFound == null) { faceIssue = "The template's face mesh is missing and no face was found on this avatar: face tracking won't move the face."; return; }
            if (faceFound != marker.face) { faceIssue = "The template's face mesh is missing. Uploads use “" + faceFound.name + "”, the face found on this avatar; point the template at it to keep them in step."; return; }
            string path = AnimationUtility.CalculateTransformPath(faceFound.transform, root.transform);
            string target = FaceTrackingSetup.BodyTarget(marker.gameObject);
            if (target != null && target != path)
                faceIssue = "“" + faceFound.name + "” moved or was renamed since face tracking was set up: the template still points at “" + target + "”. Uploads follow the face; point the template at it to keep them in step.";
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
        private Label smoothingCaption, budgetText, foldSummary, trackerCaption;
        private VisualElement foldBody, customPanel;
        private VectorIcon chevron;
        private readonly List<(FaceTrackingFeature feature, VisualElement row, ToggleSwitch toggle, Label bits)> featureRows = new List<(FaceTrackingFeature, VisualElement, ToggleSwitch, Label)>();
        private readonly List<(FaceTrackingPreset preset, Button tile, FaceTrackerLogo logo)> trackerTiles = new List<(FaceTrackingPreset, Button, FaceTrackerLogo)>();
        private ToggleSwitch expressive;
        private BudgetBar budget;
        private FaceTrackingFeatureSet.Costs costs;
        private IVisualElementScheduledItem budgetPending;
        private bool overBudget;
        // The face tracking's bits as the budget counts them (parameters the avatar shares with the template once); -1
        // until measured after a change, when the template's own count shows.
        private int faceBits = -1;

        private bool Open
        {
            get => Marker != null && SessionState.GetBool(OpenKey + Marker.GetInstanceID(), true);
            set { if (Marker != null) SessionState.SetBool(OpenKey + Marker.GetInstanceID(), value); }
        }

        // A fold at the bottom of the frame: the tracker first, every setting under Custom.
        private void BuildSettings(VisualElement frame)
        {
            var marker = Marker;
            if (marker == null) return;
            featureRows.Clear(); trackerTiles.Clear();
            costs = FaceTrackingFeatureSet.CostsOf(marker.gameObject);
            faceBits = -1;

            var fold = new VisualElement(); fold.AddToClassList("ft-fold"); frame.Add(fold);
            var header = new VisualElement(); header.AddToClassList("ft-fold__header"); fold.Add(header);
            chevron = new VectorIcon(IconGlyph.Chevron); chevron.AddToClassList("ft-fold__chevron"); header.Add(chevron);
            var title = new Label("Quick settings"); title.AddToClassList("ft-fold__title"); header.Add(title);
            foldSummary = new Label(); foldSummary.AddToClassList("ft-fold__summary"); header.Add(foldSummary);
            header.tooltip = "Applied when you upload, and live in the test.";
            // Opens on press; the change is only the fold's.
            header.RegisterCallback<PointerDownEvent>(e => { if (e.button != 0) return; Open = !Open; UpdateFold(); e.StopPropagation(); });
            foldBody = new VisualElement(); foldBody.AddToClassList("ft-fold__body"); fold.Add(foldBody);

            // The trackers
            var tiles = new VisualElement(); tiles.AddToClassList("ft-trackers"); foldBody.Add(tiles);
            var choices = FaceTrackingFeature.Trackers.Select(t => (t.preset, t.label, t.sub, tooltip: t.note + " " + costs.Total(t.features) + " bits."))
                .Append((FaceTrackingPreset.Custom, "Custom", "Your choice", "Choose each feature, the smoothing and the expressive mouth yourself."));
            foreach (var (preset, label, sub, tip) in choices)
            {
                var tile = MyAvatarEditor.Button("", () => Change("Face tracking tracker", m => FaceTrackingFeature.Choose(m, preset)));
                tile.AddToClassList("ft-tracker");
                tile.tooltip = tip;
                var logo = new FaceTrackerLogo(preset); logo.AddToClassList("ft-tracker__logo"); tile.Add(logo);
                var name = new Label(label); name.AddToClassList("ft-tracker__name"); name.pickingMode = PickingMode.Ignore; tile.Add(name);
                var detail = new Label(sub); detail.AddToClassList("ft-tracker__sub"); detail.pickingMode = PickingMode.Ignore; tile.Add(detail);
                tiles.Add(tile);
                trackerTiles.Add((preset, tile, logo));
            }
            trackerCaption = new Label(); trackerCaption.AddToClassList("ft-trackers__caption"); foldBody.Add(trackerCaption);

            customPanel = new VisualElement(); customPanel.AddToClassList("ft-custom"); foldBody.Add(customPanel);

            // Smoothing
            var smoothRow = Setting(customPanel, "Smoothing", null, true);
            smoothing = new SegmentedControl(new[]
            {
                new SegmentedControl.Option("Responsive", null, "Local smoothing 0: values follow at once."),
                new SegmentedControl.Option("Balanced", null, "Local smoothing 0.25 (the template's), the mouth 2.35× slower."),
                new SegmentedControl.Option("Smooth", null, "Local smoothing 0.6, the mouth 2.35× slower."),
            }, index => Change("Face tracking smoothing", m => m.smoothing = (FaceTrackingSmoothing)index));
            smoothRow.Add(smoothing);
            smoothingCaption = new Label(); smoothingCaption.AddToClassList("ft-setting__caption"); smoothRow.Add(smoothingCaption);

            // Features others see
            var featureSetting = Setting(customPanel, "Others see", out _);
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
            var mouth = Setting(customPanel, "Expressive mouth", out _);
            var mouthRow = new VisualElement(); mouthRow.AddToClassList("ft-switch-row"); mouth.Add(mouthRow);
            bool grin = marker.face != null && marker.face.sharedMesh != null && marker.face.sharedMesh.GetBlendShapeIndex("Grin_L") >= 0;
            var mouthText = new Label("Smiles and frowns reach full at 80 %, as VRCFaceTracking seldom sends more, and the mouth follows a little slower than the eyes. " +
                                      (grin ? "A smile with the lip raised becomes this face's own grin." : "A smile with the lip raised becomes a grin that lifts the cheeks and lower lids."));
            mouthText.AddToClassList("ft-feature__detail"); mouthText.AddToClassList("ft-switch-row__text");
            mouthRow.Add(mouthText);
            expressive = new ToggleSwitch(marker.expressiveMouth, on => Change("Face tracking expressive mouth", m => m.expressiveMouth = on));
            mouthRow.Add(expressive);
            UpdateSettings();
        }

        private void UpdateFold()
        {
            if (foldBody == null) return;
            bool open = Open;
            foldBody.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            chevron.EnableInClassList("ft-fold__chevron--open", open);
        }

        private VisualElement Setting(VisualElement parent, string title, string aside, bool first = false)
        {
            var setting = Setting(parent, title, out var header);
            if (first) setting.AddToClassList("ft-setting--first");
            if (aside != null) { var label = new Label(aside); label.AddToClassList("ft-setting__aside"); header.Add(label); }
            return setting;
        }

        private VisualElement Setting(VisualElement parent, string title, out VisualElement header)
        {
            var setting = new VisualElement(); setting.AddToClassList("ft-setting"); parent.Add(setting);
            header = new VisualElement(); header.AddToClassList("ft-setting__header"); setting.Add(header);
            var label = new Label(title); label.AddToClassList("ft-setting__title"); header.Add(label);
            return setting;
        }

        // One Undo step on the marker; the controls show the change at once, the budget and the live test follow.
        private void Change(string undo, System.Action<MyAvatarFaceTracking> change)
        {
            var marker = Marker;
            if (marker == null) return;
            Undo.RecordObject(marker, undo);
            change(marker);
            EditorUtility.SetDirty(marker);
            PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
            faceBits = -1;
            UpdateSettings(budgetNow: false);
            budgetPending?.Pause();
            budgetPending = schedule.Execute(() => { UpdateBudget(); Test?.Apply(Marker); }).StartingIn(60);
        }

        private void UpdateSettings() => UpdateSettings(true);

        private void UpdateSettings(bool budgetNow)
        {
            var marker = Marker;
            if (marker == null || smoothing == null) return;
            UpdateFold();
            foreach (var (preset, tile, logo) in trackerTiles)
            {
                bool on = marker.preset == preset;
                tile.EnableInClassList("ft-tracker--on", on);
                logo.On = on;
            }
            customPanel.style.display = marker.preset == FaceTrackingPreset.Custom ? DisplayStyle.Flex : DisplayStyle.None;
            smoothing.SetIndex((int)marker.smoothing);
            smoothingCaption.text = SmoothingCaptions[(int)marker.smoothing];
            var effective = FaceTrackingFeatureSet.Effective(marker.synced);
            foreach (var (feature, row, toggle, bits) in featureRows)
            {
                toggle.SetValueWithoutNotify((marker.synced & feature.Flag) != 0);
                bool parentOn = feature.Parent == FaceTrackingFeatures.None || (effective & feature.Parent) != 0;
                row.SetEnabled(parentOn);
                bits.EnableInClassList("ft-bits--off", (effective & feature.Flag) == 0);
            }
            expressive.SetValueWithoutNotify(marker.expressiveMouth);
            UpdateCaption();
            if (budgetNow) UpdateBudget();
        }

        private void UpdateCaption()
        {
            var marker = Marker;
            if (marker == null || trackerCaption == null) return;
            int bits = faceBits >= 0 ? faceBits : costs.Total(marker.synced);
            var tracker = FaceTrackingFeature.Trackers.FirstOrDefault(t => t.preset == marker.preset);
            string name = tracker.label != null ? tracker.label : marker.preset == FaceTrackingPreset.Custom ? "Custom" : "No tracker chosen";
            string text = marker.preset == FaceTrackingPreset.Everything
                ? (costs.Total(marker.synced) == costs.All ? "Every feature of the template is synced (" + bits + " bits)." : bits + " of the template's " + costs.All + " bits are synced.") +
                  " Pick your tracker to leave out what it can't send."
                : marker.preset == FaceTrackingPreset.Custom ? "Your own choice of features: " + bits + " of " + costs.All + " bits."
                : tracker.note + " " + bits + " of " + costs.All + " bits.";
            if (overBudget) text += " Over VRChat's synced parameter limit: choose Custom to leave out more.";
            trackerCaption.text = text;
            trackerCaption.EnableInClassList("warning", overBudget);
            foldSummary.text = name + " · " + bits + " bits";
        }

        // VRChat's 256 synced bits: this avatar's own, the face tracking it keeps, and what is left.
        private void UpdateBudget()
        {
            var marker = Marker;
            if (marker == null || budget == null || root == null) return;
            var estimate = AvatarParameterBudget.Estimate(root);
            int face = estimate.FaceTrackingBits, all = estimate.TotalBeforeCompression, own = Mathf.Max(0, all - face);
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
            overBudget = estimate.OverBudget;
            faceBits = face;
            UpdateCaption();
        }
    }
}
