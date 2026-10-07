using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// The pictures a creator takes for a listing (photoshoot shots and ref sheets): PNGs under Library until they are
    /// published, and their textures while the creator window shows them.
    /// </summary>
    internal static class GalleryPictures
    {
        private const string SheetPrefix = "refsheet-";
        private static readonly Dictionary<string, Texture2D> Loaded = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        internal static string Folder => Path.Combine(LibraryStore.Folder, "Gallery", "Pictures");

        /// <summary>Saves <paramref name="image"/> as a new picture and returns its path. An image from the project is copied as is.</summary>
        internal static string Save(Texture2D image, bool sheet)
        {
            string asset = AssetDatabase.GetAssetPath(image);
            if (!string.IsNullOrEmpty(asset) && IsImageFile(asset)) return Import(Path.GetFullPath(asset), sheet);
            string path = NewPath(sheet, ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            return path;
        }

        /// <summary>Copies a PNG or JPEG file from disk as a new picture.</summary>
        internal static string Import(string file, bool sheet)
        {
            if (!IsImageFile(file)) throw new InvalidOperationException("Choose a PNG or JPEG picture.");
            string path = NewPath(sheet, Path.GetExtension(file).ToLowerInvariant());
            File.Copy(file, path);
            return path;
        }

        private static string NewPath(bool sheet, string extension)
        {
            Directory.CreateDirectory(Folder);
            return Path.Combine(Folder, (sheet ? SheetPrefix : "picture-") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + extension);
        }

        internal static bool IsSheet(string path) => !string.IsNullOrEmpty(path) && Path.GetFileName(path).StartsWith(SheetPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>The picture's texture, loaded once; null once its file is gone.</summary>
        internal static Texture2D Load(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Loaded.TryGetValue(path, out var known) && known) return known;
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, anisoLevel = 4, name = Path.GetFileNameWithoutExtension(path) };
            if (!texture.LoadImage(File.ReadAllBytes(path), true))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return null;
            }
            Loaded[path] = texture;
            return texture;
        }

        /// <summary>Removes a picture this tool made (never a file chosen elsewhere).</summary>
        internal static void Delete(string path)
        {
            Unload(path);
            if (Owned(path) && File.Exists(path)) File.Delete(path);
        }

        /// <summary>Frees the textures shown; their files stay.</summary>
        internal static void UnloadAll()
        {
            foreach (var texture in Loaded.Values) if (texture) UnityEngine.Object.DestroyImmediate(texture);
            Loaded.Clear();
        }

        internal static bool Owned(string path) =>
            !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void Unload(string path)
        {
            if (string.IsNullOrEmpty(path) || !Loaded.TryGetValue(path, out var texture)) return;
            if (texture) UnityEngine.Object.DestroyImmediate(texture);
            Loaded.Remove(path);
        }

        private static bool IsImageFile(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg";
        }
    }
}
