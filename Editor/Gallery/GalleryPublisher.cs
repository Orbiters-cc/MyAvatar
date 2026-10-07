using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Net;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Publishing a <see cref="GalleryCreatorDraft"/>: building each package with the packaging rules, trying one on an
    /// avatar, and sending the asset, version and packages to Orbiters. The creator window and the MCP tool share it.
    /// </summary>
    internal static class GalleryPublisher
    {
        /// <summary>The draft was changed outside the creator window (MCP): the window shows it again.</summary>
        internal static event Action DraftReplaced;
        internal static void NotifyDraftReplaced() => DraftReplaced?.Invoke();

        /// <summary>Builds a package and keeps the setups that still match its prefabs (one setup with them all at first).</summary>
        internal static async Task<GalleryPackager.Report> BuildAsync(GalleryCreatorDraft draft, GalleryVariantDraft variant, MyAvatar avatar)
        {
            string name = (string.IsNullOrWhiteSpace(draft.name) ? "asset" : draft.name) + " " + variant.label;
            var report = variant.source == "package"
                ? (string.IsNullOrEmpty(variant.packagePath) ? null : await GalleryPackager.FromPackageAsync(variant.packagePath, name, avatar))
                : GalleryPackager.FromPrefabs(variant.prefabGuids.Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g))).ToList(), name, avatar);
            if (report == null) throw new InvalidOperationException("Choose the package to publish first.");
            variant.report = report;
            variant.uploadedId = 0;
            var guids = new HashSet<string>(report.prefabs.Select(p => p.guid));
            variant.manifest.setups.ForEach(s => s.prefabs.RemoveAll(p => !guids.Contains(p.guid)));
            variant.manifest.setups.RemoveAll(s => s.prefabs.Count == 0);
            if (variant.manifest.setups.Count == 0)
                variant.manifest.setups.Add(new GallerySetup { key = "default", label = variant.label,
                    prefabs = report.prefabs.Take(variant.source == "package" ? 1 : 16).Select(Copy).ToList() });
            if (variant.manifest.Setup(variant.manifest.defaultSetup) == null) variant.manifest.defaultSetup = variant.manifest.setups[0].key;
            draft.Save();
            return report;
        }

        internal static GallerySetupPrefab Copy(GallerySetupPrefab p) => new GallerySetupPrefab { guid = p.guid, path = p.path, name = p.name, attach = new GalleryAttach() };

        /// <summary>Installs a built package on the avatar the way a buyer would.</summary>
        internal static GalleryJob Test(GalleryCreatorDraft draft, GalleryVariantDraft variant, MyAvatar avatar)
        {
            if (variant.report == null || !variant.report.Publishable) throw new InvalidOperationException(variant.label + ": build a publishable package first.");
            if (!avatar || AccessoryService.Busy(avatar)) throw new InvalidOperationException("My Avatar is busy with this avatar; try again in a moment.");
            var job = GalleryInstaller.StartLocal(avatar, string.IsNullOrWhiteSpace(draft.name) ? "Asset" : draft.name, variant.report.packagePath, variant.report.sha256,
                variant.manifest, variant.manifest.defaultSetup, variant.report.dependencies, variant.report.parameterBits);
            variant.testJob = job.id;
            draft.Save();
            return job;
        }

        /// <summary>What still stops the draft from being published, or null. The rights confirmation is checked separately.</summary>
        internal static string Missing(GalleryCreatorDraft draft)
        {
            if (draft.assetId <= 0 && string.IsNullOrWhiteSpace(draft.name)) return "Choose one of your assets or name a new one.";
            if (string.IsNullOrWhiteSpace(draft.version)) return "Give the version a number.";
            if (draft.variants.Count == 0) return "Add a package.";
            var unbuilt = draft.variants.FirstOrDefault(v => v.report == null || !v.report.Publishable);
            return unbuilt == null ? null : unbuilt.label + (unbuilt.report == null ? ": build its package." : ": its package needs changes before it can be published.");
        }

        /// <summary>
        /// Sends the draft to Orbiters and publishes the version. A retry skips what already reached Orbiters unchanged.
        /// Clears the draft once published.
        /// </summary>
        internal static async Task<Created> PublishAsync(GalleryCreatorDraft draft, Action<string> progress, CancellationToken cancellation)
        {
            string missing = Missing(draft);
            if (missing != null) throw new InvalidOperationException(missing);
            if (!draft.rights) throw new InvalidOperationException("Confirm that you own the rights to everything this version distributes.");
            progress ??= _ => { };
            try
            {
                if (draft.assetId <= 0)
                {
                    progress("Creating " + draft.name + "…");
                    var created = await GalleryApi.CreateAssetAsync(new
                    {
                        name = draft.name.Trim(), type = draft.type, shortDescription = draft.shortDescription, description = draft.description,
                        free = draft.free, priceCents = draft.free ? 0 : draft.priceCents, currency = draft.currency, gumroadLink = Url(draft.gumroad), jinxxyLink = Url(draft.jinxxy),
                        preferredStore = draft.preferredStore,
                    }, Thumbnail(draft), Previews(draft), draft.idempotencyKey, null, cancellation);
                    draft.assetId = created.id; draft.newAsset = false; draft.picturesSent = true; draft.Save();
                }
                else
                {
                    var mine = await GalleryApi.CreatorAssetsAsync(cancellation);
                    bool join = mine?.assets.FirstOrDefault(a => a.id == draft.assetId)?.inGallery == false;
                    // An existing asset keeps its card picture unless the creator took a new one.
                    string picture = OwnPicture(draft);
                    var previews = Previews(draft);
                    bool pictures = !draft.picturesSent && (picture != null || previews.Count > 0);
                    if (join || pictures)
                    {
                        progress(join ? "Adding it to the gallery…" : "Sending its pictures…");
                        var metadata = new Dictionary<string, object>();
                        if (join) metadata["type"] = draft.type;
                        if (pictures) metadata["replacePreviews"] = draft.replacePreviews;
                        await GalleryApi.UpdateAssetAsync(draft.assetId, metadata, pictures ? picture : null, pictures ? previews : null, cancellation);
                        if (pictures) { draft.picturesSent = true; draft.Save(); }
                    }
                }
                if (draft.releaseId <= 0)
                {
                    progress("Creating version " + draft.version + "…");
                    var release = await GalleryApi.CreateReleaseAsync(draft.assetId, draft.version.Trim(), draft.title, draft.changelog, draft.scope);
                    draft.releaseId = release.id; draft.Save();
                }
                else await GalleryApi.UpdateReleaseAsync(draft.releaseId, new { version = draft.version.Trim(), title = draft.title, changelog = draft.changelog, scope = draft.scope });

                foreach (var variant in draft.variants)
                {
                    if (variant.uploadedId > 0 && variant.uploadedSha == variant.report.sha256) continue;
                    if (!File.Exists(variant.report.packagePath)) throw new InvalidOperationException(variant.label + ": build its package again (the file is gone).");
                    var uploaded = await GalleryApi.UploadVariantAsync(draft.releaseId, variant.Metadata(), variant.report.packagePath,
                        (bytes, total) => progress($"Uploading {variant.label} · {OrbitersTransfer.Describe(bytes, total)}"), cancellation);
                    variant.uploadedId = uploaded?.id ?? 0; variant.uploadedSha = variant.report.sha256;
                    draft.Save();
                }
                progress("Publishing…");
                var published = await GalleryApi.PublishReleaseAsync(draft.releaseId, draft.publishListing);
                published ??= new Created();
                published.id = draft.assetId; published.version = draft.version;
                GalleryCreatorDraft.Clear();
                GalleryCache.Clear();
                // On Orbiters now: the copies taken for this listing go (a file chosen elsewhere stays).
                foreach (string path in Previews(draft).Append(draft.thumbnailPath)) GalleryPictures.Delete(path);
                Debug.Log($"[My Avatar] {draft.name} {draft.version} is in the gallery" + (published.listed ? "." : " (its listing stays private until you publish it on Orbiters)."));
                return published;
            }
            finally { draft.Save(); }
        }

        private static string Url(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string OwnPicture(GalleryCreatorDraft draft) =>
            !string.IsNullOrEmpty(draft.thumbnailPath) && File.Exists(draft.thumbnailPath) ? draft.thumbnailPath : null;

        // The asset page's pictures that still exist, at most the 8 the server takes in one request.
        private static List<string> Previews(GalleryCreatorDraft draft) =>
            draft.previewPaths.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Take(8).ToList();

        // The asset's picture: the draft's own image, else the first package's first prefab as My Avatar renders it.
        private static string Thumbnail(GalleryCreatorDraft draft)
        {
            if (OwnPicture(draft) is string own) return own;
            try
            {
                var prefab = draft.variants.SelectMany(v => v.report?.prefabs ?? new List<GallerySetupPrefab>())
                    .Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(p.guid))).FirstOrDefault(p => p != null);
                if (prefab == null) return null;
                var texture = ModelThumbnails.Get(prefab, ModelThumbnails.KeyFor(AssetDatabase.GetAssetPath(prefab)));
                if (texture == null) texture = AssetPreview.GetAssetPreview(prefab);
                if (texture == null) return null;
                var readable = new RenderTexture(texture.width, texture.height, 0);
                Graphics.Blit(texture, readable);
                var previous = RenderTexture.active;
                RenderTexture.active = readable;
                var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply();
                RenderTexture.active = previous;
                readable.Release();
                string path = Path.Combine(GalleryPackager.Folder, "thumbnail-" + Guid.NewGuid().ToString("N") + ".png");
                Directory.CreateDirectory(GalleryPackager.Folder);
                File.WriteAllBytes(path, copy.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(copy);
                return path;
            }
            catch (Exception ex) { Debug.Log("[My Avatar] No thumbnail for the new asset: " + ex.Message); return null; }
        }
    }
}
