using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>ARKit's 52 face blendshapes in Live Link Face's packet order, named as the apps label them (the phone's left).</summary>
    internal enum ArKit
    {
        EyeBlinkLeft, EyeLookDownLeft, EyeLookInLeft, EyeLookOutLeft, EyeLookUpLeft, EyeSquintLeft, EyeWideLeft,
        EyeBlinkRight, EyeLookDownRight, EyeLookInRight, EyeLookOutRight, EyeLookUpRight, EyeSquintRight, EyeWideRight,
        JawForward, JawLeft, JawRight, JawOpen, MouthClose, MouthFunnel, MouthPucker, MouthLeft, MouthRight,
        MouthSmileLeft, MouthSmileRight, MouthFrownLeft, MouthFrownRight, MouthDimpleLeft, MouthDimpleRight, MouthStretchLeft, MouthStretchRight,
        MouthRollLower, MouthRollUpper, MouthShrugLower, MouthShrugUpper, MouthPressLeft, MouthPressRight, MouthLowerDownLeft, MouthLowerDownRight,
        MouthUpperUpLeft, MouthUpperUpRight, BrowDownLeft, BrowDownRight, BrowInnerUp, BrowOuterUpLeft, BrowOuterUpRight,
        CheekPuff, CheekSquintLeft, CheekSquintRight, NoseSneerLeft, NoseSneerRight, TongueOut,
        Count
    }

    /// <summary>Where face data comes from. Each iPhone app has its own VRCFaceTracking module, with its own mapping.</summary>
    internal enum FaceApp { None, IFacialMocap, LiveLink, Simulator }

    /// <summary>
    /// One frame of an iPhone app: ARKit weights (0 to 1) and the eyes as the app sends them. iFacialMocap sends degrees
    /// (pitch, yaw, roll); Live Link Face sends degrees / 180, which VRCFaceTracking's module uses as they are.
    /// </summary>
    internal sealed class FaceFrame
    {
        public FaceApp App;
        public readonly float[] Shapes = new float[(int)ArKit.Count];
        public Vector3 Head, LeftEye, RightEye;

        public float this[ArKit shape] { get => Shapes[(int)shape]; set => Shapes[(int)shape] = value; }

        public void CopyTo(FaceFrame other)
        {
            other.App = App;
            Array.Copy(Shapes, other.Shapes, Shapes.Length);
            other.Head = Head; other.LeftEye = LeftEye; other.RightEye = RightEye;
        }

        public void Clear()
        {
            Array.Clear(Shapes, 0, Shapes.Length);
            Head = LeftEye = RightEye = Vector3.zero;
        }
    }

    /// <summary>VRCFaceTracking's Unified Expressions, in its order (VRCFaceTracking.Core/Params/Expressions/UnifiedExpressions.cs).</summary>
    internal enum Ue
    {
        EyeSquintRight, EyeSquintLeft, EyeWideRight, EyeWideLeft, BrowPinchRight, BrowPinchLeft, BrowLowererRight, BrowLowererLeft,
        BrowInnerUpRight, BrowInnerUpLeft, BrowOuterUpRight, BrowOuterUpLeft, NasalDilationRight, NasalDilationLeft, NasalConstrictRight,
        NasalConstrictLeft, CheekSquintRight, CheekSquintLeft, CheekPuffRight, CheekPuffLeft, CheekSuckRight, CheekSuckLeft, JawOpen, JawRight,
        JawLeft, JawForward, JawBackward, JawClench, JawMandibleRaise, MouthClosed, LipSuckUpperRight, LipSuckUpperLeft, LipSuckLowerRight,
        LipSuckLowerLeft, LipSuckCornerRight, LipSuckCornerLeft, LipFunnelUpperRight, LipFunnelUpperLeft, LipFunnelLowerRight, LipFunnelLowerLeft,
        LipPuckerUpperRight, LipPuckerUpperLeft, LipPuckerLowerRight, LipPuckerLowerLeft, MouthUpperUpRight, MouthUpperUpLeft,
        MouthUpperDeepenRight, MouthUpperDeepenLeft, NoseSneerRight, NoseSneerLeft, MouthLowerDownRight, MouthLowerDownLeft, MouthUpperRight,
        MouthUpperLeft, MouthLowerRight, MouthLowerLeft, MouthCornerPullRight, MouthCornerPullLeft, MouthCornerSlantRight, MouthCornerSlantLeft,
        MouthFrownRight, MouthFrownLeft, MouthStretchRight, MouthStretchLeft, MouthDimpleRight, MouthDimpleLeft, MouthRaiserUpper,
        MouthRaiserLower, MouthPressRight, MouthPressLeft, MouthTightenerRight, MouthTightenerLeft, TongueOut, TongueUp, TongueDown,
        TongueRight, TongueLeft, TongueRoll, TongueBendDown, TongueCurlUp, TongueSquish, TongueFlat, TongueTwistRight, TongueTwistLeft,
        SoftPalateClose, ThroatSwallow, NeckFlexRight, NeckFlexLeft,
        Max
    }

    /// <summary>VRCFaceTracking's tracking data: Unified Expressions and both eyes.</summary>
    internal sealed class UnifiedFace
    {
        public readonly float[] Shapes = new float[(int)Ue.Max];
        public Vector2 LeftGaze, RightGaze;
        public float LeftOpenness = 1f, RightOpenness = 1f;
        public float LeftPupil = 5f, RightPupil = 5f, MinDilation, MaxDilation = 10f;

        public float this[Ue shape] { get => Shapes[(int)shape]; set => Shapes[(int)shape] = value; }

        public void Clear()
        {
            Array.Clear(Shapes, 0, Shapes.Length);
            LeftGaze = RightGaze = Vector2.zero;
            LeftOpenness = RightOpenness = 1f;
            LeftPupil = RightPupil = 5f; MinDilation = 0f; MaxDilation = 10f;
        }
    }

    /// <summary>
    /// What VRCFaceTracking does with an iPhone's data: its module's mapping to Unified Expressions, its correctors, then the
    /// "v2" parameters the face tracking templates read.
    /// </summary>
    internal static class Vrcft
    {
        // ---- iPhone modules: ARKit to Unified Expressions ----

        /// <summary>The Live Link module (kusomaigo/VRCFaceTracking-LiveLink, LiveLinkExtTrackingInterface.cs).</summary>
        internal static void FromLiveLink(FaceFrame f, UnifiedFace u)
        {
            u.Clear();
            u.LeftOpenness = 1f - Mathf.Clamp01(f[ArKit.EyeBlinkLeft] + f[ArKit.EyeBlinkLeft] * f[ArKit.EyeSquintLeft]);
            u.RightOpenness = 1f - Mathf.Clamp01(f[ArKit.EyeBlinkRight] + f[ArKit.EyeBlinkRight] * f[ArKit.EyeSquintRight]);
            // "The raw values seem to be the most right": degrees / 180 as sent, pitch inverted.
            u.LeftGaze = new Vector2(f.LeftEye.y, -f.LeftEye.x);
            u.RightGaze = new Vector2(f.RightEye.y, -f.RightEye.x);
            u[Ue.EyeWideLeft] = f[ArKit.EyeWideLeft]; u[Ue.EyeWideRight] = f[ArKit.EyeWideRight];
            // The module's own slip: both squints read the left eye.
            u[Ue.EyeSquintLeft] = f[ArKit.EyeSquintLeft]; u[Ue.EyeSquintRight] = f[ArKit.EyeSquintLeft];
            u[Ue.BrowInnerUpLeft] = u[Ue.BrowInnerUpRight] = f[ArKit.BrowInnerUp];
            u[Ue.BrowOuterUpLeft] = f[ArKit.BrowOuterUpLeft]; u[Ue.BrowOuterUpRight] = f[ArKit.BrowOuterUpRight];
            u[Ue.BrowPinchLeft] = u[Ue.BrowLowererLeft] = f[ArKit.BrowDownLeft];
            u[Ue.BrowPinchRight] = u[Ue.BrowLowererRight] = f[ArKit.BrowDownRight];
            u[Ue.JawOpen] = f[ArKit.JawOpen]; u[Ue.JawLeft] = f[ArKit.JawLeft]; u[Ue.JawRight] = f[ArKit.JawRight]; u[Ue.JawForward] = f[ArKit.JawForward];
            u[Ue.MouthClosed] = f[ArKit.MouthClose];
            u[Ue.MouthUpperLeft] = u[Ue.MouthLowerLeft] = f[ArKit.MouthLeft];
            u[Ue.MouthUpperRight] = u[Ue.MouthLowerRight] = f[ArKit.MouthRight];
            u[Ue.MouthCornerPullLeft] = u[Ue.MouthCornerSlantLeft] = f[ArKit.MouthSmileLeft];
            u[Ue.MouthCornerPullRight] = u[Ue.MouthCornerSlantRight] = f[ArKit.MouthSmileRight];
            u[Ue.MouthFrownLeft] = f[ArKit.MouthFrownLeft]; u[Ue.MouthFrownRight] = f[ArKit.MouthFrownRight];
            u[Ue.MouthLowerDownLeft] = f[ArKit.MouthLowerDownLeft]; u[Ue.MouthLowerDownRight] = f[ArKit.MouthLowerDownRight];
            u[Ue.MouthUpperUpLeft] = u[Ue.MouthUpperDeepenLeft] = f[ArKit.MouthUpperUpLeft];
            u[Ue.MouthUpperUpRight] = u[Ue.MouthUpperDeepenRight] = f[ArKit.MouthUpperUpRight];
            u[Ue.MouthRaiserUpper] = f[ArKit.MouthShrugUpper]; u[Ue.MouthRaiserLower] = f[ArKit.MouthShrugLower];
            u[Ue.MouthDimpleLeft] = f[ArKit.MouthDimpleLeft]; u[Ue.MouthDimpleRight] = f[ArKit.MouthDimpleRight];
            u[Ue.MouthPressLeft] = f[ArKit.MouthPressLeft]; u[Ue.MouthPressRight] = f[ArKit.MouthPressRight];
            u[Ue.MouthStretchLeft] = f[ArKit.MouthStretchLeft]; u[Ue.MouthStretchRight] = f[ArKit.MouthStretchRight];
            Lips(u, f[ArKit.MouthPucker], f[ArKit.MouthFunnel], f[ArKit.MouthUpperUpLeft], f[ArKit.MouthUpperUpRight], f[ArKit.MouthRollUpper], f[ArKit.MouthRollLower]);
            u[Ue.CheekPuffLeft] = u[Ue.CheekPuffRight] = f[ArKit.CheekPuff];
            u[Ue.CheekSquintLeft] = f[ArKit.CheekSquintLeft]; u[Ue.CheekSquintRight] = f[ArKit.CheekSquintRight];
            u[Ue.NoseSneerLeft] = f[ArKit.NoseSneerLeft]; u[Ue.NoseSneerRight] = f[ArKit.NoseSneerRight];
            u[Ue.TongueOut] = f[ArKit.TongueOut];
        }

        /// <summary>
        /// The iFacialMocap module (Shuisho10/VRC_iFacialMocap, iFacialMocapTrackingInterface.cs). It swaps left and right
        /// ("ARKit mirrors R/L") for every shape, but not for the eye rotations.
        /// </summary>
        internal static void FromIFacialMocap(FaceFrame f, UnifiedFace u)
        {
            u.Clear();
            // "Normalized range of -45 to 45 degrees, tan function."
            u.LeftGaze = new Vector2(Mathf.Tan(f.LeftEye.y / 90f), -Mathf.Tan(f.LeftEye.x / 90f));
            u.RightGaze = new Vector2(Mathf.Tan(f.RightEye.y / 90f), -Mathf.Tan(f.RightEye.x / 90f));
            u.LeftOpenness = 1f - Mathf.Clamp01(f[ArKit.EyeBlinkRight] + f[ArKit.EyeBlinkRight] * f[ArKit.EyeSquintRight]);
            u.RightOpenness = 1f - Mathf.Clamp01(f[ArKit.EyeBlinkLeft] + f[ArKit.EyeBlinkLeft] * f[ArKit.EyeSquintLeft]);
            u[Ue.EyeSquintLeft] = f[ArKit.EyeSquintRight]; u[Ue.EyeSquintRight] = f[ArKit.EyeSquintLeft];
            u[Ue.EyeWideLeft] = f[ArKit.EyeWideRight]; u[Ue.EyeWideRight] = f[ArKit.EyeWideLeft];
            // The module reads "browInnerUp_R" and "browInnerUp_L", which the app never sends: the inner brows stay at 0.
            u[Ue.BrowLowererLeft] = u[Ue.BrowPinchLeft] = f[ArKit.BrowDownRight];
            u[Ue.BrowLowererRight] = u[Ue.BrowPinchRight] = f[ArKit.BrowDownLeft];
            u[Ue.BrowOuterUpLeft] = f[ArKit.BrowOuterUpRight]; u[Ue.BrowOuterUpRight] = f[ArKit.BrowOuterUpLeft];
            u[Ue.NoseSneerLeft] = f[ArKit.NoseSneerRight]; u[Ue.NoseSneerRight] = f[ArKit.NoseSneerLeft];
            u[Ue.CheekPuffLeft] = u[Ue.CheekPuffRight] = f[ArKit.CheekPuff];
            u[Ue.CheekSquintLeft] = f[ArKit.CheekSquintRight]; u[Ue.CheekSquintRight] = f[ArKit.CheekSquintLeft];
            u[Ue.JawLeft] = f[ArKit.JawRight]; u[Ue.JawRight] = f[ArKit.JawLeft];
            u[Ue.JawOpen] = f[ArKit.JawOpen]; u[Ue.MouthClosed] = f[ArKit.MouthClose]; u[Ue.JawForward] = f[ArKit.JawForward];
            Lips(u, f[ArKit.MouthPucker], f[ArKit.MouthFunnel], f[ArKit.MouthUpperUpRight], f[ArKit.MouthUpperUpLeft], f[ArKit.MouthRollUpper], f[ArKit.MouthRollLower]);
            u[Ue.MouthRaiserLower] = f[ArKit.MouthShrugLower]; u[Ue.MouthRaiserUpper] = f[ArKit.MouthShrugUpper];
            u[Ue.MouthUpperLeft] = u[Ue.MouthLowerLeft] = f[ArKit.MouthRight];
            u[Ue.MouthUpperRight] = u[Ue.MouthLowerRight] = f[ArKit.MouthLeft];
            u[Ue.MouthUpperUpLeft] = f[ArKit.MouthUpperUpRight]; u[Ue.MouthUpperUpRight] = f[ArKit.MouthUpperUpLeft];
            u[Ue.MouthLowerDownLeft] = f[ArKit.MouthLowerDownRight]; u[Ue.MouthLowerDownRight] = f[ArKit.MouthLowerDownLeft];
            // Only the corner pull: VRCFaceTracking's smile is then 0.8 of the phone's.
            u[Ue.MouthCornerPullLeft] = f[ArKit.MouthSmileRight]; u[Ue.MouthCornerPullRight] = f[ArKit.MouthSmileLeft];
            u[Ue.MouthDimpleLeft] = f[ArKit.MouthDimpleRight]; u[Ue.MouthDimpleRight] = f[ArKit.MouthDimpleLeft];
            u[Ue.MouthFrownLeft] = f[ArKit.MouthFrownRight]; u[Ue.MouthFrownRight] = f[ArKit.MouthFrownLeft];
            u[Ue.MouthPressLeft] = f[ArKit.MouthPressRight]; u[Ue.MouthPressRight] = f[ArKit.MouthPressLeft];
            u[Ue.MouthStretchLeft] = f[ArKit.MouthStretchRight]; u[Ue.MouthStretchRight] = f[ArKit.MouthStretchLeft];
            u[Ue.TongueOut] = f[ArKit.TongueOut];
        }

        /// <summary>The simulator: the Live Link mapping, with eyes in degrees (full gaze at 30) so its glances read well.</summary>
        internal static void FromSimulator(FaceFrame f, UnifiedFace u)
        {
            FromLiveLink(f, u);
            u[Ue.EyeSquintRight] = f[ArKit.EyeSquintRight];
            u.LeftGaze = new Vector2(Mathf.Clamp(f.LeftEye.y / 30f, -1f, 1f), Mathf.Clamp(-f.LeftEye.x / 30f, -1f, 1f));
            u.RightGaze = new Vector2(Mathf.Clamp(f.RightEye.y / 30f, -1f, 1f), Mathf.Clamp(-f.RightEye.x / 30f, -1f, 1f));
        }

        /// <summary>The module VRCFaceTracking would use for <paramref name="f"/>.</summary>
        internal static void From(FaceFrame f, UnifiedFace u)
        {
            switch (f.App)
            {
                case FaceApp.IFacialMocap: FromIFacialMocap(f, u); break;
                case FaceApp.Simulator: FromSimulator(f, u); break;
                default: FromLiveLink(f, u); break;
            }
            Correct(u);
        }

        // Both modules: every lip quadrant from the phone's pucker and funnel; the upper lip only sucks in while not raised.
        private static void Lips(UnifiedFace u, float pucker, float funnel, float upperUpLeft, float upperUpRight, float rollUpper, float rollLower)
        {
            u[Ue.LipPuckerUpperLeft] = u[Ue.LipPuckerLowerLeft] = u[Ue.LipPuckerUpperRight] = u[Ue.LipPuckerLowerRight] = pucker;
            u[Ue.LipFunnelUpperLeft] = u[Ue.LipFunnelLowerLeft] = u[Ue.LipFunnelUpperRight] = u[Ue.LipFunnelLowerRight] = funnel;
            u[Ue.LipSuckUpperLeft] = Mathf.Min(1f - Mathf.Pow(upperUpLeft, 1f / 6f), rollUpper);
            u[Ue.LipSuckUpperRight] = Mathf.Min(1f - Mathf.Pow(upperUpRight, 1f / 6f), rollUpper);
            u[Ue.LipSuckLowerLeft] = u[Ue.LipSuckLowerRight] = rollLower;
        }

        /// <summary>The correctors VRCFaceTracking applies by default (Params/Data/Mutation/Correctors.cs).</summary>
        internal static void Correct(UnifiedFace u)
        {
            u[Ue.MouthClosed] = Math.Min(u[Ue.MouthClosed], u[Ue.JawOpen]);
            u[Ue.LipSuckLowerLeft] *= 1f - u[Ue.MouthLowerDownLeft];
            u[Ue.LipSuckLowerRight] *= 1f - u[Ue.MouthLowerDownRight];
            u[Ue.LipSuckUpperLeft] *= 1f - u[Ue.MouthUpperUpLeft];
            u[Ue.LipSuckUpperRight] *= 1f - u[Ue.MouthUpperUpRight];
        }

        // ---- The parameters (Params/Expressions/UnifiedExpressionsParameters.cs, UnifiedSimpleExpressions.cs) ----

        private static float SmileLeft(UnifiedFace u) => u[Ue.MouthCornerPullLeft] * .8f + u[Ue.MouthCornerSlantLeft] * .2f;
        private static float SmileRight(UnifiedFace u) => u[Ue.MouthCornerPullRight] * .8f + u[Ue.MouthCornerSlantRight] * .2f;
        private static float SadLeft(UnifiedFace u) => Math.Max(u[Ue.MouthFrownLeft], u[Ue.MouthStretchLeft]);
        private static float SadRight(UnifiedFace u) => Math.Max(u[Ue.MouthFrownRight], u[Ue.MouthStretchRight]);
        private static float BrowDownLeft(UnifiedFace u) => u[Ue.BrowLowererLeft] * .75f + u[Ue.BrowPinchLeft] * .25f;
        private static float BrowDownRight(UnifiedFace u) => u[Ue.BrowLowererRight] * .75f + u[Ue.BrowPinchRight] * .25f;
        private static float BrowUpLeft(UnifiedFace u) => u[Ue.BrowOuterUpLeft] * .6f + u[Ue.BrowInnerUpLeft] * .4f;
        private static float BrowUpRight(UnifiedFace u) => u[Ue.BrowOuterUpRight] * .6f + u[Ue.BrowInnerUpRight] * .4f;
        private static float Avg(UnifiedFace u, Ue a, Ue b) => (u[a] + u[b]) / 2f;
        private static float Avg(UnifiedFace u, Ue a, Ue b, Ue c, Ue d) => (u[a] + u[b] + u[c] + u[d]) / 4f;

        private static float PupilDilation(UnifiedFace u)
        {
            float average = (u.LeftPupil + u.RightPupil) / 2f;
            float normalized = (average - u.MinDilation) / (u.MaxDilation - u.MinDilation);
            return float.IsNaN(normalized) ? .5f : normalized;
        }

        private static readonly Dictionary<string, Func<UnifiedFace, float>> Combined = new Dictionary<string, Func<UnifiedFace, float>>(StringComparer.Ordinal)
        {
            ["EyeX"] = u => (u.LeftGaze.x + u.RightGaze.x) / 2f, ["EyeY"] = u => (u.LeftGaze.y + u.RightGaze.y) / 2f,
            ["EyeLeftX"] = u => u.LeftGaze.x, ["EyeLeftY"] = u => u.LeftGaze.y, ["EyeRightX"] = u => u.RightGaze.x, ["EyeRightY"] = u => u.RightGaze.y,
            ["PupilDilation"] = PupilDilation,
            ["PupilDiameterLeft"] = u => u.LeftPupil * .1f, ["PupilDiameterRight"] = u => u.RightPupil * .1f, ["PupilDiameter"] = u => (u.LeftPupil + u.RightPupil) * .05f,
            ["EyeOpenLeft"] = u => u.LeftOpenness, ["EyeOpenRight"] = u => u.RightOpenness, ["EyeOpen"] = u => (u.LeftOpenness + u.RightOpenness) / 2f,
            ["EyeClosedLeft"] = u => 1f - u.LeftOpenness, ["EyeClosedRight"] = u => 1f - u.RightOpenness, ["EyeClosed"] = u => 1f - (u.LeftOpenness + u.RightOpenness) / 2f,
            ["EyeWide"] = u => Math.Max(u[Ue.EyeWideLeft], u[Ue.EyeWideRight]),
            ["EyeLidLeft"] = u => u.LeftOpenness * .75f + u[Ue.EyeWideLeft] * .25f,
            ["EyeLidRight"] = u => u.RightOpenness * .75f + u[Ue.EyeWideRight] * .25f,
            ["EyeLid"] = u => (u.LeftOpenness + u.RightOpenness) / 2f * .75f + Avg(u, Ue.EyeWideRight, Ue.EyeWideLeft) * .25f,
            ["EyeSquint"] = u => Math.Max(u[Ue.EyeSquintLeft], u[Ue.EyeSquintRight]), ["EyesSquint"] = u => Math.Max(u[Ue.EyeSquintLeft], u[Ue.EyeSquintRight]),
            ["BrowUp"] = u => (BrowUpRight(u) + BrowUpLeft(u)) * .5f, ["BrowDown"] = u => (BrowDownRight(u) + BrowDownLeft(u)) * .5f,
            ["BrowInnerUp"] = u => Avg(u, Ue.BrowInnerUpLeft, Ue.BrowInnerUpRight), ["BrowOuterUp"] = u => Avg(u, Ue.BrowOuterUpLeft, Ue.BrowOuterUpRight),
            // -1 angry, +1 worried.
            ["BrowExpressionRight"] = u => Math.Min(1f, u[Ue.BrowInnerUpRight] * .5f + u[Ue.BrowOuterUpRight] * .5f) - BrowDownRight(u),
            ["BrowExpressionLeft"] = u => Math.Min(1f, u[Ue.BrowInnerUpLeft] * .5f + u[Ue.BrowOuterUpLeft] * .5f) - BrowDownLeft(u),
            ["BrowExpression"] = u => (Math.Min(1f, (u[Ue.BrowInnerUpRight] + u[Ue.BrowOuterUpRight]) * .5f) - BrowDownRight(u) +
                                       Math.Min(1f, (u[Ue.BrowInnerUpLeft] + u[Ue.BrowOuterUpLeft]) * .5f) - BrowDownLeft(u)) * .5f,
            ["JawX"] = u => u[Ue.JawRight] - u[Ue.JawLeft], ["JawZ"] = u => u[Ue.JawForward] - u[Ue.JawBackward],
            ["CheekSquint"] = u => Avg(u, Ue.CheekSquintLeft, Ue.CheekSquintRight),
            ["CheekPuffSuckLeft"] = u => u[Ue.CheekPuffLeft] - u[Ue.CheekSuckLeft], ["CheekPuffSuckRight"] = u => u[Ue.CheekPuffRight] - u[Ue.CheekSuckRight],
            ["CheekPuffSuck"] = u => Avg(u, Ue.CheekPuffRight, Ue.CheekPuffLeft) - Avg(u, Ue.CheekSuckRight, Ue.CheekSuckLeft),
            ["CheekSuck"] = u => Avg(u, Ue.CheekSuckLeft, Ue.CheekSuckRight),
            ["MouthUpperX"] = u => u[Ue.MouthUpperRight] - u[Ue.MouthUpperLeft], ["MouthLowerX"] = u => u[Ue.MouthLowerRight] - u[Ue.MouthLowerLeft],
            ["MouthX"] = u => Avg(u, Ue.MouthUpperRight, Ue.MouthLowerRight) - Avg(u, Ue.MouthUpperLeft, Ue.MouthLowerLeft),
            ["LipSuckUpper"] = u => Avg(u, Ue.LipSuckUpperRight, Ue.LipSuckUpperLeft), ["LipSuckLower"] = u => Avg(u, Ue.LipSuckLowerRight, Ue.LipSuckLowerLeft),
            ["LipSuck"] = u => Avg(u, Ue.LipSuckUpperRight, Ue.LipSuckUpperLeft, Ue.LipSuckLowerRight, Ue.LipSuckLowerLeft),
            ["LipFunnelUpper"] = u => Avg(u, Ue.LipFunnelUpperRight, Ue.LipFunnelUpperLeft), ["LipFunnelLower"] = u => Avg(u, Ue.LipFunnelLowerRight, Ue.LipFunnelLowerLeft),
            ["LipFunnel"] = u => Avg(u, Ue.LipFunnelUpperRight, Ue.LipFunnelUpperLeft, Ue.LipFunnelLowerRight, Ue.LipFunnelLowerLeft),
            ["LipPuckerUpper"] = u => Avg(u, Ue.LipPuckerUpperRight, Ue.LipPuckerUpperLeft), ["LipPuckerLower"] = u => Avg(u, Ue.LipPuckerLowerRight, Ue.LipPuckerLowerLeft),
            ["LipPuckerRight"] = u => Avg(u, Ue.LipPuckerUpperRight, Ue.LipPuckerLowerRight), ["LipPuckerLeft"] = u => Avg(u, Ue.LipPuckerUpperLeft, Ue.LipPuckerLowerLeft),
            ["LipPucker"] = u => Avg(u, Ue.LipPuckerUpperRight, Ue.LipPuckerUpperLeft, Ue.LipPuckerLowerRight, Ue.LipPuckerLowerLeft),
            ["LipSuckFunnelUpper"] = u => Avg(u, Ue.LipSuckUpperRight, Ue.LipSuckUpperLeft) - Avg(u, Ue.LipFunnelUpperRight, Ue.LipFunnelUpperLeft),
            ["LipSuckFunnelLower"] = u => Avg(u, Ue.LipSuckLowerRight, Ue.LipSuckLowerLeft) - Avg(u, Ue.LipFunnelLowerRight, Ue.LipFunnelLowerLeft),
            ["LipSuckFunnelLowerLeft"] = u => u[Ue.LipSuckLowerLeft] - u[Ue.LipFunnelLowerLeft], ["LipSuckFunnelLowerRight"] = u => u[Ue.LipSuckLowerRight] - u[Ue.LipFunnelLowerRight],
            ["LipSuckFunnelUpperLeft"] = u => u[Ue.LipSuckUpperLeft] - u[Ue.LipFunnelUpperLeft], ["LipSuckFunnelUpperRight"] = u => u[Ue.LipSuckUpperRight] - u[Ue.LipFunnelUpperRight],
            ["MouthUpperUp"] = u => u[Ue.MouthUpperUpRight] * .5f + u[Ue.MouthUpperUpLeft] * .5f,
            ["MouthLowerDown"] = u => u[Ue.MouthLowerDownRight] * .5f + u[Ue.MouthLowerDownLeft] * .5f,
            ["MouthOpen"] = u => (u[Ue.MouthUpperUpRight] + u[Ue.MouthUpperUpLeft] + u[Ue.MouthLowerDownRight] + u[Ue.MouthLowerDownLeft]) * .25f,
            ["MouthStretch"] = u => Avg(u, Ue.MouthStretchRight, Ue.MouthStretchLeft), ["MouthTightener"] = u => Avg(u, Ue.MouthTightenerRight, Ue.MouthTightenerLeft),
            ["MouthPress"] = u => Avg(u, Ue.MouthPressRight, Ue.MouthPressLeft), ["MouthDimple"] = u => Avg(u, Ue.MouthDimpleRight, Ue.MouthDimpleLeft),
            ["NoseSneer"] = u => Avg(u, Ue.NoseSneerRight, Ue.NoseSneerLeft),
            ["MouthTightenerStretch"] = u => Avg(u, Ue.MouthTightenerRight, Ue.MouthTightenerLeft) - Avg(u, Ue.MouthStretchRight, Ue.MouthStretchLeft),
            ["MouthTightenerStretchLeft"] = u => u[Ue.MouthTightenerLeft] - u[Ue.MouthStretchLeft], ["MouthTightenerStretchRight"] = u => u[Ue.MouthTightenerRight] - u[Ue.MouthStretchRight],
            ["MouthCornerYLeft"] = u => u[Ue.MouthCornerSlantLeft] - u[Ue.MouthFrownLeft], ["MouthCornerYRight"] = u => u[Ue.MouthCornerSlantRight] - u[Ue.MouthFrownRight],
            ["MouthCornerY"] = u => (u[Ue.MouthCornerSlantLeft] - u[Ue.MouthFrownLeft] + u[Ue.MouthCornerSlantRight] - u[Ue.MouthFrownRight]) * .5f,
            ["SmileFrownRight"] = u => SmileRight(u) - u[Ue.MouthFrownRight], ["SmileFrownLeft"] = u => SmileLeft(u) - u[Ue.MouthFrownLeft],
            ["SmileFrown"] = u => SmileRight(u) * .5f + SmileLeft(u) * .5f - u[Ue.MouthFrownRight] * .5f - u[Ue.MouthFrownLeft] * .5f,
            ["SmileSadRight"] = u => SmileRight(u) - SadRight(u), ["SmileSadLeft"] = u => SmileLeft(u) - SadLeft(u),
            ["SmileSad"] = u => (SmileLeft(u) + SmileRight(u)) / 2f - (SadLeft(u) + SadRight(u)) / 2f,
            ["TongueX"] = u => u[Ue.TongueRight] - u[Ue.TongueLeft], ["TongueY"] = u => u[Ue.TongueUp] - u[Ue.TongueDown],
            ["TongueArchY"] = u => u[Ue.TongueCurlUp] - u[Ue.TongueBendDown], ["TongueShape"] = u => u[Ue.TongueFlat] - u[Ue.TongueSquish],
        };

        // Simplified shapes, sent like raw shapes (bool threshold 0).
        private static readonly Dictionary<string, Func<UnifiedFace, float>> Simple = new Dictionary<string, Func<UnifiedFace, float>>(StringComparer.Ordinal)
        {
            ["MouthSmileLeft"] = SmileLeft, ["MouthSmileRight"] = SmileRight, ["MouthSadLeft"] = SadLeft, ["MouthSadRight"] = SadRight,
            ["BrowDownLeft"] = BrowDownLeft, ["BrowDownRight"] = BrowDownRight, ["BrowUpLeft"] = BrowUpLeft, ["BrowUpRight"] = BrowUpRight,
        };

        private static readonly Dictionary<string, Ue> Raw = Enum.GetValues(typeof(Ue)).Cast<Ue>().Where(s => s != Ue.Max).ToDictionary(s => s.ToString(), s => s, StringComparer.Ordinal);

        /// <summary>The function of a "v2/" parameter name (without the prefix), or null when VRCFaceTracking does not send it.</summary>
        internal static Func<UnifiedFace, float> Parameter(string v2, out bool raw)
        {
            raw = false;
            if (Combined.TryGetValue(v2, out var combined)) return combined;
            raw = true;
            if (Simple.TryGetValue(v2, out var simple)) return simple;
            if (Raw.TryGetValue(v2, out var shape)) return u => u[shape];
            return null;
        }

        /// <summary>
        /// One bit of a binary parameter (OSC/DataTypes/BinaryBaseParameter.cs, release 5.2.3.0): the magnitude on
        /// <paramref name="bits"/> bits, a negative value only when the avatar has the "Negative" bool.
        /// </summary>
        internal static bool Bit(float value, int index, int bits, bool hasNegative)
        {
            if (!hasNegative && value < 0f) return false;
            int big = (int)(Math.Abs(value) * ((1 << bits) - 1));
            return ((big >> index) & 1) == 1;
        }
    }

    /// <summary>
    /// The parameters VRCFaceTracking drives on one avatar, found like VRCFaceTracking finds them in the avatar's OSC
    /// configuration (its expression parameters): any name ending in "/v2/Name" (OSC/DataTypes/BaseParameter.cs),
    /// binary bools "Name1", "Name2", "Name4"... with "NameNegative", and the tracking state bools.
    /// </summary>
    internal sealed class VrcftAvatarParameters
    {
        private enum Kind { Float, Bool, Binary, Negative, EyeActive, LipActive }

        private struct Binding
        {
            public string Name;
            public Kind Kind;
            public Func<UnifiedFace, float> Value;
            public bool Raw, HasNegative;
            public int Bit, Bits;
        }

        private static readonly Regex V2 = new Regex(@"(?:^|/)v2/([A-Za-z]+?)(\d+|Negative)?$", RegexOptions.CultureInvariant);
        private readonly List<Binding> bindings = new List<Binding>();

        public IEnumerable<string> Names => bindings.Select(b => b.Name);
        public int Count => bindings.Count;

        public VrcftAvatarParameters(IEnumerable<(string name, VRCExpressionParameters.ValueType type)> parameters)
        {
            var list = parameters.Where(p => !string.IsNullOrEmpty(p.name)).GroupBy(p => p.name).Select(g => g.First()).ToList();
            var binaries = new Dictionary<string, List<(string name, int bit)>>(StringComparer.Ordinal);
            var negatives = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, type) in list)
            {
                if (name == FaceTrackingFeatureSet.EyeTrackingActive || name.EndsWith("/" + FaceTrackingFeatureSet.EyeTrackingActive, StringComparison.Ordinal))
                { if (type == VRCExpressionParameters.ValueType.Bool) bindings.Add(new Binding { Name = name, Kind = Kind.EyeActive }); continue; }
                if (name == FaceTrackingFeatureSet.LipTrackingActive || name == "ExpressionTrackingActive" ||
                    name.EndsWith("/" + FaceTrackingFeatureSet.LipTrackingActive, StringComparison.Ordinal) || name.EndsWith("/ExpressionTrackingActive", StringComparison.Ordinal))
                { if (type == VRCExpressionParameters.ValueType.Bool) bindings.Add(new Binding { Name = name, Kind = Kind.LipActive }); continue; }
                var match = V2.Match(name);
                if (!match.Success) continue;
                string v2 = match.Groups[1].Value, suffix = match.Groups[2].Value;
                var value = Vrcft.Parameter(v2, out bool raw);
                if (value == null)
                {
                    // A name ending in digits that is itself a parameter ("EyeLeftX" has none), else nothing VRCFaceTracking knows.
                    value = Vrcft.Parameter(v2 + suffix, out raw);
                    if (value == null) continue;
                    v2 += suffix; suffix = "";
                }
                if (type == VRCExpressionParameters.ValueType.Float && suffix.Length == 0) bindings.Add(new Binding { Name = name, Kind = Kind.Float, Value = value });
                else if (type != VRCExpressionParameters.ValueType.Bool) continue;
                else if (suffix == "Negative") { negatives.Add(v2); bindings.Add(new Binding { Name = name, Kind = Kind.Negative, Value = value }); }
                else if (suffix.Length > 0)
                {
                    // Steps are powers of two: the bit is their logarithm.
                    int step = int.Parse(suffix);
                    if (step <= 0 || (step & (step - 1)) != 0) continue;
                    int bit = 0; while ((1 << bit) < step) bit++;
                    if (!binaries.TryGetValue(v2, out var bits)) binaries[v2] = bits = new List<(string, int)>();
                    bits.Add((name, bit));
                    bindings.Add(new Binding { Name = name, Kind = Kind.Binary, Value = value, Bit = bit });
                }
                else bindings.Add(new Binding { Name = name, Kind = Kind.Bool, Value = value, Raw = raw });
            }
            for (int i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];
                if (binding.Kind != Kind.Binary) continue;
                var match = V2.Match(binding.Name);
                string v2 = match.Groups[1].Value;
                // The number of steps found, not the largest.
                binding.Bits = binaries[v2].Count;
                binding.HasNegative = negatives.Contains(v2);
                bindings[i] = binding;
            }
        }

        public static VrcftAvatarParameters For(VRCExpressionParameters parameters) =>
            new VrcftAvatarParameters(((parameters != null ? parameters.parameters : null) ?? Array.Empty<VRCExpressionParameters.Parameter>())
                .Where(p => p != null).Select(p => (p.name, p.valueType)));

        /// <summary>Sets every parameter (bools as 0 or 1). Floats are clamped like VRChat clamps them.</summary>
        public void Compute(UnifiedFace face, bool eyeActive, bool lipActive, Action<string, float> set, Func<string, bool> include = null)
        {
            foreach (var b in bindings)
            {
                if (include != null && !include(b.Name)) continue;
                switch (b.Kind)
                {
                    case Kind.EyeActive: set(b.Name, eyeActive ? 1f : 0f); break;
                    case Kind.LipActive: set(b.Name, lipActive ? 1f : 0f); break;
                    case Kind.Float: set(b.Name, Mathf.Clamp(b.Value(face), -1f, 1f)); break;
                    // A plain bool is true below its threshold (Params/DataTypes/ParamContainers.cs): 0 for shapes, else 0.5.
                    case Kind.Bool: set(b.Name, b.Value(face) < (b.Raw ? 0f : .5f) ? 1f : 0f); break;
                    case Kind.Negative: set(b.Name, b.Value(face) < 0f ? 1f : 0f); break;
                    case Kind.Binary: set(b.Name, Vrcft.Bit(b.Value(face), b.Bit, b.Bits, b.HasNegative) ? 1f : 0f); break;
                }
            }
        }
    }
}
