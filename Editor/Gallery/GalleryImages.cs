using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Gallery pictures: loaded once per editor session, kept on disk under Library for the next one, and faded in on the
    /// element that asked (it shows its own placeholder meanwhile). Only https URLs, or the local development server.
    /// </summary>
    internal static class GalleryImages
    {
        private static readonly Dictionary<string, Texture2D> Memory = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Task<Texture2D>> Loading = new Dictionary<string, Task<Texture2D>>();
        private static string Folder => Path.Combine(LibraryStore.Folder, "Gallery", "Images");
        internal const string ShownClass = "gallery-image--shown";

        /// <summary>Shows <paramref name="url"/> on <paramref name="image"/> once it is loaded.</summary>
        internal static async void Show(Image image, string url)
        {
            if (image == null || string.IsNullOrEmpty(url)) return;
            image.userData = url;
            var texture = await LoadAsync(url);
            if (texture == null || !Equals(image.userData, url)) return;
            image.image = texture;
            image.AddToClassList(ShownClass);
        }

        internal static Task<Texture2D> LoadAsync(string url)
        {
            if (Memory.TryGetValue(url, out var known) && known) return Task.FromResult(known);
            if (Loading.TryGetValue(url, out var running)) return running;
            var task = Fetch(url);
            Loading[url] = task;
            return task;
        }

        private static async Task<Texture2D> Fetch(string url)
        {
            try
            {
                if (!Allowed(url)) return null;
                string cached = Path.Combine(Folder, Hash(url) + ".png");
                byte[] bytes = null;
                if (File.Exists(cached) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < TimeSpan.FromDays(7)) bytes = File.ReadAllBytes(cached);
                if (bytes == null)
                {
                    using (var request = UnityWebRequest.Get(url))
                    {
                        request.timeout = 20;
                        var operation = request.SendWebRequest();
                        while (!operation.isDone) await Task.Yield();
                        if (request.result != UnityWebRequest.Result.Success) return null;
                        bytes = request.downloadHandler.data;
                    }
                }
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
                if (!texture.LoadImage(bytes, false)) { UnityEngine.Object.DestroyImmediate(texture); return null; }
                try { Directory.CreateDirectory(Folder); File.WriteAllBytes(cached, bytes); }
                catch (IOException) { }
                Memory[url] = texture;
                return texture;
            }
            catch (Exception) { return null; }
            finally { Loading.Remove(url); }
        }

        private static bool Allowed(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps ||
                (uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "localhost" || uri.Host == "127.0.0.1")));

        private static string Hash(string value)
        {
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}
