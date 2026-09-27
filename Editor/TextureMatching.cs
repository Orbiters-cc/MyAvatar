using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed class TextureSlot
    {
        public string id, materialName, shader, property, description, role, existingName, rendererPath;
        public string existingPath;
        public int existingWidth, existingHeight;
        public Material material;
        public string Label => materialName + " / " + description + " (" + property + ") · " + rendererPath;
    }

    internal static class TextureMatching
    {
        internal static List<TextureSlot> Slots(MyAvatar avatar)
        {
            var result = new List<TextureSlot>();
            var seen = new HashSet<Material>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if ((renderer.hideFlags & HideFlags.DontSave) != 0 || (renderer.gameObject.hideFlags & HideFlags.DontSave) != 0) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!material || !material.shader || !seen.Add(material) || material.shader.name.StartsWith("Hidden/", StringComparison.Ordinal)) continue;
                    var shader = material.shader;
                    for (int i = 0; i < shader.GetPropertyCount(); i++)
                    {
                        if (shader.GetPropertyType(i) != ShaderPropertyType.Texture || shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D ||
                            (shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0) continue;
                        string property = shader.GetPropertyName(i);
                        var texture = material.GetTexture(property);
                        result.Add(new TextureSlot { id = "s" + result.Count, material = material, materialName = material.name,
                            shader = shader.name, property = property, description = shader.GetPropertyDescription(i),
                            role = Role(property + " " + shader.GetPropertyDescription(i)), existingName = texture ? texture.name : "",
                            existingPath = texture ? AssetDatabase.GetAssetPath(texture) : "", existingWidth = texture ? texture.width : 0, existingHeight = texture ? texture.height : 0,
                            rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform) });
                    }
                }
            }
            return result;
        }

        internal static string Role(string text)
        {
            string s = Regex.Replace(text ?? "", "[^a-zA-Z0-9]", "").ToLowerInvariant();
            if (s.Contains("normal") || s.Contains("bumpmap")) return "normal";
            if (s.Contains("emission") || s.Contains("emissive")) return "emission";
            if (s.Contains("metallic")) return "metallic";
            if (s.Contains("occlusion") || s == "ao") return "occlusion";
            if (s.Contains("roughness")) return "roughness";
            if (s.Contains("smoothness")) return "smoothness";
            if (s.Contains("height") || s.Contains("parallax")) return "height";
            if (s.Contains("specular") || s.Contains("specgloss")) return "specular";
            if (s.Contains("mask")) return "mask";
            if (s.Contains("basecolor") || s.Contains("basemap") || s.Contains("maintex") || s.Contains("albedo") || s.Contains("diffuse")) return "color";
            return "unknown";
        }

        private static HashSet<string> Words(string value)
        {
            value = Regex.Replace(value ?? "", "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
            return new HashSet<string>(Regex.Split(value, "[^a-z]+").Select(w =>
                w.StartsWith("eye") ? "eye" : w.StartsWith("feather") || w == "hair" ? "hair" : w)
                .Where(w => w.Length > 2 && !new[] { "mat", "matt", "material", "myavatar", "map", "tex", "base", "color", "normal", "emission", "metallic", "png", "jpg", "dev", "ulti", "rex", "setup" }.Contains(w)));
        }

        internal const string RememberedReason = "Remembered from your previous apply to this avatar.";

        internal static void Match(List<TextureEntry> textures, List<TextureSlot> slots, Dictionary<Texture2D, TextureStats> stats, Dictionary<string, TextureMemory.Slot> memory)
        {
            var context = slots.GroupBy(s => s.material).ToDictionary(g => g.Key,
                g => Words(string.Join(" ", g.Select(s => s.existingName))));
            var materialWords = context.Keys.ToDictionary(m => m, m => Words(m.name));
            var existingWords = slots.ToDictionary(s => s, s => Words(s.existingName));
            foreach (var texture in textures)
            {
                var remembered = TextureMemory.Find(memory, texture, slots);
                if (remembered != null) { Assign(texture, remembered, 1f, RememberedReason); continue; }
                var words = Words(texture.fileName);
                var ranked = slots.Where(s => s.role == texture.role && texture.role != "unknown")
                    .Select(s => new { slot = s, evidence = words.Intersect(materialWords[s.material]).Count() * 4 +
                        words.Intersect(existingWords[s]).Count() * 3 + words.Intersect(context[s.material]).Count() * 2 })
                    .Where(s => s.evidence > 0)
                    .Select(s => new { s.slot, score = s.evidence + SlotPreference(texture, s.slot) })
                    .Where(s => s.score > 0).OrderByDescending(s => s.score).ToList();
                if (ranked.Count == 0) { texture.reason = "No clear material and texture-slot match. Choose a slot below."; continue; }
                if (ranked.Count > 1 && ranked[0].score == ranked[1].score)
                { texture.reason = "Several materials are equally likely. Choose the target below."; continue; }
                var best = ranked[0].slot;
                Assign(texture, best, .95f, "Matched " + best.materialName + " / " + best.description + " using the material’s existing texture set.");
            }
            SeparateEmission(textures, slots, stats);
            RejectConflicts(textures);
        }

        private static void Assign(TextureEntry texture, TextureSlot slot, float confidence, string reason)
        {
            texture.material = slot.material; texture.property = slot.property; texture.confidence = confidence;
            texture.suggestedMaterialName = slot.materialName; texture.suggestedProperty = slot.property; texture.reason = reason;
        }

        // Two colour images aimed at one colour slot, where exactly one is mostly black with bright details and the
        // material has a free emission slot: the dark one is the emission map (e.g. Eyes_BaseColor vs Eyes_BaseColor3).
        private static void SeparateEmission(List<TextureEntry> textures, List<TextureSlot> slots, Dictionary<Texture2D, TextureStats> stats)
        {
            foreach (var group in textures.Where(t => t.material && !t.applied && t.role == "color").GroupBy(t => (t.material, t.property)).Where(g => g.Count() == 2).ToList())
            {
                var emissive = group.Where(t => stats.TryGetValue(t.texture, out var s) && s.LooksEmissive).ToList();
                if (emissive.Count != 1) continue;
                var other = group.First(t => t != emissive[0]);
                if (!stats.TryGetValue(other.texture, out var otherStats) || otherStats.black > .3f) continue;
                var target = slots.Where(s => s.material == group.Key.material && s.role == "emission" && !textures.Any(t => t.material == s.material && t.property == s.property))
                    .OrderByDescending(s => SlotPreference(emissive[0], s)).FirstOrDefault();
                if (target == null) continue;
                Assign(emissive[0], target, .92f, "Mostly black with bright details: matched as " + target.materialName + " / " + target.description + ".");
            }
        }

        private static int SlotPreference(TextureEntry entry, TextureSlot slot)
        {
            bool detailTexture = entry.fileName.IndexOf("detail", StringComparison.OrdinalIgnoreCase) >= 0;
            bool detailSlot = (slot.property + " " + slot.description).IndexOf("detail", StringComparison.OrdinalIgnoreCase) >= 0;
            if (detailTexture != detailSlot) return -8;
            // A plain normal/albedo/emission map belongs in the primary slot, rather than an empty secondary layer.
            if (slot.property == "_BumpMap" || slot.property == "_MainTex" || slot.property == "_BaseMap" || slot.property == "_EmissionMap") return 2;
            return 0;
        }

        internal static void RejectConflicts(List<TextureEntry> textures)
        {
            foreach (var conflict in textures.Where(t => t.material && !t.applied).GroupBy(t => (t.material, t.property)).Where(g => g.Count() > 1))
                foreach (var entry in conflict) { entry.material = null; entry.property = null; entry.confidence = 0;
                    entry.reason = "Filename matching points multiple images at " + entry.suggestedMaterialName + " / " + entry.suggestedProperty + ". Image analysis may distinguish their roles."; }
        }
    }
}
