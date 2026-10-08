using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// The face tracking My Avatar set up, assembled as an upload would have it, in a second instead of a whole build: a
    /// copy of the avatar in its own preview scene, and in memory only the template's controllers merged the way VRCFury
    /// merges them (its binding rewrites, its parameters), then the steps that change them at upload: My Avatar's
    /// blendshape names and mouth tuning (<see cref="FaceTrackingBuild"/>), and MCB's corrective blendshape links of the
    /// avatar's custom base (e.g. the Orbit Face eyelid fix following the eyelids). The avatar's own layers and the rest
    /// of the build (VRCFury's other features, MCB's version meshes) are left out: the face shows as it is in the scene.
    /// The copy stays (one at a time) while the avatar is unchanged; scripts reloading, Play Mode, quitting or ten idle
    /// minutes release it.
    /// </summary>
    internal sealed class FaceTrackingTestBuild
    {
        private const double IdleRelease = 600;
        // The build steps that also change the template's clips at upload, by type name (MCB is optional).
        private static readonly string[] UploadSteps = { "BlendShapeLinkPostVrcfuryHook" };
        private static FaceTrackingTestBuild cached;
        private static double idleSince = -1;

        public readonly GameObject Source;
        /// <summary>The avatar as the build saw it (measured again once built).</summary>
        public string Signature { get; private set; }
        public readonly bool Expressive;
        public Scene Scene { get; private set; }
        public GameObject Copy { get; private set; }
        public VRCAvatarDescriptor Descriptor { get; private set; }
        public AnimatorController Fx { get; private set; }
        public AnimatorController Additive { get; private set; }
        public string Error { get; private set; }
        public bool Done { get; private set; }
        public double Seconds => clock.Elapsed.TotalSeconds;

        private readonly List<(string label, Action run)> steps = new List<(string, Action)>();
        private readonly List<AnimatorControllerCopy> copies = new List<AnimatorControllerCopy>();
        private readonly List<Object> made = new List<Object>();
        private readonly Stopwatch clock = new Stopwatch();
        private readonly MyAvatarFaceTracking marker;
        private SkinnedMeshRenderer face;
        private string facePath;
        private int next;

        private FaceTrackingTestBuild(GameObject source, MyAvatarFaceTracking marker, string signature, bool expressive)
        {
            Source = source; this.marker = marker; Signature = signature; Expressive = expressive;
            steps.Add(("Copying the face tracking template", Merge));
            steps.Add(("My Avatar tunes the face", Tune));
            foreach (var step in Callbacks()) steps.Add(("MCB links its corrective blendshapes", () =>
            {
                if (!step.OnPreprocessAvatar(Copy)) throw new InvalidOperationException(step.GetType().Name + " stopped.");
            }));
        }

        /// <summary>The cached build of <paramref name="source"/> when it is still the avatar as it is, else a new one started.</summary>
        internal static FaceTrackingTestBuild For(GameObject source, MyAvatarFaceTracking marker)
        {
            string signature = SignatureOf(source);
            bool expressive = marker == null || marker.expressiveMouth;
            if (cached != null && cached.Source == source && cached.Signature == signature && cached.Expressive == expressive && (cached.Usable || !cached.Done && cached.Error == null))
            { idleSince = -1; return cached; }
            Release();
            cached = new FaceTrackingTestBuild(source, marker, signature, expressive);
            cached.Begin();
            return cached;
        }

        /// <summary>A finished build of <paramref name="source"/> is kept (testing starts at once unless the avatar changed).</summary>
        internal static bool Kept(GameObject source) => cached != null && cached.Source == source && cached.Usable;

        public bool Usable => Done && Error == null && Copy != null && Scene.IsValid() && Fx != null;

        public float Progress => Done ? 1f : steps.Count == 0 ? 0f : next / (float)(steps.Count + 1);

        /// <summary>What runs next, in the words of the person waiting.</summary>
        public string Step => Done ? "Ready" : next < steps.Count ? steps[next].label : "Finishing";

        private void Begin()
        {
            clock.Start();
            try
            {
                if (marker == null) throw new InvalidOperationException("the avatar has no face tracking set up by My Avatar");
                Scene = EditorSceneManager.NewPreviewScene();
                // Instantiated under an object of the preview scene: the copy is never in the user's scene, even for a frame.
                var holder = new GameObject("Face tracking test");
                SceneManager.MoveGameObjectToScene(holder, Scene);
                Copy = Object.Instantiate(Source, holder.transform);
                Copy.transform.SetParent(null, false);
                Object.DestroyImmediate(holder);
                Copy.name = Source.name + " (face tracking test)";
                // The VRChat SDK's builder lists every active avatar in a scene but skips objects with exactly these flags.
                Copy.hideFlags = HideFlags.NotEditable;
                Copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                Copy.SetActive(true);
                Descriptor = Copy.GetComponent<VRCAvatarDescriptor>();
                if (Descriptor == null) throw new InvalidOperationException("the avatar has no VRChat avatar descriptor");
            }
            catch (Exception ex) { Fail("Could not copy the avatar: " + ex.Message); }
        }

        /// <summary>Runs the next step. False once there is nothing left to do (done or failed).</summary>
        internal bool StepOnce()
        {
            if (Done || Error != null) return false;
            if (Copy == null) { Fail("The avatar copy was destroyed while building."); return false; }
            if (next >= steps.Count) { Finish(); return false; }
            var (label, run) = steps[next];
            try { run(); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Fail(label + " failed: " + ex.Message);
                return false;
            }
            next++;
            return true;
        }

        private void Finish()
        {
            // Measured again: build steps may mark the avatar's own objects changed without changing them.
            Signature = SignatureOf(Source);
            clock.Stop();
            Done = true;
            Debug.Log($"[My Avatar] Face tracking test: {Source.name}'s face tracking assembled in {Seconds:0.00} s.");
        }

        private void Fail(string message)
        {
            Error = message;
            clock.Stop();
        }

        // ---- The template, merged as VRCFury merges it ----

        // The template's Full Controllers (the face, and the eye rotation): their controllers copied with VRCFury's binding
        // rewrites (the "Body" the template animates pointed at the face where it is now, as at upload), their parameters
        // in one asset.
        private void Merge()
        {
            var markerCopy = Copy.GetComponentInChildren<MyAvatarFaceTracking>(true);
            if (markerCopy == null) throw new InvalidOperationException("the copy has no face tracking template");
            face = FaceTrackingSetup.CurrentFace(marker, Source.transform);
            if (face == null) throw new InvalidOperationException("the face mesh is missing");
            facePath = AnimationUtility.CalculateTransformPath(face.transform, Source.transform);
            FaceTrackingSetup.PointBody(markerCopy.gameObject, facePath, undo: false);
            var parameters = new List<VRCExpressionParameters.Parameter>();
            AnimatorController fx = null, additive = null;
            foreach (var (_, feature, kind) in VrcFury.Features(markerCopy.gameObject))
            {
                if (kind != "FullController") continue;
                var rewrites = Entries(feature, "rewriteBindings")
                    .Select(r => (from: Field<string>(r, "from") ?? "", to: Field<string>(r, "to") ?? "", delete: Field<bool>(r, "delete"))).ToList();
                foreach (var entry in Entries(feature, "controllers"))
                {
                    if (!(VrcFury.ObjectReference(entry.GetType().GetField("controller")?.GetValue(entry)) is AnimatorController source)) continue;
                    var type = (VRCAvatarDescriptor.AnimLayerType)Convert.ToInt32(entry.GetType().GetField("type")?.GetValue(entry) ?? VRCAvatarDescriptor.AnimLayerType.FX);
                    if (type != VRCAvatarDescriptor.AnimLayerType.FX && type != VRCAvatarDescriptor.AnimLayerType.Additive) continue;
                    var copy = AnimatorControllerCopy.Of(source, clip => Rewrite(clip, rewrites));
                    copies.Add(copy);
                    if (type == VRCAvatarDescriptor.AnimLayerType.FX) fx = Join(fx, copy.Controller);
                    else additive = Join(additive, copy.Controller);
                }
                foreach (var entry in Entries(feature, "prms"))
                    if (VrcFury.ObjectReference(entry.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters asset && asset.parameters != null)
                        parameters.AddRange(asset.parameters.Where(p => p != null && !string.IsNullOrEmpty(p.name) && parameters.All(q => q.name != p.name))
                            .Select(p => new VRCExpressionParameters.Parameter { name = p.name, valueType = p.valueType, defaultValue = p.defaultValue, saved = p.saved, networkSynced = p.networkSynced }));
            }
            if (fx == null) throw new InvalidOperationException("the template has no FX controller");
            Fx = fx; Additive = additive;
            var merged = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            merged.hideFlags = HideFlags.HideAndDontSave;
            merged.name = "Face tracking test parameters";
            merged.parameters = parameters.ToArray();
            made.Add(merged);
            // The copy's playable layers are the template's alone: build steps after this one find them as VRCFury's.
            Descriptor.customizeAnimationLayers = true;
            var layers = (Descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Where(l => l.type != VRCAvatarDescriptor.AnimLayerType.FX && l.type != VRCAvatarDescriptor.AnimLayerType.Additive)
                .Select(l => { l.isDefault = true; l.animatorController = null; return l; }).ToList();
            layers.Add(new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = Fx });
            if (Additive != null) layers.Add(new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Additive, animatorController = Additive });
            Descriptor.baseAnimationLayers = layers.ToArray();
            Descriptor.expressionParameters = merged;
            Descriptor.expressionsMenu = null;
        }

        // Several controllers of one playable layer: VRCFury appends the layers and parameters of the next to the first
        // (through the setters: AddParameter and AddLayer would record Undo steps).
        private static AnimatorController Join(AnimatorController into, AnimatorController next)
        {
            if (into == null) return next;
            into.parameters = into.parameters.Concat(next.parameters.Where(n => into.parameters.All(p => p.name != n.name))).ToArray();
            into.layers = into.layers.Concat(next.layers).ToArray();
            return into;
        }

        private void Tune()
        {
            var standard = FaceTrackingStandard.Find(marker.standard);
            if (standard == null) throw new InvalidOperationException("unknown blendshape standard “" + marker.standard + "”");
            FaceTrackingBuild.Finish(Fx, standard, facePath, face.sharedMesh);
            FaceTrackingBuild.Tune(Fx, facePath, face.sharedMesh, Expressive, FaceTrackingBuild.MouthSlowdown(marker.smoothing));
        }

        private static AnimationClip Rewrite(AnimationClip clip, List<(string from, string to, bool delete)> rewrites)
        {
            var copy = Object.Instantiate(clip);
            if (rewrites.Count == 0) return copy;
            foreach (var binding in AnimationUtility.GetCurveBindings(copy))
            {
                string path = binding.path;
                var rule = rewrites.FirstOrDefault(r => r.from.Length == 0 || path == r.from || path.StartsWith(r.from + "/", StringComparison.Ordinal));
                if (rule.from == null) continue;
                string moved = rule.from.Length == 0 ? (rule.to.Length == 0 ? path : rule.to + "/" + path) : rule.to + path.Substring(rule.from.Length);
                if (!rule.delete && moved == path) continue;
                var curve = AnimationUtility.GetEditorCurve(copy, binding);
                AnimationUtility.SetEditorCurve(copy, binding, null);
                if (!rule.delete) AnimationUtility.SetEditorCurve(copy, EditorCurveBinding.FloatCurve(moved, binding.type, binding.propertyName), curve);
            }
            return copy;
        }

        private static IEnumerable<object> Entries(object feature, string field) =>
            VrcFury.Field(feature, field) is IEnumerable list ? list.Cast<object>().Where(e => e != null) : Enumerable.Empty<object>();

        private static T Field<T>(object model, string name) => model.GetType().GetField(name)?.GetValue(model) is T value ? value : default;

        private static IEnumerable<IVRCSDKPreprocessAvatarCallback> Callbacks()
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<IVRCSDKPreprocessAvatarCallback>())
            {
                if (type.IsAbstract || !UploadSteps.Contains(type.Name) || type.GetConstructor(Type.EmptyTypes) == null) continue;
                IVRCSDKPreprocessAvatarCallback step = null;
                try { step = (IVRCSDKPreprocessAvatarCallback)Activator.CreateInstance(type); }
                catch (Exception ex) { Debug.LogWarning("[My Avatar] Face tracking test: could not create " + type.FullName + ": " + ex.Message); }
                if (step != null) yield return step;
            }
        }

        // ---- What makes a build stale ----

        // Objects' serialized contents by instance, measured again only when their dirty count moved: inspectors often mark
        // a component changed without changing it (MCB's own does, on every Inspector).
        private static readonly Dictionary<int, (int dirty, int hash)> Contents = new Dictionary<int, (int, int)>();

        private static int Content(Object value)
        {
            int id = value.GetInstanceID(), dirty = EditorUtility.GetDirtyCount(value);
            if (Contents.TryGetValue(id, out var known) && known.dirty == dirty) return known.hash;
            int hash;
            try { hash = EditorJsonUtility.ToJson(value).GetHashCode(); }
            catch (Exception) { hash = dirty; }
            Contents[id] = (dirty, hash);
            return hash;
        }

        /// <summary>
        /// Changes to the avatar the test would show: its objects and components (not where its bones are). The settings the
        /// test applies live (smoothing, features) are left out.
        /// </summary>
        internal static string SignatureOf(GameObject root)
        {
            if (root == null) return "";
            unchecked
            {
                long hash = 17;
                void Add(long value) => hash = hash * 31 + value;
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    Add(transform.GetInstanceID());
                    Add(transform.parent != null ? transform.parent.GetInstanceID() : 0);
                    Add(Content(transform.gameObject));
                    foreach (var component in transform.GetComponents<Component>())
                    {
                        if (component == null || component is Transform || component is MyAvatarFaceTracking || component is MyAvatar) continue;
                        Add(component.GetInstanceID());
                        Add(Content(component));
                    }
                }
                return hash.ToString("x");
            }
        }

        // ---- Releasing ----

        /// <summary>The build is no longer shown: released after ten idle minutes unless a test uses it again.</summary>
        internal static void Idle() => idleSince = EditorApplication.timeSinceStartup;

        /// <summary>Closes the copy's scene and frees the controllers and clips made for it.</summary>
        internal static void Release()
        {
            var build = cached;
            cached = null;
            idleSince = -1;
            Contents.Clear();
            if (build == null) return;
            // The build steps' own objects (My Avatar's tuning, MCB's corrective links) are found in the controllers.
            var reached = new HashSet<Object>();
            foreach (var controller in new[] { build.Fx, build.Additive }) Reach(controller, reached);
            if (build.Scene.IsValid()) EditorSceneManager.ClosePreviewScene(build.Scene);
            foreach (var copy in build.copies) copy.Destroy();
            foreach (var value in build.made.Concat(reached)) if (value != null && !EditorUtility.IsPersistent(value)) Object.DestroyImmediate(value);
            build.copies.Clear(); build.made.Clear();
            build.Copy = null; build.Fx = build.Additive = null;
        }

        // Everything a controller holds: its state machines, states, transitions, behaviours, blend trees and clips.
        private static void Reach(AnimatorController controller, HashSet<Object> reached)
        {
            if (controller == null || !reached.Add(controller)) return;
            void AddMotion(Motion motion)
            {
                if (motion == null || !reached.Add(motion)) return;
                if (motion is BlendTree tree) foreach (var child in tree.children) AddMotion(child.motion);
            }
            void AddTransitions(IEnumerable<AnimatorTransitionBase> transitions) { foreach (var t in transitions) if (t != null) reached.Add(t); }
            void AddMachine(AnimatorStateMachine machine)
            {
                if (machine == null || !reached.Add(machine)) return;
                reached.UnionWith(machine.behaviours.Where(b => b != null));
                AddTransitions(machine.anyStateTransitions); AddTransitions(machine.entryTransitions);
                foreach (var child in machine.states)
                {
                    if (child.state == null || !reached.Add(child.state)) continue;
                    reached.UnionWith(child.state.behaviours.Where(b => b != null));
                    AddTransitions(child.state.transitions);
                    AddMotion(child.state.motion);
                }
                foreach (var child in machine.stateMachines)
                {
                    AddTransitions(machine.GetStateMachineTransitions(child.stateMachine));
                    AddMachine(child.stateMachine);
                }
            }
            foreach (var layer in controller.layers) AddMachine(layer.stateMachine);
        }

        // Everything the test holds goes before scripts reload (a copy left in a preview scene can hang the reload) or the
        // editor quits: the phone link's sockets and threads, the played layers, the stage, the copy and its controllers.
        private static void Shutdown()
        {
            FaceTrackingTest.StopAll();
            Release();
        }

        [InitializeOnLoadMethod]
        private static void Watch()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            EditorApplication.playModeStateChanged += change => { if (change == PlayModeStateChange.ExitingEditMode) Shutdown(); };
            EditorApplication.update += () =>
            {
                if (cached != null && idleSince >= 0 && EditorApplication.timeSinceStartup - idleSince > IdleRelease) Release();
            };
        }
    }
}
