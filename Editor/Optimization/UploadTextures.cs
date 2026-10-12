using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// The textures an upload gets: for each texture the plan changes, a copy at its size and format, made from the texture
    /// as imported (halved on the GPU step by step, then compressed for the platform), in copies of the materials that show
    /// it, the materials animations swap in included. Only the build copy changes: the avatar's textures, materials and
    /// import settings stay as they are. Copies are kept in Library by the texture's imported contents and the plan's size
    /// and format, so the next upload reuses them.
    /// </summary>
    internal static class UploadTextures
    {
        private const string CacheFolder = "Library/OrbitersMyAvatar/UploadTextures";
        // Raised when copies are made differently: old ones are made again.
        private const int Version = 1;
        private const int CacheFiles = 600;

        /// <summary>Swaps in the plan's copies on the build copy below <paramref name="root"/>; returns how many textures changed.</summary>
        internal static int Apply(GameObject root, TextureOptimization.Plan plan)
        {
            if (plan == null || plan.changes.Count == 0) return 0;
            var copies = new Dictionary<Texture, Texture2D>();
            var materials = new Dictionary<Material, Material>();

            Material Swap(Material material)
            {
                if (material == null) return null;
                if (materials.TryGetValue(material, out var known)) return known;
                Material copy = material;
                foreach (int id in material.GetTexturePropertyNameIDs())
                {
                    var texture = material.GetTexture(id);
                    var change = plan.For(texture);
                    if (change == null) continue;
                    if (!copies.TryGetValue(texture, out var replacement)) copies[texture] = replacement = Copy(change);
                    if (copy == material) copy = new Material(material) { name = material.name, renderQueue = material.renderQueue };
                    copy.SetTexture(id, replacement);
                }
                return materials[material] = copy;
            }

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var list = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < list.Length; i++)
                {
                    var swapped = Swap(list[i]);
                    if (swapped == list[i]) continue;
                    list[i] = swapped; changed = true;
                }
                if (changed) renderer.sharedMaterials = list;
            }
            SwapAnimated(root, Swap);
            return copies.Count;
        }

        // Materials animations swap in: the build's copies of the clips point at the material copies. Those clips are build
        // data (assets): the copies they point at become build data first, their textures before them, or the reference
        // would be lost.
        private static void SwapAnimated(GameObject root, Func<Material, Material> swap)
        {
            var animated = new List<Material>();
            bool Animates(AnimationClip clip) => AnimationUtility.GetObjectReferenceCurveBindings(clip)
                .Any(b => AnimationUtility.GetObjectReferenceCurve(clip, b).Any(k => k.value is Material m && swap(m) != m));
            var clips = Toolkit.Editor.VRChat.Optimization.AvatarUsage.Controllers(root).SelectMany(c => c.animationClips).Where(c => c != null).Distinct().ToList();
            if (!clips.Any(Animates)) return;
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                        if (key.value is Material material && swap(material) is Material copy && copy != material && !animated.Contains(copy)) animated.Add(copy);
            var textures = animated.SelectMany(m => m.GetTexturePropertyNameIDs().Select(m.GetTexture)).Where(t => t != null && !EditorUtility.IsPersistent(t)).Distinct();
            AttachmentAnimationBuild.Keep(root, textures.Cast<Object>().Concat(animated.Where(m => !EditorUtility.IsPersistent(m))).ToList());
            var build = AttachmentAnimationBuild.Prepare(root);
            foreach (var (clip, _) in build.Clips)
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    bool changed = false;
                    for (int i = 0; i < keys.Length; i++)
                        if (keys[i].value is Material material && swap(material) is Material copy && copy != material) { keys[i].value = copy; changed = true; }
                    if (!changed) continue;
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
                    EditorUtility.SetDirty(clip);
                }
        }

        // ---- Copies ----

        /// <summary>The copy a change asks for: from the cache when it was made before, else made now and cached.</summary>
        internal static Texture2D Copy(TextureOptimization.TexturePlan change)
        {
            var source = change.texture;
            string file = Path.Combine(CacheFolder, Key(change) + ".bin");
            Texture2D copy = null;
            if (File.Exists(file))
            {
                try
                {
                    copy = new Texture2D(change.width, change.height, change.format, true, change.linear);
                    copy.LoadRawTextureData(File.ReadAllBytes(file));
                    copy.Apply(false, false);
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                }
                catch (Exception)
                {
                    if (copy) Object.DestroyImmediate(copy);
                    copy = null;
                }
            }
            if (copy == null)
            {
                copy = Render(source, change.width, change.height, change.linear);
                EditorUtility.CompressTexture(copy, change.format, change.quality);
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllBytes(file, copy.GetRawTextureData());
                Trim();
            }
            copy.name = source.name;
            copy.wrapModeU = source.wrapModeU; copy.wrapModeV = source.wrapModeV; copy.wrapModeW = source.wrapModeW;
            copy.filterMode = source.filterMode; copy.anisoLevel = source.anisoLevel; copy.mipMapBias = source.mipMapBias;
            // Mipmap streaming, as an imported texture would have it (VRChat streams avatar textures).
            using (var serialized = new SerializedObject(copy))
            {
                var streaming = serialized.FindProperty("m_StreamingMipmaps");
                if (streaming != null) { streaming.boolValue = true; serialized.ApplyModifiedPropertiesWithoutUndo(); }
            }
            return copy;
        }

        // The image at the size, halved on the GPU until close to it (each step averages 2×2 texels, so no detail shimmers),
        // read back with its mip chain. Colour stays in sRGB, data (normal maps, masks) linear.
        private static Texture2D Render(Texture2D source, int width, int height, bool linear)
        {
            var space = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
            var previous = RenderTexture.active;
            Texture current = source;
            RenderTexture step = null;
            try
            {
                int w = source.width, h = source.height;
                while (w / 2 >= width && h / 2 >= height && (w / 2 > width || h / 2 > height))
                {
                    w /= 2; h /= 2;
                    var next = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, space);
                    next.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(current, next);
                    if (step != null) RenderTexture.ReleaseTemporary(step);
                    current = step = next;
                }
                var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, space);
                Graphics.Blit(current, target);
                if (step != null) RenderTexture.ReleaseTemporary(step);
                step = target;
                RenderTexture.active = target;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(true);
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                if (step != null) RenderTexture.ReleaseTemporary(step);
            }
        }

        // The texture's imported contents and how the copy is made.
        private static string Key(TextureOptimization.TexturePlan change)
        {
            string text = Version + "|" + change.texture.imageContentsHash + "|" + change.width + "x" + change.height + "|" + change.format + "|" +
                          change.quality + "|" + change.linear;
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        // The oldest copies go once there are too many.
        private static void Trim()
        {
            var files = new DirectoryInfo(CacheFolder).GetFiles("*.bin");
            if (files.Length <= CacheFiles) return;
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - CacheFiles))
                try { old.Delete(); } catch (IOException) { }
        }
    }
}
