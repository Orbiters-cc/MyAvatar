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
        /// <summary>Where the project's own pictures may be taken from: its Assets folder (another folder in tests).</summary>
        internal static string ProjectPictures = "Assets";

        /// <summary>Saves <paramref name="image"/> as a new picture and returns its path. An image from the project is taken from its file.</summary>
        internal static string Save(Texture2D image, bool sheet)
        {
            string asset = AssetDatabase.GetAssetPath(image);
            if (!string.IsNullOrEmpty(asset) && IsImageFile(asset)) return Import(Path.GetFullPath(asset), sheet);
            string path = NewPath(sheet, ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            return path;
        }

        /// <summary>
        /// A PNG or JPEG file from disk as a new picture, encoded again from its pixels: what the camera or editor wrote
        /// beside them (EXIF with GPS and device, XMP, comments) stays on this computer. A photo is turned upright first.
        /// </summary>
        internal static string Import(string file, bool sheet)
        {
            if (!IsImageFile(file)) throw new InvalidOperationException("Choose a PNG or JPEG picture.");
            byte[] bytes = File.ReadAllBytes(file);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(bytes)) throw new InvalidOperationException("This picture could not be read: choose a PNG or JPEG picture.");
                texture = Upright(texture, ExifOrientation(bytes));
                bool png = Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase);
                string path = NewPath(sheet, png ? ".png" : ".jpg");
                File.WriteAllBytes(path, png ? texture.EncodeToPNG() : texture.EncodeToJPG(92));
                return path;
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        /// <summary>The EXIF orientation of a JPEG (1 to 8; 1, upright, when it has none): Unity's LoadImage ignores it.</summary>
        internal static int ExifOrientation(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return 1;
            for (int at = 2; at + 4 <= jpeg.Length && jpeg[at] == 0xFF;)
            {
                int marker = jpeg[at + 1], length = (jpeg[at + 2] << 8) | jpeg[at + 3];
                if (marker == 0xDA || length < 2) break;
                int end = Math.Min(jpeg.Length, at + 2 + length);
                if (marker == 0xE1 && length >= 16 && jpeg[at + 4] == 'E' && jpeg[at + 5] == 'x' && jpeg[at + 6] == 'i' && jpeg[at + 7] == 'f')
                {
                    int tiff = at + 10;
                    bool little = jpeg[tiff] == 'I';
                    int U16(int i) => i + 2 > end ? -1 : little ? jpeg[i] | jpeg[i + 1] << 8 : jpeg[i] << 8 | jpeg[i + 1];
                    long U32(int i) => i + 4 > end ? -1L : (long)(uint)(little ? jpeg[i] | jpeg[i + 1] << 8 | jpeg[i + 2] << 16 | jpeg[i + 3] << 24 : jpeg[i] << 24 | jpeg[i + 1] << 16 | jpeg[i + 2] << 8 | jpeg[i + 3]);
                    long first = U32(tiff + 4);
                    if (first < 8 || tiff + first + 2 > end) return 1;
                    int entries = U16(tiff + (int)first);
                    for (int e = 0; e < entries; e++)
                    {
                        int entry = tiff + (int)first + 2 + e * 12;
                        if (entry + 12 > end) break;
                        if (U16(entry) == 0x0112) { int value = U16(entry + 8); return value >= 1 && value <= 8 ? value : 1; }
                    }
                    return 1;
                }
                at += 2 + length;
            }
            return 1;
        }

        /// <summary>The picture as it should be seen for its EXIF orientation (texture rows go from the bottom up).</summary>
        internal static Texture2D Upright(Texture2D texture, int orientation)
        {
            if (orientation <= 1 || orientation > 8) return texture;
            int w = texture.width, h = texture.height;
            bool turned = orientation >= 5;
            int outW = turned ? h : w, outH = turned ? w : h;
            var source = texture.GetPixels32();
            var pixels = new Color32[source.Length];
            for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
            {
                // From the top-left corner, as EXIF counts: where the shown pixel (x, y) lies in the stored picture.
                int sx, sy;
                switch (orientation)
                {
                    case 2: sx = w - 1 - x; sy = y; break;
                    case 3: sx = w - 1 - x; sy = h - 1 - y; break;
                    case 4: sx = x; sy = h - 1 - y; break;
                    case 5: sx = y; sy = x; break;
                    case 6: sx = y; sy = h - 1 - x; break;
                    case 7: sx = w - 1 - y; sy = h - 1 - x; break;
                    default: sx = w - 1 - y; sy = x; break;
                }
                pixels[(outH - 1 - y) * outW + x] = source[(h - 1 - sy) * w + sx];
            }
            var upright = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            upright.SetPixels32(pixels);
            upright.Apply();
            UnityEngine.Object.DestroyImmediate(texture);
            return upright;
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

        /// <summary>Removes a picture this tool made (never a file chosen elsewhere). One that can't be removed now stays, said in the Console.</summary>
        internal static void Delete(string path)
        {
            Unload(path);
            if (!Owned(path) || !File.Exists(path)) return;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { Debug.LogWarning("[My Avatar] Could not remove the gallery picture " + path + ": " + ex.Message); }
        }

        /// <summary>
        /// A picture for the listing from <paramref name="path"/>: one of this tool's pictures (checked to still be a PNG or
        /// JPEG picture), or a PNG or JPEG in the project's Assets, taken in as a new picture (<see cref="Import"/>). Anything
        /// else is refused: only pictures go to Orbiters.
        /// </summary>
        internal static string Adopt(string path, bool sheet = false)
        {
            string full = Check(path);
            return full == null || Owned(full) ? full : Import(full, sheet);
        }

        /// <summary>
        /// What <see cref="Adopt"/> would take, checked without changing anything: the full path of one of this tool's
        /// pictures (a readable PNG or JPEG) or of a PNG or JPEG in the project's Assets folder. Throws for anything else.
        /// </summary>
        internal static string Check(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { throw new ArgumentException("Not a picture path: " + path); }
            if (!IsImageFile(full)) throw new ArgumentException("Not a PNG or JPEG picture: " + path + ".");
            if (!File.Exists(full)) throw new ArgumentException("No picture at " + path + ".");
            if (Owned(full))
            {
                if (!Readable(full)) throw new ArgumentException(path + " is not a readable PNG or JPEG picture.");
                return full;
            }
            string assets = Path.GetFullPath(ProjectPictures) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pictures come from this tool's own (" + Folder + ") or from a PNG or JPEG in the project's Assets folder; " + path + " is neither.");
            return full;
        }

        // The file holds a picture Unity can read, whatever its name says.
        private static bool Readable(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try { return texture.LoadImage(File.ReadAllBytes(path)); }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        /// <summary>Frees the textures shown; their files stay.</summary>
        internal static void UnloadAll()
        {
            foreach (var texture in Loaded.Values) if (texture) UnityEngine.Object.DestroyImmediate(texture);
            Loaded.Clear();
        }

        internal static bool Owned(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }

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
