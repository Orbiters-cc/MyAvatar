using Orbiters.MyAvatar.Editor.Locomotion;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // GoGoLoco, Franada's locomotion system, in one click, framed like face tracking: offered on every humanoid avatar,
    // then what is on it (Select, Remove), or the setup made another way.
    internal sealed class GoGoLocoSection : AvatarSection
    {
        private const string LogoPath = "Packages/orbiters.myavatar/Editor/UI/GoGoLocoLogo.png";
        private static readonly Color Glow = new Color(.36f, .55f, 1f, .2f);

        private readonly MyAvatar avatar;
        private readonly VisualElement hero;
        private readonly Label note;
        private IVisualElementScheduledItem pending;
        private string shown;
        private GameObject root;
        private GoGoLocoReport report;

        internal GoGoLocoSection(MyAvatar avatar) : base("Locomotion", card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            hero = new VisualElement(); Body.Add(hero);
            note = new Label(); note.AddToClassList("ft-note"); note.style.display = DisplayStyle.None; Body.Add(note);
            ToolHero.Credit(Body, "Locomotion by", "Franada’s GoGoLoco", GoGoLoco.Author,
                GoGoLoco.Author + "\nGoGoLoco is Franada’s: credit it on your store or product page when you sell an avatar that uses it.");
            RegisterCallback<AttachToPanelEvent>(_ => { Subscribe(); Schedule(); });
            RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            Refresh();
        }

        private void Subscribe()
        {
            Unsubscribe();
            EditorApplication.hierarchyChanged += Schedule; Undo.undoRedoPerformed += Schedule;
        }

        private void Unsubscribe()
        {
            EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule;
        }

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(400);
        }

        private void Refresh()
        {
            root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            bool fits = root != null && GoGoLoco.Fits(root.transform);
            report = GoGoLoco.Inspect(fits ? root.transform : null);
            string state = !fits ? "" : report.Prefab != null ? "ours:" + report.Prefab.GetInstanceID() + ":" + GoGoLoco.Version
                : report.Existing != null ? "other:" + report.Existing : "ready:" + GoGoLoco.Installed + ":" + GoGoLoco.UsesModularAvatar;
            style.display = state.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (state == shown) return;
            shown = state;
            hero.Clear(); Actions.Clear();
            if (report.Prefab != null) ShowOurs();
            else if (report.Existing != null) ShowOther();
            else if (fits) ShowSuggestion();
        }

        private ToolHero Hero(string eyebrow, string eyebrowClass, string title, string subtitle, string version = null)
        {
            var frame = new ToolHero(hero, Glow, eyebrow, eyebrowClass, title, subtitle, version);
            var logo = AssetDatabase.LoadAssetAtPath<Texture2D>(LogoPath);
            if (logo != null)
            {
                var picture = new Image { image = logo, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                picture.AddToClassList("ft-hero__picture");
                frame.Badge.Add(picture);
            }
            return frame;
        }

        private void ShowSuggestion()
        {
            string with = GoGoLoco.UsesModularAvatar ? "Modular Avatar" : "VRCFury";
            var frame = Hero("READY FOR GOGOLOCO", null, "GoGoLoco",
                "Franada’s locomotion system: sit, lie down and pose anywhere, stay in place, and more from the Action Menu in VRChat. It goes on with " + with + "." +
                (GoGoLoco.Installed ? "" : " GoGoLoco is downloaded first (Creator Companion)."));
            var add = MyAvatarEditor.Button("Add", null);
            add.AddToClassList("ft-primary");
            add.tooltip = "Add GoGoLoco to this avatar (one Undo step).";
            add.clicked += () =>
            {
                add.SetEnabled(false);
                SetNote(GoGoLoco.Installed ? "Adding GoGoLoco…" : "Downloading GoGoLoco…", false);
                // Paint the immediate response before the download or the prefab and Undo work.
                schedule.Execute(() =>
                {
                    string result = root != null ? GoGoLoco.Start(avatar, root.transform) : "The avatar is no longer open.";
                    SetNote(result, result != null && !result.StartsWith("Downloaded"));
                    add.SetEnabled(true);
                    Refresh();
                });
            };
            frame.Actions.Add(add);
        }

        private void ShowOurs()
        {
            var prefab = report.Prefab;
            string version = GoGoLoco.Version;
            Hero("GOGOLOCO ON", null, "GoGoLoco", "“" + prefab.name + "” is on this avatar: its menu is in the Action Menu in VRChat.",
                version != null ? "v" + version : null);
            var select = MyAvatarEditor.Button("Select", () => { Selection.activeGameObject = prefab; EditorGUIUtility.PingObject(prefab); });
            select.AddToClassList("avatar-link");
            select.tooltip = "Select GoGoLoco on the avatar.";
            Actions.Add(select);
            if (!GoGoLoco.Removable(prefab)) return;
            var remove = MyAvatarEditor.Button("Remove", null);
            remove.AddToClassList("avatar-link");
            remove.tooltip = "Take GoGoLoco off this avatar. Undo puts it back; the downloaded package stays installed.";
            remove.clicked += () =>
            {
                SetEnabled(false);
                schedule.Execute(() => { GoGoLoco.Remove(prefab); SetEnabled(true); SetNote(null, false); Refresh(); });
            };
            Actions.Add(remove);
        }

        private void ShowOther()
        {
            var frame = Hero("GOGOLOCO", "muted", "Already set up", "GoGoLoco is already set up: " + report.Existing + ".");
            if (report.ExistingObject == null) return;
            var target = report.ExistingObject;
            var select = MyAvatarEditor.Button("Select", () => { Selection.activeGameObject = target; EditorGUIUtility.PingObject(target); });
            select.AddToClassList("ft-ghost");
            frame.Actions.Add(select);
        }

        private void SetNote(string text, bool warning)
        {
            note.text = text;
            note.EnableInClassList("warning", warning);
            note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
