using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Frees the files gallery assets brought into the project once nothing needs them. A file is only offered when every
    /// installation that brought it was removed, and it is kept (with the reason) when it was in the project before, was
    /// changed since, belongs to a package or another tool, is code, or is still used by any scene, prefab, material or
    /// installation in the project or in an open scene. Whenever that cannot be checked, the file stays. Cleaning up checks
    /// it all again first, and each file once more as it moves. Files go to a quarantine in Library with their .meta (and
    /// the .meta of the folders they leave empty), from which Restore puts them back as they were.
    /// </summary>
    internal static class GalleryCleanup
    {
        internal sealed class Item
        {
            public string guid, path, reason, from;
            /// <summary>The file's content when it was checked: a file changed since stays.</summary>
            public string hash;
            public long bytes;
        }

        internal sealed class Report
        {
            public readonly List<Item> Reclaim = new List<Item>(), Kept = new List<Item>();
            public long ReclaimBytes => Reclaim.Sum(i => i.bytes);
            public long KeptBytes => Kept.Sum(i => i.bytes);
            /// <summary>Installations whose files are all reclaimed: the ledger forgets them.</summary>
            public readonly List<string> Finished = new List<string>();
        }

        internal sealed class Batch
        {
            public string id, folder;
            public long createdAt;
            public List<Item> items = new List<Item>();
            /// <summary>The folders the move left empty: removed from the project, their .meta kept in the batch.</summary>
            public List<string> folders = new List<string>();
            public long Bytes => items.Sum(i => i.bytes);
        }

        private const string Manifest = "batch.json";
        private static string QuarantineRoot => Path.Combine(LibraryStore.Folder, "Gallery", "Quarantine");

        internal static async Task<Report> ScanAsync(Action<float, string> progress, CancellationToken cancellation)
        {
            var report = new Report();
            var entries = GalleryLedger.Entries;
            string root = GalleryLedger.ProjectRoot;
            // Installations still worn in an open scene count as installed, also after an Undo brought them back, and so
            // do those still being installed.
            var worn = new HashSet<string>(OpenSceneAttachments().Select(a => a.gallery.installId).Where(id => !string.IsNullOrEmpty(id)));
            worn.UnionWith(GalleryJournal.All.Where(j => !j.Terminal).Select(j => j.id));
            var active = entries.Where(e => e.removedAt == 0 || worn.Contains(e.installId)).ToList();
            var removed = entries.Where(e => !active.Contains(e)).ToList();
            var preexisting = new HashSet<string>(entries.SelectMany(e => e.preexisting).Select(f => f.guid), StringComparer.OrdinalIgnoreCase);
            var activeFiles = active.SelectMany(e => e.files.Select(f => (f.guid, e))).GroupBy(p => p.guid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().e, StringComparer.OrdinalIgnoreCase);
            // A file several versions brought may hold any of their contents (the latest update's).
            var installedHashes = entries.SelectMany(e => e.files).GroupBy(f => f.guid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(f => f.hash).Where(h => !string.IsNullOrEmpty(h)), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            progress?.Invoke(0.05f, "Reading what the gallery installed…");
            var candidates = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in removed)
                foreach (var file in entry.files)
                {
                    if (candidates.ContainsKey(file.guid)) continue;
                    string path = AssetDatabase.GUIDToAssetPath(file.guid);
                    if (string.IsNullOrEmpty(path) || !File.Exists(Path.Combine(root, path))) continue;
                    var item = new Item { guid = file.guid, path = path, bytes = new FileInfo(Path.Combine(root, path)).Length, from = entry.assetName };
                    string reason = null;
                    if (preexisting.Contains(file.guid)) reason = "It was in the project before the gallery imported it.";
                    else if (activeFiles.TryGetValue(file.guid, out var user)) reason = $"{user.assetName} on {user.avatarName} still uses it.";
                    else if (!path.StartsWith("Assets/", StringComparison.Ordinal)) reason = "It belongs to a package.";
                    else if (Owner(root, path) is string owner) reason = "It belongs to " + owner + ".";
                    else if (CodeContent.IsCode(path)) reason = "It is code: other tools may depend on it.";
                    else
                    {
                        item.hash = await Task.Run(() => UnityPackageFiles.FileHash(Path.Combine(root, path)), cancellation);
                        if (!installedHashes.TryGetValue(file.guid, out var known) || !known.Contains(item.hash)) reason = "It was changed since it was installed.";
                    }
                    item.reason = reason;
                    candidates[file.guid] = item;
                    if (reason != null) report.Kept.Add(item);
                }

            var open = candidates.Values.Where(i => i.reason == null).ToList();
            if (open.Count > 0)
            {
                ProjectReferences references;
                try { references = await ProjectReferences.ScanAsync((value, text) => progress?.Invoke(0.1f + value * 0.8f, text), cancellation); }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    foreach (var item in open) { item.reason = "What uses it could not be checked: " + ex.Message; report.Kept.Add(item); }
                    return report;
                }
                var usedInScenes = new HashSet<string>(OpenSceneDependencies(), StringComparer.OrdinalIgnoreCase);
                foreach (var (item, reason) in Retained(open.ToDictionary(i => i.path, i => i, StringComparer.OrdinalIgnoreCase),
                             path => references.UsersOf(path), path => usedInScenes.Contains(path)))
                {
                    item.reason = reason;
                    report.Kept.Add(item);
                }
                report.Reclaim.AddRange(open.Where(i => i.reason == null));
            }
            var reclaimed = new HashSet<string>(report.Reclaim.Select(i => i.guid), StringComparer.OrdinalIgnoreCase);
            report.Finished.AddRange(removed.Where(e => e.files.All(f => reclaimed.Contains(f.guid) || string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(f.guid)))).Select(e => e.installId));
            progress?.Invoke(1f, "Done");
            return report;
        }

        /// <summary>
        /// The candidates that must stay, with why: used in an open scene, or used by a file that is not a candidate, or used
        /// by a candidate that stays (files only used by other files being removed go together). Each file's users are read
        /// once and what stays spreads to the files it uses from a queue.
        /// </summary>
        internal static List<(Item item, string reason)> Retained(IReadOnlyDictionary<string, Item> candidates, Func<string, IReadOnlyCollection<string>> usersOf, Func<string, bool> inOpenScene)
        {
            var kept = new List<(Item, string)>();
            var staying = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            // Which candidates each candidate uses: once it stays, so do they.
            var uses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in candidates)
            {
                var users = usersOf(pair.Key) ?? Array.Empty<string>();
                var outside = users.Where(u => !candidates.ContainsKey(u)).ToList();
                foreach (string user in users.Where(candidates.ContainsKey))
                {
                    if (!uses.TryGetValue(user, out var list)) uses[user] = list = new List<string>();
                    list.Add(pair.Key);
                }
                string reason = inOpenScene(pair.Key) ? "It is used in an open scene." : outside.Count > 0 ? "Used by " + Describe(outside) + "." : null;
                if (reason == null) continue;
                staying.Add(pair.Key);
                kept.Add((pair.Value, reason));
                queue.Enqueue(pair.Key);
            }
            while (queue.Count > 0)
            {
                string user = queue.Dequeue();
                if (!uses.TryGetValue(user, out var used)) continue;
                foreach (string path in used)
                {
                    if (!staying.Add(path)) continue;
                    kept.Add((candidates[path], "Used by " + Path.GetFileName(user) + "."));
                    queue.Enqueue(path);
                }
            }
            return kept;
        }

        private static string Describe(IReadOnlyCollection<string> users)
        {
            var first = users.Take(2).Select(Path.GetFileName).ToList();
            return string.Join(", ", first) + (users.Count > first.Count ? $" and {users.Count - first.Count} more" : "");
        }

        // A folder holding a package.json up to Assets/ is another tool's (an embedded package, an asset with its own updater).
        private static string Owner(string root, string path)
        {
            for (string folder = Path.GetDirectoryName(path)?.Replace('\\', '/'); !string.IsNullOrEmpty(folder) && folder != "Assets"; folder = Path.GetDirectoryName(folder)?.Replace('\\', '/'))
                if (File.Exists(Path.Combine(root, folder, "package.json"))) return Path.GetFileName(folder);
            return null;
        }

        private static IEnumerable<OrbitersAttachment> OpenSceneAttachments()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var attachment in root.GetComponentsInChildren<OrbitersAttachment>(true))
                        if (attachment.gallery != null && attachment.gallery.Installed) yield return attachment;
            }
        }

        // Assets the loaded scenes use right now, saved or not.
        private static IEnumerable<string> OpenSceneDependencies()
        {
            var roots = new List<UnityEngine.Object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) roots.AddRange(scene.GetRootGameObjects());
            }
            return EditorUtility.CollectDependencies(roots.ToArray()).Where(o => o != null && EditorUtility.IsPersistent(o))
                .Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).Distinct();
        }

        /// <summary>
        /// Cleans up what <paramref name="shown"/> offered and is still free now: the project is checked again (a file
        /// edited, used or replaced since the report stays), then those files go to quarantine. <paramref name="stayed"/>
        /// gets the files offered that stay now, with why.
        /// </summary>
        internal static async Task<Batch> CleanUpAsync(Report shown, List<Item> stayed, Action<float, string> progress, CancellationToken cancellation)
        {
            if (shown == null || shown.Reclaim.Count == 0) return null;
            var now = await ScanAsync(progress, cancellation);
            var offered = new HashSet<string>(shown.Reclaim.Select(i => i.guid), StringComparer.OrdinalIgnoreCase);
            var still = now.Reclaim.Where(i => offered.Contains(i.guid)).ToList();
            var free = new HashSet<string>(still.Select(i => i.guid), StringComparer.OrdinalIgnoreCase);
            stayed?.AddRange(now.Kept.Where(i => offered.Contains(i.guid)));
            stayed?.AddRange(shown.Reclaim.Where(i => !free.Contains(i.guid) && !now.Kept.Any(k => string.Equals(k.guid, i.guid, StringComparison.OrdinalIgnoreCase)))
                .Select(i => new Item { guid = i.guid, path = i.path, from = i.from, bytes = i.bytes, reason = "It is not in the project any more." }));
            var report = new Report();
            report.Reclaim.AddRange(still);
            report.Finished.AddRange(now.Finished);
            return Quarantine(report, stayed);
        }

        /// <summary>
        /// Moves the reclaimable files (and their .meta) to a quarantine batch in Library; Restore brings them back. Each
        /// file moves only while it is still at its path with the content it was checked with; one that changed is added
        /// to <paramref name="stayed"/>.
        /// </summary>
        internal static Batch Quarantine(Report report, List<Item> stayed = null)
        {
            if (report == null || report.Reclaim.Count == 0) return null;
            string root = GalleryLedger.ProjectRoot;
            var batch = NewBatch();
            AssetDatabase.ReleaseCachedFileHandles();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Stay(Item item, string reason) => stayed?.Add(new Item { guid = item.guid, path = item.path, from = item.from, bytes = item.bytes, hash = item.hash, reason = reason });
            try
            {
                foreach (var item in report.Reclaim)
                {
                    string source = Path.Combine(root, item.path), target = Path.Combine(batch.folder, item.path);
                    if (!File.Exists(source)) continue;
                    if (!string.Equals(AssetDatabase.GUIDToAssetPath(item.guid), item.path, StringComparison.Ordinal)) { Stay(item, "It moved since it was checked."); continue; }
                    if (string.IsNullOrEmpty(item.hash) || !string.Equals(UnityPackageFiles.FileHash(source), item.hash, StringComparison.OrdinalIgnoreCase))
                    { Stay(item, "It was changed since it was checked."); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(source, target);
                    // In the batch from here: Restore brings back whatever of it is in the quarantine.
                    batch.items.Add(item);
                    if (File.Exists(source + ".meta"))
                    {
                        try { File.Move(source + ".meta", target + ".meta"); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // The file goes back beside its .meta rather than apart from it.
                            try { File.Move(target, source); batch.items.Remove(item); Stay(item, "Its .meta could not be moved (" + ex.Message + ")."); }
                            catch (Exception back) when (back is IOException || back is UnauthorizedAccessException) { Debug.LogWarning($"[My Avatar] {item.path} is in the quarantine without its .meta: Restore puts it back."); }
                            continue;
                        }
                    }
                    folders.Add(Path.GetDirectoryName(item.path).Replace('\\', '/'));
                }
            }
            finally
            {
                RemoveEmptyFolders(root, folders, batch);
                SaveManifest(batch);
                AssetDatabase.Refresh();
            }
            // An installation is forgotten once none of its files is left in the project.
            var entries = GalleryLedger.Entries.Where(e => report.Finished.Contains(e.installId)).ToList();
            GalleryLedger.Forget(entries.Where(e => e.files.All(f => string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(f.guid)))).Select(e => e.installId));
            return batch;
        }

        // A batch of its own (a second cleanup in the same second gets another), its manifest written at once.
        private static Batch NewBatch()
        {
            for (int attempt = 0; ; attempt++)
            {
                var batch = new Batch { id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6), createdAt = DateTime.UtcNow.Ticks };
                batch.folder = Path.Combine(QuarantineRoot, batch.id);
                if (Directory.Exists(batch.folder))
                {
                    if (attempt > 8) throw new IOException("No free quarantine folder in " + QuarantineRoot + ".");
                    continue;
                }
                Directory.CreateDirectory(batch.folder);
                using (var stream = new FileStream(Path.Combine(batch.folder, Manifest), FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream)) writer.Write(Newtonsoft.Json.JsonConvert.SerializeObject(batch));
                return batch;
            }
        }

        private static void SaveManifest(Batch batch)
        {
            string path = Path.Combine(batch.folder, Manifest), temp = path + ".tmp";
            File.WriteAllText(temp, Newtonsoft.Json.JsonConvert.SerializeObject(batch));
            File.Copy(temp, path, true);
            File.Delete(temp);
        }

        // Folders the move left empty go too, never Assets itself; their .meta (their GUID) goes into the batch.
        private static void RemoveEmptyFolders(string root, IEnumerable<string> folders, Batch batch)
        {
            foreach (string start in folders.OrderByDescending(f => f.Length))
                for (string folder = start; !string.IsNullOrEmpty(folder) && folder.StartsWith("Assets/", StringComparison.Ordinal); folder = Path.GetDirectoryName(folder)?.Replace('\\', '/'))
                {
                    string full = Path.Combine(root, folder);
                    if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) break;
                    try
                    {
                        if (File.Exists(full + ".meta"))
                        {
                            string target = Path.Combine(batch.folder, folder + ".meta");
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.Move(full + ".meta", target);
                        }
                        Directory.Delete(full);
                        batch.folders.Add(folder);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { break; }
                }
        }

        internal static List<Batch> Batches()
        {
            var result = new List<Batch>();
            if (!Directory.Exists(QuarantineRoot)) return result;
            foreach (string folder in Directory.GetDirectories(QuarantineRoot))
            {
                try
                {
                    var batch = Newtonsoft.Json.JsonConvert.DeserializeObject<Batch>(File.ReadAllText(Path.Combine(folder, Manifest)));
                    if (batch == null) continue;
                    batch.folder = folder;
                    result.Add(batch);
                }
                catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException) { }
            }
            return result.OrderByDescending(b => b.createdAt).ToList();
        }

        /// <summary>
        /// Moves a batch back where it was: every file in its quarantine folder (the batch's files with their .meta, and the
        /// .meta of the folders it emptied), a file and its .meta always together. Returns what could not go back
        /// (something is at its place now); the batch keeps it, and goes once it is empty.
        /// </summary>
        internal static List<string> Restore(Batch batch)
        {
            string root = GalleryLedger.ProjectRoot;
            var blocked = new List<string>();
            string Rel(string full) => full.Substring(batch.folder.Length).TrimStart('\\', '/').Replace('\\', '/');
            var held = Directory.Exists(batch.folder)
                ? new HashSet<string>(Directory.GetFiles(batch.folder, "*", SearchOption.AllDirectories).Select(Rel).Where(r => r != Manifest && !r.EndsWith(".tmp", StringComparison.Ordinal)), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guids = batch.items.Where(i => i.path != null).GroupBy(i => i.path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().guid, StringComparer.OrdinalIgnoreCase);
            // Folders first (shortest first), with their own GUID, so the files land in the folders they left.
            foreach (string meta in held.Where(r => r.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !held.Contains(r.Substring(0, r.Length - 5))).OrderBy(r => r.Length).ToList())
            {
                string folder = meta.Substring(0, meta.Length - 5), target = Path.Combine(root, meta);
                if (File.Exists(Path.Combine(root, folder))) continue; // the .meta of a file whose content stayed in the project
                if (File.Exists(target)) { blocked.Add(folder + " (the folder is back with another .meta)"); continue; }
                Directory.CreateDirectory(Path.Combine(root, folder));
                File.Move(Path.Combine(batch.folder, meta), target);
                held.Remove(meta);
            }
            foreach (string path in held.Where(r => !r.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string source = Path.Combine(batch.folder, path), target = Path.Combine(root, path);
                bool withMeta = held.Contains(path + ".meta");
                if (File.Exists(target) || Directory.Exists(target)) { blocked.Add(path); continue; }
                if (File.Exists(target + ".meta"))
                {
                    // Only its own .meta, left in the project when it moved, may be beside it.
                    if (withMeta || !guids.TryGetValue(path, out string guid) || !string.Equals(MetaGuid(target + ".meta"), guid, StringComparison.OrdinalIgnoreCase)) { blocked.Add(path + " (another .meta is at its place)"); continue; }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(source, target);
                if (withMeta)
                {
                    try { File.Move(source + ".meta", target + ".meta"); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        File.Move(target, source);
                        blocked.Add(path + " (" + ex.Message + ")");
                        continue;
                    }
                    held.Remove(path + ".meta");
                }
                held.Remove(path);
            }
            // A .meta left alone in the batch whose file is in the project was restored with its file already.
            if (held.Count == 0) Directory.Delete(batch.folder, true);
            else
            {
                batch.items.RemoveAll(i => !held.Contains(i.path));
                batch.folders.RemoveAll(f => !held.Contains(f + ".meta"));
                SaveManifest(batch);
            }
            AssetDatabase.Refresh();
            return blocked;
        }

        // The GUID a .meta file gives its asset, or null.
        private static string MetaGuid(string meta)
        {
            try
            {
                foreach (string line in File.ReadLines(meta))
                    if (line.StartsWith("guid:", StringComparison.Ordinal)) return line.Substring(5).Trim();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>Deletes a quarantined batch for good (the user's explicit choice).</summary>
        internal static void Delete(Batch batch)
        {
            string full = Path.GetFullPath(batch.folder), quarantine = Path.GetFullPath(QuarantineRoot);
            if (full.StartsWith(quarantine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
