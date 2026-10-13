using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.MyAvatar.Editor.Locomotion
{
    /// <summary>What an avatar has of GoGoLoco: its prefab, or a setup made another way.</summary>
    internal sealed class GoGoLocoReport
    {
        /// <summary>The GoGoLoco prefab on the avatar (added here or from GoGoLoco's own window), if any.</summary>
        public GameObject Prefab;
        /// <summary>The version of GoGoLoco that prefab is (All, Beyond...), null when it is not one of the installed package's.</summary>
        public GoGoLocoVariant Variant;
        /// <summary>GoGoLoco set up another way (what it is), or null.</summary>
        public string Existing;
        public GameObject ExistingObject;

        public bool SetUp => Prefab != null || Existing != null;
    }

    /// <summary>
    /// GoGoLoco, Franada's locomotion system, in one click: when it is missing (with VRCFury when the project has neither
    /// VRCFury nor Modular Avatar), what would be downloaded is shown first and comes through VPM once accepted
    /// (<see cref="Downloads"/>), then its prefab goes on the avatar, as GoGoLoco's own Tools &gt; GoGoLoco &gt; Add Prefabs
    /// window does. Its files stay as they are.
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

        /// <summary>The downloads GoGoLoco needs, and the avatars waiting for them.</summary>
        internal static readonly PackageSetup Downloads = new PackageSetup("GoGoLoco", "GoGoLoco", "VRCFury or GoGoLoco",
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
            if (report.Prefab != null) { report.Variant = VariantOf(report.Prefab); return report; }
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

        /// <summary>The packages to download before GoGoLoco can go on an avatar (package ids), empty when none.</summary>
        internal static List<string> Missing()
        {
            var missing = new List<string>();
            if (!VrcFury.Installed && !ModularAvatarInstalled) missing.Add(VrcFuryId);
            if (!Installed) missing.Add(PackageId);
            return missing;
        }

        /// <summary>
        /// Adds GoGoLoco to <paramref name="root"/> once nothing is missing (<see cref="Missing"/>; the section downloads it
        /// first). Returns a note for the user, or null.
        /// </summary>
        internal static string Start(MyAvatar avatar, Transform root)
        {
            if (!Fits(root)) return "GoGoLoco goes on a humanoid avatar with an avatar descriptor.";
            if (Inspect(root).SetUp) return "GoGoLoco is already on this avatar.";
            var missing = Missing();
            if (missing.Count > 0) return "Download " + string.Join(" and ", missing) + " first.";
            Downloads.Forget(avatar);
            return Apply(root);
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

        // ---- Versions ----------------------------------------------------------------------------------------

        /// <summary>
        /// The versions of the installed GoGoLoco for VRCFury, or for Modular Avatar: All first, then Beyond, then any other a
        /// newer GoGoLoco brings, read from its prefabs.
        /// </summary>
        internal static List<GoGoLocoVariant> Variants(bool modularAvatar)
        {
            string folder = Prefabs + (modularAvatar ? "Modular Avatar" : "VRCFury");
            if (!AssetDatabase.IsValidFolder(folder)) return new List<GoGoLocoVariant>();
            return AssetDatabase.FindAssets("t:Prefab", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetDirectoryName(p).Replace('\\', '/') == folder)
                .Select(p => GoGoLocoVariant.Read(p)).Where(v => v != null)
                .OrderBy(v => v.Name == "All" ? 0 : v.Name == "Beyond" ? 1 : 2).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The version <paramref name="prefab"/> on an avatar is, or null.</summary>
        internal static GoGoLocoVariant VariantOf(GameObject prefab)
        {
            string path = prefab != null ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefab) : null;
            return string.IsNullOrEmpty(path) ? null : GoGoLocoVariant.Read(path);
        }

        /// <summary>
        /// Puts <paramref name="target"/> in place of the GoGoLoco prefab <paramref name="current"/> (one Undo step): where it
        /// was, with its name if you renamed it, its position and whether it is active; the old one goes with everything it
        /// carried. The menu's saved values are VRChat's, by parameter name: both versions share theirs. False with a reason
        /// in <paramref name="note"/>; true with a note when changes made to the old prefab were not carried over.
        /// </summary>
        internal static bool Switch(GameObject current, GoGoLocoVariant target, out string note)
        {
            note = null;
            if (current == null || target == null) { note = "GoGoLoco is no longer on this avatar."; return false; }
            if (!Removable(current)) { note = "GoGoLoco is part of this avatar's own prefab: change its version in that prefab."; return false; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(target.Path);
            if (prefab == null) { note = "The installed GoGoLoco has no “" + Path.GetFileNameWithoutExtension(target.Path) + "”."; return false; }
            var old = VariantOf(current);
            if (old != null && old.Path == target.Path) return true;
            string oldPrefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(current);
            bool renamed = oldPrefab != null && current.name != Path.GetFileNameWithoutExtension(oldPrefab);
            bool changed = PrefabUtility.HasPrefabInstanceAnyOverrides(current, false);
            var place = current.transform;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            // Set up before it is recorded, so Redo brings it back exactly so.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, place.parent);
            instance.transform.SetSiblingIndex(place.GetSiblingIndex());
            instance.transform.localPosition = place.localPosition; instance.transform.localRotation = place.localRotation; instance.transform.localScale = place.localScale;
            if (renamed) instance.name = current.name;
            instance.SetActive(current.activeSelf);
            Undo.RegisterCreatedObjectUndo(instance, "Switch GoGoLoco");
            Undo.DestroyObjectImmediate(current);
            Undo.SetCurrentGroupName("Switch GoGoLoco to " + target.Name);
            Undo.CollapseUndoOperations(group);
            Debug.Log("[My Avatar] GoGoLoco " + (old != null ? old.Name + " " : "") + "switched to " + target.Name + " (" + target.Path + ").");
            if (changed) note = "Changes made to GoGoLoco " + (old != null ? old.Name : "") + "'s own components were not carried over; Undo brings them back.";
            return true;
        }

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

namespace Orbiters.MyAvatar.Editor.Locomotion
{
    /// <summary>
    /// One version of GoGoLoco, as its prefab sets it up: the playable layers and synced parameters it adds, with what it
    /// does for the versions known (All, Beyond) and what can be read for one a newer GoGoLoco brings.
    /// </summary>
    internal sealed class GoGoLocoVariant
    {
        private const string Icons = GoGoLoco.PackageFolder + "/Runtime/GoGo/GoLoco/Icons/";
        public string Name, Path, Tagline, Description;
        public bool ModularAvatar;
        public VRCExpressionParameters Parameters;
        /// <summary>The playable layers it adds (Base, Action, FX, Sitting...), when its prefab says so (VRCFury's do).</summary>
        public readonly List<string> Layers = new List<string>();

        /// <summary>Synced parameter bits it adds on an avatar without any of them.</summary>
        public int Bits => Parameters != null ? Parameters.CalcTotalCost() : 0;

        public string IconPath => Icons + (Name == "Beyond" ? "icon_fly.png" : Name == "All" ? "icon_person_pose.png" : "icon_Go_Loco.png");

        /// <summary>The synced bits it adds on the avatar of <paramref name="root"/>: parameters the avatar already syncs (VRCEmote) are shared.</summary>
        public int BitsOn(Transform root)
        {
            if (Parameters == null || Parameters.parameters == null) return 0;
            var descriptor = root != null ? root.GetComponent<VRCAvatarDescriptor>() : null;
            var own = new HashSet<string>((descriptor != null && descriptor.expressionParameters != null && descriptor.expressionParameters.parameters != null
                ? descriptor.expressionParameters.parameters : Array.Empty<VRCExpressionParameters.Parameter>()).Where(p => p != null && p.networkSynced).Select(p => p.name));
            return Parameters.parameters.Where(p => p != null && p.networkSynced && !string.IsNullOrEmpty(p.name) && !own.Contains(p.name))
                .GroupBy(p => p.name).Sum(g => VRCExpressionParameters.TypeCost(g.First().valueType));
        }

        // GoGoLoco's prefabs name their version and how they go on: "GogoLoco Beyond (VRCFury).prefab".
        private static readonly Regex PrefabName = new Regex(@"^GogoLoco (?<name>.+) \((?<with>VRCFury|Modular Avatar)\)$", RegexOptions.CultureInvariant);

        /// <summary>The version the GoGoLoco prefab at <paramref name="path"/> is, or null for another file.</summary>
        internal static GoGoLocoVariant Read(string path)
        {
            if (!GoGoLoco.IsGoGoLocoAsset(path)) return null;
            var match = PrefabName.Match(System.IO.Path.GetFileNameWithoutExtension(path) ?? "");
            if (!match.Success) return null;
            var variant = new GoGoLocoVariant { Name = match.Groups["name"].Value, Path = path, ModularAvatar = match.Groups["with"].Value == "Modular Avatar" };
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            foreach (var (_, feature, kind) in prefab != null ? VrcFury.Features(prefab) : Enumerable.Empty<(Component, object, string)>())
            {
                if (kind != "FullController") continue;
                if (VrcFury.Field(feature, "controllers") is IEnumerable controllers)
                    foreach (var entry in controllers)
                    {
                        string layer = entry?.GetType().GetField("type")?.GetValue(entry)?.ToString();
                        if (!string.IsNullOrEmpty(layer) && !variant.Layers.Contains(layer)) variant.Layers.Add(layer);
                    }
                if (variant.Parameters == null && VrcFury.Field(feature, "prms") is IEnumerable parameters)
                    foreach (var entry in parameters)
                        if (VrcFury.ObjectReference(entry?.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters asset) { variant.Parameters = asset; break; }
            }
            // Modular Avatar's prefabs list their parameters on their own components: both kinds share GoGoLoco's menu parameters.
            if (variant.Parameters == null)
                variant.Parameters = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(GoGoLoco.PackageFolder + "/Runtime/GoGo/GoLoco/MainMenu/Go" + variant.Name + "Parameters.asset");
            Describe(variant);
            return variant;
        }

        private static void Describe(GoGoLocoVariant v)
        {
            string layers = v.Layers.Count == 0 ? "" : " Its layers: " + (v.Layers.Count == 1 ? v.Layers[0]
                : string.Join(", ", v.Layers.Take(v.Layers.Count - 1)) + " and " + v.Layers[v.Layers.Count - 1]) + ".";
            switch (v.Name)
            {
                case "All":
                    v.Tagline = "Locomotion, poses, emotes";
                    v.Description = "Sit, lie down and pose anywhere, stay in place, swap idles, hover, change height and emotes, from the Action Menu." + layers;
                    break;
                case "Beyond":
                    v.Tagline = "All, plus flying";
                    v.Description = "Everything in All, plus flying in any world from seats it carries on the avatar (linked to the head and hips), " +
                                    "its angle and a dash." + layers;
                    break;
                default:
                    v.Tagline = v.Layers.Count > 0 ? string.Join(", ", v.Layers) : "Another version";
                    v.Description = "GoGoLoco " + v.Name + ", as this version of GoGoLoco ships it." + layers;
                    break;
            }
        }
    }
}
