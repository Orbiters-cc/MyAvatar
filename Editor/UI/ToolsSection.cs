using System;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
#if MYAVATAR_VPM
using Orbiters.Toolkit.Editor.Vpm;
#endif

namespace Orbiters.MyAvatar.Editor
{
    // Tools that go with an avatar, one click each: Gesture Manager and AudioLink (added to the scene, or the one there
    // selected), d4rkAvatarOptimizer and MCB (put on this avatar, or the one there selected) and Unit Git (its window).
    // A tool that is missing is installed first through VPM; it opens once Unity has reloaded its scripts.
    internal sealed class ToolsSection : AvatarSection
    {
        internal const string GestureManagerId = "vrchat.blackstartx.gesture-manager", UnitGitId = "orbiters.unitgit",
            OptimizerId = "d4rkpl4y3r.d4rkavataroptimizer", McbId = "orbiters.mcb", AudioLinkId = "com.llealloo.audiolink";
        private const string PendingKey = "Orbiters.MyAvatar.PendingTool", PendingAvatarKey = "Orbiters.MyAvatar.PendingToolAvatar";
        private const string GestureManagerType = "BlackStartX.GestureManager.GestureManager", McbType = "MyCustomBase";
        private readonly MyAvatar avatar;
        private readonly Label note;

        internal ToolsSection(MyAvatar avatar) : base("Tools")
        {
            this.avatar = avatar;
            var row = new VisualElement(); row.AddToClassList("tools-row"); Body.Add(row);
            row.Add(Tile(IconGlyph.Person, "Gesture Manager", GestureManagerId,
                "Test gestures, the expression menu and animations in Play Mode. Added to the scene and selected; installed first when missing."));
            row.Add(Tile(IconGlyph.Gauge, "d4rk Optimizer", OptimizerId,
                "d4rkAvatarOptimizer merges meshes and materials and removes unused blendshapes and bones when you upload, for a better performance rank. " +
                "Added to this avatar and selected; installed first when missing.\nMy Avatar is not associated with d4rkAvatarOptimizer."));
            row.Add(Tile(IconGlyph.Sliders, "MCB", McbId,
                "My Custom Base: shape your avatar's body and publish it as a custom base. Added to this avatar and selected; installed first when missing."));
            row.Add(Tile(IconGlyph.Sound, "AudioLink", AudioLinkId,
                "Test the avatar's audio-reactive materials: AudioLink's avatar prefab is added to the scene and selected; installed first when missing.\n" +
                "My Avatar is not associated with AudioLink."));
            row.Add(Tile(IconGlyph.Branch, "Unit Git", UnitGitId,
                "Checkpoints and history for this project, with nothing pushed unless you ask. Installed first when missing."));
            note = new Label(); note.AddToClassList("avatar-section__note"); note.style.display = DisplayStyle.None; Body.Add(note);
        }

        private VisualElement Tile(IconGlyph glyph, string label, string id, string tooltip)
        {
            var tile = new IconButton(glyph, label, tooltip, () => Use(id, label));
            tile.AddToClassList("tools-tile");
            var state = new Label(Installed(id) ? "" : "Install"); state.AddToClassList("tools-tile__badge");
            if (!Installed(id)) tile.Add(state);
            return tile;
        }

        private void Use(string id, string label)
        {
            if (Installed(id)) { SetNote(Open(id, avatar), true); return; }
#if MYAVATAR_VPM
            SetNote("Installing " + label + "… Unity reloads its scripts, then it opens.", false);
            SessionState.SetString(PendingKey, id);
            SessionState.SetInt(PendingAvatarKey, avatar != null ? avatar.GetInstanceID() : 0);
            VpmDependencyInstallResult result;
            try { result = VpmDependencies.For("orbiters.myavatar").InstallOptional(id); }
            catch (Exception ex) { result = new VpmDependencyInstallResult { Errors = { ex.Message } }; }
            if (!result.Success)
            {
                SessionState.EraseString(PendingKey);
                SetNote(label + " could not be installed: " + result.ErrorMessage, true);
            }
#else
            SetNote("Add " + label + " to this project in the VRChat Creator Companion.", true);
#endif
        }

        private void SetNote(string text, bool warning)
        {
            note.text = text;
            note.EnableInClassList("warning", warning);
            note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static bool Installed(string id) => AssetDatabase.IsValidFolder("Packages/" + id);

        // Opens the tool; the reason when it could not.
        internal static string Open(string id, MyAvatar avatar)
        {
            switch (id)
            {
                case UnitGitId:
                    EditorApplication.ExecuteMenuItem("Tools/Orbiters/Unit Git");
                    return null;
                case AudioLinkId:
                    // AudioLink's own command: its avatar prefab in the scene (the one there when there is one), pinged.
                    return EditorApplication.ExecuteMenuItem("Tools/AudioLink/Add AudioLink Prefab to Scene") ? null
                        : "This AudioLink version has no “Add AudioLink Prefab to Scene” command: add its avatar prefab from its Runtime folder.";
                case OptimizerId:
                    return OnAvatar(avatar, TextureOptimization.OptimizerType, "d4rkAvatarOptimizer");
                case McbId:
                    return OnAvatar(avatar, TypeCache.GetTypesDerivedFrom<MonoBehaviour>().FirstOrDefault(t => t.FullName == McbType), "MCB");
            }
            // One Gesture Manager per scene: an existing one is selected, else one is added at the scene root.
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(GestureManagerType)).FirstOrDefault(t => t != null);
            var existing = type != null ? UnityEngine.Object.FindObjectsOfType(type, true).OfType<Component>().FirstOrDefault() : null;
            if (existing != null)
            {
                Select(existing.gameObject);
                return null;
            }
            EditorApplication.ExecuteMenuItem("Tools/Gesture Manager Emulator");
            return null;
        }

        // A component that belongs on the avatar's root: the one there, else added (with Undo); the root is selected so the
        // Inspector shows it.
        private static string OnAvatar(MyAvatar avatar, Type type, string label)
        {
            if (type == null) return label + " is installed but Unity has not loaded it yet: try again once its scripts compiled.";
            if (avatar == null) return "Select the avatar first.";
            var root = TextureOptimization.AvatarRoot(avatar);
            if (root == null) return "Select the avatar first.";
            if (root.GetComponent(type) == null) Undo.AddComponent(root, type);
            Select(root);
            return null;
        }

        private static void Select(GameObject target)
        {
            Selection.activeGameObject = target;
            EditorGUIUtility.PingObject(target);
        }

        // After the install reloaded Unity's scripts: open what was asked for.
        [InitializeOnLoadMethod]
        private static void Resume() => EditorApplication.delayCall += TryResume;

        private static void TryResume()
        {
            string id = SessionState.GetString(PendingKey, null);
            if (string.IsNullOrEmpty(id)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += TryResume; return; }
            SessionState.EraseString(PendingKey);
            var avatar = EditorUtility.InstanceIDToObject(SessionState.GetInt(PendingAvatarKey, 0)) as MyAvatar;
            SessionState.EraseInt(PendingAvatarKey);
            if (!Installed(id)) return;
            string problem = Open(id, avatar);
            if (problem != null) Debug.LogWarning("[My Avatar] " + problem);
        }
    }
}
