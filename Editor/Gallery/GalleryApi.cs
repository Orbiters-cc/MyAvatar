using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor.Net;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// The Orbiters gallery API (avatar-assets/…) for My Avatar: cards, details, purchases, license keys, downloads and,
    /// for creators, assets, releases and their packages. URLs come from <see cref="OrbitersEnvironment"/>; large transfers
    /// go through Toolkit's <see cref="OrbitersTransfer"/>.
    /// </summary>
    internal static class GalleryApi
    {
        private const string Failure = "Orbiters answered HTTP {0}. Check your connection and try again.";
        internal static string Token => AuthenticationService.GetAuth()?.token;
        private static string Url(string path) => OrbitersEnvironment.ApiUrl("avatar-assets/" + path.TrimStart('/'));

        private static Task<T> Send<T>(string url, object payload = null, HttpMethod method = null, CancellationToken cancellation = default) =>
            OrbitersApi.SendAsync<T>(url, Token, payload, cancellation, method, Failure);

        internal static Task<GalleryPage> ListAsync(string platform, int? baseId, string type, string query, bool mine, int offset, CancellationToken cancellation)
        {
            var args = new List<string> { "platform=" + Uri.EscapeDataString(platform ?? "") };
            if (baseId.HasValue && baseId.Value > 0) args.Add("baseId=" + baseId.Value);
            if (!string.IsNullOrEmpty(type)) args.Add("type=" + type);
            if (!string.IsNullOrWhiteSpace(query)) args.Add("q=" + Uri.EscapeDataString(query.Trim()));
            if (mine) args.Add("mine=1");
            if (offset > 0) args.Add("offset=" + offset);
            return Send<GalleryPage>(Url("gallery?" + string.Join("&", args)), cancellation: cancellation);
        }

        internal static Task<GalleryAsset> DetailAsync(int assetId, string platform, int? baseId, CancellationToken cancellation) =>
            Send<GalleryAsset>(Url($"{assetId}?platform={Uri.EscapeDataString(platform ?? "")}" + (baseId > 0 ? "&baseId=" + baseId : "")), cancellation: cancellation);

        internal static Task<GalleryBases> BasesAsync(CancellationToken cancellation) => Send<GalleryBases>(Url("bases"), cancellation: cancellation);

        internal static Task<KnownCatalog> KnownDependenciesAsync(CancellationToken cancellation) => Send<KnownCatalog>(Url("known-dependencies"), cancellation: cancellation);

        internal static Task<PurchaseStart> StartPurchaseAsync(int assetId, string provider) =>
            Send<PurchaseStart>(Url($"{assetId}/purchases"), new { provider });

        internal static Task<PurchaseState> PurchaseStatusAsync(int assetId, CancellationToken cancellation) =>
            Send<PurchaseState>(Url($"{assetId}/purchases/status"), cancellation: cancellation);

        /// <summary>The website's license key redemption (POST /assets): (added, already owned, creators to pick from, error).</summary>
        internal static async Task<RedeemResult> RedeemAsync(string licenseKey, int? creatorId, CancellationToken cancellation)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, OrbitersEnvironment.ApiUrl("assets")))
            {
                string token = Token;
                if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                object payload = creatorId.HasValue ? (object)new { licenseKey, creatorId = creatorId.Value } : new { licenseKey };
                request.Content = new StringContent(JsonConvert.SerializeObject(payload), System.Text.Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, cancellation))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    var answer = Parse<RedeemAnswer>(body) ?? new RedeemAnswer();
                    int status = (int)response.StatusCode;
                    if (status == 208) return new RedeemResult { Success = true, Message = "You already own this asset." };
                    if (response.IsSuccessStatusCode) return new RedeemResult { Success = true, Message = "Asset added to your library." };
                    if (status == 404 && answer.needsCreatorHint && answer.creators?.Count > 0) return new RedeemResult { Creators = answer.creators };
                    return new RedeemResult { Message = answer.error ?? string.Format(Failure, status) };
                }
            }
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        private static T Parse<T>(string body) where T : class { try { return string.IsNullOrWhiteSpace(body) ? null : JsonConvert.DeserializeObject<T>(body); } catch (JsonException) { return null; } }
        private sealed class RedeemAnswer { public string error; public bool needsCreatorHint; public List<RedeemCreator> creators; }

        internal sealed class RedeemResult
        {
            public bool Success;
            public string Message;
            public List<RedeemCreator> Creators;
        }

        /// <summary>Downloads a package the user may install, checked against its published SHA-256.</summary>
        internal static Task<OrbitersTransfer.Result> DownloadAsync(int assetId, int variantId, string destination, string sha256,
            OrbitersTransfer.Progress progress, CancellationToken cancellation) =>
            OrbitersTransfer.DownloadFileAsync(Url($"{assetId}/variants/{variantId}/download"), destination, Token, progress, cancellation, sha256);

        // ---- Creators ----

        internal static Task<CreatorAssets> CreatorAssetsAsync(CancellationToken cancellation) => Send<CreatorAssets>(Url("creator/assets"), cancellation: cancellation);

        internal static async Task<Created> CreateAssetAsync(object metadata, string thumbnailPath, IReadOnlyList<string> previewPaths, string idempotencyKey, OrbitersTransfer.Progress progress, CancellationToken cancellation)
        {
            var parts = new List<OrbitersTransfer.Part> { OrbitersTransfer.Part.Field("metadata", JsonConvert.SerializeObject(metadata)) };
            AddPictures(parts, thumbnailPath, previewPaths);
            var result = await OrbitersTransfer.UploadMultipartAsync(Url("creator/assets"), Token, parts, progress, cancellation,
                headers: new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey });
            return Answer<Created>(result);
        }

        internal static async Task<Created> UpdateAssetAsync(int assetId, object metadata, string thumbnailPath, IReadOnlyList<string> previewPaths, CancellationToken cancellation)
        {
            var parts = new List<OrbitersTransfer.Part> { OrbitersTransfer.Part.Field("metadata", JsonConvert.SerializeObject(metadata)) };
            AddPictures(parts, thumbnailPath, previewPaths);
            return Answer<Created>(await OrbitersTransfer.UploadMultipartAsync(Url($"creator/assets/{assetId}"), Token, parts, null, cancellation, "PUT"));
        }

        // The card's picture, and the previews shown on the asset's page (photoshoot shots, ref sheets) in order.
        private static void AddPictures(List<OrbitersTransfer.Part> parts, string thumbnailPath, IReadOnlyList<string> previewPaths)
        {
            if (!string.IsNullOrEmpty(thumbnailPath)) parts.Add(OrbitersTransfer.Part.File("thumbnail", thumbnailPath, "thumbnail" + Path.GetExtension(thumbnailPath), MimeType(thumbnailPath)));
            for (int i = 0; previewPaths != null && i < previewPaths.Count; i++)
                parts.Add(OrbitersTransfer.Part.File("previews", previewPaths[i], "preview-" + (i + 1) + Path.GetExtension(previewPaths[i]), MimeType(previewPaths[i])));
        }

        private static string MimeType(string path) =>
            Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        internal static Task<object> SetPreferredStoreAsync(string provider) => Send<object>(Url("creator/preferred-store"), new { provider }, HttpMethod.Put);

        internal static Task<Created> CreateReleaseAsync(int assetId, string version, string title, string changelog, string scope) =>
            Send<Created>(Url($"creator/assets/{assetId}/versions"), new { version, title, changelog, scope });

        internal static Task<Created> UpdateReleaseAsync(int releaseId, object changes) => Send<Created>(Url($"creator/versions/{releaseId}"), changes, HttpMethod.Put);

        internal static Task<object> DeleteReleaseAsync(int releaseId) => Send<object>(Url($"creator/versions/{releaseId}"), null, HttpMethod.Delete);

        internal static async Task<GalleryVariant> UploadVariantAsync(int releaseId, object metadata, string packagePath, OrbitersTransfer.Progress progress, CancellationToken cancellation)
        {
            var result = await OrbitersTransfer.UploadMultipartAsync(Url($"creator/versions/{releaseId}/variants"), Token, new[]
            {
                OrbitersTransfer.Part.Field("metadata", JsonConvert.SerializeObject(metadata)),
                OrbitersTransfer.Part.File("packageFile", packagePath, System.IO.Path.GetFileName(packagePath), "application/octet-stream"),
            }, progress, cancellation);
            return Answer<GalleryVariant>(result);
        }

        internal static Task<object> DeleteVariantAsync(int variantId) => Send<object>(Url($"creator/variants/{variantId}"), null, HttpMethod.Delete);

        internal static Task<Created> PublishReleaseAsync(int releaseId, bool publishListing) =>
            Send<Created>(Url($"creator/versions/{releaseId}/publish"), new { rightsConfirmed = true, publishListing });

        internal static Task<Created> WithdrawReleaseAsync(int releaseId) => Send<Created>(Url($"creator/versions/{releaseId}/withdraw"), new { });

        internal static Task<StoreProducts> StoreProductsAsync(string provider, string query, CancellationToken cancellation) =>
            OrbitersApi.SendAsync<StoreProducts>(OrbitersEnvironment.ApiUrl($"creator-tools/store-products?provider={provider}&q={Uri.EscapeDataString(query ?? "")}"), Token, null, cancellation, null, Failure);

        internal static Task<object> LinkStoreProductAsync(int assetId, StoreProduct product) =>
            OrbitersApi.SendAsync<object>(OrbitersEnvironment.ApiUrl($"creator/assets/{assetId}/store-links"), Token,
                new { storeIntegrationId = product.integrationId, externalProductId = product.externalProductId, externalUrl = product.url }, default, null, Failure);

        internal static async Task<Dictionary<string, List<ScopeUser>>> TestersAsync(int assetId, CancellationToken cancellation)
        {
            var answer = await OrbitersApi.SendAsync<TestersAnswer>(OrbitersEnvironment.ApiUrl($"creator/assets/{assetId}/scopes"), Token, null, cancellation, null, Failure);
            return answer?.scopes ?? new Dictionary<string, List<ScopeUser>>();
        }
        private sealed class TestersAnswer { public Dictionary<string, List<ScopeUser>> scopes; }

        internal static Task<object> AddTesterAsync(int assetId, string scope, int userId) =>
            OrbitersApi.SendAsync<object>(OrbitersEnvironment.ApiUrl($"creator/assets/{assetId}/scopes/{scope}/users"), Token, new { userId }, default, null, Failure);

        internal static Task<object> RemoveTesterAsync(int assetId, string scope, int userId) =>
            OrbitersApi.SendAsync<object>(OrbitersEnvironment.ApiUrl($"creator/assets/{assetId}/scopes/{scope}/users/{userId}"), Token, null, default, HttpMethod.Delete, Failure);

        internal static async Task<List<ScopeUser>> SearchUsersAsync(string query, CancellationToken cancellation) =>
            (await OrbitersApi.SendAsync<SearchUsers>(OrbitersEnvironment.ApiUrl("creator/users/search?q=" + Uri.EscapeDataString(query ?? "")), Token, null, cancellation, null, Failure))?.users
            ?? new List<ScopeUser>();

        private static T Answer<T>(OrbitersTransfer.Result result) where T : class
        {
            if (!result.Success) throw result.Cancelled ? (Exception)new OperationCanceledException() : new InvalidOperationException(result.Error);
            return Parse<T>(result.Body);
        }

        /// <summary>Gallery downloads and packages waiting to be imported live here, outside Assets/.</summary>
        internal static string StagingFolder => System.IO.Path.Combine(LibraryStore.Folder, "Gallery", "Downloads");
    }
}
