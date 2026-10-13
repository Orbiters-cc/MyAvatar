using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Builds the package a creator publishes for one variant, from prefabs or from a .unitypackage. Left out: what VPM
    /// installs (anything under Packages/, which becomes a declared dependency instead), source files (.blend, .spp, a .psd
    /// nothing in the package uses, .psb) and paid shaders such as Poiyomi Pro (a material using one blocks publishing: switch it to the free Poiyomi Toon).
    /// The report gives the package size, its code, its dependencies and the parameter memory it adds to an avatar.
    /// </summary>
    internal static class GalleryPackager
    {
        internal const long MaxBytes = 300L * 1024 * 1024;
        private static readonly string[] SourceExtensions = { ".blend", ".blend1", ".spp", ".psd", ".psb", ".max", ".ma", ".mb", ".ztl", ".kra", ".xcf" };
        // Assets that can point at a texture: their text tells whether a .psd in a .unitypackage is used.
        private static readonly string[] ReferenceHolders = { ".mat", ".prefab", ".asset", ".controller", ".overrideController", ".anim" };

        internal sealed class Report
        {
            public string packagePath, sha256;
            public long bytes;
            public int files, parameterBits;
            public bool containsCode;
            public List<string> codeFiles = new List<string>();
            public List<string> excluded = new List<string>();
            public List<string> warnings = new List<string>();
            public List<string> errors = new List<string>();
            public List<GalleryDependency> dependencies = new List<GalleryDependency>();
            /// <summary>Prefabs the package holds, by GUID, for the setups to choose from.</summary>
            public List<GallerySetupPrefab> prefabs = new List<GallerySetupPrefab>();
            [JsonIgnore] public bool Publishable => errors.Count == 0 && bytes > 0 && bytes <= MaxBytes;
        }

        internal static string Folder => Path.Combine(LibraryStore.Folder, "Gallery", "Builds");

        // ---- From prefabs in the project ----

        internal static Report FromPrefabs(IReadOnlyList<GameObject> prefabs, string name, MyAvatar avatar)
        {
            var report = new Report();
            var roots = prefabs.Where(p => p != null).Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
            if (roots.Count == 0) { report.errors.Add("Choose at least one prefab from the project."); return report; }
            var paths = AssetDatabase.GetDependencies(roots.ToArray(), true).Distinct().ToList();
            var kept = new List<string>();
            foreach (string path in paths)
            {
                if (path.StartsWith("Packages/", StringComparison.Ordinal)) { Depend(report, path); continue; }
                // Everything here is a dependency of the prefabs: a .psd among them is a texture they use.
                string reason = Excluded(path, used: true);
                if (reason != null) { report.excluded.Add(path + " · " + reason); continue; }
                kept.Add(path);
            }
            // Props Toolkit fits to the avatar (a drawing pen, a hand screen) need a Toolkit that knows them.
            var toolkit = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AttachmentHooks).Assembly);
            if (toolkit != null && prefabs.Any(AttachmentHooks.Fits)) Depend(report, toolkit.assetPath);
            CheckShaders(report, paths);
            if (report.errors.Count > 0) return report;
            Directory.CreateDirectory(Folder);
            report.packagePath = Path.Combine(Folder, Safe(name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unitypackage");
            AssetDatabase.ExportPackage(kept.ToArray(), report.packagePath, ExportPackageOptions.Default);
            Finish(report, kept, avatar, roots.Select(AssetDatabase.AssetPathToGUID));
            return report;
        }

        // ---- From a .unitypackage ----

        internal static async Task<Report> FromPackageAsync(string source, string name, MyAvatar avatar)
        {
            var report = new Report();
            UnityPackageIndex index;
            HashSet<string> used;
            try
            {
                index = await Task.Run(() => UnityPackageIndex.Read(source));
                used = await Task.Run(() => UsedPsds(source, index));
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is EndOfStreamException) { report.errors.Add("Not a readable Unity package: " + ex.Message); return report; }
            if (index.UnsafePaths.Count > 0) { report.errors.Add("The package would write files outside Assets and Packages."); return report; }
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in index.Entries)
            {
                if (entry.Path.StartsWith("Packages/", StringComparison.Ordinal)) { Depend(report, entry.Path); report.excluded.Add(entry.Path + " · installed by VPM"); continue; }
                string reason = Excluded(entry.Path, used.Contains(entry.Guid));
                if (reason != null) { report.excluded.Add(entry.Path + " · " + reason); continue; }
                keep.Add(entry.Guid);
            }
            // Materials of the project with the same GUIDs tell which shaders they use.
            CheckShaders(report, index.Entries.Where(e => keep.Contains(e.Guid)).Select(e => AssetDatabase.GUIDToAssetPath(e.Guid)).Where(p => !string.IsNullOrEmpty(p)));
            if (report.errors.Count > 0) return report;
            Directory.CreateDirectory(Folder);
            report.packagePath = Path.Combine(Folder, Safe(name) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unitypackage");
            if (keep.Count == index.Entries.Count) File.Copy(source, report.packagePath, true);
            else await Task.Run(() => UnityPackageFiles.CopyEntries(source, report.packagePath, keep));
            var kept = index.Entries.Where(e => keep.Contains(e.Guid)).ToList();
            report.prefabs = kept.Where(e => e.Path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Select(e => new GallerySetupPrefab { guid = e.Guid, path = e.Path, name = Path.GetFileNameWithoutExtension(e.Path) }).ToList();
            Finish(report, kept.Select(e => e.Path).ToList(), avatar, report.prefabs.Select(p => p.guid), fromIndex: true);
            return report;
        }

        private static void Finish(Report report, List<string> paths, MyAvatar avatar, IEnumerable<string> prefabGuids, bool fromIndex = false)
        {
            var info = new FileInfo(report.packagePath);
            report.bytes = info.Exists ? info.Length : 0;
            report.sha256 = info.Exists ? UnityPackageFiles.FileHash(report.packagePath) : null;
            report.files = paths.Count;
            report.codeFiles = CodeContent.Filter(paths);
            report.containsCode = report.codeFiles.Count > 0;
            if (!fromIndex)
                report.prefabs = prefabGuids.Select(g => new GallerySetupPrefab { guid = g, path = AssetDatabase.GUIDToAssetPath(g), name = Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) }).ToList();
            if (report.bytes > MaxBytes) report.errors.Add($"The package is {GalleryUI.Size(report.bytes)}; the gallery accepts up to {GalleryUI.Size(MaxBytes)} per package. Leave out unused textures or lower their sizes.");
            report.parameterBits = ParameterCost(avatar, report.prefabs.Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(p.guid))).Where(p => p != null).ToList());
        }

        // Unity imports a .psd as a texture: one the package's content uses goes in like any texture. A .psb needs the 2D PSD
        // Importer, which buyers may not have.
        internal static string Excluded(string path, bool used)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".psd" && used) return null;
            if (ext == ".psd") return "source file nothing in the package uses";
            if (ext == ".psb") return "source file (.psb needs the 2D PSD Importer: export it as PNG to include it)";
            if (SourceExtensions.Contains(ext)) return "source file";
            if (PoiyomiPro(path)) return "Poiyomi Pro is paid: buyers need the free Poiyomi Toon";
            return null;
        }

        // The .psd files a material, prefab or animation of the package points at (by GUID in its text): a second pass over
        // the package reads those files whole, as they stream by (a big Poiyomi material included).
        internal static HashSet<string> UsedPsds(string package, UnityPackageIndex index)
        {
            var psds = new HashSet<string>(index.Entries.Where(e => e.Path.EndsWith(".psd", StringComparison.OrdinalIgnoreCase)).Select(e => e.Guid), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (psds.Count == 0) return used;
            var holders = new HashSet<string>(index.Entries.Where(e => ReferenceHolders.Contains(Path.GetExtension(e.Path), StringComparer.OrdinalIgnoreCase)).Select(e => e.Guid), StringComparer.OrdinalIgnoreCase);
            UnityPackageReader.Read(package, null, record =>
            {
                if (record.Part != UnityPackageReader.Part.Asset || !holders.Contains(record.Guid) || used.Count == psds.Count) return;
                used.UnionWith(GuidsIn(record.Content, psds));
            });
            return used;
        }

        /// <summary>Which of <paramref name="wanted"/> a Unity text asset names ("guid: …"), read in pieces from <paramref name="content"/>.</summary>
        internal static HashSet<string> GuidsIn(Stream content, ICollection<string> wanted, int chunk = 64 * 1024)
        {
            const string Key = "guid: ";
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[chunk];
            string carry = "";
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                // GUIDs are ASCII: one character per byte, and what a piece ends with is looked at again with the next.
                string text = carry + System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                for (int at = text.IndexOf(Key, StringComparison.Ordinal); at >= 0 && at + Key.Length + 32 <= text.Length; at = text.IndexOf(Key, at + 1, StringComparison.Ordinal))
                {
                    string guid = text.Substring(at + Key.Length, 32);
                    if (wanted.Contains(guid)) found.Add(guid);
                }
                carry = text.Length > Key.Length + 32 ? text.Substring(text.Length - (Key.Length + 32)) : text;
            }
            return found;
        }

        private static bool PoiyomiPro(string path) =>
            path.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0 && path.IndexOf("pro", StringComparison.OrdinalIgnoreCase) >= 0 &&
            (path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cginc", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".orlsource", StringComparison.OrdinalIgnoreCase) ||
             path.IndexOf("/_PoiyomiShaders/", StringComparison.OrdinalIgnoreCase) >= 0);

        // Materials stay (they are the creator's), but one needing Poiyomi Pro would not render for buyers.
        private static void CheckShaders(Report report, IEnumerable<string> paths)
        {
            foreach (string path in paths.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null || material.shader == null) continue;
                string shader = material.shader.name, shaderPath = AssetDatabase.GetAssetPath(material.shader);
                if (shader.IndexOf("Poiyomi Pro", StringComparison.OrdinalIgnoreCase) >= 0 || PoiyomiPro(shaderPath))
                    report.errors.Add($"{Path.GetFileName(path)} uses {shader}. Poiyomi Pro is paid: switch the material to the free Poiyomi Toon before publishing.");
                else if (material.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal))
                    report.warnings.Add($"{Path.GetFileName(path)} uses a locked shader: buyers without it see a pink material. Unlock it before publishing.");
            }
        }

        // A file under Packages/<folder>/ comes from that package: the variant needs it (VPM installs it, never the gallery).
        private static void Depend(Report report, string path)
        {
            var parts = path.Split('/');
            if (parts.Length < 2) return;
            string folder = parts[1];
            if (report.dependencies.Any(d => d.id == folder) || folder.StartsWith("com.unity.", StringComparison.Ordinal)) return;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + folder);
            string id = info?.name ?? folder;
            if (id.StartsWith("com.unity.", StringComparison.Ordinal) || report.dependencies.Any(d => d.id == id)) return;
            report.dependencies.Add(new GalleryDependency { id = id, displayName = info?.displayName ?? id, range = !string.IsNullOrEmpty(info?.version) ? ">=" + info.version : "*" });
        }

        /// <summary>
        /// Parameter memory the prefabs add: placed under the avatar for a moment, measured, and removed (outside Undo).
        /// </summary>
        internal static int ParameterCost(MyAvatar avatar, List<GameObject> prefabs)
        {
            if (!avatar || prefabs.Count == 0) return 0;
            int before = AvatarParameterBudget.Estimate(avatar.gameObject).TotalBeforeCompression;
            var placed = new List<GameObject>();
            try
            {
                foreach (var prefab in prefabs) { var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform); instance.hideFlags = HideFlags.DontSave; placed.Add(instance); }
                return Math.Max(0, AvatarParameterBudget.Estimate(avatar.gameObject).TotalBeforeCompression - before);
            }
            catch (Exception) { return 0; }
            finally { foreach (var instance in placed) if (instance) UnityEngine.Object.DestroyImmediate(instance); }
        }

        private static string Safe(string name)
        {
            string safe = new string((name ?? "asset").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            return string.IsNullOrEmpty(safe) ? "asset" : safe.Length > 60 ? safe.Substring(0, 60) : safe;
        }
    }
}
