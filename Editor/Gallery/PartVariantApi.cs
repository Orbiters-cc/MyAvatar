using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Editor.Net;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Orbiters' part variants API (avatar-assets/…, the server's avatarAssetPartsRouter): an asset's customizable parts,
    /// each part's gallery of variants, a variant's zip, posting one and taking one down. Every route and answer shape
    /// lives here.
    /// </summary>
    internal static class PartVariantApi
    {
        private const string Failure = "Orbiters answered HTTP {0}. Check your connection and try again.";
        private static string Url(string path) => OrbitersEnvironment.ApiUrl("avatar-assets/" + path.TrimStart('/'));

        private static Task<T> Send<T>(string url, object payload = null, HttpMethod method = null, CancellationToken cancellation = default) =>
            OrbitersApi.SendAsync<T>(url, GalleryApi.Token, payload, cancellation, method, Failure);

        /// <summary>GET /:assetId/parts: the parts of the asset's newest published release the member reaches.</summary>
        internal static Task<AssetParts> PartsAsync(int assetId, CancellationToken cancellation) => Send<AssetParts>(Url($"{assetId}/parts"), cancellation: cancellation);

        /// <summary>GET /:assetId/parts/:part/variants: a page of a part's published variants, the creator's own first.</summary>
        internal static Task<VariantPage> VariantsAsync(int assetId, string part, string query, string language, bool newest, string cursor, CancellationToken cancellation)
        {
            var args = new List<string> { "sort=" + (newest ? "new" : "popular") };
            if (!string.IsNullOrWhiteSpace(query)) args.Add("query=" + Uri.EscapeDataString(query.Trim()));
            if (!string.IsNullOrWhiteSpace(language)) args.Add("language=" + Uri.EscapeDataString(language));
            if (!string.IsNullOrEmpty(cursor)) args.Add("cursor=" + Uri.EscapeDataString(cursor));
            return Send<VariantPage>(Url($"{assetId}/parts/{Uri.EscapeDataString(part)}/variants?" + string.Join("&", args)), cancellation: cancellation);
        }

        internal static Task<VariantInfo> VariantAsync(int id, CancellationToken cancellation) => Send<VariantInfo>(Url($"part-variants/{id}"), cancellation: cancellation);

        /// <summary>GET /part-variants/:id/download: the variant's zip, checked against its published SHA-256.</summary>
        internal static Task<OrbitersTransfer.Result> DownloadAsync(VariantInfo variant, string destination, OrbitersTransfer.Progress progress, CancellationToken cancellation) =>
            OrbitersTransfer.DownloadFileAsync(Url($"part-variants/{variant.id}/download"), destination, GalleryApi.Token, progress, cancellation, variant.sha256);

        /// <summary>
        /// POST /:assetId/parts/:part/variants: the zip (<c>variantFile</c>) and its metadata (<c>official</c> for a
        /// creator's base variant, posted with a release). Orbiters checks it as Toolkit's ItemParts does.
        /// </summary>
        internal static async Task<VariantInfo> PostAsync(int assetId, string part, string zipPath, bool official, string sha256, OrbitersTransfer.Progress progress, CancellationToken cancellation)
        {
            var result = await OrbitersTransfer.UploadMultipartAsync(Url($"{assetId}/parts/{Uri.EscapeDataString(part)}/variants"), GalleryApi.Token, new[]
            {
                OrbitersTransfer.Part.Field("metadata", JsonConvert.SerializeObject(new { official, sha256 })),
                OrbitersTransfer.Part.File("variantFile", zipPath, Path.GetFileName(zipPath), "application/zip"),
            }, progress, cancellation);
            if (!result.Success) throw result.Cancelled ? (Exception)new OperationCanceledException() : new InvalidOperationException(result.Error);
            try { return JsonConvert.DeserializeObject<VariantInfo>(result.Body); }
            catch (JsonException) { return null; }
        }

        /// <summary>DELETE /part-variants/:id: withdrawn by its author, removed by the asset's creator.</summary>
        internal static Task<object> DeleteAsync(int id) => Send<object>(Url($"part-variants/{id}"), null, HttpMethod.Delete);

        /// <summary>A release's parts, as the publish request's <c>parts</c> takes them: the file's path, its JSON, each strings part's base texts.</summary>
        internal static object ReleaseParts(string file, string definition, Dictionary<string, JObject> strings) =>
            new { file, definition = JObject.Parse(definition), strings };
    }

    internal sealed class AssetParts
    {
        public int assetId;
        public AssetPartsRelease release;
        public List<AssetPart> parts = new List<AssetPart>();
    }

    internal sealed class AssetPartsRelease
    {
        public int id;
        public string version;
    }

    internal sealed class AssetPart
    {
        public string key, label, description, kind, renderer;
        public int variantCount;
    }

    internal sealed class VariantPage
    {
        public string part, kind, sort, nextCursor;
        public List<VariantInfo> variants = new List<VariantInfo>();
    }

    internal sealed class VariantInfo
    {
        public int id, assetId, downloads;
        public string part, kind, name, description, language, thumbnail, sha256, status;
        public bool official, mine, canRemove;
        public long sizeBytes;
        public GalleryCreator author;
        public DateTime? createdAt;
    }
}
