using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor.Elements;

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

        static VrcSdkThumbnail() => EditorApplication.update += Watch;

        // Shows the SDK panel on this avatar; the thumbnail follows as soon as the builder has loaded it.
        internal static void Open(MyAvatar avatar)
        {
            pendingSelection = avatar.gameObject;
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
        }

        private static void Watch()
        {
            if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder)) { lastSelected = null; return; }
            if (pendingSelection)
            {
                if (builder.SelectedAvatar != pendingSelection) builder.SelectAvatar(pendingSelection);
                pendingSelection = null;
            }
            var selected = builder.SelectedAvatar;
            if (selected != lastSelected) { lastSelected = selected; offered = null; selectedAt = EditorApplication.timeSinceStartup; }
            if (!selected) return;
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
            block.OnNewThumbnailSelected.Invoke(block, path);
            offered = key;
        }
    }
}
