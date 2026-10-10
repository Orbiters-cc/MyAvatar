using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// The "Edit menu" card, framed like face tracking and GoGoLoco: a Quick Menu tile as its badge, what the menu holds, the
    /// button opening <see cref="MenuEditorWindow"/>, and "Remove default menu" (the avatar's own menu left out of the upload,
    /// with the layers only its items switch; one Undo step).
    /// </summary>
    internal sealed class MenuSection : AvatarSection
    {
        private const string GestureManagerUrl = "https://github.com/BlackStartX/VRC-Gesture-Manager";
        private static readonly Color Glow = new Color(.17f, .65f, .74f, .22f);

        private readonly MyAvatar avatar;
        private readonly VisualElement hero;
        private IVisualElementScheduledItem pending;
        private string shown;

        internal MenuSection(MyAvatar avatar) : base("Menu", card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.myavatar/Editor/Expressions/UI/menu-editor.uss");
            if (sheet) styleSheets.Add(sheet);
            hero = new VisualElement(); Body.Add(hero);
            ToolHero.Credit(Body, "Action Menu view from", "Gesture Manager", GestureManagerUrl,
                "BlackStartX's Gesture Manager (MIT License): the Action Menu view is its radial menu.");
            RegisterCallback<AttachToPanelEvent>(_ => { Subscribe(); Schedule(); });
            RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            Refresh();
        }

        private void Subscribe()
        {
            Unsubscribe();
            EditorApplication.hierarchyChanged += Schedule; Undo.undoRedoPerformed += Schedule; MenuEditorWindow.Changed += Schedule;
        }

        private void Unsubscribe()
        {
            EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule; MenuEditorWindow.Changed -= Schedule;
        }

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(300);
        }

        private void Refresh()
        {
            var root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            var descriptor = root != null ? root.GetComponent<VRCAvatarDescriptor>() : null;
            style.display = descriptor == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (descriptor == null) return;
            var built = MenuBuild.Run(MenuInputs.Read(root));
            var all = MenuTree.Walk(built.Root);
            int items = all.Count(e => !e.Node.IsFolder), folders = all.Count(e => e.Node.IsFolder);
            int pending = MenuEditorWindow.PendingChanges(root);
            int defaults = MenuUploadBuild.DefaultItems(built.Root).Count;
            int left = avatar.menuRemovals.Count;
            bool removed = avatar.removeDefaultMenu;
            string state = items + ":" + folders + ":" + pending + ":" + built.VrcFury + ":" + built.ModularAvatar + ":" + defaults + ":" + left + ":" + removed;
            if (state == shown) return;
            shown = state;
            hero.Clear();
            string eyebrow = pending > 0 ? pending + (pending == 1 ? " CHANGE NOT SAVED" : " CHANGES NOT SAVED")
                : all.Count == 0 ? "NO MENU YET" : items + (items == 1 ? " ITEM" : " ITEMS") + " · " + folders + (folders == 1 ? " FOLDER" : " FOLDERS") +
                  (removed ? " · DEFAULT MENU REMOVED" : left > 0 ? " · " + left + " LEFT OUT" : "");
            var frame = new ToolHero(hero, Glow, eyebrow, pending > 0 ? "warning" : all.Count == 0 ? "muted" : null, "Edit menu",
                "The Expressions menu exactly as VRChat will show it" + (built.VrcFury && built.ModularAvatar ? ", with everything VRCFury and Modular Avatar add"
                    : built.VrcFury ? ", with everything VRCFury adds" : built.ModularAvatar ? ", with everything Modular Avatar adds" : "") +
                ". Rename, reorder, move items into folders, change icons and settings, or leave items out of the upload, then save.");
            frame.Badge.Add(Badge());
            // Still in beta: the chip My Avatar's and ReFit's other beta features carry.
            var beta = new Orbiters.Toolkit.Editor.StageBadge(Orbiters.Toolkit.Editor.FeatureStage.Beta);
            beta.AddToClassList("me-beta");
            frame.Frame.Q<VisualElement>(className: "ft-hero__title-row")?.Add(beta);
            var open = MyAvatarEditor.Button("Edit menu", null);
            open.AddToClassList("ft-primary");
            open.tooltip = "Open the menu editor on this avatar.";
            Orbiters.Toolkit.Editor.ButtonInteraction.RegisterImmediateClick(open, () => MenuEditorWindow.Open(root, avatar));
            frame.Actions.Add(open);
            if (defaults == 0 && !removed) return;
            var strip = MyAvatarEditor.Button(removed ? "Put the default menu back" : "Remove default menu", null);
            strip.AddToClassList("ft-ghost");
            strip.AddToClassList(removed ? "me-card-default--on" : "me-card-default");
            strip.tooltip = removed
                ? "The avatar's own menu (" + defaults + " items, as its creator made it) is left out of the upload, with the layers only its items switch. Click to upload it again."
                : "Leave the avatar's own menu (" + defaults + " items, as its creator made it) out of the upload, with the layers only its items switch: what they don't touch keeps working (a petting reaction stays, its on/off switch goes), and what they could only show (other materials, hidden objects) isn't uploaded.";
            Orbiters.Toolkit.Editor.ButtonInteraction.RegisterImmediateClick(strip, () =>
            {
                Undo.RecordObject(avatar, removed ? "Put the default menu back" : "Remove default menu");
                avatar.removeDefaultMenu = !removed;
                EditorUtility.SetDirty(avatar);
                PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
                shown = null;
                Refresh();
                MenuEditorWindow.Refreshed(root);
            });
            frame.Actions.Add(strip);
        }

        // A Quick Menu folder tile, the way VRChat draws it.
        private static VisualElement Badge()
        {
            var tile = new VisualElement { pickingMode = PickingMode.Ignore };
            tile.AddToClassList("me-badge");
            var icon = new Image { image = MenuArt.Person, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            icon.AddToClassList("me-badge__icon"); tile.Add(icon);
            var circle = new VisualElement { pickingMode = PickingMode.Ignore }; circle.AddToClassList("me-badge__circle"); tile.Add(circle);
            var glyph = new Image { image = MenuArt.Badge(ControlType.SubMenu), scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            glyph.AddToClassList("me-badge__glyph"); circle.Add(glyph);
            return tile;
        }
    }
}
