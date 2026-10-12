using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF;
using UniGLTF.Extensions.VRMC_vrm;
using UnityEditor;
using UnityEngine;
using UniVRM10;
using VRM10.MToon10;
using Orbiters.MyAvatar.Editor.Vrm.Gltf;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Vrm.V1
{
    /// <summary>
    /// VRM 1.0 with UniVRM: MToon 1.0 materials, the T-pose, a <see cref="Vrm10Instance"/> holding the meta, expressions,
    /// look-at from the eyes and first person, spring joints per PhysBone chain (a branch is a spring of its own, the
    /// last joint pointing at an end node), sphere, capsule and plane colliders, then UniVRM's VRM 1.0 export.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class Vrm1Writer : IVrmWriter
    {
        static Vrm1Writer() => VrmExport.Writers.Add(new Vrm1Writer());

        public int Version => 1;
        public string UniVrm => UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.vrmc.vrm")?.version;

        public byte[] Write(VrmModel model)
        {
            var root = model.Root;
            var made = new List<Object>();
            try
            {
                Materials(model, made);
                VrmGltf.TPose(root);

                var vrm = ScriptableObject.CreateInstance<VRM10Object>(); made.Add(vrm);
                var settings = model.Meta;
                var meta = vrm.Meta;
                meta.Name = model.Title;
                meta.Version = settings.modelVersion;
                meta.Authors = (settings.authors ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                meta.ContactInformation = settings.contact;
                if (!string.IsNullOrWhiteSpace(settings.reference)) meta.References = new List<string> { settings.reference };
                meta.Thumbnail = model.Thumbnail;
                meta.AvatarPermission = (AvatarPermissionType)Mathf.Clamp(settings.allowedUsers, 0, 2);
                meta.ViolentUsage = settings.violentUse;
                meta.SexualUsage = settings.sexualUse;
                meta.CommercialUsage = settings.commercialUse ? CommercialUsageType.personalProfit : CommercialUsageType.personalNonProfit;
                meta.CreditNotation = CreditNotationType.required;
                meta.Redistribution = settings.redistribution;
                meta.Modification = !settings.modification ? ModificationType.prohibited
                    : settings.redistribution ? ModificationType.allowModificationRedistribution : ModificationType.allowModification;

                foreach (var expression in model.Expressions)
                {
                    var clip = ScriptableObject.CreateInstance<VRM10Expression>(); made.Add(clip);
                    clip.name = expression.Name ?? expression.Preset.ToString();
                    clip.MorphTargetBindings = expression.Shapes.Select(s => (s, index: s.Renderer.sharedMesh.GetBlendShapeIndex(s.Shape))).Where(x => x.index >= 0)
                        .Select(x => new MorphTargetBinding(model.PathOf(x.s.Renderer.transform), x.index, Mathf.Clamp01(x.s.Weight))).ToArray();
                    if (expression.Preset is VrmPreset preset) Assign(vrm.Expression, preset, clip);
                    else vrm.Expression.CustomClips.Add(clip);
                }
                if (model.LeftEye != null && model.RightEye != null) vrm.LookAt.LookAtType = LookAtType.bone;
                vrm.LookAt.OffsetFromHead = model.ViewOffset;
                vrm.FirstPerson.SetDefault(root.transform);

                var instance = root.AddComponent<Vrm10Instance>();
                instance.Vrm = vrm;
                instance.UpdateType = Vrm10Instance.UpdateTypes.None;
                Springs(model, instance);

                // As UniVRM's own export window does, with morph targets storing only the vertices they move.
                var gltf = new GltfExportSettings { UseSparseAccessorForMorphTarget = true };
                using (var arrays = new NativeArrayManager())
                {
                    var converter = new ModelExporter();
                    var exported = converter.Export(gltf, arrays, root);
                    exported.ConvertCoordinate(VrmLib.Coordinates.Vrm1, ignoreVrm: false);
                    var exporter = new Vrm10Exporter(gltf, textureSerializer: VrmGltf.Textures());
                    exporter.Export(root, exported, converter, new VrmLib.ExportArgs { sparse = true }, meta);
                    return exporter.Storage.ToGlbBytes();
                }
            }
            finally
            {
                foreach (var value in made) if (value != null) Object.DestroyImmediate(value);
            }
        }

        private static void Assign(VRM10ObjectExpression expressions, VrmPreset preset, VRM10Expression clip)
        {
            switch (preset)
            {
                case VrmPreset.Aa: expressions.Aa = clip; break;
                case VrmPreset.Ih: expressions.Ih = clip; break;
                case VrmPreset.Ou: expressions.Ou = clip; break;
                case VrmPreset.Ee: expressions.Ee = clip; break;
                case VrmPreset.Oh: expressions.Oh = clip; break;
                case VrmPreset.Blink: expressions.Blink = clip; break;
                case VrmPreset.BlinkLeft: expressions.BlinkLeft = clip; break;
                case VrmPreset.BlinkRight: expressions.BlinkRight = clip; break;
                case VrmPreset.Happy: expressions.Happy = clip; break;
                case VrmPreset.Angry: expressions.Angry = clip; break;
                case VrmPreset.Sad: expressions.Sad = clip; break;
                case VrmPreset.Relaxed: expressions.Relaxed = clip; break;
                case VrmPreset.Surprised: expressions.Surprised = clip; break;
            }
        }

        // ---- Springs ----

        private static void Springs(VrmModel model, Vrm10Instance instance)
        {
            var groups = new Dictionary<VrmModel.Collider, VRM10SpringBoneColliderGroup>();
            foreach (var collider in model.Colliders.Where(c => c.Transform != null))
            {
                var shape = collider.Transform.gameObject.AddComponent<VRM10SpringBoneCollider>();
                shape.Radius = collider.Radius;
                shape.Offset = collider.Offset;
                if (collider.Plane) { shape.ColliderType = VRM10SpringBoneColliderTypes.Plane; shape.Normal = collider.Tail; }
                else if (collider.Capsule) { shape.ColliderType = collider.Inside ? VRM10SpringBoneColliderTypes.CapsuleInside : VRM10SpringBoneColliderTypes.Capsule; shape.Tail = collider.Tail; }
                else shape.ColliderType = collider.Inside ? VRM10SpringBoneColliderTypes.SphereInside : VRM10SpringBoneColliderTypes.Sphere;
                var group = collider.Transform.gameObject.AddComponent<VRM10SpringBoneColliderGroup>();
                group.Name = collider.Transform.name;
                group.Colliders.Add(shape);
                instance.SpringBone.ColliderGroups.Add(group);
                groups[collider] = group;
            }
            foreach (var source in model.Springs)
            {
                var spring = new Vrm10InstanceSpringBone.Spring(source.Name);
                var joints = source.Joints.ToList();
                // The last joint turns toward a node at the chain's end.
                var end = new GameObject(joints[joints.Count - 1].name + " end").transform;
                end.SetParent(joints[joints.Count - 1], false);
                end.localPosition = source.End;
                joints.Add(end);
                foreach (var t in joints)
                {
                    var joint = t.gameObject.AddComponent<VRM10SpringBoneJoint>();
                    joint.m_stiffnessForce = source.Stiffness;
                    joint.m_gravityPower = source.GravityPower;
                    joint.m_gravityDir = source.GravityDirection;
                    joint.m_dragForce = source.Drag;
                    joint.m_jointRadius = source.Radius;
                    spring.Joints.Add(joint);
                }
                spring.ColliderGroups.AddRange(source.Colliders.Where(groups.ContainsKey).Select(c => groups[c]).Distinct());
                instance.SpringBone.Springs.Add(spring);
            }
        }

        // ---- Materials ----

        private static void Materials(VrmModel model, List<Object> made)
        {
            var shader = Shader.Find("VRM10/MToon10");
            if (shader == null) throw new InvalidOperationException("UniVRM's MToon 1.0 shader isn't in the project.");
            var converted = new Dictionary<Material, Material>();
            foreach (var renderer in model.Root.GetComponentsInChildren<Renderer>(true))
            {
                var list = renderer.sharedMaterials;
                for (int i = 0; i < list.Length; i++)
                {
                    var source = list[i];
                    if (source == null || !model.Materials.TryGetValue(source, out var read)) continue;
                    if (!converted.TryGetValue(source, out var mtoon))
                    {
                        converted[source] = mtoon = new Material(shader) { name = source.name };
                        made.Add(mtoon);
                        var context = new MToon10Context(mtoon)
                        {
                            AlphaMode = read.Alpha == VrmAlpha.Transparent ? MToon10AlphaMode.Transparent : read.Alpha == VrmAlpha.Cutout ? MToon10AlphaMode.Cutout : MToon10AlphaMode.Opaque,
                            AlphaCutoff = read.Cutoff,
                            DoubleSidedMode = read.DoubleSided ? MToon10DoubleSidedMode.On : MToon10DoubleSidedMode.Off,
                            BaseColorFactorSrgb = read.Color,
                            BaseColorTexture = read.MainTexture,
                            ShadeColorFactorSrgb = read.ShadeColor,
                            ShadeColorTexture = read.MainTexture,
                            NormalTexture = read.NormalTexture,
                            NormalTextureScale = read.NormalScale,
                            ShadingShiftFactor = -.05f,
                            ShadingToonyFactor = .9f,
                            EmissiveFactorLinear = read.Emission.linear,
                            EmissiveTexture = read.EmissionTexture,
                            TextureScale = read.Scale,
                            TextureOffset = read.Offset,
                        };
                        context.Validate();
                    }
                    list[i] = mtoon;
                }
                renderer.sharedMaterials = list;
            }
        }
    }
}
