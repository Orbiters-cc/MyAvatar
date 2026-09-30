using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // What an accessory drop contains once folders and archives are opened, and the import of what is not in the project yet.
    internal static class AccessoryImport
    {
        internal const string Folder = "Assets/Orbiters/MyAvatar/Accessories";
        internal const int MaxDocChars = 4000, MaxDocs = 6;
        // A text file larger than this is not a readme (a log, exported data): it is skipped without being read.
        internal const long MaxDocBytes = 1024 * 1024;
        // What the archives of one drop may expand to on disk: a clothing ZIP with its textures fits well within these.
        internal const int MaxArchiveEntries = 20000;
        internal const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;
        private static readonly string[] Models = { ".fbx", ".obj" };
        private static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".tga" };
        private static readonly string[] Docs = { ".txt", ".md" };
        // What an OBJ's material libraries may bring along: images Unity imports as textures, nothing else.
        private static readonly string[] MaterialLibraries = { ".mtl" };
        private static readonly string[] MapImages = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".tif", ".tiff", ".psd", ".gif", ".exr", ".hdr" };
        // MTL statements naming an image: every "map_…" plus the older names without the prefix.
        private static readonly string[] MapStatements = { "map_", "bump", "disp", "decal", "refl", "norm" };
        // Pictures shipped for people, not materials: setting screenshots, previews, guides.
        private static readonly System.Text.RegularExpressions.Regex NotTexture = new System.Text.RegularExpressions.Regex(
            "settings|screenshot|preview|thumbnail|promo|banner|guide|credits|readme|instructions|showcase", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        [Serializable]
        internal sealed class Doc { public string name, text; }

        /// <summary>A file an external model names and needs beside it once copied, like an OBJ's material library.</summary>
        [Serializable]
        internal sealed class Companion { public string model, path; }

        [Serializable]
        internal sealed class Drop
        {
            public List<string> packages = new List<string>(), assets = new List<string>(), models = new List<string>(), images = new List<string>();
            public List<Companion> companions = new List<Companion>();
            public List<Doc> docs = new List<Doc>();
            public List<string> refused = new List<string>();
            /// <summary>This drop's own folder for opened archives, removed once the drop is done.</summary>
            public string staging;
        }

        private sealed class Budget { public int entries, archives; public long bytes; }

        /// <summary>
        /// Whether a drop holds clothes or accessories (packages, archives, prefabs, models), not only textures. Background thread:
        /// folders are looked into three levels deep, at most a few thousand entries.
        /// </summary>
        internal static bool HoldsAccessory(IEnumerable<string> paths, string projectRoot)
        {
            int seen = 0;
            bool Holds(string path, int depth)
            {
                if (++seen > 4000) return false;
                if (Directory.Exists(path)) return depth < 3 && SafeEntries(path).Any(entry => Holds(entry, depth + 1));
                string ext = Ext(path);
                return ext == ".unitypackage" || ext == ".zip" || ext == ".prefab" || Models.Contains(ext);
            }
            return paths.Any(p => Holds(Absolute(p, projectRoot), 0));
        }

        // Background thread: no Unity API. Archives open into the drop's own staging folder.
        internal static Drop Expand(string[] paths, string projectRoot, string staging)
        {
            var drop = new Drop { staging = staging };
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var budget = new Budget();
            foreach (var raw in paths)
            {
                // What a model brings along must be inside what was dropped: the folder, or the folder of a dropped file.
                string path = Absolute(raw, projectRoot);
                Add(drop, path, Directory.Exists(path) ? path : Path.GetDirectoryName(path), projectRoot, staging, parents, budget, 0);
            }
            // A prefab or model dropped alone often has its README next to it or one folder up.
            foreach (var parent in parents)
            {
                var dir = parent;
                for (int level = 0; level < 2 && dir != null && (level == 0 || IsBelowAssets(dir, projectRoot)); level++, dir = Path.GetDirectoryName(dir))
                    foreach (var file in SafeFiles(dir).Where(f => Docs.Contains(Ext(f)))) AddDoc(drop.docs, Path.GetFileName(file), () => ReadDoc(file));
            }
            drop.assets = drop.assets.Distinct().ToList();
            return drop;
        }

        private static void Add(Drop drop, string path, string root, string projectRoot, string staging, HashSet<string> parents, Budget budget, int depth)
        {
            // macOS archive metadata: "__MACOSX" folders and "._name" resource forks are not the files they are named after.
            if (Path.GetFileName(path) == "__MACOSX" || Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal)) return;
            if (Directory.Exists(path))
            {
                if (depth > 3) return;
                foreach (var entry in SafeEntries(path)) Add(drop, entry, root, projectRoot, staging, parents, budget, depth + 1);
                return;
            }
            if (!File.Exists(path)) return;
            string ext = Ext(path), name = Path.GetFileName(path);
            string asset = ProjectPath(path, projectRoot);
            if (ext == ".unitypackage") drop.packages.Add(path);
            else if (ext == ".zip" && depth < 3)
            {
                var target = Path.Combine(staging, (++budget.archives).ToString());
                try { ExtractZip(path, target, budget); }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
                {
                    DeleteFolder(target);
                    drop.refused.Add(name + ": not opened, " + (ex is InvalidDataException ? ex.Message : "it is not a readable ZIP."));
                    return;
                }
                Add(drop, target, target, projectRoot, staging, parents, budget, depth + 1);
            }
            else if (ext == ".prefab")
            {
                if (asset != null) { drop.assets.Add(asset); if (depth == 0) parents.Add(Path.GetDirectoryName(path)); }
                else drop.refused.Add(name + ": a prefab outside the project needs the package it came with.");
            }
            else if (Models.Contains(ext))
            {
                if (asset != null) drop.assets.Add(asset);
                else
                {
                    drop.models.Add(path);
                    if (ext == ".obj") drop.companions.AddRange(ObjCompanions(path, root).Select(file => new Companion { model = path, path = file }));
                }
                if (depth == 0) parents.Add(Path.GetDirectoryName(path));
            }
            else if (Images.Contains(ext)) { if (!NotTexture.IsMatch(name)) drop.images.Add(path); }
            else if (Docs.Contains(ext)) AddDoc(drop.docs, name, () => ReadDoc(path));
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

        // Only the part of a readme that is kept is read.
        internal static string ReadDoc(string path)
        {
            if (new FileInfo(path).Length > MaxDocBytes) return null;
            using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var buffer = new char[MaxDocChars];
                int read = 0;
                for (int n; read < buffer.Length && (n = reader.Read(buffer, read, buffer.Length - read)) > 0;) read += n;
                return new string(buffer, 0, read);
            }
        }

        // Unity reads an OBJ's materials from the libraries its "mtllib" lines name, and their images from the map lines of
        // those, each relative to the file naming it. Only relative names of files inside what was dropped count, and only
        // .mtl libraries and images: a drop cannot bring anything else into the project this way.
        internal static List<string> ObjCompanions(string obj, string root)
        {
            var result = new List<string>();
            string inside = Full(root).TrimEnd('/') + "/";
            foreach (var names in Statements(obj, "mtllib"))
                foreach (var library in Named(obj, names, inside, MaterialLibraries, several: true).Where(l => !result.Contains(l)))
                {
                    result.Add(library);
                    foreach (var map in Statements(library, MapStatements))
                        result.AddRange(Named(library, map, inside, MapImages, several: false).Where(i => !result.Contains(i)));
                }
            return result;
        }

        // What follows the keyword on each line starting with one of them ("map_" stands for every keyword it begins).
        // Keywords are matched ignoring case, as exporters write them either way.
        private static List<string> Statements(string file, params string[] keywords)
        {
            var result = new List<string>();
            try
            {
                using (var reader = new StreamReader(file))
                    for (string line; (line = reader.ReadLine()) != null;)
                    {
                        int start = 0;
                        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;
                        int end = start;
                        while (end < line.Length && !char.IsWhiteSpace(line[end])) end++;
                        if (end < line.Length && Starts(line, start, end - start, keywords)) result.Add(line.Substring(end).Trim());
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        private static bool Starts(string line, int start, int length, string[] keywords)
        {
            foreach (var k in keywords)
                if ((k.EndsWith("_", StringComparison.Ordinal) ? length > k.Length : length == k.Length) &&
                    string.Compare(line, start, k, 0, k.Length, StringComparison.OrdinalIgnoreCase) == 0) return true;
            return false;
        }

        // The files a statement names, relative to the file holding it. Names may hold spaces: the whole text is tried first,
        // then, for "mtllib", each word as its own library, or, for a map, the longest ending after its options that exists.
        private static IEnumerable<string> Named(string from, string text, string inside, string[] extensions, bool several)
        {
            var whole = Resolve(from, text, inside, extensions);
            if (whole != null) return new[] { whole };
            var words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (several) return words.Select(w => Resolve(from, w, inside, extensions)).Where(p => p != null).ToList();
            for (int i = 1; i < words.Length; i++)
            {
                var file = Resolve(from, string.Join(" ", words.Skip(i)), inside, extensions);
                if (file != null) return new[] { file };
            }
            return Enumerable.Empty<string>();
        }

        private static string Resolve(string from, string name, string inside, string[] extensions)
        {
            name = name.Trim().Trim('"').Replace('\\', '/');
            try
            {
                if (name.Length == 0 || Path.IsPathRooted(name) || !extensions.Contains(Ext(name))) return null;
                string full = Full(Path.Combine(Path.GetDirectoryName(from), name));
                return full.StartsWith(inside, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return null; }
        }

        // The package's entries without importing it, with the small text files kept to read its readmes.
        internal static UnityPackageIndex ReadPackage(string path) => UnityPackageIndex.Read(path, p => Docs.Contains(Ext(p)));

        internal static List<Doc> PackageDocs(UnityPackageIndex index)
        {
            var docs = new List<Doc>();
            foreach (var entry in index.Entries.Where(e => e.Content != null)) AddDoc(docs, Path.GetFileName(entry.Path), () => Encoding.UTF8.GetString(entry.Content));
            return docs;
        }

        // Bounded in files and expanded bytes across the whole drop, nested archives included; every file stays inside its folder.
        private static void ExtractZip(string zip, string target, Budget budget)
        {
            string root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zip))
            {
                long declared = archive.Entries.Sum(e => e.Length);
                if (budget.entries + archive.Entries.Count > MaxArchiveEntries) throw new InvalidDataException("it holds too many files.");
                if (budget.bytes + declared > MaxArchiveBytes) throw new InvalidDataException("it expands to more than 4 GB.");
                budget.entries += archive.Entries.Count;
                Directory.CreateDirectory(root);
                var buffer = new byte[81920];
                foreach (var entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("it has files that would be written outside its folder.");
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal)) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = entry.Open())
                    using (var output = File.Create(destination))
                        for (int n; (n = input.Read(buffer, 0, buffer.Length)) > 0;)
                        {
                            // Sizes declared by the archive can lie: the bytes actually written count.
                            if ((budget.bytes += n) > MaxArchiveBytes) throw new InvalidDataException("it expands to more than 4 GB.");
                            output.Write(buffer, 0, n);
                        }
                }
            }
        }

        /// <summary>Removes a drop's opened archives. Only folders inside the staging folder are ever deleted.</summary>
        internal static void DeleteStaging(string staging)
        {
            if (string.IsNullOrEmpty(staging)) return;
            string root = Path.GetFullPath(Staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, full;
            try { full = Path.GetFullPath(staging); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return; }
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length) DeleteFolder(full);
        }

        // Background thread. Leftovers of drops that never finished (Unity closed, no Inspector after a resumed drop), except
        // those a pending choice still offers.
        internal static void SweepStaging(string staging, ICollection<string> keep)
        {
            foreach (var dir in SafeDirectories(staging))
                if (!keep.Contains(Path.GetFullPath(dir)) && Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-7)) DeleteFolder(dir);
        }

        private static void DeleteFolder(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Imports share a gate with other Orbiters tools; the caller persists its job before a script can reload Unity.
        internal static Task ImportPackageAsync(string path, CancellationToken cancellation, Action starting = null) =>
            UnityPackageImport.ImportAsync(path, cancellation, starting);

        // External models are copied with the files they name, laid out as they were relative to each other, so Unity finds
        // an OBJ's material libraries and their images. A model whose files would land on another model's different files gets
        // a folder of its own; files two models share are copied once. Those files import first, for the models to find them.
        internal static List<string> CopyModels(Drop drop, string batch)
        {
            var models = new List<string>();
            if (drop.models.Count == 0) return models;
            string folder = Folder + "/" + batch;
            // Asset path → the file copied there.
            var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var model in drop.models)
            {
                var files = new[] { model }.Concat(drop.companions.Where(c => c.model == model).Select(c => c.path)).Select(Full).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string common = CommonFolder(files), destination = folder;
                for (int i = 1; files.Any(f => copies.TryGetValue(destination + "/" + f.Substring(common.Length), out var copied) && !string.Equals(copied, f, StringComparison.OrdinalIgnoreCase)); i++)
                    destination = folder + "/" + Path.GetFileNameWithoutExtension(model) + (i == 1 ? "" : " " + i);
                foreach (var file in files) copies[destination + "/" + file.Substring(common.Length)] = file;
                models.Add(destination + "/" + files[0].Substring(common.Length));
            }
            models = models.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var target in copies.Keys) TextureImport.EnsureFolder(Path.GetDirectoryName(target).Replace('\\', '/'));
            Copy(copies.Where(c => !models.Contains(c.Key, StringComparer.OrdinalIgnoreCase)));
            Copy(copies.Where(c => models.Contains(c.Key, StringComparer.OrdinalIgnoreCase)));
            return models;
        }

        private static void Copy(IEnumerable<KeyValuePair<string, string>> files)
        {
            var copies = files.ToList();
            if (copies.Count == 0) return;
            string project = ProjectRoot + "/";
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var copy in copies)
                {
                    File.Copy(copy.Value, project + copy.Key);
                    AssetDatabase.ImportAsset(copy.Key);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
        }

        // The deepest folder holding all the files, ending with "/".
        private static string CommonFolder(List<string> files)
        {
            string common = files[0].Substring(0, files[0].LastIndexOf('/') + 1);
            foreach (var file in files)
                while (common.Length > 0 && !file.StartsWith(common, StringComparison.OrdinalIgnoreCase))
                    common = common.Length < 2 ? "" : common.Substring(0, common.LastIndexOf('/', common.Length - 2) + 1);
            return common;
        }

        internal static string ProjectRoot => Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');
        internal static string Staging => Path.Combine(LibraryStore.Folder, "Staging");

        private static string Absolute(string path, string projectRoot) => Full(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        private static string Full(string path) => Path.GetFullPath(path).Replace('\\', '/');

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

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).ToList() : Enumerable.Empty<string>(); }
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
    }
}
