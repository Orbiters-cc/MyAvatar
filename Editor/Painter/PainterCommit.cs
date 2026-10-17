using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// The maps Substance 3D Painter exported when its project was saved, applied to the avatar as a texture drop applies
    /// textures (<see cref="TextureChanges.Apply"/>: material copies, packed maps, one Undo step, Save and checkpoint). Each
    /// map is imported into the link's folder under a stable name: the next save replaces the same files, and the same
    /// material copies are edited again while no other texture set was dropped meanwhile.
    /// </summary>
    internal static class PainterCommit
    {
        private const string Root = "Assets/Orbiters/MyAvatar/";

        /// <summary>Applies the maps; how many were applied. <paramref name="resolve"/> gives a texture set's material.</summary>
        internal static int Apply(MyAvatar avatar, IEnumerable<PainterLinkProtocol.CommitMap> maps, Func<string, (Material material, PainterTargets targets)> resolve)
        {
            string folder = BatchFolder(avatar);
            string images = folder + "/Painter";
            TextureImport.EnsureFolder(images);
            var entries = new List<TextureEntry>();
            foreach (var map in maps)
            {
                if (string.IsNullOrEmpty(map.path) || !File.Exists(map.path)) continue;
                var (material, targets) = resolve(map.textureSet);
                if (!material || targets == null) continue;
                var (role, property) = targets.Slot(map.map);
                if (property == null) continue;
                string asset = images + "/" + ObjWriter.Clean(map.textureSet) + "_" + Label(map.map) + ".png";
                File.Copy(map.path, asset, true);
                var texture = Import(asset, map.map);
                if (!texture) continue;
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
            return applied;
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
