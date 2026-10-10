using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// VRChat's Quick Menu "Expressions" page, measured from the game: the sidebar, the header (menu button or back, the title),
    /// Reset Avatar on the main menu, then the items on two columns of rounded teal tiles with their icon, badge and label.
    /// Items are picked, ticked (their select box), renamed (double click), given an icon (drop a picture on them) and dragged:
    /// between items to another place, onto a folder's middle to put them inside, held on another item's middle to make a
    /// folder of the two, onto the back button or a breadcrumb to move them up. Hovering a folder (or back) while dragging opens
    /// it. Tiles are known by item id, so the menu can be rebuilt (live changes of the avatar) without disturbing a press or a drag.
    /// </summary>
    internal sealed class QuickMenuView : VisualElement
    {
        // The layout, in pixels of the capture it was measured on (436 × 724).
        public const float Width = 312f, Height = 642f;
        private const float TileWidth = 102f, TileHeight = 95f, Gap = 19f, Left = 17f, Top = 28f, ResetHeight = 81f;
        private const float DragStart = 5f, SpringDelay = 650f, FoldDelay = 450f;
        private const string BackTarget = "\u0001back";
        private const string DragHint = "Drop between items to move it, on a folder to put it inside, hold it on an item to make a folder, on back or a breadcrumb to move it up. Esc cancels.";

        private readonly IMenuEditor editor;
        private readonly VisualElement frame, header, grid, back;
        private readonly Label title;
        private readonly List<VisualElement> bars = new List<VisualElement>();
        private readonly Dictionary<string, VisualElement> tiles = new Dictionary<string, VisualElement>();
        private MenuViewData data;
        private TextField rename;

        // Pressing and dragging: the item (by id, and as last seen), its ghost, where it would land.
        private string pressed, dragged, into, crumb, springTarget, fold, folding;
        private MenuNode draggedNode;
        private VisualElement ghost, gap, foldBackdrop;
        private Vector2 pressedAt, grab;
        private int pointer = -1, insertion = -1;
        private bool overBack, outside;
        private IVisualElementScheduledItem spring, foldTimer;

        public QuickMenuView(IMenuEditor editor)
        {
            this.editor = editor;
            AddToClassList("qm");
            pickingMode = PickingMode.Position;
            var sidebar = new VisualElement { pickingMode = PickingMode.Ignore }; sidebar.AddToClassList("qm-sidebar"); Add(sidebar);
            var logo = new QuickMenuLogo(); logo.AddToClassList("qm-logo"); sidebar.Add(logo);
            frame = new VisualElement { pickingMode = PickingMode.Ignore }; frame.AddToClassList("qm-panel"); Add(frame);
            header = new VisualElement { pickingMode = PickingMode.Ignore }; header.AddToClassList("qm-header"); frame.Add(header);
            back = new VisualElement(); back.AddToClassList("qm-menu-button"); header.Add(back);
            for (int i = 0; i < 3; i++)
            {
                var bar = new VisualElement { pickingMode = PickingMode.Ignore }; bar.AddToClassList("qm-menu-button__bar");
                bar.style.top = i * 12f; back.Add(bar); bars.Add(bar);
            }
            back.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || !editor.CanBack) return;
                evt.StopPropagation();
                editor.Back();
            });
            title = new Label { pickingMode = PickingMode.Ignore }; title.AddToClassList("qm-title"); header.Add(title);
            grid = new VisualElement { pickingMode = PickingMode.Ignore }; grid.AddToClassList("qm-grid"); frame.Add(grid);
            style.width = Width; style.height = Height;
            frame.style.height = Height;

            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => CancelDrag());
            RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape && dragged != null) { CancelDrag(); evt.StopPropagation(); } });
        }

        public bool Dragging => dragged != null;

        private MenuNode Find(string id) => id == null ? null : data?.Page?.Items.FirstOrDefault(n => n.Id == id);

        private int IndexOf(string id) => data?.Page == null || id == null ? -1 : data.Page.Items.FindIndex(n => n.Id == id);

        /// <summary>The tile showing an item (tests and the editor's rename).</summary>
        internal VisualElement TileOf(string id) => id != null && tiles.TryGetValue(id, out var tile) ? tile : null;

        public void Show(MenuViewData next)
        {
            data = next;
            // A long folder name shrinks on one line, like the labels.
            MenuArt.Fit(title, data.Root ? "Expressions" : data.Title, 196, 37, 24, 12, lines: 1);
            // The menu button on the main menu, back (a chevron made of the same bars) inside a folder.
            bool root = data.Root;
            back.EnableInClassList("qm-back", !root);
            back.tooltip = root ? "" : "Back (drop an item here to move it up)";
            bars[0].style.width = root ? 28 : 17; bars[2].style.width = root ? 28 : 17;
            bars[1].style.display = root ? DisplayStyle.Flex : DisplayStyle.None;
            bars[0].style.rotate = new Rotate(root ? 0 : -42); bars[2].style.rotate = new Rotate(root ? 0 : 42);
            bars[0].style.left = root ? 0 : 3; bars[2].style.left = root ? 0 : 3;
            bars[0].style.top = root ? 0 : 6; bars[2].style.top = root ? 24 : 17;
            Rebuild();
        }

        /// <summary>The same page as shown, from a newer build of the menu: nothing is redrawn.</summary>
        public void Rebind(MenuViewData next) => data = next;

        /// <summary>Marks the selected item without rebuilding the tiles (a press keeps its tile).</summary>
        public void SetSelected(string id)
        {
            if (data != null) data.Selected = id;
            foreach (var pair in tiles) pair.Value.EnableInClassList("qm-tile--selected", pair.Key == id);
        }

        /// <summary>Ticks the select boxes of the ticked items, without rebuilding the tiles.</summary>
        public void SetChecked(HashSet<string> ids)
        {
            if (data != null) data.Checked = ids;
            MenuCheck.Restyle(grid, ids);
        }

        // ---- Tiles ----------------------------------------------------------------------------------------------------------

        private float GridTop => data != null && data.Root ? Top + ResetHeight + Gap : Top;

        private Vector2 Slot(int index) => new Vector2(Left + (index % 2) * (TileWidth + Gap), GridTop + (index / 2) * (TileHeight + Gap));

        private void Rebuild()
        {
            EndRename(false);
            grid.Clear();
            tiles.Clear();
            gap = null;
            foldBackdrop = null;
            if (data == null) return;
            if (data.Root)
            {
                var reset = Tile(new MenuNode { Name = "Reset Avatar", Type = ControlType.Button, Icon = MenuArt.Reset, Id = "vrc:reset" }, wide: true);
                reset.style.left = Left; reset.style.top = Top;
                reset.tooltip = "VRChat's own button: it always comes first in the Expressions menu.";
                reset.AddToClassList("qm-tile--static");
                grid.Add(reset);
            }
            var items = data.Page?.Items ?? new List<MenuNode>();
            for (int i = 0; i < items.Count; i++)
            {
                var node = items[i];
                var tile = Tile(node, wide: false);
                var at = Slot(i);
                tile.style.left = at.x; tile.style.top = at.y;
                tiles[node.Id] = tile;
                grid.Add(tile);
                Interact(tile, node.Id);
            }
            if (pressed != null && dragged == null && tiles.TryGetValue(pressed, out var held)) held.AddToClassList("qm-tile--pressed");
            if (items.Count == 0)
            {
                var empty = new Label(data.Root ? "This avatar has no expressions menu yet." : "This menu is empty. Drag items here.") { pickingMode = PickingMode.Ignore };
                empty.AddToClassList("qm-empty");
                empty.style.top = GridTop + 40;
                grid.Add(empty);
            }
            if (dragged != null) Relayout();
        }

        private VisualElement Tile(MenuNode node, bool wide, bool ghostTile = false)
        {
            var tile = new VisualElement();
            tile.AddToClassList("qm-tile");
            if (wide) tile.AddToClassList("qm-tile--wide");
            var icon = new Image { image = node.Icon != null ? node.Icon : MenuArt.Person, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            icon.AddToClassList("qm-tile__icon");
            tile.Add(icon);
            var badge = MenuArt.Badge(node.Type);
            if (badge != null && !wide)
            {
                var circle = new VisualElement { pickingMode = PickingMode.Ignore }; circle.AddToClassList("qm-tile__badge");
                var glyph = new Image { image = badge, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore }; glyph.AddToClassList("qm-tile__glyph");
                circle.Add(glyph); tile.Add(circle);
            }
            var label = new Label { pickingMode = PickingMode.Ignore, enableRichText = true };
            label.AddToClassList("qm-tile__label");
            tile.Add(label);
            MenuArt.Fit(label, node.Name, wide ? 217 : 96, 28);
            if (data != null && data.Changed.Contains(node.Id))
            {
                var dot = new VisualElement { pickingMode = PickingMode.Ignore, tooltip = "Changed" }; dot.AddToClassList("qm-tile__dot"); tile.Add(dot);
            }
            if (!wide && !ghostTile && editor.CanCheck(node)) tile.Add(MenuCheck.Create(editor, node, data != null && data.Checked.Contains(node.Id)));
            if (!wide && !ghostTile && editor.CanDelete(node)) tile.Add(MenuDelete.Create(editor, node));
            if (data != null && node.Id == data.Selected) tile.AddToClassList("qm-tile--selected");
            return tile;
        }

        private void Interact(VisualElement tile, string id)
        {
            var node = Find(id);
            tile.tooltip = node != null ? Describe(node) : null;
            tile.RegisterCallback<PointerDownEvent>(evt =>
            {
                var current = Find(id);
                if (evt.button != 0 || dragged != null || current == null) return;
                // Feedback at once; what the press does waits for the release (or a drag).
                tile.AddToClassList("qm-tile--pressed");
                if (evt.clickCount >= 2 && editor.CanRename(current)) { tile.RemoveFromClassList("qm-tile--pressed"); BeginRename(current); evt.StopPropagation(); return; }
                pressed = id; pressedAt = evt.position;
                grab = tile.WorldToLocal(evt.position);
                pointer = evt.pointerId;
                this.CapturePointer(evt.pointerId);
                editor.Select(current);
                evt.StopPropagation();
            });
            tile.RegisterCallback<PointerLeaveEvent>(_ => { if (dragged == null && pressed != id) tile.RemoveFromClassList("qm-tile--pressed"); });
            // A picture dropped on an item becomes its icon.
            tile.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (!MenuArt.Dragged(out _, out _)) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                tile.AddToClassList("qm-tile--file");
                evt.StopPropagation();
            });
            tile.RegisterCallback<DragLeaveEvent>(_ => tile.RemoveFromClassList("qm-tile--file"));
            tile.RegisterCallback<DragExitedEvent>(_ => tile.RemoveFromClassList("qm-tile--file"));
            tile.RegisterCallback<DragPerformEvent>(evt =>
            {
                tile.RemoveFromClassList("qm-tile--file");
                var current = Find(id);
                if (current == null || !MenuArt.Dragged(out var texture, out var file)) return;
                DragAndDrop.AcceptDrag();
                editor.SetIcon(current, texture, file);
                evt.StopPropagation();
            });
        }

        private static string Describe(MenuNode node)
        {
            switch (node.Type)
            {
                case ControlType.SubMenu: return node.Origin?.Kind == MenuOriginKind.Next ? "More items. Click to open, drop an item here to move it to the next page." : "Click to open. Drop an item here to move it in. Double-click to rename.";
                case ControlType.Toggle: return "Toggle. Drag to move it, hold it on another item to make a folder, double-click to rename, drop a picture on it to change its icon.";
                case ControlType.Button: return "Button. Drag to move it, hold it on another item to make a folder, double-click to rename, drop a picture on it to change its icon.";
                default: return "Drag to move it, hold it on another item to make a folder, double-click to rename, drop a picture on it to change its icon.";
            }
        }

        // ---- Pointer: click, drag, drop -----------------------------------------------------------------------------------

        private void OnMove(PointerMoveEvent evt)
        {
            if (evt.pointerId != pointer || pressed == null) return;
            if (dragged == null)
            {
                var node = Find(pressed);
                if (node == null || ((Vector2)evt.position - pressedAt).magnitude < DragStart || !editor.CanMove(node)) return;
                BeginDrag(node);
            }
            Track(evt.position);
            evt.StopPropagation();
        }

        private void OnUp(PointerUpEvent evt)
        {
            if (evt.pointerId != pointer) return;
            string id = pressed;
            if (id != null && tiles.TryGetValue(id, out var tile)) tile.RemoveFromClassList("qm-tile--pressed");
            if (dragged != null)
            {
                // What the drop does is decided first; the drag then ends with every tile back in its place, and only then
                // does the menu change (or not: a drop in place, outside the menu or nowhere valid changes nothing).
                var drop = Decide();
                Release();
                Restore();
                drop?.Invoke();
            }
            else
            {
                var node = Find(id);
                bool open = node != null && node.IsFolder && node.Page != null && tiles.TryGetValue(id, out var current) && current.worldBound.Contains(evt.position);
                Release();
                if (open) editor.Open(node);
            }
            evt.StopPropagation();
        }

        private void BeginDrag(MenuNode node)
        {
            dragged = node.Id;
            draggedNode = node;
            if (tiles.TryGetValue(node.Id, out var tile)) tile.RemoveFromClassList("qm-tile--pressed");
            ghost = Tile(node, wide: false, ghostTile: true);
            ghost.AddToClassList("qm-ghost");
            ghost.pickingMode = PickingMode.Ignore;
            ghost.Query<VisualElement>().ForEach(e => e.pickingMode = PickingMode.Ignore);
            Add(ghost);
            editor.Hint(DragHint);
            Relayout();
        }

        private void Track(Vector2 world)
        {
            var local = this.WorldToLocal(world);
            ghost.style.left = local.x - grab.x;
            ghost.style.top = local.y - grab.y;
            var inGrid = grid.WorldToLocal(world);
            // Over back, a breadcrumb, an item's middle (a folder takes it in, another item makes a folder with it), a place
            // between items, or outside the menu (the item goes back).
            bool onBack = data != null && !data.Root && back.worldBound.Contains(world);
            string onCrumb = onBack ? null : editor.CrumbAt(world);
            string onFolder = null, onItem = null;
            if (!onBack && onCrumb == null)
                foreach (var pair in tiles)
                {
                    var node = Find(pair.Key);
                    if (pair.Key == dragged || node == null) continue;
                    var r = pair.Value.worldBound;
                    var middle = new Rect(r.x + r.width * .18f, r.y + r.height * .14f, r.width * .64f, r.height * .72f);
                    if (!middle.Contains(world)) continue;
                    if (node.IsFolder && node.Page != null) onFolder = pair.Key;
                    else if (editor.CanFold(node, draggedNode)) onItem = pair.Key;
                    break;
                }
            outside = !onBack && onCrumb == null && !worldBound.Contains(world);
            SetBack(onBack);
            crumb = onCrumb;
            if (onFolder != into)
            {
                if (into != null && tiles.TryGetValue(into, out var old)) old.RemoveFromClassList("qm-tile--into");
                into = onFolder;
                if (into != null && tiles.TryGetValue(into, out var now)) now.AddToClassList("qm-tile--into");
            }
            SetFoldTarget(onItem);
            // Hovering a folder (or back) opens it, so the item can be dropped deeper.
            string target = onBack ? BackTarget : into;
            if (target != springTarget)
            {
                spring?.Pause();
                springTarget = target;
                if (target != null) spring = schedule.Execute(Spring).StartingIn((long)SpringDelay);
            }
            if (outside)
            {
                // Released out here, the item goes back where it was.
                if (insertion != -1) { insertion = -1; Relayout(); }
                return;
            }
            if (onBack || onCrumb != null || into != null || onItem != null) return;
            int count = data.Page.Items.Count(n => n.Id != dragged);
            int best = 0; float distance = float.MaxValue;
            for (int i = 0; i <= count; i++)
            {
                if (data.Pages && i >= MenuBuild.MaxControls) break;
                var center = Slot(i) + new Vector2(TileWidth, TileHeight) * .5f;
                float d = (center - inGrid).sqrMagnitude;
                if (d < distance) { distance = d; best = i; }
            }
            if (best != insertion) { insertion = best; Relayout(); }
        }

        private void Spring()
        {
            if (dragged == null) return;
            string target = springTarget;
            springTarget = null;
            if (target == BackTarget) { SetBack(false); editor.Back(); }
            else if (Find(target) is MenuNode folder && folder.Page != null) { into = null; editor.Open(folder); }
            insertion = -1;
        }

        private void SetBack(bool on)
        {
            if (overBack == on) return;
            overBack = on;
            back.EnableInClassList("qm-back--drop", on);
        }

        // Held a moment on an item's middle, the dragged item makes a folder with it (iOS's "forming a folder").
        private void SetFoldTarget(string id)
        {
            if (id == fold) return;
            foldTimer?.Pause(); foldTimer = null;
            if (folding != null) ShowFold(null);
            fold = id;
            if (id != null) foldTimer = schedule.Execute(() => { foldTimer = null; if (dragged != null && fold == id) ShowFold(id); }).StartingIn((long)FoldDelay);
        }

        private void ShowFold(string id)
        {
            if (folding != null && tiles.TryGetValue(folding, out var old)) old.RemoveFromClassList("qm-tile--fold");
            folding = id;
            ghost?.EnableInClassList("qm-ghost--fold", id != null);
            if (id != null && tiles.TryGetValue(id, out var tile))
            {
                if (foldBackdrop == null)
                {
                    foldBackdrop = new VisualElement { pickingMode = PickingMode.Ignore };
                    foldBackdrop.AddToClassList("qm-fold");
                    grid.Insert(0, foldBackdrop);
                }
                foldBackdrop.style.left = tile.style.left.value.value - 9;
                foldBackdrop.style.top = tile.style.top.value.value - 9;
                foldBackdrop.style.display = DisplayStyle.Flex;
                tile.AddToClassList("qm-tile--fold");
                editor.Hint("Release to make a folder of the two items.");
            }
            else
            {
                if (foldBackdrop != null) foldBackdrop.style.display = DisplayStyle.None;
                if (dragged != null) editor.Hint(DragHint);
            }
        }

        // The other items close up around where the dragged one would land.
        private void Relayout()
        {
            if (data?.Page == null) return;
            var others = data.Page.Items.Where(n => n.Id != dragged).ToList();
            int at = insertion < 0 ? IndexOf(dragged) : insertion;
            if (at < 0) at = others.Count;
            at = Mathf.Clamp(at, 0, others.Count);
            for (int i = 0; i < others.Count; i++)
            {
                if (!tiles.TryGetValue(others[i].Id, out var tile)) continue;
                var slot = Slot(i < at ? i : i + 1);
                tile.style.left = slot.x; tile.style.top = slot.y;
            }
            if (dragged != null && tiles.TryGetValue(dragged, out var own)) own.AddToClassList("qm-tile--lifted");
            if (gap == null)
            {
                gap = new VisualElement { pickingMode = PickingMode.Ignore };
                gap.AddToClassList("qm-gap");
                grid.Insert(0, gap);
            }
            var g = Slot(at);
            gap.style.left = g.x; gap.style.top = g.y;
            gap.style.display = into != null ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // What releasing the drag does now, decided before the drag state is cleared; null for nothing.
        private Action Decide()
        {
            var node = Find(dragged) ?? draggedNode;
            if (node == null) return null;
            if (folding != null && Find(folding) is MenuNode target)
                return () =>
                {
                    string made = editor.CreateFolder(new List<MenuNode> { target, node });
                    if (made != null) BeginRename(Find(made));
                };
            if (overBack) return () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Parent });
            if (crumb != null) { string level = crumb; return () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Folder, FolderId = level }); }
            if (into != null && Find(into) is MenuNode folder) return () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Into, Folder = folder });
            if (outside || insertion < 0) return null;
            int at = insertion, from = IndexOf(dragged);
            if (from >= 0 && at == from) return null;
            return () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Index, Index = at });
        }

        // Every tile back in its place, visible: the menu as it is.
        private void Restore()
        {
            var items = data?.Page?.Items;
            if (items != null)
                for (int i = 0; i < items.Count; i++)
                {
                    if (!tiles.TryGetValue(items[i].Id, out var tile)) continue;
                    tile.RemoveFromClassList("qm-tile--lifted");
                    tile.RemoveFromClassList("qm-tile--fold");
                    var slot = Slot(i);
                    tile.style.left = slot.x; tile.style.top = slot.y;
                }
            if (gap != null) gap.style.display = DisplayStyle.None;
            if (foldBackdrop != null) foldBackdrop.style.display = DisplayStyle.None;
        }

        private void CancelDrag()
        {
            if (dragged == null && pressed == null) return;
            if (pressed != null && tiles.TryGetValue(pressed, out var tile)) tile.RemoveFromClassList("qm-tile--pressed");
            Release();
            Restore();
        }

        private void Release()
        {
            // State first: releasing the pointer sends PointerCaptureOutEvent, which cancels any drag left.
            int id = pointer;
            pointer = -1;
            spring?.Pause(); spring = null; springTarget = null;
            foldTimer?.Pause(); foldTimer = null; fold = null;
            if (folding != null) ShowFold(null);
            ghost?.RemoveFromHierarchy(); ghost = null;
            if (into != null && tiles.TryGetValue(into, out var tile)) tile.RemoveFromClassList("qm-tile--into");
            into = null; insertion = -1; crumb = null; outside = false;
            SetBack(false);
            dragged = null; draggedNode = null; pressed = null;
            editor.EndCrumbHover();
            editor.Hint(null);
            if (id >= 0 && this.HasPointerCapture(id)) this.ReleasePointer(id);
        }

        // ---- Renaming in place -------------------------------------------------------------------------------------------

        public void BeginRename(MenuNode node)
        {
            EndRename(false);
            if (node == null || !tiles.TryGetValue(node.Id, out var tile)) return;
            rename = new TextField { value = node.Name, isDelayed = false };
            rename.AddToClassList("qm-rename");
            rename.userData = node;
            tile.Add(rename);
            var label = tile.Q<Label>(className: "qm-tile__label");
            if (label != null) label.style.visibility = Visibility.Hidden;
            rename.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { EndRename(true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { EndRename(false); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            rename.RegisterCallback<FocusOutEvent>(_ => EndRename(true));
            rename.schedule.Execute(() => { rename?.Focus(); rename?.SelectAll(); });
        }

        /// <summary>The item being renamed in place, if any (tests).</summary>
        internal string Renaming => (rename?.userData as MenuNode)?.Id;

        private void EndRename(bool commit)
        {
            var field = rename;
            if (field == null) return;
            rename = null;
            var node = field.userData as MenuNode;
            string value = field.value;
            var label = field.parent?.Q<Label>(className: "qm-tile__label");
            if (label != null) label.style.visibility = Visibility.Visible;
            field.RemoveFromHierarchy();
            if (commit && node != null && value != node.Name) editor.Rename(node, value);
        }
    }
}
