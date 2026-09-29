using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        internal static bool Supported(string path) => new[] { ".png", ".jpg", ".jpeg", ".tga" }.Contains(Path.GetExtension(path).ToLowerInvariant());
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
            for (int i = 0; i < paths.Length; i++)
            {
                string full = paths[i].Replace('\\', '/');
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                string asset = full.Substring(root.Length);
                // A project texture is used as it is when its import settings suit its role; otherwise it is copied with
                // the settings an external file of that role gets, and the original stays untouched.
                if ((asset.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || asset.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) && AssetDatabase.LoadAssetAtPath<Texture2D>(asset) &&
                    Suits(AssetImporter.GetAtPath(asset) as TextureImporter, TextureMatching.FileRole(Path.GetFileName(paths[i])))) resolved[i] = asset;
            }
            var cache = await Task.Run(() => LibraryStore.Read<Dictionary<string, CachedFile>>(CacheFile), token);
            var pending = new List<PendingFile>();
            string staging = Path.Combine(LibraryStore.Folder, Guid.NewGuid().ToString("N"));
            // One owned folder per drop; the source path, size and time identify repeat drops, so no content hash is needed.
            string textures = "Assets/Orbiters/MyAvatar/Textures/" + Path.GetFileName(folder);
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
                        asset = destination, staged = Path.Combine(staging, pending.Count.ToString()) });
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
                            File.WriteAllText(Path.Combine(root, file.asset) + ".meta", Meta(TextureMatching.FileRole(Path.GetFileName(file.asset))));
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
                    result.Add(new TextureEntry { texture = texture, sourceKey = TextureMemory.SourceKey(paths[i]), fileName = Path.GetFileName(paths[i]), role = TextureMatching.FileRole(Path.GetFileName(paths[i])) });
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
                !cached.asset.StartsWith("Assets/Orbiters/MyAvatar/Textures/", StringComparison.Ordinal) || cached.asset.Split('/').Contains("..")) return false;
            string asset = Path.Combine(root, cached.asset);
            return File.Exists(asset) && cached.assetModified == File.GetLastWriteTimeUtc(asset).Ticks && cached.metaModified == File.GetLastWriteTimeUtc(asset + ".meta").Ticks;
        }

        // Colour images are sRGB; metallic, roughness, occlusion, masks and other data are read as linear values.
        private static bool Srgb(string role) => role == "color" || role == "emission" || role == "unknown";

        internal static bool Suits(TextureImporter importer, string role) => importer != null &&
            (role == "normal" ? importer.textureType == TextureImporterType.NormalMap :
                importer.textureType == TextureImporterType.Default && importer.sRGBTexture == Srgb(role));

        // Unity fills omitted importer fields with its defaults; only the settings My Avatar owns are specified.
        private static string Meta(string role)
        {
            bool normal = role == "normal", srgb = Srgb(role);
            return "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\nTextureImporter:\n  serializedVersion: 12\n  mipmaps:\n    sRGBTexture: " +
                (srgb ? 1 : 0) + "\n  isReadable: 0\n  textureType: " + (normal ? 1 : 0) + "\n";
        }

        private sealed class PendingFile { internal string source, asset, staged; internal long length, modified; }
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
