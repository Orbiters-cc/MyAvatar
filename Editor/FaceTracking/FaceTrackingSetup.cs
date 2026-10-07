using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;
#if MYAVATAR_VPM
using Orbiters.Toolkit.Editor.Vpm;
#endif

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// Sets up Adjerry91's face tracking template in one click: installs it (and VRCFury) through VPM when missing, puts the
    /// template for the avatar's blendshape standard on the avatar and points it at the face mesh. The template's files
    /// stay as they are, as its author asks; the build animates the face's own blendshape names and keeps hand gestures
    /// off the face while tracking (<see cref="FaceTrackingBuild"/>).
    /// </summary>
    internal static class FaceTrackingSetup
    {
        private const string VrcFuryId = "com.vrcfury.vrcfury";
        private const string PendingKey = "Orbiters.MyAvatar.FaceTracking.PendingAvatar";

        /// <summary>Sets up face tracking on <paramref name="root"/>, installing what is missing first. Returns a note for the user.</summary>
        internal static string Start(MyAvatar avatar, Transform root)
        {
            var report = FaceTrackingDetection.Inspect(root);
            if (report.SetUp) return "Face tracking is already set up on this avatar.";
            if (!report.Ready) return "This avatar's face does not have enough face tracking blendshapes.";
            var missing = new List<string>();
            if (!VrcFury.Installed) missing.Add(VrcFuryId);
            if (!FaceTrackingDetection.TemplatesInstalled) missing.Add(FaceTrackingDetection.PackageId);
            if (missing.Count == 0) { SessionState.EraseInt(PendingKey); return Apply(root, report); }
#if MYAVATAR_VPM
            var dependencies = VpmDependencies.For("orbiters.myavatar");
            var statuses = missing.Select(id => dependencies.OptionalStatus(id, true)).ToList();
            if (statuses.Any(s => s == null)) return "My Avatar does not know where to download " + string.Join(" and ", missing) + ".";
            SessionState.SetInt(PendingKey, avatar != null ? avatar.GetInstanceID() : 0);
            VpmDependencyInstallResult result;
            try { result = VpmDependencies.Install(statuses); }
            catch (Exception ex) { result = new VpmDependencyInstallResult { Errors = { ex.Message } }; }
            if (result.Success)
            {
                // The templates are assets only: Unity may import them without reloading scripts. Finish as soon as both are there.
                EditorApplication.update -= Poll;
                EditorApplication.update += Poll;
                return "Downloaded " + string.Join(" and ", statuses.Select(s => s.DisplayName)) + ". Face tracking is set up as soon as Unity has imported them.";
            }
            SessionState.EraseInt(PendingKey);
            return "Could not download " + string.Join(" and ", statuses.Select(s => s.DisplayName)) + ": " + result.ErrorMessage;
#else
            return "Add " + string.Join(" and ", missing) + " to this project in the VRChat Creator Companion, then set up face tracking again.";
#endif
        }

        /// <summary>Puts the template on the avatar (one Undo step). Null when done, else why not.</summary>
        internal static string Apply(Transform root, FaceTrackingReport report)
        {
            string guid = AssetDatabase.FindAssets(report.Standard.Prefab + " t:Prefab", new[] { FaceTrackingDetection.PackageFolder })
                .FirstOrDefault(g => System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == report.Standard.Prefab);
            var prefab = guid != null ? AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)) : null;
            if (prefab == null) return "The installed templates have no “" + report.Standard.Prefab + "”: update Adjerry91’s Face Tracking Templates in the Creator Companion.";
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
            Undo.RegisterCreatedObjectUndo(instance, "Set up face tracking");
            // The template animates a mesh named "Body" at the avatar's root: point it at the face wherever it is.
            string facePath = AnimationUtility.CalculateTransformPath(report.Face.transform, root);
            if (facePath != "Body" && !RewriteBody(instance, facePath))
            {
                Undo.RevertAllDownToGroup(group);
                return "This template version has no path rewrite to point it at “" + facePath + "”.";
            }
            var marker = Undo.AddComponent<MyAvatarFaceTracking>(instance);
            marker.standard = report.Standard.Id;
            marker.face = report.Face;
            Undo.SetCurrentGroupName("Set up face tracking");
            Undo.CollapseUndoOperations(group);
            return null;
        }

        private static bool RewriteBody(GameObject instance, string facePath)
        {
            foreach (var (component, _, kind) in VrcFury.Features(instance))
            {
                if (kind != "FullController") continue;
                var serialized = new SerializedObject(component);
                var rewrites = serialized.FindProperty("content.rewriteBindings");
                if (rewrites == null || !rewrites.isArray) return false;
                rewrites.arraySize++;
                var rewrite = rewrites.GetArrayElementAtIndex(rewrites.arraySize - 1);
                rewrite.FindPropertyRelative("from").stringValue = "Body";
                rewrite.FindPropertyRelative("to").stringValue = facePath;
                rewrite.FindPropertyRelative("delete").boolValue = false;
                serialized.ApplyModifiedProperties();
                return true;
            }
            return false;
        }

        /// <summary>Takes the template My Avatar set up off the avatar (one Undo step). Its package stays installed.</summary>
        internal static void Remove(MyAvatarFaceTracking marker)
        {
            if (marker != null) Undo.DestroyObjectImmediate(marker.gameObject);
        }

        // After the download reloaded Unity's scripts: finish the setup that was asked for.
        [InitializeOnLoadMethod]
        private static void Resume()
        {
            if (SessionState.GetInt(PendingKey, 0) != 0) { EditorApplication.update -= Poll; EditorApplication.update += Poll; }
        }

        private static void Poll()
        {
            if (SessionState.GetInt(PendingKey, 0) == 0) { EditorApplication.update -= Poll; return; }
            TryResume();
        }

        private static void TryResume()
        {
            int id = SessionState.GetInt(PendingKey, 0);
            if (id == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!VrcFury.Installed || !FaceTrackingDetection.TemplatesInstalled) return; // Still importing.
            SessionState.EraseInt(PendingKey);
            EditorApplication.update -= Poll;
            var avatar = EditorUtility.InstanceIDToObject(id) as MyAvatar;
            var root = avatar != null ? TextureOptimization.AvatarRoot(avatar) : null;
            if (root == null) return;
            var report = FaceTrackingDetection.Inspect(root.transform);
            string problem = report.SetUp ? null : report.Ready ? Apply(root.transform, report) : "the face no longer has enough face tracking blendshapes";
            if (problem != null) Debug.LogWarning("[My Avatar] Face tracking was not set up on " + root.name + ": " + problem);
            else Debug.Log("[My Avatar] Face tracking set up on " + root.name + " with Adjerry91’s " + report.Standard.Label + " template.");
        }
    }
}
