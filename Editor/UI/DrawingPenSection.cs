using System;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed class DrawingPenSection : AvatarSection
    {
        private readonly MyAvatar avatar;
        private readonly Label description;
        private readonly Button add, remove;
        private bool busy;

        internal DrawingPenSection(MyAvatar avatar) : base("Drawing pen")
        {
            this.avatar = avatar;
            description = new Label(); description.AddToClassList("avatar-section__note");
            description.style.whiteSpace = WhiteSpace.Normal; Body.Add(description);
            var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; row.style.marginTop = 10; Body.Add(row);
            add = new Button(AddPen) { text = "Add drawing pen" }; add.AddToClassList("mcb-button"); add.AddToClassList("git-button--primary"); row.Add(add);
            remove = new Button(RemovePen) { text = "Remove pen" }; remove.AddToClassList("mcb-button"); row.Add(remove);
            // The press is visible immediately; the native button still provides keyboard and mouse-click fallback.
            add.RegisterCallback<PointerDownEvent>(_ => add.style.opacity = .7f);
            add.RegisterCallback<PointerUpEvent>(_ => add.style.opacity = 1);
            add.RegisterCallback<PointerLeaveEvent>(_ => add.style.opacity = 1);
            Undo.undoRedoPerformed += Refresh;
            EditorApplication.hierarchyChanged += Refresh;
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                Undo.undoRedoPerformed -= Refresh;
                EditorApplication.hierarchyChanged -= Refresh;
            });
            Refresh();
        }

        private void Refresh()
        {
            if (avatar == null) return;
            bool installed = DrawingPenInstaller.Find(avatar.gameObject) != null;
            add.style.display = installed ? DisplayStyle.None : DisplayStyle.Flex;
            remove.style.display = installed ? DisplayStyle.Flex : DisplayStyle.None;
            description.text = installed
                ? "In VRChat: Drawing pen → Enable pen. Grab it, then make a fist to draw. Guests draw while grabbing. Release to leave it in place. Disable the pen to erase everything, or use Clear drawing.\nGuests need avatar interactions enabled. Ink is not replayed to late joiners."
                : "A shared pen that appears in front of you. Draw with a fist, let friends grab it, and leave it in the world. Turning it off clears the drawing.";
        }

        private void AddPen()
        {
            if (busy) return;
            busy = true; add.SetEnabled(false); add.text = "Adding pen…";
            schedule.Execute(() =>
            {
                try { DrawingPenInstaller.Install(avatar.gameObject); Refresh(); }
                catch (Exception ex) { description.text = ex.Message; }
                finally { busy = false; add.SetEnabled(true); add.text = "Add drawing pen"; }
            }).StartingIn(30);
        }

        private void RemovePen()
        {
            var pen = DrawingPenInstaller.Find(avatar.gameObject);
            if (pen != null)
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Remove drawing pen");
                Undo.DestroyObjectImmediate(pen.gameObject);
            }
            Refresh();
        }
    }
}
