using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.FaceTracking;
using Orbiters.MyAvatar.Editor.Vrm;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.VrchatReady
{
    /// <summary>
    /// What Make it VRChat ready will do to an avatar without a VRChat avatar descriptor, read from the model (and its VRM
    /// components when it has them) without changing anything: shown on the card, then done by <see cref="ReadyConversion"/>.
    /// </summary>
    internal sealed class ReadyPlan
    {
        /// <summary>The VRM readers UniVRM's assemblies registered (VRM 0.x, VRM 1.0).</summary>
        internal static readonly List<IVrmReader> Readers = new List<IVrmReader>();

        // Namespaces of UniVRM's components (0.x, 1.0, its humanoid and glTF helpers): VRChat allows none on an avatar.
        private static readonly string[] VrmNamespaces = { "VRM", "UniVRM10", "UniHumanoid", "UniGLTF" };

        // How far in front of the eye bones (the eyeballs' centres) the view sits, as a share of the distance between them:
        // VRChat wants it between the eyes, at their surface.
        internal const float ViewForward = .3f;
        // Eye turns without a VRM to say: VRM's own default, small enough for eyes made without eye tracking in mind.
        internal const float DefaultLook = 10f;

        public GameObject Root;
        public Animator Animator;
        /// <summary>Why it can't be made ready (no humanoid rig), what to do about it, and the model file to fix; null when it can.</summary>
        public string Problem, Help;
        public Object ModelFile;
        public VrmSource Vrm;
        /// <summary>The descriptor's view point, in the root's space, and where it comes from.</summary>
        public Vector3 ViewPosition;
        public string ViewFrom;
        /// <summary>Lip sync: the face mesh and its 15 viseme shapes (null entries stay empty), and where they come from.</summary>
        public SkinnedMeshRenderer Face;
        public string[] Visemes;
        public string VisemesFrom;
        public Transform LeftEye, RightEye;
        public float LookUp = DefaultLook, LookDown = DefaultLook, LookInner = DefaultLook, LookOuter = DefaultLook;
        public string EyesFrom;
        public SkinnedMeshRenderer BlinkMesh;
        public string Blink;
        public readonly List<SpringMapping.Group> PhysBones = new List<SpringMapping.Group>();
        public readonly List<VrmModel.Collider> Colliders = new List<VrmModel.Collider>();
        /// <summary>The VRM's emotions (and its other expressions that aren't perfect sync), as menu toggles.</summary>
        public readonly List<VrmModel.Expression> Toggles = new List<VrmModel.Expression>();
        /// <summary>The components VRChat doesn't allow, which only VRM apps read.</summary>
        public readonly List<Component> Remove = new List<Component>();

        public bool CanConvert => Problem == null && Root != null;
        public int VisemeCount => Visemes == null ? 0 : Visemes.Count(v => !string.IsNullOrEmpty(v));

        /// <summary>Whether the avatar My Avatar is on already has a VRChat avatar descriptor (on it or above it).</summary>
        internal static bool HasDescriptor(MyAvatar avatar) => avatar != null && avatar.GetComponentInParent<VRC.SDKBase.VRC_AvatarDescriptor>(true) != null;

        /// <summary>The plan for the avatar My Avatar is on.</summary>
        internal static ReadyPlan Of(MyAvatar avatar)
        {
            if (avatar == null) return new ReadyPlan { Problem = "The avatar is gone." };
            // The model's root holds the Animator: My Avatar's object or one above it.
            var animator = avatar.GetComponentInParent<Animator>(true);
            if (animator == null)
            {
                var below = avatar.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.avatar != null && a.avatar.isHuman);
                return new ReadyPlan
                {
                    Root = avatar.gameObject,
                    Problem = "There's no Animator here: VRChat needs the model's root, with a humanoid rig.",
                    Help = below != null ? "The model is “" + below.name + "”: put My Avatar on that object (Add Component › Orbiters › My Avatar) and make it ready there."
                        : "Put My Avatar on the model's root object, the one with an Animator.",
                };
            }
            return Of(animator.gameObject);
        }

        /// <summary>The plan for a model whose root is <paramref name="root"/> (it holds the Animator).</summary>
        internal static ReadyPlan Of(GameObject root)
        {
            var plan = new ReadyPlan { Root = root };
            if (root == null) { plan.Problem = "The avatar is gone."; return plan; }
            plan.Animator = root.GetComponent<Animator>();
            foreach (var reader in Readers)
            {
                VrmSource source = null;
                try { source = reader.Read(root); }
                catch (Exception ex) { Debug.LogException(ex); }
                if (source != null) { plan.Vrm = source; break; }
            }
            var animator = plan.Animator;
            if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                plan.ModelFile = ModelFileOf(root, animator);
                string file = plan.ModelFile != null ? "“" + plan.ModelFile.name + "”" : "the model file";
                plan.Problem = animator == null ? "There's no Animator on “" + root.name + "”: VRChat needs one, with a humanoid rig."
                    : "Its rig isn't humanoid: VRChat's walking, sitting and full body tracking need a humanoid one.";
                plan.Help = "Select " + file + " in the Project window, open Rig in the Inspector, set Animation Type to Humanoid and press Apply. " +
                            "Then come back here.";
                plan.RemoveList(root);
                return plan;
            }
            var humanoid = new HashSet<Transform>(Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>()
                .Where(b => b != HumanBodyBones.LastBone).Select(animator.GetBoneTransform).Where(t => t != null));
            plan.ReadEyes(animator);
            plan.ReadView(animator);
            plan.ReadLipSync(root);
            plan.ReadBlink(root);
            if (plan.Vrm != null)
            {
                plan.PhysBones.AddRange(SpringMapping.Groups(plan.Vrm, humanoid));
                plan.Colliders.AddRange(plan.PhysBones.SelectMany(g => g.Colliders).Distinct());
                plan.ReadToggles();
            }
            plan.RemoveList(root);
            return plan;
        }

        // ---- Eyes and view ----

        private void ReadEyes(Animator animator)
        {
            var vrm = Vrm;
            LeftEye = vrm?.LeftEye != null ? vrm.LeftEye : animator.GetBoneTransform(HumanBodyBones.LeftEye);
            RightEye = vrm?.RightEye != null ? vrm.RightEye : animator.GetBoneTransform(HumanBodyBones.RightEye);
            if (LeftEye == null || RightEye == null) (LeftEye, RightEye) = EyesByName(animator.GetBoneTransform(HumanBodyBones.Head));
            if (LeftEye == null || RightEye == null || LeftEye == RightEye)
            {
                LeftEye = RightEye = null;
                EyesFrom = vrm != null && !vrm.EyeBones ? "The VRM moves its eyes with blend shapes: VRChat turns eye bones only" : "No eye bones found";
                return;
            }
            if (vrm != null && vrm.EyeBones)
            {
                LookUp = vrm.LookUp; LookDown = vrm.LookDown; LookInner = vrm.LookInner; LookOuter = vrm.LookOuter;
                EyesFrom = "The VRM's look-at";
            }
            else EyesFrom = animator.GetBoneTransform(HumanBodyBones.LeftEye) == LeftEye ? "The humanoid rig's eyes" : "Eye bones found by name";
        }

        // Eye bones named so under the head, when the rig doesn't map them: Eye_L, LeftEye, VRoid's J_Adj_L_FaceEye...
        private static readonly string[] LeftEyeNames = { "lefteye", "eyeleft", "eyel", "leye", "eyeballl", "eyeballleft", "lefteyeball", "jadjlfaceeye" };
        private static readonly string[] RightEyeNames = { "righteye", "eyeright", "eyer", "reye", "eyeballr", "eyeballright", "righteyeball", "jadjrfaceeye" };

        private static (Transform, Transform) EyesByName(Transform head)
        {
            if (head == null) return (null, null);
            var all = head.GetComponentsInChildren<Transform>(true);
            Transform Find(string[] names) => names.Select(n => all.FirstOrDefault(t => FaceTrackingNames.Normalize(t.name) == n)).FirstOrDefault(t => t != null);
            return (Find(LeftEyeNames), Find(RightEyeNames));
        }

        // Between the eyes, at their surface: the eye bones when there are any (a VRM's first person point usually sits
        // inside the head: VRoid's is 6 cm above the head bone, 8 cm behind the eyes, so the avatar's face would float in
        // front of the player's); else the VRM's first person point; else a guess from the head's height above the hips.
        private void ReadView(Animator animator)
        {
            var root = Root.transform;
            Vector3 world;
            var head = Vrm?.Head != null ? Vrm.Head : animator.GetBoneTransform(HumanBodyBones.Head);
            if (LeftEye != null && RightEye != null)
            {
                float spacing = Vector3.Distance(LeftEye.position, RightEye.position);
                world = (LeftEye.position + RightEye.position) / 2 + root.forward * spacing * ViewForward;
                ViewFrom = "Between the eyes";
            }
            else if (Vrm?.ViewOffset != null && head != null)
            {
                world = head.TransformPoint(Vrm.ViewOffset.Value);
                ViewFrom = "The VRM's first person point";
            }
            else if (head != null)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                float length = hips != null ? Mathf.Max(.05f, Vector3.Dot(head.position - hips.position, root.up)) : .6f * root.lossyScale.y;
                world = head.position + root.up * length * .13f + root.forward * length * .14f;
                ViewFrom = "Guessed from the head (no eye bones)";
            }
            else
            {
                world = root.TransformPoint(new Vector3(0, 1.6f, .1f));
                ViewFrom = "VRChat's default";
            }
            ViewPosition = root.InverseTransformPoint(world);
        }

        // ---- Face ----

        private static readonly VrmPreset[] VowelPresets = { VrmPreset.Aa, VrmPreset.Ih, VrmPreset.Ou, VrmPreset.Ee, VrmPreset.Oh };

        // The VRM's vowel expressions on the mesh most of them use; else the face mesh whose shapes' names look most
        // like vowels (VRChat's visemes, VRoid's, MMD's...).
        private void ReadLipSync(GameObject root)
        {
            var vowels = new string[5];
            if (Vrm != null)
            {
                var main = VowelPresets.Select(p => Main(Vrm.Expressions.FirstOrDefault(e => e.Preset == p))).ToArray();
                Face = main.Where(m => m.Renderer != null).GroupBy(m => m.Renderer).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
                if (Face != null)
                    for (int v = 0; v < 5; v++)
                    {
                        var expression = Vrm.Expressions.FirstOrDefault(e => e.Preset == VowelPresets[v]);
                        // The vowel's strongest shape on the face mesh (VRChat's visemes all live on one mesh).
                        vowels[v] = expression?.Shapes.Where(s => s.Renderer == Face && Has(Face, s.Shape)).OrderByDescending(s => s.Weight).Select(s => s.Shape).FirstOrDefault();
                    }
                if (vowels.Any(v => v != null)) VisemesFrom = "The VRM's " + string.Join(", ", Enumerable.Range(0, 5).Where(v => vowels[v] != null).Select(v => FaceShapes.Vowels[v])) + " expressions";
            }
            if (Face == null)
            {
                var faces = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0).ToList();
                var scored = faces.Select(r => (Renderer: r, Score: FaceShapes.Score(Names(r)))).Where(x => x.Score > 0).ToList();
                if (scored.Count > 0)
                {
                    int best = scored.Max(x => x.Score);
                    var tied = scored.Where(x => x.Score == best).Select(x => x.Renderer).ToList();
                    Face = tied.FirstOrDefault(r => r.name == "Body" || r.name == "Face") ?? tied.OrderByDescending(r => r.sharedMesh.blendShapeCount).First();
                }
            }
            if (Face == null) { VisemesFrom = "No mouth shapes found: lip sync stays off"; return; }
            var names = Names(Face);
            // Vowels the VRM leaves out, by name.
            for (int v = 0; v < 5; v++) if (vowels[v] == null) vowels[v] = FaceShapes.Vowel(names, v);
            Visemes = FaceShapes.Fill(names, vowels);
            if (VisemesFrom == null)
                VisemesFrom = Visemes[10] != null && Visemes[10] == FaceShapes.Viseme(names, 10) ? "Its VRChat viseme shapes"
                    : "Its mouth shapes " + string.Join(", ", Enumerable.Range(0, 5).Where(v => vowels[v] != null).Select(v => "“" + vowels[v] + "”"));
            if (VisemeCount == 0) { Face = null; Visemes = null; VisemesFrom = "No mouth shapes found: lip sync stays off"; }
        }

        // The VRM's blink, else a shape named so (VRoid's Fcl_EYE_Close, Blink, まばたき...) on the face mesh first.
        private void ReadBlink(GameObject root)
        {
            var blink = Main(Vrm?.Expressions.FirstOrDefault(e => e.Preset == VrmPreset.Blink));
            if (blink.Renderer != null && Has(blink.Renderer, blink.Shape)) { BlinkMesh = blink.Renderer; Blink = blink.Shape; return; }
            var meshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0)
                .OrderByDescending(r => r == Face).ToList();
            foreach (var mesh in meshes)
            {
                string shape = FaceShapes.Blink(Names(mesh));
                if (shape != null) { BlinkMesh = mesh; Blink = shape; return; }
            }
        }

        // The emotions first (VRM's joy, angry, sorrow, fun and surprised; 1.0's happy, angry, sad, relaxed, surprised),
        // then the VRM's own expressions; never the vowels, blink, look or perfect sync's ARKit shapes, which VRChat drives.
        private static readonly VrmPreset[] Emotions = { VrmPreset.Happy, VrmPreset.Angry, VrmPreset.Sad, VrmPreset.Relaxed, VrmPreset.Surprised };
        internal const int MaxToggles = 16;
        private static readonly HashSet<string> ARKitNames = new HashSet<string>(FaceTrackingStandard.ARKit.Core.Select(FaceTrackingNames.Normalize), StringComparer.Ordinal);

        private void ReadToggles()
        {
            var shown = Vrm.Expressions.Where(e => e.Shapes.Any(s => s.Renderer != null && Has(s.Renderer, s.Shape))).ToList();
            Toggles.AddRange(Emotions.Select(p => shown.FirstOrDefault(e => e.Preset == p)).Where(e => e != null));
            foreach (var custom in shown.Where(e => e.Preset == null && !string.IsNullOrWhiteSpace(e.Name)))
            {
                string key = FaceTrackingNames.Normalize(custom.Name);
                if (ARKitNames.Contains(key) || key.StartsWith("blendshape", StringComparison.Ordinal) && ARKitNames.Contains(key.Substring("blendshape".Length))) continue;
                if (Toggles.Any(t => FaceTrackingNames.Normalize(t.Name) == key)) continue;
                Toggles.Add(custom);
            }
            if (Toggles.Count > MaxToggles) Toggles.RemoveRange(MaxToggles, Toggles.Count - MaxToggles);
        }

        // ---- What goes ----

        private void RemoveList(GameObject root)
        {
            var found = new List<Component>();
            if (Vrm != null) found.AddRange(Vrm.Components.Where(c => c != null));
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && IsVrm(component.GetType())) found.Add(component);
            Remove.AddRange(found.Distinct());
        }

        internal static bool IsVrm(Type type)
        {
            string space = type.Namespace;
            return space != null && VrmNamespaces.Any(n => space == n || space.StartsWith(n + ".", StringComparison.Ordinal));
        }

        // ---- Helpers ----

        private static (SkinnedMeshRenderer Renderer, string Shape) Main(VrmModel.Expression expression)
        {
            if (expression == null) return (null, null);
            var best = expression.Shapes.Where(s => s.Renderer != null).OrderByDescending(s => s.Weight).FirstOrDefault();
            return (best.Renderer, best.Shape);
        }

        private static bool Has(SkinnedMeshRenderer renderer, string shape) =>
            renderer != null && renderer.sharedMesh != null && !string.IsNullOrEmpty(shape) && renderer.sharedMesh.GetBlendShapeIndex(shape) >= 0;

        internal static List<string> Names(SkinnedMeshRenderer renderer) => FaceTrackingDetection.Shapes(renderer != null ? renderer.sharedMesh : null).ToList();

        // The model file whose import settings make the rig (its avatar's, else a mesh's).
        private static Object ModelFileOf(GameObject root, Animator animator)
        {
            string path = animator != null && animator.avatar != null ? AssetDatabase.GetAssetPath(animator.avatar) : null;
            if (string.IsNullOrEmpty(path) || !(AssetImporter.GetAtPath(path) is ModelImporter))
                path = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).Select(r => AssetDatabase.GetAssetPath(r.sharedMesh))
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p) && AssetImporter.GetAtPath(p) is ModelImporter);
            if (string.IsNullOrEmpty(path))
            {
                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(root);
                path = source != null ? AssetDatabase.GetAssetPath(source) : null;
            }
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
        }
    }
}
