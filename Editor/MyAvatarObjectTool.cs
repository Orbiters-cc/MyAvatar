using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// My Avatar in other views of the avatar's objects (Mixer's Hierarchy): a drop on the avatar root goes to its drop
    /// field, and an accessory it added whose fit is in question offers ReFit. Both are done by the avatar's own
    /// Inspector, so they show and behave exactly as there.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class MyAvatarObjectTool : IAvatarObjectTool
    {
        // What waits for the avatar's Inspector to show (by the avatar's instance id).
        private static readonly Dictionary<int, Action<MyAvatarEditor>> Waiting = new Dictionary<int, Action<MyAvatarEditor>>();

        static MyAvatarObjectTool()
        {
            AvatarObjectTools.Register(new MyAvatarObjectTool());
            AccessoryService.Changed += _ => AvatarObjectTools.NotifyChanged();
        }

        private static MyAvatar AvatarOn(GameObject root) => root ? root.GetComponent<MyAvatar>() : null;

        public bool TakesDrops(GameObject avatarRoot)
        {
            var avatar = AvatarOn(avatarRoot);
            return avatar && !EditorUtility.IsPersistent(avatar) && !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        public bool Busy(GameObject avatarRoot)
        {
            var avatar = AvatarOn(avatarRoot);
            return avatar && AccessoryService.Busy(avatar);
        }

        public void Drop(GameObject avatarRoot, string[] paths)
        {
            var avatar = AvatarOn(avatarRoot);
            if (!avatar || paths == null || paths.Length == 0) return;
            Ask(avatar, editor => editor.DropFromElsewhere(paths));
        }

        public bool AsksForRefit(GameObject item)
        {
            var attachment = item ? item.GetComponent<OrbitersAttachment>() : null;
            if (!attachment) return false;
            var avatar = item.GetComponentInParent<MyAvatar>(true);
            return avatar && avatar.accessoryNotes != null && avatar.accessoryNotes.Any(n => n.accessory == attachment && AccessoryFit.IsQuestion(n));
        }

        public void Refit(GameObject item)
        {
            var attachment = item ? item.GetComponent<OrbitersAttachment>() : null;
            var avatar = item ? item.GetComponentInParent<MyAvatar>(true) : null;
            if (!attachment || !avatar) return;
            Ask(avatar, editor => editor.Accessories?.RefitFromElsewhere(attachment));
        }

        // The avatar's Inspector does the work: the one showing it now; else the selection goes to the avatar (as a click
        // on it would) and the Inspector that shows it does; else (every Inspector locked elsewhere) one of its own.
        private static void Ask(MyAvatar avatar, Action<MyAvatarEditor> work)
        {
            var live = LiveEditor(avatar);
            if (live != null) { work(live); return; }
            int id = avatar.GetInstanceID();
            Waiting[id] = work;
            Selection.activeGameObject = avatar.gameObject;
            double deadline = EditorApplication.timeSinceStartup + 1.5;
            EditorApplication.CallbackFunction check = null;
            check = () =>
            {
                if (!Waiting.ContainsKey(id)) { EditorApplication.update -= check; return; }
                if (EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= check;
                if (avatar) EditorUtility.OpenPropertyEditor(avatar);
                else Waiting.Remove(id);
            };
            EditorApplication.update += check;
        }

        /// <summary>Called by a My Avatar Inspector once it shows its avatar: runs what waited for it.</summary>
        internal static void RunWaiting(MyAvatarEditor editor)
        {
            if (editor == null || !editor.Live) return;
            int id = editor.Avatar.GetInstanceID();
            if (!Waiting.TryGetValue(id, out var work)) return;
            Waiting.Remove(id);
            work(editor);
        }

        private static MyAvatarEditor LiveEditor(MyAvatar avatar) =>
            Resources.FindObjectsOfTypeAll<MyAvatarEditor>().FirstOrDefault(e => e != null && e.Avatar == avatar && e.Live);
    }
}
