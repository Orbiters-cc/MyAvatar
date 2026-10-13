using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// Sets up Adjerry91's face tracking template in one click: installs it (and VRCFury) through VPM when missing, once the
    /// plan of what it downloads was accepted (<see cref="Downloads"/>), puts the
    /// template for the avatar's blendshape standard on the avatar and points it at the face mesh. The template's files
    /// stay as they are, as its author asks; the build animates the face's own blendshape names and keeps hand gestures
    /// off the face while tracking (<see cref="FaceTrackingBuild"/>).
    /// </summary>
    internal static class FaceTrackingSetup
    {
        private const string VrcFuryId = "com.vrcfury.vrcfury";
        // The template animates a mesh named "Body" at the avatar's root.
        internal const string TemplateFace = "Body";
        /// <summary>The downloads face tracking needs, and the avatars waiting for them.</summary>
        internal static readonly PackageSetup Downloads = new PackageSetup("FaceTracking", "face tracking", "VRCFury or Adjerry91’s Face Tracking Templates",
            () => VrcFury.Installed && FaceTrackingDetection.TemplatesInstalled, Finish);

        /// <summary>The packages to download before face tracking can be set up (package ids), empty when none.</summary>
        internal static List<string> Missing()
        {
            var missing = new List<string>();
            if (!VrcFury.Installed) missing.Add(VrcFuryId);
            if (!FaceTrackingDetection.TemplatesInstalled) missing.Add(FaceTrackingDetection.PackageId);
            return missing;
        }

        /// <summary>
        /// Sets up face tracking on <paramref name="root"/> once nothing is missing (<see cref="Missing"/>; the section
        /// downloads it first). Returns a note for the user.
        /// </summary>
        internal static string Start(MyAvatar avatar, Transform root)
        {
            var report = FaceTrackingDetection.Inspect(root);
            if (report.SetUp) return "Face tracking is already set up on this avatar.";
            if (!report.Ready) return "This avatar's face does not have enough face tracking blendshapes.";
            var missing = Missing();
            if (missing.Count > 0) return "Download " + string.Join(" and ", missing) + " first.";
            Downloads.Forget(avatar);
            return Apply(root, report);
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
            Undo.RegisterCreatedObjectUndo(instance, "Add face tracking");
            // Point the template's "Body" at the face wherever it is.
            string facePath = AnimationUtility.CalculateTransformPath(report.Face.transform, root);
            if (!PointBody(instance, facePath, undo: true))
            {
                Undo.RevertAllDownToGroup(group);
                return "This template version has no path rewrite to point it at “" + facePath + "”.";
            }
            var marker = Undo.AddComponent<MyAvatarFaceTracking>(instance);
            marker.standard = report.Standard.Id;
            marker.face = report.Face;
            Undo.SetCurrentGroupName("Add face tracking");
            Undo.CollapseUndoOperations(group);
            return null;
        }

        /// <summary>The face <paramref name="marker"/> animates: its own when it is on the avatar, else the face found there (null if none).</summary>
        internal static SkinnedMeshRenderer CurrentFace(MyAvatarFaceTracking marker, Transform root)
        {
            if (marker == null || root == null) return null;
            var face = marker.face;
            return face != null && face.sharedMesh != null && face.transform.IsChildOf(root) ? face : FaceTrackingDetection.Face(root);
        }

        /// <summary>Where the template's "Body" points: the path of its rewrite, "Body" without one, null without a Full Controller.</summary>
        internal static string BodyTarget(GameObject template)
        {
            bool any = false;
            foreach (var (_, feature, kind) in VrcFury.Features(template))
            {
                if (kind != "FullController") continue;
                any = true;
                if (!(VrcFury.Field(feature, "rewriteBindings") is IEnumerable rules)) continue;
                foreach (var rule in rules)
                    if (rule != null && rule.GetType().GetField("from")?.GetValue(rule) as string == TemplateFace)
                        return rule.GetType().GetField("to")?.GetValue(rule) as string ?? "";
            }
            return any ? TemplateFace : null;
        }

        /// <summary>
        /// Points the template's "Body" at <paramref name="facePath"/>: the Full Controllers with a "Body" rewrite follow (the
        /// first one gets it when none has), and it goes when the face is "Body". False when the template has no rewrites.
        /// </summary>
        internal static bool PointBody(GameObject template, string facePath, bool undo)
        {
            var lists = VrcFury.Features(template).Where(f => f.kind == "FullController")
                .Select(f => { var serialized = new SerializedObject(f.component); return (serialized, rules: serialized.FindProperty("content.rewriteBindings")); })
                .Where(f => f.rules != null && f.rules.isArray).ToList();
            if (lists.Count == 0) return facePath == TemplateFace;
            bool anyRule = lists.Any(f => BodyRule(f.rules) >= 0);
            for (int i = 0; i < lists.Count; i++)
            {
                var (serialized, rules) = lists[i];
                int at = BodyRule(rules);
                if (facePath == TemplateFace) { if (at < 0) continue; rules.DeleteArrayElementAtIndex(at); }
                else if (at >= 0)
                {
                    var to = rules.GetArrayElementAtIndex(at).FindPropertyRelative("to");
                    if (to.stringValue == facePath) continue;
                    to.stringValue = facePath;
                }
                else if (i == 0 && !anyRule)
                {
                    rules.arraySize++;
                    var rule = rules.GetArrayElementAtIndex(rules.arraySize - 1);
                    rule.FindPropertyRelative("from").stringValue = TemplateFace;
                    rule.FindPropertyRelative("to").stringValue = facePath;
                    rule.FindPropertyRelative("delete").boolValue = false;
                }
                else continue;
                if (undo) serialized.ApplyModifiedProperties();
                else serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return true;
        }

        private static int BodyRule(SerializedProperty rules)
        {
            for (int i = 0; i < rules.arraySize; i++)
                if (rules.GetArrayElementAtIndex(i).FindPropertyRelative("from")?.stringValue == TemplateFace) return i;
            return -1;
        }

        /// <summary>Points the template at the avatar's face again (one Undo step): the marker's face, else the face found. Null when done, else why not.</summary>
        internal static string Repoint(MyAvatarFaceTracking marker, Transform root)
        {
            var face = CurrentFace(marker, root);
            if (face == null) return "No face was found on this avatar.";
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            if (marker.face != face)
            {
                Undo.RecordObject(marker, "Repoint face tracking");
                marker.face = face;
                PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
            }
            string facePath = AnimationUtility.CalculateTransformPath(face.transform, root);
            if (!PointBody(marker.gameObject, facePath, undo: true))
            {
                Undo.RevertAllDownToGroup(group);
                return "This template version has no path rewrite to point it at “" + facePath + "”.";
            }
            Undo.SetCurrentGroupName("Repoint face tracking");
            Undo.CollapseUndoOperations(group);
            return null;
        }

        /// <summary>Takes the template My Avatar set up off the avatar (one Undo step). Its package stays installed.</summary>
        internal static void Remove(MyAvatarFaceTracking marker)
        {
            if (marker != null) Undo.DestroyObjectImmediate(marker.gameObject);
        }

        // After the download reloaded Unity's scripts: finish the setup that was asked for.
        [InitializeOnLoadMethod]
        private static void Resume() => Downloads.Resume();

        private static string Finish(GameObject root)
        {
            var report = FaceTrackingDetection.Inspect(root.transform);
            string problem = report.SetUp ? null : report.Ready ? Apply(root.transform, report) : "the face no longer has enough face tracking blendshapes";
            if (problem == null) Debug.Log("[My Avatar] Face tracking set up on " + root.name + " with Adjerry91’s " + report.Standard.Label + " template.");
            return problem;
        }
    }
}
