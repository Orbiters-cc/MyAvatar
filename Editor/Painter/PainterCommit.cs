using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.Textures;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// The maps Substance 3D Painter exported when its project was saved, applied to the avatar as a texture drop applies
    /// textures (<see cref="TextureChanges.Apply"/>: material copies, packed maps, one Undo step, Save and checkpoint). Each
    /// save's maps are imported into the link's folder as new files (<c>Skin_BaseColor.png</c>, then <c>Skin_BaseColor 2.png</c>...):
    /// Undo of a save shows the one before it again. The newest <see cref="KeptSaves"/> of each map stay; the same material
    /// copies are edited again while no other texture set was dropped meanwhile. Only maps Painter exported in the link's
    /// <c>commit/</c> folder are read.
    /// </summary>
    internal static class PainterCommit
    {
        private const string Root = "Assets/Orbiters/MyAvatar/";
        /// <summary>How many saves of a map stay in the batch (so Undo can go back that far).</summary>
        internal const int KeptSaves = 4;

        /// <summary>Where Painter exports the maps of a save (<c>commit/&lt;pid&gt;/&lt;time&gt;/</c>).</summary>
        internal static string Exports => Path.Combine(PainterLinkService.LinkFolder, "commit");

        /// <summary>Applies the maps; how many were applied. <paramref name="resolve"/> gives a texture set's material.</summary>
        internal static int Apply(MyAvatar avatar, IEnumerable<PainterLinkProtocol.CommitMap> maps, Func<string, (Material material, PainterTargets targets)> resolve)
        {
            string folder = BatchFolder(avatar);
            string images = folder + "/Painter";
            bool made = false;
            var entries = new List<TextureEntry>();
            var painted = new List<AvatarTextureEdits.Painted>();
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string exports = Exports;
            foreach (var map in maps)
            {
                if (string.IsNullOrEmpty(map.path) || !PainterLinkService.Inside(map.path, exports) || !File.Exists(map.path)) continue;
                var (material, targets) = resolve(map.textureSet);
                if (!material || targets == null) continue;
                var (role, property) = targets.Slot(map.map);
                if (property == null) continue;
                // Two texture sets on one material (or one map twice): the first one's goes in that slot.
                if (entries.Any(e => e.material == material && e.property == property && e.role == role)) continue;
                // The batch's folder once there is something to put in it.
                if (!made) { TextureImport.EnsureFolder(images); made = true; }
                string stem = ObjWriter.Clean(map.textureSet) + "_" + Label(map.map);
                string asset = NextFile(images, stem, written);
                File.Copy(map.path, asset, false);
                var texture = Import(asset, map.map);
                if (!texture) continue;
                stems.Add(stem);
                // The slots it paints, found before the apply gives them material copies.
                foreach (var (renderer, slot) in PainterLivePreview.Parts(avatar.transform, material))
                    painted.Add(new AvatarTextureEdits.Painted { Renderer = renderer, Slot = slot, Channel = map.map, Path = Path.GetFullPath(asset), Final = true });
                entries.Add(new TextureEntry
                {
                    texture = texture, fileName = Path.GetFileName(asset), role = role, material = material, property = property,
                    reason = TextureMatching.ChosenReason, sourceKey = TextureMemory.SourceKey(asset),
                });
            }
            if (entries.Count == 0) return 0;
            // The same batch goes on (its material copies edited again) while the textures shown last are this link's. Only
            // what it still shows carries over: an entry undone (Undo in the drop field) is never applied again by a save.
            bool extend = avatar.batchFolder == folder && !avatar.canRedo;
            var list = extend
                ? avatar.textures.Where(e => e.applied && !entries.Any(n => n.material == e.material && n.property == e.property)).Concat(entries).ToList()
                : entries;
            int applied = TextureChanges.Apply(avatar, list, folder, new List<Transform>());
            // Where the next save goes (bookkeeping, outside the apply's Undo step).
            avatar.painter.textureFolder = folder;
            TextureChanges.Dirty(avatar);
            if (applied > 0)
            {
                painted.ForEach(AvatarTextureEdits.Notify);
                // The older saves go once the new ones are on the materials.
                Prune(images, stems, written);
            }
            return applied;
        }

        /// <summary>Deletes the folders Painter exported these maps to (once applied, or not: the next save exports again).</summary>
        internal static void Discard(IEnumerable<PainterLinkProtocol.CommitMap> maps)
        {
            string exports = Exports;
            foreach (string folder in maps.Where(m => m != null && !string.IsNullOrEmpty(m.path)).Select(m => Path.GetDirectoryName(m.path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                // commit/<pid>/<time> only.
                string pid = Path.GetDirectoryName(folder);
                if (!PainterLinkService.Inside(folder, exports) || !PainterLinkService.SamePath(Path.GetDirectoryName(pid), exports)) continue;
                try { Directory.Delete(folder, true); }
                catch (Exception) { }
            }
        }

        // "Skin_BaseColor.png", then "Skin_BaseColor 2.png", "… 3.png": one past the newest, never a name used in this save.
        private static string NextFile(string folder, string stem, HashSet<string> written)
        {
            int next = Saves(folder, stem).Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
            string path;
            for (; ; next++)
            {
                path = folder + "/" + stem + (next == 1 ? "" : " " + next) + ".png";
                if (!File.Exists(path) && written.Add(path)) return path;
            }
        }

        // The saves of one map in the folder, newest first.
        private static List<(string Path, int Number)> Saves(string folder, string stem)
        {
            var pattern = new Regex("^" + Regex.Escape(stem) + @"(?: (\d+))?\.png$", RegexOptions.IgnoreCase);
            if (!Directory.Exists(folder)) return new List<(string, int)>();
            return Directory.GetFiles(folder, "*.png")
                .Select(f => (Path: f.Replace('\\', '/'), Match: pattern.Match(Path.GetFileName(f))))
                .Where(p => p.Match.Success)
                .Select(p => (p.Path, Number: p.Match.Groups[1].Success && int.TryParse(p.Match.Groups[1].Value, out int n) ? n : 1))
                .OrderByDescending(p => p.Number).ToList();
        }

        // Older saves of each map than the newest few go to the Recycle Bin (Undo past them shows the slot empty; a material
        // pointed at one by hand can get it back from there).
        private static void Prune(string folder, IEnumerable<string> stems, HashSet<string> written)
        {
            var older = stems.SelectMany(stem => Saves(folder, stem).Skip(KeptSaves)).Select(s => s.Path).Where(p => !written.Contains(p)).ToArray();
            var failed = new List<string>();
            if (older.Length > 0 && !AssetDatabase.MoveAssetsToTrash(older, failed))
                Debug.LogWarning("[My Avatar] Substance Painter link: " + failed.Count + " older save(s) could not be moved to the Recycle Bin.");
        }

        // The link's batch folder while it is the avatar's current one and not undone; a new batch otherwise (another texture
        // set was dropped since, or the last save was undone: their materials and images stay as they are, for Redo).
        private static string BatchFolder(MyAvatar avatar)
        {
            string folder = avatar.painter.textureFolder;
            if (!string.IsNullOrEmpty(folder) && folder == avatar.batchFolder && !avatar.canRedo && AssetDatabase.IsValidFolder(folder)) return folder;
            return Root + Guid.NewGuid().ToString("N");
        }

        private static string Label(string map) => map switch
        {
            "basecolor" => "BaseColor",
            "normal" => "Normal",
            "roughness" => "Roughness",
            "metallic" => "Metallic",
            "emissive" => "Emissive",
            "ambientocclusion" => "AO",
            _ => map,
        };

        // Imported at its full size (Unity's default import halves 4K images), as a normal map or in sRGB where it is one.
        private static Texture2D Import(string asset, string map)
        {
            AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            if (!(AssetImporter.GetAtPath(asset) is TextureImporter importer)) return null;
            bool normal = map == "normal", srgb = map == "basecolor" || map == "emissive";
            int size = 2048;
            importer.GetSourceTextureWidthAndHeight(out int width, out int height);
            if (width > 0 && height > 0) size = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(width, height)), 32, 8192);
            bool changed = false;
            if (importer.textureType != (normal ? TextureImporterType.NormalMap : TextureImporterType.Default))
            {
                importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                changed = true;
            }
            if (!normal && importer.sRGBTexture != srgb) { importer.sRGBTexture = srgb; changed = true; }
            if (importer.maxTextureSize < size) { importer.maxTextureSize = size; changed = true; }
            if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; changed = true; }
            if (changed) importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(asset);
        }
    }
}
