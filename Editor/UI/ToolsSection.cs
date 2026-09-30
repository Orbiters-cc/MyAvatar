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
    // Tools that go with an avatar, one click each: Gesture Manager (added to the scene root and selected) and Unit Git
    // (its window). A tool that is missing is installed first through VPM; it opens once Unity has reloaded its scripts.
    internal sealed class ToolsSection : AvatarSection
    {
        internal const string GestureManagerId = "vrchat.blackstartx.gesture-manager", UnitGitId = "orbiters.unitgit";
        private const string PendingKey = "Orbiters.MyAvatar.PendingTool";
        private const string GestureManagerType = "BlackStartX.GestureManager.GestureManager";
        private readonly Label note;

        internal ToolsSection() : base("Tools")
        {
            var row = new VisualElement(); row.AddToClassList("tools-row"); Body.Add(row);
            row.Add(Tile(IconGlyph.Person, "Gesture Manager", GestureManagerId,
                "Test gestures, the expression menu and animations in Play Mode. Added to the scene and selected; installed first when missing."));
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
            if (Installed(id)) { Open(id); return; }
#if MYAVATAR_VPM
            SetNote("Installing " + label + "… Unity reloads its scripts, then it opens.", false);
            SessionState.SetString(PendingKey, id);
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

        internal static void Open(string id)
        {
            if (id == UnitGitId) { EditorApplication.ExecuteMenuItem("Tools/Orbiters/Unit Git"); return; }
            // One Gesture Manager per scene: an existing one is selected, else one is added at the scene root.
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(GestureManagerType)).FirstOrDefault(t => t != null);
            var existing = type != null ? UnityEngine.Object.FindObjectsOfType(type, true).OfType<Component>().FirstOrDefault() : null;
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }
            EditorApplication.ExecuteMenuItem("Tools/Gesture Manager Emulator");
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
            if (Installed(id)) Open(id);
        }
    }
}
