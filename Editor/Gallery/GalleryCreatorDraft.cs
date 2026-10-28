using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>What a creator is preparing to publish; kept across script reloads (building a package can reload them).</summary>
    internal sealed class GalleryCreatorDraft
    {
        /// <summary>Names this draft until it is published; the window and the MCP tool each hold a copy of it.</summary>
        public string draftId = NewKey();
        public int assetId;
        public bool newAsset = true;
        public string name, type = "ACCESSORY", shortDescription, description, gumroad, jinxxy, currency = "USD", preferredStore, thumbnailPath;
        /// <summary>Pictures for the asset's page besides <see cref="thumbnailPath"/> (the card's): photoshoot shots and ref sheets, in order.</summary>
        public List<string> previewPaths = new List<string>();
        /// <summary>An existing asset's current previews make way for these instead of following them.</summary>
        public bool replacePreviews;
        /// <summary>The pictures reached Orbiters: publishing again doesn't send them twice.</summary>
        public bool picturesSent;
        public bool free;
        public long priceCents;
        public bool listed;
        public string version = "1.0.0", title, changelog, scope = "public";
        /// <summary>The draft version on Orbiters and the asset it belongs to (see <see cref="Release"/>).</summary>
        public int releaseId, releaseAssetId;
        public List<GalleryVariantDraft> variants = new List<GalleryVariantDraft> { new GalleryVariantDraft() };
        public bool rights, publishListing = true;
        /// <summary>The creation request of the next new asset: a new one once that asset exists.</summary>
        public string idempotencyKey = NewKey();
        public string message;
        public bool messageError;
        /// <summary>
        /// The server and account its ids on Orbiters (the asset, the draft version, the packages and pictures sent) belong
        /// to (<see cref="GalleryApi.Context"/>); null until it has any.
        /// </summary>
        public string context;

        /// <summary>The gallery takes this many pictures for an asset's page in one request, besides the card's.</summary>
        internal const int MaxPreviews = 8;
        private const string Key = "Orbiters.MyAvatar.GalleryCreatorDraft";
        // The saved lists replace the defaults above instead of being appended to them.
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
        // Published drafts: a copy still held somewhere (the window, an MCP job) never brings one back.
        private static readonly HashSet<string> Ended = new HashSet<string>();

        internal static string NewKey() => Guid.NewGuid().ToString("N");

        internal static GalleryCreatorDraft Load()
        {
            try
            {
                var draft = JsonConvert.DeserializeObject<GalleryCreatorDraft>(SessionState.GetString(Key, ""), Settings);
                if (draft == null || Ended.Contains(draft.draftId)) return new GalleryCreatorDraft();
                draft.Bind(GalleryApi.Context);
                return draft;
            }
            catch (JsonException) { return new GalleryCreatorDraft(); }
        }

        /// <summary>
        /// Keeps the draft's ids on Orbiters only for the server and account they came from: with another one (the
        /// Development environment switched, another account signed in) it forgets them, keeps what was written and chosen,
        /// and says so. True when it had to.
        /// </summary>
        internal bool Bind(string now)
        {
            bool held = assetId > 0 || releaseId > 0 || picturesSent || variants.Any(v => v.uploadedId > 0);
            if (context == now || !held) { context = now; return false; }
            var asset = assetId;
            assetId = releaseId = releaseAssetId = 0;
            newAsset = true;
            picturesSent = false;
            foreach (var variant in variants) variant.ForgetUpload();
            idempotencyKey = NewKey();
            context = now;
            message = "This draft was started with another Orbiters account or server" + (asset > 0 ? ": choose the asset again" : "") + " before publishing.";
            messageError = true;
            return true;
        }

        internal void Save()
        {
            if (!Ended.Contains(draftId)) SessionState.SetString(Key, JsonConvert.SerializeObject(this));
        }

        internal static void Clear() => SessionState.EraseString(Key);

        /// <summary>This draft was published: <see cref="Load"/> gives the next one.</summary>
        [JsonIgnore] internal bool Over => Ended.Contains(draftId);

        /// <summary>
        /// Published: this draft is over. The next one starts empty, with a new asset creation request and nothing sent
        /// yet, and no copy of this one is saved again.
        /// </summary>
        internal void End()
        {
            Ended.Add(draftId);
            Clear();
        }

        /// <summary>
        /// Points the draft at one of the creator's assets (0: a new one). Another asset drops what went to the previous
        /// one: its draft version, the packages uploaded to it and the pictures sent.
        /// </summary>
        internal void Target(int asset)
        {
            asset = Math.Max(0, asset);
            newAsset = asset == 0;
            if (asset == assetId) return;
            assetId = asset;
            // An asset chosen now is one of this account's on this server.
            context = GalleryApi.Context;
            releaseId = releaseAssetId = 0;
            picturesSent = false;
            foreach (var variant in variants) variant.ForgetUpload();
        }

        /// <summary>Uses a draft version of the targeted asset (0: none yet).</summary>
        internal void UseRelease(int release)
        {
            releaseId = Math.Max(0, release);
            releaseAssetId = releaseId > 0 ? assetId : 0;
        }

        /// <summary>The draft version to send packages to: only one of the asset the draft targets now.</summary>
        internal int Release => releaseId > 0 && assetId > 0 && releaseAssetId == assetId ? releaseId : 0;

        /// <summary>The first package name used twice (the gallery tells them apart by name, ignoring case), or null.</summary>
        internal string DuplicateLabel() => variants.GroupBy(v => v.Label, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
    }

    internal sealed class GalleryVariantDraft
    {
        public string label = "Default", source = "prefab", packagePath;
        public List<string> prefabGuids = new List<string>();
        public List<string> platforms = new List<string> { AvatarPlatform.Current };
        public string baseScope = "any";
        public List<int> baseIds = new List<int>();
        public GalleryManifest manifest = new GalleryManifest();
        public GalleryPackager.Report report;
        /// <summary>The package as uploaded: its id on Orbiters, the version it went to and the <see cref="Fingerprint"/> sent.</summary>
        public int uploadedId, uploadedRelease;
        public string uploadedFingerprint;
        public string testJob;

        /// <summary>The name the server stores (it trims it, and an empty one is "Default").</summary>
        [JsonIgnore] internal string Label => string.IsNullOrWhiteSpace(label) ? "Default" : label.Trim();

        /// <summary>What the server stores for this variant (avatarAssets/variantInput.js).</summary>
        internal object Metadata() => new
        {
            label = Label, platforms, baseScope, avatarBaseIds = baseScope == "bases" ? baseIds : new List<int>(), parameterBits = report?.parameterBits ?? 0,
            dependencies = report?.dependencies ?? new List<GalleryDependency>(), manifest, sha256 = report?.sha256, replace = true,
        };

        /// <summary>Everything sent for this variant, its package's hash included: any change sends it again.</summary>
        internal string Fingerprint()
        {
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Metadata()))));
        }

        /// <summary>This variant, as it is now, is on that draft version already.</summary>
        internal bool UploadedTo(int release) => release > 0 && uploadedId > 0 && uploadedRelease == release && uploadedFingerprint == Fingerprint();

        internal void MarkUploaded(int id, int release)
        {
            uploadedId = id; uploadedRelease = release; uploadedFingerprint = Fingerprint();
        }

        internal void ForgetUpload()
        {
            uploadedId = uploadedRelease = 0;
            uploadedFingerprint = null;
        }
    }
}
