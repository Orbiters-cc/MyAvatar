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

        internal static async Task<List<TextureEntry>> ImportAsync(string[] paths, string folder, Action<string> progress, CancellationToken token)
        {
            var result = new List<TextureEntry>();
            Directory.CreateDirectory(folder + "/Textures");
            AssetDatabase.Refresh();
            for (int i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                progress($"Importing {i + 1} / {paths.Length} · {Path.GetFileName(paths[i])}");
                string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/Textures/" + Path.GetFileName(paths[i]));
                string source = paths[i];
                await Task.Run(() => File.Copy(source, path, false), token);
                token.ThrowIfCancellationRequested();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                string role = TextureMatching.Role(Path.GetFileNameWithoutExtension(source));
                if (importer != null)
                {
                    importer.textureType = role == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                    importer.sRGBTexture = role == "color" || role == "emission" || role == "unknown";
                    importer.isReadable = false;
                    importer.SaveAndReimport();
                }
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (!texture) throw new InvalidOperationException("Unity could not import " + Path.GetFileName(source));
                result.Add(new TextureEntry { texture = texture, fileName = Path.GetFileName(source), role = role });
                await Task.Yield();
            }
            return result;
        }

        internal static Texture2D Preview(Texture source, int size = 128)
        {
            int w = Math.Max(1, Math.Min(size, source.width));
            int h = Math.Max(1, Mathf.RoundToInt(source.height * (w / (float)source.width)));
            if (h > size) { w = Math.Max(1, Mathf.RoundToInt(w * (size / (float)h))); h = size; }
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(source, rt); RenderTexture.active = rt;
                copy = new Texture2D(w, h, TextureFormat.RGB24, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0); copy.Apply(); return copy;
            }
            catch { if (copy) UnityEngine.Object.DestroyImmediate(copy); throw; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
        }
    }
}
