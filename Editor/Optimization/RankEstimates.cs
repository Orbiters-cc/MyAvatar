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
        /// <summary>
        /// Hidden objects (its object or one above hidden for good) or Disabled components (the renderer off for good) take it
        /// out of the upload, by Toolkit's own rules (<see cref="ComponentCleanup.Removes"/>), unless a build tool names it.
        /// </summary>
        public bool HiddenGone, DisabledGone;
    }

    internal sealed class TextureFact
    {
        public string Name;
        /// <summary>How it is imported and used for a PC and for a mobile upload (what decides its copy).</summary>
        public TextureOptimization.TextureState Pc, Mobile;
        /// <summary>
        /// Only renderers Hidden objects takes out show it (<see cref="HiddenGone"/>), or only renderers Hidden objects and
        /// Disabled components together take out (<see cref="DisabledGone"/>): it leaves with them.
        /// </summary>
        public bool HiddenGone, DisabledGone;
        /// <summary>
        /// Only material properties its shader doesn't declare hold it (Unused textures drops it), or it is the same image
        /// imported the same way as another one the materials show (Duplicate textures keeps that one).
        /// </summary>
        public bool Unused, Duplicate;
        public TextureOptimization.TextureState State(bool mobile) => mobile ? Mobile : Pc;
    }

    /// <summary>
    /// What settings would save at upload, estimated from the facts of the avatar as it is (<see cref="RankFacts"/>) by the
    /// rules the changes follow: each texture as the upload's own rule makes it (<see cref="TextureOptimization.Copy"/>,
    /// from its density and import read once), gone with Unused and Duplicate textures; polygons from each mesh's triangles
    /// and a share of them for the budget (scaled to what the current estimate measured, when it simplified any); merges
    /// from Mesh Merge's rules (meshes always shown join one, meshes one toggle each shows and hides join it when that gives
    /// a better rating); what Hidden objects and Disabled components take out, by Toolkit's rules
    /// (<see cref="ComponentCleanup.Removes"/>). Pure arithmetic: the plan shows it as "about", until the settings are measured.
    /// Calibrated on the avatars of Enzo's projects: RankCalibrationTests.
    /// </summary>
    internal static class RankEstimates
    {
        // Share of a mesh's triangles the simplifier removes for each budget (mm), on a mesh without blendshapes, times
        // e^(-blendshapes / ShapesKeep): what any blendshape moves is kept as it is, and a body with hundreds of shapes keeps
        // everything. Fitted on 42 meshes of 2,000 triangles or more of 14 avatars of Enzo's projects (Tests/Editor/Fixtures/
        // rank-calibration.json, RankCalibrationTests): at 8 mm by least squares on each avatar's total (a fifth of a mesh
        // without shapes); under it nothing does better than next to nothing (a few meshes lose a part, most none), so the
        // shares there are those that err least on each avatar. The simplifier goes by geometry these facts don't hold (how
        // dense a mesh is, how much of it the shapes move): a mesh loses a part or nothing, so one avatar's figure stays rough.
        private static readonly (float Millimetres, float Share)[] Shares = { (.5f, 0f), (1f, .0003f), (2f, .001f), (4f, .004f), (8f, .19f) };
        private const float ShapesKeep = 40f;
        private const int MinTriangles = 2000;

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
            var all = root.GetComponentsInChildren<Renderer>(true).Where(r => !TextureOptimization.EditorHelper(r)).ToList();
            var renderers = all.Where(r => r is SkinnedMeshRenderer || r is MeshRenderer).ToList();
            // What Hidden objects and Disabled components take out, by their own rules (read only when something is hidden).
            AvatarUsage usage = null;
            bool usageFailed = false;
            (bool Hidden, bool Disabled) Gone(Renderer renderer)
            {
                if (renderer.enabled && renderer.gameObject.activeInHierarchy || usageFailed) return (false, false);
                try { usage ??= new AvatarUsage(root); }
                catch (Exception ex) { usageFailed = true; Debug.LogWarning("[My Avatar] Reach a rank: hidden objects are not estimated (" + ex.Message + ")."); return (false, false); }
                // Not what an animation toggles or a build tool names (it may show it).
                if (Toggles(renderer) || Named(renderer.transform, root.transform, referenced)) return (false, false);
                return (ComponentCleanup.Removes(usage, renderer, true, false), ComponentCleanup.Removes(usage, renderer, false, true));
            }
            bool Toggles(Renderer renderer)
            {
                for (var t = renderer.transform; t != null && t != root.transform; t = t.parent) if (toggled.Contains(t.name)) return true;
                return false;
            }
            var gone = new Dictionary<Renderer, (bool Hidden, bool Disabled)>();
            foreach (var renderer in all) gone[renderer] = Gone(renderer);
            foreach (var renderer in renderers)
            {
                var mesh = MeshOptimization.MeshOf(renderer);
                if (mesh == null) continue;
                int triangles = RankContributors.Triangles(mesh);
                bool toggles = Toggles(renderer);
                facts.Renderers.Add(new RendererFact
                {
                    Path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform), Skinned = renderer is SkinnedMeshRenderer,
                    Shown = renderer.enabled && renderer.gameObject.activeInHierarchy, Toggled = toggles, Animated = animated.Contains(renderer.name),
                    Constrained = renderer.GetComponentInParent<IConstraint>(true) != null || renderer.GetComponentInParent<VRCConstraintBase>(true) != null,
                    Cloth = renderer.GetComponent<Cloth>() != null, Triangles = triangles, BlendShapes = mesh.blendShapeCount, Materials = renderer.sharedMaterials.Length,
                    Referenced = Named(renderer.transform, root.transform, referenced),
                });
                var fact = facts.Renderers[facts.Renderers.Count - 1];
                (fact.HiddenGone, fact.DisabledGone) = gone[renderer];
            }
            // Every texture the materials show, as the rank counts texture memory; each as the upload's texture rules see it
            // (TextureOptimization.States: its density measured, as Small's estimate would, and kept for it).
            List<TextureSlot> slots = null;
            Dictionary<Texture, TextureOptimization.TextureState> pc = null, mobile = null;
            if (avatar)
            {
                slots = TextureMatching.Slots(avatar);
                pc = TextureOptimization.States(avatar, false, true, true, slots);
                mobile = TextureOptimization.States(avatar, true, true, true, slots);
            }
            TextureOptimization.TextureState Fixed(Texture texture, bool onMobile) =>
                new TextureOptimization.TextureState { Before = TextureOptimization.BuildBytes(texture, onMobile), Width = texture.width, Height = texture.height };
            // What Unused textures and Duplicate textures take out, by their own rules.
            var declared = new HashSet<Texture>();
            foreach (var material in all.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct())
            {
                var unused = new HashSet<string>(MaterialCleanup.UnusedTextures(material), StringComparer.Ordinal);
                foreach (string property in material.GetTexturePropertyNames())
                    if (!unused.Contains(property) && material.GetTexture(property) is Texture held && held != null) declared.Add(held);
            }
            Dictionary<Texture, Texture> duplicates;
            try { duplicates = MaterialCleanup.DuplicateTextures(root); }
            catch (Exception ex) { duplicates = new Dictionary<Texture, Texture>(); Debug.LogWarning("[My Avatar] Reach a rank: duplicate textures are not estimated (" + ex.Message + ")."); }
            var showing = new Dictionary<Texture, List<Renderer>>();
            foreach (var renderer in all)
                foreach (var texture in Shown(renderer))
                {
                    if (!showing.TryGetValue(texture, out var list)) showing[texture] = list = new List<Renderer>();
                    list.Add(renderer);
                }
            foreach (var pair in showing)
            {
                var texture = pair.Key;
                facts.Textures.Add(new TextureFact
                {
                    Name = texture.name,
                    Pc = pc != null && pc.TryGetValue(texture, out var p) ? p : Fixed(texture, false),
                    Mobile = mobile != null && mobile.TryGetValue(texture, out var q) ? q : Fixed(texture, true),
                    HiddenGone = pair.Value.All(r => gone[r].Hidden),
                    DisabledGone = pair.Value.All(r => gone[r].Hidden || gone[r].Disabled),
                    Unused = !declared.Contains(texture), Duplicate = duplicates.ContainsKey(texture),
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

            // What Hidden objects and Disabled components take out leaves the upload.
            bool Gone(RendererFact r, OptimizationPasses passes) =>
                (passes & OptimizationPasses.HiddenObjects) != 0 && r.HiddenGone || (passes & OptimizationPasses.DisabledComponents) != 0 && r.DisabledGone;
            var hidden = facts.Renderers.Where(r => Gone(r, then) && !Gone(r, now)).ToList();
            if (hidden.Count > 0)
            {
                Save(Category.PolyCount, hidden.Sum(r => r.Triangles));
                Save(Category.SkinnedMeshCount, hidden.Count(r => r.Skinned));
                Save(Category.MeshCount, hidden.Count(r => !r.Skinned));
                Save(Category.MaterialCount, hidden.Sum(r => r.Materials));
            }
            var kept = facts.Renderers.Where(r => !Gone(r, then)).ToList();

            // Polygons: each mesh's share for the budget, scaled to what the current estimate measured when it simplified.
            float Predicted(UploadOptimization settings) => (settings.Passes & OptimizationPasses.FewerPolygons) == 0 ? 0f
                : kept.Sum(r => Removed(r, settings.PolygonQuality));
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

            // Texture memory: each texture as the upload's rules make it under each setting (TextureOptimization.Copy),
            // nothing for one only what leaves the upload shows.
            double change = 0;
            foreach (var texture in facts.Textures) change += Bytes(texture, mobile, current) - Bytes(texture, mobile, candidate);
            Save(Category.TextureMegabytes, (float)(change / 1048576d));
            return saves;
        }

        /// <summary>A texture's memory in the upload under <paramref name="settings"/>, for PC or mobile.</summary>
        internal static long Bytes(TextureFact texture, bool mobile, UploadOptimization settings)
        {
            var passes = settings.Passes;
            if ((passes & OptimizationPasses.HiddenObjects) != 0 && texture.HiddenGone ||
                (passes & (OptimizationPasses.HiddenObjects | OptimizationPasses.DisabledComponents)) == (OptimizationPasses.HiddenObjects | OptimizationPasses.DisabledComponents) && texture.DisabledGone)
                return 0;
            if ((passes & OptimizationPasses.UnusedTextures) != 0 && texture.Unused || (passes & OptimizationPasses.DuplicateTextures) != 0 && texture.Duplicate) return 0;
            var state = texture.State(mobile);
            return state == null ? 0 : TextureOptimization.Copy(state, passes, TextureDensity.Clamp(settings.SharpFrom), mobile).After;
        }

        /// <summary>The triangles the simplifier is estimated to remove from the mesh at this polygon quality.</summary>
        internal static float Removed(RendererFact renderer, float quality) =>
            renderer.Triangles < MinTriangles ? 0f : renderer.Triangles * Share(quality) * Mathf.Exp(-renderer.BlendShapes / ShapesKeep);

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
