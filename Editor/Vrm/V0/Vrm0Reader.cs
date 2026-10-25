using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.VrchatReady;
using UnityEditor;
using UnityEngine;
using VRM;

namespace Orbiters.MyAvatar.Editor.Vrm.V0
{
    /// <summary>
    /// Reads a VRM 0.x model's UniVRM components for Make it VRChat ready (<see cref="VrmSource"/>): its meta, first person
    /// point, look-at (bones and how far they turn), blend shape clips, spring bones and their sphere colliders, and the
    /// components themselves, which VRChat doesn't allow.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class Vrm0Reader : IVrmReader
    {
        static Vrm0Reader() => ReadyPlan.Readers.Add(new Vrm0Reader());

        public VrmSource Read(GameObject root)
        {
            var meta = root.GetComponent<VRMMeta>();
            var proxy = root.GetComponent<VRMBlendShapeProxy>();
            var firstPerson = root.GetComponent<VRMFirstPerson>();
            var springs = root.GetComponentsInChildren<VRMSpringBone>(true);
            if (meta == null && proxy == null && firstPerson == null && springs.Length == 0) return null;
            var source = new VrmSource { Format = "VRM 0.x" };
            if (meta != null && meta.Meta != null)
            {
                var m = meta.Meta;
                source.Title = m.Title;
                source.Authors = m.Author;
                source.License = m.LicenseType == LicenseType.Other && !string.IsNullOrWhiteSpace(m.OtherLicenseUrl) ? m.OtherLicenseUrl : m.LicenseType.ToString().Replace('_', ' ');
                source.Restricted = m.AllowedUser != AllowedUser.Everyone;
            }
            if (firstPerson != null && firstPerson.FirstPersonBone != null)
            {
                source.Head = firstPerson.FirstPersonBone;
                source.ViewOffset = firstPerson.FirstPersonOffset;
            }
            var eyes = root.GetComponent<VRMLookAtBoneApplyer>();
            if (eyes != null && eyes.LeftEye.Transform != null && eyes.RightEye.Transform != null)
            {
                source.EyeBones = true;
                source.LeftEye = eyes.LeftEye.Transform;
                source.RightEye = eyes.RightEye.Transform;
                source.LookUp = Range(eyes.VerticalUp);
                source.LookDown = Range(eyes.VerticalDown);
                source.LookInner = Range(eyes.HorizontalInner);
                source.LookOuter = Range(eyes.HorizontalOuter);
            }
            if (proxy != null && proxy.BlendShapeAvatar != null)
                foreach (var clip in proxy.BlendShapeAvatar.Clips.Where(c => c != null))
                    Expression(source, root.transform, clip);
            var colliders = new Dictionary<(VRMSpringBoneColliderGroup, int), VrmModel.Collider>();
            int n = 0;
            foreach (var spring in springs)
            {
                n++;
                var read = new VrmSource.Spring
                {
                    Name = string.IsNullOrWhiteSpace(spring.m_comment) ? "Spring " + n : spring.m_comment,
                    Stiffness = spring.m_stiffnessForce, GravityPower = spring.m_gravityPower, GravityDirection = spring.m_gravityDir,
                    Drag = spring.m_dragForce, Radius = spring.m_hitRadius,
                };
                read.Roots.AddRange((spring.RootBones ?? new List<Transform>()).Where(t => t != null).Distinct());
                foreach (var group in (spring.ColliderGroups ?? new VRMSpringBoneColliderGroup[0]).Where(g => g != null).Distinct())
                    for (int i = 0; i < (group.Colliders?.Length ?? 0); i++)
                    {
                        if (!colliders.TryGetValue((group, i), out var collider))
                            colliders[(group, i)] = collider = new VrmModel.Collider { Transform = group.transform, Offset = group.Colliders[i].Offset, Radius = group.Colliders[i].Radius };
                        read.Colliders.Add(collider);
                    }
                if (read.Roots.Count > 0) source.Springs.Add(read);
            }
            source.Components.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true).Where(c => c != null && c.GetType().Namespace == "VRM"));
            return source;
        }

        // How far the eyes turn at the curve's end, in degrees.
        private static float Range(CurveMapper curve) => curve != null ? Mathf.Clamp(curve.CurveYRangeDegree, 0f, 90f) : 10f;

        private static void Expression(VrmSource source, Transform root, BlendShapeClip clip)
        {
            var expression = new VrmModel.Expression { Preset = Preset(clip), Name = Name(clip) };
            if (expression.Preset == null && expression.Name == null) return;
            foreach (var binding in clip.Values ?? new BlendShapeBinding[0])
            {
                var at = string.IsNullOrEmpty(binding.RelativePath) ? root : root.Find(binding.RelativePath);
                var renderer = at != null && at.TryGetComponent<SkinnedMeshRenderer>(out var found) ? found : null;
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null || binding.Index < 0 || binding.Index >= mesh.blendShapeCount || binding.Weight <= 0f) continue;
                expression.Shapes.Add((renderer, mesh.GetBlendShapeName(binding.Index), Mathf.Clamp01(binding.Weight / 100f)));
            }
            if (expression.Shapes.Count > 0) source.Expressions.Add(expression);
        }

        // VRM 0.x has no Surprised preset: a clip of that name is one.
        private static VrmPreset? Preset(BlendShapeClip clip)
        {
            switch (clip.Preset)
            {
                case BlendShapePreset.A: return VrmPreset.Aa;
                case BlendShapePreset.I: return VrmPreset.Ih;
                case BlendShapePreset.U: return VrmPreset.Ou;
                case BlendShapePreset.E: return VrmPreset.Ee;
                case BlendShapePreset.O: return VrmPreset.Oh;
                case BlendShapePreset.Blink: return VrmPreset.Blink;
                case BlendShapePreset.Blink_L: return VrmPreset.BlinkLeft;
                case BlendShapePreset.Blink_R: return VrmPreset.BlinkRight;
                case BlendShapePreset.Joy: return VrmPreset.Happy;
                case BlendShapePreset.Angry: return VrmPreset.Angry;
                case BlendShapePreset.Sorrow: return VrmPreset.Sad;
                case BlendShapePreset.Fun: return VrmPreset.Relaxed;
                case BlendShapePreset.Unknown:
                    string name = (clip.BlendShapeName ?? "").Trim().ToLowerInvariant();
                    return name == "surprised" || name == "surprise" ? VrmPreset.Surprised : (VrmPreset?)null;
                default: return null;
            }
        }

        // Neutral and the look-at clips are VRM's own business (VRChat turns eye bones): they never become toggles.
        private static string Name(BlendShapeClip clip)
        {
            switch (clip.Preset)
            {
                case BlendShapePreset.Neutral:
                case BlendShapePreset.LookUp:
                case BlendShapePreset.LookDown:
                case BlendShapePreset.LookLeft:
                case BlendShapePreset.LookRight:
                    return null;
                case BlendShapePreset.Unknown:
                    return string.IsNullOrWhiteSpace(clip.BlendShapeName) ? (string.IsNullOrWhiteSpace(clip.name) ? null : clip.name) : clip.BlendShapeName.Trim();
                default:
                    return clip.Preset.ToString();
            }
        }
    }
}
