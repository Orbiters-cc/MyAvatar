using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.FaceTracking;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Vrm
{
    internal enum VrmPreset { Aa, Ih, Ou, Ee, Oh, Blink, BlinkLeft, BlinkRight, Happy, Angry, Sad, Relaxed, Surprised }

    internal enum VrmAlpha { Opaque, Cutout, Transparent }

    /// <summary>
    /// The avatar as a VRM file describes it, read from a built copy without UniVRM: what VRChat's descriptor says
    /// (visemes, blink, eyes, the view point), springs and colliders from its PhysBones, and each material's colours and
    /// textures for MToon. The writers (one per VRM version, compiled only with UniVRM) turn it into UniVRM's components.
    /// </summary>
    internal sealed class VrmModel
    {
        internal sealed class Expression
        {
            /// <summary>One of VRM's own, or null for a custom one named <see cref="Name"/> (perfect sync's ARKit names).</summary>
            public VrmPreset? Preset;
            public string Name;
            /// <summary>The shapes it sets, by name, at a weight from 0 to 1.</summary>
            public readonly List<(SkinnedMeshRenderer Renderer, string Shape, float Weight)> Shapes = new List<(SkinnedMeshRenderer, string, float)>();
        }

        internal sealed class Collider
        {
            public Transform Transform;
            /// <summary>Center, and for a capsule its other end, in the transform's space; Plane: the normal in Tail.</summary>
            public Vector3 Offset, Tail;
            public float Radius;
            public bool Capsule, Plane, Inside;
        }

        internal sealed class Spring
        {
            public string Name;
            /// <summary>The chain from its first joint (it turns too) to its last.</summary>
            public readonly List<Transform> Joints = new List<Transform>();
            /// <summary>Where the last joint points, in its space (a joint turns toward the next one).</summary>
            public Vector3 End;
            public float Stiffness, GravityPower, Drag, Radius;
            public Vector3 GravityDirection = Vector3.down;
            public readonly List<Collider> Colliders = new List<Collider>();
        }

        internal sealed class Material
        {
            public UnityEngine.Material Source;
            public Texture MainTexture, NormalTexture, EmissionTexture;
            public Vector2 Scale = Vector2.one, Offset;
            public Color Color = Color.white, ShadeColor = new Color(.8f, .8f, .8f), Emission = Color.black;
            public float NormalScale = 1f, Cutoff = .5f;
            public VrmAlpha Alpha;
            public bool DoubleSided;
        }

        public GameObject Root;
        public VrmExportSettings Meta;
        public string Title;
        public Texture2D Thumbnail;
        public Transform Head, LeftEye, RightEye;
        /// <summary>The player's view point from the head, in the avatar's axes.</summary>
        public Vector3 ViewOffset = new Vector3(0, .06f, 0);
        public readonly List<Expression> Expressions = new List<Expression>();
        public readonly List<Spring> Springs = new List<Spring>();
        public readonly List<Collider> Colliders = new List<Collider>();
        public readonly Dictionary<UnityEngine.Material, Material> Materials = new Dictionary<UnityEngine.Material, Material>();

        // VRChat's visemes in the descriptor's order; VRM keeps the five vowels.
        private static readonly (int Viseme, VrmPreset Preset)[] Vowels =
            { (10, VrmPreset.Aa), (12, VrmPreset.Ih), (14, VrmPreset.Ou), (11, VrmPreset.Ee), (13, VrmPreset.Oh) };

        // Shape names a face may have for VRM's emotions, compared letters and digits only.
        private static readonly (VrmPreset Preset, string[] Names)[] Emotions =
        {
            (VrmPreset.Happy, new[] { "happy", "joy" }), (VrmPreset.Angry, new[] { "angry", "anger" }), (VrmPreset.Sad, new[] { "sad", "sorrow" }),
            (VrmPreset.Relaxed, new[] { "relaxed", "relax", "fun" }), (VrmPreset.Surprised, new[] { "surprised", "surprise" }),
        };

        /// <summary>
        /// Reads a built copy for VRM and readies it: objects and renderers that start off go (a VRM shows everything it
        /// holds), the eyes, view point, expressions, springs, colliders and materials are read, and the meshes keep only
        /// the blendshapes an expression sets (the others baked in at their weight): a VRM app moves nothing else.
        /// </summary>
        internal static VrmModel From(GameObject root, VrmExportSettings meta, string title, Texture2D thumbnail)
        {
            var model = new VrmModel { Root = root, Meta = meta, Title = title, Thumbnail = thumbnail };
            var animator = root.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) throw new InvalidOperationException("The avatar has no humanoid rig: VRM needs one.");
            var humanoid = new HashSet<Transform>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                if (bone != HumanBodyBones.LastBone && animator.GetBoneTransform(bone) is Transform t) humanoid.Add(t);
            model.Head = animator.GetBoneTransform(HumanBodyBones.Head);
            model.LeftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            model.RightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
            Strip(root, humanoid);

            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                if (model.Head != null) model.ViewOffset = root.transform.TransformPoint(descriptor.ViewPosition) - model.Head.position;
                var eyes = descriptor.customEyeLookSettings;
                if (descriptor.enableEyeLook)
                {
                    if (model.LeftEye == null && eyes.leftEye != null) model.LeftEye = eyes.leftEye;
                    if (model.RightEye == null && eyes.rightEye != null) model.RightEye = eyes.rightEye;
                }
            }
            model.ReadExpressions(root);
            model.PruneShapes(root);
            model.ReadColliders(root);
            model.ReadSprings(root);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && !model.Materials.ContainsKey(material)) model.Materials[material] = Read(material);
            return model;
        }

        // Visemes and blink from the descriptor, then what the face's shape names give.
        private void ReadExpressions(GameObject root)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                var eyes = descriptor.customEyeLookSettings;
                ReadVisemes(descriptor);
                if (descriptor.enableEyeLook && eyes.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes && eyes.eyelidsSkinnedMesh != null &&
                    eyes.eyelidsBlendshapes != null && eyes.eyelidsBlendshapes.Length > 0 && eyes.eyelidsBlendshapes[0] >= 0)
                    Add(VrmPreset.Blink, eyes.eyelidsSkinnedMesh, eyes.eyelidsBlendshapes[0]);
            }
            ReadFace(root);
        }

        /// <summary>
        /// The blendshapes the VRM's expressions will set, read on the avatar before it is built: the build keeps them
        /// (<see cref="Toolkit.Editor.VRChat.BlendShapes.BlendShapePruning.Keep"/>), though no VRChat animation may use
        /// them (perfect sync's shapes on an avatar without face tracking).
        /// </summary>
        internal static HashSet<string> Shapes(GameObject root)
        {
            var probe = new VrmModel { Root = root };
            probe.ReadExpressions(root);
            return new HashSet<string>(probe.Expressions.SelectMany(e => e.Shapes).Select(s => s.Shape), StringComparer.Ordinal);
        }

        // A VRM shows everything it holds: what starts off goes (unless a humanoid bone is in it), and so do the
        // renderers VRM has no use for (particles, trails, lines).
        private static void Strip(GameObject root, HashSet<Transform> humanoid)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform && !t.gameObject.activeSelf).ToList())
                if (t != null && !humanoid.Any(h => h.IsChildOf(t))) Object.DestroyImmediate(t.gameObject);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true).ToList())
            {
                if (renderer == null) continue;
                bool mesh = renderer is SkinnedMeshRenderer || renderer is MeshRenderer;
                if (!mesh || !renderer.enabled) Object.DestroyImmediate(renderer);
            }
            // UniVRM 1.0 reads every mesh's normals and UVs (a mesh without them fails the export): a copy gets them.
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = Toolkit.Editor.VRChat.Optimization.MeshOptimization.MeshOf(renderer);
                if (mesh == null || mesh.vertexCount == 0) continue;
                bool normals = mesh.normals.Length == mesh.vertexCount, uvs = mesh.uv.Length == mesh.vertexCount;
                if (normals && uvs) continue;
                var copy = Object.Instantiate(mesh);
                copy.name = mesh.name;
                if (!normals) copy.RecalculateNormals();
                if (!uvs) copy.uv = new Vector2[mesh.vertexCount];
                Toolkit.Editor.VRChat.Optimization.MeshOptimization.SetMesh(renderer, copy);
            }
        }

        private void Add(VrmPreset preset, SkinnedMeshRenderer renderer, int index, float weight = 1f) => Add(preset, null, renderer, index, weight);

        private void Add(VrmPreset? preset, string name, SkinnedMeshRenderer renderer, int index, float weight = 1f)
        {
            if (renderer == null || renderer.sharedMesh == null || index < 0 || index >= renderer.sharedMesh.blendShapeCount) return;
            var expression = Expressions.FirstOrDefault(e => e.Preset == preset && e.Name == name);
            if (expression == null) Expressions.Add(expression = new Expression { Preset = preset, Name = name });
            string shape = renderer.sharedMesh.GetBlendShapeName(index);
            if (!expression.Shapes.Any(s => s.Renderer == renderer && s.Shape == shape)) expression.Shapes.Add((renderer, shape, weight));
        }

        // Every mesh keeps the shapes an expression sets.
        private void PruneShapes(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null || renderer.sharedMesh.blendShapeCount == 0) continue;
                var keep = new HashSet<string>(Expressions.SelectMany(e => e.Shapes).Where(s => s.Renderer == renderer).Select(s => s.Shape), StringComparer.Ordinal);
                Toolkit.Editor.VRChat.BlendShapes.BlendShapePruning.Prune(root, renderer, keep);
            }
        }

        private void ReadVisemes(VRCAvatarDescriptor descriptor)
        {
            var face = descriptor.VisemeSkinnedMesh;
            if (descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape && face != null && face.sharedMesh != null && descriptor.VisemeBlendShapes != null)
                foreach (var (viseme, preset) in Vowels)
                    if (viseme < descriptor.VisemeBlendShapes.Length) Add(preset, face, face.sharedMesh.GetBlendShapeIndex(descriptor.VisemeBlendShapes[viseme]));
            // A jaw-flap avatar opens its mouth for every vowel.
            if (descriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape && face != null && face.sharedMesh != null)
                Add(VrmPreset.Aa, face, face.sharedMesh.GetBlendShapeIndex(descriptor.MouthOpenBlendShapeName));
        }

        // One-eye blinks and emotions from the face's shape names.
        private void ReadFace(GameObject root)
        {
            var face = FaceTrackingDetection.Face(root.transform) as SkinnedMeshRenderer;
            if (face == null || face.sharedMesh == null) return;
            var mesh = face.sharedMesh;
            var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToList();
            var index = FaceTrackingNames.Index(names);
            foreach (var (preset, template) in new[] { (VrmPreset.BlinkLeft, "EyeClosedLeft"), (VrmPreset.BlinkRight, "EyeClosedRight") })
            {
                string name = FaceTrackingNames.Resolve(template, index);
                if (name != null) Add(preset, face, mesh.GetBlendShapeIndex(name));
            }
            if (!Expressions.Any(e => e.Preset == VrmPreset.Blink))
            {
                string blink = FaceTrackingNames.Resolve("EyeClosed", index) ?? names.FirstOrDefault(n => FaceTrackingNames.Normalize(n) == "blink");
                if (blink != null) Add(VrmPreset.Blink, face, mesh.GetBlendShapeIndex(blink));
            }
            foreach (var (preset, candidates) in Emotions)
            {
                string name = names.FirstOrDefault(n => candidates.Contains(FaceTrackingNames.Normalize(n)));
                if (name != null) Add(preset, face, mesh.GetBlendShapeIndex(name));
            }
            // Perfect sync: a custom expression per ARKit shape the face has (under its own name or another standard's),
            // named as ARKit names it: VSeeFace, Warudo and VNyan drive them from an iPhone's face tracking.
            foreach (string arKit in FaceTrackingStandard.ARKit.Core)
            {
                string name = FaceTrackingNames.Resolve(arKit, index);
                if (name != null) Add(null, char.ToUpperInvariant(arKit[0]) + arKit.Substring(1), face, mesh.GetBlendShapeIndex(name));
            }
        }

        // ---- Springs ----

        private void ReadColliders(GameObject root)
        {
            foreach (var source in root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true))
            {
                var at = source.rootTransform != null ? source.rootTransform : source.transform;
                var collider = new Collider { Transform = at, Offset = source.position, Radius = source.radius, Inside = source.insideBounds };
                if (source.shapeType == VRCPhysBoneColliderBase.ShapeType.Capsule)
                {
                    // The capsule's height counts its round ends: its segment is the rest.
                    var axis = source.rotation * Vector3.up;
                    float half = Mathf.Max(0f, source.height * .5f - source.radius);
                    collider.Capsule = true;
                    collider.Offset = source.position - axis * half;
                    collider.Tail = source.position + axis * half;
                }
                else if (source.shapeType == VRCPhysBoneColliderBase.ShapeType.Plane)
                {
                    collider.Plane = true;
                    collider.Tail = source.rotation * Vector3.up;
                }
                Colliders.Add(collider);
                byCollider[source] = collider;
            }
        }

        private readonly Dictionary<VRCPhysBoneColliderBase, Collider> byCollider = new Dictionary<VRCPhysBoneColliderBase, Collider>();

        /// <summary>
        /// Each PhysBone's chains: from its root through the first child of every bone, a branch starting a chain of its
        /// own (VRM 1.0 turns each joint toward the next one, so a chain can't fork). Ignored bones are left out.
        /// </summary>
        private void ReadSprings(GameObject root)
        {
            foreach (var bone in root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                if (!bone.enabled) continue;
                var start = bone.rootTransform != null ? bone.rootTransform : bone.transform;
                var ignored = (bone.ignoreTransforms ?? new List<Transform>()).Where(t => t != null).ToList();
                bool Ignored(Transform t) => ignored.Any(i => t == i || t.IsChildOf(i));
                var chains = new List<List<Transform>>();
                void Walk(Transform t, List<Transform> chain)
                {
                    chain.Add(t);
                    var children = t.Cast<Transform>().Where(c => !Ignored(c)).ToList();
                    if (children.Count == 0) { chains.Add(chain); return; }
                    Walk(children[0], chain);
                    foreach (var other in children.Skip(1)) Walk(other, new List<Transform>());
                }
                if (!Ignored(start)) Walk(start, new List<Transform>());
                float scale = Mathf.Abs(start.lossyScale.x);
                var (stiffness, gravity, drag) = Behaviour(bone);
                int n = 0;
                foreach (var chain in chains)
                {
                    var spring = new Spring
                    {
                        Name = chains.Count == 1 ? bone.name : bone.name + " " + (++n),
                        Stiffness = stiffness, GravityPower = gravity, Drag = drag, Radius = bone.radius * scale,
                    };
                    spring.Joints.AddRange(chain);
                    spring.End = End(bone, chain);
                    foreach (var source in bone.colliders ?? new List<VRCPhysBoneColliderBase>())
                        if (source != null && byCollider.TryGetValue(source, out var collider)) spring.Colliders.Add(collider);
                    Springs.Add(spring);
                }
            }
        }

        /// <summary>
        /// A PhysBone's feel as a spring's: there is no exact match (VRM springs have no pull curve, limits or momentum),
        /// so pull and stiffness make the stiffness (VRM's usual 1 for PhysBone's usual 0.2), spring (bounce) lowers the
        /// drag, gravity carries over.
        /// </summary>
        internal static (float Stiffness, float Gravity, float Drag) Behaviour(VRCPhysBoneBase bone) =>
            (Mathf.Clamp(bone.pull * 4f + bone.stiffness * 2f, 0f, 4f), Mathf.Clamp(bone.gravity, 0f, 2f), Mathf.Clamp01(1f - bone.spring * .9f));

        // Where the last joint points: the PhysBone's endpoint, else on along the last bone by its length.
        private static Vector3 End(VRCPhysBoneBase bone, List<Transform> chain)
        {
            var last = chain[chain.Count - 1];
            if (bone.endpointPosition != Vector3.zero) return bone.endpointPosition;
            if (chain.Count >= 2)
            {
                var previous = chain[chain.Count - 2];
                var along = last.position - previous.position;
                if (along.sqrMagnitude > 1e-8f) return last.InverseTransformVector(along);
            }
            return last.InverseTransformVector(Vector3.down * .07f);
        }

        // ---- Materials ----

        /// <summary>What MToon needs from a material of any shader: colour, textures, alpha, faces drawn.</summary>
        internal static Material Read(UnityEngine.Material source)
        {
            var read = new Material { Source = source };
            if (source.HasProperty("_MainTex"))
            {
                read.MainTexture = source.GetTexture("_MainTex");
                read.Scale = source.GetTextureScale("_MainTex");
                read.Offset = source.GetTextureOffset("_MainTex");
            }
            if (source.HasProperty("_Color")) read.Color = source.GetColor("_Color");
            read.ShadeColor = new Color(read.Color.r * .8f, read.Color.g * .8f, read.Color.b * .82f, 1f);
            if (source.HasProperty("_BumpMap")) read.NormalTexture = source.GetTexture("_BumpMap");
            if (source.HasProperty("_BumpScale")) read.NormalScale = source.GetFloat("_BumpScale");
            bool emits = source.IsKeywordEnabled("_EMISSION") || Flag(source, "_EnableEmission") || Flag(source, "_UseEmission");
            if (emits && source.HasProperty("_EmissionColor"))
            {
                read.Emission = source.GetColor("_EmissionColor");
                if (source.HasProperty("_EmissionMap")) read.EmissionTexture = source.GetTexture("_EmissionMap");
            }
            int queue = source.renderQueue;
            read.Alpha = queue >= 2900 ? VrmAlpha.Transparent : queue >= 2450 ? VrmAlpha.Cutout : VrmAlpha.Opaque;
            if (source.HasProperty("_Cutoff")) read.Cutoff = source.GetFloat("_Cutoff");
            read.DoubleSided = source.HasProperty("_Cull") && Mathf.RoundToInt(source.GetFloat("_Cull")) == 0;
            return read;
        }

        private static bool Flag(UnityEngine.Material material, string property) => material.HasProperty(property) && material.GetFloat(property) > .5f;

        /// <summary>The path of <paramref name="t"/> from the model's root, as VRM bindings name it.</summary>
        internal string PathOf(Transform t) => AnimationUtility.CalculateTransformPath(t, Root.transform);
    }
}
