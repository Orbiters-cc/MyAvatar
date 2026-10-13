using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.Expressions;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Storage;
using Orbiters.Toolkit.Storage;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// The expression parameters My Avatar writes for an avatar (a look's starting value, the menu editor's settings): the
    /// descriptor's own asset when it is the avatar's alone, else a copy put in its place first, in the avatar's folder of
    /// <see cref="MenuSave.CopiesFolder"/> (the descriptor change is recorded for Undo). Copies are tracked as generated
    /// files: one an Undo left unused goes, and a Redo brings it back. Shared means: in a package or read-only, used by
    /// another avatar or by a VRCFury Full Controller in the open scenes (this avatar's own too: it merges them as they
    /// are), or used by a prefab or scene of the project other than the avatar's own scene (its other variants).
    /// </summary>
    internal static class AvatarParameters
    {
        private static readonly string[] Users = { "t:Prefab", "t:Scene" };
        // The project files using an asset, asked again only once the project changed: a slider writes on every rest.
        private static readonly Dictionary<string, List<string>> FileUsers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        [InitializeOnLoadMethod]
        private static void ForgetOnProjectChange() => EditorApplication.projectChanged += FileUsers.Clear;

        /// <summary>The parameters asset of <paramref name="descriptor"/> to write, copied first when it is shared.</summary>
        internal static VRCExpressionParameters Own(VRCAvatarDescriptor descriptor, string undo) => Own(descriptor, undo, out _);

        /// <param name="created">The copy's path when one was made now, else null.</param>
        internal static VRCExpressionParameters Own(VRCAvatarDescriptor descriptor, string undo, out string created)
        {
            created = null;
            var asset = descriptor != null ? descriptor.expressionParameters : null;
            if (asset == null || !Shared(asset, descriptor.gameObject)) return asset;
            var copy = Object.Instantiate(asset);
            copy.name = asset.name;
            string folder = Folder(MenuSave.CopiesFolder + "/" + SafePaths.FileName(descriptor.gameObject.name, "Avatar"));
            created = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + SafePaths.FileName(asset.name, "Parameters") + ".asset");
            AssetDatabase.CreateAsset(copy, created);
            // A copy that an Undo leaves unused goes; a test's avatar (in a preview scene) is not the user's.
            if (!UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(descriptor.gameObject.scene)) GeneratedAssets.Track(created);
            Undo.RecordObject(descriptor, undo);
            descriptor.expressionParameters = copy;
            EditorUtility.SetDirty(descriptor);
            PrefabUtility.RecordPrefabInstancePropertyModifications(descriptor);
            Debug.Log("[My Avatar] " + descriptor.name + "'s expression parameters “" + asset.name + "” are shared (" + AssetDatabase.GetAssetPath(asset) +
                      "): this avatar now uses its own copy, " + created + ", so changing them changes it alone.");
            return copy;
        }

        /// <summary>Whether writing <paramref name="asset"/> would change more than the avatar of <paramref name="root"/>.</summary>
        internal static bool Shared(VRCExpressionParameters asset, GameObject root)
        {
            if (asset == null) return false;
            string path = AssetDatabase.GetAssetPath(asset);
            if (path.Length > 0 && (!path.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsOpenForEdit(path))) return true;
            if (UsedInScenes(asset, root)) return true;
            if (path.Length == 0) return false;
            if (!FileUsers.TryGetValue(path, out var users))
                FileUsers[path] = users = ProjectReferences.DirectUsers(new[] { path }, Users).TryGetValue(path, out var found) ? found : new List<string>();
            // The avatar's own scene (or the prefab open in Prefab Mode) uses it without sharing it; so does the prefab the
            // avatar is an instance of for a copy made for it (its overrides applied), as the menu editor counts it.
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.scene.path ?? "" };
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(root);
            if (stage != null) own.Add(stage.assetPath);
            if (path.StartsWith(MenuSave.CopiesFolder + "/", StringComparison.Ordinal) && PrefabUtility.IsPartOfPrefabInstance(root))
                own.Add(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root));
            return users.Any(user => !own.Contains(user));
        }

        private static bool UsedInScenes(VRCExpressionParameters asset, GameObject root)
        {
            if (Merges(root, asset)) return true;
            for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var top in scene.GetRootGameObjects())
                foreach (var other in top.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                    if (other.gameObject != root && (other.expressionParameters == asset || Merges(other.gameObject, asset))) return true;
            }
            return false;
        }

        // A VRCFury Full Controller of the avatar merges the asset as it is.
        private static bool Merges(GameObject avatar, VRCExpressionParameters asset)
        {
            foreach (var (_, feature, kind) in VrcFury.Features(avatar))
            {
                if (kind != "FullController" || !(VrcFury.Field(feature, "prms") is System.Collections.IEnumerable entries)) continue;
                foreach (var entry in entries)
                    if (VrcFury.ObjectReference(VrcFury.Field(entry, "parameters")) == asset) return true;
            }
            return false;
        }

        private static string Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return path;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            Folder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
            return path;
        }
    }
}
