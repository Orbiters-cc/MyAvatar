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
    /// Gallery pictures: kept on disk under Library for a week and faded in on the element that asked (it shows its own
    /// placeholder meanwhile). Only https URLs, or the local development server; never larger than a picture should be:
    /// a download stops at its size limit, and a picture's dimensions are read from its header before it is decoded.
    /// Members' faces (reviewers, creators) come only from Orbiters and the sign-in services it takes them from
    /// (<see cref="OrbitersEnvironment.IsProfilePictureHost"/>), and only Orbiters' own links may redirect them. In memory,
    /// the pictures shown most recently are kept up to a budget; one still shown somewhere stays.
    /// </summary>
    internal static class GalleryImages
    {
        private const long MaxPictureBytes = 16L * 1024 * 1024, MaxFaceBytes = 2L * 1024 * 1024, MemoryBudget = 256L * 1024 * 1024;
        private const int MaxPictureSize = 4096, MaxFaceSize = 1024, MaxRedirects = 3;
        internal const string ShownClass = "gallery-image--shown";

        private sealed class Entry
        {
            public Texture2D Texture;
            public long Bytes;
            // The images showing it: while one is alive and still shows it, it stays in memory.
            public readonly List<WeakReference<Image>> Shown = new List<WeakReference<Image>>();
        }

        private static readonly Dictionary<string, Entry> Memory = new Dictionary<string, Entry>();
        private static readonly LinkedList<string> Recent = new LinkedList<string>();
        private static readonly Dictionary<string, Task<Texture2D>> Loading = new Dictionary<string, Task<Texture2D>>();
        private static long used;
        private static string Folder => Path.Combine(LibraryStore.Folder, "Gallery", "Images");

        /// <summary>Shows <paramref name="url"/> on <paramref name="image"/> once it is loaded.</summary>
        internal static void Show(Image image, string url) => Show(image, url, face: false);

        /// <summary>Shows a member's profile picture (a reviewer's, a creator's) on <paramref name="image"/> once it is loaded.</summary>
        internal static void ShowAvatar(Image image, string url) => Show(image, url, face: true);

        private static async void Show(Image image, string url, bool face)
        {
            if (image == null || string.IsNullOrEmpty(url)) return;
            // Orbiters' own files may come as a path of its API ("/files/serve/12").
            url = OrbitersEnvironment.ResolveApiUrl(url);
            image.userData = url;
            string key = Key(url, face);
            var texture = await LoadAsync(url, face);
            if (texture == null || !Equals(image.userData, url)) return;
            image.image = texture;
            image.AddToClassList(ShownClass);
            if (Memory.TryGetValue(key, out var entry)) entry.Shown.Add(new WeakReference<Image>(image));
        }

        internal static Task<Texture2D> LoadAsync(string url, bool face = false)
        {
            string key = Key(url, face);
            if (Memory.TryGetValue(key, out var known) && known.Texture) { Touch(key); return Task.FromResult(known.Texture); }
            if (Loading.TryGetValue(key, out var running)) return running;
            var task = Fetch(url, key, face);
            Loading[key] = task;
            return task;
        }

        private static string Key(string url, bool face) => (face ? "face|" : "") + url;

        private static async Task<Texture2D> Fetch(string url, string key, bool face)
        {
            try
            {
                if (!Allowed(url, face)) return null;
                long limit = face ? MaxFaceBytes : MaxPictureBytes;
                int size = face ? MaxFaceSize : MaxPictureSize;
                string cached = Path.Combine(Folder, Hash(key) + ".png");
                byte[] bytes = null;
                var file = new FileInfo(cached);
                bool fromCache = file.Exists && file.Length <= limit && DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromDays(7);
                if (fromCache) bytes = File.ReadAllBytes(cached);
                else bytes = await Download(url, limit, face);
                if (bytes == null || !Dimensions(bytes, out int width, out int height) || width > size || height > size) return null;
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
                // Shown only: the CPU copy goes once the picture is on the graphics card.
                if (!texture.LoadImage(bytes, true)) { UnityEngine.Object.DestroyImmediate(texture); return null; }
                if (!fromCache)
                {
                    try { Directory.CreateDirectory(Folder); File.WriteAllBytes(cached, bytes); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                long bytesInMemory = (long)texture.width * texture.height * 4 * 4 / 3;
                // One destroyed elsewhere (a domain of its own, an editor quit) is counted out first.
                if (Memory.TryGetValue(key, out var old)) used -= old.Bytes;
                Memory[key] = new Entry { Texture = texture, Bytes = bytesInMemory };
                used += bytesInMemory;
                Touch(key);
                Trim();
                return texture;
            }
            catch (Exception) { return null; }
            finally { Loading.Remove(key); }
        }

        // The picture's bytes, stopped as soon as they go over the limit; redirects are followed by hand, each one checked:
        // a face only through Orbiters' own links (its storage), any picture only to https.
        private static async Task<byte[]> Download(string url, long limit, bool face)
        {
            string current = url;
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                var handler = new CappedDownload(limit);
                using (var request = new UnityWebRequest(current, UnityWebRequest.kHttpVerbGET, handler, null))
                {
                    request.timeout = 20;
                    request.redirectLimit = 0;
                    var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();
                    long code = request.responseCode;
                    if (code >= 300 && code < 400)
                    {
                        string location = request.GetResponseHeader("Location");
                        if (string.IsNullOrEmpty(location) || !Uri.TryCreate(current, UriKind.Absolute, out var from)) return null;
                        if (face && !OrbitersEnvironment.IsOrbitersHost(from.Host)) return null;
                        if (!Uri.TryCreate(from, location, out var next) || !Allowed(next.ToString(), false)) return null;
                        current = next.ToString();
                        continue;
                    }
                    if (request.result != UnityWebRequest.Result.Success || handler.TooBig) return null;
                    return handler.Bytes;
                }
            }
            return null;
        }

        // A download kept in memory once, refused past its limit (announced or arriving).
        private sealed class CappedDownload : DownloadHandlerScript
        {
            private readonly long limit;
            private readonly MemoryStream data = new MemoryStream();
            internal bool TooBig { get; private set; }

            internal CappedDownload(long limit) : base(new byte[64 * 1024]) { this.limit = limit; }

            protected override void ReceiveContentLengthHeader(ulong contentLength)
            {
                if ((long)Math.Min(contentLength, long.MaxValue) > limit) TooBig = true;
            }

            protected override bool ReceiveData(byte[] chunk, int length)
            {
                if (TooBig || data.Length + length > limit) { TooBig = true; return false; }
                data.Write(chunk, 0, length);
                return true;
            }

            internal byte[] Bytes => data.ToArray();
        }

        /// <summary>A PNG's or JPEG's width and height, read from its header; false for anything else.</summary>
        internal static bool Dimensions(byte[] bytes, out int width, out int height)
        {
            width = height = 0;
            if (bytes == null || bytes.Length < 24) return false;
            // PNG: the IHDR chunk follows the signature.
            if (bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            {
                width = bytes[16] << 24 | bytes[17] << 16 | bytes[18] << 8 | bytes[19];
                height = bytes[20] << 24 | bytes[21] << 16 | bytes[22] << 8 | bytes[23];
                return width > 0 && height > 0;
            }
            if (bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
            // JPEG: the first start-of-frame segment.
            for (int at = 2; at + 9 < bytes.Length;)
            {
                if (bytes[at] != 0xFF) return false;
                int marker = bytes[at + 1];
                if (marker == 0xFF) { at++; continue; }
                if (marker == 0x01 || marker >= 0xD0 && marker <= 0xD8) { at += 2; continue; }
                int length = bytes[at + 2] << 8 | bytes[at + 3];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = bytes[at + 5] << 8 | bytes[at + 6];
                    width = bytes[at + 7] << 8 | bytes[at + 8];
                    return width > 0 && height > 0;
                }
                if (marker == 0xDA || length < 2) return false;
                at += 2 + length;
            }
            return false;
        }

        private static void Touch(string key)
        {
            Recent.Remove(key);
            Recent.AddLast(key);
        }

        // The least recently shown go first, until memory is within budget; one an image still shows stays.
        private static void Trim()
        {
            for (var node = Recent.First; node != null && used > MemoryBudget;)
            {
                var next = node.Next;
                if (Memory.TryGetValue(node.Value, out var entry) && !Shown(entry))
                {
                    Recent.Remove(node);
                    Memory.Remove(node.Value);
                    used -= entry.Bytes;
                    if (entry.Texture) UnityEngine.Object.DestroyImmediate(entry.Texture);
                }
                node = next;
            }
        }

        private static bool Shown(Entry entry)
        {
            entry.Shown.RemoveAll(reference => !reference.TryGetTarget(out var image) || image.image != entry.Texture);
            return entry.Shown.Count > 0;
        }

        internal static bool Allowed(string url, bool face)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            bool local = uri.Scheme == Uri.UriSchemeHttp && OrbitersEnvironment.IsLoopback(uri.Host);
            if (uri.Scheme != Uri.UriSchemeHttps && !local) return false;
            return !face || local || OrbitersEnvironment.IsProfilePictureHost(uri.Host);
        }

        private static string Hash(string value)
        {
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}
