using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal static class TextureImport
    {
        internal const int MaxFiles = 48;
        private const string CacheFile = "texture-cache.json";
        // Where drops copy their images, one folder per drop.
        internal const string DropFolder = "Assets/Orbiters/MyAvatar/Textures/";
        internal static bool Supported(string path) => new[] { ".png", ".jpg", ".jpeg", ".tga" }.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>An image a drop copied into the project: wherever it is shown, a drop put it there.</summary>
        internal static bool Dropped(string assetPath) => assetPath != null && assetPath.StartsWith(DropFolder, StringComparison.Ordinal);

        // The material copies a drop makes, in its batch folder ("Assets/Orbiters/MyAvatar/<batch>/Materials/").
        private static readonly System.Text.RegularExpressions.Regex DropMaterialPath =
            new System.Text.RegularExpressions.Regex(@"^Assets/Orbiters/MyAvatar/[0-9a-f]{32}/Materials/", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        internal static bool DropMaterial(Material material) => material != null && DropMaterialPath.IsMatch(AssetDatabase.GetAssetPath(material));

        // Paths on disk for a drop: the Project window drags project paths, and a read-only package's "Packages/..." lives in
        // Library/PackageCache. Runs on the main thread, where Unity resolves package paths.
        internal static string[] Physical(IEnumerable<string> paths)
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            return paths.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(root, FileUtil.GetPhysicalPath(p)))).ToArray();
        }

        internal static string[] Expand(IEnumerable<string> paths)
        {
            var files = new List<string>();
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.Exists(path)) files.AddRange(Directory.EnumerateFiles(path).Where(Supported));
                else if (File.Exists(path) && Supported(path)) files.Add(path);
                if (files.Count > MaxFiles) throw new InvalidOperationException("Drop at most 48 textures at a time.");
            }
            if (files.Count == 0) throw new InvalidOperationException("Drop PNG, JPG or TGA files, or a folder containing them.");
            var unique = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (unique.Any(p => new FileInfo(p).Length > 256L * 1024 * 1024) || unique.Sum(p => new FileInfo(p).Length) > 1024L * 1024 * 1024)
                throw new InvalidOperationException("Use files under 256 MB and a texture set under 1 GB.");
            return unique;
        }

        internal static async Task<List<TextureEntry>> ImportAsync(string[] paths, string folder, Action<float, string> progress, CancellationToken token)
        {
            string root = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/') + "/";
            var resolved = new string[paths.Length];
            var roles = paths.Select(p => TextureMatching.FileRole(Path.GetFileName(p))).ToArray();
            for (int i = 0; i < paths.Length; i++)
            {
                string full = paths[i].Replace('\\', '/');
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string asset = full.Substring(root.Length);
                if (!asset.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !asset.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ||
                    !AssetDatabase.LoadAssetAtPath<Texture2D>(asset)) continue;
                var importer = AssetImporter.GetAtPath(asset) as TextureImporter;
                // A texture already set up as a normal map is one, even when its name does not say so.
                if (roles[i] == "unknown" && importer != null && importer.textureType == TextureImporterType.NormalMap) roles[i] = "normal";
                // A project texture is used as it is when its import settings suit its role; otherwise it is copied with
                // the settings an external file of that role gets, and the original stays untouched.
                if (Suits(importer, roles[i])) resolved[i] = asset;
            }
            var cache = await Task.Run(() => LibraryStore.Read<Dictionary<string, CachedFile>>(CacheFile), token);
            var pending = new List<PendingFile>();
            string staging = Path.Combine(LibraryStore.Folder, Guid.NewGuid().ToString("N"));
            // One owned folder per drop; the source path, size and time identify repeat drops, so no content hash is needed.
            string textures = DropFolder + Path.GetFileName(folder);
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    if (resolved[i] != null) continue;
                    var source = new FileInfo(paths[i]);
                    if (cache.TryGetValue(source.FullName, out var cached) && Valid(cached, source, root)) { resolved[i] = cached.asset; continue; }
                    string name = Path.GetFileName(paths[i]), destination = textures + "/" + name;
                    for (int n = 1; pending.Any(p => string.Equals(p.asset, destination, StringComparison.OrdinalIgnoreCase)); n++)
                        destination = textures + "/" + Path.GetFileNameWithoutExtension(name) + " " + n + Path.GetExtension(name);
                    pending.Add(new PendingFile { source = source.FullName, length = source.Length, modified = source.LastWriteTimeUtc.Ticks,
                        asset = destination, staged = Path.Combine(staging, pending.Count.ToString()), role = roles[i] });
                    resolved[i] = destination;
                }
                if (pending.Count > 0)
                {
                    progress(.15f, $"Copying {pending.Count} new texture{(pending.Count == 1 ? "" : "s")}…");
                    Directory.CreateDirectory(staging);
                    // Copies are I/O-bound; a few in parallel keeps the disk busy without oversubscribing it.
                    await Task.Run(() => Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, file => {
                        File.Copy(file.source, file.staged);
                        var source = new FileInfo(file.source);
                        if (source.Length != file.length || source.LastWriteTimeUtc.Ticks != file.modified)
                            throw new IOException("A texture changed while being copied. Drop the set again.");
                    }), token);
                    token.ThrowIfCancellationRequested();
                    progress(.35f, $"Importing {pending.Count} new texture{(pending.Count == 1 ? "" : "s")}…");
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    EnsureFolder(textures);
                    // Targeted imports rather than a project Refresh, which could also start unrelated imports or script compilation.
                    AssetDatabase.StartAssetEditing();
                    try
                    {
                        foreach (var file in pending)
                        {
                            File.Move(file.staged, Path.Combine(root, file.asset));
                            // Settings are written before the first import, so each texture is imported exactly once.
                            File.WriteAllText(Path.Combine(root, file.asset) + ".meta", Meta(file.role == "normal", Srgb(file.role)));
                            AssetDatabase.ImportAsset(file.asset);
                        }
                    }
                    finally { AssetDatabase.StopAssetEditing(); }
                }
                var result = new List<TextureEntry>();
                for (int i = 0; i < paths.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(resolved[i]);
                    if (!texture) throw new InvalidOperationException("Unity could not load " + Path.GetFileName(paths[i]));
                    result.Add(new TextureEntry { texture = texture, sourceKey = TextureMemory.SourceKey(paths[i]), fileName = Path.GetFileName(paths[i]), role = roles[i] });
                }
                foreach (var file in pending) cache[file.source] = new CachedFile { asset = file.asset, length = file.length, modified = file.modified };
                var written = pending.Select(p => p.asset).ToArray();
                await Task.Run(() => {
                    foreach (var pair in cache.Where(p => p.Value != null && written.Contains(p.Value.asset)))
                    {
                        string asset = Path.Combine(root, pair.Value.asset);
                        pair.Value.assetModified = File.GetLastWriteTimeUtc(asset).Ticks; pair.Value.metaModified = File.GetLastWriteTimeUtc(asset + ".meta").Ticks;
                    }
                    LibraryStore.Write(CacheFile, cache.Skip(Math.Max(0, cache.Count - 2048)).ToDictionary(p => p.Key, p => p.Value));
                });
                progress(.7f, pending.Count > 0 ? $"Imported {pending.Count} new texture{(pending.Count == 1 ? "" : "s")}" : "Textures ready");
                return result;
            }
            finally
            {
                // Only this operation's staging directory; never remove project assets on cancellation.
                if (Directory.Exists(staging)) await Task.Run(() => Directory.Delete(staging, true));
            }
        }

        private static bool Valid(CachedFile cached, FileInfo source, string root)
        {
            if (cached == null || string.IsNullOrEmpty(cached.asset) || cached.length != source.Length || cached.modified != source.LastWriteTimeUtc.Ticks ||
                !Dropped(cached.asset) || cached.asset.Split('/').Contains("..")) return false;
            string asset = Path.Combine(root, cached.asset);
            return File.Exists(asset) && cached.assetModified == File.GetLastWriteTimeUtc(asset).Ticks && cached.metaModified == File.GetLastWriteTimeUtc(asset + ".meta").Ticks;
        }

        // Colour images are sRGB; metallic, roughness, occlusion, masks and other data are read as linear values.
        private static bool Srgb(string role) => role == "color" || role == "emission" || role == "unknown";

        // An image whose name gives no role suits any plain import: the slot it lands in decides (see ForSlots).
        internal static bool Suits(TextureImporter importer, string role) => importer != null &&
            (role == "normal" ? importer.textureType == TextureImporterType.NormalMap :
                importer.textureType == TextureImporterType.Default && (role == "unknown" || importer.sRGBTexture == Srgb(role)));

        // Unity fills omitted importer fields with its defaults; only the settings My Avatar owns are specified.
        private static string Meta(bool normal, bool srgb)
        {
            return "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\nTextureImporter:\n  serializedVersion: 12\n  mipmaps:\n    sRGBTexture: " +
                (srgb ? 1 : 0) + "\n  isReadable: 0\n  textureType: " + (normal ? 1 : 0) + "\n";
        }

        /// <summary>
        /// The import settings of a copy of a project texture: every setting of the original (max size, compression, wrap,
        /// filter, alpha, mipmaps, each platform's size), read another way only (normal map or not, sRGB or not), under a new
        /// GUID. A platform's chosen format goes back to automatic: one picked for the other reading (BC5 for a normal map)
        /// would lose channels. Null when the original's settings can't be read that way (the copy then gets <see cref="Meta"/>).
        /// </summary>
        internal static string CopyMeta(string original, bool normal, bool srgb)
        {
            if (string.IsNullOrEmpty(original) || !original.Contains("TextureImporter:")) return null;
            var guid = new Regex(@"(?m)^guid: [0-9a-fA-F]{32}[ \t]*\r?$");
            var type = new Regex(@"(?m)^(  textureType: )-?\d+");
            var color = new Regex(@"(?m)^(    sRGBTexture: )\d+");
            if (!guid.IsMatch(original) || !type.IsMatch(original) || !color.IsMatch(original)) return null;
            string meta = guid.Replace(original, "guid: " + Guid.NewGuid().ToString("N"), 1);
            meta = type.Replace(meta, "${1}" + (normal ? 1 : 0), 1);
            meta = Regex.Replace(meta, @"(?m)^(    textureFormat: )-?\d+", "${1}-1");
            return color.Replace(meta, "${1}" + (srgb ? 1 : 0), 1);
        }

        // ---- The slot an image lands in ---------------------------------------------------------------------

        // How a slot reads its image: "normal", "color" (sRGB) or linear "data" (metallic, smoothness, roughness, occlusion,
        // masks, height); null when its role says nothing.
        internal static string Reading(string slotRole) => slotRole == "normal" ? "normal" : slotRole == "color" || slotRole == "emission" ? "color" :
            slotRole == "unknown" || slotRole == "specular" ? null : "data";

        private static bool Reads(TextureImporter importer, string reading) => importer != null && (reading == "normal"
            ? importer.textureType == TextureImporterType.NormalMap
            : importer.textureType == TextureImporterType.Default && importer.sRGBTexture == (reading == "color"));

        /// <summary>
        /// The image each slot gets, imported the way that slot reads it (a normal map, sRGB colour or linear data). An image
        /// this batch copied in is changed in place when the batch shows it nowhere else; any other image (a project texture
        /// used as it is, one the batch already shows, or one wanted two ways) gets a copy in the batch folder, so nothing
        /// else that uses it changes. Undo and Redo follow: the batch's materials hold the image they show.
        /// </summary>
        internal static Dictionary<(Texture2D texture, string reading), Texture2D> ForSlots(IEnumerable<(Texture2D texture, string reading)> uses,
            ICollection<Texture2D> shown, string folder)
        {
            var result = new Dictionary<(Texture2D, string), Texture2D>();
            var needed = uses.Where(u => u.texture && u.reading != null).Distinct().ToList();
            string root = Path.GetDirectoryName(Application.dataPath), own = DropFolder + Path.GetFileName(folder) + "/";
            var inPlace = new List<TextureImporter>();
            var copies = new List<(Texture2D texture, string reading, string path)>();
            foreach (var (texture, reading) in needed)
            {
                string path = AssetDatabase.GetAssetPath(texture);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || Reads(importer, reading)) { result[(texture, reading)] = texture; continue; }
                if (path.StartsWith(own, StringComparison.Ordinal) && !shown.Contains(texture) && needed.Count(u => u.texture == texture) == 1)
                {
                    importer.textureType = reading == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    if (reading != "normal") importer.sRGBTexture = reading == "color";
                    inPlace.Add(importer); result[(texture, reading)] = texture;
                    continue;
                }
                string name = Path.GetFileNameWithoutExtension(path) + " (" + reading + ")", extension = Path.GetExtension(path), copy = folder + "/Textures/" + name + extension;
                for (int i = 1; File.Exists(Path.Combine(root, copy)) || copies.Any(c => string.Equals(c.path, copy, StringComparison.OrdinalIgnoreCase)); i++)
                    copy = folder + "/Textures/" + name + " " + i + extension;
                copies.Add((texture, reading, copy));
            }
            if (inPlace.Count + copies.Count == 0) return result;
            if (copies.Count > 0) EnsureFolder(folder + "/Textures");
            // One import pass; copies keep the original's settings but how they are read (CopyMeta).
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var importer in inPlace) importer.SaveAndReimport();
                foreach (var (texture, reading, copy) in copies)
                {
                    string source = Path.Combine(root, FileUtil.GetPhysicalPath(AssetDatabase.GetAssetPath(texture)));
                    File.Copy(source, Path.Combine(root, copy));
                    string original = File.Exists(source + ".meta") ? File.ReadAllText(source + ".meta") : null;
                    File.WriteAllText(Path.Combine(root, copy) + ".meta",
                        CopyMeta(original, reading == "normal", reading == "color") ?? Meta(reading == "normal", reading == "color"));
                    AssetDatabase.ImportAsset(copy);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            foreach (var (texture, reading, copy) in copies)
                result[(texture, reading)] = AssetDatabase.LoadAssetAtPath<Texture2D>(copy) ?? throw new InvalidOperationException("Unity could not load the copy of " + texture.name);
            return result;
        }

        private sealed class PendingFile { internal string source, asset, staged, role; internal long length, modified; }
        private sealed class CachedFile
        {
            public string asset;
            public long length, modified, assetModified, metaModified;
        }

        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
