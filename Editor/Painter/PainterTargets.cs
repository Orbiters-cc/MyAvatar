using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// Where each map painted in Substance 3D Painter goes on one material, read from its shader's texture slots (the slots
    /// texture drops use, see <see cref="TextureMatching.Slots"/>): base colour, normal, emission and occlusion slots, and
    /// the metallic and smoothness (or roughness) channels, packed in one texture (Standard, Poiyomi) or apart (lilToon, VRChat
    /// Toon Standard).
    /// </summary>
    internal sealed class PainterTargets
    {
        /// <summary>A channel of a texture slot.</summary>
        internal sealed class Channel
        {
            public string Property;
            /// <summary>0 to 3 (R, G, B, A).</summary>
            public int Index;
            /// <summary>Roughness rather than smoothness (or an inverted metallic map).</summary>
            public bool Inverted;
        }

        public string Color, Normal, Emission, Occlusion;
        public Channel Metallic, Smoothness;

        /// <summary>The material's slots for <paramref name="material"/> (null when it isn't on the avatar's renderers).</summary>
        internal static PainterTargets For(Material material, IEnumerable<TextureSlot> slots)
        {
            var own = slots.Where(s => s.material == material).ToList();
            if (own.Count == 0) return null;
            string Pick(string role, bool secondaryToo = false)
            {
                var found = own.Where(s => s.role == role && (secondaryToo || !s.secondary)).OrderByDescending(TextureMatching.Commonness).FirstOrDefault();
                return found?.property;
            }
            var targets = new PainterTargets
            {
                Color = Pick("color"),
                Normal = Pick("normal"),
                Emission = Pick("emission"),
                Occlusion = Pick("occlusion", secondaryToo: true),
            };
            var family = MaterialShaders.FamilyOf(material.shader);
            bool Has(string property) => own.Any(s => s.property == property);
            float Value(string property, float fallback) => material.HasProperty(property) ? material.GetFloat(property) : fallback;
            int Index(string property, int fallback) => Mathf.Clamp(Mathf.RoundToInt(Value(property, fallback)), 0, 4);
            if (Has("_MochieMetallicMaps"))
            {
                // Poiyomi's packed maps: each value's channel is a setting (4: none, white).
                int metal = Index("_MochieMetallicMapsMetallicChannel", 0), smooth = Index("_MochieMetallicMapsRoughnessChannel", 1);
                if (metal < 4) targets.Metallic = new Channel { Property = "_MochieMetallicMaps", Index = metal, Inverted = Value("_MochieMetallicMapInvert", 0) > .5f };
                if (smooth < 4) targets.Smoothness = new Channel { Property = "_MochieMetallicMaps", Index = smooth, Inverted = Value("_MochieRoughnessMapInvert", 0) > .5f };
            }
            else if (family == ShaderFamily.LilToon)
            {
                if (Has("_MetallicGlossMap")) targets.Metallic = new Channel { Property = "_MetallicGlossMap" };
                if (Has("_SmoothnessTex")) targets.Smoothness = new Channel { Property = "_SmoothnessTex" };
            }
            else if (Has("_MetallicGlossMap"))
            {
                // Standard's: metallic in R, smoothness in A.
                targets.Metallic = new Channel { Property = "_MetallicGlossMap" };
                targets.Smoothness = new Channel { Property = "_MetallicGlossMap", Index = 3 };
            }
            else
            {
                var metallic = own.FirstOrDefault(s => s.role == "metallic" && !s.secondary);
                if (metallic != null) targets.Metallic = new Channel { Property = metallic.property, Index = Index(metallic.property + "Channel", 0) % 4 };
                var surface = own.FirstOrDefault(s => (s.role == "smoothness" || s.role == "roughness") && !s.secondary);
                if (surface != null)
                {
                    int index = surface.property == "_GlossMap" ? Index("_GlossMapChannel", 0) % 4 : 0;
                    targets.Smoothness = new Channel { Property = surface.property, Index = index, Inverted = surface.role == "roughness" };
                }
            }
            return targets;
        }

        /// <summary>The maps the material takes when the Painter project is saved.</summary>
        public List<string> Maps
        {
            get
            {
                var maps = new List<string>();
                if (Color != null) maps.Add("basecolor");
                if (Normal != null) maps.Add("normal");
                if (Smoothness != null) maps.Add("roughness");
                if (Metallic != null) maps.Add("metallic");
                if (Emission != null) maps.Add("emissive");
                if (Occlusion != null) maps.Add("ambientocclusion");
                return maps;
            }
        }

        /// <summary>
        /// The channels shown live: the base colour always (the shader samples it whether or not it has a texture), the others
        /// only where the material already has a texture (a slot without one may be switched off in the shader: the first
        /// save in Painter fills it, then it shows live too).
        /// </summary>
        public List<string> LiveChannels(Material material)
        {
            bool Filled(string property) => property != null && material.HasProperty(property) && material.GetTexture(property) != null;
            var channels = new List<string>();
            if (Color != null && material.HasProperty(Color)) channels.Add("basecolor");
            if (Filled(Normal)) channels.Add("normal");
            if (Smoothness != null && Filled(Smoothness.Property)) channels.Add("roughness");
            if (Metallic != null && Filled(Metallic.Property)) channels.Add("metallic");
            if (Filled(Emission)) channels.Add("emissive");
            if (Filled(Occlusion)) channels.Add("ambientocclusion");
            return channels;
        }

        /// <summary>The texture role a map is applied as (see <see cref="TextureEntry.role"/>), and the slot it goes to.</summary>
        public (string role, string property) Slot(string map)
        {
            switch (map)
            {
                case "basecolor": return ("color", Color);
                case "normal": return ("normal", Normal);
                case "roughness": return ("roughness", Smoothness?.Property);
                case "metallic": return ("metallic", Metallic?.Property);
                case "emissive": return ("emission", Emission);
                case "ambientocclusion": return ("occlusion", Occlusion);
                default: return (null, null);
            }
        }
    }
}
