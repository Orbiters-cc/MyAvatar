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
    /// installation in the project or in an open scene. Whenever that cannot be checked, the file stays. Files are moved to
    /// a quarantine in Library first, from which they can be restored.
    /// </summary>
    internal static class GalleryCleanup
    {
        internal sealed class Item
        {
            public string guid, path, reason, from;
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
            public long Bytes => items.Sum(i => i.bytes);
        }

        private static string QuarantineRoot => Path.Combine(LibraryStore.Folder, "Gallery", "Quarantine");

        internal static async Task<Report> ScanAsync(Action<float, string> progress, CancellationToken cancellation)
        {
            var report = new Report();
            var entries = GalleryLedger.Entries;
            string root = GalleryLedger.ProjectRoot;
            // Installations still worn in an open scene count as installed, also after an Undo brought them back.
            var worn = new HashSet<string>(OpenSceneAttachments().Select(a => a.gallery.installId).Where(id => !string.IsNullOrEmpty(id)));
            var active = entries.Where(e => e.removedAt == 0 || worn.Contains(e.installId)).ToList();
            var removed = entries.Where(e => !active.Contains(e)).ToList();
            var preexisting = new HashSet<string>(entries.SelectMany(e => e.preexisting).Select(f => f.guid), StringComparer.OrdinalIgnoreCase);
            var activeFiles = active.SelectMany(e => e.files.Select(f => (f.guid, e))).GroupBy(p => p.guid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().e, StringComparer.OrdinalIgnoreCase);

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
                        string hash = await Task.Run(() => UnityPackageFiles.FileHash(Path.Combine(root, path)), cancellation);
                        if (!string.Equals(hash, file.hash, StringComparison.OrdinalIgnoreCase)) reason = "It was changed since it was installed.";
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
                // Files only used by other files being removed go together; anything used from outside that set stays.
                var removable = new HashSet<string>(open.Select(i => i.path), StringComparer.OrdinalIgnoreCase);
                for (bool changed = true; changed;)
                {
                    changed = false;
                    foreach (var item in open.Where(i => removable.Contains(i.path)).ToList())
                    {
                        string reason = usedInScenes.Contains(item.path) ? "It is used in an open scene."
                            : references.UsersOf(item.path, removable) is var users && users.Count > 0 ? "Used by " + Describe(users) + "." : null;
                        if (reason == null) continue;
                        item.reason = reason;
                        removable.Remove(item.path);
                        report.Kept.Add(item);
                        changed = true;
                    }
                }
                report.Reclaim.AddRange(open.Where(i => removable.Contains(i.path)));
            }
            var reclaimed = new HashSet<string>(report.Reclaim.Select(i => i.guid), StringComparer.OrdinalIgnoreCase);
            report.Finished.AddRange(removed.Where(e => e.files.All(f => reclaimed.Contains(f.guid) || string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(f.guid)))).Select(e => e.installId));
            progress?.Invoke(1f, "Done");
            return report;
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

        /// <summary>Moves the reclaimable files (and their .meta) to a quarantine batch in Library; Restore brings them back.</summary>
        internal static Batch Quarantine(Report report)
        {
            if (report == null || report.Reclaim.Count == 0) return null;
            string root = GalleryLedger.ProjectRoot;
            var batch = new Batch { id = DateTime.Now.ToString("yyyyMMdd-HHmmss"), createdAt = DateTime.UtcNow.Ticks };
            batch.folder = Path.Combine(QuarantineRoot, batch.id);
            AssetDatabase.ReleaseCachedFileHandles();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var item in report.Reclaim)
                {
                    string source = Path.Combine(root, item.path), target = Path.Combine(batch.folder, item.path);
                    if (!File.Exists(source)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(source, target);
                    if (File.Exists(source + ".meta")) File.Move(source + ".meta", target + ".meta");
                    batch.items.Add(item);
                    folders.Add(Path.GetDirectoryName(item.path).Replace('\\', '/'));
                }
            }
            finally
            {
                Directory.CreateDirectory(batch.folder);
                File.WriteAllText(Path.Combine(batch.folder, "batch.json"), Newtonsoft.Json.JsonConvert.SerializeObject(batch));
                RemoveEmptyFolders(root, folders);
                AssetDatabase.Refresh();
            }
            GalleryLedger.Forget(report.Finished);
            return batch;
        }

        // Folders the move left empty go too (with their .meta), never Assets itself.
        private static void RemoveEmptyFolders(string root, IEnumerable<string> folders)
        {
            foreach (string start in folders.OrderByDescending(f => f.Length))
                for (string folder = start; !string.IsNullOrEmpty(folder) && folder.StartsWith("Assets/", StringComparison.Ordinal); folder = Path.GetDirectoryName(folder)?.Replace('\\', '/'))
                {
                    string full = Path.Combine(root, folder);
                    if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) break;
                    Directory.Delete(full);
                    if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
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
                    var batch = Newtonsoft.Json.JsonConvert.DeserializeObject<Batch>(File.ReadAllText(Path.Combine(folder, "batch.json")));
                    if (batch == null) continue;
                    batch.folder = folder;
                    result.Add(batch);
                }
                catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException) { }
            }
            return result.OrderByDescending(b => b.createdAt).ToList();
        }

        /// <summary>Moves a batch back where it was. Returns the files that could not go back (something is there now).</summary>
        internal static List<string> Restore(Batch batch)
        {
            string root = GalleryLedger.ProjectRoot;
            var blocked = new List<string>();
            foreach (var item in batch.items)
            {
                string source = Path.Combine(batch.folder, item.path), target = Path.Combine(root, item.path);
                if (!File.Exists(source)) continue;
                if (File.Exists(target)) { blocked.Add(item.path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(source, target);
                if (File.Exists(source + ".meta") && !File.Exists(target + ".meta")) File.Move(source + ".meta", target + ".meta");
            }
            if (blocked.Count == 0) Directory.Delete(batch.folder, true);
            AssetDatabase.Refresh();
            return blocked;
        }

        /// <summary>Deletes a quarantined batch for good (the user's explicit choice).</summary>
        internal static void Delete(Batch batch)
        {
            string full = Path.GetFullPath(batch.folder), quarantine = Path.GetFullPath(QuarantineRoot);
            if (full.StartsWith(quarantine + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
