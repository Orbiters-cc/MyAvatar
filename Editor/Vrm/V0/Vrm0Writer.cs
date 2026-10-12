using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF;
using UnityEditor;
using UnityEngine;
using VRM;
using Orbiters.MyAvatar.Editor.Vrm.Gltf;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Vrm.V0
{
    /// <summary>
    /// VRM 0.x (VSeeFace and most apps) with UniVRM: MToon materials, the T-pose, the meta, blendshape clips, first person
    /// and look-at from the eyes, springs per PhysBone, sphere colliders (a capsule becomes a row of spheres; planes have
    /// none in 0.x), then UniVRM's normalized export.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class Vrm0Writer : IVrmWriter
    {
        static Vrm0Writer() => VrmExport.Writers.Add(new Vrm0Writer());

        public int Version => 0;
        public string UniVrm => PackageVersion("com.vrmc.univrm");

        internal static string PackageVersion(string name) => UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + name)?.version;

        public byte[] Write(VrmModel model)
        {
            var root = model.Root;
            var made = new List<Object>();
            try
            {
                Materials(model, made);
                VrmGltf.TPose(root);
                UniqueNames(root, root.GetComponent<Animator>());

                var meta = ScriptableObject.CreateInstance<VRMMetaObject>(); made.Add(meta);
                var settings = model.Meta;
                meta.Title = model.Title;
                meta.Version = settings.modelVersion;
                meta.Author = settings.authors;
                meta.ContactInformation = settings.contact;
                meta.Reference = settings.reference;
                meta.Thumbnail = model.Thumbnail;
                meta.AllowedUser = (AllowedUser)Mathf.Clamp(settings.allowedUsers, 0, 2);
                meta.ViolentUssage = settings.violentUse ? UssageLicense.Allow : UssageLicense.Disallow;
                meta.SexualUssage = settings.sexualUse ? UssageLicense.Allow : UssageLicense.Disallow;
                meta.CommercialUssage = settings.commercialUse ? UssageLicense.Allow : UssageLicense.Disallow;
                meta.LicenseType = License(settings);
                root.AddComponent<VRMMeta>().Meta = meta;

                var proxy = root.AddComponent<VRMBlendShapeProxy>();
                var clips = ScriptableObject.CreateInstance<BlendShapeAvatar>(); made.Add(clips);
                foreach (var expression in model.Expressions)
                {
                    var clip = ScriptableObject.CreateInstance<BlendShapeClip>(); made.Add(clip);
                    clip.Preset = expression.Preset is VrmPreset preset ? Preset(preset) : BlendShapePreset.Unknown;
                    clip.BlendShapeName = expression.Name ?? clip.Preset.ToString();
                    clip.Values = expression.Shapes.Select(s => (s, index: s.Renderer.sharedMesh.GetBlendShapeIndex(s.Shape))).Where(x => x.index >= 0)
                        .Select(x => new BlendShapeBinding { RelativePath = model.PathOf(x.s.Renderer.transform), Index = x.index, Weight = x.s.Weight * 100f }).ToArray();
                    clips.Clips.Add(clip);
                }
                proxy.BlendShapeAvatar = clips;

                var firstPerson = root.AddComponent<VRMFirstPerson>();
                firstPerson.SetDefault();
                if (model.Head != null) firstPerson.FirstPersonBone = model.Head;
                firstPerson.FirstPersonOffset = model.ViewOffset;
                firstPerson.TraverseRenderers();
                if (model.LeftEye != null && model.RightEye != null)
                {
                    root.AddComponent<VRMLookAtHead>().Head = model.Head;
                    var eyes = root.AddComponent<VRMLookAtBoneApplyer>();
                    eyes.LeftEye = OffsetOnTransform.Create(model.LeftEye);
                    eyes.RightEye = OffsetOnTransform.Create(model.RightEye);
                }

                Springs(model);

                var export = ScriptableObject.CreateInstance<VRMExportSettings>(); made.Add(export);
                export.PoseFreeze = true;
                export.ForceTPose = false;
                export.FreezeMeshUseCurrentBlendShapeWeight = true;
                // Morph targets store only the vertices they move.
                export.UseSparseAccessor = true;
                VRMBoneNormalizer.Execute(root, false, true);
                var data = new ExportingGltfData();
                using (var exporter = new VRMExporter(data, export.GltfExportSettings, textureSerializer: VrmGltf.Textures()))
                {
                    exporter.Prepare(root);
                    exporter.Export();
                }
                return data.ToGlbBytes();
            }
            finally
            {
                foreach (var value in made) if (value != null) Object.DestroyImmediate(value);
            }
        }

        // The permissions the form gives, as VRM 0.x's single license.
        private static LicenseType License(VrmExportSettings settings)
        {
            if (!settings.redistribution) return LicenseType.Redistribution_Prohibited;
            if (settings.commercialUse) return settings.modification ? LicenseType.CC_BY : LicenseType.CC_BY_ND;
            return settings.modification ? LicenseType.CC_BY_NC : LicenseType.CC_BY_NC_ND;
        }

        private static BlendShapePreset Preset(VrmPreset preset)
        {
            switch (preset)
            {
                case VrmPreset.Aa: return BlendShapePreset.A;
                case VrmPreset.Ih: return BlendShapePreset.I;
                case VrmPreset.Ou: return BlendShapePreset.U;
                case VrmPreset.Ee: return BlendShapePreset.E;
                case VrmPreset.Oh: return BlendShapePreset.O;
                case VrmPreset.Blink: return BlendShapePreset.Blink;
                case VrmPreset.BlinkLeft: return BlendShapePreset.Blink_L;
                case VrmPreset.BlinkRight: return BlendShapePreset.Blink_R;
                case VrmPreset.Happy: return BlendShapePreset.Joy;
                case VrmPreset.Angry: return BlendShapePreset.Angry;
                case VrmPreset.Sad: return BlendShapePreset.Sorrow;
                case VrmPreset.Relaxed: return BlendShapePreset.Fun;
                default: return BlendShapePreset.Unknown;
            }
        }

        // VRM 0.x finds springs and nodes by name: a name twice (not a humanoid bone's) gets a number.
        private static void UniqueNames(GameObject root, Animator animator)
        {
            var humanoid = new HashSet<Transform>(Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>()
                .Where(b => b != HumanBodyBones.LastBone).Select(animator.GetBoneTransform).Where(t => t != null));
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in root.GetComponentsInChildren<Transform>(true).OrderByDescending(humanoid.Contains))
            {
                if (taken.Add(t.name)) continue;
                for (int i = 2; ; i++)
                    if (taken.Add(t.name + "_" + i)) { t.name = t.name + "_" + i; break; }
            }
        }

        // ---- Springs ----

        private static void Springs(VrmModel model)
        {
            var groups = new Dictionary<Transform, VRMSpringBoneColliderGroup>();
            var byCollider = new Dictionary<VrmModel.Collider, VRMSpringBoneColliderGroup>();
            foreach (var collider in model.Colliders.Where(c => !c.Plane && !c.Inside && c.Transform != null))
            {
                if (!groups.TryGetValue(collider.Transform, out var group))
                {
                    groups[collider.Transform] = group = collider.Transform.gameObject.AddComponent<VRMSpringBoneColliderGroup>();
                    group.Colliders = new VRMSpringBoneColliderGroup.SphereCollider[0];
                }
                float scale = Mathf.Abs(collider.Transform.lossyScale.x);
                var spheres = new List<VRMSpringBoneColliderGroup.SphereCollider>(group.Colliders);
                if (!collider.Capsule) spheres.Add(new VRMSpringBoneColliderGroup.SphereCollider { Offset = collider.Offset, Radius = collider.Radius });
                else
                {
                    // Spheres a radius apart along the capsule's segment.
                    float length = (collider.Tail - collider.Offset).magnitude;
                    int count = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(collider.Radius, 1e-4f)) + 1);
                    for (int i = 0; i < count; i++)
                        spheres.Add(new VRMSpringBoneColliderGroup.SphereCollider { Offset = Vector3.Lerp(collider.Offset, collider.Tail, i / (float)(count - 1)), Radius = collider.Radius });
                }
                group.Colliders = spheres.ToArray();
                byCollider[collider] = group;
            }
            if (model.Springs.Count == 0) return;
            var holder = new GameObject("secondary");
            holder.transform.SetParent(model.Root.transform, false);
            foreach (var spring in model.Springs)
            {
                var bone = holder.AddComponent<VRMSpringBone>();
                bone.m_comment = spring.Name;
                bone.m_stiffnessForce = spring.Stiffness;
                bone.m_gravityPower = spring.GravityPower;
                bone.m_gravityDir = spring.GravityDirection;
                bone.m_dragForce = spring.Drag;
                bone.m_hitRadius = spring.Radius;
                bone.RootBones = new List<Transform> { spring.Joints[0] };
                bone.ColliderGroups = spring.Colliders.Where(byCollider.ContainsKey).Select(c => byCollider[c]).Distinct().ToArray();
            }
        }

        // ---- Materials ----

        private static void Materials(VrmModel model, List<Object> made)
        {
            var shader = Shader.Find(MToon.Utils.ShaderName);
            if (shader == null) throw new InvalidOperationException("UniVRM's MToon shader isn't in the project.");
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
                        MToon.Utils.SetMToonParametersToMaterial(mtoon, Definition(read));
                    }
                    list[i] = mtoon;
                }
                renderer.sharedMaterials = list;
            }
        }

        private static MToon.MToonDefinition Definition(VrmModel.Material read)
        {
            var main = read.MainTexture as Texture2D;
            return new MToon.MToonDefinition
            {
                Meta = new MToon.MetaDefinition { Implementation = MToon.Utils.Implementation, VersionNumber = MToon.Utils.VersionNumber },
                Rendering = new MToon.RenderingDefinition
                {
                    RenderMode = read.Alpha == VrmAlpha.Transparent ? MToon.RenderMode.Transparent : read.Alpha == VrmAlpha.Cutout ? MToon.RenderMode.Cutout : MToon.RenderMode.Opaque,
                    CullMode = read.DoubleSided ? MToon.CullMode.Off : MToon.CullMode.Back,
                    RenderQueueOffsetNumber = 0,
                },
                Color = new MToon.ColorDefinition
                {
                    LitColor = read.Color, LitMultiplyTexture = main, ShadeColor = read.ShadeColor, ShadeMultiplyTexture = main, CutoutThresholdValue = read.Cutoff,
                },
                Lighting = new MToon.LightingDefinition
                {
                    LitAndShadeMixing = new MToon.LitAndShadeMixingDefinition
                    {
                        ShadingShiftValue = -.05f, ShadingToonyValue = .9f, ShadowReceiveMultiplierValue = 1f, LitAndShadeMixingMultiplierValue = 1f,
                    },
                    LightingInfluence = new MToon.LightingInfluenceDefinition { LightColorAttenuationValue = 0f, GiIntensityValue = .1f },
                    Normal = new MToon.NormalDefinition { NormalTexture = read.NormalTexture as Texture2D, NormalScaleValue = read.NormalScale },
                },
                Emission = new MToon.EmissionDefinition { EmissionColor = read.Emission, EmissionMultiplyTexture = read.EmissionTexture as Texture2D },
                MatCap = new MToon.MatCapDefinition(),
                Rim = new MToon.RimDefinition { RimColor = Color.black, RimLightingMixValue = 0f, RimFresnelPowerValue = 1f, RimLiftValue = 0f },
                Outline = new MToon.OutlineDefinition
                {
                    OutlineWidthMode = MToon.OutlineWidthMode.None, OutlineScaledMaxDistanceValue = 1f, OutlineColorMode = MToon.OutlineColorMode.FixedColor,
                    OutlineColor = Color.black, OutlineLightingMixValue = 1f,
                },
                TextureOption = new MToon.TextureUvCoordsDefinition { MainTextureLeftBottomOriginScale = read.Scale, MainTextureLeftBottomOriginOffset = read.Offset },
            };
        }
    }
}
