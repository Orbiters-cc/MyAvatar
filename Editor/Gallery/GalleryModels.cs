using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Orbiters.Toolkit.Versions;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    // What the Orbiters gallery API answers (backend services/avatarAssets). Field names follow the JSON.
    internal sealed class GalleryPage
    {
        public List<GalleryAsset> assets = new List<GalleryAsset>();
        public int total, offset, limit;
    }

    internal sealed class GalleryCreator
    {
        public int id;
        public string username, avatarUrl;
        public bool trusted;
    }

    internal sealed class GalleryPrice
    {
        public long cents;
        public string currency;
        public bool free;

        public string Label
        {
            get
            {
                if (free) return "Free";
                string amount = (cents / 100m).ToString(cents % 100 == 0 ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture);
                switch ((currency ?? "").ToUpperInvariant())
                {
                    case "USD": return "$" + amount;
                    case "EUR": return "€" + amount;
                    case "GBP": return "£" + amount;
                    case "JPY": return "¥" + amount;
                    default: return amount + " " + (currency ?? "").ToUpperInvariant();
                }
            }
        }
    }

    internal sealed class GalleryStore
    {
        public string provider, label, url;
        public bool linked, preferred;
    }

    internal sealed class GalleryAccess
    {
        /// <summary>creator | owned | included | free | purchase</summary>
        public string state, label, tier;
        public List<string> scopes = new List<string>();
        public bool CanAdd => state != "purchase";
    }

    internal sealed class GallerySetupPrefab
    {
        public string guid, path, name;
        public GalleryAttach attach = new GalleryAttach();
    }

    internal sealed class GalleryAttach
    {
        /// <summary>auto | configured | merge | parent</summary>
        public string mode = "auto";
        public string bone;
    }

    internal sealed class GallerySetup
    {
        public string key, label;
        public List<GallerySetupPrefab> prefabs = new List<GallerySetupPrefab>();
    }

    internal sealed class GalleryManifest
    {
        public int schema = 1;
        public List<GallerySetup> setups = new List<GallerySetup>();
        public string defaultSetup;

        public GallerySetup Setup(string key) => setups.FirstOrDefault(s => s.key == key) ?? setups.FirstOrDefault(s => s.key == defaultSetup) ?? setups.FirstOrDefault();
    }

    internal sealed class GalleryDependency
    {
        public string id, range, displayName;
    }

    internal sealed class GalleryVariant
    {
        public int id;
        public string label, baseScope, sha256, status;
        public List<string> platforms = new List<string>();
        public List<int> avatarBaseIds = new List<int>();
        public long sizeBytes;
        public int parameterBits;
        public bool containsCode;
        public List<GalleryDependency> dependencies = new List<GalleryDependency>();
        public GalleryManifest manifest = new GalleryManifest();
        public GalleryContents contents = new GalleryContents();
    }

    internal sealed class GalleryContents
    {
        public int entryCount, codeFileCount, prefabCount;
        public long expandedBytes;
        public List<string> codeFiles = new List<string>(), folders = new List<string>();
    }

    internal sealed class GalleryRelease : IVersionRecord
    {
        public int id;
        public string version, title, scope, status, changelog;
        public DateTime? publishedAt;
        public List<GalleryVariant> variants = new List<GalleryVariant>();

        int IVersionRecord.Id => id;
        string IVersionRecord.Version => version;
        string IVersionRecord.Title => title;
        string IVersionRecord.Scope => scope;
        string IVersionRecord.Date => publishedAt?.ToString("o");
        string IVersionRecord.Changelog => changelog;
        int IVersionRecord.UploaderId => 0;
        string IVersionRecord.CreatorName => null;
        bool? IVersionRecord.CreatorTrusted => null;
    }

    /// <summary>Which release fits the avatar, or why none does (compatible, platform, base, unknown-base, none).</summary>
    internal sealed class GalleryFit
    {
        public string state;
        public GalleryRelease release;
        public List<GalleryVariant> variants = new List<GalleryVariant>();
        public List<string> platforms = new List<string>();
        public List<int> bases = new List<int>();
        public bool Compatible => state == "compatible" && variants.Count > 0;
    }

    internal sealed class GalleryAsset
    {
        public int id;
        public string name, type, shortDescription, thumbnail, preferredStore, description;
        public GalleryCreator creator = new GalleryCreator();
        public GalleryPrice price;
        public List<GalleryStore> stores = new List<GalleryStore>();
        public GalleryAccess access = new GalleryAccess();
        public GalleryFit fit = new GalleryFit();
        public bool listed;
        public int drafts;
        public DateTime? updatedAt;
        // Detail only
        public List<string> previews = new List<string>();
        public List<GalleryBase> bases = new List<GalleryBase>();
        public List<GalleryRelease> releases = new List<GalleryRelease>();

        [JsonIgnore] public bool Clothing => string.Equals(type, "CLOTHING", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class GalleryBase
    {
        public int id;
        public string name;
        public BasePatterns patterns;
    }

    internal sealed class BasePatterns
    {
        public PathPatterns paths;
    }

    internal sealed class PathPatterns
    {
        public string strategy;
        public List<string> pathsList = new List<string>();
    }

    internal sealed class GalleryBases { public List<GalleryBase> bases = new List<GalleryBase>(); }

    internal sealed class KnownPackage
    {
        public string id, displayName, repositoryUrl, supportedRange, latestVersion;
        public List<KnownVersion> versions = new List<KnownVersion>();
    }

    internal sealed class KnownVersion
    {
        public string version, unity;
        public Dictionary<string, string> dependencies = new Dictionary<string, string>();
    }

    internal sealed class KnownCatalog { public List<KnownPackage> packages = new List<KnownPackage>(); }

    internal sealed class PurchaseStart
    {
        public string url, provider, label;
        public AutoRedeem autoRedeem = new AutoRedeem();
    }

    internal sealed class AutoRedeem
    {
        public bool supported, instant;
        public string reason;
    }

    internal sealed class PurchaseState { public string state, provider; }

    internal sealed class RedeemCreator { public int id; public string username; }

    // ---- Creator side ----

    internal sealed class CreatorAssets
    {
        public string preferredStore;
        public List<CreatorAsset> assets = new List<CreatorAsset>();
    }

    internal sealed class CreatorAsset
    {
        public int id;
        public string name, type, currency, preferredStore;
        public bool inGallery, listed;
        public long? priceCents;
        public List<GalleryStore> stores = new List<GalleryStore>();
        public List<CreatorRelease> releases = new List<CreatorRelease>();
    }

    internal sealed class CreatorRelease { public int id; public string version, status, scope; }

    internal sealed class Created { public int id; public bool created; public string version, status; public bool listed; }

    internal sealed class StoreProduct { public int integrationId; public string provider, account, externalProductId, name, shortDescription, thumbnail, url; }

    internal sealed class StoreProducts { public bool connected, supported; public List<StoreProduct> items = new List<StoreProduct>(); }

    internal sealed class ScopeUser { public int id; public string username; }

    internal sealed class SearchUsers { public List<ScopeUser> users = new List<ScopeUser>(); }
}
