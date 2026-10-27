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
            // What the project had before: kept by Cleanup whatever the gallery does to it.
            job.preexisting = await Task.Run(() => preview.Unchanged.Concat(preview.Replaced).Select(c => FileRecord(root, c.Guid, c.ProjectPath)).Where(f => f != null).ToList(), cancellation);
            job.importGuids = preview.ImportGuids.ToList();
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
                // A reload or restart interrupted the wait: the files decide whether the import happened.
                job.importing = false;
                if (job.importGuids.All(g => !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g)))) { job.imported = true; Move(job, GalleryJob.Attach, "Adding it to the avatar…", 0.85f); return; }
            }
            if (string.IsNullOrEmpty(job.importPath) || !System.IO.File.Exists(job.importPath)) { Move(job, GalleryJob.Preview, "Checking your project…", 0.5f); return; }
            if (job.importGuids.Count == 0) { job.imported = true; Move(job, GalleryJob.Attach, "Adding it to the avatar…", 0.85f); return; }
            job.status = "Importing " + job.assetName + "…"; job.progress = 0.6f;
            GalleryJournal.Save(job);
            // Cancelling now only stops the job once Unity's importer has finished.
            await UnityPackageImport.ImportAsync(job.importPath, CancellationToken.None, () => { job.importing = true; GalleryJournal.Save(job); });
            job.importing = false;
            job.imported = true;
            while (EditorApplication.isCompiling || EditorApplication.isUpdating) await Task.Delay(150);
            if (job.cancelRequested) { Finish(job, GalleryJob.Cancelled, CancelledText(job)); return; }
            Move(job, GalleryJob.Attach, "Adding it to the avatar…", 0.85f);
        }

        // ---- Attach exactly what the manifest declares ----

        private static void Attach(GalleryJob job, MyAvatar avatar)
        {
            // A retry finds what an earlier run placed: never a second copy.
            var existing = AttachmentInstaller.Installed(avatar.transform).Where(a => a && a.gallery != null && a.gallery.installId == job.id).ToList();
            if (existing.Count > 0) { job.placed = existing.Select(a => GlobalObjectId.GetGlobalObjectIdSlow(a).ToString()).ToList(); Move(job, GalleryJob.Verify, "Checking it…", 0.95f); return; }
            var setup = job.manifest.Setup(job.setupKey);
            var prefabs = setup.prefabs.Select(p => (item: p, asset: AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(p.guid)))).ToList();
            var missing = prefabs.Where(p => p.asset == null).Select(p => p.item.name).ToList();
            if (missing.Count > 0)
            {
                // Retry imports the package again instead of looking for the same files once more.
                job.imported = false;
                Fail(job, $"Unity did not import {string.Join(", ", missing)}. Retry imports it again; Cleanup removes the files it brought.");
                return;
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
                    if (item.attach?.mode == "parent" && !string.IsNullOrEmpty(item.attach.bone) && Enum.TryParse(item.attach.bone, true, out HumanBodyBones bone))
                    {
                        var target = Orbiters.Toolkit.Armature.AvatarBoneIndex.Build(avatar.transform,
                            Orbiters.Toolkit.Editor.Posing.AvatarSkeleton.Bones(avatar.transform, AttachmentPlanner.Body(avatar.transform))).Humanoid(bone);
                        if (target != null) AttachmentInstaller.Retarget(attachment, target, snap: false);
                    }
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
            job.placed = placed.Select(a => GlobalObjectId.GetGlobalObjectIdSlow(a).ToString()).ToList();
            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            Move(job, GalleryJob.Verify, "Checking it…", 0.95f);
        }

        private static OrbitersAttachment.GalleryReceipt Receipt(GalleryJob job, GallerySetup setup) => new OrbitersAttachment.GalleryReceipt
        {
            assetId = job.assetId, releaseId = job.releaseId, variantId = job.variantId, assetName = job.assetName, version = job.version,
            setup = setup?.key, sha256 = job.sha256, creatorName = job.creatorName, installId = job.id, installedAtTicks = DateTime.UtcNow.Ticks,
        };

        // ---- Verify: the avatar wears it; only then is it "Installed" ----

        /// <summary>
        /// The older copies and the new ones of an update, each with the one of the same prefab (its source), else in
        /// their order.
        /// </summary>
        internal static List<(OrbitersAttachment old, OrbitersAttachment now)> Pairs(List<OrbitersAttachment> olds, List<OrbitersAttachment> news)
        {
            var pairs = new List<(OrbitersAttachment, OrbitersAttachment)>();
            var left = news.ToList();
            var unmatched = new List<OrbitersAttachment>();
            foreach (var old in olds)
            {
                var now = string.IsNullOrEmpty(old.source) ? null : left.FirstOrDefault(n => string.Equals(n.source, old.source, StringComparison.OrdinalIgnoreCase));
                if (now == null) { unmatched.Add(old); continue; }
                left.Remove(now);
                pairs.Add((old, now));
            }
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
            var attachments = AttachmentInstaller.Installed(avatar.transform).Where(a => a && a.gallery != null && a.gallery.installId == job.id).ToList();
            if (attachments.Count == 0) { Fail(job, "It was imported but is not on the avatar any more (removed or undone). Retry adds it again."); return; }
            if (attachments.Any(a => !a.GetComponentsInChildren<Renderer>(true).Any())) { Fail(job, "It is on the avatar but shows nothing: check its materials and meshes."); return; }
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
            string root = GalleryLedger.ProjectRoot;
            var preexisting = new HashSet<string>(job.preexisting.Select(f => f.guid), StringComparer.OrdinalIgnoreCase);
            GalleryLedger.Record(new GalleryLedger.Entry
            {
                installId = job.id, avatar = job.avatar, avatarName = job.avatarName, assetId = job.assetId, assetName = job.assetName, version = job.version,
                releaseId = job.releaseId, variantId = job.variantId, installedAt = DateTime.UtcNow.Ticks, preexisting = job.preexisting,
                files = job.importGuids.Where(g => !preexisting.Contains(g)).Select(g => FileRecord(root, g, AssetDatabase.GUIDToAssetPath(g))).Where(f => f != null).ToList(),
            });
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
            GalleryLedger.MarkRemoved(installId);
            foreach (var job in GalleryJournal.All.Where(j => j.id == installId && j.Terminal).ToList()) GalleryJournal.Remove(job);
            AccessoryService.NotifyChanged(avatar);
        }
    }
}
