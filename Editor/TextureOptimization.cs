using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Orbiters.MyAvatar.Editor
{
    // What the upload does to the avatar's textures: each one its materials show is sized to stay sharp from the
    // Optimization level's distance (from its measured density on the meshes, see TextureDensity), never larger than it is
    // imported, in the format with the best quality per byte of texture memory for the platform the project builds for.
    // The textures themselves never change: the upload gets copies (UploadTextures).
    internal static class TextureOptimization
    {
        // Sizes at one meter for textures whose density can't be measured (no mesh, no UVs): the body carries most of what
        // people look at.
        internal const int MaxSize = 512;
        internal const int BodyMaxSize = 2048;
        internal const string Platform = "Standalone", Android = "Android";

        /// <summary>One texture the upload changes: the copy it gets instead.</summary>
        internal sealed class TexturePlan
        {
            public Texture2D texture;
            public string path;
            /// <summary>The copy's format, size and (crunched) quality.</summary>
            public TextureFormat format;
            public int width, height, quality = 100;
            /// <summary>Texture memory in the build before and after.</summary>
            public long before, after;
            /// <summary>Whether the image holds data rather than colour (normal maps, masks): read and written linear.</summary>
            public bool linear;
        }

        internal sealed class Plan
        {
            public readonly List<TexturePlan> changes = new List<TexturePlan>();
            /// <summary>Texture memory of every texture the materials show, before and after.</summary>
            public long before, after;
            /// <summary>The distance textures are sized to stay sharp from, in meters.</summary>
            public float sharpFrom;
            /// <summary>For a Quest (Android) build.</summary>
            public bool mobile;

            /// <summary>The change for a texture, or null.</summary>
            public TexturePlan For(Texture texture) => texture ? changes.FirstOrDefault(c => c.texture == texture) : null;
        }

        // ---- Rules --------------------------------------------------------------------------------------------

        // Ramps, lookup tables and gradients are read as data: resizing or block compression bands them. HDR sources lose range.
        private static readonly HashSet<string> DataWords = new HashSet<string> { "ramp", "lut", "lookup", "gradient", "gradation", "sdf" };

        internal static string Exclusion(string path, string name, string slot, int width, int height)
        {
            string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
            if (extension == ".exr" || extension == ".hdr") return "HDR image";
            int small = Mathf.Min(width, height);
            if (small <= 16 || Mathf.Max(width, height) >= 8 * small) return "ramp or lookup strip";
            if (TextureMatching.Tokens(name + " " + slot, stem: false).Any(DataWords.Contains)) return "ramp, lookup or gradient data";
            return null;
        }

        // Only Default and Normal map importers hold plain images; sprites, cookies, lightmaps and single-channel data stay.
        internal static bool Compressible(Texture texture, out TextureImporter importer)
        {
            importer = texture is Texture2D ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter : null;
            return importer != null && (importer.textureType == TextureImporterType.Default || importer.textureType == TextureImporterType.NormalMap) &&
                Exclusion(importer.assetPath, "", "", 512, 512) == null;
        }

        // BC5 keeps both normal channels at full precision; BC1 is half the size of BC7 when alpha is unused.
        internal static TextureFormat Format(bool normalMap, bool alphaUsed) =>
            normalMap ? TextureFormat.BC5 : alphaUsed ? TextureFormat.BC7 : TextureFormat.DXT1;

        /// <summary>The runtime format an importer format produces.</summary>
        internal static TextureFormat RuntimeFormat(TextureImporterFormat format) =>
            Enum.IsDefined(typeof(TextureFormat), (int)format) ? (TextureFormat)(int)format :
            format == TextureImporterFormat.BC5 ? TextureFormat.BC5 : format == TextureImporterFormat.BC7 ? TextureFormat.BC7 : TextureFormat.DXT1;

        /// <summary>The crunch quality Big crunches textures with: Unity's default leaves visible blocks on gradients.</summary>
        internal const int CrunchQuality = 75;

        // Crunch shrinks only the download: memory holds the same DXT1/DXT5 blocks (crunch has no BC5 or BC7: alpha keeps DXT5,
        // the same 8 bits per pixel).
        internal static TextureFormat Crunch(TextureFormat format) =>
            format == TextureFormat.DXT1 || format == TextureFormat.DXT1Crunched ? TextureFormat.DXT1Crunched : TextureFormat.DXT5Crunched;

        internal static bool Crunched(TextureFormat format) =>
            format == TextureFormat.DXT1Crunched || format == TextureFormat.DXT5Crunched || format == TextureFormat.ETC_RGB4Crunched || format == TextureFormat.ETC2_RGBA8Crunched;

        // Unity lowers an oversized texture by whole mip levels; smaller textures are never scaled up.
        internal static Vector2Int Capped(int width, int height, int max)
        {
            while (Mathf.Max(width, height) > max) { width = Mathf.Max(1, width / 2); height = Mathf.Max(1, height / 2); }
            return new Vector2Int(width, height);
        }

        internal static int BitsPerPixel(GraphicsFormat format)
        {
            if (format == GraphicsFormat.None) return 32;
            return (int)(GraphicsFormatUtility.GetBlockSize(format) * 8 / (GraphicsFormatUtility.GetBlockWidth(format) * GraphicsFormatUtility.GetBlockHeight(format)));
        }

        internal static int BitsPerPixel(TextureFormat format) => BitsPerPixel(GraphicsFormatUtility.GetGraphicsFormat(format, false));

        // Estimated VRAM, as VRChat ranks texture memory: bits per pixel × pixels, plus a third for the mip chain.
        internal static long Bytes(int width, int height, int bitsPerPixel, bool mips)
        {
            long bytes = (long)width * height * bitsPerPixel / 8;
            return mips ? bytes * 4 / 3 : bytes;
        }

        private static long Bytes(Texture texture) => Bytes(texture.width, texture.height, BitsPerPixel(texture.graphicsFormat),
            texture is Texture2D t ? t.mipmapCount > 1 : texture is RenderTexture r && r.useMipMap);

        /// <summary>The bytes of a texture of this size and format with its mip chain (block formats counted by whole blocks).</summary>
        internal static long FormatBytes(TextureFormat format, int width, int height, bool mips)
        {
            var graphics = GraphicsFormatUtility.GetGraphicsFormat(format, false);
            if (graphics == GraphicsFormat.None) return Bytes(width, height, 32, mips);
            long blockWidth = GraphicsFormatUtility.GetBlockWidth(graphics), blockHeight = GraphicsFormatUtility.GetBlockHeight(graphics);
            long bytes = (width + blockWidth - 1) / blockWidth * ((height + blockHeight - 1) / blockHeight) * GraphicsFormatUtility.GetBlockSize(graphics);
            return mips ? bytes * 4 / 3 : bytes;
        }

        // ---- Alpha --------------------------------------------------------------------------------------------

        // Alpha is measured on the imported pixels: many opaque textures still carry an unused alpha channel. Any texel below
        // full opacity counts: the image is halved down to 512 px at most, each step averaging 2×2 texels, so a small cut-out
        // or near-opaque alpha still shows; when mipmap streaming holds only smaller mips, alpha is kept to be safe.
        // Keyed by the imported contents' hash and kept in Library: measuring reads the image back from the GPU, too slow
        // to repeat for each texture after every script reload.
        private const string AlphaCacheFile = "alpha-cache.json";
        private static Dictionary<string, bool> alphaCache;

        internal static Dictionary<Texture2D, bool> AlphaUsed(IEnumerable<(Texture2D texture, TextureImporter importer)> textures)
        {
            var result = new Dictionary<Texture2D, bool>();
            alphaCache ??= LibraryStore.Read<Dictionary<string, bool>>(AlphaCacheFile);
            bool measured = false;
            foreach (var (texture, importer) in textures)
            {
                string key = texture.imageContentsHash.ToString();
                if (importer.alphaSource == TextureImporterAlphaSource.None || importer.alphaSource == TextureImporterAlphaSource.FromInput && !importer.DoesSourceTextureHaveAlpha() ||
                    texture.format == TextureFormat.DXT1 || texture.format == TextureFormat.DXT1Crunched || !GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat)) result[texture] = false;
                else if (alphaCache.TryGetValue(key, out bool used)) result[texture] = used;
                else if (QualitySettings.streamingMipmapsActive && texture.streamingMipmaps && texture.loadedMipmapLevel > 0) result[texture] = true;
                else { result[texture] = alphaCache[key] = TextureAnalysis.UsesAlpha(texture); measured = true; }
            }
            if (measured) SaveAlpha();
            return result;
        }

        private static void SaveAlpha() => LibraryStore.Write(AlphaCacheFile, alphaCache.Skip(Math.Max(0, alphaCache.Count - 4096)).ToDictionary(p => p.Key, p => p.Value));

        // ---- Plan ---------------------------------------------------------------------------------------------

        // Quest headsets have the same pixels for a quarter of the texture memory: half the size, at most 1024 px, in ASTC,
        // 6x6 blocks for colour and normal maps (3.6 bits a pixel), 8x8 for masks and other linear data (2 bits).
        private const int MobileMaxSize = 1024, MobileMinSize = 64;

        /// <summary>The plan for the platform the project builds for (Quest when Android or iOS).</summary>
        internal static Plan Build(MyAvatar avatar) => Build(avatar, Mobile);

        /// <summary>Whether the project builds for Quest (Android) or iPhone.</summary>
        internal static bool Mobile => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;

        /// <summary>The plan for a PC or a Quest (Android) upload.</summary>
        internal static Plan Build(MyAvatar avatar, bool mobile)
        {
            var settings = avatar.uploadOptimization;
            var passes = settings.Passes;
            var plan = new Plan { sharpFrom = SharpFrom(avatar), mobile = mobile };
            bool resize = (passes & OptimizationPasses.ResizeTextures) != 0, crunch = (passes & OptimizationPasses.CrunchTextures) != 0;
            bool half = (passes & OptimizationPasses.HalfSurfaceMaps) != 0, quest = mobile && (passes & OptimizationPasses.QuestTextures) != 0;
            var candidates = new List<(Texture2D texture, TextureImporter importer, bool view, bool surface)>();
            var slots = TextureMatching.Slots(avatar);
            var bodyTextures = BodyTextures(avatar, slots);
            var meters = resize ? TextureDensity.MetersPerTexture(avatar, slots) : new Dictionary<Texture, float>();
            foreach (var group in slots.Where(s => s.existing).GroupBy(s => s.existing))
            {
                var texture = group.Key;
                long bytes = BuildBytes(texture, mobile);
                plan.before += bytes; plan.after += bytes;
                if (passes == OptimizationPasses.None || !Compressible(texture, out var importer) ||
                    group.Any(s => Exclusion(importer.assetPath, texture.name, s.property + " " + s.description, texture.width, texture.height) != null)) continue;
                // Metallic and roughness maps, by the slots showing them or, for linear data, by name.
                bool surface = group.All(s => TextureDensity.SurfaceMap(s.property + " " + s.description)) || !importer.sRGBTexture && TextureDensity.SurfaceMap(texture.name);
                // A texture read by direction (a matcap) everywhere it is used has no surface density.
                candidates.Add(((Texture2D)texture, importer, group.All(s => TextureDensity.ViewMapped(s.property + " " + s.description)), surface));
            }
            var alpha = AlphaUsed(candidates.Where(c => c.importer.textureType != TextureImporterType.NormalMap).Select(c => (c.texture, c.importer)));
            foreach (var (texture, importer, view, surface) in candidates)
            {
                bool normal = importer.textureType == TextureImporterType.NormalMap;
                var (current, currentlyCrunched, currentQuality) = BuildFormat(texture, mobile);
                var size = BuildSize(texture, importer, mobile);
                int cap = int.MaxValue;
                if (resize)
                {
                    cap = TextureDensity.Cap(meters.TryGetValue(texture, out float m) ? m : (float?)null, bodyTextures.Contains(texture), view, plan.sharpFrom);
                    if (half && surface) cap = Mathf.Max(64, cap / 2);
                }
                TextureFormat format;
                int quality = 100;
                if (mobile)
                {
                    format = current;
                    if (quest)
                    {
                        cap = Mathf.Clamp((cap == int.MaxValue ? size.x : cap) / 2, MobileMinSize, view ? 256 : MobileMaxSize);
                        format = !normal && !importer.sRGBTexture ? TextureFormat.ASTC_8x8 : TextureFormat.ASTC_6x6;
                    }
                }
                else
                {
                    // A normal map keeps how its channels are laid out (BC5 or DXT5nm); a crunched texture stays crunched.
                    format = normal ? (current == TextureFormat.DXT5 || current == TextureFormat.BC5 ? current : TextureFormat.BC5) : Format(false, alpha[texture]);
                    if (!normal && (crunch || currentlyCrunched))
                    {
                        format = Crunch(format);
                        quality = currentlyCrunched ? currentQuality : CrunchQuality;
                    }
                }
                // Only ever smaller: never larger than the texture is imported for the platform.
                var target = Capped(size.x, size.y, cap);
                long before = BuildBytes(texture, mobile), after = FormatBytes(format, target.x, target.y, true);
                bool change = target != size || format != current || Crunched(format) && quality != currentQuality;
                // Nothing that takes more memory (turning mipmaps on can outweigh the saving on a small one).
                if (!change || after > before) continue;
                plan.changes.Add(new TexturePlan { texture = texture, path = importer.assetPath, format = format, width = target.x, height = target.y,
                    quality = quality, before = before, after = after, linear = normal || !importer.sRGBTexture });
                plan.after += after - before;
            }
            return plan;
        }

        // The size the texture is imported at for the platform: its source, scaled to a power of two unless that is turned
        // off, capped by the platform's max size.
        internal static Vector2Int BuildSize(Texture texture, TextureImporter importer, bool mobile)
        {
            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            if (width <= 0 || height <= 0) return new Vector2Int(texture.width, texture.height);
            int Scale(int v) => importer.npotScale == TextureImporterNPOTScale.None || Mathf.IsPowerOfTwo(v) ? v :
                importer.npotScale == TextureImporterNPOTScale.ToLarger ? Mathf.NextPowerOfTwo(v) :
                importer.npotScale == TextureImporterNPOTScale.ToSmaller ? Mathf.NextPowerOfTwo(v) / 2 : Mathf.ClosestPowerOfTwo(v);
            var settings = importer.GetPlatformTextureSettings(mobile ? Android : Platform);
            return Capped(Scale(width), Scale(height), settings.overridden ? settings.maxTextureSize : importer.maxTextureSize);
        }

        // The body's and the face's textures, on every material of those meshes (the face often has a head mesh of its own):
        // what people look at most keeps more when its density can't be measured.
        private static HashSet<Texture> BodyTextures(MyAvatar avatar, List<TextureSlot> slots)
        {
            var root = AvatarRoot(avatar).transform;
            var meshes = new HashSet<Renderer>(new Renderer[] { Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentPlanner.Body(root), FaceTracking.FaceTrackingDetection.Face(root) }.Where(r => r));
            return new HashSet<Texture>(slots.Where(s => s.existing && s.parts.Any(p => meshes.Contains(p.renderer))).Select(s => s.existing));
        }

        // ---- Changes ------------------------------------------------------------------------------------------

        private static int imports;

        internal sealed class ImportWatcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) => imports++;
        }

        /// <summary>The distance textures stay sharp from at upload, in meters: the level's, or the slider's under Custom.</summary>
        internal static float SharpFrom(MyAvatar avatar) => TextureDensity.Clamp(avatar.uploadOptimization.SharpFrom);

        /// <summary>Changes with the avatar's renderers, their meshes and materials, and any imported asset.</summary>
        internal static string ChangeKey(MyAvatar avatar)
        {
            var key = new System.Text.StringBuilder().Append(imports);
            foreach (var renderer in AvatarRoot(avatar).GetComponentsInChildren<Renderer>(true))
            {
                key.Append('|').Append(renderer.GetInstanceID());
                if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh) key.Append('#').Append(skinned.sharedMesh.GetInstanceID());
                foreach (var material in renderer.sharedMaterials)
                    key.Append(',').Append(material ? material.GetInstanceID() + "." + EditorUtility.GetDirtyCount(material) : "-");
            }
            return key.ToString();
        }

        // ---- Formats and memory in a build ----------------------------------------------------------------------

        /// <summary>Texture memory in a PC or Quest (Android) build, from the import settings.</summary>
        internal static long BuildBytes(Texture texture, bool mobile = false)
        {
            if (!(texture is Texture2D) || !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)) return Bytes(texture);
            var size = BuildSize(texture, importer, mobile);
            return FormatBytes(BuildFormat(texture, mobile).format, size.x, size.y, importer.mipmapEnabled);
        }

        /// <summary>
        /// The format a texture has in a PC or a Quest (Android) build, from its import settings: an explicit format for
        /// the platform, else what Unity picks for its compression setting (the editor may hold it uncompressed meanwhile).
        /// Not an imported image (a render texture, a copy made for an upload): what the editor holds.
        /// </summary>
        internal static (TextureFormat format, bool crunched, int quality) BuildFormat(Texture texture, bool mobile = false)
        {
            if (!(texture is Texture2D) || !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer))
                return texture is Texture2D own ? (own.format, Crunched(own.format), 100) : (TextureFormat.RGBA32, false, 100);
            var settings = importer.GetPlatformTextureSettings(mobile ? Android : Platform);
            if (settings.overridden && settings.format != TextureImporterFormat.Automatic)
            {
                var explicitFormat = RuntimeFormat(settings.format);
                return (explicitFormat, Crunched(explicitFormat), settings.compressionQuality);
            }
            var compression = settings.overridden ? settings.textureCompression : importer.textureCompression;
            bool crunch = (settings.overridden ? settings.crunchedCompression : importer.crunchedCompression) && compression != TextureImporterCompression.Uncompressed;
            int quality = settings.overridden ? settings.compressionQuality : importer.compressionQuality;
            bool alpha = importer.alphaSource != TextureImporterAlphaSource.None && (importer.alphaSource != TextureImporterAlphaSource.FromInput || importer.DoesSourceTextureHaveAlpha());
            if (compression == TextureImporterCompression.Uncompressed) return (alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, 100);
            if (mobile)
                return (compression == TextureImporterCompression.CompressedHQ ? TextureFormat.ASTC_4x4 :
                        compression == TextureImporterCompression.CompressedLQ ? TextureFormat.ASTC_8x8 : TextureFormat.ASTC_6x6, false, 100);
            if (crunch) return (alpha ? TextureFormat.DXT5Crunched : TextureFormat.DXT1Crunched, true, quality);
            if (compression == TextureImporterCompression.CompressedHQ) return (TextureFormat.BC7, false, 100);
            if (importer.textureType == TextureImporterType.NormalMap) return (TextureFormat.DXT5, false, 100);
            return (alpha ? TextureFormat.DXT5 : TextureFormat.DXT1, false, 100);
        }

        // ---- d4rkAvatarOptimizer ------------------------------------------------------------------------------

        internal const string OptimizerUrl = "https://github.com/d4rkc0d3r/d4rkAvatarOptimizer";
        private static Type optimizerType;
        private static bool optimizerSearched;

        // Found by name: My Avatar does not reference the optimizer's assembly.
        internal static Type OptimizerType
        {
            get
            {
                if (!optimizerSearched) { optimizerSearched = true; optimizerType = TypeCache.GetTypesDerivedFrom<MonoBehaviour>().FirstOrDefault(t => t.Name == "d4rkAvatarOptimizer"); }
                return optimizerType;
            }
        }

        internal static GameObject AvatarRoot(MyAvatar avatar)
        {
            var descriptor = avatar.GetComponentInParent<VRC.SDKBase.VRC_AvatarDescriptor>(true);
            return descriptor ? descriptor.gameObject : avatar.gameObject;
        }
    }
}
