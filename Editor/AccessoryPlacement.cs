using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// How an item goes onto an avatar, the same for a drop (<see cref="AccessoryService"/>) and the asset gallery
    /// (<see cref="Gallery.GalleryInstaller"/>): a copy under the avatar, its hooks prepared, how it attaches worked out,
    /// then attached. What each adds around it (notes, bones, receipts) stays with it.
    /// </summary>
    internal static class AccessoryPlacement
    {
        /// <summary>A copy of the prefab under the avatar, with Undo.</summary>
        internal static GameObject Instantiate(GameObject prefab, Transform avatar, string name)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar);
            Undo.RegisterCreatedObjectUndo(instance, "Add " + name);
            return instance;
        }

        /// <summary>
        /// Attaches an object under the avatar as placed by My Avatar, or returns null when it has nothing to show (the
        /// object is left as it is: the caller removes a copy it made). <paramref name="plan"/> says how it attaches.
        /// </summary>
        internal static OrbitersAttachment Install(GameObject instance, Transform avatar, string source, string variant, out AttachmentPlan plan)
        {
            AttachmentHooks.Prepare(instance, avatar);
            plan = AttachmentPlanner.Analyze(instance, avatar);
            if (plan.Kind == AttachmentKind.Empty) return null;
            return AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, Source = source, Variant = variant });
        }

        /// <summary>Clothing (or a configured outfit) is fitted to the avatar's armature once attached.</summary>
        internal static bool Fitted(AttachmentPlan plan) => plan.Kind == AttachmentKind.Clothing || plan.Kind == AttachmentKind.Configured;
    }
}
