using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Editor.Net;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Switching a placed item's customizable parts between variants (Toolkit's <see cref="ItemParts"/>) from My Avatar:
    /// a gallery variant is downloaded (kept in Library by its hash), checked as Orbiters checks posts and applied with
    /// Undo; a base variant (the creator's, in the asset) is applied at once. One job per item and part, which every view
    /// showing it follows (<see cref="Changed"/>). Also posts the variants members make and the creator's base variants.
    /// </summary>
    internal static class PartVariants
    {
        internal sealed class Job
        {
            public int Item;
            public string Part, Name, Status, Error;
            public float Progress;
            public bool Done, Failed;
            /// <summary>The gallery variant being applied (0 for a base variant).</summary>
            public int VariantId;
            public string BaseName;
            public CancellationTokenSource Cancel = new CancellationTokenSource();
            public bool Running => !Done && !Failed;
        }

        private static readonly Dictionary<(int, string), Job> Jobs = new Dictionary<(int, string), Job>();

        /// <summary>A job started, moved on or ended: views showing that item's parts refresh.</summary>
        internal static event Action<Job> Changed;

        internal static Job JobOf(Transform item, string part) =>
            item != null && Jobs.TryGetValue((item.GetInstanceID(), part), out var job) ? job : null;

        private static void Move(Job job, string status, float progress)
        {
            job.Status = status; job.Progress = progress;
            Changed?.Invoke(job);
        }

        internal static string Folder => Path.Combine(LibraryStore.Folder, "Gallery", "PartVariants");

        /// <summary>Applies a gallery variant: download (or the copy kept), checks, then the item changes in one Undo step.</summary>
        internal static Job Apply(ItemParts.Placed placed, VariantInfo info) =>
            Start(placed, info.name, info.id, null, async job =>
            {
                var variant = await DownloadAsync(info, (text, progress) => Move(job, text, progress * .6f), job.Cancel.Token);
                return variant;
            });

        /// <summary>Applies a base variant, or a variant made here (the + form's).</summary>
        internal static Job Apply(ItemParts.Placed placed, PartVariant variant) =>
            Start(placed, variant.name, variant.Id, variant.BaseName, _ => Task.FromResult(variant));

        private static Job Start(ItemParts.Placed placed, string name, int variantId, string baseName, Func<Job, Task<PartVariant>> load)
        {
            var key = (placed.Item.GetInstanceID(), placed.Part.key);
            if (Jobs.TryGetValue(key, out var running) && running.Running) running.Cancel.Cancel();
            var job = new Job { Item = key.Item1, Part = key.Item2, Name = name, VariantId = variantId, BaseName = baseName };
            Jobs[key] = job;
            // Shown at once, before anything is read.
            Move(job, variantId > 0 ? "Downloading…" : "Applying…", .05f);
            _ = RunAsync(job, placed, load);
            return job;
        }

        private static async Task RunAsync(Job job, ItemParts.Placed placed, Func<Job, Task<PartVariant>> load)
        {
            try
            {
                var variant = await load(job);
                job.Cancel.Token.ThrowIfCancellationRequested();
                Move(job, "Applying…", .65f);
                var result = await ItemParts.ApplyAsync(placed, variant, text => Move(job, text, Mathf.Max(job.Progress, .7f)), job.Cancel.Token);
                if (!result.Applied) throw new InvalidOperationException(string.Join(" ", result.Problems.Take(4)));
                job.Done = true;
                Move(job, result.Note ?? "Applied. Undo (Ctrl+Z) puts back the earlier one.", 1);
            }
            catch (OperationCanceledException)
            {
                job.Failed = true; job.Error = "Cancelled.";
                Move(job, job.Error, 0);
            }
            catch (Exception e)
            {
                job.Failed = true; job.Error = e.Message;
                Move(job, job.Error, 0);
            }
        }

        /// <summary>A gallery variant's zip, downloaded once (kept by its hash in Library) and read as Toolkit checks it.</summary>
        internal static async Task<PartVariant> DownloadAsync(VariantInfo info, Action<string, float> progress, CancellationToken cancellation)
        {
            string file = Path.Combine(Folder, info.id + "-" + (info.sha256 ?? "x").Substring(0, Math.Min(16, (info.sha256 ?? "x").Length)) + ".zip");
            if (!File.Exists(file))
            {
                var result = await PartVariantApi.DownloadAsync(info, file, (bytes, total) => progress?.Invoke("Downloading · " + OrbitersTransfer.Describe(bytes, total), total > 0 ? (float)bytes / total : .3f), cancellation);
                if (!result.Success) throw result.Cancelled ? (Exception)new OperationCanceledException() : new InvalidOperationException(result.Error);
            }
            progress?.Invoke("Checking…", 1);
            var problems = new List<string>();
            var variant = PartVariant.ReadZip(File.ReadAllBytes(file), problems);
            if (variant == null)
            {
                try { File.Delete(file); } catch (IOException) { }
                throw new InvalidOperationException(string.Join(" ", problems));
            }
            variant.Id = info.id; variant.Official = info.official; variant.Author = info.author?.username;
            return variant;
        }

        // ---- Posting ----

        /// <summary>
        /// Posts a variant to the part's gallery: written as the zip Orbiters takes, read back and checked as Orbiters
        /// will (nothing is sent when a check fails), then uploaded. <paramref name="official"/>: a creator's base variant.
        /// </summary>
        internal static async Task<VariantInfo> PostAsync(int assetId, ItemParts.Placed placed, PartVariant variant, bool official, Action<string> progress, CancellationToken cancellation)
        {
            progress?.Invoke("Checking…");
            string zip = Path.Combine(Folder, "Posts", Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zip));
            try
            {
                variant.WriteZip(zip, placed.Part);
                var problems = new List<string>();
                var bytes = File.ReadAllBytes(zip);
                var read = PartVariant.ReadZip(bytes, problems);
                if (read != null)
                    ItemParts.Check(placed.Part, read, AssetDatabase.LoadAssetAtPath<GameObject>(placed.PrefabPath), problems, partsPath: placed.PartsPath);
                if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems.Take(6)));
                string sha = PartVariant.Hash(bytes);
                var posted = await PartVariantApi.PostAsync(assetId, placed.Part.key, zip, official, sha,
                    (sent, total) => progress?.Invoke("Posting · " + OrbitersTransfer.Describe(sent, total)), cancellation);
                if (posted == null || posted.id <= 0) throw new InvalidOperationException("Orbiters did not confirm the post. Post again.");
                // Kept as downloaded: applying it later needs no download.
                try { File.Copy(zip, Path.Combine(Folder, posted.id + "-" + sha.Substring(0, 16) + ".zip"), true); } catch (IOException) { }
                return posted;
            }
            finally
            {
                try { File.Delete(zip); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ---- Thumbnails ----

        private static readonly Dictionary<string, Texture2D> Pictures = new Dictionary<string, Texture2D>();

        [InitializeOnLoadMethod]
        private static void FreeOnReload() => AssemblyReloadEvents.beforeAssemblyReload += () =>
        {
            foreach (var picture in Pictures.Values) if (picture != null) UnityEngine.Object.DestroyImmediate(picture);
            Pictures.Clear();
        };

        /// <summary>A picture from PNG bytes, kept by <paramref name="key"/>.</summary>
        internal static Texture2D Picture(string key, Func<byte[]> bytes)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (Pictures.TryGetValue(key, out var known) && known != null) return known;
            var data = bytes?.Invoke();
            if (data == null) return null;
            var picture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = "Variant " + key };
            if (!picture.LoadImage(data)) { UnityEngine.Object.DestroyImmediate(picture); return null; }
            return Pictures[key] = picture;
        }

        private static string BaseThumbnailCache => Path.Combine(Folder, "Base thumbnails");

        /// <summary>
        /// A base variant's thumbnail: the one shipped beside its file, else one drawn here once (N4N0's menu in that
        /// language) and kept in Library. Null while it is drawn: <paramref name="drawn"/> is called once it is.
        /// </summary>
        internal static Texture2D BaseThumbnail(ItemParts.Placed placed, PartVariant variant, Action drawn)
        {
            if (variant.Thumbnail != null) return Picture("base|" + placed.PartsPath + "|" + placed.Part.key + "|" + variant.BaseName, () => variant.Thumbnail);
            string key = "base|" + ItemParts.ShortHash(placed.PartsPath + "|" + placed.Part.key + "|" + variant.ToJson());
            string file = Path.Combine(BaseThumbnailCache, key.Substring(5) + ".png");
            var shown = Picture(key, () => File.Exists(file) ? File.ReadAllBytes(file) : null);
            if (shown != null || Drawing.Contains(key)) return shown;
            Drawing.Add(key);
            _ = DrawAsync(placed, variant, file, key, drawn);
            return null;
        }

        private static readonly HashSet<string> Drawing = new HashSet<string>();

        private static async Task DrawAsync(ItemParts.Placed placed, PartVariant variant, string file, string key, Action drawn)
        {
            try
            {
                var png = await ItemParts.ThumbnailAsync(placed.Item, placed.Definition, placed.PrefabPath, placed.PartsPath, placed.Part, variant);
                if (png == null) return;
                Directory.CreateDirectory(BaseThumbnailCache);
                File.WriteAllBytes(file, png);
                drawn?.Invoke();
            }
            catch (Exception e) when (!(e is OutOfMemoryException)) { Debug.Log("[My Avatar] No thumbnail for " + variant.name + ": " + e.Message); }
            finally { Drawing.Remove(key); }
        }

        /// <summary>The thumbnail of the variant a part uses now: a base variant's, or the one kept when it was applied.</summary>
        internal static Texture2D CurrentThumbnail(ItemParts.Placed placed, Action drawn)
        {
            var current = placed.Current;
            if (current == null) return null;
            if (current.IsBase)
            {
                var variant = ItemParts.Bases(placed.PartsPath, placed.Part).FirstOrDefault(b => b.BaseName == current.baseName);
                return variant != null ? BaseThumbnail(placed, variant, drawn) : null;
            }
            return Picture("applied|" + current.thumbnail, () => ItemParts.ThumbnailBytes(current));
        }

        /// <summary>The languages a strings part's variants are in, for the gallery's filter: its base variants' first.</summary>
        internal static List<string> Languages(ItemParts.Placed placed) =>
            ItemParts.Bases(placed.PartsPath, placed.Part).Select(b => b.language).Where(l => !string.IsNullOrEmpty(l)).Distinct().ToList();

        /// <summary>A language's name for a code ("fr" → "French"), or the code.</summary>
        internal static string LanguageName(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            try { return new System.Globalization.CultureInfo(code).EnglishName; }
            catch (System.Globalization.CultureNotFoundException) { return code; }
        }

        /// <summary>The parts definition and base texts a release sends (the publish request's <c>parts</c>), or null.</summary>
        internal static object ReleaseParts(string partsPath)
        {
            var definition = ItemParts.Read(partsPath);
            if (definition == null) return null;
            var strings = new Dictionary<string, JObject>();
            foreach (var part in definition.parts.Where(p => p.kind == ItemParts.Strings))
            {
                var texts = ItemParts.BaseStrings.Read(ItemParts.AssetPath(partsPath, part.strings), null);
                if (texts == null) continue;
                var entries = new JObject();
                foreach (var pair in texts.Texts)
                    entries[pair.Key] = texts.Max.TryGetValue(pair.Key, out int max) ? (JToken)new JObject { ["text"] = pair.Value, ["max"] = max } : pair.Value;
                strings[part.key] = entries;
            }
            return PartVariantApi.ReleaseParts(partsPath, definition.Text, strings);
        }
    }
}
