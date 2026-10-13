using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// How big the file players download is (what VRChat shows as the avatar's size), estimated without building it: what
    /// the bundle holds, each part at the size its build stores (textures in their build format, meshes with their
    /// vertices, triangles and blendshape offsets, animations, shaders, sounds), then compressed as VRChat compresses it
    /// (LZMA). Textures are measured: the data the bundle stores (the upload's copy when the upload changes the texture) is
    /// compressed with Deflate, scaled to what LZMA makes of it, and kept in Library. The other ratios were measured on
    /// real bundles (2026-10-12, MasculineCanine built three ways): vertices to about a third, blendshape offsets to a
    /// fifth, a locked shader to about 27 KB.
    /// </summary>
    internal static class DownloadSize
    {
        internal sealed class Parts
        {
            public long Textures, Meshes, BlendShapes, Animations, Shaders, Audio, Other;
            /// <summary>The bundle the SDK uploads, compressed: what VRChat's download limits apply to.</summary>
            public long Bundle => Textures + Meshes + BlendShapes + Animations + Shaders + Audio + Other;
            /// <summary>The bundle's size as VRChat shows it (see <see cref="Shown"/>).</summary>
            public long Total => (long)(Bundle * Shown);
            /// <summary>
            /// The same once unpacked: what a player's game decompresses and loads after the download (VRChat's uncompressed
            /// limits apply to it).
            /// </summary>
            public long Raw;
        }

        /// <summary>VRChat's limits on an avatar's bundle, compressed (the download) and unpacked, on PC and on mobile.</summary>
        internal const long PcLimit = 200L * 1048576, PcRawLimit = 500L * 1048576, MobileLimit = 10L * 1048576, MobileRawLimit = 40L * 1048576;

        // Loading the built MasculineCanine bundle in Unity 2022.3 (VRChat's version) on a desktop CPU (2026-10-12): opening it
        // (LZMA, 18.4 MB in, 84 MB out) took 830 ms, loading its assets 110 ms. A client then sets the avatar up (its
        // animator layers, PhysBones, contacts). Quest's processor is about four times slower.
        private const float UnpackPerSecond = 100f * 1048576, LoadPerSecond = 750f * 1048576, Setup = .1f, PerLayer = .001f, PerPhysBone = .0005f, Quest = 4f;
        // Uncompressed sizes of what isn't measured: a compiled shader for one platform, a material, a component.
        private const long RawShader = 150 * 1024, RawMaterial = 20 * 1024, RawComponent = 1024;

        /// <summary>
        /// Seconds from the end of the download until the avatar shows: unpacking (LZMA), loading its assets, setting it up.
        /// On a desktop PC like the one it was measured on; <paramref name="mobile"/> for a Quest.
        /// </summary>
        internal static float LoadSeconds(Parts parts, GameObject root, bool mobile)
        {
            int layers = AvatarUsage.Controllers(root).OfType<UnityEditor.Animations.AnimatorController>().Sum(c => c.layers.Length);
            int physBones = root.GetComponentsInChildren<VRC.Dynamics.VRCPhysBoneBase>(true).Length;
            float seconds = parts.Raw / UnpackPerSecond + parts.Raw / LoadPerSecond + Setup + layers * PerLayer + physBones * PerPhysBone;
            return mobile ? seconds * Quest : seconds;
        }

        // Compressed size over stored size for meshes and animations (MasculineCanine's built meshes, 12.84 MB, within 0.2%).
        private const float Vertices = .33f, Shapes = .2f, Clips = .5f;
        // VRChat shows an avatar's size about 5% under the bundle the SDK builds and uploads: MasculineCanine's 17.70 MB
        // bundle shows as 16.83 MB in game (2026-10-12, one avatar measured).
        private const float Shown = .951f;
        // A shader's compiled variants for one platform, and what every object, component and material adds, compressed.
        private const long Shader = 28 * 1024, PerComponent = 300, PerMaterial = 3 * 1024;
        // How compressible a texture's pixels are when its source image says nothing (PSD, TGA, generated).
        private const float UnknownEntropy = .25f;

        /// <summary>
        /// The download of <paramref name="root"/> as it stands, for PC or for Quest (Android). <paramref name="planned"/>
        /// replaces a texture's size and format (textures an optimization will resize); <paramref name="kept"/> gives the
        /// blendshapes a skinned mesh keeps when a build step removes the rest (null: all of them).
        /// </summary>
        internal static Parts Of(GameObject root, bool mobile = false, Func<Texture, TextureOptimization.TexturePlan> planned = null,
            Func<SkinnedMeshRenderer, ICollection<string>> kept = null)
        {
            var parts = new Parts();
            var meshes = new Dictionary<Mesh, ICollection<string>>();
            var clips = new HashSet<AnimationClip>();
            var sounds = new HashSet<AudioClip>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = MeshOptimization.MeshOf(renderer);
                if (mesh == null || meshes.ContainsKey(mesh) && meshes[mesh] == null) continue;
                // Null: every shape stays; else the shapes kept.
                meshes[mesh] = renderer is SkinnedMeshRenderer skinned && kept != null && mesh.blendShapeCount > 0 ? kept(skinned) : null;
            }
            var materials = Materials(root, clips);
            foreach (var source in root.GetComponentsInChildren<AudioSource>(true)) if (source.clip != null) sounds.Add(source.clip);

            foreach (var texture in Textures(root, materials))
            {
                parts.Textures += Texture(texture, mobile, planned?.Invoke(texture), out long raw);
                parts.Raw += raw;
            }
            foreach (var pair in meshes)
            {
                long vertices = VertexBytes(pair.Key);
                long shapes = ShapeBytes(pair.Key).Where(s => pair.Value == null || pair.Value.Contains(s.Key)).Sum(s => s.Value);
                parts.Meshes += (long)(vertices * Vertices);
                parts.BlendShapes += (long)(shapes * Shapes);
                parts.Raw += vertices + shapes;
            }
            foreach (var clip in clips)
            {
                long raw = ClipBytes(clip);
                parts.Animations += (long)(raw * Clips);
                parts.Raw += raw;
            }
            int shaderCount = materials.Select(m => m.shader).Where(s => s != null).Distinct().Count();
            parts.Shaders = shaderCount * Shader;
            foreach (var sound in sounds) parts.Audio += Sound(sound);
            int components = root.GetComponentsInChildren<Component>(true).Length;
            parts.Other = components * PerComponent + materials.Count * PerMaterial;
            parts.Raw += shaderCount * RawShader + materials.Count * RawMaterial + components * RawComponent + parts.Audio;
            return parts;
        }

        // ---- What the bundle holds ----

        // The renderers' materials and the materials animations swap in (they upload with them); clips collects the animations.
        private static HashSet<Material> Materials(GameObject root, HashSet<AnimationClip> clips)
        {
            var materials = new HashSet<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) materials.UnionWith(renderer.sharedMaterials.Where(m => m != null));
            foreach (var controller in AvatarUsage.Controllers(root))
                foreach (var clip in controller.animationClips.Where(c => c != null))
                {
                    if (!clips.Add(clip)) continue;
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                        foreach (var key in AnimationUtility.GetObjectReferenceCurve(clip, binding))
                            if (key.value is Material material) materials.Add(material);
                }
            return materials;
        }

        // The textures those materials show, and the menu's icons.
        private static HashSet<Texture> Textures(GameObject root, IEnumerable<Material> materials)
        {
            var textures = new HashSet<Texture>();
            foreach (var material in materials)
                foreach (int id in material.GetTexturePropertyNameIDs())
                {
                    var texture = material.GetTexture(id);
                    if (texture != null) textures.Add(texture);
                }
            textures.UnionWith(MenuIcons.Of(root));
            return textures;
        }

        // ---- Textures ----

        /// <summary>One texture's share of the download, as it stands or as the upload's copy <paramref name="planned"/>.</summary>
        internal static long TextureBytes(Texture texture, bool mobile, TextureOptimization.TexturePlan planned = null) =>
            Texture(texture, mobile, planned, out _);

        // Compressed (measured, else modelled), and as loaded (raw): block data, or the DXT image a crunched texture is decoded into.
        private static long Texture(Texture texture, bool mobile, TextureOptimization.TexturePlan planned, out long raw)
        {
            var (format, crunched, quality) = TextureOptimization.BuildFormat(texture, mobile);
            int width = texture.width, height = texture.height;
            if (planned != null)
            {
                width = planned.width; height = planned.height; format = planned.format; quality = planned.quality;
                crunched = TextureOptimization.Crunched(format);
            }
            bool mips = !(texture is Texture2D t) || t.mipmapCount > 1;
            long measured = Measured(texture, mobile, planned);
            if (measured >= 0)
            {
                raw = TextureOptimization.FormatBytes(TextureOptimization.Decoded(format), width, height, mips);
                return measured;
            }
            float entropy = Entropy(texture, width);
            if (texture is Cubemap)
            {
                raw = TextureOptimization.FormatBytes(format, width, height, mips) * 6;
                return (long)(raw * Ratio(TextureFormat.RGBA32, entropy));
            }
            if (crunched)
            {
                // Crunch stores a DXT or ETC image in 1-2 bits per pixel, more at higher quality; it doesn't compress any further.
                var block = TextureOptimization.Decoded(format);
                raw = TextureOptimization.FormatBytes(block, width, height, mips);
                return (long)(raw * Mathf.Min(.15f + .35f * Mathf.Clamp01(quality / 100f), Ratio(block, entropy)));
            }
            raw = TextureOptimization.FormatBytes(format, width, height, mips);
            return (long)(raw * Ratio(format, entropy));
        }

        // Compressed over stored size of block data, from how compressible the image is (its PNG size over its pixels).
        private static float Ratio(TextureFormat format, float entropy)
        {
            switch (format)
            {
                case TextureFormat.DXT1: case TextureFormat.DXT5: case TextureFormat.BC4:
                    return Mathf.Clamp(3f * entropy, .005f, .65f);
                case TextureFormat.BC5:
                    return Mathf.Clamp(.1f + .85f * entropy, .05f, .85f);
                case TextureFormat.BC7: case TextureFormat.BC6H:
                case TextureFormat.ASTC_4x4: case TextureFormat.ASTC_5x5: case TextureFormat.ASTC_6x6: case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10: case TextureFormat.ASTC_12x12:
                    return Mathf.Clamp(.01f + 1.8f * entropy, .01f, .9f);
                default:
                    return Mathf.Clamp(.8f * entropy, .005f, .9f);
            }
        }

        // The source image's compressed size over its pixels (a JPEG hides more than it keeps), denser per pixel once the
        // image is made smaller.
        private static float Entropy(Texture texture, int width)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return UnknownEntropy;
            string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool jpeg = extension == ".jpg" || extension == ".jpeg";
            if (extension != ".png" && !jpeg) return UnknownEntropy;
            importer.GetSourceTextureWidthAndHeight(out int sourceWidth, out int sourceHeight);
            var file = new System.IO.FileInfo(path);
            if (sourceWidth <= 0 || sourceHeight <= 0 || !file.Exists) return UnknownEntropy;
            float pixels = (float)sourceWidth * sourceHeight * (importer.DoesSourceTextureHaveAlpha() ? 4 : 3);
            float entropy = file.Length / pixels * (jpeg ? 2.5f : 1f);
            float shrink = width > 0 ? Mathf.Max(1f, sourceWidth / (float)width) : 1f;
            return Mathf.Clamp01(entropy * Mathf.Sqrt(shrink) / 1.4f);
        }

        // ---- Measured textures ----

        // Deflate's size of each texture's stored data, by its image and how it is stored: kept in Library, measured once.
        private const string MeasuredFile = "texture-downloads.json";
        private const int MeasuredVersion = 2;
        private static Dictionary<string, long> measured;
        // Textures made in memory (a build step's copies), by instance: those destroyed since leave it.
        private static readonly Dictionary<int, long> MeasuredInMemory = new Dictionary<int, long>();

        /// <summary>Reads the measures from <see cref="LibraryStore"/> again (tests point it at a folder of their own).</summary>
        internal static void ForgetCaches()
        {
            SaveMeasures();
            measured = null;
            MeasuredInMemory.Clear();
        }

        // Measures not written yet: written once a job's textures are all measured.
        private static bool unsaved;

        /// <summary>Writes the measures taken since the last write.</summary>
        internal static void SaveMeasures()
        {
            if (!unsaved || measured == null) return;
            unsaved = false;
            LibraryStore.Write(MeasuredFile, measured.Skip(Math.Max(0, measured.Count - 4096)).ToDictionary(p => p.Key, p => p.Value));
        }

        // What the bundle stores for a texture: the upload's copy, or the imported image in its build format and size.
        private static TextureOptimization.TexturePlan Stored(Texture texture, bool mobile, TextureOptimization.TexturePlan planned)
        {
            if (planned != null) return planned;
            if (!(texture is Texture2D image) || !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(image)) is TextureImporter importer)) return null;
            var (format, crunched, quality) = TextureOptimization.BuildFormat(image, mobile);
            var size = TextureOptimization.BuildSize(image, importer, mobile);
            bool normal = importer.textureType == TextureImporterType.NormalMap;
            return new TextureOptimization.TexturePlan
            {
                texture = image, path = importer.assetPath, format = format, width = size.x, height = size.y,
                quality = crunched ? quality : TextureOptimization.EncodeQuality, linear = normal || !importer.sRGBTexture, normal = normal,
            };
        }

        private static string MeasuredKey(TextureOptimization.TexturePlan stored) =>
            MeasuredVersion + "|" + stored.texture.imageContentsHash + "|" + stored.format + "|" + stored.width + "x" + stored.height + "|" + stored.quality;

        // The measured download of a texture, or -1 until it is measured. A texture made in memory (a build step's copy) is
        // measured at once: it holds its stored data already.
        private static long Measured(Texture texture, bool mobile, TextureOptimization.TexturePlan planned)
        {
            if (planned == null && texture is Texture2D own && !EditorUtility.IsPersistent(own))
            {
                if (!own.isReadable) return -1;
                if (!MeasuredInMemory.TryGetValue(own.GetInstanceID(), out long bytes))
                {
                    if (MeasuredInMemory.Count >= 256)
                        foreach (int gone in MeasuredInMemory.Keys.Where(k => EditorUtility.InstanceIDToObject(k) == null).ToList()) MeasuredInMemory.Remove(gone);
                    MeasuredInMemory[own.GetInstanceID()] = bytes = Deflated(own.GetRawTextureData());
                }
                return Lzma(bytes, own.format, own.width, own.height);
            }
            var stored = Stored(texture, mobile, planned);
            if (stored == null) return -1;
            measured ??= LibraryStore.Read<Dictionary<string, long>>(MeasuredFile);
            return measured.TryGetValue(MeasuredKey(stored), out long deflated) ? Lzma(deflated, stored.format, stored.width, stored.height) : -1;
        }

        /// <summary>
        /// The textures of <paramref name="root"/> whose download isn't measured yet for the platform, as they stand and as
        /// the upload's copies <paramref name="plan"/> makes.
        /// </summary>
        internal static List<(Texture Texture, TextureOptimization.TexturePlan Planned)> Unmeasured(GameObject root, bool mobile, TextureOptimization.Plan plan)
        {
            var result = new List<(Texture, TextureOptimization.TexturePlan)>();
            foreach (var texture in Textures(root, Materials(root, new HashSet<AnimationClip>())))
            {
                if (!EditorUtility.IsPersistent(texture)) continue;
                if (Stored(texture, mobile, null) != null && Measured(texture, mobile, null) < 0) result.Add((texture, null));
                var change = plan?.For(texture);
                if (change != null && Measured(texture, mobile, change) < 0) result.Add((texture, change));
            }
            return result;
        }

        /// <summary>
        /// Starts measuring a texture's download as the bundle stores it: its imported data when the editor holds it in the
        /// build's format and size, else a copy made as the upload makes them (read here, on the editor's thread). The
        /// upload's own copies are kept in Library (the upload reuses them); a copy of a texture the upload keeps as it is
        /// is only measured. Its compression runs on another thread: <see cref="Record"/> keeps the result. Null when there
        /// is nothing to measure.
        /// </summary>
        internal static (string Key, System.Threading.Tasks.Task<long> Deflate)? StartMeasure(Texture texture, bool mobile, TextureOptimization.TexturePlan planned)
        {
            var stored = Stored(texture, mobile, planned);
            if (stored == null) return null;
            measured ??= LibraryStore.Read<Dictionary<string, long>>(MeasuredFile);
            string key = MeasuredKey(stored);
            if (measured.ContainsKey(key)) return null;
            var image = stored.texture;
            var data = image.format == stored.format && image.width == stored.width && image.height == stored.height ? image.GetRawTextureData()
                : UploadTextures.Data(stored, cache: planned != null);
            // The editor can't give the copy (it holds the texture smaller): estimated, not measured.
            if (data == null) return null;
            return (key, System.Threading.Tasks.Task.Run(() => Deflated(data)));
        }

        /// <summary>Keeps a measure <see cref="StartMeasure"/> took (on the editor's thread); written with <see cref="SaveMeasures"/>.</summary>
        internal static void Record(string key, long deflated)
        {
            measured ??= LibraryStore.Read<Dictionary<string, long>>(MeasuredFile);
            measured[key] = deflated;
            unsaved = true;
        }

        private static long Deflated(byte[] data)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                using (var deflate = new System.IO.Compression.DeflateStream(stream, System.IO.Compression.CompressionLevel.Optimal, true)) deflate.Write(data, 0, data.Length);
                return stream.Length;
            }
        }

        // What LZMA makes of data Deflate compressed to deflated bytes, measured on MasculineCanine's textures and copies of
        // them, one bundle each (2026-10-12; within 5% each, 0.2% on the whole): LZMA's larger window finds more in a large
        // texture (per mip level above 512 px, a few percent more); crunched data holds nothing more to find; uncompressed
        // gradients compress far better.
        private static long Lzma(long deflated, TextureFormat format, int width, int height)
        {
            float levels = Mathf.Log(Mathf.Max(1f, (float)width * height) / (512f * 512f), 2f) / 2f;
            float ratio;
            switch (format)
            {
                case TextureFormat.DXT1Crunched: case TextureFormat.DXT5Crunched: case TextureFormat.ETC_RGB4Crunched: case TextureFormat.ETC2_RGBA8Crunched:
                    ratio = 1f; break;
                case TextureFormat.DXT1: case TextureFormat.DXT5: case TextureFormat.BC4:
                    ratio = Mathf.Clamp(.8f - .05f * levels, .62f, .9f); break;
                case TextureFormat.BC5:
                    ratio = Mathf.Clamp(.925f - .07f * levels, .7f, .95f); break;
                case TextureFormat.BC7: case TextureFormat.BC6H:
                    ratio = Mathf.Clamp(.855f - .05f * levels, .7f, .95f); break;
                case TextureFormat.ASTC_4x4: case TextureFormat.ASTC_5x5: case TextureFormat.ASTC_6x6: case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10: case TextureFormat.ASTC_12x12:
                    ratio = Mathf.Clamp(.93f - .04f * levels, .75f, .98f); break;
                default:
                    ratio = .42f; break;
            }
            return (long)(deflated * ratio);
        }

        // ---- Meshes ----

        // Offsets per blendshape, counted once per mesh asset: a sculpted body holds hundreds of shapes.
        private static readonly Dictionary<int, (int Vertices, Dictionary<string, long> Shapes)> ShapeCache = new Dictionary<int, (int, Dictionary<string, long>)>();

        /// <summary>A mesh's stored vertices, triangles and bind poses.</summary>
        internal static long VertexBytes(Mesh mesh)
        {
            long vertices = 0;
            for (int stream = 0; stream < mesh.vertexBufferCount; stream++) vertices += (long)mesh.vertexCount * mesh.GetVertexBufferStride(stream);
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += (long)mesh.GetIndexCount(i);
            return vertices + indices * (mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4 : 2) + mesh.bindposes.Length * 64L;
        }

        /// <summary>Each blendshape's stored offsets: only the vertices a frame moves are kept.</summary>
        internal static Dictionary<string, long> ShapeBytes(Mesh mesh)
        {
            var result = new Dictionary<string, long>();
            if (mesh.blendShapeCount == 0) return result;
            int id = mesh.GetInstanceID();
            if (ShapeCache.TryGetValue(id, out var known) && known.Vertices == mesh.vertexCount && known.Shapes.Count == mesh.blendShapeCount) return known.Shapes;
            // Meshes unloaded since leave it.
            if (ShapeCache.Count >= 256)
                foreach (int gone in ShapeCache.Keys.Where(k => EditorUtility.InstanceIDToObject(k) == null).ToList()) ShapeCache.Remove(gone);
            int n = mesh.vertexCount;
            var dv = new Vector3[n]; var dn = new Vector3[n]; var dt = new Vector3[n];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                long bytes = 0;
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    mesh.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);
                    long moved = 0;
                    for (int v = 0; v < n; v++) if (dv[v].sqrMagnitude > 0f || dn[v].sqrMagnitude > 0f || dt[v].sqrMagnitude > 0f) moved++;
                    // Position, normal and tangent offsets and the vertex index; the frame's name and weight.
                    bytes += moved * 40 + 64;
                }
                result[mesh.GetBlendShapeName(shape)] = bytes;
            }
            if (EditorUtility.IsPersistent(mesh)) ShapeCache[id] = (mesh.vertexCount, result);
            return result;
        }

        // ---- Animations and sounds ----

        private static long ClipBytes(AnimationClip clip)
        {
            long bytes = 256;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                bytes += 48 + (curve != null ? curve.length * 16L : 0);
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                bytes += 48 + AnimationUtility.GetObjectReferenceCurve(clip, binding).Length * 8L;
            return bytes;
        }

        // Vorbis at its quality, or the raw samples.
        private static long Sound(AudioClip clip)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as AudioImporter;
            float seconds = clip.length;
            if (importer == null) return (long)(seconds * 16000);
            var settings = importer.defaultSampleSettings;
            if (settings.compressionFormat == AudioCompressionFormat.PCM) return (long)(seconds * clip.frequency * clip.channels * 2);
            if (settings.compressionFormat == AudioCompressionFormat.ADPCM) return (long)(seconds * clip.frequency * clip.channels / 2f);
            // Vorbis: about 64 kbit/s per channel at quality 0, 320 at 1.
            return (long)(seconds * clip.channels * (64000 + 256000 * settings.quality) / 8f);
        }

        // Written as VRChat writes sizes, whatever the system's language: "17.7 MB".
        internal static string Megabytes(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 * 1048576L ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";

        /// <summary>Megabytes, or kilobytes under a tenth of one (a small texture's saving).</summary>
        internal static string Amount(long bytes) => Math.Abs(bytes) < 104858 ? Mathf.RoundToInt(bytes / 1024f).ToString(CultureInfo.InvariantCulture) + " KB" : Megabytes(bytes);
    }
}
