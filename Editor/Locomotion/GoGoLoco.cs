using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.MyAvatar.Editor.Locomotion
{
    /// <summary>What an avatar has of GoGoLoco: its prefab, or a setup made another way.</summary>
    internal sealed class GoGoLocoReport
    {
        /// <summary>The GoGoLoco prefab on the avatar (added here or from GoGoLoco's own window), if any.</summary>
        public GameObject Prefab;
        /// <summary>GoGoLoco set up another way (what it is), or null.</summary>
        public string Existing;
        public GameObject ExistingObject;

        public bool SetUp => Prefab != null || Existing != null;
    }

    /// <summary>
    /// GoGoLoco, Franada's locomotion system, in one click: it is downloaded through VPM when missing (with VRCFury when the
    /// project has neither VRCFury nor Modular Avatar), then its prefab goes on the avatar, as GoGoLoco's own Tools &gt;
    /// GoGoLoco &gt; Add Prefabs window does. Its files stay as they are.
    /// </summary>
    internal static class GoGoLoco
    {
        internal const string PackageId = "gogoloco";
        internal const string PackageFolder = "Packages/" + PackageId;
        /// <summary>Where its author asks to be found (GoGoLoco's readme), and its VPM listing.</summary>
        internal const string Author = "https://linktr.ee/franada", Listing = "https://spokeek.github.io/goloco/";
        private const string Prefabs = PackageFolder + "/Runtime/GoGo/GoLoco/Prefabs/";
        // The standard GoGoLoco, for VRCFury or Modular Avatar ("Beyond" adds its own FX layer and more parameters).
        internal const string VrcFuryPrefab = Prefabs + "VRCFury/GogoLoco All (VRCFury).prefab";
        internal const string ModularAvatarPrefab = Prefabs + "Modular Avatar/GogoLoco All (Modular Avatar).prefab";
        private const string VrcFuryId = "com.vrcfury.vrcfury", ModularAvatarFolder = "Packages/nadena.dev.modular-avatar";

        private static readonly PackageSetup Downloads = new PackageSetup("GoGoLoco", "GoGoLoco", "VRCFury or GoGoLoco",
            () => Installed && (VrcFury.Installed || ModularAvatarInstalled), Finish);

        internal static bool Installed => AssetDatabase.IsValidFolder(PackageFolder);
        /// <summary>The installed GoGoLoco's version, or null.</summary>
        internal static string Version => UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageFolder)?.version;
        private static bool ModularAvatarInstalled => AssetDatabase.IsValidFolder(ModularAvatarFolder);
        /// <summary>Modular Avatar's prefab in a project that has it and not VRCFury; VRCFury's otherwise.</summary>
        internal static bool UsesModularAvatar => !VrcFury.Installed && ModularAvatarInstalled;

        /// <summary>Whether GoGoLoco fits <paramref name="root"/>: a humanoid avatar, whose locomotion it replaces.</summary>
        internal static bool Fits(Transform root)
        {
            if (root == null || root.GetComponent<VRCAvatarDescriptor>() == null) return false;
            var animator = root.GetComponent<Animator>();
            return animator != null && animator.isHuman;
        }

        public static GoGoLocoReport Inspect(Transform root)
        {
            var report = new GoGoLocoReport();
            if (root == null) return report;
            report.Prefab = PackageUse.Prefab(root, IsGoGoLocoAsset);
            if (report.Prefab != null) return report;
            var component = PackageUse.Component(root, IsGoGoLocoAsset);
            if (component != null) { report.ExistingObject = component.gameObject; report.Existing = "GoGoLoco on “" + component.name + "”"; return report; }
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor != null && (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                    .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                    .Any(layer => !layer.isDefault && layer.animatorController != null && IsGoGoLocoAsset(AssetDatabase.GetAssetPath(layer.animatorController))))
            { report.ExistingObject = root.gameObject; report.Existing = "GoGoLoco's controllers in the avatar descriptor's playable layers"; }
            return report;
        }

        /// <summary>One of GoGoLoco's files: in its package, or from its older unitypackage (Assets/GoGo).</summary>
        internal static bool IsGoGoLocoAsset(string path) => !string.IsNullOrEmpty(path) &&
            (path.StartsWith(PackageFolder + "/", StringComparison.Ordinal) || path.StartsWith("Assets/GoGo/", StringComparison.Ordinal));

        /// <summary>Adds GoGoLoco to <paramref name="root"/>, downloading what is missing first. Returns a note for the user, or null.</summary>
        internal static string Start(MyAvatar avatar, Transform root)
        {
            if (!Fits(root)) return "GoGoLoco goes on a humanoid avatar with an avatar descriptor.";
            if (Inspect(root).SetUp) return "GoGoLoco is already on this avatar.";
            var missing = new List<string>();
            if (!VrcFury.Installed && !ModularAvatarInstalled) missing.Add(VrcFuryId);
            if (!Installed) missing.Add(PackageId);
            if (missing.Count == 0) { Downloads.StopWaiting(); return Apply(root); }
            return Downloads.Download(avatar, missing);
        }

        /// <summary>Puts GoGoLoco's prefab on the avatar (one Undo step). Null when done, else why not.</summary>
        internal static string Apply(Transform root)
        {
            string path = UsesModularAvatar ? ModularAvatarPrefab : VrcFuryPrefab;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return "The installed GoGoLoco has no “" + Path.GetFileNameWithoutExtension(path) + "”: update GoGoLoco in the Creator Companion.";
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
            Undo.RegisterCreatedObjectUndo(instance, "Add GoGoLoco");
            return null;
        }

        /// <summary>Whether <paramref name="prefab"/> can be taken off: not part of the avatar's own prefab.</summary>
        internal static bool Removable(GameObject prefab) => prefab != null &&
            (PrefabUtility.GetOutermostPrefabInstanceRoot(prefab) == prefab || PrefabUtility.IsAddedGameObjectOverride(prefab));

        /// <summary>Takes GoGoLoco's prefab off the avatar (one Undo step). The package stays installed.</summary>
        internal static void Remove(GameObject prefab)
        {
            if (Removable(prefab)) Undo.DestroyObjectImmediate(prefab);
        }

        // After the download reloaded Unity's scripts: finish what was asked for.
        [InitializeOnLoadMethod]
        private static void Resume() => Downloads.Resume();

        private static string Finish(GameObject root)
        {
            string problem = !Fits(root.transform) ? "it is no longer a humanoid avatar" : Inspect(root.transform).SetUp ? null : Apply(root.transform);
            if (problem == null) Debug.Log("[My Avatar] GoGoLoco added to " + root.name + ".");
            return problem;
        }
    }
}
