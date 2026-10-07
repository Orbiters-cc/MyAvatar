using System;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// A phone-free performance for the test: a looping sequence of expressions with blinks and glances, sent through the
    /// same path as a phone's frames (VRCFaceTracking's Live Link mapping, the parameters, the avatar's FX). Its eyes are
    /// degrees, full gaze at 30 (<see cref="Vrcft.FromSimulator"/>).
    /// </summary>
    internal static class FaceTrackingSimulator
    {
        private struct Beat
        {
            public string Name;
            public float Length;
            public Action<FaceFrame, float, float> Pose; // frame, weight (0 to 1, eased in and out), seconds into the beat
        }

        private static readonly Beat[] Beats =
        {
            new Beat { Name = "Neutral", Length = 1.6f, Pose = (f, w, t) => { } },
            new Beat { Name = "Smile", Length = 2.4f, Pose = (f, w, t) =>
            {
                f[ArKit.MouthSmileLeft] = f[ArKit.MouthSmileRight] = .95f * w;
                f[ArKit.CheekSquintLeft] = f[ArKit.CheekSquintRight] = .45f * w;
                f[ArKit.EyeSquintLeft] = f[ArKit.EyeSquintRight] = .3f * w;
                f[ArKit.MouthUpperUpLeft] = f[ArKit.MouthUpperUpRight] = .25f * w;
            } },
            new Beat { Name = "Talking", Length = 3f, Pose = (f, w, t) =>
            {
                float syllable = Mathf.Abs(Mathf.Sin(t * 9.5f)) * (.55f + .45f * Mathf.Sin(t * 2.3f));
                f[ArKit.JawOpen] = .45f * syllable * w;
                f[ArKit.MouthLowerDownLeft] = f[ArKit.MouthLowerDownRight] = .3f * syllable * w;
                float round = Mathf.Clamp01(Mathf.Sin(t * 3.1f + 1f));
                f[ArKit.MouthFunnel] = .5f * round * w;
                f[ArKit.MouthPucker] = .35f * Mathf.Clamp01(-Mathf.Sin(t * 3.1f + 1f)) * w;
                f[ArKit.MouthSmileLeft] = f[ArKit.MouthSmileRight] = .2f * w;
                f[ArKit.BrowInnerUp] = .25f * Mathf.Clamp01(Mathf.Sin(t * 1.7f)) * w;
            } },
            new Beat { Name = "Looking around", Length = 2.8f, Pose = (f, w, t) =>
            {
                float yaw = Mathf.Sin(t * 2.2f) * 26f * w, pitch = Mathf.Sin(t * 1.4f + .6f) * 14f * w;
                Look(f, yaw, pitch);
            } },
            new Beat { Name = "Surprised", Length = 2.2f, Pose = (f, w, t) =>
            {
                f[ArKit.EyeWideLeft] = f[ArKit.EyeWideRight] = .85f * w;
                f[ArKit.BrowInnerUp] = .9f * w;
                f[ArKit.BrowOuterUpLeft] = f[ArKit.BrowOuterUpRight] = .75f * w;
                f[ArKit.JawOpen] = .55f * w;
                f[ArKit.MouthFunnel] = .3f * w;
            } },
            new Beat { Name = "Sad", Length = 2.2f, Pose = (f, w, t) =>
            {
                f[ArKit.MouthFrownLeft] = f[ArKit.MouthFrownRight] = .85f * w;
                f[ArKit.MouthStretchLeft] = f[ArKit.MouthStretchRight] = .25f * w;
                f[ArKit.MouthShrugLower] = .45f * w;
                f[ArKit.BrowInnerUp] = .6f * w;
                f[ArKit.BrowDownLeft] = f[ArKit.BrowDownRight] = .2f * w;
                Look(f, 0f, -10f * w);
            } },
            new Beat { Name = "Cheeks puffed", Length = 2f, Pose = (f, w, t) =>
            {
                f[ArKit.CheekPuff] = .9f * w;
                f[ArKit.MouthClose] = .25f * w;
                f[ArKit.MouthPucker] = .3f * w;
                f[ArKit.EyeWideLeft] = f[ArKit.EyeWideRight] = .25f * w;
            } },
            new Beat { Name = "Tongue out", Length = 2.2f, Pose = (f, w, t) =>
            {
                f[ArKit.JawOpen] = .4f * w;
                f[ArKit.TongueOut] = .95f * w;
                f[ArKit.MouthSmileLeft] = .35f * w;
                f[ArKit.EyeBlinkRight] = .6f * w;
            } },
            new Beat { Name = "Kiss and wink", Length = 2.4f, Pose = (f, w, t) =>
            {
                f[ArKit.MouthPucker] = .95f * w;
                f[ArKit.MouthFunnel] = .2f * w;
                float wink = Mathf.Clamp01((t - .5f) * 4f) * Mathf.Clamp01((1.9f - t) * 4f);
                f[ArKit.EyeBlinkLeft] = wink;
                f[ArKit.CheekSquintLeft] = .4f * wink;
                f[ArKit.MouthLeft] = .3f * w;
            } },
            new Beat { Name = "Smirk", Length = 2.2f, Pose = (f, w, t) =>
            {
                f[ArKit.MouthSmileRight] = .9f * w;
                f[ArKit.MouthDimpleRight] = .5f * w;
                f[ArKit.MouthRight] = .35f * w;
                f[ArKit.JawRight] = .25f * w;
                f[ArKit.BrowOuterUpLeft] = .5f * w;
                f[ArKit.EyeSquintRight] = .3f * w;
                Look(f, -14f * w, 4f * w);
            } },
        };

        /// <summary>The length of the loop, in seconds.</summary>
        internal static float Length
        {
            get { float sum = 0f; foreach (var beat in Beats) sum += beat.Length; return sum; }
        }

        /// <summary>The expression playing at <paramref name="time"/>.</summary>
        internal static string Name(double time) => Find(time, out _).Name;

        /// <summary>Fills <paramref name="frame"/> with the performance at <paramref name="time"/> (seconds).</summary>
        internal static void Fill(double time, FaceFrame frame)
        {
            frame.Clear();
            frame.App = FaceApp.Simulator;
            var beat = Find(time, out float at);
            // Each beat eases in over its first 0.35 s and out over its last 0.35 s.
            float weight = Smooth(Mathf.Clamp01(at / .35f)) * Smooth(Mathf.Clamp01((beat.Length - at) / .35f));
            beat.Pose(frame, weight, at);
            // Small idle glances, and a blink every 3.1 s (none during a wink).
            float t = (float)time;
            if (Mathf.Approximately(frame.LeftEye.y, 0f) && Mathf.Approximately(frame.LeftEye.x, 0f))
                Look(frame, Mathf.Sin(t * .9f) * 4f + Mathf.Sin(t * 2.7f) * 1.5f, Mathf.Sin(t * .7f + 1f) * 2.5f);
            float phase = t % 3.1f;
            float blink = phase < .16f ? Mathf.Sin(phase / .16f * Mathf.PI) : 0f;
            if (frame[ArKit.EyeBlinkLeft] < .5f && frame[ArKit.EyeBlinkRight] < .5f)
            {
                frame[ArKit.EyeBlinkLeft] = Mathf.Max(frame[ArKit.EyeBlinkLeft], blink);
                frame[ArKit.EyeBlinkRight] = Mathf.Max(frame[ArKit.EyeBlinkRight], blink);
            }
            frame.Head = new Vector3(Mathf.Sin(t * .5f) * 3f, Mathf.Sin(t * .37f) * 5f, 0f);
        }

        // Where the eyes look, in degrees (yaw to the right, pitch up), as ARKit's look shapes and Live Link's eye angles.
        private static void Look(FaceFrame f, float yaw, float pitch)
        {
            float right = Mathf.Clamp01(yaw / 30f), left = Mathf.Clamp01(-yaw / 30f), up = Mathf.Clamp01(pitch / 25f), down = Mathf.Clamp01(-pitch / 25f);
            // Looking right turns the left eye in and the right eye out.
            f[ArKit.EyeLookInLeft] = right; f[ArKit.EyeLookOutRight] = right;
            f[ArKit.EyeLookOutLeft] = left; f[ArKit.EyeLookInRight] = left;
            f[ArKit.EyeLookUpLeft] = f[ArKit.EyeLookUpRight] = up;
            f[ArKit.EyeLookDownLeft] = f[ArKit.EyeLookDownRight] = down;
            // Pitch positive looking down, as the apps send it.
            f.LeftEye = f.RightEye = new Vector3(-pitch, yaw, 0f);
        }

        private static float Smooth(float x) => x * x * (3f - 2f * x);

        private static Beat Find(double time, out float at)
        {
            double loop = Length;
            double t = ((time % loop) + loop) % loop;
            foreach (var beat in Beats)
            {
                if (t < beat.Length) { at = (float)t; return beat; }
                t -= beat.Length;
            }
            at = 0f;
            return Beats[0];
        }
    }
}
