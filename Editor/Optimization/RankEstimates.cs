using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRC.Dynamics;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Category = VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// What the avatar is made of, read as its estimate measures it (no other pass): each renderer's triangles, whether it
    /// shows at rest, whether animations switch it or change it otherwise, and each texture's size and memory. A rank plan
    /// estimates from it what settings not measured yet would save.
    /// </summary>
    internal sealed class RankFacts
    {
        public List<RendererFact> Renderers = new List<RendererFact>();
        public List<TextureFact> Textures = new List<TextureFact>();
    }

    internal sealed class RendererFact
    {
        public string Path;
        public bool Skinned, Shown, Toggled, Animated, Constrained, Cloth;
        /// <summary>
        /// Something points at it or animates its blendshapes (the descriptor's visemes and eyelids, a VRCFury or Modular
        /// Avatar component, a blendshape curve): it stays where it is, and a build tool may show it.
        /// </summary>
        public bool Referenced;
        public int Triangles, BlendShapes, Materials;
    }

    internal sealed class TextureFact
    {
        public string Name;
        /// <summary>The size the build stores it at now (its import settings), and its memory then on PC and on mobile.</summary>
        public int Width, Height;
        public long PcBytes, MobileBytes;
        /// <summary>Meters of the avatar's surface its width covers, when an earlier estimate measured it; else -1.</summary>
        public float Meters = -1f;
        public bool Body, Normal, Linear, Surface;
    }

    /// <summary>
    /// What settings would save at upload, estimated from the facts of the avatar as it is (<see cref="RankFacts"/>) by the
    /// rules the changes follow: textures sized for the distance (<see cref="TextureDensity.Cap"/>), halved surface maps,
    /// Quest's half-size ASTC; polygons from each mesh's triangles and a share of them for the budget (scaled to what the
    /// current estimate measured, when it simplified any); merges from Mesh Merge's rules (meshes always shown join one,
    /// meshes one toggle each shows and hides join it when that gives a better rating); objects hidden at rest that nothing
    /// shows. Pure arithmetic: the plan shows it as "about", until the settings are measured.
    /// </summary>
    internal static class RankEstimates
    {
        // Share of a mesh's triangles the simplifier removes for each budget (mm), on a mesh without blendshapes. What any
        // blendshape moving less than a quarter of the mesh moves is kept as it is: a mesh keeps more the more shapes it has
        // (ShapesLock shapes halve the share). MasculineCanine at 8 mm (a body of 99,642 triangles with 497 shapes, hair of
        // 3,832 and 4,658 with 7 each, a mane a VRCFury toggle shows): 192 removed, where this says about 680.
        private static readonly (float Millimetres, float Share)[] Shares = { (.5f, .015f), (1f, .03f), (2f, .05f), (4f, .08f), (8f, .12f) };
        private const float ShapesLock = 4f;
        private const int MinTriangles = 2000;
        // ASTC's bits per pixel: 6x6 for colour and normal maps, 8x8 for other linear data (TextureOptimization's Quest rule).
        private const float Astc6 = 128f / 36f, Astc8 = 2f;

        // ---- Reading ----

        /// <summary>
        /// The facts of <paramref name="root"/> as it stands. <paramref name="bindings"/> are the animations' bindings the
        /// download estimate reads anyway (curve and object reference ones).
        /// </summary>
        internal static RankFacts Read(GameObject root, MyAvatar avatar, IEnumerable<EditorCurveBinding> bindings)
        {
            var facts = new RankFacts();
            if (root == null) return facts;
            // Objects by name, as animations find them from wherever a tool merges them.
            var toggled = new HashSet<string>(StringComparer.Ordinal);
            var animated = new HashSet<string>(StringComparer.Ordinal);
            var referenced = References(root);
            foreach (var binding in bindings ?? Enumerable.Empty<EditorCurveBinding>())
            {
                string name = binding.path.Substring(binding.path.LastIndexOf('/') + 1);
                if (binding.propertyName == "m_IsActive" && binding.type == typeof(GameObject)) toggled.Add(name);
                else if (typeof(Renderer).IsAssignableFrom(binding.type))
                {
                    if (binding.propertyName == "m_Enabled") toggled.Add(name);
                    else if (binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) referenced.Add(name);
                    else animated.Add(name);
                }
            }
            var renderers = root.GetComponentsInChildren<Renderer>(true).Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToList();
            Renderer body = null;
            int most = -1;
            foreach (var renderer in renderers)
            {
                var mesh = MeshOptimization.MeshOf(renderer);
                if (mesh == null) continue;
                int triangles = RankContributors.Triangles(mesh);
                if (triangles > most) { most = triangles; body = renderer; }
                bool toggles = false;
                for (var t = renderer.transform; t != null && t != root.transform; t = t.parent) if (toggled.Contains(t.name)) { toggles = true; break; }
                facts.Renderers.Add(new RendererFact
                {
                    Path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform), Skinned = renderer is SkinnedMeshRenderer,
                    Shown = renderer.enabled && renderer.gameObject.activeInHierarchy, Toggled = toggles, Animated = animated.Contains(renderer.name),
                    Constrained = renderer.GetComponentInParent<IConstraint>(true) != null || renderer.GetComponentInParent<VRCConstraintBase>(true) != null,
                    Cloth = renderer.GetComponent<Cloth>() != null, Triangles = triangles, BlendShapes = mesh.blendShapeCount, Materials = renderer.sharedMaterials.Length,
                    Referenced = Named(renderer.transform, root.transform, referenced),
                });
            }
            var bodyTextures = new HashSet<Texture>(body != null ? Shown(body) : Enumerable.Empty<Texture>());
            var meters = avatar ? TextureDensity.Known(avatar) : null;
            foreach (var texture in renderers.SelectMany(Shown).Distinct())
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
                var size = importer != null ? TextureOptimization.BuildSize(texture, importer, false) : new Vector2Int(texture.width, texture.height);
                facts.Textures.Add(new TextureFact
                {
                    Name = texture.name, Width = size.x, Height = size.y,
                    PcBytes = TextureOptimization.BuildBytes(texture, false), MobileBytes = TextureOptimization.BuildBytes(texture, true),
                    Meters = meters != null && meters.TryGetValue(texture, out float m) ? m : -1f, Body = bodyTextures.Contains(texture),
                    Normal = importer != null && importer.textureType == TextureImporterType.NormalMap, Linear = importer != null && !importer.sRGBTexture,
                    Surface = importer != null && !importer.sRGBTexture && TextureDensity.SurfaceMap(texture.name),
                });
            }
            return facts;
        }

        // The names of what the descriptor (visemes, eyelids) and the build tools' components (VRCFury, Modular Avatar: their
        // toggles, links and merged animators, before they build) point at or name.
        private static HashSet<string> References(GameObject root)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor != null)
            {
                if (descriptor.VisemeSkinnedMesh != null) names.Add(descriptor.VisemeSkinnedMesh.name);
                if (descriptor.customEyeLookSettings.eyelidsSkinnedMesh != null) names.Add(descriptor.customEyeLookSettings.eyelidsSkinnedMesh.name);
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !Toolkit.Editor.VRChat.VrcFury.IsComponent(component) && !Toolkit.Editor.VRChat.ModularAvatar.IsComponent(component)) continue;
                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            if (property.objectReferenceValue is Component target && target != null) names.Add(target.name);
                            else if (property.objectReferenceValue is GameObject go && go != null) names.Add(go.name);
                        }
                        else if (property.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(property.stringValue))
                            names.Add(property.stringValue.Substring(property.stringValue.LastIndexOf('/') + 1));
                    }
                }
            }
            return names;
        }

        // Whether the object or one above it (below the root) is named in the set.
        private static bool Named(Transform t, Transform root, HashSet<string> names)
        {
            for (; t != null && t != root; t = t.parent) if (names.Contains(t.name)) return true;
            return false;
        }

        private static IEnumerable<Texture> Shown(Renderer renderer)
        {
            foreach (var material in renderer.sharedMaterials)
                if (material != null)
                    foreach (int id in material.GetTexturePropertyNameIDs())
                        if (material.GetTexture(id) is Texture texture && texture != null) yield return texture;
        }

        // ---- Estimating ----

        /// <summary>
        /// What <paramref name="candidate"/> would save at upload against the estimate of <paramref name="current"/>
        /// (<paramref name="upload"/>, which removed <paramref name="removedPolygons"/> polygons), on PC or mobile. Only the
        /// categories it can estimate; null without the facts.
        /// </summary>
        internal static Dictionary<Category, float> Saves(RankFacts facts, OptimizationEstimate.Snapshot upload, int removedPolygons,
            UploadOptimization current, UploadOptimization candidate, bool mobile)
        {
            if (facts == null || upload?.Rank(mobile) == null) return null;
            var stats = upload.Rank(mobile);
            var saves = new Dictionary<Category, float>();
            void Save(Category category, float amount)
            {
                if (amount > .5f || category == Category.TextureMegabytes && amount > .05f) saves[category] = (saves.TryGetValue(category, out float s) ? s : 0f) + amount;
            }
            var now = current.Passes; var then = candidate.Passes;
            bool Adds(OptimizationPasses pass) => (then & pass) != 0 && (now & pass) == 0;

            // Objects hidden at rest that nothing shows leave the upload.
            var hidden = facts.Renderers.Where(r => !r.Shown && !r.Toggled && !r.Referenced).ToList();
            if (Adds(OptimizationPasses.HiddenObjects))
            {
                Save(Category.PolyCount, hidden.Sum(r => r.Triangles));
                Save(Category.SkinnedMeshCount, hidden.Count(r => r.Skinned));
                Save(Category.MeshCount, hidden.Count(r => !r.Skinned));
                Save(Category.MaterialCount, hidden.Sum(r => r.Materials));
            }
            var kept = (then & OptimizationPasses.HiddenObjects) != 0 ? facts.Renderers.Except(hidden).ToList() : facts.Renderers;

            // Polygons: each mesh's share for the budget, scaled to what the current estimate measured when it simplified.
            float Predicted(UploadOptimization settings) => (settings.Passes & OptimizationPasses.FewerPolygons) == 0 ? 0f
                : kept.Where(r => r.Triangles >= MinTriangles).Sum(r => r.Triangles * Share(settings.PolygonQuality) / (1f + r.BlendShapes / ShapesLock));
            float predictedNow = Predicted(current), predictedThen = Predicted(candidate);
            if ((now & OptimizationPasses.FewerPolygons) != 0 && predictedNow > 0f) predictedThen *= removedPolygons / predictedNow;
            Save(Category.PolyCount, predictedThen - ((now & OptimizationPasses.FewerPolygons) != 0 ? removedPolygons : 0));

            // Merges: meshes always shown become one skinned mesh; with toggled ones merged too, each mesh one toggle shows
            // and hides joins it when that gives skinned meshes or meshes a better rating (MeshMerge.ToggledPolicy).
            // What something points at or animates the shapes of stays where it is (MeshMerge's Movable): the others join it.
            var always = kept.Where(r => r.Shown && !r.Toggled && !r.Animated && !r.Cloth).ToList();
            var movable = always.Where(r => !r.Referenced).ToList();
            bool target = always.Any(r => r.Referenced && r.Skinned);
            int skinnedNow = (int)stats.Value(Category.SkinnedMeshCount), meshesNow = (int)stats.Value(Category.MeshCount);
            int skinnedSaved = 0, meshesSaved = 0;
            if (Adds(OptimizationPasses.MergeMeshes) && (target ? movable.Count > 0 : movable.Count > 1))
            {
                skinnedSaved = movable.Count(r => r.Skinned) - (target ? 0 : 1);
                meshesSaved = movable.Count(r => !r.Skinned);
                if (skinnedSaved < 0) { meshesSaved += skinnedSaved; skinnedSaved = 0; }
            }
            if (Adds(OptimizationPasses.MergeToggledMeshes) && always.Count(r => r.Skinned) > 0)
            {
                var toggled = kept.Where(r => r.Toggled && !r.Animated && !r.Constrained && !r.Cloth && !r.Referenced).ToList();
                int skinned = toggled.Count(r => r.Skinned), meshes = toggled.Count(r => !r.Skinned);
                if (skinned + meshes > 0 &&
                    (Better(l => l.skinnedMeshCount, skinnedNow - skinnedSaved, skinnedNow - skinnedSaved - skinned, mobile) ||
                     Better(l => l.meshCount, meshesNow - meshesSaved, meshesNow - meshesSaved - meshes, mobile)))
                {
                    skinnedSaved += skinned;
                    meshesSaved += meshes;
                }
            }
            Save(Category.SkinnedMeshCount, Math.Min(skinnedSaved, skinnedNow - 1));
            Save(Category.MeshCount, Math.Min(meshesSaved, meshesNow));

            // Texture memory: each texture at the size the candidate's rules give it, never larger than it is now.
            bool resizes = (then & OptimizationPasses.ResizeTextures) != 0, half = (then & OptimizationPasses.HalfSurfaceMaps) != 0;
            bool quest = mobile && (then & OptimizationPasses.QuestTextures) != 0;
            if (resizes || half || quest)
            {
                double total = 0;
                foreach (var texture in facts.Textures) total += Bytes(texture, mobile, resizes, half, quest, candidate.SharpFrom);
                Save(Category.TextureMegabytes, stats.Value(Category.TextureMegabytes) - (float)(total / 1048576d));
            }
            return saves;
        }

        /// <summary>A texture's memory under a candidate's texture rules (TextureOptimization.Build's, simplified).</summary>
        internal static long Bytes(TextureFact texture, bool mobile, bool resizes, bool half, bool quest, float sharpFrom)
        {
            long bytes = mobile ? texture.MobileBytes : texture.PcBytes;
            if (texture.Width <= 0 || texture.Height <= 0) return bytes;
            var size = new Vector2Int(texture.Width, texture.Height);
            if (resizes) size = TextureOptimization.Capped(size.x, size.y, TextureDensity.Cap(texture.Meters > 0f ? texture.Meters : (float?)null, texture.Body, false, sharpFrom));
            if (half && texture.Surface) size = TextureOptimization.Half(size, 64);
            double area = (double)size.x * size.y, before = (double)texture.Width * texture.Height;
            long after;
            if (quest)
            {
                size = TextureOptimization.Capped(Mathf.Max(64, size.x / 2), Mathf.Max(64, size.y / 2), 1024);
                area = (double)size.x * size.y;
                after = (long)(area * 4d / 3d * (texture.Linear && !texture.Normal ? Astc8 : Astc6) / 8d);
            }
            else after = (long)(bytes * area / before);
            return Math.Min(bytes, after);
        }

        // The share of a mesh's triangles the budget of this polygon quality removes (log-linear between the stops).
        internal static float Share(float quality)
        {
            float millimetres = PolygonReduction.Budget(quality) * 1000f;
            if (millimetres <= Shares[0].Millimetres) return Shares[0].Share;
            for (int i = 1; i < Shares.Length; i++)
                if (millimetres <= Shares[i].Millimetres)
                {
                    float t = Mathf.InverseLerp(Mathf.Log(Shares[i - 1].Millimetres, 2f), Mathf.Log(Shares[i].Millimetres, 2f), Mathf.Log(millimetres, 2f));
                    return Mathf.Lerp(Shares[i - 1].Share, Shares[i].Share, t);
                }
            return Shares[Shares.Length - 1].Share;
        }

        // Whether the new count rates better than the old one (the SDK's limits for the platform).
        private static bool Better(Func<AvatarPerformanceStatsLevel, int> stat, int before, int after, bool mobile) =>
            after < before && AvatarBudget.Rate(stat, Math.Max(0, after), mobile) < AvatarBudget.Rate(stat, Math.Max(0, before), mobile);
    }
}
