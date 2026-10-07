using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// The avatar built as VRChat builds it for an upload, on a hidden copy in its own preview scene: every preprocess
    /// callback in VRChat's order (VRCFury, MCB, My Avatar, the Toolkit…), one per editor frame so the window can show the
    /// progress. The copy stays (one at a time) while the avatar is unchanged, so testing again starts at once; scripts
    /// reloading, Play Mode, quitting or ten idle minutes release it with the build data it holds.
    /// </summary>
    internal sealed class FaceTrackingTestBuild
    {
        private const string TimesKey = "Orbiters.MyAvatar.FaceTracking.BuildSeconds.";
        private const double IdleRelease = 600;
        private static FaceTrackingTestBuild cached;
        private static double idleSince = -1;

        public readonly GameObject Source;
        /// <summary>The avatar as the build saw it (measured again once built: build steps may mark source objects changed).</summary>
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

        private readonly List<IVRCSDKPreprocessAvatarCallback> steps;
        private readonly Stopwatch clock = new Stopwatch();
        private readonly double[] estimates;
        private int next;
        private Renderer[] renderers;

        private FaceTrackingTestBuild(GameObject source, string signature, bool expressive)
        {
            Source = source; Signature = signature; Expressive = expressive;
            steps = Callbacks();
            estimates = steps.Select(s => (double)EditorPrefs.GetFloat(TimesKey + s.GetType().FullName, DefaultSeconds(s))).ToArray();
        }

        /// <summary>The cached build of <paramref name="source"/> when it is still the avatar as it is, else a new one started.</summary>
        internal static FaceTrackingTestBuild For(GameObject source, MyAvatarFaceTracking marker)
        {
            string signature = SignatureOf(source);
            bool expressive = marker == null || marker.expressiveMouth;
            bool building = cached != null && !cached.Done && cached.Error == null && cached.Copy != null;
            if (cached != null && cached.Source == source && cached.Signature == signature && cached.Expressive == expressive && (cached.Usable || building))
            { idleSince = -1; return cached; }
            Release();
            cached = new FaceTrackingTestBuild(source, signature, expressive);
            cached.Begin();
            return cached;
        }

        /// <summary>A finished build of <paramref name="source"/> is kept (testing starts at once unless the avatar changed).</summary>
        internal static bool Kept(GameObject source) => cached != null && cached.Source == source && cached.Usable;

        /// <summary>The build is done and its copy, controllers and meshes are all still there (VRCFury's next build may delete them).</summary>
        public bool Usable => Done && Error == null && Copy != null && Scene.IsValid() && Fx != null && renderers != null && renderers.All(r => r == null || Mesh(r) != null);

        /// <summary>From 0 to 1, by how long each callback took last time.</summary>
        public float Progress
        {
            get
            {
                if (Done) return 1f;
                double total = estimates.Sum(), done = estimates.Take(next).Sum();
                return total <= 0 ? 0f : (float)(done / total);
            }
        }

        public double SecondsLeft => Done ? 0 : estimates.Skip(next).Sum();

        /// <summary>What runs next, in the words of the person waiting.</summary>
        public string Step => Done ? "Ready" : next < steps.Count ? Describe(steps[next]) : "Finishing";

        private void Begin()
        {
            clock.Start();
            try
            {
                Scene = EditorSceneManager.NewPreviewScene();
                // Instantiated under an object of the preview scene: the copy is never in the user's scene, even for a frame.
                var holder = new GameObject("Face tracking test");
                SceneManager.MoveGameObjectToScene(holder, Scene);
                Copy = Object.Instantiate(Source, holder.transform);
                Copy.transform.SetParent(null, false);
                Object.DestroyImmediate(holder);
                // Its own name: VRCFury keeps each build's controllers in a folder named after the avatar.
                Copy.name = Source.name + " (face tracking test)";
                Copy.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                Copy.SetActive(true);
            }
            catch (Exception ex) { Fail("Could not copy the avatar: " + ex.Message); }
        }

        /// <summary>Runs the next callback. False once there is nothing left to do (done or failed).</summary>
        internal bool StepOnce()
        {
            if (Done || Error != null) return false;
            if (Copy == null) { Fail("The avatar copy was destroyed while building."); return false; }
            if (next >= steps.Count) { Finish(); return false; }
            var step = steps[next];
            var watch = Stopwatch.StartNew();
            FaceTrackingBuild.TestBuild = true;
            try
            {
                if (!step.OnPreprocessAvatar(Copy)) { Fail(Describe(step) + " stopped the build (" + step.GetType().Name + " returned false). Uploading would stop there too."); return false; }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Fail(Describe(step) + " failed: " + ex.Message);
                return false;
            }
            finally { FaceTrackingBuild.TestBuild = false; }
            // Remember how long it took, for the next progress bar.
            EditorPrefs.SetFloat(TimesKey + step.GetType().FullName, (float)watch.Elapsed.TotalSeconds);
            estimates[next] = watch.Elapsed.TotalSeconds;
            next++;
            return true;
        }

        private void Finish()
        {
            Descriptor = Copy.GetComponent<VRCAvatarDescriptor>();
            if (Descriptor == null) { Fail("The built avatar has no VRChat avatar descriptor."); return; }
            AnimatorController Layer(VRCAvatarDescriptor.AnimLayerType type) =>
                Descriptor.baseAnimationLayers.FirstOrDefault(l => l.type == type && !l.isDefault).animatorController as AnimatorController;
            Fx = Layer(VRCAvatarDescriptor.AnimLayerType.FX);
            Additive = Layer(VRCAvatarDescriptor.AnimLayerType.Additive);
            if (Fx == null) { Fail("The built avatar has no FX controller: VRCFury did not merge the face tracking template."); return; }
            renderers = Copy.GetComponentsInChildren<Renderer>(true).Where(r => Mesh(r) != null).ToArray();
            Signature = SignatureOf(Source);
            clock.Stop();
            Done = true;
            Debug.Log($"[My Avatar] Face tracking test: {Source.name} built like an upload in {Seconds:0.0} s.");
        }

        private void Fail(string message)
        {
            Error = message;
            clock.Stop();
        }

        private static Mesh Mesh(Renderer renderer) =>
            renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;

        // ---- VRChat's callbacks ----

        // Every preprocess callback, in VRChat's order: the ones its build pipeline runs (VRCBuildPipelineCallbacks collects
        // the same types).
        private static List<IVRCSDKPreprocessAvatarCallback> Callbacks()
        {
            var list = new List<IVRCSDKPreprocessAvatarCallback>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<IVRCSDKPreprocessAvatarCallback>())
            {
                if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) == null) continue;
                try { list.Add((IVRCSDKPreprocessAvatarCallback)Activator.CreateInstance(type)); }
                catch (Exception ex) { Debug.LogWarning("[My Avatar] Face tracking test: could not create the build step " + type.FullName + ": " + ex.Message); }
            }
            return list.OrderBy(c => c.callbackOrder).ThenBy(c => c.GetType().FullName, StringComparer.Ordinal).ToList();
        }

        private static float DefaultSeconds(IVRCSDKPreprocessAvatarCallback step)
        {
            string name = step.GetType().FullName ?? "";
            if (name.StartsWith("VF.Hooks.VrcPreuploadHook", StringComparison.Ordinal)) return 30f;
            if (name.Contains("VersionCustomizationApply")) return 7f;
            if (name.Contains("VersionCustomizationCapture")) return 2f;
            if (name.Contains("BuildCopyAssets")) return 1.5f;
            return .1f;
        }

        private static string Describe(IVRCSDKPreprocessAvatarCallback step)
        {
            string name = step.GetType().FullName ?? "";
            if (name.StartsWith("VF.Hooks.VrcPreuploadHook", StringComparison.Ordinal)) return "VRCFury merges the face tracking template";
            if (name.StartsWith("VF.Hooks.ParameterCompressor", StringComparison.Ordinal)) return "VRCFury compresses parameters";
            if (name.StartsWith("VF.", StringComparison.Ordinal)) return "VRCFury prepares the avatar";
            if (name.Contains("VersionCustomization") || name.Contains("NativeMesh") || name.Contains("BlendShapeLink")) return "MCB applies your version";
            if (name.Contains("FaceTrackingBuild")) return "My Avatar tunes the face";
            if (name.StartsWith("Orbiters.", StringComparison.Ordinal)) return "Orbiters tools finish the avatar";
            if (name.StartsWith("Thry", StringComparison.Ordinal)) return "Thry prepares the materials";
            return "VRChat prepares the avatar";
        }

        // ---- What makes a build stale ----

        // Objects' serialized contents by instance, measured again only when their dirty count moved: inspectors and build
        // steps often mark a component changed without changing it (MCB's own does, on every Inspector).
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
        /// Changes to the avatar that the build sees: its objects and components as saved (not where its bones are), and
        /// the assets its descriptor uses. The face tracking settings the test applies live (smoothing, features) are left out.
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
                var descriptor = root.GetComponent<VRCAvatarDescriptor>();
                if (descriptor != null)
                {
                    foreach (var layer in descriptor.baseAnimationLayers.Concat(descriptor.specialAnimationLayers))
                        if (layer.animatorController != null) Add(EditorUtility.GetDirtyCount(layer.animatorController));
                    if (descriptor.expressionParameters != null) Add(EditorUtility.GetDirtyCount(descriptor.expressionParameters));
                    if (descriptor.expressionsMenu != null) Add(EditorUtility.GetDirtyCount(descriptor.expressionsMenu));
                }
                return hash.ToString("x");
            }
        }

        // ---- Releasing ----

        /// <summary>The build is no longer shown: released after ten idle minutes unless a test uses it again.</summary>
        internal static void Idle() => idleSince = EditorApplication.timeSinceStartup;

        /// <summary>Closes the copy's scene and releases the build data the Toolkit keeps for it (meshes made for the build).</summary>
        internal static void Release()
        {
            var build = cached;
            cached = null;
            idleSince = -1;
            Contents.Clear();
            if (build == null) return;
            try { if (build.Copy != null) AttachmentAnimationBuild.Release(build.Copy); }
            catch (Exception ex) { Debug.LogWarning("[My Avatar] Face tracking test: could not release the build data: " + ex.Message); }
            if (build.Scene.IsValid()) EditorSceneManager.ClosePreviewScene(build.Scene);
            build.Copy = null;
        }

        [InitializeOnLoadMethod]
        private static void Watch()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
            EditorApplication.playModeStateChanged += change => { if (change == PlayModeStateChange.ExitingEditMode) { FaceTrackingTest.StopAll(); Release(); } };
            EditorApplication.update += () =>
            {
                if (cached != null && idleSince >= 0 && EditorApplication.timeSinceStartup - idleSince > IdleRelease) Release();
            };
        }
    }
}
