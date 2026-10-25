using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.VrchatReady;
using UniGLTF.Extensions.VRMC_vrm;
using UnityEditor;
using UnityEngine;
using UniVRM10;

namespace Orbiters.MyAvatar.Editor.Vrm.V1
{
    /// <summary>
    /// Reads a VRM 1.0 model's UniVRM components for Make it VRChat ready (<see cref="VrmSource"/>): the meta, the look-at
    /// (its origin, bones or expressions, how far the eyes turn), the expressions, the spring chains (each joint's settings
    /// averaged over the chain: a PhysBone has one of each) with their sphere, capsule and plane colliders, and the
    /// components themselves. Node constraints are counted, not converted.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class Vrm1Reader : IVrmReader
    {
        static Vrm1Reader() => ReadyPlan.Readers.Add(new Vrm1Reader());

        public VrmSource Read(GameObject root)
        {
            var instance = root.GetComponent<Vrm10Instance>();
            if (instance == null) return null;
            var source = new VrmSource { Format = "VRM 1.0" };
            var vrm = instance.Vrm;
            var animator = root.GetComponent<Animator>();
            var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (vrm != null)
            {
                var meta = vrm.Meta;
                if (meta != null)
                {
                    source.Title = meta.Name;
                    source.Authors = meta.Authors != null ? string.Join(", ", meta.Authors.Where(a => !string.IsNullOrWhiteSpace(a))) : null;
                    source.License = !string.IsNullOrWhiteSpace(meta.OtherLicenseUrl) ? meta.OtherLicenseUrl
                        : (meta.Redistribution ? "Redistribution allowed" : "Redistribution prohibited") + ", " + meta.CommercialUsage;
                    source.Restricted = meta.AvatarPermission != AvatarPermissionType.everyone;
                }
                var lookAt = vrm.LookAt;
                if (lookAt != null && head != null)
                {
                    source.Head = head;
                    source.ViewOffset = lookAt.OffsetFromHead;
                    source.EyeBones = lookAt.LookAtType == LookAtType.bone;
                    if (source.EyeBones && animator != null)
                    {
                        source.LeftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
                        source.RightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
                        source.LookUp = Range(lookAt.VerticalUp);
                        source.LookDown = Range(lookAt.VerticalDown);
                        source.LookInner = Range(lookAt.HorizontalInner);
                        source.LookOuter = Range(lookAt.HorizontalOuter);
                    }
                }
                if (vrm.Expression != null)
                    foreach (var (preset, clip) in vrm.Expression.Clips)
                        Expression(source, root.transform, preset, clip);
            }
            Springs(source, instance);
            source.Constraints = root.GetComponentsInChildren<Vrm10RollConstraint>(true).Length + root.GetComponentsInChildren<Vrm10AimConstraint>(true).Length +
                                 root.GetComponentsInChildren<Vrm10RotationConstraint>(true).Length;
            source.Components.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true).Where(c => c != null && c.GetType().Namespace == "UniVRM10"));
            source.Components.AddRange(root.GetComponentsInChildren<UniHumanoid.Humanoid>(true));
            return source;
        }

        private static float Range(CurveMapper curve) => curve != null ? Mathf.Clamp(curve.CurveYRangeDegree, 0f, 90f) : 10f;

        private static void Expression(VrmSource source, Transform root, ExpressionPreset preset, VRM10Expression clip)
        {
            if (clip == null) return;
            var expression = new VrmModel.Expression { Preset = Preset(preset), Name = Name(preset, clip) };
            if (expression.Preset == null && expression.Name == null) return;
            foreach (var binding in clip.MorphTargetBindings ?? new MorphTargetBinding[0])
            {
                var at = string.IsNullOrEmpty(binding.RelativePath) ? root : root.Find(binding.RelativePath);
                var renderer = at != null && at.TryGetComponent<SkinnedMeshRenderer>(out var found) ? found : null;
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null || binding.Index < 0 || binding.Index >= mesh.blendShapeCount || binding.Weight <= 0f) continue;
                expression.Shapes.Add((renderer, mesh.GetBlendShapeName(binding.Index), Mathf.Clamp01(binding.Weight)));
            }
            if (expression.Shapes.Count > 0) source.Expressions.Add(expression);
        }

        private static VrmPreset? Preset(ExpressionPreset preset)
        {
            switch (preset)
            {
                case ExpressionPreset.aa: return VrmPreset.Aa;
                case ExpressionPreset.ih: return VrmPreset.Ih;
                case ExpressionPreset.ou: return VrmPreset.Ou;
                case ExpressionPreset.ee: return VrmPreset.Ee;
                case ExpressionPreset.oh: return VrmPreset.Oh;
                case ExpressionPreset.blink: return VrmPreset.Blink;
                case ExpressionPreset.blinkLeft: return VrmPreset.BlinkLeft;
                case ExpressionPreset.blinkRight: return VrmPreset.BlinkRight;
                case ExpressionPreset.happy: return VrmPreset.Happy;
                case ExpressionPreset.angry: return VrmPreset.Angry;
                case ExpressionPreset.sad: return VrmPreset.Sad;
                case ExpressionPreset.relaxed: return VrmPreset.Relaxed;
                case ExpressionPreset.surprised: return VrmPreset.Surprised;
                default: return null;
            }
        }

        // Neutral and the look-at expressions are VRM's own business: they never become toggles.
        private static string Name(ExpressionPreset preset, VRM10Expression clip)
        {
            switch (preset)
            {
                case ExpressionPreset.neutral:
                case ExpressionPreset.lookUp:
                case ExpressionPreset.lookDown:
                case ExpressionPreset.lookLeft:
                case ExpressionPreset.lookRight:
                    return null;
                case ExpressionPreset.custom:
                    return string.IsNullOrWhiteSpace(clip.name) ? null : clip.name.Trim();
                default:
                    string name = preset.ToString();
                    return char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
        }

        // Each spring is a chain of joints, the last one its tip. Joint settings may differ along the chain: a PhysBone has
        // one value each, so the turning joints' average (the tip's own settings move nothing).
        private static void Springs(VrmSource source, Vrm10Instance instance)
        {
            var colliders = new Dictionary<VRM10SpringBoneCollider, VrmModel.Collider>();
            int n = 0;
            foreach (var spring in instance.SpringBone?.Springs ?? new List<Vrm10InstanceSpringBone.Spring>())
            {
                n++;
                var joints = (spring.Joints ?? new List<VRM10SpringBoneJoint>()).Where(j => j != null).ToList();
                if (joints.Count == 0) continue;
                var turning = joints.Count > 1 ? joints.Take(joints.Count - 1).ToList() : joints;
                var read = new VrmSource.Spring
                {
                    Name = string.IsNullOrWhiteSpace(spring.Name) ? "Spring " + n : spring.Name,
                    Chain = joints.Select(j => j.transform).ToList(),
                    Stiffness = turning.Average(j => j.m_stiffnessForce),
                    GravityPower = turning.Average(j => j.m_gravityPower),
                    Drag = turning.Average(j => j.m_dragForce),
                    Radius = turning.Average(j => j.m_jointRadius),
                    GravityDirection = turning[0].m_gravityDir,
                };
                read.Roots.Add(joints[0].transform);
                foreach (var group in (spring.ColliderGroups ?? new List<VRM10SpringBoneColliderGroup>()).Where(g => g != null).Distinct())
                    foreach (var shape in (group.Colliders ?? new List<VRM10SpringBoneCollider>()).Where(c => c != null))
                    {
                        if (!colliders.TryGetValue(shape, out var collider)) colliders[shape] = collider = Collider(shape);
                        read.Colliders.Add(collider);
                    }
                source.Springs.Add(read);
            }
        }

        private static VrmModel.Collider Collider(VRM10SpringBoneCollider shape)
        {
            var collider = new VrmModel.Collider { Transform = shape.transform, Offset = shape.Offset, Radius = shape.Radius };
            switch (shape.ColliderType)
            {
                case VRM10SpringBoneColliderTypes.Capsule:
                case VRM10SpringBoneColliderTypes.CapsuleInside:
                    collider.Capsule = true;
                    collider.Tail = shape.Tail;
                    collider.Inside = shape.ColliderType == VRM10SpringBoneColliderTypes.CapsuleInside;
                    break;
                case VRM10SpringBoneColliderTypes.Plane:
                    collider.Plane = true;
                    collider.Tail = shape.Normal;
                    break;
                case VRM10SpringBoneColliderTypes.SphereInside:
                    collider.Inside = true;
                    break;
            }
            return collider;
        }
    }
}
