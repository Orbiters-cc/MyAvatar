using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Textures;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
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
            // What the sets taken off put in each slot.
            var taken = new Dictionary<(Renderer, int), Dictionary<string, Texture>>();
            var removed = sets.Where(p => textureSet == null || p.Key == textureSet).ToList();
            foreach (var pair in removed)
            {
                foreach (var part in pair.Value.Parts)
                {
                    if (!taken.TryGetValue(part.Key, out var properties)) taken[part.Key] = properties = new Dictionary<string, Texture>(StringComparer.Ordinal);
                    foreach (var property in part.Value) properties[property.Key] = property.Value;
                }
                sets.Remove(pair.Key);
            }
            var current = new MaterialPropertyBlock();
            foreach (var pair in taken)
            {
                var slot = pair.Key;
                var (renderer, index) = slot;
                before.TryGetValue(slot, out var kept);
                var still = sets.Values.Where(s => s.Parts.ContainsKey(slot)).ToList();
                if (still.Count == 0) before.Remove(slot);
                if (!renderer) continue;
                // The block as it is now, what other tools set since included, without what the live images put in (a
                // property another tool set since keeps its value). A property they replaced gets back what it held before,
                // then the sets still shown put theirs in again.
                renderer.GetPropertyBlock(current, index);
                var ours = pair.Value.Where(p => current.HasTexture(p.Key) && current.GetTexture(p.Key) == p.Value).Select(p => p.Key).ToList();
                var materials = renderer.sharedMaterials;
                var block = Without(current, index < materials.Length && materials[index] ? materials[index].shader : null, ours);
                if (kept != null)
                    foreach (string property in ours)
                        if (kept.HasTexture(property) && kept.GetTexture(property) is Texture held && held) block.SetTexture(property, held);
                foreach (var shown in still.SelectMany(s => s.Parts[slot]))
                    if (shown.Value) block.SetTexture(shown.Key, shown.Value);
                renderer.SetPropertyBlock(block, index);
            }
            foreach (var pair in removed)
                foreach (var image in pair.Value.Images.Values.Concat(pair.Value.Packed.Values))
                    if (image) { image.Release(); Object.DestroyImmediate(image); }
            SceneView.RepaintAll();
        }

        // A copy of the block without these texture properties (a block can't drop one): every other property the slot's
        // shader declares is carried over, with its textures' tiling, size and HDR values.
        private static MaterialPropertyBlock Without(MaterialPropertyBlock block, Shader shader, ICollection<string> dropped)
        {
            var copy = new MaterialPropertyBlock();
            if (!shader) return copy;
            void Vector(string name)
            {
                if (block.HasVector(name) || block.HasColor(name)) copy.SetVector(name, block.GetVector(name));
            }
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Texture:
                        if (!dropped.Contains(name) && block.HasTexture(name) && block.GetTexture(name) is Texture texture && texture) copy.SetTexture(name, texture);
                        Vector(name + "_ST");
                        // Unity adds a texture's size and HDR decoding to the block with it: they leave with a dropped one.
                        if (dropped.Contains(name)) break;
                        Vector(name + "_TexelSize");
                        Vector(name + "_HDR");
                        break;
                    case ShaderPropertyType.Color:
                    case ShaderPropertyType.Vector:
                        Vector(name);
                        break;
                    case ShaderPropertyType.Int:
                        if (block.HasInteger(name)) copy.SetInteger(name, block.GetInteger(name));
                        else if (block.HasFloat(name)) copy.SetFloat(name, block.GetFloat(name));
                        break;
                    default:
                        if (block.HasFloat(name)) copy.SetFloat(name, block.GetFloat(name));
                        break;
                }
            }
            return copy;
        }

        public void Dispose()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            Clear();
        }
    }
}
