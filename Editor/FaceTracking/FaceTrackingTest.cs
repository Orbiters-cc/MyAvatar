using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// Testing face tracking without Play Mode: the avatar built like an upload (<see cref="FaceTrackingTestBuild"/>), an
    /// iPhone app or the simulator as the face, VRCFaceTracking's mapping and parameters (<see cref="Vrcft"/>), the built
    /// layers played every editor frame (<see cref="FaceTrackingTestRig"/>) and the face shown up close
    /// (<see cref="FaceTrackingStage"/>). One test runs at a time; it stops when no face tracking section shows it.
    /// </summary>
    internal sealed class FaceTrackingTest
    {
        internal enum Phase { Building, Waiting, Live, Failed }

        private const string SourceKey = "Orbiters.MyAvatar.FaceTracking.Source", PhoneKey = "Orbiters.MyAvatar.FaceTracking.Phone";
        // VRChat's frame rate as a ceiling: the layers' smoothing depends on the frame time, as in game.
        private const double FrameSeconds = 1.0 / 90.0;

        private static FaceTrackingTest current;
        /// <summary>The running test, or null.</summary>
        internal static FaceTrackingTest Current => current;
        /// <summary>The phase, the source or the build's progress changed.</summary>
        internal static event Action Changed;
        /// <summary>A new image of the face is ready.</summary>
        internal static event Action Rendered;

        public readonly MyAvatar Avatar;
        public readonly GameObject Root;
        public Phase State { get; private set; }
        public string Error { get; private set; }
        public FaceApp Source { get; private set; }
        public FaceTrackingTestBuild Build { get; private set; }
        public FaceTrackingStage Stage { get; private set; }
        public FaceTrackingReceiver Receiver { get; private set; }
        /// <summary>The avatar changed since the build: the test shows the avatar as it was.</summary>
        public bool Stale { get; private set; }
        /// <summary>A setting only a new build shows changed (the expressive mouth).</summary>
        public bool NeedsBuild { get; private set; }
        public readonly FaceFrame Frame = new FaceFrame();
        public readonly UnifiedFace Face = new UnifiedFace();
        public bool HasFace { get; private set; }
        public int Width = 480, Height = 300;
        public FaceTrackingFeatures Features { get; private set; } = FaceTrackingFeatures.All;
        public FaceTrackingSmoothing Smoothing { get; private set; } = FaceTrackingSmoothing.Balanced;
        /// <summary>The parameters VRCFaceTracking drives on this avatar that the chosen features keep.</summary>
        public int Driven { get; private set; }

        private FaceTrackingTestRig rig;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HashSet<object> viewers = new HashSet<object>();
        private double lastFrame, lastCheck, simulatorStart, unwatched = -1;
        private bool eyesActive, lipActive;

        private FaceTrackingTest(MyAvatar avatar, GameObject root, MyAvatarFaceTracking marker)
        {
            Avatar = avatar; Root = root;
            Source = (FaceApp)EditorPrefs.GetInt(SourceKey, (int)FaceApp.IFacialMocap);
            if (Source == FaceApp.None) Source = FaceApp.IFacialMocap;
            if (marker != null) { Features = marker.synced; Smoothing = marker.smoothing; }
        }

        /// <summary>The phone running iFacialMocap, as typed last time.</summary>
        internal static string PhoneAddress
        {
            get => EditorPrefs.GetString(PhoneKey, "");
            set => EditorPrefs.SetString(PhoneKey, value ?? "");
        }

        /// <summary>Starts testing <paramref name="root"/> (or keeps the test running on it), shown by <paramref name="viewer"/>.</summary>
        internal static FaceTrackingTest Start(MyAvatar avatar, GameObject root, MyAvatarFaceTracking marker, object viewer)
        {
            if (current != null && current.Root != root) StopAll();
            if (current == null)
            {
                current = new FaceTrackingTest(avatar, root, marker);
                current.Begin();
            }
            current.Watch(viewer);
            return current;
        }

        /// <summary>Stops the test: the phone link closes, the face stops; the build stays for a quick restart.</summary>
        internal static void StopAll()
        {
            var test = current;
            current = null;
            if (test == null) return;
            test.Dispose();
            Changed?.Invoke();
        }

        public void Watch(object viewer) { viewers.Add(viewer); unwatched = -1; }
        public void Unwatch(object viewer) { viewers.Remove(viewer); if (viewers.Count == 0) unwatched = Now; }

        private double Now => clock.Elapsed.TotalSeconds;

        private void Begin()
        {
            EditorApplication.update += Update;
            Receiver = new FaceTrackingReceiver();
            if (Source != FaceApp.Simulator) StartReceiver();
            Build = FaceTrackingTestBuild.For(Root, Marker);
            State = Phase.Building;
            if (Build.Usable) Ready();
            Changed?.Invoke();
        }

        private MyAvatarFaceTracking Marker => Root != null ? Root.GetComponentInChildren<MyAvatarFaceTracking>(true) : null;

        private void StartReceiver()
        {
            Receiver.Start();
            if (IPAddress.TryParse(PhoneAddress, out var phone)) Receiver.SetPhone(phone);
        }

        /// <summary>Where the face comes from: an iPhone app or the simulator.</summary>
        public void SetSource(FaceApp source)
        {
            if (source == Source) return;
            Source = source;
            EditorPrefs.SetInt(SourceKey, (int)source);
            if (source == FaceApp.Simulator) simulatorStart = Now;
            else if (!Receiver.Running) StartReceiver();
            // The face rests until the new source sends something, as VRCFaceTracking's would.
            HasFace = false;
            if (State == Phase.Live) State = Phase.Waiting;
            Changed?.Invoke();
        }

        /// <summary>The iPhone running iFacialMocap: asked for its frames until they arrive. False when it is not an address.</summary>
        public bool SetPhone(string address)
        {
            address = (address ?? "").Trim();
            if (!IPAddress.TryParse(address, out var phone) || phone.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
            PhoneAddress = address;
            if (!Receiver.Running) StartReceiver();
            Receiver.SetPhone(phone);
            return true;
        }

        /// <summary>
        /// The quick settings, live: the features others see (VRCFaceTracking drives only their parameters) and the
        /// smoothing (the template's Local Smoothing and the slower mouth). The expressive mouth needs a new build.
        /// </summary>
        public void Apply(MyAvatarFaceTracking marker)
        {
            if (marker == null) return;
            bool smoothing = marker.smoothing != Smoothing;
            Features = marker.synced;
            Smoothing = marker.smoothing;
            NeedsBuild = Build != null && Build.Done && Build.Expressive != marker.expressiveMouth;
            if (rig == null) { Changed?.Invoke(); return; }
            if (smoothing && Build.Fx != null && FaceTrackingBuild.SetMouthSlowdown(Build.Fx, FaceTrackingBuild.MouthSlowdown(Smoothing)) > 0)
                CreateRig();
            ApplyToRig();
            Changed?.Invoke();
        }

        private void ApplyToRig()
        {
            rig.Set(FaceTrackingBuild.LocalSmoothingParameter, FaceTrackingBuild.StartingSmoothing(Smoothing));
            // Features left out: their parameters stay where they start, as on an avatar without them.
            foreach (string name in rig.Vrcft.Names)
                if (!FaceTrackingFeatureSet.Kept(name, Features)) rig.Reset(name);
            Driven = rig.Vrcft.Names.Count(n => FaceTrackingFeatureSet.Kept(n, Features));
        }

        /// <summary>Builds the avatar again (it changed, or a setting only a build shows).</summary>
        public void Rebuild()
        {
            DisposeView();
            FaceTrackingTestBuild.Release();
            Stale = NeedsBuild = false;
            Build = FaceTrackingTestBuild.For(Root, Marker);
            State = Phase.Building;
            Error = null;
            Changed?.Invoke();
        }

        private void Ready()
        {
            try
            {
                CreateRig();
                Stage = new FaceTrackingStage(Build.Scene, Build.Copy, Build.Descriptor);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Fail("Could not play the built avatar: " + ex.Message);
                return;
            }
            Apply(Marker);
            State = HasFace ? Phase.Live : Phase.Waiting;
            if (Source == FaceApp.Simulator) simulatorStart = Now;
            lastFrame = Now;
            Render(0f);
        }

        private void CreateRig()
        {
            rig?.Dispose();
            rig = new FaceTrackingTestRig(Build.Copy, Build.Descriptor, Build.Additive, Build.Fx);
            ApplyToRig();
            eyesActive = lipActive = false;
        }

        private void Fail(string message)
        {
            Error = message;
            State = Phase.Failed;
            DisposeView();
            Changed?.Invoke();
        }

        private void Update()
        {
            if (current != this) { EditorApplication.update -= Update; return; }
            // No section shows the test any more (the Inspector moved on): stop it.
            if (unwatched >= 0 && Now - unwatched > 2.0) { StopAll(); return; }
            if (Root == null) { StopAll(); return; }
            if (State == Phase.Building)
            {
                // Quick steps run together (Unity in the background updates seldom); a long one (VRCFury's) runs alone.
                var budget = Stopwatch.StartNew();
                bool stepped = false;
                while (budget.ElapsedMilliseconds < 40 && Build.StepOnce()) stepped = true;
                if (stepped && !Build.Done && Build.Error == null) { Changed?.Invoke(); return; }
                if (Build.Error != null) { Fail(Build.Error); return; }
                if (Build.Done) { Ready(); Changed?.Invoke(); }
                return;
            }
            if (State == Phase.Failed) return;
            if (!Build.Usable) { Fail("The test's build was removed (another build or upload replaced VRCFury's files). Test again to rebuild."); return; }
            Receiver.Tick();
            double now = Now;
            if (now - lastCheck > 2.0)
            {
                lastCheck = now;
                bool stale = FaceTrackingTestBuild.SignatureOf(Root) != Build.Signature;
                if (stale != Stale) { Stale = stale; Changed?.Invoke(); }
            }
            if (now - lastFrame < FrameSeconds) return;
            float seconds = Mathf.Clamp((float)(now - lastFrame), .001f, .1f);
            lastFrame = now;
            Read();
            Render(seconds);
        }

        // The latest frame of the chosen source through VRCFaceTracking's module, correctors and parameters.
        private void Read()
        {
            // Frames from the other app before any face: that is the app the user started.
            if (!HasFace && Source != FaceApp.Simulator && Receiver.App != FaceApp.None && Receiver.App != Source && Receiver.Age < .5) SetSource(Receiver.App);
            bool fresh;
            if (Source == FaceApp.Simulator) { FaceTrackingSimulator.Fill(Now - simulatorStart, Frame); fresh = true; }
            else fresh = Receiver.App == Source && Receiver.TryRead(Frame);
            if (!fresh) return;
            if (!HasFace) { HasFace = true; State = Phase.Live; Changed?.Invoke(); }
            Vrcft.From(Frame, Face);
            // The module stays active once it started: VRCFaceTracking keeps the last values when the phone pauses.
            eyesActive = lipActive = true;
            rig.Vrcft.Compute(Face, eyesActive, lipActive, rig.Set, name => FaceTrackingFeatureSet.Kept(name, Features));
        }

        private void Render(float seconds)
        {
            if (rig == null || Stage == null) return;
            rig.Evaluate(seconds);
            Stage.Render(Width, Height);
            Rendered?.Invoke();
        }

        /// <summary>A parameter as the built avatar's layers read it now.</summary>
        public float Parameter(string name) => rig != null ? rig.Get(name) : 0f;

        public bool HasParameter(string name) => rig != null && rig.Has(name);

        /// <summary>The simulator's expression playing now.</summary>
        public string SimulatorBeat => FaceTrackingSimulator.Name(Now - simulatorStart);

        /// <summary>The avatar has a parameter VRCFaceTracking drives for this "v2/" name (float or binary).</summary>
        public bool Drives(string v2) => rig != null && rig.Vrcft.Names.Any(n => n.EndsWith("v2/" + v2, StringComparison.Ordinal) ||
            (n.Contains("v2/" + v2) && char.IsDigit(n[n.Length - 1])) || n.EndsWith("v2/" + v2 + "Negative", StringComparison.Ordinal));

        private void DisposeView()
        {
            rig?.Dispose(); rig = null;
            Stage?.Dispose(); Stage = null;
        }

        private void Dispose()
        {
            EditorApplication.update -= Update;
            Receiver?.Dispose();
            DisposeView();
            if (Build != null && !Build.Done) FaceTrackingTestBuild.Release();
            else FaceTrackingTestBuild.Idle();
        }
    }
}
