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
            /// <summary>The PhysBone's ignored bones: VRM 0.x moves every bone under a spring's root, so its roots go around them.</summary>
            public readonly List<Transform> Ignored = new List<Transform>();
        }

        /// <summary>
        /// A material as MToon (0.x and 1.0) draws it. Shading is said once for both: where lit meets shade, in N·L
        /// (-1 to 1, 0 at a right angle to the light), and how wide the step between them is (in N·L, 0 a hard edge).
        /// </summary>
        internal sealed class Material
        {
            public UnityEngine.Material Source;
            public Texture MainTexture, NormalTexture, EmissionTexture;
            public Vector2 Scale = Vector2.one, Offset;
            public Color Color = Color.white, ShadeColor = new Color(.8f, .8f, .8f), Emission = Color.black;
            public float NormalScale = 1f, Cutoff = .5f;
            /// <summary>The emission's strength over its colour (HDR: may pass 1).</summary>
            public float EmissionStrength = 1f;
            public VrmAlpha Alpha;
            public bool DoubleSided;
            public float ShadeCenter, ShadeWidth = .2f;
            /// <summary>A rim light added at grazing angles: none while black.</summary>
            public Color RimColor = Color.black;
            public float RimFresnelPower = 3.5f, RimLightingMix = 1f;
            /// <summary>A matcap added over the surface (its colour tints it; MToon 0.x draws it untinted).</summary>
            public Texture MatcapTexture;
            public Color MatcapColor = Color.white;
            /// <summary>The outline's width in centimetres (0: none), its width mask, colour and how much light it takes.</summary>
            public float OutlineWidth;
            public Texture OutlineWidthTexture;
            public Color OutlineColor = Color.black;
            public float OutlineLighting = 1f;
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
        /// <summary>What reading made (a humanoid with the eyes): destroyed once the file is written.</summary>
        public readonly List<Object> Made = new List<Object>();
        /// <summary>
        /// The face's shapes perfect sync found under another standard's name, and their ARKit name: the file names them so,
        /// as Warudo looks them up on the mesh by ARKit's exact names (its clips are enough for VSeeFace and VNyan).
        /// </summary>
        private readonly Dictionary<SkinnedMeshRenderer, Dictionary<string, string>> arKitNames = new Dictionary<SkinnedMeshRenderer, Dictionary<string, string>>();
        // Perfect sync needs every ARKit clip (VNyan, VSeeFace): a face with the eyes', the jaw's and half of the others gets
        // the ones it lacks, empty.
        private const float PerfectSyncShare = .5f;

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
            meta = meta ?? new VrmExportSettings();
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
            if (model.LeftEye != null && model.RightEye != null && model.LeftEye != model.RightEye &&
                (animator.GetBoneTransform(HumanBodyBones.LeftEye) == null || animator.GetBoneTransform(HumanBodyBones.RightEye) == null))
                model.AddEyes(animator);
            model.ReadExpressions(root);
            model.PruneShapes(root, meta.keepAllShapes);
            model.ReadColliders(root);
            model.ReadSprings(root);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                    if (material != null && !model.Materials.ContainsKey(material)) model.Materials[material] = Read(material);
            return model;
        }

        /// <summary>
        /// The descriptor's eyes, which the humanoid rig lacks: the copy's humanoid is built again with them, so apps that
        /// turn eye bones (VNyan's and Warudo's eye tracking, VRM 1.0's bone look-at) find them. Kept only when every other
        /// bone still maps to the same object (a name twice in the copy could mislead Unity's builder).
        /// </summary>
        private void AddEyes(Animator animator)
        {
            var before = animator.avatar;
            var bones = Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>().Where(b => b != HumanBodyBones.LastBone)
                .ToDictionary(b => b, animator.GetBoneTransform);
            var description = before.humanDescription;
            var human = description.human.Where(h => h.humanName != "LeftEye" && h.humanName != "RightEye").ToList();
            human.Add(new HumanBone { humanName = "LeftEye", boneName = LeftEye.name, limit = new HumanLimit { useDefaultValues = true } });
            human.Add(new HumanBone { humanName = "RightEye", boneName = RightEye.name, limit = new HumanLimit { useDefaultValues = true } });
            description.human = human.ToArray();
            // The rest pose is the rig's own; the eyes (and what holds them) join it as they stand.
            var skeleton = description.skeleton.ToList();
            var named = new HashSet<string>(skeleton.Select(b => b.name), StringComparer.Ordinal);
            foreach (var eye in new[] { LeftEye, RightEye })
                for (var t = eye; t != null && t != Root.transform; t = t.parent)
                    if (named.Add(t.name)) skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            description.skeleton = skeleton.ToArray();
            var built = AvatarBuilder.BuildHumanAvatar(Root, description);
            if (built == null) return;
            built.name = before.name + " (eyes)";
            if (!built.isValid || !built.isHuman)
            {
                Object.DestroyImmediate(built);
                return;
            }
            animator.avatar = built;
            animator.Rebind();
            bool same = bones.All(pair => pair.Key == HumanBodyBones.LeftEye || pair.Key == HumanBodyBones.RightEye || animator.GetBoneTransform(pair.Key) == pair.Value);
            if (!same || animator.GetBoneTransform(HumanBodyBones.LeftEye) != LeftEye || animator.GetBoneTransform(HumanBodyBones.RightEye) != RightEye)
            {
                Debug.LogWarning("[My Avatar] VRM: the eyes could not be added to the humanoid (a bone name is used twice); the file has no eye bones.");
                animator.avatar = before;
                animator.Rebind();
                Object.DestroyImmediate(built);
                return;
            }
            Made.Add(built);
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

        // Every mesh keeps the shapes an expression sets (or all of them, for apps that make expressions of any), perfect
        // sync's under their ARKit names.
        private void PruneShapes(GameObject root, bool keepAll)
        {
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;
                var keep = keepAll ? new HashSet<string>(Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName), StringComparer.Ordinal)
                    : new HashSet<string>(Expressions.SelectMany(e => e.Shapes).Where(s => s.Renderer == renderer).Select(s => s.Shape), StringComparer.Ordinal);
                arKitNames.TryGetValue(renderer, out var rename);
                if (Toolkit.Editor.VRChat.BlendShapes.BlendShapePruning.Prune(root, renderer, keep, rename) == null || rename == null) continue;
                // The expressions follow the shapes renamed (those whose new name the mesh had already kept theirs).
                foreach (var expression in Expressions)
                    for (int i = 0; i < expression.Shapes.Count; i++)
                    {
                        var (r, shape, weight) = expression.Shapes[i];
                        if (r == renderer && rename.TryGetValue(shape, out string to) && renderer.sharedMesh.GetBlendShapeIndex(shape) < 0) expression.Shapes[i] = (r, to, weight);
                    }
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
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (string arKit in FaceTrackingStandard.ARKit.Core)
            {
                string name = FaceTrackingNames.Resolve(arKit, index);
                if (name == null) continue;
                found.Add(arKit);
                Add(null, Clip(arKit), face, mesh.GetBlendShapeIndex(name));
                if (name != arKit && !names.Contains(arKit) && !renames.ContainsKey(name)) renames[name] = arKit;
            }
            if (renames.Count > 0) arKitNames[face] = renames;
            if (found.Contains("eyeBlinkLeft") && found.Contains("eyeBlinkRight") && found.Contains("jawOpen") &&
                found.Count >= FaceTrackingStandard.ARKit.Core.Length * PerfectSyncShare)
                foreach (string arKit in FaceTrackingStandard.ARKit.Core.Where(a => !found.Contains(a)))
                    Expressions.Add(new Expression { Name = Clip(arKit) });
        }

        // Perfect sync's clip names: ARKit's with a capital first letter, as VSeeFace and VNyan look them up.
        private static string Clip(string arKit) => char.ToUpperInvariant(arKit[0]) + arKit.Substring(1);

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
        /// own (VRM 1.0 turns each joint toward the next one, so a chain can't fork). Ignored bones are left out, and so
        /// are humanoid ones, which the app's tracking moves (a skirt's PhysBone on the hips: its chains start below).
        /// </summary>
        private void ReadSprings(GameObject root)
        {
            var animator = root.GetComponent<Animator>();
            var humanoid = new HashSet<Transform>(Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>()
                .Where(b => b != HumanBodyBones.LastBone).Select(animator.GetBoneTransform).Where(t => t != null));
            // A bone two PhysBones reach (one inside the other's) moves with the first one only.
            var taken = new HashSet<Transform>();
            foreach (var bone in root.GetComponentsInChildren<VRCPhysBoneBase>(true))
            {
                if (!bone.enabled) continue;
                var start = bone.rootTransform != null ? bone.rootTransform : bone.transform;
                var ignored = (bone.ignoreTransforms ?? new List<Transform>()).Where(t => t != null).ToList();
                bool Ignored(Transform t) => ignored.Any(i => t == i || t.IsChildOf(i));
                var chains = new List<List<Transform>>();
                void Walk(Transform t, List<Transform> chain)
                {
                    var children = t.Cast<Transform>().Where(c => !Ignored(c)).ToList();
                    if (humanoid.Contains(t))
                    {
                        if (chain.Count > 0) chains.Add(chain);
                        foreach (var child in children) Walk(child, new List<Transform>());
                        return;
                    }
                    chain.Add(t);
                    if (children.Count == 0) { chains.Add(chain); return; }
                    Walk(children[0], chain);
                    foreach (var other in children.Skip(1)) Walk(other, new List<Transform>());
                }
                if (!Ignored(start)) Walk(start, new List<Transform>());
                float scale = Mathf.Abs(start.lossyScale.x);
                var (stiffness, gravity, drag) = Behaviour(bone);
                for (int i = 0; i < chains.Count; i++) chains[i] = chains[i].TakeWhile(j => !taken.Contains(j)).ToList();
                chains.RemoveAll(c => c.Count == 0);
                foreach (var chain in chains) taken.UnionWith(chain);
                int n = 0;
                foreach (var chain in chains)
                {
                    var spring = new Spring
                    {
                        Name = chains.Count == 1 ? bone.name : bone.name + " " + (++n),
                        Stiffness = stiffness, GravityPower = gravity, Drag = drag, Radius = bone.radius * scale,
                    };
                    spring.Joints.AddRange(chain);
                    spring.Ignored.AddRange(ignored);
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

        /// <summary>
        /// What MToon needs from a material of any shader, read as the shader keeps it (<see cref="MaterialShaders"/>, which
        /// knows lilToon's and Poiyomi's switches): colour, textures, emission, alpha, faces drawn; for lilToon and Poiyomi
        /// also their toon shading (Multilayer Math's on Poiyomi), lilToon-style rim light, an added matcap and the outline.
        /// </summary>
        internal static Material Read(UnityEngine.Material source)
        {
            var saved = SavedMaterial.Read(source);
            var family = MaterialShaders.FamilyOf(source.shader);
            if (family == ShaderFamily.Unknown) family = MaterialShaders.Detect(saved);
            string shaderName = source.shader != null ? source.shader.name : "";
            string lilToon = family != ShaderFamily.LilToon ? null
                : shaderName.IndexOf("lilToon", StringComparison.Ordinal) >= 0 ? shaderName
                : saved.ShaderGuid != null && MaterialShaders.LilToonShaders.TryGetValue(saved.ShaderGuid, out string known) ? known : "lilToon";
            var mode = MaterialShaders.Mode(saved, family, lilToon);
            var surface = MaterialShaders.Read(saved, family, lilToon, mode);
            var read = new Material
            {
                Source = source,
                MainTexture = surface.Main.Texture, Scale = surface.Main.Scale, Offset = surface.Main.Offset,
                Color = surface.Color,
                NormalTexture = surface.Normal.Texture, NormalScale = surface.NormalScale,
                Cutoff = surface.Cutoff,
                Alpha = mode == SurfaceMode.Opaque ? VrmAlpha.Opaque : mode == SurfaceMode.Cutout ? VrmAlpha.Cutout : VrmAlpha.Transparent,
                DoubleSided = Mathf.RoundToInt(surface.Cull) == 0,
            };
            if (surface.Emits)
            {
                read.Emission = surface.Emission;
                read.EmissionStrength = surface.EmissionStrength;
                read.EmissionTexture = surface.EmissionMap.Texture;
            }
            Shading(read, saved, family, surface);
            if (family == ShaderFamily.LilToon || family == ShaderFamily.Poiyomi)
            {
                Rim(read, saved, surface);
                Matcap(read, saved, family);
                Outline(read, saved, family, lilToon);
            }
            return read;
        }

        // A plain darker shade for shaders without a toon shading of their own; lilToon's shadow (and Poiyomi's Multilayer
        // Math, under lilToon's names): its colour over the base and its border and blur, which lilToon takes over half
        // Lambert (0 to 1) and MToon over N·L (-1 to 1); flat lighting has no shade.
        private static void Shading(Material read, SavedMaterial saved, ShaderFamily family, Surface surface)
        {
            var c = read.Color;
            read.ShadeColor = new Color(c.r * .8f, c.g * .8f, c.b * .82f, 1f);
            if (family != ShaderFamily.LilToon && family != ShaderFamily.Poiyomi) return;
            if (surface.FlatLit)
            {
                read.ShadeColor = new Color(c.r, c.g, c.b, 1f);
                return;
            }
            if (!surface.Shadows) return;
            var shadow = Color.Lerp(Color.white, saved.Color("_ShadowColor", new Color(.82f, .76f, .85f)), saved.Float("_ShadowStrength", 1));
            read.ShadeColor = new Color(c.r * shadow.r, c.g * shadow.g, c.b * shadow.b, 1f);
            read.ShadeCenter = Mathf.Clamp(2 * saved.Float("_ShadowBorder", .5f) - 1, -1, 1);
            read.ShadeWidth = Mathf.Clamp(2 * saved.Float("_ShadowBlur", .1f), 0, 2);
        }

        // lilToon-style rim light (lilToon's own, Poiyomi's LilToon style): its colour, as bright as its alpha says.
        private static void Rim(Material read, SavedMaterial saved, Surface surface)
        {
            if (!surface.Rim) return;
            var rim = saved.Color("_RimColor", new Color(.66f, .5f, .48f));
            read.RimColor = new Color(rim.r * rim.a, rim.g * rim.a, rim.b * rim.a, 1f);
            read.RimFresnelPower = Mathf.Clamp(saved.Float("_RimFresnelPower", 3.5f), .01f, 50f);
            read.RimLightingMix = Mathf.Clamp01(saved.Float("_RimEnableLighting", 1));
        }

        // A matcap the shader adds (lilToon's Add or Screen, Poiyomi's Add part): MToon's matcap only adds.
        private static void Matcap(Material read, SavedMaterial saved, ShaderFamily family)
        {
            if (family == ShaderFamily.LilToon)
            {
                int blend = Mathf.RoundToInt(saved.Float("_MatCapBlendMode"));
                if (!saved.On("_UseMatCap") || (blend != 1 && blend != 2)) return;
                read.MatcapTexture = saved.Texture("_MatCapTex").Texture;
                read.MatcapColor = Scaled(saved.Color("_MatCapColor", Color.white), saved.Float("_MatCapBlend", 1));
            }
            else
            {
                float add = saved.Float("_MatcapAdd");
                if (!saved.On("_MatcapEnable") || add <= 0) return;
                read.MatcapTexture = saved.Texture("_Matcap").Texture;
                read.MatcapColor = Scaled(saved.Color("_MatcapColor", Color.white), add * saved.Float("_MatcapIntensity", 1));
            }
            if (read.MatcapTexture == null) read.MatcapColor = Color.white;
        }

        // lilToon's outline shaders and Poiyomi's outlines: both give the width in centimetres, lit by the scene.
        private static void Outline(Material read, SavedMaterial saved, ShaderFamily family, string lilToon)
        {
            if (family == ShaderFamily.LilToon)
            {
                if (lilToon == null || lilToon.IndexOf("Outline", StringComparison.Ordinal) < 0) return;
                read.OutlineWidth = Mathf.Max(0, saved.Float("_OutlineWidth", .08f));
                read.OutlineWidthTexture = saved.Texture("_OutlineWidthMask").Texture;
                read.OutlineColor = saved.Color("_OutlineColor", new Color(.6f, .56f, .73f));
                read.OutlineLighting = Mathf.Clamp01(saved.Float("_OutlineEnableLighting", 1));
            }
            else
            {
                if (!saved.On("_EnableOutlines")) return;
                read.OutlineWidth = Mathf.Max(0, saved.Float("_LineWidth", 1));
                read.OutlineWidthTexture = saved.Texture("_OutlineMask").Texture;
                read.OutlineColor = saved.Color("_LineColor", Color.white);
            }
            read.OutlineColor.a = 1;
        }

        private static Color Scaled(Color color, float by) => new Color(color.r * by, color.g * by, color.b * by, 1f);

        /// <summary>The path of <paramref name="t"/> from the model's root, as VRM bindings name it.</summary>
        internal string PathOf(Transform t) => AnimationUtility.CalculateTransformPath(t, Root.transform);
    }
}
