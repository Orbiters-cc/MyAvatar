using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Net;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
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

        /// <summary>
        /// The build or publish running now ("Publishing", "Building PC"…), null when none: one at a time in the whole
        /// editor, whichever window or MCP call started it. Released when it ends, and by a script reload.
        /// </summary>
        internal static string Running { get; private set; }
        internal static event Action RunningChanged;

        [InitializeOnLoadMethod]
        private static void ReleaseOnReload() => AssemblyReloadEvents.beforeAssemblyReload += () => Running = null;

        private static void Begin(string what)
        {
            if (Running != null) throw new InvalidOperationException(Running + " is still running; try again once it ends.");
            Running = what;
            RunningChanged?.Invoke();
        }

        private static void Done()
        {
            Running = null;
            RunningChanged?.Invoke();
        }

        /// <summary>Builds a package and keeps the setups that still match its prefabs (one setup with them all at first).</summary>
        internal static async Task<GalleryPackager.Report> BuildAsync(GalleryCreatorDraft draft, GalleryVariantDraft variant, MyAvatar avatar)
        {
            Begin("Building " + variant.Label);
            try
            {
                string name = (string.IsNullOrWhiteSpace(draft.name) ? "asset" : draft.name) + " " + variant.Label;
                var report = variant.source == "package"
                    ? (string.IsNullOrEmpty(variant.packagePath) ? null : await GalleryPackager.FromPackageAsync(variant.packagePath, name, avatar))
                    : GalleryPackager.FromPrefabs(variant.prefabGuids.Select(g => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(g))).ToList(), name, avatar);
                if (report == null) throw new InvalidOperationException("Choose the package to publish first.");
                variant.report = report;
                var guids = new HashSet<string>(report.prefabs.Select(p => p.guid));
                variant.manifest.setups.ForEach(s => s.prefabs.RemoveAll(p => !guids.Contains(p.guid)));
                variant.manifest.setups.RemoveAll(s => s.prefabs.Count == 0);
                if (variant.manifest.setups.Count == 0)
                    variant.manifest.setups.Add(new GallerySetup { key = "default", label = variant.Label,
                        prefabs = report.prefabs.Take(variant.source == "package" ? 1 : 16).Select(Copy).ToList() });
                variant.manifest.FixDefault();
                draft.Save();
                return report;
            }
            finally { Done(); }
        }

        internal static GallerySetupPrefab Copy(GallerySetupPrefab p) => new GallerySetupPrefab { guid = p.guid, path = p.path, name = p.name, attach = new GalleryAttach() };

        /// <summary>Installs a built package on the avatar the way a buyer would.</summary>
        internal static GalleryJob Test(GalleryCreatorDraft draft, GalleryVariantDraft variant, MyAvatar avatar)
        {
            if (variant.report == null || !variant.report.Publishable) throw new InvalidOperationException(variant.Label + ": build a publishable package first.");
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
            if (draft.DuplicateLabel() is string twice) return $"Two packages are named “{twice}”: give each its own name.";
            int previews = Previews(draft, int.MaxValue).Count;
            if (previews > GalleryCreatorDraft.MaxPreviews)
                return $"The asset's page takes {GalleryCreatorDraft.MaxPreviews} pictures besides the card's: remove {previews - GalleryCreatorDraft.MaxPreviews}.";
            var unbuilt = draft.variants.FirstOrDefault(v => v.report == null || !v.report.Publishable);
            return unbuilt == null ? null : unbuilt.Label + (unbuilt.report == null ? ": build its package." : ": its package needs changes before it can be published.");
        }

        /// <summary>
        /// Sends the draft to Orbiters and publishes the version. A retry skips what already reached that version unchanged.
        /// Once published the draft ends: the next one starts empty.
        /// </summary>
        internal static async Task<Created> PublishAsync(GalleryCreatorDraft draft, Action<string> progress, CancellationToken cancellation)
        {
            string missing = Missing(draft);
            if (missing != null) throw new InvalidOperationException(missing);
            if (!draft.rights) throw new InvalidOperationException("Confirm that you own the rights to everything this version distributes.");
            // Its ids belong to the server and account it was prepared with: never sent to another.
            if (draft.Bind(GalleryApi.Context)) { draft.Save(); throw new InvalidOperationException(draft.message); }
            string context = draft.context;
            void Same() { if (GalleryApi.Context != context) throw new InvalidOperationException("The Orbiters account or server changed while publishing: publish again."); }
            Begin("Publishing");
            progress ??= _ => { };
            bool published = false;
            try
            {
                if (draft.assetId <= 0)
                {
                    progress("Creating " + draft.name + "…");
                    string thumbnail = Thumbnail(draft, out bool generated);
                    Created created;
                    try
                    {
                        Same();
                        created = await GalleryApi.CreateAssetAsync(new
                        {
                            name = draft.name.Trim(), type = draft.type, shortDescription = draft.shortDescription, description = draft.description,
                            free = draft.free, priceCents = draft.free ? 0 : draft.priceCents, currency = draft.currency, gumroadLink = Url(draft.gumroad), jinxxyLink = Url(draft.jinxxy),
                            preferredStore = draft.preferredStore,
                        }, thumbnail, Previews(draft), draft.idempotencyKey, null, cancellation);
                    }
                    finally { if (generated) DeleteFile(thumbnail); }
                    if (created == null || created.id <= 0) throw new InvalidOperationException("Orbiters did not confirm the new asset. Publish again.");
                    // The asset exists: its creation request is spent, a later new asset sends its own.
                    draft.assetId = created.id; draft.newAsset = false; draft.picturesSent = true; draft.idempotencyKey = GalleryCreatorDraft.NewKey();
                    draft.Save();
                }
                else
                {
                    var mine = await GalleryApi.CreatorAssetsAsync(cancellation);
                    var asset = mine?.assets.FirstOrDefault(a => a.id == draft.assetId);
                    bool join = asset?.inGallery == false;
                    // A version published or deleted meanwhile takes no more changes: the draft makes a new one.
                    if (draft.Release > 0 && asset?.releases.FirstOrDefault(r => r.id == draft.Release)?.status != "draft") { draft.UseRelease(0); draft.Save(); }
                    // An existing asset keeps its card picture unless the creator took a new one.
                    string picture = OwnPicture(draft);
                    var previews = Previews(draft);
                    bool pictures = !draft.picturesSent && (picture != null || previews.Count > 0);
                    if (join || pictures)
                    {
                        progress(join ? "Adding it to the gallery…" : "Sending its pictures…");
                        Same();
                        var metadata = new Dictionary<string, object>();
                        if (join) metadata["type"] = draft.type;
                        if (pictures) metadata["replacePreviews"] = draft.replacePreviews;
                        await GalleryApi.UpdateAssetAsync(draft.assetId, metadata, pictures ? picture : null, pictures ? previews : null, cancellation);
                        if (pictures) { draft.picturesSent = true; draft.Save(); }
                    }
                }
                // A version of the asset targeted now; Orbiters gives back the creator's draft of that number when an
                // earlier answer was lost. Either way it takes the draft's number, title, changelog and scope.
                int release = draft.Release;
                var changes = new { version = draft.version.Trim(), title = draft.title, changelog = draft.changelog, scope = draft.scope };
                Same();
                if (release <= 0)
                {
                    progress("Creating version " + draft.version + "…");
                    var created = await GalleryApi.CreateReleaseAsync(draft.assetId, changes.version, draft.title, draft.changelog, draft.scope);
                    if (created == null || created.id <= 0) throw new InvalidOperationException("Orbiters did not confirm the new version. Publish again.");
                    draft.UseRelease(created.id); draft.Save();
                    release = created.id;
                    if (!created.created) await GalleryApi.UpdateReleaseAsync(draft.assetId, release, changes);
                }
                else await GalleryApi.UpdateReleaseAsync(draft.assetId, release, changes);

                var sent = new List<int>();
                foreach (var variant in draft.variants.ToList())
                {
                    variant.manifest.FixDefault();
                    // Unchanged since it reached this version (and still its own package there): not sent again.
                    if (variant.UploadedTo(release) && !sent.Contains(variant.uploadedId)) { sent.Add(variant.uploadedId); continue; }
                    if (!File.Exists(variant.report.packagePath)) throw new InvalidOperationException(variant.Label + ": build its package again (the file is gone).");
                    Same();
                    var uploaded = await GalleryApi.UploadVariantAsync(draft.assetId, release, variant.Metadata(), variant.report.packagePath,
                        (bytes, total) => progress($"Uploading {variant.Label} · {OrbitersTransfer.Describe(bytes, total)}"), cancellation);
                    if (uploaded == null || uploaded.id <= 0) throw new InvalidOperationException(variant.Label + ": Orbiters did not confirm the upload. Publish again.");
                    variant.MarkUploaded(uploaded.id, release);
                    sent.Add(uploaded.id);
                    draft.Save();
                }
                progress("Publishing…");
                Same();
                // Exactly these packages: the version's others (renamed or removed here since) leave it first. Its
                // customizable parts go with it.
                string partsFile = draft.variants.Select(v => v.report?.partsFile).FirstOrDefault(f => !string.IsNullOrEmpty(f) && File.Exists(f));
                var answer = await GalleryApi.PublishReleaseAsync(draft.assetId, release, draft.publishListing, sent, partsFile != null ? PartVariants.ReleaseParts(partsFile) : null) ?? new Created();
                answer.id = draft.assetId; answer.version = draft.version;
                published = true;
                if (partsFile != null)
                {
                    string prefab = draft.variants.Select(v => v.report?.partsPrefab).FirstOrDefault(p => !string.IsNullOrEmpty(p));
                    string missed = await PostBaseVariantsAsync(draft.assetId, prefab, partsFile, progress, cancellation);
                    if (missed != null) Debug.LogWarning("[My Avatar] " + draft.name + " " + draft.version + " is published, but " + missed);
                }
                draft.End();
                GalleryCache.Clear();
                // On Orbiters now: the copies taken for this listing go (a file chosen elsewhere stays).
                foreach (string path in draft.previewPaths.Append(draft.thumbnailPath)) GalleryPictures.Delete(path);
                Debug.Log($"[My Avatar] {draft.name} {draft.version} is in the gallery" + (answer.listed ? "." : " (its listing stays private until you publish it on Orbiters)."));
                return answer;
            }
            finally
            {
                if (!published) draft.Save();
                Done();
            }
        }

        private static string Url(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// Posts the parts' base variants (what the package ships) as the creator's official variants, once the version is
        /// published; reposting one unchanged changes nothing. Null when all went, else what did not.
        /// </summary>
        internal static async Task<string> PostBaseVariantsAsync(int assetId, string prefabPath, string partsFile, Action<string> progress, CancellationToken cancellation)
        {
            var definition = ItemParts.Read(partsFile);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (definition == null || prefab == null) return "its parts file or prefab could not be read, so its base variants were not posted.";
            var failed = new List<string>();
            foreach (var part in definition.parts)
            foreach (var variant in ItemParts.Bases(partsFile, part))
            {
                try
                {
                    progress?.Invoke("Posting " + part.Label.ToLowerInvariant() + " · " + variant.name + "…");
                    var packed = await ItemParts.PackAsync(prefab, definition, prefabPath, partsFile, part, variant);
                    var placed = new ItemParts.Placed { PrefabPath = prefabPath, PartsPath = partsFile, Definition = definition, Part = part };
                    await PartVariants.PostAsync(assetId, placed, packed, true, null, cancellation);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { failed.Add(part.Label + " › " + variant.name + " (" + ex.Message + ")"); }
            }
            return failed.Count == 0 ? null : "these base variants were not posted: " + string.Join("; ", failed);
        }

        private static void DeleteFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Only pictures taken in through GalleryPictures are sent (the window and MCP both put them there), whatever the
        // saved draft says.
        private static string OwnPicture(GalleryCreatorDraft draft) =>
            GalleryPictures.Owned(draft.thumbnailPath) && File.Exists(draft.thumbnailPath) ? draft.thumbnailPath : null;

        // The asset page's pictures that still exist, at most the ones the server takes in one request.
        private static List<string> Previews(GalleryCreatorDraft draft, int max = GalleryCreatorDraft.MaxPreviews) =>
            draft.previewPaths.Where(p => GalleryPictures.Owned(p) && File.Exists(p)).Take(max).ToList();

        // The asset's picture: the draft's own image, else the first package's first prefab as My Avatar renders it, in
        // a file made for this request (generated) that the caller deletes.
        private static string Thumbnail(GalleryCreatorDraft draft, out bool generated)
        {
            generated = false;
            if (OwnPicture(draft) is string own) return own;
            RenderTexture readable = null, previous = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                var prefab = draft.variants.SelectMany(v => v.report?.prefabs ?? new List<GallerySetupPrefab>())
                    .Select(p => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(p.guid))).FirstOrDefault(p => p != null);
                if (prefab == null) return null;
                var texture = ModelThumbnails.Get(prefab, ModelThumbnails.KeyFor(AssetDatabase.GetAssetPath(prefab)));
                if (texture == null) texture = AssetPreview.GetAssetPreview(prefab);
                if (texture == null) return null;
                readable = RenderTexture.GetTemporary(texture.width, texture.height, 0);
                Graphics.Blit(texture, readable);
                RenderTexture.active = readable;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply();
                string path = Path.Combine(GalleryPackager.Folder, "thumbnail-" + Guid.NewGuid().ToString("N") + ".png");
                Directory.CreateDirectory(GalleryPackager.Folder);
                File.WriteAllBytes(path, copy.EncodeToPNG());
                generated = true;
                return path;
            }
            catch (Exception ex) { Debug.Log("[My Avatar] No thumbnail for the new asset: " + ex.Message); return null; }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null) RenderTexture.ReleaseTemporary(readable);
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
            }
        }
    }
}
