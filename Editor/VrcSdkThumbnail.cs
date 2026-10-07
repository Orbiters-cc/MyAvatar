using UnityEditor;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor.Elements;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace Orbiters.MyAvatar.Editor
{
    // Offers the My Avatar thumbnail to the VRChat SDK builder whenever it shows that avatar, through the SDK's public
    // thumbnail-selected event: the same path as choosing the file with "Select New Thumbnail". It only switches the
    // builder's avatar when asked to (Open), and a thumbnail the user picks in the SDK afterwards stays theirs.
    // VRChat refuses an image identical to the avatar's current one, and only at the end of the upload: an offer is
    // checked against VRChat first, and taken back when the blueprint ID changes to an avatar that already has it.
    [InitializeOnLoad]
    internal static class VrcSdkThumbnail
    {
        private static readonly TimeSpan RemoteFresh = TimeSpan.FromMinutes(10);
        private static readonly FieldInfo PendingField =
            typeof(VRCSdkControlPanelAvatarBuilder).GetField("_newThumbnailImagePath", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<string, (string md5, DateTime checkedUtc)> remoteHashes =
            new Dictionary<string, (string, DateTime)>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> remoteRequested = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static GameObject lastSelected, pendingSelection;
        private static string offered;
        private static double selectedAt;
        private static int revision;
        private static Texture2D offeredImage;

        static VrcSdkThumbnail() => EditorApplication.update += Watch;

        // Shows the SDK panel on this avatar; the thumbnail follows as soon as the builder has loaded it.
        internal static void Open(MyAvatar avatar)
        {
            pendingSelection = avatar.gameObject;
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
        }

        private static void Watch()
        {
            if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) { if (lastSelected) revision++; lastSelected = null; return; }
            if (pendingSelection)
            {
                if (Selected(builder) != pendingSelection) builder.SelectAvatar(pendingSelection);
                pendingSelection = null;
            }
            var selected = Selected(builder);
            if (selected != lastSelected) { lastSelected = selected; offered = null; offeredImage = null; selectedAt = EditorApplication.timeSinceStartup; revision++; }
            if (!selected) return;
            if (builder.BuildState == SdkBuildState.Building || builder.UploadState == SdkUploadState.Uploading) return;
            // Whatever thumbnail waits in the SDK, VRChat's copy is known ahead of Build & Publish (VrcSdkThumbnailCheck).
            string id = BlueprintId(selected);
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(Pending(builder))) RefreshRemote(id);
            var avatar = selected.GetComponent<MyAvatar>();
            if (!avatar) avatar = selected.GetComponentInChildren<MyAvatar>(true);
            string path = avatar ? AvatarThumbnail.FullPath(avatar.thumbnail) : null;
            // A new capture overwrites the same file, so the write time is part of what was offered. So is the blueprint
            // ID: the SDK clears it when VRChat doesn't answer for the avatar, then keeps a thumbnail offered meanwhile
            // through every reload, including the one that brings the ID back.
            string key = path == null ? null : path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks + "|" + BlueprintId(selected);
            if (key == null || key == offered) return;
            var block = VRCSdkControlPanel.window.rootVisualElement.Q<ThumbnailBlock>();
            // Wait until the builder finished loading this avatar's data. Loading may only start a few frames after the
            // switch, so the selection must also have settled.
            if (EditorApplication.timeSinceStartup - selectedAt < .75 || block?.Thumbnail == null || block.Thumbnail.Loading || block.OnNewThumbnailSelected == null) return;
            offered = key;
            Offer(builder, selected, avatar, block, path, key, ++revision);
        }

        // The SDK's getter throws once the avatar it shows was destroyed (a build copy or a test avatar it picked up).
        // Thrown here every frame, it also stopped every update callback registered after this one.
        private static GameObject Selected(IVRCSdkAvatarBuilderApi builder)
        {
            try { return builder.SelectedAvatar; }
            catch (MissingReferenceException) { return null; }
        }

        private static string BlueprintId(GameObject avatar) => avatar ? avatar.GetComponent<VRC.Core.PipelineManager>()?.blueprintId?.Trim() : null;

        private static async void Offer(IVRCSdkAvatarBuilderApi builder, GameObject selected, MyAvatar avatar,
            ThumbnailBlock block, string path, string key, int request)
        {
            bool edited = false;
            EventHandler<string> changed = (_, __) => edited = true;
            block.OnNewThumbnailSelected += changed;
            try
            {
                string id = BlueprintId(selected);
                if (!string.IsNullOrEmpty(id))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (await AlreadyUploaded(id, path, timeout.Token))
                    {
                        // Offered while the ID was missing: take it back, or the upload ends on "This file was already uploaded".
                        if (!edited && SamePath(Pending(builder), path)) Withdraw(builder);
                        return;
                    }
                }
                // Never replace a thumbnail already chosen in the SDK.
                if (block.Thumbnail.CurrentImageTexture != null && block.Thumbnail.CurrentImageTexture != offeredImage) return;
                if (request != revision || edited || !selected || !avatar || Selected(builder) != selected ||
                    VRCSdkControlPanel.window == null || VRCSdkControlPanel.window.rootVisualElement.Q<ThumbnailBlock>() != block ||
                    builder.BuildState == SdkBuildState.Building || builder.UploadState == SdkUploadState.Uploading ||
                    path != AvatarThumbnail.FullPath(avatar.thumbnail) ||
                    key != path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks + "|" + BlueprintId(selected)) return;
                block.OnNewThumbnailSelected.Invoke(block, path);
                offeredImage = block.Thumbnail.CurrentImageTexture;
            }
            catch (Exception)
            {
                // An unavailable comparison must not queue a duplicate or block an otherwise valid avatar upload.
                Debug.LogWarning("My Avatar could not check the saved thumbnail against VRChat. The SDK thumbnail was left unchanged. To replace it, use Select New Thumbnail in the SDK.");
            }
            finally { block.OnNewThumbnailSelected -= changed; }
        }

        internal static async Task<bool> AlreadyUploaded(string id, string path, CancellationToken token)
        {
            string remote = await RemoteHash(id, token);
            return remote != null && remote == VrcThumbnailHash.Compute(path);
        }

        // MD5 of the image VRChat holds as the avatar's thumbnail, the way its upload compares files; null without one.
        private static async Task<string> RemoteHash(string id, CancellationToken token)
        {
            var remote = await VRCApi.GetAvatar(id, forceRefresh: true, cancellationToken: token);
            string fileId = VRC.Core.ApiFile.ParseFileIdFromFileAPIUrl(remote.ImageUrl);
            string md5 = null;
            if (!string.IsNullOrEmpty(fileId))
            {
                var file = await VRCApi.Get<VRCFile>("file/" + fileId, forceRefresh: true, cancellationToken: token);
                // The SDK's own test (VRCApi.UploadFile): the latest version's file has the same MD5 and isn't waiting.
                if (file.HasExistingOrPendingVersion() && !file.IsLatestVersionWaiting()) md5 = file.Versions[file.GetLatestVersion()]?.File?.MD5;
            }
            if (string.IsNullOrEmpty(md5)) md5 = null;
            remoteHashes[id] = (md5, DateTime.UtcNow);
            return md5;
        }

        /// <summary>
        /// The thumbnail the SDK would upload with the next Build &amp; Publish when VRChat is known (from a recent check)
        /// to hold that exact image already; null otherwise. Main thread, no network.
        /// </summary>
        internal static string PendingDuplicate(IVRCSdkAvatarBuilderApi builder, string id)
        {
            string pending = Pending(builder);
            if (string.IsNullOrEmpty(pending) || string.IsNullOrEmpty(id) || !System.IO.File.Exists(pending)) return null;
            if (!remoteHashes.TryGetValue(id, out var remote) || remote.md5 == null || DateTime.UtcNow - remote.checkedUtc > RemoteFresh) return null;
            return remote.md5 == VrcThumbnailHash.Compute(pending) ? pending : null;
        }

        // At most once a minute per avatar.
        private static async void RefreshRemote(string id)
        {
            var now = DateTime.UtcNow;
            if (remoteHashes.TryGetValue(id, out var known) && now - known.checkedUtc < TimeSpan.FromMinutes(1)) return;
            if (remoteRequested.TryGetValue(id, out var asked) && now - asked < TimeSpan.FromMinutes(1)) return;
            remoteRequested[id] = now;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await RemoteHash(id, timeout.Token);
            }
            catch (Exception)
            {
                // Checked again at the next build.
            }
        }

        private static string Pending(IVRCSdkAvatarBuilderApi builder) =>
            builder is VRCSdkControlPanelAvatarBuilder && PendingField != null ? PendingField.GetValue(builder) as string : null;

        /// <summary>Drops the thumbnail waiting in the SDK builder, as its Discard Changes does, keeping the other edits.</summary>
        internal static bool Withdraw(IVRCSdkAvatarBuilderApi builder)
        {
            if (!(builder is VRCSdkControlPanelAvatarBuilder) || PendingField == null) return false;
            PendingField.SetValue(builder, null);
            offeredImage = null;
            try
            {
                const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(VRCSdkControlPanelAvatarBuilder);
                var dirty = type.GetMethod("CheckDirty", Instance)?.Invoke(builder, null);
                if (dirty is bool value) type.GetProperty("IsContentInfoDirty", Instance)?.SetValue(builder, value);
            }
            catch (Exception)
            {
                // Only the Save Changes button's state: it is updated at the next edit.
            }
            return true;
        }

        private static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
