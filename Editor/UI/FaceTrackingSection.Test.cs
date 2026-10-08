using System;
using System.Linq;
using Orbiters.MyAvatar.Editor.FaceTracking;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // The live test: the built avatar's face on a stage, the link with the phone (or the simulator), and the values
    // VRCFaceTracking sends.
    internal sealed partial class FaceTrackingSection
    {
        private static readonly (FaceApp app, string label, string tooltip)[] Sources =
        {
            (FaceApp.IFacialMocap, "iFacialMocap", "VRCFaceTracking's iFacialMocap module: UDP port 49983."),
            (FaceApp.LiveLink, "Live Link Face", "VRCFaceTracking's Live Link module: UDP port 11111."),
            (FaceApp.Simulator, "Simulator", "A looping performance, no phone needed."),
        };

        private static readonly (string title, (string label, string v2, bool signed)[] meters)[] MeterGroups =
        {
            ("EYES", new[] { ("Lid L", "EyeLidLeft", false), ("Lid R", "EyeLidRight", false), ("Look L/R", "EyeLeftX", true), ("Look U/D", "EyeY", true), ("Squint", "EyeSquintLeft", false) }),
            ("MOUTH", new[] { ("Jaw", "JawOpen", false), ("Smile L", "SmileSadLeft", true), ("Smile R", "SmileSadRight", true), ("Pucker", "LipPucker", false), ("Funnel", "LipFunnel", false) }),
            ("LIPS", new[] { ("Upper up", "MouthUpperUpLeft", false), ("Lower down", "MouthLowerDown", false), ("Side", "MouthX", true), ("Jaw side", "JawX", true), ("Closed", "MouthClosed", false) }),
            ("TONGUE & CHEEKS", new[] { ("Tongue", "TongueOut", false), ("Tongue X", "TongueX", true), ("Puff L", "CheekPuffSuckLeft", true), ("Puff R", "CheekPuffSuckRight", true) }),
        };

        private VisualElement stage, overlay, overlayCard, connectBody, notice;
        private Image image;
        private Label statusTag, rateTag, overlayTitle, overlayText, statusText, hint, noticeText, sourceAside, stageHint;
        private double liveSince = -1;
        private VisualElement statusDot, statusRowDot, progressFill, progressShimmer, pulse;
        private SegmentedControl sourceControl;
        private Button overlayAction;
        private TextField phoneField;
        private FaceApp connectFor = FaceApp.None;
        private IVisualElementScheduledItem ticker;
        private readonly System.Collections.Generic.List<FaceTrackingMeter> meters = new System.Collections.Generic.List<FaceTrackingMeter>();
        private Vector2 dragFrom;
        private int dragPointer = -1;

        private void StartTest()
        {
            var marker = Marker;
            if (marker == null || root == null) return;
            // The stage shows at once; the copy of the avatar is made on the next frame.
            BuildLive(starting: true);
            schedule.Execute(() =>
            {
                FaceTrackingTest.Start(avatar, root, marker, this);
                ShowTest();
            });
        }

        private void TestChanged() => ShowTest();

        private void Repaint()
        {
            image?.MarkDirtyRepaint();
            var test = Test;
            if (test != null && stage != null && meters.Count > 0) UpdateMeters(test);
        }

        private void ShowTest()
        {
            var test = Test;
            bool showing = test != null && Marker != null;
            ShowHeroTop(!showing);
            if (!showing)
            {
                if (stage != null) { live.Clear(); stage = null; meters.Clear(); ticker?.Pause(); connectFor = FaceApp.None; }
                return;
            }
            if (stage == null) BuildLive(starting: false);
            Tick();
        }

        // ---- Building the panel ----

        private void BuildLive(bool starting)
        {
            live.Clear(); meters.Clear(); connectFor = FaceApp.None;
            ShowHeroTop(false);
            stage = new VisualElement(); stage.AddToClassList("ft-stage"); live.Add(stage);
            // Several sections may show the test at different sizes: the image fills each one without stretching.
            image = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Position };
            image.AddToClassList("ft-stage__image");
            stage.Add(image);
            stage.RegisterCallback<GeometryChangedEvent>(_ => Resize());
            Orbit(image);
            stageHint = new Label("Drag to turn · scroll to zoom · double-click to reset"); stageHint.AddToClassList("ft-stage__hint"); stageHint.pickingMode = PickingMode.Ignore; stage.Add(stageHint);
            var status = Tag("ft-tag--status", out statusDot, out statusTag); stage.Add(status);
            var rate = Tag("ft-tag--rate", out _, out rateTag); rate.Q(className: "ft-dot").style.display = DisplayStyle.None; stage.Add(rate);
            notice = new VisualElement(); notice.AddToClassList("ft-stage__notice"); stage.Add(notice);
            noticeText = new Label(); noticeText.AddToClassList("ft-stage__notice-text"); notice.Add(noticeText);
            var rebuild = MyAvatarEditor.Button("Rebuild", () => Test?.Rebuild());
            rebuild.AddToClassList("ft-stage__notice-button");
            notice.Add(rebuild);
            notice.style.display = DisplayStyle.None;
            var stop = MyAvatarEditor.Button("Stop test", () => { FaceTrackingTest.StopAll(); });
            stop.AddToClassList("ft-stage__stop");
            stop.tooltip = "Stop listening to the phone. The face stays ready for ten minutes, so testing again starts at once.";
            stage.Add(stop);

            overlay = new VisualElement(); overlay.AddToClassList("ft-overlay"); stage.Add(overlay);
            pulse = new FaceTrackingPulse(); pulse.AddToClassList("ft-overlay__pulse"); overlay.Add(pulse);
            overlayCard = new VisualElement(); overlayCard.AddToClassList("ft-overlay__card"); overlay.Add(overlayCard);
            overlayTitle = new Label(); overlayTitle.AddToClassList("ft-overlay__title"); overlayCard.Add(overlayTitle);
            overlayText = new Label(); overlayText.AddToClassList("ft-overlay__text"); overlayCard.Add(overlayText);
            var progress = new VisualElement(); progress.AddToClassList("ft-progress"); overlayCard.Add(progress);
            progressFill = new VisualElement(); progressFill.AddToClassList("ft-progress__fill"); progress.Add(progressFill);
            progressShimmer = new VisualElement(); progressShimmer.AddToClassList("ft-progress__shimmer"); progress.Add(progressShimmer);
            var actions = new VisualElement(); actions.AddToClassList("ft-overlay__actions"); overlayCard.Add(actions);
            overlayAction = MyAvatarEditor.Button("Try again", () => Test?.Rebuild());
            overlayAction.AddToClassList("ft-ghost");
            actions.Add(overlayAction);
            if (starting) { overlayTitle.text = "Starting the test…"; overlayText.text = "Copying the avatar."; overlayAction.style.display = DisplayStyle.None; progressFill.style.width = Length.Percent(2f); }

            // The link with the phone.
            var connect = new VisualElement(); connect.AddToClassList("ft-card"); live.Add(connect);
            var header = new VisualElement(); header.AddToClassList("ft-card__header"); connect.Add(header);
            var title = new Label("Face source"); title.AddToClassList("ft-card__title"); header.Add(title);
            sourceAside = new Label("Same Wi-Fi as this PC"); sourceAside.AddToClassList("ft-card__aside"); header.Add(sourceAside);
            sourceControl = new SegmentedControl(Sources.Select(s => new SegmentedControl.Option(s.label, s.app == FaceApp.Simulator ? IconGlyph.Sparkle : (IconGlyph?)null, s.tooltip)),
                index => { Test?.SetSource(Sources[index].app); BuildConnect(); });
            connect.Add(sourceControl);
            connectBody = new VisualElement(); connect.Add(connectBody);
            var statusRow = new VisualElement(); statusRow.AddToClassList("ft-status"); connect.Add(statusRow);
            statusRowDot = new VisualElement(); statusRowDot.AddToClassList("ft-dot"); statusRow.Add(statusRowDot);
            statusText = new Label(); statusText.AddToClassList("ft-status__text"); statusRow.Add(statusText);
            hint = new Label(); hint.AddToClassList("ft-hint"); hint.style.display = DisplayStyle.None; connect.Add(hint);

            // What VRChat receives.
            var values = new VisualElement(); values.AddToClassList("ft-card"); live.Add(values);
            var valuesHeader = new VisualElement(); valuesHeader.AddToClassList("ft-card__header"); values.Add(valuesHeader);
            var valuesTitle = new Label("What VRChat receives"); valuesTitle.AddToClassList("ft-card__title"); valuesHeader.Add(valuesTitle);
            var valuesAside = new Label("VRCFaceTracking’s v2 parameters"); valuesAside.AddToClassList("ft-card__aside"); valuesHeader.Add(valuesAside);
            valuesAside.tooltip = "Computed like VRCFaceTracking from the phone's data. Faded: not on this avatar, or a feature others don't see.";
            var grid = new VisualElement(); grid.AddToClassList("ft-meters"); values.Add(grid);
            foreach (var (groupTitle, rows) in MeterGroups)
            {
                var group = new VisualElement(); group.AddToClassList("ft-meters__group"); grid.Add(group);
                var label = new Label(groupTitle); label.AddToClassList("ft-meters__title"); group.Add(label);
                foreach (var (name, v2, signed) in rows) { var meter = new FaceTrackingMeter(name, v2, signed); group.Add(meter); meters.Add(meter); }
            }
            ticker?.Pause();
            ticker = schedule.Execute(Tick).Every(100);
            BuildConnect();
        }

        private static VisualElement Tag(string position, out VisualElement dot, out Label text)
        {
            var tag = new VisualElement(); tag.AddToClassList("ft-tag"); tag.AddToClassList(position); tag.pickingMode = PickingMode.Ignore;
            dot = new VisualElement(); dot.AddToClassList("ft-dot"); tag.Add(dot);
            text = new Label(); text.AddToClassList("ft-tag__text"); tag.Add(text);
            return tag;
        }

        private void Resize()
        {
            var test = Test;
            if (test == null || stage == null) return;
            float scale = EditorGUIUtility.pixelsPerPoint;
            test.Width = Mathf.Max(8, Mathf.RoundToInt(stage.layout.width * scale));
            test.Height = Mathf.Max(8, Mathf.RoundToInt(stage.layout.height * scale));
        }

        // Drag turns the camera around the face, the wheel zooms, a double-click frames it again.
        private void Orbit(VisualElement target)
        {
            target.RegisterCallback<PointerDownEvent>(evt =>
            {
                var stageView = Test?.Stage;
                if (stageView == null || evt.button != 0) return;
                if (evt.clickCount == 2) { stageView.Yaw = 0f; stageView.Pitch = 3f; stageView.Zoom = 1f; return; }
                dragPointer = evt.pointerId; dragFrom = evt.position;
                if (stageHint != null) stageHint.style.display = DisplayStyle.None;
                target.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            });
            target.RegisterCallback<PointerMoveEvent>(evt =>
            {
                var stageView = Test?.Stage;
                if (stageView == null || evt.pointerId != dragPointer || !target.HasPointerCapture(evt.pointerId)) return;
                var delta = (Vector2)evt.position - dragFrom;
                dragFrom = evt.position;
                stageView.Yaw = Mathf.Clamp(stageView.Yaw - delta.x * .4f, -FaceTrackingStage.MaxYaw, FaceTrackingStage.MaxYaw);
                stageView.Pitch = Mathf.Clamp(stageView.Pitch + delta.y * .25f, -FaceTrackingStage.MaxPitch, FaceTrackingStage.MaxPitch);
            });
            void End(int pointer) { if (pointer == dragPointer && target.HasPointerCapture(pointer)) target.ReleasePointer(pointer); dragPointer = -1; }
            target.RegisterCallback<PointerUpEvent>(evt => End(evt.pointerId));
            target.RegisterCallback<PointerCaptureOutEvent>(evt => dragPointer = -1);
            target.RegisterCallback<WheelEvent>(evt =>
            {
                var stageView = Test?.Stage;
                if (stageView == null) return;
                stageView.Zoom = Mathf.Clamp(stageView.Zoom * (1f - evt.delta.y * .04f), FaceTrackingStage.MinZoom, FaceTrackingStage.MaxZoom);
                evt.StopPropagation();
            });
        }

        // What to do on the phone, for the chosen app.
        private void BuildConnect()
        {
            var test = Test;
            var source = test != null ? test.Source : (FaceApp)EditorPrefs.GetInt("Orbiters.MyAvatar.FaceTracking.Source", (int)FaceApp.IFacialMocap);
            if (source == connectFor || connectBody == null) return;
            connectFor = source;
            connectBody.Clear();
            sourceControl.SetIndex(Array.FindIndex(Sources, s => s.app == source));
            sourceAside.text = source == FaceApp.Simulator ? "No phone needed" : "Same Wi-Fi as this PC";
            if (source == FaceApp.Simulator)
            {
                Step(connectBody, 1, "A looping performance (smiles, talking, glances, a pout, the tongue, a wink) goes through VRCFaceTracking’s Live Link mapping, the parameters and the avatar’s FX, exactly like a phone’s frames.");
                return;
            }
            var addresses = FaceTrackingProtocols.LocalAddresses();
            int port = source == FaceApp.IFacialMocap ? FaceTrackingProtocols.IFacialMocapPort : FaceTrackingProtocols.LiveLinkPort;
            string ip = addresses.Count > 0 ? addresses[0].address.ToString() : "no network";
            if (source == FaceApp.IFacialMocap)
            {
                Step(connectBody, 1, "Open iFacialMocap on your iPhone, on the same Wi-Fi as this PC.");
                var two = Step(connectBody, 2, "Type the address iFacialMocap shows at the top of its screen:");
                var row = new VisualElement(); row.AddToClassList("ft-phone"); two.Add(row);
                phoneField = new TextField { value = FaceTrackingTest.PhoneAddress, isDelayed = false };
                phoneField.AddToClassList("ft-phone__field");
                phoneField.tooltip = "The iPhone's address on the Wi-Fi, e.g. 192.168.1.42. Remembered for next time.";
                row.Add(phoneField);
                var link = MyAvatarEditor.Button("Connect", null);
                link.AddToClassList("ft-copy");
                link.clicked += () => Connect(link);
                phoneField.RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) Connect(link); });
                row.Add(link);
                Step(connectBody, 3, "Or, in iFacialMocap’s settings, send to this PC instead:");
            }
            else
            {
                Step(connectBody, 1, "In Live Link Face, choose Live Link (ARKit), then open Settings › Live Link and add a target:");
                Step(connectBody, 2, "Enter this PC’s address and port " + port + ", then keep the app open on your face.");
            }
            var address = new VisualElement(); address.AddToClassList("ft-address"); connectBody.Add(address);
            var texts = new VisualElement(); texts.AddToClassList("ft-address__texts"); address.Add(texts);
            var label = new Label("THIS PC"); label.AddToClassList("ft-address__label"); texts.Add(label);
            var line = new VisualElement(); line.style.flexDirection = FlexDirection.Row; line.style.alignItems = Align.FlexEnd; texts.Add(line);
            var value = new Label(ip); value.AddToClassList("ft-address__value"); line.Add(value);
            var portLabel = new Label("port " + port); portLabel.AddToClassList("ft-address__port"); line.Add(portLabel);
            var more = addresses.Skip(1).Where(a => !FaceTrackingProtocols.IsVirtual(a.adapter)).Take(3).ToList();
            if (more.Count > 0)
            {
                var others = new Label("Also: " + string.Join(", ", more.Select(a => a.address + " (" + a.adapter + ")")));
                others.AddToClassList("ft-address__others"); texts.Add(others);
            }
            address.Add(Copy("Copy address", ip));
            address.Add(Copy("Copy port", port.ToString()));
        }

        private static VisualElement Step(VisualElement parent, int number, string text)
        {
            var step = new VisualElement(); step.AddToClassList("ft-step"); parent.Add(step);
            var index = new Label(number.ToString()); index.AddToClassList("ft-step__number"); step.Add(index);
            var body = new VisualElement(); body.AddToClassList("ft-step__body"); step.Add(body);
            var label = new Label(text); label.AddToClassList("ft-step__text"); body.Add(label);
            return body;
        }

        private Button Copy(string label, string text)
        {
            var button = MyAvatarEditor.Button(label, null);
            button.AddToClassList("ft-copy");
            button.clicked += () =>
            {
                EditorGUIUtility.systemCopyBuffer = text;
                button.text = "Copied";
                button.AddToClassList("ft-copy--done");
                button.schedule.Execute(() => { button.text = label; button.RemoveFromClassList("ft-copy--done"); }).StartingIn(1400);
            };
            return button;
        }

        private void Connect(Button button)
        {
            var test = Test;
            if (test == null || phoneField == null) return;
            bool ok = test.SetPhone(phoneField.value);
            phoneField.EnableInClassList("invalid", !ok);
            if (!ok) { statusText.text = "“" + phoneField.value + "” is not an address like 192.168.1.42."; return; }
            button.text = "Asking…";
            button.schedule.Execute(() => button.text = "Connect").StartingIn(1600);
        }

        // ---- Live state, ten times a second ----

        private void Tick()
        {
            var test = Test;
            if (test == null || stage == null) return;
            if (image.image != test.Stage?.Texture) image.image = test.Stage?.Texture;
            if (test.Source != connectFor) BuildConnect();
            var receiver = test.Receiver;
            double age = receiver != null ? receiver.Age : double.PositiveInfinity;
            bool simulated = test.Source == FaceApp.Simulator;
            string appName = Sources.First(s => s.app == test.Source).label;

            // Stage tags and overlay.
            overlay.style.display = test.State == FaceTrackingTest.Phase.Live ? DisplayStyle.None : DisplayStyle.Flex;
            overlay.EnableInClassList("ft-overlay--solid", test.State == FaceTrackingTest.Phase.Building || test.Stage == null);
            pulse.style.display = test.State == FaceTrackingTest.Phase.Waiting ? DisplayStyle.Flex : DisplayStyle.None;
            progressFill.parent.style.display = test.State == FaceTrackingTest.Phase.Building ? DisplayStyle.Flex : DisplayStyle.None;
            overlayAction.style.display = test.State == FaceTrackingTest.Phase.Failed ? DisplayStyle.Flex : DisplayStyle.None;
            overlayText.EnableInClassList("warning", test.State == FaceTrackingTest.Phase.Failed);
            statusDot.EnableInClassList("ft-dot--live", test.State == FaceTrackingTest.Phase.Live && !simulated);
            statusDot.EnableInClassList("ft-dot--sim", test.State == FaceTrackingTest.Phase.Live && simulated);
            statusDot.EnableInClassList("ft-dot--error", test.State == FaceTrackingTest.Phase.Failed);
            // The live dot breathes.
            statusDot.EnableInClassList("ft-dot--dim", test.State == FaceTrackingTest.Phase.Live && (int)(EditorApplication.timeSinceStartup * 1.25) % 2 == 1);
            switch (test.State)
            {
                case FaceTrackingTest.Phase.Building:
                    var build = test.Build;
                    statusTag.text = "PREPARING";
                    overlayTitle.text = "Preparing the face";
                    overlayText.text = (build != null ? build.Step : "Copying the avatar") + "…";
                    progressFill.style.width = Length.Percent(Mathf.Max(2f, (build != null ? build.Progress : 0f) * 100f));
                    float sweep = (float)(EditorApplication.timeSinceStartup % 1.6 / 1.6);
                    progressShimmer.style.left = Length.Percent(-30f + sweep * 130f);
                    rateTag.text = "";
                    break;
                case FaceTrackingTest.Phase.Waiting:
                    statusTag.text = "WAITING · " + appName.ToUpperInvariant();
                    overlayTitle.text = simulated ? "Starting the simulator…" : "Waiting for " + appName;
                    overlayText.text = simulated ? "" : test.Source == FaceApp.IFacialMocap && !string.IsNullOrEmpty(FaceTrackingTest.PhoneAddress)
                        ? "Asking " + FaceTrackingTest.PhoneAddress + " for frames. Keep iFacialMocap open on your face."
                        : "Follow the steps below; the face moves as soon as the first frame arrives.";
                    rateTag.text = "";
                    break;
                case FaceTrackingTest.Phase.Live:
                    statusTag.text = simulated ? "SIMULATOR · " + test.SimulatorBeat.ToUpperInvariant() : "LIVE · " + appName.ToUpperInvariant();
                    rateTag.text = (simulated ? "" : receiver.Fps.ToString("0") + " in · ") + test.ShownFps + " fps" + (simulated ? "" : " · " + (age < 10 ? Math.Round(age * 1000) + " ms" : "paused"));
                    rateTag.parent.tooltip = UnityEditorInternal.InternalEditorUtility.isApplicationActive
                        ? "Frames the phone sends each second, the images of the face drawn, and the age of the last frame. Every frame shows: the face plays a moment behind, between the frames around it."
                        : "Unity draws less often while another app is in front: click into Unity for the full frame rate.";
                    break;
                case FaceTrackingTest.Phase.Failed:
                    statusTag.text = "STOPPED";
                    overlayTitle.text = "The test could not start";
                    overlayText.text = test.Error;
                    rateTag.text = "";
                    break;
            }
            bool stale = test.State != FaceTrackingTest.Phase.Building && test.Stale;
            notice.style.display = stale ? DisplayStyle.Flex : DisplayStyle.None;
            // The camera hint shows for the first seconds of the face, out of the way of a notice.
            if (test.State == FaceTrackingTest.Phase.Live && liveSince < 0) liveSince = EditorApplication.timeSinceStartup;
            if (stageHint.resolvedStyle.display != DisplayStyle.None && (stale || (liveSince >= 0 && EditorApplication.timeSinceStartup - liveSince > 6)))
                stageHint.style.display = DisplayStyle.None;
            noticeText.text = "Avatar changed since the test started";

            // The phone link's status.
            string error = receiver != null ? receiver.Error(test.Source) : null;
            statusRowDot.EnableInClassList("ft-dot--live", test.HasFace && !simulated && age < 1);
            statusRowDot.EnableInClassList("ft-dot--sim", simulated);
            statusRowDot.EnableInClassList("ft-dot--error", error != null);
            if (test.State == FaceTrackingTest.Phase.Building) statusText.text = simulated ? "The simulator starts in a moment." : "Listening already: start the app.";
            else if (simulated) statusText.text = "Simulating: " + test.SimulatorBeat + " (the loop lasts " + Mathf.RoundToInt(FaceTrackingSimulator.Length) + " s).";
            else if (error != null) statusText.text = error;
            else if (test.HasFace && age < 1) statusText.text = "Receiving from " + receiver.Device + " · " + receiver.Fps.ToString("0") + " frames per second · last " + Math.Round(age * 1000) + " ms ago.";
            else if (test.HasFace) statusText.text = "No frames for " + age.ToString("0") + " s: the app paused or the phone left the Wi-Fi. VRCFaceTracking keeps the last face meanwhile.";
            else statusText.text = test.Source == FaceApp.IFacialMocap
                ? "Listening on port " + FaceTrackingProtocols.IFacialMocapPort + (string.IsNullOrEmpty(FaceTrackingTest.PhoneAddress) ? "." : " and asking " + FaceTrackingTest.PhoneAddress + " for frames.")
                : "Listening on port " + FaceTrackingProtocols.LiveLinkPort + " for Live Link Face.";
            // Nothing after ten seconds: most often the firewall.
            bool quiet = !simulated && !test.HasFace && error == null && receiver != null && receiver.Running && receiver.Now - receiver.Started > 10;
            hint.style.display = quiet ? DisplayStyle.Flex : DisplayStyle.None;
            if (quiet) hint.text = "Nothing arrives yet? Check the phone is on the same Wi-Fi (not a guest network), and that Windows Firewall lets Unity receive on private networks: " +
                                   "Windows Security › Firewall & network protection › Allow an app through firewall › Unity " + Application.unityVersion + " › Private. Close VRCFaceTracking while testing here.";
            UpdateMeters(test);
        }

        // What VRChat receives: every rendered frame of the face, not only the ten-times-a-second status.
        private void UpdateMeters(FaceTrackingTest test)
        {
            var effective = FaceTrackingFeatureSet.Effective(test.Features);
            foreach (var meter in meters)
            {
                var function = Vrcft.Parameter(meter.V2, out _);
                float value = test.HasFace && function != null ? function(test.Face) : meter.V2.StartsWith("EyeLid") ? .75f : 0f;
                var feature = FaceTrackingFeatureSet.FeatureOf("v2/" + meter.V2);
                bool on = test.Drives(meter.V2) && (effective & feature) != 0;
                meter.Set(value, on);
            }
        }
    }
}
