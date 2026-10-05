using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>What a creator is preparing to publish; kept across script reloads (building a package can reload them).</summary>
    internal sealed class GalleryCreatorDraft
    {
        public int assetId;
        public bool newAsset = true;
        public string name, type = "ACCESSORY", shortDescription, description, gumroad, jinxxy, currency = "USD", preferredStore, thumbnailPath;
        public bool free;
        public long priceCents;
        public bool listed;
        public string version = "1.0.0", title, changelog, scope = "public";
        public int releaseId;
        public List<GalleryVariantDraft> variants = new List<GalleryVariantDraft> { new GalleryVariantDraft() };
        public bool rights, publishListing = true;
        public string idempotencyKey = Guid.NewGuid().ToString("N");
        public string message;
        public bool messageError;

        private const string Key = "Orbiters.MyAvatar.GalleryCreatorDraft";
        // The saved lists replace the defaults above instead of being appended to them.
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };

        internal static GalleryCreatorDraft Load()
        {
            try { return JsonConvert.DeserializeObject<GalleryCreatorDraft>(SessionState.GetString(Key, ""), Settings) ?? new GalleryCreatorDraft(); }
            catch (JsonException) { return new GalleryCreatorDraft(); }
        }

        internal void Save() => SessionState.SetString(Key, JsonConvert.SerializeObject(this));

        internal static void Clear() => SessionState.EraseString(Key);
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
        public int uploadedId;
        public string uploadedSha;
        public string testJob;

        /// <summary>What the server stores for this variant (avatarAssets/variantInput.js).</summary>
        internal object Metadata() => new
        {
            label, platforms, baseScope, avatarBaseIds = baseScope == "bases" ? baseIds : new List<int>(), parameterBits = report?.parameterBits ?? 0,
            dependencies = report?.dependencies ?? new List<GalleryDependency>(), manifest, sha256 = report?.sha256, replace = true,
        };
    }
}
