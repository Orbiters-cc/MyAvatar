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
        // Seconds after which the avatar opens in an Inspector of its own (the selection showed it in none), and after which
        // what waits for an Inspector is given up: a drop never runs long after it was made.
        private const double OwnInspectorAfter = 1.5, GiveUpAfter = 10;

        // A drop or a ReFit waiting for the avatar's Inspector to show: each one is kept, in the order asked.
        private sealed class Request
        {
            public MyAvatar Avatar;
            public Action<MyAvatarEditor> Work;
            // For the warning when it is given up: "A drop", and what to do then.
            public string What, Again;
            public double Asked;
            public bool OwnInspector;
        }

        private static readonly List<Request> Waiting = new List<Request>();
        private static double nextLook;

        static MyAvatarObjectTool()
        {
            AvatarObjectTools.Register(new MyAvatarObjectTool());
            AccessoryService.Changed += _ => AvatarObjectTools.NotifyChanged();
            AssemblyReloadEvents.beforeAssemblyReload += () => GiveUp(Waiting.ToList(), "scripts reloaded before an Inspector showed the avatar");
        }

        private static MyAvatar AvatarOn(GameObject root) => root ? root.GetComponent<MyAvatar>() : null;

        public bool TakesDrops(GameObject avatarRoot)
        {
            var avatar = AvatarOn(avatarRoot);
            return avatar && !EditorUtility.IsPersistent(avatar) && !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        // Busy from the drop on, also while it waits for the Inspector: the view keeps saying so.
        public bool Busy(GameObject avatarRoot)
        {
            var avatar = AvatarOn(avatarRoot);
            return avatar && (AccessoryService.Busy(avatar) || Waiting.Any(r => r.Avatar == avatar));
        }

        public void Drop(GameObject avatarRoot, string[] paths)
        {
            var avatar = AvatarOn(avatarRoot);
            if (!avatar || paths == null || paths.Length == 0) return;
            Ask(avatar, "A drop", "drop it again", editor => editor.DropFromElsewhere(paths));
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
            Ask(avatar, "A ReFit", "click ReFit again", editor => editor.Accessories?.RefitFromElsewhere(attachment));
        }

        // The avatar's Inspector does the work: the one showing it now; else the selection goes to the avatar (as a click
        // on it would) and the Inspector that shows it does; else (every Inspector locked elsewhere) one of its own.
        private static void Ask(MyAvatar avatar, string what, string again, Action<MyAvatarEditor> work)
        {
            var live = LiveEditor(avatar);
            if (live != null) { work(live); return; }
            Waiting.Add(new Request { Avatar = avatar, Work = work, What = what, Again = again, Asked = EditorApplication.timeSinceStartup });
            Selection.activeGameObject = avatar.gameObject;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
            AvatarObjectTools.NotifyChanged();
        }

        // Hands what waits to the avatar's Inspector once one shows it (a selection takes a few frames, and an Inspector
        // being built or rebuilt isn't live yet), opens the avatar in an Inspector of its own when none did, and gives up on
        // what waited too long.
        private static void Pump()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextLook) return;
            nextLook = now + .1;
            // Avatars deleted meanwhile: nothing to do it on.
            bool changed = Waiting.RemoveAll(r => !r.Avatar) > 0;
            foreach (var avatar in Waiting.Select(r => r.Avatar).Distinct().ToList())
            {
                var live = LiveEditor(avatar);
                if (live != null) { RunWaiting(live); continue; }
                var mine = Waiting.Where(r => r.Avatar == avatar).ToList();
                if (mine.Any(r => r.OwnInspector) || now - mine[0].Asked < OwnInspectorAfter) continue;
                mine.ForEach(r => r.OwnInspector = true);
                EditorUtility.OpenPropertyEditor(avatar);
            }
            GiveUp(Waiting.Where(r => now - r.Asked >= GiveUpAfter).ToList(), "no Inspector showed the avatar within " + GiveUpAfter + " seconds");
            if (Waiting.Count == 0) EditorApplication.update -= Pump;
            if (changed) AvatarObjectTools.NotifyChanged();
        }

        private static void GiveUp(List<Request> requests, string why)
        {
            if (requests.Count == 0) return;
            foreach (var request in requests)
            {
                Waiting.Remove(request);
                Debug.LogWarning("[My Avatar] " + request.What + " from another view on " + (request.Avatar ? request.Avatar.name : "an avatar") +
                                 " was not done: " + why + ". Select the avatar and " + request.Again + ".");
            }
            if (Waiting.Count == 0) EditorApplication.update -= Pump;
            AvatarObjectTools.NotifyChanged();
        }

        /// <summary>Called by a My Avatar Inspector once it shows its avatar: runs what waited for it, in order.</summary>
        internal static void RunWaiting(MyAvatarEditor editor)
        {
            if (editor == null || !editor.Live) return;
            var mine = Waiting.Where(r => r.Avatar == editor.Avatar).ToList();
            if (mine.Count == 0) return;
            Waiting.RemoveAll(mine.Contains);
            if (Waiting.Count == 0) EditorApplication.update -= Pump;
            foreach (var request in mine)
            {
                try { request.Work(editor); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
            AvatarObjectTools.NotifyChanged();
        }

        private static MyAvatarEditor LiveEditor(MyAvatar avatar) =>
            Resources.FindObjectsOfTypeAll<MyAvatarEditor>().FirstOrDefault(e => e != null && e.Avatar == avatar && e.Live);
    }
}
