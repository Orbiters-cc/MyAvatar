using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Textures;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// What Substance 3D Painter streams, shown on the avatar: each image goes into the material slot it paints through the
    /// renderers' material property blocks, which are never saved, recorded for Undo or seen by other tools. Metallic and
    /// roughness are packed on the GPU the way the material reads them. Taken off when the link closes, when the textures are
    /// applied for good, and before script reloads.
    /// </summary>
    internal sealed class PainterLivePreview : IDisposable
    {
        private sealed class SetState
        {
            public readonly Dictionary<string, Texture2D> Images = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> Revisions = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, RenderTexture> Packed = new Dictionary<string, RenderTexture>(StringComparer.Ordinal);
            public readonly HashSet<(Renderer, int)> Parts = new HashSet<(Renderer, int)>();
        }

        private static Material packer;
        private readonly Dictionary<string, SetState> sets = new Dictionary<string, SetState>(StringComparer.Ordinal);

        public PainterLivePreview() => AssemblyReloadEvents.beforeAssemblyReload += Dispose;

        /// <summary>Whether a newer image of this channel was shown already (an older one arriving late is dropped).</summary>
        public bool Stale(string textureSet, string channel, long revision) =>
            sets.TryGetValue(textureSet, out var set) && set.Revisions.TryGetValue(channel, out long shown) && shown >= revision;

        /// <summary>The texture sets shown live now.</summary>
        public IEnumerable<string> Live => sets.Where(p => p.Value.Parts.Count > 0).Select(p => p.Key);

        /// <summary>
        /// Shows a decoded image of a texture set's channel on every renderer slot showing <paramref name="material"/> under
        /// <paramref name="root"/>.
        /// </summary>
        public void Show(Transform root, string textureSet, string channel, long revision, TgaImage image, Material material, PainterTargets targets)
        {
            if (!sets.TryGetValue(textureSet, out var set)) sets[textureSet] = set = new SetState();
            set.Revisions[channel] = revision;
            bool color = channel == "basecolor" || channel == "emissive";
            var texture = Upload(set, channel, image, linear: !color);
            var parts = Parts(root, material).ToList();
            switch (channel)
            {
                case "basecolor": Assign(set, parts, targets.Color, texture); break;
                case "normal": Assign(set, parts, targets.Normal, texture); break;
                case "emissive": Assign(set, parts, targets.Emission, texture); break;
                case "ambientocclusion": Assign(set, parts, targets.Occlusion, texture); break;
                case "metallic":
                case "roughness":
                    foreach (var property in new[] { targets.Metallic?.Property, targets.Smoothness?.Property }.Where(p => p != null).Distinct())
                        Assign(set, parts, property, Pack(set, property, material, targets));
                    break;
            }
            SceneView.RepaintAll();
        }

        private static Texture2D Upload(SetState set, string channel, TgaImage image, bool linear)
        {
            if (!set.Images.TryGetValue(channel, out var texture) || !texture || texture.width != image.Width || texture.height != image.Height)
            {
                if (texture) Object.DestroyImmediate(texture);
                texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, true, linear)
                {
                    name = "Painter live " + channel, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear, anisoLevel = 4,
                };
                set.Images[channel] = texture;
            }
            texture.SetPixelData(image.Rgba, 0);
            texture.Apply(true, false);
            return texture;
        }

        // The material's packed (or single) texture, its metallic and smoothness channels replaced by what Painter streams;
        // a channel not streamed yet keeps the material's own values.
        private static RenderTexture Pack(SetState set, string property, Material material, PainterTargets targets)
        {
            set.Images.TryGetValue("metallic", out var metal);
            set.Images.TryGetValue("roughness", out var rough);
            var source = metal ? metal : rough;
            if (!set.Packed.TryGetValue(property, out var packed) || !packed || packed.width != source.width || packed.height != source.height)
            {
                if (packed) { packed.Release(); Object.DestroyImmediate(packed); }
                packed = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "Painter live " + property, hideFlags = HideFlags.HideAndDontSave, useMipMap = true, autoGenerateMips = true,
                    wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4,
                };
                packed.Create();
                set.Packed[property] = packed;
            }
            if (!packer) packer = new Material(Shader.Find("Hidden/Orbiters/MyAvatar/PainterPack")) { hideFlags = HideFlags.HideAndDontSave };
            Vector4 keep = Vector4.one, metalMask = Vector4.zero, invertedMetal = Vector4.zero, smooth = Vector4.zero, roughMask = Vector4.zero;
            if (metal && targets.Metallic?.Property == property)
            {
                int i = targets.Metallic.Index;
                keep[i] = 0;
                if (targets.Metallic.Inverted) invertedMetal[i] = 1; else metalMask[i] = 1;
            }
            if (rough && targets.Smoothness?.Property == property)
            {
                int i = targets.Smoothness.Index;
                keep[i] = 0;
                if (targets.Smoothness.Inverted) roughMask[i] = 1; else smooth[i] = 1;
            }
            packer.SetTexture("_Metal", metal ? metal : Texture2D.blackTexture);
            packer.SetTexture("_Rough", rough ? rough : Texture2D.whiteTexture);
            packer.SetVector("_KeepMask", keep);
            packer.SetVector("_Constant", Vector4.zero);
            packer.SetVector("_MetalMask", metalMask);
            packer.SetVector("_InvertedMetalMask", invertedMetal);
            packer.SetVector("_SmoothMask", smooth);
            packer.SetVector("_RoughMask", roughMask);
            var own = material.HasProperty(property) ? material.GetTexture(property) : null;
            Graphics.Blit(own ? own : Texture2D.blackTexture, packed, packer);
            return packed;
        }

        private static void Assign(SetState set, IEnumerable<(Renderer renderer, int index)> parts, string property, Texture texture)
        {
            if (property == null || texture == null) return;
            var block = new MaterialPropertyBlock();
            foreach (var (renderer, index) in parts)
            {
                renderer.GetPropertyBlock(block, index);
                block.SetTexture(property, texture);
                renderer.SetPropertyBlock(block, index);
                set.Parts.Add((renderer, index));
            }
        }

        /// <summary>Every renderer slot under <paramref name="root"/> showing <paramref name="material"/>.</summary>
        internal static IEnumerable<(Renderer, int)> Parts(Transform root, Material material)
        {
            if (!root || !material) yield break;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] == material) yield return (renderer, i);
            }
        }

        /// <summary>Takes a texture set off (every set without one), its images released.</summary>
        public void Clear(string textureSet = null)
        {
            var empty = new MaterialPropertyBlock();
            foreach (var key in sets.Keys.ToList())
            {
                if (textureSet != null && key != textureSet) continue;
                var set = sets[key];
                foreach (var (renderer, index) in set.Parts)
                    if (renderer) renderer.SetPropertyBlock(empty, index);
                foreach (var image in set.Images.Values) if (image) Object.DestroyImmediate(image);
                foreach (var packed in set.Packed.Values)
                    if (packed) { packed.Release(); Object.DestroyImmediate(packed); }
                sets.Remove(key);
            }
            SceneView.RepaintAll();
        }

        public void Dispose()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            Clear();
        }
    }
}
