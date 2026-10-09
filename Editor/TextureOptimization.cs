using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor
{
    // Quick optimization: caps every texture the avatar's materials show at 512 px and picks the PC (Standalone) format
    // with the best quality per byte of texture memory. Import settings change in place, unless something besides this
    // avatar also uses the texture (another object in the open scenes, a prefab or scene in Assets) or it lives in a
    // package: then this avatar gets its own copy, through material copies.
    [InitializeOnLoad]
    internal static class TextureOptimization
    {
        internal const int MaxSize = 512;
        // The body carries most of what people look at: its textures keep up to 2048 px.
        internal const int BodyMaxSize = 2048;
        internal const string Platform = "Standalone";
        internal const string OptimizeName = "My Avatar: quick optimization", RevertName = "My Avatar: undo optimization";

        internal sealed class TexturePlan
        {
            public Texture2D texture;
            public string path;
            public TextureImporterFormat format;
            public int maxSize, quality = 100;
            public long before, after;
            public bool duplicate;
        }

        internal sealed class Plan
        {
            public readonly List<TexturePlan> changes = new List<TexturePlan>();
            public long before, after;
        }

        // Unity's Undo restores the record; the importers, which Undo does not track, follow it.
        static TextureOptimization() { Undo.undoRedoEvent += UndoRedone; }

        private static void UndoRedone(in UndoRedoInfo info)
        {
            if (info.undoName != OptimizeName && info.undoName != RevertName) return;
            EditorApplication.delayCall += () => {
                foreach (var avatar in Object.FindObjectsByType<MyAvatar>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (avatar.optimization.textures.Count > 0) Reconcile(avatar.optimization.textures);
            };
        }

        internal static bool Applied(TextureOptimizationRecord record) => record.textures.Any(t => t.applied) || record.swaps.Count > 0;
        internal static int Changed(TextureOptimizationRecord record) => record.textures.Count(t => t.applied) + record.duplicates.Count(d => d);

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

        // Only Default and Normal map importers hold plain images; sprites, cookies, lightmaps and single-channel data keep their settings.
        internal static bool Compressible(Texture texture, out TextureImporter importer)
        {
            importer = texture is Texture2D ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter : null;
            return importer != null && (importer.textureType == TextureImporterType.Default || importer.textureType == TextureImporterType.NormalMap) &&
                Exclusion(importer.assetPath, "", "", 512, 512) == null;
        }

        // BC5 keeps both normal channels at full precision; BC1 is half the size of BC7 when alpha is unused.
        internal static TextureImporterFormat Format(bool normalMap, bool alphaUsed) =>
            normalMap ? TextureImporterFormat.BC5 : alphaUsed ? TextureImporterFormat.BC7 : TextureImporterFormat.DXT1;

        internal static TextureFormat Runtime(TextureImporterFormat format) =>
            format == TextureImporterFormat.BC5 ? TextureFormat.BC5 : format == TextureImporterFormat.BC7 ? TextureFormat.BC7 :
            format == TextureImporterFormat.DXT1Crunched ? TextureFormat.DXT1Crunched : format == TextureImporterFormat.DXT5Crunched ? TextureFormat.DXT5Crunched : TextureFormat.DXT1;

        // Crunch shrinks only the download: memory holds the same DXT1/DXT5 blocks. A crunched texture stays crunched (crunch
        // has no BC5 or BC7, so alpha and normal maps keep DXT5, the same 8 bits per pixel).
        internal static TextureImporterFormat Crunch(TextureImporterFormat format) =>
            format == TextureImporterFormat.DXT1 ? TextureImporterFormat.DXT1Crunched : TextureImporterFormat.DXT5Crunched;

        internal static bool Crunched(TextureImporter importer)
        {
            var s = importer.GetPlatformTextureSettings(Platform);
            if (s.overridden && s.format != TextureImporterFormat.Automatic) return s.format == TextureImporterFormat.DXT1Crunched || s.format == TextureImporterFormat.DXT5Crunched;
            return s.overridden ? s.crunchedCompression && s.textureCompression != TextureImporterCompression.Uncompressed
                : importer.crunchedCompression && importer.textureCompression != TextureImporterCompression.Uncompressed;
        }

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

        // A crunched format keeps the creator's crunch quality.
        internal static ImporterState Target(TextureImporterFormat format, int maxSize, int quality = 100) => new ImporterState {
            overridden = true, maxSize = maxSize, format = (int)format, quality = quality, compression = (int)TextureImporterCompression.CompressedHQ,
            crunched = format == TextureImporterFormat.DXT1Crunched || format == TextureImporterFormat.DXT5Crunched, mipmaps = true, streaming = true };

        // The compression mode is ignored while an explicit format is overridden; without an override only the flags matter.
        internal static bool Same(ImporterState a, ImporterState b) =>
            a.overridden == b.overridden && a.mipmaps == b.mipmaps && a.streaming == b.streaming &&
            (!a.overridden || a.maxSize == b.maxSize && a.format == b.format && a.quality == b.quality && a.crunched == b.crunched);

        internal static ImporterState Read(TextureImporter importer)
        {
            var s = importer.GetPlatformTextureSettings(Platform);
            return new ImporterState { overridden = s.overridden, maxSize = s.maxTextureSize, format = (int)s.format, quality = s.compressionQuality,
                compression = (int)s.textureCompression, crunched = s.crunchedCompression, mipmaps = importer.mipmapEnabled, streaming = importer.streamingMipmaps };
        }

        private static void Write(TextureImporter importer, ImporterState state)
        {
            var s = importer.GetPlatformTextureSettings(Platform);
            s.name = Platform; s.overridden = state.overridden; s.maxTextureSize = state.maxSize; s.format = (TextureImporterFormat)state.format;
            s.compressionQuality = state.quality; s.textureCompression = (TextureImporterCompression)state.compression; s.crunchedCompression = state.crunched;
            importer.SetPlatformTextureSettings(s);
            importer.mipmapEnabled = state.mipmaps; importer.streamingMipmaps = state.streaming;
        }

        // ---- Plan ---------------------------------------------------------------------------------------------

        // Alpha is measured on the imported pixels: many opaque textures still carry an unused alpha channel. Any texel below
        // full opacity counts: the image is halved down to 512 px at most, each step averaging 2×2 texels, so a small cut-out
        // or near-opaque alpha still shows; when mipmap streaming holds only smaller mips, alpha is kept to be safe.
        // Keyed by the imported contents' hash and kept in Library: measuring reads the image back from the GPU, too slow
        // to repeat for each texture after every script reload.
        private const string AlphaCacheFile = "alpha-cache.json";
        private static Dictionary<string, bool> alphaCache;

        // With unmeasured, textures never measured are listed there instead of being measured now.
        internal static Dictionary<Texture2D, bool> AlphaUsed(IEnumerable<(Texture2D texture, TextureImporter importer)> textures, List<Texture2D> unmeasured = null)
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
                else if (unmeasured != null) unmeasured.Add(texture);
                else { result[texture] = alphaCache[key] = TextureAnalysis.UsesAlpha(texture); measured = true; }
            }
            if (measured) SaveAlpha();
            return result;
        }

        private static void SaveAlpha() => LibraryStore.Write(AlphaCacheFile, alphaCache.Skip(Math.Max(0, alphaCache.Count - 4096)).ToDictionary(p => p.Key, p => p.Value));

        private static IEnumerable<Renderer> Renderers(MyAvatar avatar) => avatar.GetComponentsInChildren<Renderer>(true)
            .Where(r => (r.hideFlags & HideFlags.DontSave) == 0 && (r.gameObject.hideFlags & HideFlags.DontSave) == 0);

        internal static Plan Build(MyAvatar avatar) => Build(avatar, null);

        // With unmeasured, textures whose alpha was never measured are listed there and no plan is made.
        private static Plan Build(MyAvatar avatar, List<Texture2D> unmeasured)
        {
            var plan = new Plan();
            var candidates = new List<(Texture2D texture, TextureImporter importer)>();
            var slots = TextureMatching.Slots(avatar);
            var bodyTextures = BodyTextures(avatar, slots);
            foreach (var group in slots.Where(s => s.existing).GroupBy(s => s.existing))
            {
                var texture = group.Key;
                long bytes = BuildBytes(texture);
                plan.before += bytes; plan.after += bytes;
                if (!Compressible(texture, out var importer) ||
                    group.Any(s => Exclusion(importer.assetPath, texture.name, s.property + " " + s.description, texture.width, texture.height) != null)) continue;
                candidates.Add(((Texture2D)texture, importer));
            }
            var alpha = AlphaUsed(candidates.Where(c => c.importer.textureType != TextureImporterType.NormalMap), unmeasured);
            if (unmeasured != null && unmeasured.Count > 0) return null;
            foreach (var (texture, importer) in candidates)
            {
                bool normal = importer.textureType == TextureImporterType.NormalMap, crunched = Crunched(importer);
                var format = Format(normal, !normal && alpha[texture]);
                if (crunched) format = Crunch(format);
                var standalone = importer.GetPlatformTextureSettings(Platform);
                int cap = bodyTextures.Contains(texture) ? BodyMaxSize : MaxSize;
                // From the size Unity imports without a cap, not the current one: a texture capped lower before can go back up.
                var full = Uncapped(importer, texture);
                var size = Capped(full.x, full.y, cap);
                // The smallest max size that keeps that size; a larger one already set (up to the cap) changes nothing.
                int fit = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(size.x, size.y)), 32, 16384);
                int current = standalone.overridden ? standalone.maxTextureSize : importer.maxTextureSize;
                int maxSize = current >= fit && current <= cap ? current : fit;
                int quality = crunched ? standalone.overridden ? standalone.compressionQuality : importer.compressionQuality : 100;
                long before = BuildBytes(texture), after = Bytes(size.x, size.y, BitsPerPixel(Runtime(format)), true);
                // Compared with the import settings, not the loaded texture: with "Compress textures on import" off, the editor
                // keeps textures uncompressed until the build.
                bool change = size.x != texture.width || size.y != texture.height || !Same(Read(importer), Target(format, maxSize, quality));
                // Never grow a texture (turning mipmaps on can outweigh the saving on a small one), except back up from a size
                // this optimization set itself. A crunched texture changes only to take less memory.
                if (!change || after > before && !Ours(avatar.optimization, texture, importer) || crunched && after >= before) continue;
                plan.changes.Add(new TexturePlan { texture = texture, path = importer.assetPath, format = format, before = before, after = after, maxSize = maxSize, quality = quality });
                plan.after += after - before;
            }
            return plan;
        }

        // The size Unity imports at without a max size: the source, scaled to a power of two unless that is turned off.
        private static Vector2Int Uncapped(TextureImporter importer, Texture texture)
        {
            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            if (width <= 0 || height <= 0) return new Vector2Int(texture.width, texture.height);
            int Scale(int v) => importer.npotScale == TextureImporterNPOTScale.None || Mathf.IsPowerOfTwo(v) ? v :
                importer.npotScale == TextureImporterNPOTScale.ToLarger ? Mathf.NextPowerOfTwo(v) :
                importer.npotScale == TextureImporterNPOTScale.ToSmaller ? Mathf.NextPowerOfTwo(v) / 2 : Mathf.ClosestPowerOfTwo(v);
            return new Vector2Int(Scale(width), Scale(height));
        }

        // Import settings this optimization wrote and nobody changed since, or a copy it made.
        private static bool Ours(TextureOptimizationRecord record, Texture2D texture, TextureImporter importer)
        {
            if (record.duplicates.Contains(texture)) return true;
            string guid = AssetDatabase.AssetPathToGUID(importer.assetPath);
            return record.textures.Any(t => t.applied && t.guid == guid && Same(t.after, Read(importer)));
        }

        // The body's and the face's textures, on every material of those meshes (the face often has a head mesh of its own):
        // what people look at most keeps up to 2048 px.
        private static HashSet<Texture> BodyTextures(MyAvatar avatar, List<TextureSlot> slots)
        {
            var root = AvatarRoot(avatar).transform;
            var meshes = new HashSet<Renderer>(new Renderer[] { Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentPlanner.Body(root), FaceTracking.FaceTrackingDetection.Face(root) }.Where(r => r));
            return new HashSet<Texture>(slots.Where(s => s.existing && s.parts.Any(p => meshes.Contains(p.renderer))).Select(s => s.existing));
        }

        // ---- The card's plan ----------------------------------------------------------------------------------

        // Kept per avatar until its renderers, their materials or an imported asset change: redrawing the Inspector does not
        // plan again. Alpha the plan still needs is measured one texture per editor frame.
        private static readonly Dictionary<int, (string key, Plan plan)> Plans = new Dictionary<int, (string key, Plan plan)>();
        private static readonly Queue<Texture2D> Unmeasured = new Queue<Texture2D>();
        private static readonly List<Action> Waiting = new List<Action>();
        private static int imports;

        internal sealed class ImportWatcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) => imports++;
        }

        /// <summary>The plan the card offers, or null while alpha it needs is measured; <paramref name="ready"/> runs once it is.</summary>
        internal static Plan Offer(MyAvatar avatar, Action ready)
        {
            string key = Key(avatar);
            if (Plans.TryGetValue(avatar.GetInstanceID(), out var cached) && cached.key == key) return cached.plan;
            var pending = new List<Texture2D>();
            var plan = Build(avatar, pending);
            if (plan != null) { Plans[avatar.GetInstanceID()] = (key, plan); return plan; }
            foreach (var texture in pending) if (!Unmeasured.Contains(texture)) Unmeasured.Enqueue(texture);
            if (ready != null && !Waiting.Contains(ready)) Waiting.Add(ready);
            EditorApplication.update -= MeasureNext; EditorApplication.update += MeasureNext;
            return null;
        }

        private static void MeasureNext()
        {
            if (Unmeasured.Count > 0)
            {
                var texture = Unmeasured.Dequeue();
                alphaCache ??= LibraryStore.Read<Dictionary<string, bool>>(AlphaCacheFile);
                if (texture) alphaCache[texture.imageContentsHash.ToString()] = TextureAnalysis.UsesAlpha(texture);
                if (Unmeasured.Count > 0) return;
                SaveAlpha();
            }
            EditorApplication.update -= MeasureNext;
            var ready = Waiting.ToArray(); Waiting.Clear();
            foreach (var callback in ready)
                try { callback(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        private static string Key(MyAvatar avatar)
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

        // Memory in the PC build: an explicit PC format decides it, whatever the editor currently holds.
        private static long BuildBytes(Texture texture)
        {
            if (texture is Texture2D && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) is TextureImporter importer)
            {
                var standalone = importer.GetPlatformTextureSettings(Platform);
                if (standalone.overridden && standalone.format != TextureImporterFormat.Automatic)
                    {
                    // Importer formats share their values with the runtime texture formats they produce.
                    var format = Enum.IsDefined(typeof(TextureFormat), (int)standalone.format) ? (TextureFormat)(int)standalone.format : Runtime(standalone.format);
                    return Bytes(texture.width, texture.height, BitsPerPixel(format), importer.mipmapEnabled);
                }
            }
            return Bytes(texture);
        }

        // Which of the textures something besides this avatar shows: another object's renderers in the open scenes (other
        // avatars included), or a prefab or scene in Assets depending on them through its materials, nested prefabs or
        // animations. The open scenes count as they are now, not as saved; this avatar's own prefab and model assets, and
        // those it nests, do not count. A descriptor around or inside this avatar is this avatar.
        private static HashSet<Texture> UsedElsewhere(MyAvatar avatar, HashSet<Texture> textures)
        {
            var result = new HashSet<Texture>();
            if (textures.Count == 0) return result;
            var root = AvatarRoot(avatar).transform;
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                own.Add(scene.path);
                foreach (var sceneRoot in scene.GetRootGameObjects())
                    foreach (var renderer in sceneRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        // Editor-only objects (previews, photo shoots) are not part of the scene.
                        if (renderer.transform.IsChildOf(root) || ((renderer.hideFlags | renderer.gameObject.hideFlags) & HideFlags.DontSave) != 0) continue;
                        foreach (var material in renderer.sharedMaterials.Where(m => m))
                            foreach (int id in material.GetTexturePropertyNameIDs())
                            { var texture = material.GetTexture(id); if (texture && textures.Contains(texture)) result.Add(texture); }
                    }
            }
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                for (Object source = transform.gameObject; (source = PrefabUtility.GetCorrespondingObjectFromSource(source)) != null;)
                    own.Add(AssetDatabase.GetAssetPath(source));
            var stage = PrefabStageUtility.GetPrefabStage(root.gameObject);
            if (stage != null) own.Add(stage.assetPath);
            var remaining = textures.Where(t => !result.Contains(t)).GroupBy(AssetDatabase.GetAssetPath).ToDictionary(g => g.Key, g => g.First());
            if (remaining.Count == 0) return result;
            // One dependency walk over every other prefab and scene: each asset they reach is visited once.
            var elsewhere = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }).Concat(AssetDatabase.FindAssets("t:SceneAsset", new[] { "Assets" }))
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => (p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) && !own.Contains(p))
                .Distinct().ToArray();
            if (elsewhere.Length == 0) return result;
            foreach (var path in AssetDatabase.GetDependencies(elsewhere, true))
                if (remaining.TryGetValue(path, out var texture)) result.Add(texture);
            return result;
        }

        // ---- Apply --------------------------------------------------------------------------------------------

        internal static async Task OptimizeAsync(MyAvatar avatar, Action<float, string> progress, CancellationToken token)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
                throw new InvalidOperationException("Optimize an avatar in an open scene, outside Play Mode.");
            progress(.08f, "Measuring textures…");
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            var plan = Build(avatar);
            if (plan.changes.Count == 0) throw new InvalidOperationException("The avatar's textures are already optimized.");
            progress(.16f, "Checking what else uses these textures…");
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            foreach (var change in plan.changes) change.duplicate = change.path.StartsWith("Packages/", StringComparison.Ordinal);
            var elsewhere = UsedElsewhere(avatar, new HashSet<Texture>(plan.changes.Where(c => !c.duplicate).Select(c => (Texture)c.texture)));
            foreach (var change in plan.changes) change.duplicate |= elsewhere.Contains(change.texture);
            int copies = plan.changes.Count(c => c.duplicate);
            if (copies > 0)
            {
                progress(.25f, $"Copying {copies} shared texture{(copies == 1 ? "" : "s")}…");
                await Task.Yield();
                token.ThrowIfCancellationRequested();
            }
            progress(.45f, $"Compressing {plan.changes.Count} texture{(plan.changes.Count == 1 ? "" : "s")}…");
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            Apply(avatar, plan);
            progress(1f, "Done");
        }

        /// <summary>Writes one texture's new import settings; replaced in tests to fail part-way.</summary>
        internal static Action<TextureImporter, ImporterState> WriteSettings = (importer, state) => { Write(importer, state); importer.SaveAndReimport(); };

        // Every change or none: the copies, material copies and import settings are made first, and a failure on the way puts
        // back the settings already written and removes this run's folder. Only then are the materials swapped and the
        // record written, so it lists exactly what applied.
        private static void Apply(MyAvatar avatar, Plan plan)
        {
            var record = avatar.optimization;
            bool merge = Applied(record);
            string folder = "Assets/Orbiters/MyAvatar/" + Guid.NewGuid().ToString("N");
            var inPlace = plan.changes.Where(c => !c.duplicate).Select(c => (change: c, importer: AssetImporter.GetAtPath(c.path) as TextureImporter)).ToList();
            if (inPlace.Any(p => p.importer == null)) throw new InvalidOperationException("A texture was moved or changed while it was measured. Optimize again.");
            var written = new List<(TexturePlan change, TextureImporter importer, ImporterState before)>();
            Dictionary<Texture, Texture2D> duplicates;
            Dictionary<Material, Material> replacements;
            try
            {
                duplicates = Duplicate(plan.changes.Where(c => c.duplicate).ToList(), folder);
                replacements = CopyMaterials(avatar, duplicates, folder);
                // One import pass for every changed texture, copies included.
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var (change, importer) in inPlace)
                    {
                        // Listed before writing: a write that fails half-way is put back too.
                        written.Add((change, importer, Read(importer)));
                        WriteSettings(importer, Target(change.format, change.maxSize, change.quality));
                    }
                    foreach (var change in plan.changes.Where(c => c.duplicate))
                        WriteSettings((TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(duplicates[change.texture])), Target(change.format, change.maxSize, change.quality));
                }
                finally { AssetDatabase.StopAssetEditing(); }
            }
            catch
            {
                Restore(written.Select(w => (w.importer, w.before)).ToList());
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                throw;
            }

            // Recorded before the Undo snapshot, as not applied: undoing then knows both states and restores the original.
            record.textures.RemoveAll(t => !t.applied);
            var entries = new List<OptimizedTexture>();
            foreach (var (change, importer, before) in written)
            {
                string guid = AssetDatabase.AssetPathToGUID(change.path);
                var entry = record.textures.FirstOrDefault(t => t.guid == guid);
                if (entry == null) record.textures.Add(entry = new OptimizedTexture { guid = guid, before = before });
                // Unity normalises some fields on save; compare later against what it actually stored.
                entry.after = Read(importer);
                entries.Add(entry);
            }
            EditorUtility.SetDirty(avatar);

            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(OptimizeName);
            var renderers = Renderers(avatar).Where(r => r.sharedMaterials.Any(m => m && replacements.ContainsKey(m))).ToList();
            Undo.RecordObjects(new Object[] { avatar }.Concat(renderers).ToArray(), OptimizeName);
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                    if (materials[i] && replacements.TryGetValue(materials[i], out var copy))
                    { record.swaps.Add(new MaterialSwap { renderer = renderer, index = i, before = materials[i], after = copy }); materials[i] = copy; }
                renderer.sharedMaterials = materials; PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            foreach (var entry in entries) entry.applied = true;
            record.duplicates.AddRange(duplicates.Values);
            if (duplicates.Count > 0) record.folder = folder;
            record.batch = avatar.batchFolder;
            record.bytesBefore = merge ? record.bytesBefore + plan.before - record.bytesAfter : plan.before;
            record.bytesAfter = plan.after;
            TextureChanges.Dirty(avatar);
            Undo.CollapseUndoOperations(group);
            Plans.Clear();
        }

        // Puts import settings back after a failed optimization; one that cannot be restored is reported and the rest go on.
        private static void Restore(List<(TextureImporter importer, ImporterState state)> written)
        {
            if (written.Count == 0) return;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (importer, state) in written)
                    try { Write(importer, state); importer.SaveAndReimport(); }
                    catch (Exception ex) { Debug.LogException(ex); }
            }
            finally { AssetDatabase.StopAssetEditing(); }
        }

        // Copies the files with their import settings under a new GUID, imported together; the originals stay untouched.
        internal static Dictionary<Texture, Texture2D> Duplicate(List<TexturePlan> changes, string folder)
        {
            var result = new Dictionary<Texture, Texture2D>();
            if (changes.Count == 0) return result;
            TextureImport.EnsureFolder(folder + "/Textures");
            var paths = new Dictionary<TexturePlan, string>();
            // Names differing only in case are one file on Windows and macOS.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var change in changes)
                {
                    string name = Path.GetFileNameWithoutExtension(change.path), extension = Path.GetExtension(change.path);
                    string destination = folder + "/Textures/" + name + extension;
                    for (int i = 1; !used.Add(destination); i++) destination = folder + "/Textures/" + name + " " + i + extension;
                    string source = FileUtil.GetPhysicalPath(change.path);
                    File.Copy(source, destination);
                    File.WriteAllText(destination + ".meta", Regex.Replace(File.ReadAllText(source + ".meta"), @"(?m)^guid: [0-9a-f]{32}", "guid: " + GUID.Generate()));
                    AssetDatabase.ImportAsset(destination);
                    paths[change] = destination;
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            foreach (var pair in paths)
                result[pair.Key.texture] = AssetDatabase.LoadAssetAtPath<Texture2D>(pair.Value) ?? throw new InvalidOperationException("Unity could not load the copy of " + pair.Key.texture.name);
            return result;
        }

        // Only this avatar's materials that show a copied texture are copied, and only its renderers use the copies.
        private static Dictionary<Material, Material> CopyMaterials(MyAvatar avatar, Dictionary<Texture, Texture2D> duplicates, string folder)
        {
            var result = new Dictionary<Material, Material>();
            if (duplicates.Count == 0) return result;
            var materials = Renderers(avatar).SelectMany(r => r.sharedMaterials).Where(m => m).Distinct()
                .Where(m => m.GetTexturePropertyNameIDs().Any(id => { var t = m.GetTexture(id); return t && duplicates.ContainsKey(t); })).ToList();
            TextureImport.EnsureFolder(folder + "/Materials");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var material in materials)
                {
                    var copy = new Material(material) { name = material.name };
                    foreach (int id in copy.GetTexturePropertyNameIDs())
                    { var texture = copy.GetTexture(id); if (texture && duplicates.TryGetValue(texture, out var duplicate)) copy.SetTexture(id, duplicate); }
                    string name = string.Concat(material.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), path = folder + "/Materials/" + name + ".mat";
                    for (int i = 1; !paths.Add(path); i++) path = folder + "/Materials/" + name + " " + i + ".mat";
                    AssetDatabase.CreateAsset(copy, path);
                    result[material] = copy;
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            return result;
        }

        // ---- Undo ---------------------------------------------------------------------------------------------

        // Restores the materials and import settings. Slots and importers edited since are left as they are; copies stay on disk.
        internal static string Revert(MyAvatar avatar)
        {
            var record = avatar.optimization;
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(RevertName);
            var renderers = record.swaps.Where(s => s.renderer).Select(s => s.renderer).Distinct().ToList();
            // Renderers gone since (a custom base version replaced them) are skipped and reported; the others still revert.
            int goneSlots = record.swaps.Count(s => !s.renderer);
            Undo.RecordObjects(new Object[] { avatar }.Concat(renderers).ToArray(), RevertName);
            int keptSlots = 0;
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                // Newest first: a later optimization copied the copy (A → B → C), so C goes back to B, then B to A.
                foreach (var swap in Enumerable.Reverse(record.swaps).Where(s => s.renderer == renderer))
                    if (swap.index < materials.Length && materials[swap.index] == swap.after) materials[swap.index] = swap.before; else keptSlots++;
                renderer.sharedMaterials = materials; PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            record.swaps.Clear(); record.duplicates.Clear();
            foreach (var texture in record.textures) texture.applied = false;
            TextureChanges.Dirty(avatar);
            int keptImporters = Reconcile(record.textures);
            Undo.CollapseUndoOperations(group);
            Plans.Clear();
            var kept = new List<string>();
            if (keptSlots > 0) kept.Add($"{keptSlots} material slot{(keptSlots == 1 ? "" : "s")}");
            if (keptImporters > 0) kept.Add($"{keptImporters} texture import setting{(keptImporters == 1 ? "" : "s")}");
            string skipped = goneSlots == 0 ? "" : $" Skipped {goneSlots} material slot{(goneSlots == 1 ? "" : "s")} of renderers no longer on the avatar (replaced since, e.g. by a custom base version).";
            if (kept.Count == 0) return goneSlots == 0 ? null : "Optimization undone." + skipped;
            return "Optimization undone. Kept " + string.Join(" and ", kept) + " you changed since." + skipped;
        }

        // Brings each recorded importer to the state its entry asks for. One that matches neither state was edited by hand and stays.
        internal static int Reconcile(IEnumerable<OptimizedTexture> entries)
        {
            int kept = 0;
            var pending = new List<(TextureImporter importer, ImporterState state)>();
            foreach (var entry in entries)
            {
                if (!(AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(entry.guid)) is TextureImporter importer)) continue;
                var current = Read(importer);
                var desired = entry.applied ? entry.after : entry.before;
                if (Same(current, desired)) continue;
                if (!Same(current, entry.applied ? entry.before : entry.after)) { kept++; continue; }
                pending.Add((importer, desired));
            }
            if (pending.Count == 0) return kept;
            AssetDatabase.StartAssetEditing();
            try { foreach (var (importer, state) in pending) { Write(importer, state); importer.SaveAndReimport(); } }
            finally { AssetDatabase.StopAssetEditing(); }
            return kept;
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
