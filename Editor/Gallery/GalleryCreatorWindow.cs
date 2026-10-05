using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Publishing to the gallery in its own window, like MCB's Create MCB Version: selecting something else in the project,
    /// a script reload or closing the Inspector keeps the form. Its draft lives in <see cref="GalleryCreatorDraft"/>.
    /// </summary>
    internal sealed class GalleryCreatorWindow : EditorWindow
    {
        // The avatar packages are tried on; publishing works without one.
        [SerializeField] private MyAvatar avatar;

        internal static GalleryCreatorWindow Open(MyAvatar avatar)
        {
            var window = Resources.FindObjectsOfTypeAll<GalleryCreatorWindow>().FirstOrDefault();
            if (window == null)
            {
                window = CreateInstance<GalleryCreatorWindow>();
                window.titleContent = new GUIContent("Publish to the gallery");
                window.minSize = new Vector2(460, 480);
                window.position = new Rect(160, 120, 560, 860);
            }
            if (avatar && window.avatar != avatar) { window.avatar = avatar; window.Rebuild(); }
            window.Show();
            window.Focus();
            return window;
        }

        private void CreateGUI() => Rebuild();

        private void Rebuild()
        {
            rootVisualElement.Clear();
            foreach (string path in new[] { "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss", "Packages/orbiters.myavatar/Editor/UI/myavatar.uss",
                         "Packages/orbiters.toolkit/Editor/UI/gallery-card.uss", GalleryUI.StyleSheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet && !rootVisualElement.styleSheets.Contains(sheet)) rootVisualElement.styleSheets.Add(sheet);
            }
            rootVisualElement.AddToClassList("gallery-creator-window");
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.AddToClassList("gallery-creator-window__scroll");
            rootVisualElement.Add(scroll);
            scroll.Add(new GalleryCreatorPage(avatar, Close));
        }
    }
}
