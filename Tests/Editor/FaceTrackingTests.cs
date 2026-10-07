using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.FaceTracking;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class FaceTrackingTests
    {
        // Rexouium names its face tracking shapes its own way: SRanipal's lip set as "LipTrack_*", eyes as "Eye_LookUp_L".
        private static readonly string[] Rexouium =
        {
            "Blink_L", "Blink_R", "EyesWide_L", "EyesWide_R", "Eye_Squeeze_L", "Eye_Squeeze_R", "Eye_LookUp_L", "Eye_LookUp_R",
            "Eye_LookDown_L", "Eye_LookDown_R", "Eye_LookLeft_L", "Eye_LookLeft_R", "Eye_LookRight_L", "Eye_LookRight_R",
            "LipTrack_JawOpen", "LipTrack_JawFoward", "LipTrack_JawLeft", "LipTrack_JawRight", "LipTrack_UpperLipLeft", "LipTrack_UpperLipRight",
            "LipTrack_LowerLipLeft", "LipTrack_LowerLipRight", "LipTrack_UpperLipRollOut", "LipTrack_LowerLipRollOut", "LipTrack_Pout_Pucker",
            "LipTrack_LipSmileLeft", "LipTrack_LipSmileRight", "LipTrack_LipFrownLeft", "LipTrack_LipFrownRight", "LipTrack_CheekPuff_L",
            "LipTrack_CheekPuff_R", "LipTrack_CheeksIn", "LipTrack_LipCornerTopUp_L", "LipTrack_LipCornerTopUp_R", "LipTrack_LipCornerLowDown_L",
            "LipTrack_LipCornerLowDown_R", "LipTrack_UpperLipIn", "LipTrack_LowerLipIn", "LipTrack_LowerLipUp", "LipTrack_TongueExtend",
            "LipTrack_TongueUp", "LipTrack_TongueDown", "LipTrack_TongueLeft", "LipTrack_TongueRight", "LipTrack_Tongue_Roll",
            "Smile_L", "Smile_R", "Blink", "Angry",
        };

        private readonly List<Object> owned = new List<Object>();

        [TearDown]
        public void Clean()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
        }

        [Test]
        public void AFaceNamedItsOwnWayFitsTheStandardWhoseShapesItHas()
        {
            var root = new GameObject("Avatar"); owned.Add(root);
            var body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
            body.transform.SetParent(root.transform, false);
            body.sharedMesh = Mesh(Rexouium);

            var report = FaceTrackingDetection.Inspect(root.transform);

            Assert.AreSame(body, report.Face);
            Assert.AreEqual(FaceTrackingStandard.SRanipal, report.Standard);
            Assert.True(report.Ready);
            Assert.False(report.SetUp);
            CollectionAssert.AreEqual(new[] { "Mouth_Ape_Shape" }, report.Missing);
            var index = FaceTrackingNames.Index(Rexouium);
            Assert.AreEqual("LipTrack_JawFoward", FaceTrackingNames.Resolve("Jaw_Forward", index));
            Assert.AreEqual("Eye_LookRight_L", FaceTrackingNames.Resolve("Eye_Left_Right", index), "the left eye looking right looks in");
            Assert.AreEqual("LipTrack_LipSmileLeft", FaceTrackingNames.Resolve("Mouth_Smile_Left", index), "the lip tracking smile, not the expression");
            Assert.AreEqual("Blink_L", FaceTrackingNames.Resolve("EyeClosedLeft", index));
            Assert.IsNull(FaceTrackingNames.Resolve("MouthStretchLeft", index));
        }

        [Test]
        public void AFaceWithoutTrackingShapesIsNotOffered()
        {
            var root = new GameObject("Avatar"); owned.Add(root);
            var body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
            body.transform.SetParent(root.transform, false);
            body.sharedMesh = Mesh(new[] { "Smile", "Angry", "Blink", "vrc.v_aa" });
            Assert.False(FaceTrackingDetection.Inspect(root.transform).Ready);
        }

        [Test]
        public void TheBuildAnimatesTheFacesOwnShapesAndHoldsGesturesWhileTracking()
        {
            var face = Mesh(Rexouium);
            var fx = new AnimatorController(); owned.Add(fx);
            foreach (var name in new[] { "GestureLeft", "FT/v2/JawOpen" }) fx.AddParameter(name, name == "GestureLeft" ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Float);

            // The template's layer: a direct blend tree on VRCFaceTracking's parameter, animating SRanipal's names.
            var template = Layer(fx, "Face Tracking");
            var tree = new BlendTree { blendType = BlendTreeType.Direct }; owned.Add(tree);
            var jaw = Clip(("Jaw_Open", 100f), ("LipTrack_JawOpen", 30f));
            var smiling = Clip(("Mouth_Smile_Left", 100f));
            tree.children = new[]
            {
                new ChildMotion { motion = jaw, directBlendParameter = "FT/v2/JawOpen", timeScale = 1 },
                new ChildMotion { motion = smiling, directBlendParameter = "FT/v2/JawOpen", timeScale = 1 },
            };
            template.AddState("Tracking").motion = tree;

            // The avatar's own expressions on hand gestures, and the drawing pen's gesture layer that never touches the face.
            var expressions = Layer(fx, "Expressions");
            var rest = expressions.AddState("Rest"); rest.motion = Clip(("LipTrack_LipSmileLeft", 0f));
            var smile = expressions.AddState("Smile"); smile.motion = Clip(("LipTrack_LipSmileLeft", 100f));
            var toSmile = rest.AddTransition(smile); toSmile.AddCondition(AnimatorConditionMode.Equals, 3, "GestureLeft");
            expressions.defaultState = rest;
            // Ear poses on gestures move the face mesh too, but no shape face tracking drives.
            var ears = Layer(fx, "Ears");
            var earsRest = ears.AddState("Rest"); earsRest.motion = Clip(("EarsBack", 0f));
            var earsBack = ears.AddState("Back"); earsBack.motion = Clip(("EarsBack", 100f));
            earsRest.AddTransition(earsBack).AddCondition(AnimatorConditionMode.Equals, 2, "GestureLeft");
            ears.defaultState = earsRest;
            var pen = Layer(fx, "Pen");
            var idle = pen.AddState("Idle"); var draw = pen.AddState("Draw");
            idle.AddTransition(draw).AddCondition(AnimatorConditionMode.Equals, 1, "GestureLeft");

            var (renamed, gated) = FaceTrackingBuild.Finish(fx, FaceTrackingStandard.SRanipal, "Body", face);

            Assert.AreEqual(2, renamed);
            Assert.AreEqual(1, gated, "only the expressions moving shapes face tracking drives");
            var shapes = AnimationUtility.GetCurveBindings(jaw).Select(b => b.propertyName).ToList();
            CollectionAssert.AreEquivalent(new[] { "blendShape.LipTrack_JawOpen" }, shapes, "the template's curve moves onto the face's own shape");
            Assert.AreEqual(30f, AnimationUtility.GetEditorCurve(jaw, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.LipTrack_JawOpen")).keys[0].value,
                "the face's own animation of that shape wins");
            Assert.True(toSmile.conditions.Any(c => c.parameter == FaceTrackingBuild.ExpressionsDisabled && c.mode == AnimatorConditionMode.IfNot));
            var back = expressions.anyStateTransitions.Single();
            Assert.AreSame(rest, back.destinationState);
            Assert.True(back.conditions.Any(c => c.parameter == FaceTrackingBuild.ExpressionsDisabled && c.mode == AnimatorConditionMode.If));
            Assert.False(idle.transitions[0].conditions.Any(c => c.parameter == FaceTrackingBuild.ExpressionsDisabled), "hand gestures that do not touch the face keep working");
            Assert.False(earsRest.transitions[0].conditions.Any(c => c.parameter == FaceTrackingBuild.ExpressionsDisabled), "ear poses keep working while the face is tracked");
            Assert.AreEqual(0, ears.anyStateTransitions.Length);
            Assert.True(fx.parameters.Any(p => p.name == FaceTrackingBuild.ExpressionsDisabled && p.type == AnimatorControllerParameterType.Bool));
        }

        [Test]
        public void TheTemplatesMouthIsTunedLikeTheUltirex()
        {
            var face = Mesh(Rexouium.Concat(new[] { "Grin_L", "Grin_R", "MouthNarrow" }));
            var fx = new AnimatorController(); owned.Add(fx);
            // The template's shape in VRCFury's copy, after its curves were renamed to the face's shapes.
            var up = Tree("Copied from FX - Face Tracking - SRanipal Blendshapes/Mouth Upper Up Left Blend", "OSCm/Proxy/FT/v2/MouthUpperUpLeft", (0f, Clip(("LipTrack_LipCornerTopUp_L", 0f))), (1f, Clip(("LipTrack_LipCornerTopUp_L", 100f))));
            var smile = Tree("Copied from FX - Face Tracking - SRanipal Blendshapes/Mouth Smile Left Blend", "OSCm/Proxy/FT/v2/SmileSadLeft", (0f, Clip(("LipTrack_LipSmileLeft", 0f))), (1f, Clip(("LipTrack_LipSmileLeft", 100f))));
            var sad = Tree("Mouth Sad Left Blend", "OSCm/Proxy/FT/v2/SmileSadLeft", (-1f, Clip(("LipTrack_LipFrownLeft", 100f))), (0f, Clip(("LipTrack_LipFrownLeft", 0f))));
            var lower = Clip(("LipTrack_LipCornerLowDown_L", 100f), ("LipTrack_LipCornerLowDown_R", 100f));
            var pout = Clip(("LipTrack_Pout_Pucker", 100f));
            var expressions = Direct("Expressions", (smile, "LipTrackingActive"), (up, "LipTrackingActive"), (sad, "LipTrackingActive"),
                (lower, "OSCm/Proxy/FT/v2/MouthLowerDown"), (pout, "OSCm/Proxy/FT/v2/LipPucker"));
            var smileDriver = Tree("Smile Sad Left Driver", "OSCm/Smooth/FT/v2/SmileSadLeft", (-1f, Clip()), (1f, Clip()));
            var smileInput = Tree("Smile Sad Left Input", "FT/v2/SmileSadLeft", (-1f, Clip()), (1f, Clip()));
            var jawDriver = Clip(); var jawInput = Clip();
            var drivers = Direct("Drivers", (smileDriver, "FT/DirectBlend"), (jawDriver, "OSCm/Smooth/FT/v2/JawOpen"));
            var inputs = Direct("Inputs", (smileInput, "FT/DirectBlend"), (jawInput, "FT/v2/JawOpen"));
            var smoothing = Tree("Lip Tracking Smoothing", FaceTrackingBuild.LocalSmoothing, (0f, drivers), (1f, inputs));
            var root = Direct("Face Tracking", (expressions, "FT/DirectBlend"), (smoothing, "LipTrackingActive"));
            Layer(fx, "Face Tracking").AddState("Tracking").motion = root;

            var tuning = FaceTrackingBuild.Tune(fx, "Body", face);

            Assert.AreEqual(1, tuning.Grins);
            Assert.False(expressions.children.Any(c => c.motion == up), "raising the upper lip is part of the grin");
            var grin = (BlendTree)expressions.children.Select(c => c.motion).Single(m => m.name.Contains("Grin"));
            Assert.AreEqual(smile.blendParameter, grin.blendParameter);
            CollectionAssert.AreEqual(new[] { 0f, FaceTrackingBuild.FullSmile }, grin.children.Select(c => c.threshold));
            var grinning = (AnimationClip)((BlendTree)grin.children[1].motion).children[1].motion;
            Assert.AreEqual(100f, Value(grinning, "Grin_L"));
            Assert.AreEqual(61.4f, Value(grinning, "LipTrack_LipSmileLeft"), .01f);
            Assert.AreEqual(0f, Value(grinning, "LipTrack_LipCornerTopUp_L"), "the grin lifts the corners itself");
            var lipUp = (AnimationClip)((BlendTree)grin.children[0].motion).children[1].motion;
            Assert.AreEqual(50f, Value(lipUp, "LipTrack_LipCornerTopUp_L"));
            CollectionAssert.AreEquivalent(new[] { -FaceTrackingBuild.FullSmile, 0f }, sad.children.Select(c => c.threshold), "a frown is full at -0.8");
            Assert.AreEqual(20f, Value(lower, "LipTrack_LipCornerLowDown_L"), .01f);
            Assert.AreEqual(20f, Value(pout, "LipTrack_Pout_Pucker"), .01f);
            Assert.AreEqual(60f, Value(pout, "MouthNarrow"), .01f, "a pout narrows the mouth");

            Assert.AreEqual(1, tuning.Slowed);
            CollectionAssert.AreEqual(new Motion[] { jawDriver }, drivers.children.Select(c => c.motion), "the jaw keeps the template's smoothing");
            CollectionAssert.AreEqual(new Motion[] { jawInput }, inputs.children.Select(c => c.motion));
            var slow = root.children.Last();
            Assert.AreEqual("LipTrackingActive", slow.directBlendParameter);
            var slowTree = (BlendTree)slow.motion;
            Assert.AreEqual(FaceTrackingBuild.LocalSmoothing, slowTree.blendParameter);
            CollectionAssert.AreEqual(new[] { 0f, FaceTrackingBuild.SlowSmoothing }, slowTree.children.Select(c => c.threshold));
            Assert.AreSame(smileDriver, ((BlendTree)slowTree.children[0].motion).children.Single().motion);
            Assert.AreSame(smileInput, ((BlendTree)slowTree.children[1].motion).children.Single().motion);

            Assert.AreEqual(1, FaceTrackingBuild.SetMouthSlowdown(fx, 1f), "the test switches smoothing without building again");
            Assert.AreEqual(1f, slowTree.children[1].threshold);
        }

        [Test]
        public void WithoutTheExpressiveMouthOnlyTheSmoothingIsTuned()
        {
            var face = Mesh(Rexouium.Concat(new[] { "Grin_L", "Grin_R" }));
            var fx = new AnimatorController(); owned.Add(fx);
            var smile = Tree("Mouth Smile Left Blend", "OSCm/Proxy/FT/v2/SmileSadLeft", (0f, Clip(("LipTrack_LipSmileLeft", 0f))), (1f, Clip(("LipTrack_LipSmileLeft", 100f))));
            var expressions = Direct("Expressions", (smile, "LipTrackingActive"));
            var drivers = Direct("Drivers", (Tree("Driver", "OSCm/Smooth/FT/v2/SmileSadLeft", (-1f, Clip()), (1f, Clip())), "FT/DirectBlend"));
            var inputs = Direct("Inputs", (Tree("Input", "FT/v2/SmileSadLeft", (-1f, Clip()), (1f, Clip())), "FT/DirectBlend"));
            var root = Direct("Face Tracking", (expressions, "FT/DirectBlend"), (Tree("Smoothing", FaceTrackingBuild.LocalSmoothing, (0f, drivers), (1f, inputs)), "LipTrackingActive"));
            Layer(fx, "Face Tracking").AddState("Tracking").motion = root;

            var tuning = FaceTrackingBuild.Tune(fx, "Body", face, expressive: false, slowdown: FaceTrackingBuild.MouthSlowdown(FaceTrackingSmoothing.Responsive));

            Assert.AreEqual(0, tuning.Grins + tuning.Gains + tuning.Shapes);
            CollectionAssert.AreEqual(new[] { 0f, 1f }, smile.children.Select(c => c.threshold), "the smile keeps the template's range");
            Assert.AreEqual(1, tuning.Slowed);
            Assert.AreEqual(1f, ((BlendTree)root.children.Last().motion).children[1].threshold, "responsive: the mouth follows as fast as the eyes");
        }

        // ---- VRCFaceTracking ----

        [Test]
        public void TheLiveLinkModuleMapsArKitToTheParametersTheTemplatesRead()
        {
            var frame = new FaceFrame { App = FaceApp.LiveLink };
            frame[ArKit.MouthSmileLeft] = .9f;
            frame[ArKit.MouthFrownRight] = .5f; frame[ArKit.MouthStretchRight] = .7f;
            frame[ArKit.JawOpen] = .6f; frame[ArKit.MouthClose] = .8f;
            frame[ArKit.EyeBlinkLeft] = 1f; frame[ArKit.EyeSquintLeft] = .5f; frame[ArKit.EyeWideRight] = .4f;
            frame[ArKit.CheekPuff] = .7f; frame[ArKit.MouthRollUpper] = .9f; frame[ArKit.MouthRight] = .4f; frame[ArKit.JawLeft] = .3f;
            frame[ArKit.TongueOut] = .75f;
            frame.LeftEye = new Vector3(-.1f, .2f, 0f);
            var face = new UnifiedFace();

            Vrcft.From(frame, face);

            Assert.AreEqual(.9f, Value("SmileSadLeft", face), 1e-5f, "smile: corner pull 0.8 + slant 0.2, both the phone's smile");
            Assert.AreEqual(-.7f, Value("SmileSadRight", face), 1e-5f, "sad: the larger of frown and stretch");
            Assert.AreEqual(.6f, Value("MouthClosed", face), 1e-5f, "a closed mouth never exceeds the open jaw (correctors)");
            Assert.AreEqual(0f, Value("EyeLidLeft", face), 1e-5f, "a blink with a squint closes the lid");
            Assert.AreEqual(.85f, Value("EyeLidRight", face), 1e-5f, "open 0.75, plus a quarter of the wide eye");
            Assert.AreEqual(.7f, Value("CheekPuffSuckLeft", face), 1e-5f);
            Assert.AreEqual(.9f, Value("LipSuckUpper", face), 1e-5f, "the lip rolls in while not raised");
            Assert.AreEqual(.4f, Value("MouthX", face), 1e-5f);
            Assert.AreEqual(-.3f, Value("JawX", face), 1e-5f);
            Assert.AreEqual(.75f, Value("TongueOut", face), 1e-5f);
            Assert.AreEqual(.2f, Value("EyeLeftX", face), 1e-5f, "Live Link's eye yaw as sent");
            Assert.AreEqual(.1f, Value("EyeLeftY", face), 1e-5f, "pitch inverted");
            Assert.AreEqual(.5f, Value("PupilDilation", face), 1e-5f, "iPhones report a fixed pupil");
        }

        [Test]
        public void TheIFacialMocapModuleSwapsTheAppsSides()
        {
            var frame = new FaceFrame { App = FaceApp.IFacialMocap };
            frame[ArKit.MouthSmileRight] = .9f;
            frame[ArKit.EyeBlinkRight] = 1f;
            frame[ArKit.JawLeft] = .5f;
            frame.LeftEye = new Vector3(0f, 45f, 0f);
            var face = new UnifiedFace();

            Vrcft.From(frame, face);

            Assert.AreEqual(.72f, Value("SmileSadLeft", face), 1e-5f, "the app's right smile is the left one, as the corner pull only (0.8)");
            Assert.AreEqual(0f, Value("SmileSadRight", face), 1e-5f);
            Assert.AreEqual(0f, face.LeftOpenness, 1e-5f, "the app's right blink closes the left eye");
            Assert.AreEqual(.5f, Value("JawX", face), 1e-5f, "the app's jaw left moves the jaw right");
            Assert.AreEqual(Mathf.Tan(.5f), Value("EyeLeftX", face), 1e-5f, "gaze: tan of the angle over 90");
        }

        [Test]
        public void TheAvatarsParametersAreDrivenLikeVrcftFindsThem()
        {
            var parameters = new VrcftAvatarParameters(new[]
            {
                ("FT/v2/JawX", VRCExpressionParameters.ValueType.Float),
                ("FT/v2/JawX1", VRCExpressionParameters.ValueType.Bool), ("FT/v2/JawX2", VRCExpressionParameters.ValueType.Bool),
                ("FT/v2/JawX4", VRCExpressionParameters.ValueType.Bool), ("FT/v2/JawXNegative", VRCExpressionParameters.ValueType.Bool),
                ("v2/JawOpen", VRCExpressionParameters.ValueType.Float),
                ("EyeTrackingActive", VRCExpressionParameters.ValueType.Bool), ("LipTrackingActive", VRCExpressionParameters.ValueType.Bool),
                ("FT/v2/NotAThing", VRCExpressionParameters.ValueType.Float), ("Toggle/Hat", VRCExpressionParameters.ValueType.Bool),
            });
            var face = new UnifiedFace();
            face[Ue.JawRight] = .6f; face[Ue.JawOpen] = .4f;
            var sent = new Dictionary<string, float>();

            parameters.Compute(face, true, false, (name, value) => sent[name] = value);

            Assert.AreEqual(8, parameters.Count, "VRCFaceTracking drives only the names it knows");
            Assert.AreEqual(.6f, sent["FT/v2/JawX"], 1e-5f);
            Assert.AreEqual(.4f, sent["v2/JawOpen"], 1e-5f, "any prefix before v2/");
            // 0.6 on 3 bits: (int)(0.6 * 7) = 4.
            Assert.AreEqual(new[] { 0f, 0f, 1f, 0f }, new[] { sent["FT/v2/JawX1"], sent["FT/v2/JawX2"], sent["FT/v2/JawX4"], sent["FT/v2/JawXNegative"] });
            Assert.AreEqual(1f, sent["EyeTrackingActive"]);
            Assert.AreEqual(0f, sent["LipTrackingActive"]);

            face[Ue.JawRight] = 0f; face[Ue.JawLeft] = .3f;
            parameters.Compute(face, true, true, (name, value) => sent[name] = value, name => name != "v2/JawOpen");
            // -0.3: (int)(0.3 * 7) = 2, negative.
            Assert.AreEqual(new[] { 0f, 1f, 0f, 1f }, new[] { sent["FT/v2/JawX1"], sent["FT/v2/JawX2"], sent["FT/v2/JawX4"], sent["FT/v2/JawXNegative"] });
            Assert.AreEqual(.4f, sent["v2/JawOpen"], 1e-5f, "a parameter left out keeps its value");
            Assert.False(Vrcft.Bit(-.5f, 0, 3, false), "without a Negative bool, negative values send nothing");
        }

        // ---- The phone apps' packets ----

        [Test]
        public void IFacialMocapFramesAreParsed()
        {
            var frame = new FaceFrame();
            Assert.True(FaceTrackingProtocols.TryParseIFacialMocap(
                "mouthSmile_R-35|eyeBlink_L-100|jawOpen-12|mouthLeft-7|=head#-21.488958,-6.038993,-6.6019735,-0.030653415,-0.10287084,-0.6584072|" +
                "rightEye#6.0297494,2.4403017,0.25649446|leftEye#6.034903,-1.6660284,-0.17520553|", frame));
            Assert.AreEqual(FaceApp.IFacialMocap, frame.App);
            Assert.AreEqual(.35f, frame[ArKit.MouthSmileRight], 1e-5f);
            Assert.AreEqual(1f, frame[ArKit.EyeBlinkLeft], 1e-5f);
            Assert.AreEqual(.12f, frame[ArKit.JawOpen], 1e-5f);
            Assert.AreEqual(.07f, frame[ArKit.MouthLeft], 1e-5f, "the mouth's own left has no side suffix");
            Assert.AreEqual(-21.488958f, frame.Head.x, 1e-4f);
            Assert.AreEqual(-1.6660284f, frame.LeftEye.y, 1e-4f);
            Assert.AreEqual(2.4403017f, frame.RightEye.y, 1e-4f);

            Assert.True(FaceTrackingProtocols.TryParseIFacialMocap("mouthSmile_L&80|tongueOut&55|", frame), "the v2 format separates with &");
            Assert.AreEqual(.8f, frame[ArKit.MouthSmileLeft], 1e-5f);
            Assert.AreEqual(.55f, frame[ArKit.TongueOut], 1e-5f);
            Assert.False(FaceTrackingProtocols.TryParseIFacialMocap("iFacialMocap_sahuasouryya9218sauhuiayeta91555dy3719", frame));

            var sent = new FaceFrame();
            sent[ArKit.CheekPuff] = .42f; sent[ArKit.EyeLookInRight] = .3f; sent.LeftEye = new Vector3(3f, -4f, 0f);
            Assert.True(FaceTrackingProtocols.TryParseIFacialMocap(FaceTrackingProtocols.IFacialMocapPacket(sent), frame));
            Assert.AreEqual(.42f, frame[ArKit.CheekPuff], 1e-5f);
            Assert.AreEqual(.3f, frame[ArKit.EyeLookInRight], 1e-5f);
            Assert.AreEqual(-4f, frame.LeftEye.y, 1e-4f);
        }

        [Test]
        public void LiveLinkFacePacketsAreParsed()
        {
            var sent = new FaceFrame();
            sent[ArKit.EyeBlinkRight] = .8f; sent[ArKit.MouthPucker] = .55f; sent[ArKit.TongueOut] = 1f;
            sent.Head = new Vector3(.01f, -.05f, .02f); sent.LeftEye = new Vector3(.03f, .12f, 0f); sent.RightEye = new Vector3(-.02f, .1f, 0f);
            var packet = FaceTrackingProtocols.LiveLinkPacket(sent, "6B5A0D43-0000-4000-8000-000000000000", "iPhone");
            Assert.AreEqual(270 + 36 + 6, packet.Length, "Unreal's layout: 270 bytes plus the device id and subject name");
            var frame = new FaceFrame();

            Assert.True(FaceTrackingProtocols.TryParseLiveLink(packet, packet.Length, frame, out string device));

            Assert.AreEqual(FaceApp.LiveLink, frame.App);
            Assert.AreEqual("6B5A0D43-0000-4000-8000-000000000000", device);
            Assert.AreEqual(.8f, frame[ArKit.EyeBlinkRight], 1e-6f);
            Assert.AreEqual(.55f, frame[ArKit.MouthPucker], 1e-6f);
            Assert.AreEqual(1f, frame[ArKit.TongueOut], 1e-6f);
            Assert.AreEqual(-.05f, frame.Head.y, 1e-6f, "head yaw");
            Assert.AreEqual(.12f, frame.LeftEye.y, 1e-6f, "left eye yaw");
            Assert.AreEqual(-.02f, frame.RightEye.x, 1e-6f, "right eye pitch");
            Assert.False(FaceTrackingProtocols.TryParseLiveLink(new byte[200], 200, frame, out _), "too short for 61 values");
            var noise = Encoding.ASCII.GetBytes(new string('x', 300));
            Assert.False(FaceTrackingProtocols.TryParseLiveLink(noise, noise.Length, frame, out _), "not a face");
        }

        [Test]
        public void TheSimulatorPerformsThroughTheSamePipeline()
        {
            var frame = new FaceFrame();
            var face = new UnifiedFace();
            FaceTrackingSimulator.Fill(1.6 + 1.2, frame);
            Assert.AreEqual("Smile", FaceTrackingSimulator.Name(1.6 + 1.2));
            Vrcft.From(frame, face);
            Assert.Greater(Value("SmileSadLeft", face), .8f);
            FaceTrackingSimulator.Fill(.08, frame);
            Vrcft.From(frame, face);
            Assert.Less(Value("EyeLidLeft", face), .1f, "it blinks");
            FaceTrackingSimulator.Fill(FaceTrackingSimulator.Length + 1.6 + 1.2, frame);
            Assert.Greater(frame[ArKit.MouthSmileLeft], .8f, "it loops");
        }

        // ---- Quick settings ----

        [Test]
        public void FeaturesOthersDontSeeAreLeftOutOfTheBuild()
        {
            Assert.AreEqual(FaceTrackingFeatures.Eyes, FaceTrackingFeatureSet.FeatureOf("FT/v2/EyeLidLeft"));
            Assert.AreEqual(FaceTrackingFeatures.Pupils, FaceTrackingFeatureSet.FeatureOf("FT/v2/PupilDilation4"));
            Assert.AreEqual(FaceTrackingFeatures.Mouth, FaceTrackingFeatureSet.FeatureOf("FT/v2/SmileSadLeftNegative"));
            Assert.AreEqual(FaceTrackingFeatures.Tongue, FaceTrackingFeatureSet.FeatureOf("FT/v2/TongueX1"));
            Assert.AreEqual(FaceTrackingFeatures.Cheeks, FaceTrackingFeatureSet.FeatureOf("FT/v2/CheekPuffSuckLeft"));
            Assert.AreEqual(FaceTrackingFeatures.Mouth, FaceTrackingFeatureSet.FeatureOf("LipTrackingActive"));
            Assert.AreEqual(FaceTrackingFeatures.None, FaceTrackingFeatureSet.FeatureOf("FacialExpressionsDisabled"));
            Assert.AreEqual(FaceTrackingFeatures.Eyes, FaceTrackingFeatureSet.Effective(FaceTrackingFeatures.Eyes | FaceTrackingFeatures.Tongue), "no tongue without the mouth");

            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>(); owned.Add(parameters);
            VRCExpressionParameters.Parameter P(string name, VRCExpressionParameters.ValueType type, float value = 0f) =>
                new VRCExpressionParameters.Parameter { name = name, valueType = type, defaultValue = value, networkSynced = true };
            parameters.parameters = new[]
            {
                P("EyeTrackingActive", VRCExpressionParameters.ValueType.Bool), P("LipTrackingActive", VRCExpressionParameters.ValueType.Bool),
                P("FT/v2/EyeLidLeft", VRCExpressionParameters.ValueType.Float, .75f), P("FT/v2/JawOpen", VRCExpressionParameters.ValueType.Float),
                P("FT/v2/TongueOut1", VRCExpressionParameters.ValueType.Bool), P("FT/v2/TongueOut2", VRCExpressionParameters.ValueType.Bool),
                P(FaceTrackingBuild.LocalSmoothingParameter, VRCExpressionParameters.ValueType.Float, .25f), P("Hat", VRCExpressionParameters.ValueType.Bool),
            };
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>(); owned.Add(menu);
            VRCExpressionsMenu.Control Toggle(string parameter) => new VRCExpressionsMenu.Control
            { name = parameter, type = VRCExpressionsMenu.Control.ControlType.Toggle, parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter } };
            menu.controls = new List<VRCExpressionsMenu.Control> { Toggle("LipTrackingActive"), Toggle("Hat") };
            var avatar = new GameObject("Avatar"); owned.Add(avatar);
            var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
            descriptor.expressionParameters = parameters; descriptor.expressionsMenu = menu;
            var template = parameters.parameters.Select(p => p.name).Where(n => n != "Hat").ToList();

            var removed = FaceTrackingFeatureSet.Removed(template, FaceTrackingFeatures.Eyes | FaceTrackingFeatures.Tongue);
            FaceTrackingBuild.Configure(descriptor, FaceTrackingBuild.StartingSmoothing(FaceTrackingSmoothing.Smooth), removed);

            CollectionAssert.AreEquivalent(new[] { "EyeTrackingActive", "FT/v2/EyeLidLeft", FaceTrackingBuild.LocalSmoothingParameter, "Hat" }, parameters.parameters.Select(p => p.name),
                "the mouth and the tongue (which needs it) are left out; the avatar's own parameters stay");
            Assert.AreEqual(.6f, parameters.parameters.Single(p => p.name == FaceTrackingBuild.LocalSmoothingParameter).defaultValue, 1e-5f);
            CollectionAssert.AreEqual(new[] { "Hat" }, menu.controls.Select(c => c.name), "the menu toggle of a removed parameter goes");
        }

        [Test]
        public void TheTestPlaysTheBuiltLayersAndTheirParameterDrivers()
        {
            var avatar = new GameObject("Avatar"); owned.Add(avatar);
            avatar.AddComponent<Animator>();
            var descriptor = avatar.AddComponent<VRCAvatarDescriptor>();
            var body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
            body.transform.SetParent(avatar.transform, false);
            body.sharedMesh = Mesh(new[] { "Smile" });
            var fx = new AnimatorController(); owned.Add(fx);
            fx.AddParameter("FT/v2/SmileSadLeft", AnimatorControllerParameterType.Float);
            fx.AddParameter("EyeTrackingActive", AnimatorControllerParameterType.Float);
            fx.AddParameter("FacialExpressionsDisabled", AnimatorControllerParameterType.Bool);
            // The template's state layer, renamed by VRCFury (its state machine keeps the template's name): tracking turns the
            // hand-gesture expressions off through a parameter driver.
            var state = new AnimatorStateMachine { name = "Tracking_State" };
            fx.AddLayer(new AnimatorControllerLayer { name = "[VF106] Tracking_State", stateMachine = state, defaultWeight = 0 });
            var idle = state.AddState("Idle"); state.defaultState = idle;
            var on = state.AddState("Tracking");
            var driver = on.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter> { new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = "FacialExpressionsDisabled", value = 1f } };
            var toOn = state.AddAnyStateTransition(on); toOn.AddCondition(AnimatorConditionMode.Greater, .992f, "EyeTrackingActive"); toOn.duration = 0f; toOn.canTransitionToSelf = false;
            // The face layer: the smile parameter drives the blendshape.
            var smile = Tree("Smile", "FT/v2/SmileSadLeft", (0f, Clip(("Smile", 0f))), (1f, Clip(("Smile", 100f))));
            Layer(fx, "Face").AddState("Face").motion = smile;

            using (var rig = new FaceTrackingTestRig(avatar, descriptor, null, fx))
            {
                rig.Evaluate(.02f);
                Assert.AreEqual(0f, rig.Get("FacialExpressionsDisabled"));
                rig.Set("EyeTrackingActive", 1f);
                rig.Set("FT/v2/SmileSadLeft", .5f);
                for (int i = 0; i < 4; i++) rig.Evaluate(.02f);
                Assert.AreEqual(1f, rig.Get("FacialExpressionsDisabled"), "the driver ran when its state was entered");
                Assert.AreEqual(50f, body.GetBlendShapeWeight(0), .5f, "the layers animate the copy outside Play Mode");
                Assert.AreEqual(1f, rig.Get("IsLocal"), "VRChat's built-in parameters, as the wearer");
            }
        }

        private BlendTree Tree(string name, string parameter, params (float threshold, Motion motion)[] children)
        {
            var tree = new BlendTree { name = name, blendType = BlendTreeType.Simple1D, blendParameter = parameter, useAutomaticThresholds = false }; owned.Add(tree);
            tree.children = children.Select(c => new ChildMotion { motion = c.motion, threshold = c.threshold, timeScale = 1 }).ToArray();
            return tree;
        }

        private BlendTree Direct(string name, params (Motion motion, string weight)[] children)
        {
            var tree = new BlendTree { name = name, blendType = BlendTreeType.Direct }; owned.Add(tree);
            tree.children = children.Select(c => new ChildMotion { motion = c.motion, directBlendParameter = c.weight, timeScale = 1 }).ToArray();
            return tree;
        }

        private static float Value(string v2, UnifiedFace face) => Vrcft.Parameter(v2, out _)(face);

        private static float Value(AnimationClip clip, string shape) =>
            AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape)).Evaluate(0);

        private Mesh Mesh(IEnumerable<string> shapes)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } };
            owned.Add(mesh);
            var delta = new Vector3[3];
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, delta, null, null);
            return mesh;
        }

        private AnimationClip Clip(params (string shape, float value)[] curves)
        {
            var clip = new AnimationClip(); owned.Add(clip);
            foreach (var (shape, value) in curves)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SkinnedMeshRenderer), "blendShape." + shape), AnimationCurve.Constant(0, 1, value));
            return clip;
        }

        private static AnimatorStateMachine Layer(AnimatorController fx, string name)
        {
            var machine = new AnimatorStateMachine { name = name };
            fx.AddLayer(new AnimatorControllerLayer { name = name, stateMachine = machine, defaultWeight = 1 });
            return machine;
        }
    }
}
