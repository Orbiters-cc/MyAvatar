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

        internal static void Match(List<TextureEntry> textures, List<TextureSlot> slots)
        {
            foreach (var texture in textures)
            {
                var words = Words(texture.fileName);
                var ranked = slots.Where(s => s.role == texture.role && texture.role != "unknown")
                    .Select(s => new { slot = s, score = words.Intersect(Words(s.materialName)).Count() * 4 + words.Intersect(Words(s.existingName)).Count() * 2 })
                    .Where(s => s.score > 0).OrderByDescending(s => s.score).ToList();
                if (ranked.Count == 0) { texture.reason = "No clear material and texture-slot match. Choose a slot below."; continue; }
                if (ranked.Count > 1 && ranked[0].score == ranked[1].score)
                { texture.reason = "Several materials are equally likely. Choose the target below."; continue; }
                var best = ranked[0].slot;
                texture.material = best.material; texture.property = best.property; texture.confidence = .95f;
                texture.reason = "Matched the material name and " + texture.role + " slot.";
            }
            RejectConflicts(textures);
        }

        internal static void RejectConflicts(List<TextureEntry> textures)
        {
            foreach (var conflict in textures.Where(t => t.material && !t.applied).GroupBy(t => (t.material, t.property)).Where(g => g.Count() > 1))
                foreach (var entry in conflict) { entry.material = null; entry.property = null; entry.confidence = 0;
                    entry.reason = "Alternative textures compete for the same slot. Choose which one to use."; }
        }
    }
}
