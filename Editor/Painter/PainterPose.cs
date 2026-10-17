using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// Poses for painting a hidden copy of an avatar: arms level as in a T-pose and legs well apart (the armpits and
    /// the inner legs open), fingers spread and straight, chosen blendshapes (the mouth open by default); natural poses for
    /// the copies shown with the painted one. Limbs are turned toward directions in the avatar's own space, so they land the
    /// same whatever pose the avatar was made in, symmetrically: Painter's symmetry mirrors across the avatar's middle.
    /// </summary>
    internal static class PainterPose
    {
        internal const string Relaxed = "Relaxed";
        private const string BodyPoseFolder = "Packages/orbiters.toolkit/Editor/Photoshoot/BodyPoses";

        /// <summary>The natural poses a copy can take: arms down, and the Photoshoot's body poses.</summary>
        internal static List<string> NaturalPoses()
        {
            var names = new List<string> { Relaxed };
            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { BodyPoseFolder }))
                names.Add(Label(AssetDatabase.GUIDToAssetPath(guid)));
            return names;
        }

        private static string Label(string path)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path).Replace("_pose", "").Replace('_', ' ');
            name = Regex.Replace(name, "(?<=[a-z])(?=[A-Z]|[0-9]+(?![a-z]))", " ");
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        private static AnimationClip Clip(string label)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { BodyPoseFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Label(path) == label) return AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            }
            return null;
        }

        /// <summary>Every transform and blendshape weight of a copy, to put it back between poses.</summary>
        internal sealed class Rest
        {
            private readonly Transform[] transforms;
            private readonly Vector3[] positions, scales;
            private readonly Quaternion[] rotations;
            private readonly Dictionary<SkinnedMeshRenderer, float[]> weights = new Dictionary<SkinnedMeshRenderer, float[]>();

            public Rest(GameObject root)
            {
                transforms = root.GetComponentsInChildren<Transform>(true);
                positions = transforms.Select(t => t.localPosition).ToArray();
                rotations = transforms.Select(t => t.localRotation).ToArray();
                scales = transforms.Select(t => t.localScale).ToArray();
                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (renderer.sharedMesh)
                        weights[renderer] = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.GetBlendShapeWeight).ToArray();
            }

            public void Restore()
            {
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (!transforms[i]) continue;
                    transforms[i].localPosition = positions[i];
                    transforms[i].localRotation = rotations[i];
                    transforms[i].localScale = scales[i];
                }
                foreach (var pair in weights)
                    if (pair.Key)
                        for (int i = 0; i < pair.Value.Length; i++) pair.Key.SetBlendShapeWeight(i, pair.Value[i]);
            }
        }

        /// <summary>A blendshape's key among all of a copy's: its renderer's path, then its name.</summary>
        internal static string Key(string renderer, string name) => renderer + "\n" + name;

        internal static string Key(PainterShape shape) => Key(shape.renderer, shape.name);

        /// <summary>The blendshape a key names (<see cref="Key(string, string)"/>), at full weight on the painted avatar.</summary>
        internal static PainterShape FromKey(string key)
        {
            int split = key.IndexOf('\n');
            return new PainterShape { renderer = key.Substring(0, split), name = key.Substring(split + 1) };
        }

        /// <summary>A copy of the choices (the avatar keeps its own).</summary>
        internal static PainterPoseOptions Copy(PainterPoseOptions options) => JsonUtility.FromJson<PainterPoseOptions>(JsonUtility.ToJson(options));

        /// <summary>Poses the painted copy.</summary>
        internal static void Paint(GameObject root, PainterPoseOptions options)
        {
            var animator = root.GetComponent<Animator>();
            if (animator && animator.isHuman)
            {
                if (options.paintingPose) Open(animator);
                if (options.spreadFingers) Fingers(animator);
            }
            Apply(root, options.shapes.Where(s => s.painted));
        }

        /// <summary>Poses a copy naturally: arms down, or one of the Photoshoot's body poses.</summary>
        internal static void Natural(GameObject root, string pose, PainterPoseOptions options)
        {
            Apply(root, options.shapes.Where(s => s.copies));
            var animator = root.GetComponent<Animator>();
            if (!animator || !animator.isHuman) return;
            if (pose == Relaxed)
            {
                Arms(animator, new Vector3(.32f, -1f, .06f));
                return;
            }
            var clip = Clip(pose);
            if (clip) Orbiters.Toolkit.Editor.Photoshoot.PhotoshootService.SampleBodyPose(root, clip);
        }

        /// <summary>
        /// How an unbuilt copy's clothing on armatures of its own follows its bones (a build merges them): noted at rest,
        /// parents first, for <see cref="Follow"/>.
        /// </summary>
        internal static List<FollowLink> Links(GameObject root)
        {
            int Depth(Transform t) { int depth = 0; for (; t; t = t.parent) depth++; return depth; }
            return AttachmentFollow.Links(root.transform).OrderBy(l => Depth(l.Follower)).ToList();
        }

        /// <summary>Puts the clothing back on the posed bones.</summary>
        internal static void Follow(IEnumerable<FollowLink> links)
        {
            foreach (var link in links)
                if (link.Follower && link.Target) link.Apply();
        }

        private static void Apply(GameObject root, IEnumerable<PainterShape> shapes)
        {
            foreach (var shape in shapes)
            {
                var renderer = Find(root, shape.renderer);
                int index = renderer && renderer.sharedMesh ? renderer.sharedMesh.GetBlendShapeIndex(shape.name) : -1;
                if (index >= 0) renderer.SetBlendShapeWeight(index, shape.weight);
            }
        }

        private static SkinnedMeshRenderer Find(GameObject root, string path)
        {
            var transform = string.IsNullOrEmpty(path) || path == "." ? root.transform : root.transform.Find(path);
            return transform ? transform.GetComponent<SkinnedMeshRenderer>() : null;
        }

        internal static string PathOf(GameObject root, Renderer renderer)
        {
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
            return string.IsNullOrEmpty(path) ? "." : path;
        }

        /// <summary>Every blendshape of the copy's skinned meshes: (renderer path, renderer name, shape).</summary>
        internal static IEnumerable<(string Renderer, string RendererName, string Name)> Shapes(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.sharedMesh) continue;
                string path = PathOf(root, renderer);
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++) yield return (path, renderer.name, renderer.sharedMesh.GetBlendShapeName(i));
            }
        }

        // Arms level and straight out, as in a T-pose; legs well apart (about 27 degrees from vertical), knees straight.
        private static void Open(Animator animator)
        {
            Arms(animator, new Vector3(1f, 0f, 0f));
            foreach (bool left in new[] { true, false })
            {
                var upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
                var lower = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
                var foot = animator.GetBoneTransform(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
                if (!upper || !lower || !foot) continue;
                var direction = Side(animator, upper, new Vector3(.5f, -1f, 0f));
                Aim(upper, lower, direction);
                Aim(lower, foot, direction);
            }
        }

        /// <summary>
        /// From the scene to the project's space: the avatar's middle (between its shoulders and its hips, along its sides) at
        /// X = 0, its hips over the origin, facing +Z, its feet's level at Y = 0. Painter's symmetry mirrors across X = 0, so
        /// a stroke on one side lands on the same place on the other.
        /// </summary>
        internal static Matrix4x4 Frame(GameObject root)
        {
            var basis = root.transform;
            var forward = Vector3.ProjectOnPlane(basis.forward, Vector3.up);
            var rotation = Quaternion.LookRotation(forward.sqrMagnitude > 1e-6f ? forward : Vector3.forward, Vector3.up);
            var center = basis.position;
            var animator = root.GetComponent<Animator>();
            if (animator && animator.isHuman)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var pairs = new[]
                {
                    (animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg), animator.GetBoneTransform(HumanBodyBones.RightUpperLeg)),
                    (animator.GetBoneTransform(HumanBodyBones.LeftUpperArm), animator.GetBoneTransform(HumanBodyBones.RightUpperArm)),
                }.Where(p => p.Item1 && p.Item2).Select(p => (p.Item1.position + p.Item2.position) * .5f).ToList();
                if (hips)
                {
                    var right = rotation * Vector3.right;
                    var middle = pairs.Count > 0 ? pairs.Aggregate(Vector3.zero, (sum, p) => sum + p) / pairs.Count : hips.position;
                    center = new Vector3(hips.position.x, basis.position.y, hips.position.z);
                    // Along the sides, the skeleton's middle (the hips may sit off it); front to back, the hips.
                    center += right * Vector3.Dot(middle - center, right);
                }
            }
            return Matrix4x4.TRS(center, rotation, Vector3.one).inverse;
        }

        // Both arms along a direction (its X mirrored for each side, outward), elbows and wrists straight.
        private static void Arms(Animator animator, Vector3 direction)
        {
            foreach (bool left in new[] { true, false })
            {
                var upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                var lower = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                var hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var middle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
                if (!upper || !lower || !hand) continue;
                var along = Side(animator, upper, direction);
                Aim(upper, lower, along);
                Aim(lower, hand, along);
                if (middle) Aim(hand, middle, along);
            }
        }

        // The direction in world space, its X pointing away from the body on the bone's side.
        private static Vector3 Side(Animator animator, Transform bone, Vector3 local)
        {
            var root = animator.transform;
            float side = Mathf.Sign(root.InverseTransformPoint(bone.position).x);
            if (side == 0f) side = 1f;
            return root.TransformDirection(new Vector3(local.x * side, local.y, local.z)).normalized;
        }

        // Turns a bone so its child lies along the direction (the smallest turn: the bone keeps its twist).
        private static void Aim(Transform bone, Transform child, Vector3 direction)
        {
            var current = child.position - bone.position;
            if (current.sqrMagnitude < 1e-10f || direction.sqrMagnitude < 1e-10f) return;
            bone.rotation = Quaternion.FromToRotation(current, direction) * bone.rotation;
        }

        private static readonly (HumanBodyBones Proximal, HumanBodyBones Intermediate, HumanBodyBones Distal, float Angle)[] LeftFingers =
        {
            (HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal, 14f),
            (HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal, 0f),
            (HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal, -13f),
            (HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal, -26f),
        };

        // Each finger straight, fanned out from the middle one in the palm's plane; the thumb away from the index.
        private static void Fingers(Animator animator)
        {
            foreach (bool left in new[] { true, false })
            {
                var hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var middle = animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
                var index = animator.GetBoneTransform(left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
                var little = animator.GetBoneTransform(left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
                if (!hand || !middle) continue;
                var forward = (middle.position - hand.position).normalized;
                var across = index && little ? (index.position - little.position).normalized : Vector3.zero;
                var palm = Vector3.Cross(forward, across);
                if (palm.sqrMagnitude < 1e-8f) continue;
                palm.Normalize();
                foreach (var (proximal, intermediate, distal, angle) in LeftFingers)
                {
                    var p = animator.GetBoneTransform(left ? proximal : Mirror(proximal));
                    var i = animator.GetBoneTransform(left ? intermediate : Mirror(intermediate));
                    var d = animator.GetBoneTransform(left ? distal : Mirror(distal));
                    if (!p || !i) continue;
                    // The palm's normal is forward × across: a positive angle turns toward the index, whichever hand.
                    var direction = Quaternion.AngleAxis(angle, palm) * forward;
                    Aim(p, i, direction);
                    if (d) Aim(i, d, direction);
                }
                var thumbProximal = animator.GetBoneTransform(left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
                var thumbIntermediate = animator.GetBoneTransform(left ? HumanBodyBones.LeftThumbIntermediate : HumanBodyBones.RightThumbIntermediate);
                var thumbDistal = animator.GetBoneTransform(left ? HumanBodyBones.LeftThumbDistal : HumanBodyBones.RightThumbDistal);
                if (thumbProximal && thumbIntermediate && across.sqrMagnitude > 0f)
                {
                    var thumb = (forward * .55f + across * .85f).normalized;
                    Aim(thumbProximal, thumbIntermediate, thumb);
                    if (thumbDistal) Aim(thumbIntermediate, thumbDistal, thumb);
                }
            }
        }

        private static HumanBodyBones Mirror(HumanBodyBones bone) => (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), bone.ToString().Replace("Left", "Right"));

        /// <summary>
        /// The blendshapes set by default: the viseme "aa" and a grin (or smile) at full weight on the painted copy, the lips
        /// apart and the teeth and inside of the mouth in view.
        /// </summary>
        internal static List<PainterShape> MouthOpen(GameObject root)
        {
            var shapes = new List<PainterShape>();
            var face = FaceTracking.FaceTrackingDetection.Face(root.transform);
            if (!face || !face.sharedMesh) return shapes;
            var mesh = face.sharedMesh;
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            string aa = descriptor && descriptor.VisemeBlendShapes != null && descriptor.VisemeBlendShapes.Length > 10 && descriptor.VisemeSkinnedMesh == face
                ? descriptor.VisemeBlendShapes[10] : null;
            int Find(params string[] patterns)
            {
                for (int p = 0; p < patterns.Length; p++)
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                        if (Regex.IsMatch(mesh.GetBlendShapeName(i), patterns[p], RegexOptions.IgnoreCase)) return i;
                return -1;
            }
            int open = !string.IsNullOrEmpty(aa) ? mesh.GetBlendShapeIndex(aa) : -1;
            if (open < 0) open = Find(@"^v(isemes?)?[_ .-]?aa$", @"(^|[_ .-])aa$", @"^(mouth[_ .-]?)?a$", "jaw.?open", "mouth.?open");
            int grin = Find("grin", "smile");
            foreach (int index in new[] { open, grin }.Where(i => i >= 0).Distinct())
                shapes.Add(new PainterShape { renderer = PathOf(root, face), name = mesh.GetBlendShapeName(index) });
            return shapes;
        }

        /// <summary>
        /// The painted avatar, then each copy in a natural pose, baked where it shows in the project (the parts, and how many
        /// are the painted one's). Clothing on armatures of its own (an unbuilt copy) follows the bones through
        /// <paramref name="follow"/>. The copy is back at rest after.
        /// </summary>
        internal static (List<Part> Parts, int Main) Posed(GameObject copy, Rest rest, IEnumerable<FollowLink> follow, PainterPoseOptions options)
        {
            var links = follow.ToList();
            var baked = new List<Part>();
            rest.Restore();
            // Centered from the avatar at rest: the pose turns limbs only, the middle stays where it is.
            var frame = Frame(copy);
            Paint(copy, options);
            Follow(links);
            baked.AddRange(Bake(copy, frame));
            int main = baked.Count;
            var bounds = Bounds(baked);
            for (int i = 0; i < options.copies.Count; i++)
            {
                rest.Restore();
                Natural(copy, options.copies[i], options);
                Follow(links);
                float step = i + 1;
                // Above and below stay on the symmetry plane; beside, they alternate right and left.
                var offset = options.placement == 0 ? new Vector3(0f, bounds.size.y * 1.08f * step, 0f)
                    : options.placement == 1 ? new Vector3(0f, -bounds.size.y * 1.08f * step, 0f)
                    : Vector3.right * (bounds.size.x * .6f + bounds.size.x * .9f * (i / 2 + 1)) * (i % 2 == 0 ? 1f : -1f);
                baked.AddRange(Bake(copy, Matrix4x4.Translate(offset) * frame));
            }
            rest.Restore();
            return (baked, main);
        }

        /// <summary>
        /// Starts building the avatar as it uploads (clothing merged onto the armature), without the upload's optimization (the
        /// meshes stay whole): the copy a project's mesh is posed from.
        /// </summary>
        internal static HiddenAvatarBuild Build(MyAvatar avatar) =>
            HiddenAvatarBuild.Start(TextureOptimization.AvatarRoot(avatar), type => type.Name == "ParameterCompressorHook", copy =>
            {
                foreach (var own in copy.GetComponentsInChildren<MyAvatar>(true)) own.uploadOptimization.level = OptimizationLevel.Off;
            });

        /// <summary>A mesh of the posed copy, as it shows now.</summary>
        internal sealed class Part
        {
            public string Name;
            public Mesh Mesh;
            public Matrix4x4 ToWorld;
            public Material[] Materials;
            public bool Mirrored;
            public Renderer Source;
        }

        /// <summary>
        /// The meshes the copy shows now (renderers on and in active objects, skinned meshes with their blendshapes), from the
        /// scene into <paramref name="space"/> (see <see cref="Frame"/>). The meshes are new: <see cref="Release"/> them.
        /// </summary>
        internal static List<Part> Bake(GameObject root, Matrix4x4 space)
        {
            var parts = new List<Part>();
            var move = space;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!renderer.enabled || renderer.CompareTag("EditorOnly")) continue;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    if (!skinned.sharedMesh) continue;
                    var mesh = new Mesh { name = skinned.sharedMesh.name, hideFlags = HideFlags.HideAndDontSave };
                    skinned.BakeMesh(mesh);
                    var basis = skinned.rootBone ? skinned.rootBone : skinned.transform;
                    parts.Add(new Part
                    {
                        Name = skinned.name, Mesh = mesh, ToWorld = move * SkinnedMeshBounds.BakedToWorld(skinned), Materials = skinned.sharedMaterials,
                        Mirrored = basis.localToWorldMatrix.determinant < 0f, Source = skinned,
                    });
                }
                else if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh)
                {
                    parts.Add(new Part
                    {
                        Name = renderer.name, Mesh = Object.Instantiate(filter.sharedMesh), ToWorld = move * renderer.localToWorldMatrix,
                        Materials = renderer.sharedMaterials, Source = renderer,
                    });
                    parts[parts.Count - 1].Mesh.hideFlags = HideFlags.HideAndDontSave;
                }
            }
            return parts;
        }

        internal static void Release(IEnumerable<Part> parts)
        {
            foreach (var part in parts) if (part.Mesh) Object.DestroyImmediate(part.Mesh);
        }

        /// <summary>The space the parts take, in world space.</summary>
        internal static Bounds Bounds(IEnumerable<Part> parts)
        {
            Bounds? all = null;
            foreach (var part in parts)
            {
                var b = part.Mesh.bounds;
                foreach (var corner in new[] { new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(-1, 1, -1), new Vector3(1, 1, -1),
                                               new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(-1, 1, 1), new Vector3(1, 1, 1) })
                {
                    var p = part.ToWorld.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, corner));
                    if (all == null) all = new Bounds(p, Vector3.zero);
                    else { var grown = all.Value; grown.Encapsulate(p); all = grown; }
                }
            }
            return all ?? new Bounds(Vector3.up, Vector3.one);
        }
    }
}
