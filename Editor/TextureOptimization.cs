using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
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
        internal const string Platform = "Standalone", Android = "Android", IPhone = "iPhone";

        /// <summary>The passes that change textures: with none of them on, every texture uploads as it is, format included.</summary>
        internal const OptimizationPasses TexturePasses = OptimizationPasses.ResizeTextures | OptimizationPasses.CrunchTextures |
            OptimizationPasses.HalfSurfaceMaps | OptimizationPasses.QuestTextures;

        /// <summary>One texture the upload changes: the copy it gets instead.</summary>
        internal sealed class TexturePlan
        {
            public Texture2D texture;
            public string path;
            /// <summary>The copy's format, size and quality (crunch quality for crunched formats, else the encoder's effort).</summary>
            public TextureFormat format;
            public int width, height, quality = EncodeQuality;
            /// <summary>Texture memory in the build before and after.</summary>
            public long before, after;
            /// <summary>Whether the image holds data rather than colour (normal maps, masks): read and written linear.</summary>
            public bool linear;
            /// <summary>A normal map: a BC5 copy takes X from wherever the texture keeps it (DXT5nm keeps it in alpha).</summary>
            public bool normal;
        }

        internal sealed class Plan
        {
            public readonly List<TexturePlan> changes = new List<TexturePlan>();
            /// <summary>Texture memory of every texture the materials show, before and after.</summary>
            public long before, after;
            /// <summary>The distance textures are sized to stay sharp from, in meters.</summary>
            public float sharpFrom;
            /// <summary>For a mobile build (Quest, or iOS when the project builds for it).</summary>
            public bool mobile;

            /// <summary>The change for a texture, or null.</summary>
            public TexturePlan For(Texture texture) => texture ? changes.FirstOrDefault(c => c.texture == texture) : null;
        }

        // ---- Rules --------------------------------------------------------------------------------------------

        // Ramps, lookup tables and gradients are read as data: resizing or block compression bands them. HDR sources lose range.
        private static readonly HashSet<string> DataWords = new HashSet<string> { "ramp", "lut", "lookup", "gradient", "gradation", "sdf" };
        // Glyph and font atlases are read a letter's cell at a time, at coordinates the shader works out (N4N0's panel and
        // screen, avatar text): their mesh says nothing of how sharp they must be, and any shrinking blurs the letters.
        private static readonly HashSet<string> GlyphWords = new HashSet<string> { "glyph", "glyphs", "font", "fonts" };

        internal static string Exclusion(string path, string name, string slot, int width, int height)
        {
            string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
            if (extension == ".exr" || extension == ".hdr") return "HDR image";
            int small = Mathf.Min(width, height);
            if (small <= 16 || Mathf.Max(width, height) >= 8 * small) return "ramp or lookup strip";
            var tokens = TextureMatching.Tokens(name + " " + slot, stem: false).ToList();
            if (tokens.Any(DataWords.Contains)) return "ramp, lookup or gradient data";
            if (tokens.Any(GlyphWords.Contains)) return "glyph or font atlas";
            return null;
        }

        // Only Default and Normal map importers hold plain images; sprites, cookies, lightmaps and single-channel data stay.
        // So do point-filtered textures: pixel data read texel by texel (a lookup table, N4N0's screen layout) or pixel art,
        // which resizing or block compression breaks.
        internal static bool Compressible(Texture texture, out TextureImporter importer)
        {
            importer = texture is Texture2D ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter : null;
            return importer != null && (importer.textureType == TextureImporterType.Default || importer.textureType == TextureImporterType.NormalMap) &&
                importer.filterMode != FilterMode.Point && Exclusion(importer.assetPath, "", "", 512, 512) == null;
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

        /// <summary>
        /// The encoder's effort for the formats that aren't crunched (Unity's Normal): Best takes seconds per BC7 or ASTC
        /// texture on the editor's thread for a difference no one sees.
        /// </summary>
        internal const int EncodeQuality = (int)TextureCompressionQuality.Normal;

        // Crunch shrinks only the download: memory holds the same DXT1/DXT5 blocks (crunch has no BC5 or BC7: alpha keeps DXT5,
        // the same 8 bits per pixel).
        internal static TextureFormat Crunch(TextureFormat format) =>
            format == TextureFormat.DXT1 || format == TextureFormat.DXT1Crunched ? TextureFormat.DXT1Crunched : TextureFormat.DXT5Crunched;

        // Mobile crunch holds ETC blocks: ETC2 when alpha is used.
        internal static TextureFormat MobileCrunch(bool alphaUsed) => alphaUsed ? TextureFormat.ETC2_RGBA8Crunched : TextureFormat.ETC_RGB4Crunched;

        internal static bool Crunched(TextureFormat format) =>
            format == TextureFormat.DXT1Crunched || format == TextureFormat.DXT5Crunched || format == TextureFormat.ETC_RGB4Crunched || format == TextureFormat.ETC2_RGBA8Crunched;

        /// <summary>The blocks a crunched format is decoded into when it loads (what memory holds); any other format as it is.</summary>
        internal static TextureFormat Decoded(TextureFormat format) =>
            format == TextureFormat.DXT1Crunched ? TextureFormat.DXT1 : format == TextureFormat.DXT5Crunched ? TextureFormat.DXT5 :
            format == TextureFormat.ETC_RGB4Crunched ? TextureFormat.ETC_RGB4 : format == TextureFormat.ETC2_RGBA8Crunched ? TextureFormat.ETC2_RGBA8 : format;

        // Unity lowers an oversized texture by whole mip levels; smaller textures are never scaled up.
        internal static Vector2Int Capped(int width, int height, int max)
        {
            while (Mathf.Max(width, height) > max) { width = Mathf.Max(1, width / 2); height = Mathf.Max(1, height / 2); }
            return new Vector2Int(width, height);
        }

        /// <summary>One mip level smaller, unless its larger side would go under <paramref name="min"/>.</summary>
        internal static Vector2Int Half(Vector2Int size, int min) =>
            Mathf.Max(size.x, size.y) / 2 < min ? size : new Vector2Int(Mathf.Max(1, size.x / 2), Mathf.Max(1, size.y / 2));

        internal static int BitsPerPixel(GraphicsFormat format)
        {
            if (format == GraphicsFormat.None) return 32;
            return (int)(GraphicsFormatUtility.GetBlockSize(format) * 8 / (GraphicsFormatUtility.GetBlockWidth(format) * GraphicsFormatUtility.GetBlockHeight(format)));
        }

        internal static int BitsPerPixel(TextureFormat format) => BitsPerPixel(GraphicsFormatUtility.GetGraphicsFormat(Decoded(format), false));

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
            var graphics = GraphicsFormatUtility.GetGraphicsFormat(Decoded(format), false);
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

        /// <summary>Reads the alpha cache from <see cref="LibraryStore"/> again (tests point it at a folder of their own).</summary>
        internal static void ForgetCaches() => alphaCache = null;

        // ---- Platforms ----------------------------------------------------------------------------------------

        /// <summary>The build target the plans follow instead of the project's (tests: no platform switch).</summary>
        internal static BuildTarget? TargetOverride;

        private static BuildTarget Target => TargetOverride ?? EditorUserBuildSettings.activeBuildTarget;

        /// <summary>Whether the project builds for Quest (Android) or iPhone.</summary>
        internal static bool Mobile => Target == BuildTarget.Android || Target == BuildTarget.iOS;

        /// <summary>The importer's platform of a PC or a mobile build: iPhone when the project builds for iOS, else Android (Quest).</summary>
        internal static string PlatformName(bool mobile) => !mobile ? Platform : Target == BuildTarget.iOS ? IPhone : Android;

        // ---- Plan ---------------------------------------------------------------------------------------------

        // Quest headsets have the same pixels for a quarter of the texture memory: half the size, at most 1024 px, in ASTC,
        // 6x6 blocks for colour and normal maps (3.6 bits a pixel), 8x8 for masks and other linear data (2 bits).
        private const int MobileMaxSize = 1024, MobileMinSize = 64, SurfaceMinSize = 64;

        /// <summary>The plan for the platform the project builds for (mobile when Android or iOS).</summary>
        internal static Plan Build(MyAvatar avatar) => Build(avatar, Mobile);

        /// <summary>
        /// What decides a texture's copy for one platform besides the settings: its kind, where it shows, its measured density
        /// and how it is imported for the platform. Read once (<see cref="States"/>); a rank plan's estimates keep it.
        /// </summary>
        internal sealed class TextureState
        {
            /// <summary>False where it uploads as it is whatever the settings (not a plain image, a ramp, a glyph atlas...).</summary>
            public bool Changeable;
            public bool Normal, Linear, View, Surface, Body, AlphaUsed;
            /// <summary>Meters of surface its width covers on its most demanding use; null where that can't be measured.</summary>
            public float? Meters;
            /// <summary>As imported for the platform: its size, format, whether crunched and the crunch quality, and its memory.</summary>
            public int Width, Height, Quality;
            public TextureFormat Format;
            public bool Crunched;
            public long Before;
        }

        /// <summary>
        /// The state of each texture the avatar's materials show (<see cref="TextureMatching.Slots"/>), for a PC or a mobile
        /// upload. Its density is measured with <paramref name="meters"/> (only resizing needs it), whether it uses alpha with
        /// <paramref name="alpha"/> (only a new format does).
        /// </summary>
        internal static Dictionary<Texture, TextureState> States(MyAvatar avatar, bool mobile, bool meters, bool alpha, List<TextureSlot> slots = null)
        {
            slots ??= TextureMatching.Slots(avatar);
            var states = new Dictionary<Texture, TextureState>();
            var candidates = new List<(Texture2D texture, TextureImporter importer)>();
            foreach (var group in slots.Where(s => s.existing).GroupBy(s => s.existing))
            {
                var texture = group.Key;
                var state = states[texture] = new TextureState { Before = BuildBytes(texture, mobile), Width = texture.width, Height = texture.height };
                if (!Compressible(texture, out var importer) ||
                    group.Any(s => Exclusion(importer.assetPath, texture.name, s.property + " " + s.description, texture.width, texture.height) != null))
                    continue;
                state.Changeable = true;
                state.Normal = importer.textureType == TextureImporterType.NormalMap;
                state.Linear = !importer.sRGBTexture;
                // Metallic and roughness maps, by the slots showing them or, for linear data, by name.
                state.Surface = group.All(s => TextureDensity.SurfaceMap(s.property + " " + s.description)) || !importer.sRGBTexture && TextureDensity.SurfaceMap(texture.name);
                // A texture read by direction (a matcap) everywhere it is used has no surface density.
                state.View = group.All(s => TextureDensity.ViewMapped(s.property + " " + s.description));
                (state.Format, state.Crunched, state.Quality) = BuildFormat(texture, mobile);
                var size = BuildSize(texture, importer, mobile);
                state.Width = size.x; state.Height = size.y;
                candidates.Add(((Texture2D)texture, importer));
            }
            if (candidates.Count == 0) return states;
            var bodyTextures = BodyTextures(avatar, slots);
            var density = meters ? TextureDensity.MetersPerTexture(avatar, slots) : new Dictionary<Texture, float>();
            var alphas = alpha ? AlphaUsed(candidates.Where(c => c.importer.textureType != TextureImporterType.NormalMap)) : new Dictionary<Texture2D, bool>();
            foreach (var (texture, _) in candidates)
            {
                var state = states[texture];
                state.Body = bodyTextures.Contains(texture);
                state.Meters = density.TryGetValue(texture, out float m) ? m : (float?)null;
                state.AlphaUsed = !state.Normal && alphas.TryGetValue(texture, out bool used) && used;
            }
            return states;
        }

        /// <summary>
        /// The copy a texture gets under <paramref name="passes"/> (sharp from <paramref name="sharpFrom"/> meters), for a PC or
        /// a mobile upload: its size, format, quality and memory, and whether it changes at all (never into more memory:
        /// turning mipmaps on can outweigh the saving on a small one).
        /// </summary>
        internal static (int Width, int Height, TextureFormat Format, int Quality, long After, bool Change) Copy(TextureState state,
            OptimizationPasses passes, float sharpFrom, bool mobile)
        {
            bool resize = (passes & OptimizationPasses.ResizeTextures) != 0, crunch = (passes & OptimizationPasses.CrunchTextures) != 0;
            bool half = (passes & OptimizationPasses.HalfSurfaceMaps) != 0, quest = mobile && (passes & OptimizationPasses.QuestTextures) != 0;
            // Only the texture passes change textures: under Custom with "Hidden objects" alone, formats stay too.
            if (!state.Changeable || !(resize || crunch || half || quest)) return (state.Width, state.Height, state.Format, state.Quality, state.Before, false);
            var size = new Vector2Int(state.Width, state.Height);
            // Only ever smaller: never larger than the texture is imported for the platform, then halved from there.
            var target = resize ? Capped(size.x, size.y, TextureDensity.Cap(state.Meters, state.Body, state.View, sharpFrom)) : size;
            if (half && state.Surface) target = Half(target, SurfaceMinSize);
            var format = state.Format;
            int quality = state.Crunched ? state.Quality : EncodeQuality;
            if (mobile)
            {
                if (quest)
                {
                    target = Half(target, MobileMinSize);
                    target = Capped(target.x, target.y, state.View ? 256 : MobileMaxSize);
                    format = !state.Normal && state.Linear ? TextureFormat.ASTC_8x8 : TextureFormat.ASTC_6x6;
                    quality = EncodeQuality;
                }
                else if (crunch && !state.Normal && !state.Crunched) { format = MobileCrunch(state.AlphaUsed); quality = CrunchQuality; }
            }
            else if (state.Normal)
                // A normal map keeps how its channels are laid out where it can (BC5, DXT5nm with X in alpha, crunched
                // DXT5nm); any other becomes BC5, its copy taking X from where the texture keeps it.
                format = state.Format == TextureFormat.BC5 || state.Format == TextureFormat.DXT5 || state.Format == TextureFormat.DXT5Crunched ? state.Format : TextureFormat.BC5;
            else
            {
                // Resized: the format with the best quality per byte; crunched: from it. Otherwise the format stays.
                if (resize || crunch && !state.Crunched) format = Format(false, state.AlphaUsed);
                if (crunch || state.Crunched)
                {
                    format = Crunch(format);
                    quality = state.Crunched ? state.Quality : CrunchQuality;
                }
            }
            long after = FormatBytes(format, target.x, target.y, true);
            bool change = (target != size || format != state.Format || Crunched(format) && quality != state.Quality) && after <= state.Before;
            return change ? (target.x, target.y, format, quality, after, true) : (state.Width, state.Height, state.Format, state.Quality, state.Before, false);
        }

        /// <summary>The plan for a PC or a mobile (Quest, or iOS when the project builds for it) upload.</summary>
        internal static Plan Build(MyAvatar avatar, bool mobile)
        {
            var passes = avatar.uploadOptimization.Passes;
            var plan = new Plan { sharpFrom = SharpFrom(avatar), mobile = mobile };
            bool resize = (passes & OptimizationPasses.ResizeTextures) != 0;
            // Only the texture passes change textures: under Custom with "Hidden objects" alone, formats stay too.
            bool measure = resize || (passes & (OptimizationPasses.CrunchTextures | OptimizationPasses.HalfSurfaceMaps)) != 0 ||
                           mobile && (passes & OptimizationPasses.QuestTextures) != 0;
            var slots = TextureMatching.Slots(avatar);
            var states = States(avatar, mobile, resize, measure, slots);
            // Every texture has an entry: its change, or null where it uploads as it is (a duplicate of one of those keeps its size too).
            var entries = new Dictionary<Texture2D, TexturePlan>();
            foreach (var pair in states)
            {
                plan.before += pair.Value.Before; plan.after += pair.Value.Before;
                if (!measure || !(pair.Key is Texture2D texture)) continue;
                var state = pair.Value;
                var (width, height, format, quality, after, change) = Copy(state, passes, plan.sharpFrom, mobile);
                entries[texture] = !change ? null : new TexturePlan
                {
                    texture = texture, path = AssetDatabase.GetAssetPath(texture), format = format, width = width, height = height, quality = quality,
                    before = state.Before, after = after, linear = state.Normal || state.Linear, normal = state.Normal,
                };
            }
            if ((passes & OptimizationPasses.DuplicateTextures) != 0) Duplicates(entries);
            foreach (var change in entries.Values.Where(c => c != null))
            {
                plan.changes.Add(change);
                plan.after += change.after - change.before;
            }
            return plan;
        }

        // The same image imported twice uploads once (MaterialCleanup.MergeDuplicateTextures, after this plan): whichever
        // of them is kept takes the place of the others, so each gets what the most demanding of them needs. One that
        // uploads as it is keeps them all as they are.
        private static void Duplicates(Dictionary<Texture2D, TexturePlan> entries)
        {
            foreach (var group in entries.Keys.GroupBy(t => t.imageContentsHash).Where(g => g.Count() > 1))
            {
                var members = group.ToList();
                if (members.Any(t => entries[t] == null)) { foreach (var t in members) entries[t] = null; continue; }
                var largest = members.Select(t => entries[t]).OrderByDescending(c => (long)c.width * c.height).ThenByDescending(c => c.after).First();
                foreach (var t in members)
                {
                    var own = entries[t];
                    entries[t] = new TexturePlan
                    {
                        texture = own.texture, path = own.path, format = largest.format, width = largest.width, height = largest.height,
                        quality = largest.quality, before = own.before, after = largest.after, linear = largest.linear, normal = largest.normal,
                    };
                }
            }
        }

        // The size the texture is imported at for the platform: its source, scaled to a power of two unless that is turned
        // off, capped by the platform's max size and by Build Settings' override.
        internal static Vector2Int BuildSize(Texture texture, TextureImporter importer, bool mobile)
        {
            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            if (width <= 0 || height <= 0) return new Vector2Int(texture.width, texture.height);
            int Scale(int v) => importer.npotScale == TextureImporterNPOTScale.None || Mathf.IsPowerOfTwo(v) ? v :
                importer.npotScale == TextureImporterNPOTScale.ToLarger ? Mathf.NextPowerOfTwo(v) :
                importer.npotScale == TextureImporterNPOTScale.ToSmaller ? Mathf.NextPowerOfTwo(v) / 2 : Mathf.ClosestPowerOfTwo(v);
            var settings = importer.GetPlatformTextureSettings(PlatformName(mobile));
            int max = settings.overridden ? settings.maxTextureSize : importer.maxTextureSize;
            if (EditorUserBuildSettings.overrideMaxTextureSize > 0) max = Mathf.Min(max, EditorUserBuildSettings.overrideMaxTextureSize);
            return Capped(Scale(width), Scale(height), max);
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

        // Imports that may change what an avatar shows. Code, style sheets and documents never do: counting them made every
        // script save (and the script reload after it) measure every avatar again. A package or SDK update does (its
        // package.json, its assemblies and their definitions: the build and the SDK's ratings may change). Kept across script
        // reloads, like the estimates keyed by it.
        private const string ImportsKey = "Orbiters.MyAvatar.TextureOptimization.Imports";
        private static int? imports;
        private static int Imports => imports ??= SessionState.GetInt(ImportsKey, 0);

        private static readonly HashSet<string> Inert = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".rsp", ".uss", ".uxml", ".tss", ".txt", ".md", ".json", ".xml", ".meta", ".pdb", ".so", ".dylib", ".bytes",
        };

        /// <summary>Whether importing this asset may change what an avatar shows or uploads.</summary>
        internal static bool ImportMatters(string path) =>
            !Inert.Contains(System.IO.Path.GetExtension(path)) || string.Equals(System.IO.Path.GetFileName(path), "package.json", StringComparison.OrdinalIgnoreCase);

        internal sealed class ImportWatcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                bool Matters(string[] paths) => paths.Any(ImportMatters);
                if (!Matters(imported) && !Matters(deleted) && !Matters(moved)) return;
                imports = Imports + 1;
                SessionState.SetInt(ImportsKey, imports.Value);
            }
        }

        /// <summary>The distance textures stay sharp from at upload, in meters: the level's, or the slider's under Custom.</summary>
        internal static float SharpFrom(MyAvatar avatar) => TextureDensity.Clamp(avatar.uploadOptimization.SharpFrom);

        /// <summary>
        /// Changes with the avatar's renderers, the meshes they show, their scale and blendshape weights (the surface a
        /// texture covers), their materials, and any imported asset.
        /// </summary>
        internal static string ChangeKey(MyAvatar avatar)
        {
            var key = new System.Text.StringBuilder().Append(Imports);
            void Scale(Transform transform)
            {
                if (!transform) return;
                var s = transform.lossyScale;
                key.Append('*').Append(s.x.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                    .Append(s.y.ToString("0.####", CultureInfo.InvariantCulture)).Append(',').Append(s.z.ToString("0.####", CultureInfo.InvariantCulture));
            }
            foreach (var renderer in AvatarRoot(avatar).GetComponentsInChildren<Renderer>(true))
            {
                // What editor tools show on the avatar (the bones' X-ray, weight paint) comes and goes: never the avatar's.
                if (EditorHelper(renderer)) continue;
                key.Append('|').Append(renderer.GetInstanceID());
                var mesh = MeshOptimization.MeshOf(renderer);
                // The mesh and its edits (an edit marks it dirty: its vertices and UVs decide the texture density).
                if (mesh) key.Append('#').Append(mesh.GetInstanceID()).Append('.').Append(EditorUtility.GetDirtyCount(mesh));
                Scale(renderer.transform);
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    Scale(skinned.rootBone);
                    if (mesh)
                        for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                        {
                            float weight = skinned.GetBlendShapeWeight(shape);
                            if (Mathf.Abs(weight) > 1e-4f) key.Append('~').Append(shape).Append('=').Append(weight.ToString("0.##", CultureInfo.InvariantCulture));
                        }
                }
                foreach (var material in renderer.sharedMaterials)
                    key.Append(',').Append(material ? material.GetInstanceID() + "." + EditorUtility.GetDirtyCount(material) : "-");
            }
            return key.ToString();
        }

        /// <summary>A component of an object an editor tool made for display on the avatar (hidden, never saved).</summary>
        internal static bool EditorHelper(Component component) =>
            ((component.gameObject.hideFlags | component.hideFlags) & HideFlags.DontSave) != 0;

        // ---- Formats and memory in a build ----------------------------------------------------------------------

        /// <summary>Texture memory in a PC or mobile build, from the import settings.</summary>
        internal static long BuildBytes(Texture texture, bool mobile = false)
        {
            if (!(texture is Texture2D) || !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)) return Bytes(texture);
            var size = BuildSize(texture, importer, mobile);
            return FormatBytes(BuildFormat(texture, mobile).format, size.x, size.y, importer.mipmapEnabled);
        }

        /// <summary>
        /// The format a texture has in a PC or a mobile build, from its import settings: an explicit format for the
        /// platform, else what Unity picks for its compression setting (the editor may hold it uncompressed meanwhile).
        /// Not an imported image (a render texture, a copy made for an upload): what the editor holds.
        /// </summary>
        internal static (TextureFormat format, bool crunched, int quality) BuildFormat(Texture texture, bool mobile = false)
        {
            if (!(texture is Texture2D) || !(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer))
                return texture is Texture2D own ? (own.format, Crunched(own.format), 100) : (TextureFormat.RGBA32, false, 100);
            var settings = importer.GetPlatformTextureSettings(PlatformName(mobile));
            if (settings.overridden && settings.format != TextureImporterFormat.Automatic)
            {
                var explicitFormat = RuntimeFormat(settings.format);
                return (explicitFormat, Crunched(explicitFormat), settings.compressionQuality);
            }
            var compression = settings.overridden ? settings.textureCompression : importer.textureCompression;
            bool crunch = (settings.overridden ? settings.crunchedCompression : importer.crunchedCompression) && compression != TextureImporterCompression.Uncompressed;
            int quality = settings.overridden ? settings.compressionQuality : importer.compressionQuality;
            bool alpha = importer.alphaSource != TextureImporterAlphaSource.None && (importer.alphaSource != TextureImporterAlphaSource.FromInput || importer.DoesSourceTextureHaveAlpha());
            bool normal = importer.textureType == TextureImporterType.NormalMap;
            if (compression == TextureImporterCompression.Uncompressed) return (alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, 100);
            if (mobile)
            {
                // Crunch on Android and iOS holds ETC blocks; otherwise ASTC by the compression's quality.
                if (crunch) return (MobileCrunch(alpha), true, quality);
                return (compression == TextureImporterCompression.CompressedHQ ? TextureFormat.ASTC_4x4 :
                        compression == TextureImporterCompression.CompressedLQ ? TextureFormat.ASTC_8x8 : TextureFormat.ASTC_6x6, false, 100);
            }
            // A normal map is DXT5nm (X in alpha), BC5 at high quality, crunched DXT5nm when crunched.
            if (crunch) return (alpha || normal ? TextureFormat.DXT5Crunched : TextureFormat.DXT1Crunched, true, quality);
            if (normal) return (compression == TextureImporterCompression.CompressedHQ ? TextureFormat.BC5 : TextureFormat.DXT5, false, 100);
            if (compression == TextureImporterCompression.CompressedHQ) return (TextureFormat.BC7, false, 100);
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
