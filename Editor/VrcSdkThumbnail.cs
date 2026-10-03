using UnityEditor;
using System;
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
    [InitializeOnLoad]
    internal static class VrcSdkThumbnail
    {
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
                if (builder.SelectedAvatar != pendingSelection) builder.SelectAvatar(pendingSelection);
                pendingSelection = null;
            }
            var selected = builder.SelectedAvatar;
            if (selected != lastSelected) { lastSelected = selected; offered = null; offeredImage = null; selectedAt = EditorApplication.timeSinceStartup; revision++; }
            if (!selected) return;
            if (builder.BuildState == SdkBuildState.Building || builder.UploadState == SdkUploadState.Uploading) return;
            var avatar = selected.GetComponent<MyAvatar>();
            if (!avatar) avatar = selected.GetComponentInChildren<MyAvatar>(true);
            string path = avatar ? AvatarThumbnail.FullPath(avatar.thumbnail) : null;
            // A new capture overwrites the same file, so the write time is part of what was offered.
            string key = path == null ? null : path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks;
            if (key == null || key == offered) return;
            var block = VRCSdkControlPanel.window.rootVisualElement.Q<ThumbnailBlock>();
            // Wait until the builder finished loading this avatar's data, which resets its pending thumbnail. Loading may
            // only start a few frames after the switch, so the selection must also have settled.
            if (EditorApplication.timeSinceStartup - selectedAt < .75 || block?.Thumbnail == null || block.Thumbnail.Loading || block.OnNewThumbnailSelected == null) return;
            offered = key;
            Offer(builder, selected, avatar, block, path, key, ++revision);
        }

        private static async void Offer(IVRCSdkAvatarBuilderApi builder, GameObject selected, MyAvatar avatar,
            ThumbnailBlock block, string path, string key, int request)
        {
            // Never replace a thumbnail already chosen in the SDK, including while the network check is pending.
            if (block.Thumbnail.CurrentImageTexture != null && block.Thumbnail.CurrentImageTexture != offeredImage) return;
            bool edited = false;
            EventHandler<string> changed = (_, __) => edited = true;
            block.OnNewThumbnailSelected += changed;
            try
            {
                string id = selected.GetComponent<VRC.Core.PipelineManager>()?.blueprintId;
                if (!string.IsNullOrEmpty(id))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (await AlreadyUploaded(id, path, timeout.Token)) return;
                }
                if (request != revision || edited || !selected || !avatar || builder.SelectedAvatar != selected ||
                    VRCSdkControlPanel.window == null || VRCSdkControlPanel.window.rootVisualElement.Q<ThumbnailBlock>() != block ||
                    builder.BuildState == SdkBuildState.Building || builder.UploadState == SdkUploadState.Uploading ||
                    path != AvatarThumbnail.FullPath(avatar.thumbnail) ||
                    key != path + "|" + System.IO.File.GetLastWriteTimeUtc(path).Ticks) return;
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
            var remote = await VRCApi.GetAvatar(id, forceRefresh: true, cancellationToken: token);
            string fileId = VRC.Core.ApiFile.ParseFileIdFromFileAPIUrl(remote.ImageUrl);
            if (string.IsNullOrEmpty(fileId)) return false;
            var file = await VRCApi.Get<VRCFile>("file/" + fileId, forceRefresh: true, cancellationToken: token);
            var latest = file.Versions != null && file.Versions.Count > 0 ? file.Versions[file.Versions.Count - 1] : null;
            return latest != null && !latest.Deleted && latest.File?.Status == "complete" &&
                latest.File.MD5 == VrcThumbnailHash.Compute(path);
        }
    }
}
