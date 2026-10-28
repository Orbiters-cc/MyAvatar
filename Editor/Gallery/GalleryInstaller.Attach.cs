using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    internal static partial class GalleryInstaller
    {
        // ---- What the import would change ----

        private static async Task PreviewImport(GalleryJob job, CancellationToken cancellation)
        {
            var index = await Task.Run(() => UnityPackageIndex.Read(job.packagePath), cancellation);
            var preview = await UnityPackagePreview.CreateAsync(job.packagePath, index, cancellation);
            string root = GalleryLedger.ProjectRoot;
            // What the project had before: kept by Cleanup whatever the gallery does to it. Files another gallery
            // installation brought (an earlier version of this one) stay the gallery's: this installation owns them too.
            var galleryOwned = new HashSet<string>(GalleryLedger.Entries.SelectMany(e => e.files).Select(f => f.guid), StringComparer.OrdinalIgnoreCase);
            job.preexisting = await Task.Run(() => preview.Unchanged.Concat(preview.Replaced).Where(c => !galleryOwned.Contains(c.Guid))
                .Select(c => FileRecord(root, c.Guid, c.ProjectPath)).Where(f => f != null).ToList(), cancellation);
            job.importGuids = preview.ImportGuids.ToList();
            // What each file holds in the package: the import is done once the project's files are these.
            var hashes = await Task.Run(() => UnityPackageFiles.AssetHashes(job.packagePath), cancellation);
            job.expected = job.importGuids.Where(hashes.ContainsKey).Select(g => new GalleryFile { guid = g, hash = hashes[g] }).ToList();
            if (preview.HasConflicts && !job.accepted.Contains("conflicts"))
            {
                Ask(job, "conflicts", $"Adding {job.assetName} replaces {UnityPackagePreview.Summary(preview.Replaced)} your project already has, for every avatar and scene using them.",
                    preview.Replaced.Select(c => c.ProjectPath + (c.Moved ? "  (the package names it " + c.PackagePath + ")" : "")));
                return;
            }
            // Entries outside Assets/ (copies of VPM packages) are never imported: a filtered copy holds the rest.
            if (preview.Skipped.Count > 0)
            {
                job.importPath = Path.Combine(GalleryApi.StagingFolder, job.id + "-assets.unitypackage");
                var keep = new HashSet<string>(job.importGuids, StringComparer.OrdinalIgnoreCase);
                await Task.Run(() => UnityPackageFiles.CopyEntries(job.packagePath, job.importPath, keep), cancellation);
            }
            else job.importPath = job.packagePath;
            Move(job, GalleryJob.Import, "Importing…", 0.55f);
        }

        internal static GalleryFile FileRecord(string root, string guid, string path)
        {
            try
            {
                string full = Path.Combine(root, path);
                if (!System.IO.File.Exists(full)) return null;
                return new GalleryFile { guid = guid, path = path, hash = UnityPackageFiles.FileHash(full), bytes = new FileInfo(full).Length };
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // ---- Import (Unity's native importer: it runs to the end once started) ----

        private static async Task ImportPackage(GalleryJob job, CancellationToken cancellation)
        {
            if (job.importing)
            {
                // A reload or restart interrupted the wait: done only when every file is the package's (an update finds
                // the older version's files under the same GUIDs); otherwise it imports again.
                job.importing = false;
                if ((await Mismatched(job, cancellation)).Count == 0) { Imported(job); return; }
            }
            if (string.IsNullOrEmpty(job.importPath) || !System.IO.File.Exists(job.importPath)) { Move(job, GalleryJob.Preview, "Checking your project…", 0.5f); return; }
            if (job.importGuids.Count == 0) { Imported(job); return; }
            job.status = "Importing " + job.assetName + "…"; job.progress = 0.6f;
            GalleryJournal.Save(job);
            // Cancelling while another package imports stops this one before it starts; once Unity's importer started,
            // only once it has finished.
            try { await UnityPackageImport.ImportAsync(job.importPath, cancellation, () => { job.importing = true; GalleryJournal.Save(job); }); }
            catch (OperationCanceledException) when (job.importing)
            {
                job.importing = false;
                job.imported = true;
                RecordFiles(job, onAvatar: false);
                throw;
            }
            job.importing = false;
            while (EditorApplication.isCompiling || EditorApplication.isUpdating) await Task.Delay(150);
            var wrong = await Mismatched(job, CancellationToken.None);
            if (wrong.Count > 0)
            {
                // Whatever Unity wrote is the gallery's (Cleanup knows it); Retry imports the package again.
                job.imported = false;
                RecordFiles(job, onAvatar: false);
                Fail(job, $"Unity did not import every file of {job.assetName} ({string.Join(", ", wrong.Take(3))}{(wrong.Count > 3 ? $" and {wrong.Count - 3} more" : "")}). Retry imports it again.");
                return;
            }
            job.imported = true;
            RecordFiles(job, onAvatar: false);
            if (job.cancelRequested) { Finish(job, GalleryJob.Cancelled, CancelledText(job)); return; }
            Move(job, GalleryJob.Attach, "Adding it to the avatar…", 0.85f);
        }

        private static void Imported(GalleryJob job)
        {
            job.imported = true;
            RecordFiles(job, onAvatar: false);
            Move(job, GalleryJob.Attach, "Adding it to the avatar…", 0.85f);
        }

        /// <summary>The package's files the project does not hold as the package has them (missing or other content).</summary>
        internal static async Task<List<string>> Mismatched(GalleryJob job, CancellationToken cancellation)
        {
            string root = GalleryLedger.ProjectRoot;
            var files = job.expected.Select(f => (f.hash, path: AssetDatabase.GUIDToAssetPath(f.guid), f.guid)).ToList();
            return await Task.Run(() => files.Where(f =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(f.path)) return true;
                string full = Path.Combine(root, f.path);
                if (Directory.Exists(full)) return false;
                try { return !System.IO.File.Exists(full) || !string.Equals(UnityPackageFiles.FileHash(full), f.hash, StringComparison.OrdinalIgnoreCase); }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }).Select(f => string.IsNullOrEmpty(f.path) ? f.guid : f.path).ToList(), cancellation);
        }

        /// <summary>
        /// Records the files the import brought in the ledger as soon as they are in the project, also when nothing ends up
        /// on the avatar (a failed or cancelled installation): Cleanup can find them. <paramref name="onAvatar"/>: the
        /// installation is on its avatar now (its files are in use).
        /// </summary>
        private static void RecordFiles(GalleryJob job, bool onAvatar)
        {
            string root = GalleryLedger.ProjectRoot;
            var preexisting = new HashSet<string>(job.preexisting.Select(f => f.guid), StringComparer.OrdinalIgnoreCase);
            GalleryLedger.Record(new GalleryLedger.Entry
            {
                installId = job.id, avatar = job.avatar, avatarName = job.avatarName, assetId = job.assetId, assetName = job.assetName, version = job.version,
                releaseId = job.releaseId, variantId = job.variantId, installedAt = DateTime.UtcNow.Ticks, removedAt = onAvatar ? 0 : DateTime.UtcNow.Ticks,
                preexisting = job.preexisting,
                files = job.importGuids.Where(g => !preexisting.Contains(g)).Select(g => FileRecord(root, g, AssetDatabase.GUIDToAssetPath(g))).Where(f => f != null).ToList(),
            });
        }

        // ---- Attach exactly what the manifest declares ----

        private static void Attach(GalleryJob job, MyAvatar avatar)
        {
            // A retry finds what an earlier run placed: never a second copy, only the prefabs missing from it are placed.
            var existing = Of(avatar, job.id);
            var setup = job.manifest.Setup(job.setupKey);
            var unplaced = Unplaced(setup, existing);
            if (unplaced.Count == 0) { job.placed = existing.Select(a => GlobalObjectId.GetGlobalObjectIdSlow(a).ToString()).ToList(); Move(job, GalleryJob.Verify, "Checking it…", 0.95f); return; }
            var prefabs = unplaced.Select(p => (item: p, asset: AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(p.guid)))).ToList();
            var missing = prefabs.Where(p => p.asset == null).Select(p => p.item.name).ToList();
            if (missing.Count > 0)
            {
                // Retry imports the package again instead of looking for the same files once more.
                job.imported = false;
                Fail(job, $"Unity did not import {string.Join(", ", missing)}. Retry imports it again; Cleanup removes the files it brought.");
                return;
            }
            // The bones the creator put its prefabs on: an avatar without one of them gets nothing.
            var bones = new Dictionary<GallerySetupPrefab, Transform>();
            Orbiters.Toolkit.Armature.AvatarBoneIndex index = null;
            foreach (var (item, _) in prefabs)
            {
                if (item.attach?.mode != "parent" || string.IsNullOrEmpty(item.attach.bone)) continue;
                index = index ?? Orbiters.Toolkit.Armature.AvatarBoneIndex.Build(avatar.transform,
                    Orbiters.Toolkit.Editor.Posing.AvatarSkeleton.Bones(avatar.transform, AttachmentPlanner.Body(avatar.transform)));
                var target = Enum.TryParse(item.attach.bone, true, out HumanBodyBones bone) && bone != HumanBodyBones.LastBone ? index.Humanoid(bone) : null;
                if (target == null) { Fail(job, $"{item.name} goes on the avatar's {ObjectNames.NicifyVariableName(item.attach.bone)}, which {job.avatarName} does not have. Nothing was added to the avatar."); return; }
                bones[item] = target;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("My Avatar: add " + job.assetName);
            var placed = new List<OrbitersAttachment>();
            try
            {
                foreach (var (item, asset) in prefabs)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset, avatar.transform);
                    Undo.RegisterCreatedObjectUndo(instance, "Add " + item.name);
                    AttachmentHooks.Prepare(instance, avatar.transform);
                    var plan = AttachmentPlanner.Analyze(instance, avatar.transform);
                    if (plan.Kind == AttachmentKind.Empty) { Undo.DestroyObjectImmediate(instance); throw new InvalidOperationException(item.name + " has nothing to show on the avatar."); }
                    var attachment = AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, Source = item.guid, Variant = AssetDatabase.GetAssetPath(asset) });
                    if (bones.TryGetValue(item, out var target)) AttachmentInstaller.Retarget(attachment, target, snap: false);
                    else if ((plan.Kind == AttachmentKind.Clothing || plan.Kind == AttachmentKind.Configured) && item.attach?.mode != "configured")
                        AttachmentFit.Fit(attachment, avatar.transform);
                    attachment.gallery = Receipt(job, setup);
                    // Its creator says it is not worn on the body, or it is a prop this Toolkit fits itself (N4N0, the drawing
                    // pen): never asked about a refit, also once later Toolkit versions no longer recognise this version of it.
                    attachment.neverRefit = item.attach?.refit == false || AttachmentHooks.Fits(instance);
                    EditorUtility.SetDirty(attachment);
                    placed.Add(attachment);
                }
            }
            catch (Exception ex)
            {
                // Scene changes of a failed attach are rolled back; imported files stay for Cleanup.
                Undo.CollapseUndoOperations(group);
                Undo.RevertAllDownToGroup(group);
                Fail(job, ex.Message + " Nothing was added to the avatar; its files are in the project (Cleanup removes them).");
                return;
            }
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
            job.placed = existing.Concat(placed).Select(a => GlobalObjectId.GetGlobalObjectIdSlow(a).ToString()).ToList();
            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            Move(job, GalleryJob.Verify, "Checking it…", 0.95f);
        }

        private static OrbitersAttachment.GalleryReceipt Receipt(GalleryJob job, GallerySetup setup) => new OrbitersAttachment.GalleryReceipt
        {
            assetId = job.assetId, releaseId = job.releaseId, variantId = job.variantId, assetName = job.assetName, version = job.version,
            setup = setup?.key, sha256 = job.sha256, creatorName = job.creatorName, installId = job.id, installedAtTicks = DateTime.UtcNow.Ticks,
        };

        // ---- Verify: the avatar wears it; only then is it "Installed" ----

        private static List<OrbitersAttachment> Of(MyAvatar avatar, string installId) =>
            AttachmentInstaller.Installed(avatar.transform).Where(a => a && a.gallery != null && a.gallery.installId == installId).ToList();

        /// <summary>The setup's prefabs that none of <paramref name="placed"/> is a copy of (each copy counts once).</summary>
        internal static List<GallerySetupPrefab> Unplaced(GallerySetup setup, IEnumerable<OrbitersAttachment> placed)
        {
            var left = placed.ToList();
            var unplaced = new List<GallerySetupPrefab>();
            foreach (var prefab in setup?.prefabs ?? new List<GallerySetupPrefab>())
            {
                var copy = left.FirstOrDefault(a => string.Equals(a.source, prefab.guid, StringComparison.OrdinalIgnoreCase));
                if (copy != null) left.Remove(copy); else unplaced.Add(prefab);
            }
            return unplaced;
        }

        /// <summary>
        /// The older copies and the new ones of an update, each with the one of the same prefab (its source), else of the
        /// same name (a version built into a folder of its own has new GUIDs), else in their order.
        /// </summary>
        internal static List<(OrbitersAttachment old, OrbitersAttachment now)> Pairs(List<OrbitersAttachment> olds, List<OrbitersAttachment> news)
        {
            var pairs = new List<(OrbitersAttachment, OrbitersAttachment)>();
            var left = news.ToList();
            var unmatched = olds.ToList();
            void Match(Func<OrbitersAttachment, OrbitersAttachment, bool> same)
            {
                foreach (var old in unmatched.ToList())
                {
                    var now = left.FirstOrDefault(n => same(old, n));
                    if (now == null) continue;
                    left.Remove(now); unmatched.Remove(old);
                    pairs.Add((old, now));
                }
            }
            Match((old, now) => !string.IsNullOrEmpty(old.source) && string.Equals(now.source, old.source, StringComparison.OrdinalIgnoreCase));
            Match((old, now) => old.name == now.name);
            for (int i = 0; i < unmatched.Count && i < left.Count; i++) pairs.Add((unmatched[i], left[i]));
            return pairs;
        }

        /// <summary>
        /// The new copy where the older one was: put on the same bone, at the same place (one attached to a bone keeps where
        /// it was moved), shown or hidden as it was, with the name it was given on its card. A fitted garment is fitted again
        /// (its fit follows the new version's meshes) and keeps only whether it shows and its name.
        /// </summary>
        internal static void CarryPlacement(OrbitersAttachment old, OrbitersAttachment now)
        {
            if (!old || !now) return;
            if (old.mode == OrbitersAttachment.AttachMode.Parent && now.mode == OrbitersAttachment.AttachMode.Parent && old.parent != null)
            {
                if (now.parent != old.parent) AttachmentInstaller.Retarget(now, old.parent, snap: false);
                if (now.transform.parent == old.transform.parent)
                {
                    Undo.RecordObject(now.transform, "Keep " + now.name + "'s place");
                    now.transform.localPosition = old.transform.localPosition;
                    now.transform.localRotation = old.transform.localRotation;
                    now.transform.localScale = old.transform.localScale;
                }
            }
            if (now.gameObject.activeSelf != old.gameObject.activeSelf)
            {
                Undo.RecordObject(now.gameObject, "Keep " + now.name + " shown or hidden");
                now.gameObject.SetActive(old.gameObject.activeSelf);
            }
            if (!string.IsNullOrEmpty(old.displayName) && old.displayNameFor == old.name && string.IsNullOrEmpty(now.displayName))
            {
                Undo.RecordObject(now, "Keep " + now.name + "'s name");
                now.displayName = old.displayName;
                now.displayNameFor = now.name;
            }
            EditorUtility.SetDirty(now);
        }

        private static void Verify(GalleryJob job, MyAvatar avatar)
        {
            var attachments = Of(avatar, job.id);
            if (attachments.Count == 0) { Fail(job, "It was imported but is not on the avatar any more (removed or undone). Retry adds it again."); return; }
            // Every prefab its setup declares, not some of them.
            var absent = Unplaced(job.manifest.Setup(job.setupKey), attachments);
            if (absent.Count > 0) { Fail(job, $"{string.Join(", ", absent.Select(p => p.name))} {(absent.Count == 1 ? "is" : "are")} not on the avatar any more (removed or undone). Retry adds {(absent.Count == 1 ? "it" : "them")} again."); return; }
            if (attachments.Any(a => !a.GetComponentsInChildren<Renderer>(true).Any())) { Fail(job, "It is on the avatar but shows nothing: check its materials and meshes."); return; }
            var loose = attachments.FirstOrDefault(a => a.mode == OrbitersAttachment.AttachMode.Parent && (a.parent == null || !a.parent.IsChildOf(avatar.transform)));
            if (loose != null) { Fail(job, $"{loose.name} is not on a bone of {job.avatarName}. Retry adds it again."); return; }
            // An update replaces the previous installation only once the new one is in place.
            if (!string.IsNullOrEmpty(job.updateOf))
            {
                var olds = AttachmentInstaller.Installed(avatar.transform).Where(a => a && a.gallery?.installId == job.updateOf).ToList();
                // Each new copy keeps what was chosen for the older copy of the same prefab: its card's settings (N4N0's
                // text slots, an item's switches), the bone it was put on and where, whether it shows, the name it was given.
                foreach (var (old, now) in Pairs(olds, attachments))
                {
                    try { AttachmentHooks.CarryOptions(old.gameObject, now.gameObject); }
                    catch (Exception ex) { Debug.LogWarning("[My Avatar] " + job.assetName + " was updated without its earlier settings (set them again on its card): " + ex.Message); }
                    try { CarryPlacement(old, now); }
                    catch (Exception ex) { Debug.LogWarning("[My Avatar] " + job.assetName + " was updated without its earlier place (put it back with Bone ▾): " + ex.Message); }
                }
                foreach (var old in olds) AttachmentInstaller.Remove(old);
                GalleryLedger.MarkRemoved(job.updateOf);
            }
            RecordFiles(job, onAvatar: true);
            Finish(job, GalleryJob.Complete, job.updateOf != null ? $"Updated to {job.version}." : $"{job.assetName} {job.version} is on {job.avatarName}.");
            AccessoryService.NotifyChanged(avatar);
        }

        // ---- Installed gallery assets ----

        /// <summary>The gallery assets on the avatar: one receipt per installation, with its attachments.</summary>
        internal static List<(OrbitersAttachment.GalleryReceipt receipt, List<OrbitersAttachment> attachments)> Installed(MyAvatar avatar)
        {
            if (!avatar) return new List<(OrbitersAttachment.GalleryReceipt, List<OrbitersAttachment>)>();
            return AttachmentInstaller.Installed(avatar.transform).Where(a => a && a.gallery != null && a.gallery.Installed)
                .GroupBy(a => a.gallery.installId ?? a.gallery.assetId.ToString())
                .Select(g => (g.First().gallery, g.ToList())).ToList();
        }

        /// <summary>
        /// Takes an installation off the avatar (one Undo step). Its files stay in the project: other avatars may use them,
        /// and Cleanup removes them once nothing does.
        /// </summary>
        internal static void Remove(MyAvatar avatar, string installId)
        {
            var attachments = AttachmentInstaller.Installed(avatar.transform).Where(a => a && a.gallery?.installId == installId).ToList();
            if (attachments.Count == 0) return;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("My Avatar: remove " + attachments[0].gallery.assetName);
            foreach (var attachment in attachments) AttachmentInstaller.Remove(attachment);
            Undo.CollapseUndoOperations(group);
            // Not saved: Cleanup still counts it as installed and keeps its files.
            try { GalleryLedger.MarkRemoved(installId); }
            catch (IOException ex) { Debug.LogWarning("[My Avatar] " + ex.Message + ": Cleanup keeps " + attachments[0].gallery.assetName + "'s files."); }
            foreach (var job in GalleryJournal.All.Where(j => j.id == installId && j.Terminal).ToList()) GalleryJournal.Remove(job);
            AccessoryService.NotifyChanged(avatar);
        }
    }
}
