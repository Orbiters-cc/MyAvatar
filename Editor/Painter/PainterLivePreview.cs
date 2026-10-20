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
    /// renderers' material property blocks, which are never saved, recorded for Undo or seen by other tools. Images are
    /// uploaded as they come and their mips made on the GPU; metallic and roughness are packed on the GPU the way the
    /// material reads them. Taken off when the link closes, when the textures are applied for good, when the texture set
    /// paints another material, and before script reloads: a slot gets back what other tools had put in its block, and the
    /// images of the other texture sets showing on it.
    /// </summary>
    internal sealed class PainterLivePreview : IDisposable
    {
        private sealed class SetState
        {
            public Material Material;
            public readonly Dictionary<string, RenderTexture> Images = new Dictionary<string, RenderTexture>(StringComparer.Ordinal);
            public readonly Dictionary<string, long> Revisions = new Dictionary<string, long>(StringComparer.Ordinal);
            public readonly Dictionary<string, RenderTexture> Packed = new Dictionary<string, RenderTexture>(StringComparer.Ordinal);
            // The slots it shows on, and what it put in each one's property block.
            public readonly Dictionary<(Renderer, int), Dictionary<string, Texture>> Parts = new Dictionary<(Renderer, int), Dictionary<string, Texture>>();
        }

        private static Material packer;
        private readonly Dictionary<string, SetState> sets = new Dictionary<string, SetState>(StringComparer.Ordinal);
        // Each slot's block as it was before the first live image went in (what other tools put there).
        private readonly Dictionary<(Renderer, int), MaterialPropertyBlock> before = new Dictionary<(Renderer, int), MaterialPropertyBlock>();

        public PainterLivePreview() => AssemblyReloadEvents.beforeAssemblyReload += Dispose;

        /// <summary>Whether a newer image of this channel was shown already (an older one arriving late is dropped).</summary>
        public bool Stale(string textureSet, string channel, long revision) =>
            sets.TryGetValue(textureSet, out var set) && set.Revisions.TryGetValue(channel, out long shown) && shown >= revision;

        /// <summary>The texture sets shown live now.</summary>
        public IEnumerable<string> Live => sets.Where(p => p.Value.Parts.Count > 0).Select(p => p.Key);

        /// <summary>The material a texture set shows on now, or null.</summary>
        public Material ShownOn(string textureSet) => sets.TryGetValue(textureSet, out var set) && set.Parts.Count > 0 ? set.Material : null;

        /// <summary>
        /// Shows a decoded image of a texture set's channel on every renderer slot showing <paramref name="material"/> under
        /// <paramref name="root"/>.
        /// </summary>
        public void Show(Transform root, string textureSet, string channel, long revision, TgaImage image, Material material, PainterTargets targets)
        {
            // Painting another material now: the one it showed on gets its own textures back first.
            if (sets.TryGetValue(textureSet, out var set) && set.Material != material) { Clear(textureSet); set = null; }
            if (set == null) sets[textureSet] = set = new SetState { Material = material };
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

        // Uploaded without a mip chain (making one on the CPU took the editor's thread for every image), then copied on the
        // GPU into the texture shown, which makes its mips.
        private static RenderTexture Upload(SetState set, string channel, TgaImage image, bool linear)
        {
            var upload = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false, linear) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                upload.SetPixelData(image.Rgba, 0);
                upload.Apply(false, true);
                var shown = Target(set.Images, channel, image.Width, image.Height, linear);
                Blit(upload, shown, null);
                return shown;
            }
            finally { Object.DestroyImmediate(upload); }
        }

        private static RenderTexture Target(Dictionary<string, RenderTexture> cache, string key, int width, int height, bool linear)
        {
            if (cache.TryGetValue(key, out var target) && target && target.width == width && target.height == height) return target;
            if (target) { target.Release(); Object.DestroyImmediate(target); }
            target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
            {
                name = "Painter live " + key, hideFlags = HideFlags.HideAndDontSave, useMipMap = true, autoGenerateMips = true,
                wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4,
            };
            target.Create();
            cache[key] = target;
            return target;
        }

        // The material's packed (or single) texture, its metallic and smoothness channels replaced by what Painter streams;
        // a channel not streamed yet keeps the material's own values.
        private static RenderTexture Pack(SetState set, string property, Material material, PainterTargets targets)
        {
            set.Images.TryGetValue("metallic", out var metal);
            set.Images.TryGetValue("roughness", out var rough);
            var source = metal ? metal : rough;
            var packed = Target(set.Packed, property, source.width, source.height, linear: true);
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
            packer.SetTexture("_Metal", metal ? (Texture)metal : Texture2D.blackTexture);
            packer.SetTexture("_Rough", rough ? (Texture)rough : Texture2D.whiteTexture);
            packer.SetVector("_KeepMask", keep);
            packer.SetVector("_Constant", Vector4.zero);
            packer.SetVector("_MetalMask", metalMask);
            packer.SetVector("_InvertedMetalMask", invertedMetal);
            packer.SetVector("_SmoothMask", smooth);
            packer.SetVector("_RoughMask", roughMask);
            var own = material.HasProperty(property) ? material.GetTexture(property) : null;
            // Pass 0 alone: without a pass index Blit runs every pass of the shader, the last one (a channel read with no
            // channel chosen) writing (0, 0, 0, 1).
            Blit(own ? own : Texture2D.blackTexture, packed, packer);
            return packed;
        }

        // Blit leaves its target active: the one before is put back (the target's mips are made once it isn't active).
        private static void Blit(Texture source, RenderTexture target, Material material)
        {
            var active = RenderTexture.active;
            try
            {
                if (material) Graphics.Blit(source, target, material, 0);
                else Graphics.Blit(source, target);
            }
            finally { RenderTexture.active = active; }
        }

        private void Assign(SetState set, IEnumerable<(Renderer renderer, int index)> parts, string property, Texture texture)
        {
            if (property == null || texture == null) return;
            var block = new MaterialPropertyBlock();
            foreach (var (renderer, index) in parts)
            {
                var slot = (renderer, index);
                if (!before.ContainsKey(slot))
                {
                    var kept = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(kept, index);
                    before[slot] = kept;
                }
                renderer.GetPropertyBlock(block, index);
                block.SetTexture(property, texture);
                renderer.SetPropertyBlock(block, index);
                if (!set.Parts.TryGetValue(slot, out var assigned)) set.Parts[slot] = assigned = new Dictionary<string, Texture>(StringComparer.Ordinal);
                assigned[property] = texture;
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
            var slots = new HashSet<(Renderer, int)>();
            foreach (var key in sets.Keys.Where(k => textureSet == null || k == textureSet).ToList())
            {
                var set = sets[key];
                slots.UnionWith(set.Parts.Keys);
                foreach (var image in set.Images.Values.Concat(set.Packed.Values))
                    if (image) { image.Release(); Object.DestroyImmediate(image); }
                sets.Remove(key);
            }
            var block = new MaterialPropertyBlock();
            foreach (var slot in slots)
            {
                var (renderer, index) = slot;
                before.TryGetValue(slot, out var kept);
                var still = sets.Values.Where(s => s.Parts.ContainsKey(slot)).ToList();
                if (still.Count == 0) before.Remove(slot);
                if (!renderer) continue;
                // The block as other tools left it, then what the sets still shown put in.
                renderer.SetPropertyBlock(kept ?? new MaterialPropertyBlock(), index);
                if (still.Count == 0) continue;
                renderer.GetPropertyBlock(block, index);
                foreach (var pair in still.SelectMany(s => s.Parts[slot]))
                    if (pair.Value) block.SetTexture(pair.Key, pair.Value);
                renderer.SetPropertyBlock(block, index);
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
