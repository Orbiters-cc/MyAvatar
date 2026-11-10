using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using Unity.Collections;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// The textures an upload gets: for each texture the plan changes, a copy at its size and format, in copies of the
    /// materials that show it, the materials and textures animations swap in included. A copy is made from the image file
    /// itself when Unity's importer only passes its pixels through (PNG, JPG: nothing compressed twice), else from the
    /// texture as imported, never scaled up; halved on the GPU step by step, its mips keeping a cut-out's coverage when the
    /// importer does, then compressed for the platform. Only the build copy changes: the avatar's textures, materials and
    /// import settings stay as they are. Copies are kept in Library by the texture's imported contents and the plan's size
    /// and format, so the next upload reuses them.
    /// </summary>
    internal static class UploadTextures
    {
        internal static string CacheFolder => Path.Combine(LibraryStore.Folder, "UploadTextures");
        // Raised when copies are made differently: old ones are made again.
        private const int Version = 2;
        // The most the copies take on disk: those used longest ago go first.
        private const long CacheBytes = 1L << 30;

        /// <summary>Makes each copy <see cref="Apply"/> swaps in (tests make one fail half-way).</summary>
        internal static Func<TextureOptimization.TexturePlan, Texture2D> MakeCopy = Copy;

        // What a copy costs, for the profiler: reading it from the cache, rendering it on the GPU, compressing it.
        private static readonly ProfilerMarker ReadMarker = new ProfilerMarker("MyAvatar.UploadTextures.Read"),
            RenderMarker = new ProfilerMarker("MyAvatar.UploadTextures.Render"), CompressMarker = new ProfilerMarker("MyAvatar.UploadTextures.Compress");

        /// <summary>
        /// Swaps in the plan's copies on the build copy below <paramref name="root"/>; returns how many textures changed. Every
        /// copy is made before anything changes, and a failure puts back what had changed: nothing is half applied.
        /// </summary>
        internal static int Apply(GameObject root, TextureOptimization.Plan plan)
        {
            if (plan == null || plan.changes.Count == 0) return 0;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var clips = AvatarUsage.Controllers(root).SelectMany(c => c.animationClips).Where(c => c != null).Distinct().ToList();
            var swapped = clips.SelectMany(Swapped).Distinct().ToList();
            var shown = renderers.SelectMany(r => r.sharedMaterials).Concat(swapped.OfType<Material>()).Where(m => m != null).Distinct()
                .SelectMany(m => m.GetTexturePropertyNameIDs().Select(m.GetTexture)).Concat(swapped.OfType<Texture>()).Where(t => t != null).Distinct().ToList();
            var copies = new Dictionary<Texture, Texture2D>();
            var changes = shown.Select(t => (Texture: t, Change: plan.For(t))).Where(c => c.Change != null).ToList();
            // Most copies were made before (an estimate makes the upload's): read from the disk together, on worker threads,
            // before the editor's thread turns them into textures one by one.
            using (ReadAhead(changes.Select(c => c.Change)))
            try
            {
                foreach (var (texture, change) in changes)
                {
                    var copy = MakeCopy(change);
                    if (copy != null) copies[texture] = copy;
                }
            }
            catch
            {
                foreach (var copy in copies.Values) if (copy) Object.DestroyImmediate(copy);
                throw;
            }
            if (copies.Count == 0) return 0;

            var materials = new Dictionary<Material, Material>();
            Material Swap(Material material)
            {
                if (material == null) return null;
                if (materials.TryGetValue(material, out var known)) return known;
                Material copy = material;
                foreach (int id in material.GetTexturePropertyNameIDs())
                {
                    var texture = material.GetTexture(id);
                    if (texture == null || !copies.TryGetValue(texture, out var replacement)) continue;
                    if (copy == material) copy = new Material(material) { name = material.name, renderQueue = material.renderQueue };
                    copy.SetTexture(id, replacement);
                }
                return materials[material] = copy;
            }

            var before = new List<(Renderer Renderer, Material[] Materials)>();
            try
            {
                foreach (var renderer in renderers)
                {
                    var list = renderer.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < list.Length; i++)
                    {
                        var replaced = Swap(list[i]);
                        if (replaced == list[i]) continue;
                        list[i] = replaced; changed = true;
                    }
                    if (!changed) continue;
                    before.Add((renderer, renderer.sharedMaterials));
                    renderer.sharedMaterials = list;
                }
                SwapAnimated(root, clips, Swap, copies);
            }
            catch
            {
                foreach (var (renderer, list) in before) if (renderer) renderer.sharedMaterials = list;
                foreach (var pair in materials) if (pair.Value && pair.Value != pair.Key && !EditorUtility.IsPersistent(pair.Value)) Object.DestroyImmediate(pair.Value);
                foreach (var copy in copies.Values) if (copy && !EditorUtility.IsPersistent(copy)) Object.DestroyImmediate(copy);
                throw;
            }
            return copies.Count;
        }

        // What a clip swaps in: materials, and textures set straight on a renderer's material ("material._MainTex").
        private static IEnumerable<Object> Swapped(AnimationClip clip) =>
            AnimationUtility.GetObjectReferenceCurveBindings(clip).SelectMany(b => AnimationUtility.GetObjectReferenceCurve(clip, b)).Select(k => k.value).Where(v => v != null);

        // Materials and textures animations swap in: the build's copies of the clips point at the copies. All or nothing: the
        // clips are copied for the build first (nothing of this step changed yet if that fails), every rewrite is worked out
        // before any is made, and a rewrite that fails puts back those made. Those clips are build data (assets): the copies
        // they point at become build data first, their textures before them, or the reference would be lost; if what follows
        // fails they stay in the build's data unreferenced (nothing uploads them) until the build is released.
        private static void SwapAnimated(GameObject root, List<AnimationClip> clips, Func<Material, Material> swap, Dictionary<Texture, Texture2D> copies)
        {
            Object Replacement(Object value) =>
                value is Material material ? swap(material) : value is Texture texture && copies.TryGetValue(texture, out var copy) ? copy : value;
            if (!clips.SelectMany(Swapped).Distinct().Any(value => Replacement(value) != value)) return;
            var build = AttachmentAnimationBuild.Prepare(root);
            var rewrites = new List<(AnimationClip Clip, EditorCurveBinding Binding, ObjectReferenceKeyframe[] Before, ObjectReferenceKeyframe[] After)>();
            var animated = new List<Object>();
            foreach (var (clip, _) in build.Clips)
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var before = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    var after = (ObjectReferenceKeyframe[])before.Clone();
                    bool changed = false;
                    for (int i = 0; i < after.Length; i++)
                    {
                        var replacement = Replacement(after[i].value);
                        if (replacement == after[i].value) continue;
                        after[i].value = replacement; changed = true;
                        if (!animated.Contains(replacement)) animated.Add(replacement);
                    }
                    if (changed) rewrites.Add((clip, binding, before, after));
                }
            if (rewrites.Count == 0) return;
            var textures = animated.OfType<Material>().SelectMany(m => m.GetTexturePropertyNameIDs().Select(m.GetTexture)).Concat(animated.OfType<Texture>())
                .Where(t => t != null && !EditorUtility.IsPersistent(t)).Distinct();
            AttachmentAnimationBuild.Keep(root, textures.Cast<Object>().Concat(animated.OfType<Material>().Where(m => !EditorUtility.IsPersistent(m))).ToList());
            int done = 0;
            using var edits = build.Edits();
            try
            {
                foreach (var (clip, binding, _, after) in rewrites)
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, after);
                    EditorUtility.SetDirty(clip);
                    done++;
                }
            }
            catch
            {
                for (int i = 0; i < done; i++) AnimationUtility.SetObjectReferenceCurve(rewrites[i].Clip, rewrites[i].Binding, rewrites[i].Before);
                throw;
            }
        }

        // ---- Copies ----

        /// <summary>
        /// The copy a change asks for: from the cache when it was made before, else made now and cached. Null when the
        /// texture can't give it (the editor holds it smaller than the copy): the upload keeps that texture as it is.
        /// </summary>
        internal static Texture2D Copy(TextureOptimization.TexturePlan change)
        {
            // A size the format can't have (block compression needs whole blocks: 80 x 11 can't be BC7): uploads as it is,
            // and the other textures still get their copies.
            if (!Creatable(change)) return null;
            string file = CacheFile(change);
            var copy = Load(change, Read(file));
            if (copy == null)
            {
                var data = Encode(change);
                if (data == null) return null;
                Store(file, data);
                copy = Load(change, data) ?? throw new InvalidOperationException(change.texture.name + "'s copy couldn't be loaded.");
            }
            var source = change.texture;
            copy.name = source.name;
            copy.wrapModeU = source.wrapModeU; copy.wrapModeV = source.wrapModeV; copy.wrapModeW = source.wrapModeW;
            copy.filterMode = source.filterMode; copy.anisoLevel = source.anisoLevel; copy.mipMapBias = source.mipMapBias;
            // As an imported texture would be: mipmap streaming (VRChat streams avatar textures), and not readable (a readable
            // texture keeps a second copy in memory in game). Set on the serialized data, which keeps the pixels: the build
            // saves the copy as an asset afterwards, and Apply(makeNoLongerReadable) would drop the pixels it needs to save.
            using (var serialized = new SerializedObject(copy))
            {
                var streaming = serialized.FindProperty("m_StreamingMipmaps");
                if (streaming != null) streaming.boolValue = true;
                var readable = serialized.FindProperty("m_IsReadable");
                if (readable != null) readable.boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return copy;
        }

        /// <summary>
        /// The copy's stored data (its blocks, mips included), from the cache or made now: kept in the cache when
        /// <paramref name="cache"/> (the upload will want it). Null when the texture can't give it.
        /// </summary>
        internal static byte[] Data(TextureOptimization.TexturePlan change, bool cache = true)
        {
            string file = CacheFile(change);
            var data = Read(file);
            if (data != null) return data;
            data = Encode(change);
            if (data != null && cache) Store(file, data);
            return data;
        }

        /// <summary>Where the copy is kept.</summary>
        internal static string CacheFile(TextureOptimization.TexturePlan change) => Path.Combine(CacheFolder, Key(change) + ".bin");

        // Block-compressed formats hold whole blocks (4 x 4 texels), ASTC excepted (it pads its blocks).
        private static bool Creatable(TextureOptimization.TexturePlan change)
        {
            var format = GraphicsFormatUtility.GetGraphicsFormat(change.format, !change.linear);
            bool whole = change.width > 0 && change.height > 0 && (!GraphicsFormatUtility.IsCompressedFormat(format) || GraphicsFormatUtility.IsASTCFormat(format) ||
                change.width % GraphicsFormatUtility.GetBlockWidth(format) == 0 && change.height % GraphicsFormatUtility.GetBlockHeight(format) == 0);
            if (!whole && Unavailable.Add("size:" + Key(change)))
                Debug.LogWarning("[My Avatar] Optimization: " + change.texture.name + " uploads as it is (" + change.format + " can't be " + change.width + " x " +
                                 change.height + ").");
            return whole;
        }

        private static Texture2D Load(TextureOptimization.TexturePlan change, byte[] data)
        {
            if (data == null) return null;
            var copy = new Texture2D(change.width, change.height, change.format, true, change.linear);
            try
            {
                copy.LoadRawTextureData(data);
                copy.Apply(false, false);
                return copy;
            }
            catch (Exception)
            {
                // A damaged file: made again.
                Object.DestroyImmediate(copy);
                return null;
            }
        }

        // Copies the editor couldn't give, said once each: the estimate asks again at every change.
        private static readonly HashSet<string> Unavailable = new HashSet<string>();

        // The image at the copy's size with its mips, compressed. Unity's compressor runs on the editor's thread only
        // (EditorUtility.CompressTexture), as do the GPU steps and every texture call: a copy not cached is made here, whole.
        private static byte[] Encode(TextureOptimization.TexturePlan change)
        {
            Texture2D image;
            string why;
            using (RenderMarker.Auto()) image = Render(change, out why);
            if (image == null)
            {
                if (Unavailable.Add(Key(change))) Debug.LogWarning("[My Avatar] Optimization: " + change.texture.name + " uploads as it is (" + why + ").");
                return null;
            }
            try
            {
                using (CompressMarker.Auto())
                {
                    EditorUtility.CompressTexture(image, change.format, change.quality);
                    return image.GetRawTextureData();
                }
            }
            finally { Object.DestroyImmediate(image); }
        }

        // The image at the size, uncompressed with its mip chain: from the image file when the importer only passes its
        // pixels through, else from the texture as imported. Halved on the GPU until close to the size (each step averages
        // 2×2 texels, so no detail shimmers); colour stays in sRGB, data (normal maps, masks) linear. Null (with why) when
        // neither holds the copy's size: it is never scaled up.
        private static Texture2D Render(TextureOptimization.TexturePlan change, out string why)
        {
            why = null;
            var importer = AssetImporter.GetAtPath(change.path) as TextureImporter;
            var file = Original(change, importer);
            try
            {
                Texture source = file;
                if (source == null)
                {
                    // Mipmap streaming can leave the editor only smaller mips; Build Settings' size override or another
                    // platform's import can make the texture smaller than the copy too.
                    var texture = change.texture;
                    int loaded = QualitySettings.streamingMipmapsActive && texture.streamingMipmaps ? Mathf.Max(0, texture.loadedMipmapLevel) : 0;
                    int width = Mathf.Max(1, texture.width >> loaded), height = Mathf.Max(1, texture.height >> loaded);
                    if (width < change.width || height < change.height)
                    {
                        why = "the editor holds it at " + width + " × " + height + ", smaller than its " + change.width + " × " + change.height + " copy";
                        return null;
                    }
                    source = texture;
                }
                var image = Downsample(source, change.width, change.height, change.linear);
                try
                {
                    // BC5 holds X and Y in red and green; DXT5nm keeps X in alpha (red white): X is red × alpha, as shaders read both.
                    if (change.normal && change.format == TextureFormat.BC5) XToRed(image);
                    image.Apply(true);
                    if (importer != null && importer.mipMapsPreserveCoverage && KeepsAlpha(change)) PreserveCoverage(image, importer.alphaTestReferenceValue);
                    return image;
                }
                catch
                {
                    Object.DestroyImmediate(image);
                    throw;
                }
            }
            finally
            {
                if (file) Object.DestroyImmediate(file);
            }
        }

        // The image file read as it is, when the importer only passes its pixels through: a plain colour or mask image whose
        // alpha comes from the file, unswizzled, and whose transparent texels the importer doesn't recolour (alpha is
        // transparency) where the copy keeps alpha. PNG and JPG only.
        private static Texture2D Original(TextureOptimization.TexturePlan change, TextureImporter importer)
        {
            if (importer == null || importer.textureType != TextureImporterType.Default || importer.alphaSource != TextureImporterAlphaSource.FromInput) return null;
            if (importer.swizzleR != TextureImporterSwizzle.R || importer.swizzleG != TextureImporterSwizzle.G ||
                importer.swizzleB != TextureImporterSwizzle.B || importer.swizzleA != TextureImporterSwizzle.A) return null;
            if (importer.alphaIsTransparency && KeepsAlpha(change)) return null;
            string extension = Path.GetExtension(change.path ?? "").ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") return null;
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, change.linear);
            try
            {
                // Read in the colour space the copy is written in, or not at all.
                if (image.LoadImage(File.ReadAllBytes(change.path), false) && image.isDataSRGB != change.linear) return image;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            Object.DestroyImmediate(image);
            return null;
        }

        private static bool KeepsAlpha(TextureOptimization.TexturePlan change) =>
            !change.normal && change.format != TextureFormat.DXT1 && change.format != TextureFormat.DXT1Crunched && change.format != TextureFormat.ETC_RGB4Crunched &&
            change.format != TextureFormat.RGB24 && change.format != TextureFormat.BC5;

        private static Texture2D Downsample(Texture source, int width, int height, bool linear)
        {
            var space = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
            var previous = RenderTexture.active;
            Texture current = source;
            RenderTexture step = null;
            Texture2D image = null;
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
                image = new Texture2D(width, height, TextureFormat.RGBA32, true, linear);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                return image;
            }
            catch
            {
                if (image) Object.DestroyImmediate(image);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                if (step != null) RenderTexture.ReleaseTemporary(step);
            }
        }

        // Rewritten where the texture keeps its pixels (an RGBA32 image): no copy of them in managed memory.
        internal static void XToRed(Texture2D image)
        {
            var pixels = image.GetPixelData<Color32>(0);
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                pixels[i] = new Color32((byte)((p.r * p.a + 127) / 255), p.g, 0, 255);
            }
        }

        // As Unity's "Mip Maps Preserve Coverage": each mip's alpha scaled so as many texels pass the alpha test as in the
        // full image (cut-out fur and lace stay as thick from afar). Each mip's alphas are counted once (256 values): how
        // many texels pass at any scale is then a sum over those counts, so the search for the scale reads no texel, and
        // only the mips that change are written, where the texture keeps them.
        internal static void PreserveCoverage(Texture2D image, float cutoff)
        {
            int limit = Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 0, 254);
            var counts = new long[256];
            long Count(NativeArray<Color32> pixels)
            {
                Array.Clear(counts, 0, counts.Length);
                for (int i = 0; i < pixels.Length; i++) counts[pixels[i].a]++;
                return pixels.Length;
            }
            // The same test as on each texel, once per alpha value.
            float Coverage(long texels, float scale)
            {
                long passing = 0;
                for (int a = 0; a < 256; a++) if (counts[a] > 0 && Mathf.Min(255f, a * scale) > limit) passing += counts[a];
                return passing / (float)texels;
            }
            float wanted = Coverage(Count(image.GetPixelData<Color32>(0)), 1f);
            bool changed = false;
            var scaled = new byte[256];
            for (int mip = 1; mip < image.mipmapCount; mip++)
            {
                var pixels = image.GetPixelData<Color32>(mip);
                long texels = Count(pixels);
                float now = Coverage(texels, 1f);
                if (Mathf.Abs(now - wanted) * texels < 1f) continue;
                float low = now < wanted ? 1f : 0f, high = now < wanted ? 16f : 1f;
                for (int i = 0; i < 12; i++)
                {
                    float middle = (low + high) / 2f;
                    if (Coverage(texels, middle) < wanted) low = middle; else high = middle;
                }
                float scale = (low + high) / 2f;
                for (int a = 0; a < 256; a++) scaled[a] = (byte)Mathf.Min(255, Mathf.RoundToInt(a * scale));
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    p.a = scaled[p.a];
                    pixels[i] = p;
                }
                changed = true;
            }
            if (changed) image.Apply(false);
        }

        // The texture's imported contents and how the copy is made.
        private static string Key(TextureOptimization.TexturePlan change)
        {
            string text = Version + "|" + change.texture.imageContentsHash + "|" + change.width + "x" + change.height + "|" + change.format + "|" +
                          change.quality + "|" + change.linear + "|" + change.normal;
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        private static byte[] Read(string file)
        {
            if (readAhead != null && readAhead.TryGetValue(file, out var ahead))
            {
                readAhead.Remove(file);
                return ahead;
            }
            using (ReadMarker.Auto()) return ReadFile(file);
        }

        // Safe on any thread: plain file calls.
        private static byte[] ReadFile(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var data = File.ReadAllBytes(file);
                // Used now: the last to go.
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                return data;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        // The cached copies read ahead for the copies being made (Apply), by file; each is taken once.
        private static Dictionary<string, byte[]> readAhead;
        // At most this much is read ahead: the rest is read when its copy is made.
        private const long ReadAheadBytes = 512L << 20;

        /// <summary>
        /// Reads the cached copies of <paramref name="changes"/> from the disk together on worker threads (the editor's
        /// thread waits for plain file reads only), for <see cref="Read"/> to take while the scope lasts.
        /// </summary>
        private static IDisposable ReadAhead(IEnumerable<TextureOptimization.TexturePlan> changes)
        {
            var files = new List<string>();
            long bytes = 0;
            foreach (string file in changes.Where(Creatable).Select(CacheFile).Distinct())
            {
                long length;
                try { var info = new FileInfo(file); if (!info.Exists) continue; length = info.Length; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                if (bytes + length > ReadAheadBytes) break;
                bytes += length;
                files.Add(file);
            }
            var read = new byte[files.Count][];
            using (ReadMarker.Auto())
                System.Threading.Tasks.Parallel.For(0, files.Count, i => read[i] = ReadFile(files[i]));
            readAhead = new Dictionary<string, byte[]>();
            for (int i = 0; i < files.Count; i++) if (read[i] != null) readAhead[files[i]] = read[i];
            return new Scope(() => readAhead = null);
        }

        private sealed class Scope : IDisposable
        {
            private Action end;
            public Scope(Action end) => this.end = end;
            public void Dispose() { end?.Invoke(); end = null; }
        }

        // Written whole or not at all: a temporary file moved in place.
        private static void Store(string file, byte[] data)
        {
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(CacheFolder);
                long replaced = File.Exists(file) ? new FileInfo(file).Length : 0;
                File.WriteAllBytes(temp, data);
                if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
                Stored(data.Length - replaced);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        // What the cache folder holds on the disk: counted once (the first copy stored there), then kept as copies are
        // stored and trimmed.
        private static (string Folder, long Bytes)? cached;

        // The copies used longest ago go once they take more than the cache's size, down to nine tenths of it: the folder
        // is listed again only then, not after every copy.
        private static void Stored(long added)
        {
            string folder = CacheFolder;
            long held = cached != null && cached.Value.Folder == folder ? cached.Value.Bytes + added : new DirectoryInfo(folder).GetFiles("*.bin").Sum(f => f.Length);
            cached = (folder, held);
            if (held <= CacheBytes) return;
            var files = new DirectoryInfo(folder).GetFiles("*.bin");
            long total = files.Sum(f => f.Length);
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= CacheBytes / 10 * 9) break;
                try { old.Delete(); total -= old.Length; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            cached = (folder, total);
        }
    }
}
