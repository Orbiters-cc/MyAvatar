using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // What an accessory drop contains once folders and archives are opened, and the import of what is not in the project yet.
    internal static class AccessoryImport
    {
        internal const string Folder = "Assets/Orbiters/MyAvatar/Accessories";
        internal const int MaxDocChars = 4000, MaxDocs = 6;
        private static readonly string[] Models = { ".fbx", ".obj" };
        private static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".tga" };
        private static readonly string[] Docs = { ".txt", ".md" };
        // Pictures shipped for people, not materials: setting screenshots, previews, guides.
        private static readonly System.Text.RegularExpressions.Regex NotTexture = new System.Text.RegularExpressions.Regex(
            "settings|screenshot|preview|thumbnail|promo|banner|guide|credits|readme|instructions|showcase", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        [Serializable]
        internal sealed class Doc { public string name, text; }

        [Serializable]
        internal sealed class Drop
        {
            public List<string> packages = new List<string>(), assets = new List<string>(), models = new List<string>(), images = new List<string>();
            public List<Doc> docs = new List<Doc>();
            public List<string> refused = new List<string>();
        }

        // Package index read before import: what it will bring, so the entry prefab is found by GUID wherever Unity puts it.
        internal sealed class PackageIndex
        {
            public readonly List<string> guids = new List<string>(), paths = new List<string>();
            public readonly List<Doc> docs = new List<Doc>();
            public bool scripts;
        }

        // Background thread: no Unity API.
        internal static Drop Expand(string[] paths, string projectRoot, string staging)
        {
            var drop = new Drop();
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paths) Add(drop, Absolute(raw, projectRoot), projectRoot, staging, parents, 0);
            // A prefab or model dropped alone often has its README next to it or one folder up.
            foreach (var parent in parents)
            {
                var dir = parent;
                for (int level = 0; level < 2 && dir != null && (level == 0 || IsBelowAssets(dir, projectRoot)); level++, dir = Path.GetDirectoryName(dir))
                    foreach (var file in SafeFiles(dir).Where(f => Docs.Contains(Ext(f)))) AddDoc(drop.docs, Path.GetFileName(file), () => File.ReadAllText(file));
            }
            drop.assets = drop.assets.Distinct().ToList();
            return drop;
        }

        private static void Add(Drop drop, string path, string projectRoot, string staging, HashSet<string> parents, int depth)
        {
            if (Directory.Exists(path))
            {
                if (depth > 3) return;
                foreach (var entry in SafeEntries(path)) Add(drop, entry, projectRoot, staging, parents, depth + 1);
                return;
            }
            if (!File.Exists(path)) return;
            string ext = Ext(path), name = Path.GetFileName(path);
            string asset = ProjectPath(path, projectRoot);
            if (ext == ".unitypackage") drop.packages.Add(path);
            else if (ext == ".zip" && depth < 3)
            {
                var target = Path.Combine(staging, Hash(path + File.GetLastWriteTimeUtc(path).Ticks));
                if (!Directory.Exists(target)) { var temp = target + ".tmp"; if (Directory.Exists(temp)) Directory.Delete(temp, true); ZipFile.ExtractToDirectory(path, temp); Directory.Move(temp, target); }
                Add(drop, target, projectRoot, staging, parents, depth + 1);
            }
            else if (ext == ".prefab")
            {
                if (asset != null) { drop.assets.Add(asset); if (depth == 0) parents.Add(Path.GetDirectoryName(path)); }
                else drop.refused.Add(name + ": a prefab outside the project needs the package it came with.");
            }
            else if (Models.Contains(ext))
            {
                if (asset == null) drop.models.Add(path); else drop.assets.Add(asset);
                if (depth == 0) parents.Add(Path.GetDirectoryName(path));
            }
            else if (Images.Contains(ext)) { if (!NotTexture.IsMatch(name)) drop.images.Add(path); }
            else if (Docs.Contains(ext)) AddDoc(drop.docs, name, () => File.ReadAllText(path));
            else if (ext == ".blend" || ext == ".max" || ext == ".stl" || ext == ".ma" || ext == ".mb")
                drop.refused.Add(name + ": a source file, not a Unity model. Export it as FBX first.");
            else if (ext == ".unity") drop.refused.Add(name + ": setup scenes are not supported yet. Drop the model or prefab it uses.");
        }

        internal static void AddDoc(List<Doc> docs, string name, Func<string> read)
        {
            if (docs.Count >= MaxDocs || docs.Any(d => d.name == name)) return;
            try
            {
                var text = read();
                if (string.IsNullOrWhiteSpace(text)) return;
                docs.Add(new Doc { name = name, text = text.Length > MaxDocChars ? text.Substring(0, MaxDocChars) : text });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // A .unitypackage is a gzipped tar of <guid>/pathname, <guid>/asset and <guid>/asset.meta entries.
        internal static PackageIndex ReadPackage(string path)
        {
            var index = new PackageIndex();
            var pathnames = new Dictionary<string, string>();
            var texts = new Dictionary<string, byte[]>();
            using (var stream = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            {
                var header = new byte[512];
                string longName = null;
                while (ReadExactly(stream, header, 512))
                {
                    if (header.All(b => b == 0)) break;
                    string name = longName ?? Ascii(header, 0, 100);
                    string prefix = Ascii(header, 345, 155);
                    if (longName == null && prefix.Length > 0) name = prefix + "/" + name;
                    longName = null;
                    long size = Convert.ToInt64(Ascii(header, 124, 12).Trim().Trim('\0').PadLeft(1, '0'), 8);
                    char type = (char)header[156];
                    var parts = name.TrimStart('.', '/').Split('/');
                    bool keep = type == 'L' || parts.Length == 2 && (parts[1] == "pathname" || parts[1] == "asset" && size <= 64 * 1024);
                    byte[] data = keep ? new byte[size] : null;
                    if (keep) ReadExactly(stream, data, (int)size); else Skip(stream, size);
                    Skip(stream, (512 - size % 512) % 512);
                    if (type == 'L') { longName = Encoding.UTF8.GetString(data).TrimEnd('\0'); continue; }
                    if (data == null) continue;
                    if (parts[1] == "pathname") pathnames[parts[0]] = Encoding.UTF8.GetString(data).Split('\n')[0].Trim();
                    else texts[parts[0]] = data;
                }
            }
            foreach (var pair in pathnames)
            {
                index.guids.Add(pair.Key);
                index.paths.Add(pair.Value);
                string ext = Ext(pair.Value);
                if (ext == ".cs" || ext == ".dll") index.scripts = true;
                if (Docs.Contains(ext) && texts.TryGetValue(pair.Key, out var text)) AddDoc(index.docs, Path.GetFileName(pair.Value), () => Encoding.UTF8.GetString(text));
            }
            return index;
        }

        // Imports one package without Unity's dialog. A package with scripts recompiles afterwards; the job it belongs to is
        // persisted by the caller and resumes after the domain reload.
        internal static Task ImportPackageAsync(string path, CancellationToken cancellation)
        {
            var done = new TaskCompletionSource<bool>();
            string name = Path.GetFileNameWithoutExtension(path);
            void Completed(string package) { if (package == name) done.TrySetResult(true); }
            void Failed(string package, string error) { if (package == name) done.TrySetException(new InvalidOperationException("Unity could not import " + package + ": " + error)); }
            void Cancelled(string package) { if (package == name) done.TrySetCanceled(); }
            AssetDatabase.importPackageCompleted += Completed;
            AssetDatabase.importPackageFailed += Failed;
            AssetDatabase.importPackageCancelled += Cancelled;
            var registration = cancellation.Register(() => done.TrySetCanceled());
            done.Task.ContinueWith(_ =>
            {
                AssetDatabase.importPackageCompleted -= Completed;
                AssetDatabase.importPackageFailed -= Failed;
                AssetDatabase.importPackageCancelled -= Cancelled;
                registration.Dispose();
            }, TaskScheduler.FromCurrentSynchronizationContext());
            AssetDatabase.ImportPackage(path, false);
            return done.Task;
        }

        // External models are copied next to each other with the images beside them, so their materials can find textures.
        internal static List<string> CopyModels(Drop drop, string batch)
        {
            var result = new List<string>();
            if (drop.models.Count == 0) return result;
            string folder = Folder + "/" + batch;
            TextureImport.EnsureFolder(folder);
            string root = Path.GetDirectoryName(Application.dataPath);
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var model in drop.models)
                {
                    string target = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileName(model));
                    File.Copy(model, Path.Combine(root, target));
                    result.Add(target);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            foreach (var path in result) AssetDatabase.ImportAsset(path);
            return result;
        }

        internal static string ProjectRoot => Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
        internal static string Staging => Path.Combine(LibraryStore.Folder, "Staging");

        private static string Absolute(string path, string projectRoot) =>
            Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path)).Replace('\\', '/');

        // "Assets/..." or "Packages/..." when the file is inside the project, else null.
        private static string ProjectPath(string full, string projectRoot)
        {
            string prefix = projectRoot.TrimEnd('/') + "/";
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string relative = full.Substring(prefix.Length);
            return relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) ? relative : null;
        }

        private static bool IsBelowAssets(string dir, string projectRoot) =>
            dir.Replace('\\', '/').StartsWith(projectRoot.TrimEnd('/') + "/Assets/", StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> SafeEntries(string dir)
        {
            try { return Directory.EnumerateFileSystemEntries(dir).Select(p => p.Replace('\\', '/')).ToList(); }
            catch (IOException) { return Enumerable.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.EnumerateFiles(dir).ToList(); }
            catch (IOException) { return Enumerable.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
        }

        private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

        private static string Hash(string text)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Take(8).Select(b => b.ToString("x2")));
        }

        private static string Ascii(byte[] buffer, int offset, int length)
        {
            int end = Array.IndexOf(buffer, (byte)0, offset, length);
            return Encoding.UTF8.GetString(buffer, offset, (end < 0 ? offset + length : end) - offset);
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        private static void Skip(Stream stream, long count)
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (n <= 0) return;
                count -= n;
            }
        }
    }
}
