// The radial menu below is Gesture Manager's (https://github.com/BlackStartX/VRC-Gesture-Manager, MIT License,
// Copyright © 2019-2023 BlackStartX): its slices, colours, sizes, cursor and icons, adapted to show and edit a menu instead of
// driving parameters. The full licence is in THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>Gesture Manager's colours, including the ones a user picked in its settings.</summary>
    internal static class RadialColors
    {
        public static readonly Color DefaultMain = new Color(0.14f, 0.18f, 0.2f), DefaultBorder = new Color(0.1f, 0.35f, 0.38f), DefaultSelected = new Color(0.07f, 0.55f, 0.58f);
        public static readonly Color CenterSelected = new Color(0.06f, 0.2f, 0.22f), RadialInner = new Color(0.21f, 0.24f, 0.27f), CenterIdle = new Color(0.06f, 0.27f, 0.29f);
        public static readonly Color SubIcon = new Color(0.22f, 0.24f, 0.27f);

        private static Color Pref(string name, Color fallback) => ColorUtility.TryParseHtmlString(EditorPrefs.GetString(name), out var color) ? color : fallback;
        public static Color Main => Pref("GM3 Main Color", DefaultMain);
        public static Color Border => Pref("GM3 Border Color", DefaultBorder);
        public static Color Selected => Pref("GM3 Selected Color", DefaultSelected);
        public static Color Cursor { get { var m = Main; return new Color(m.r, m.g, m.b, m.a - 0.3f); } }
        public static Color CursorBorder { get { var b = Border; return new Color(b.r, b.g, b.b, b.a - 0.5f); } }
    }

    /// <summary>Gesture Manager's circle: a disc (or a slice of it) with a border, coloured from its centre outwards.</summary>
    internal class RadialCircle : VisualElement
    {
        private float progress = 1f, border = 2f;
        private Color borderColor, vertexColor = Color.white, centerColor;

        public float Progress { set { if (Mathf.Abs(progress - value) < 1e-5f) return; progress = value; MarkDirtyRepaint(); } }
        public Color BorderColor { set { if (borderColor == value) return; borderColor = value; MarkDirtyRepaint(); } }
        public Color VertexColor { set { if (vertexColor == value) return; vertexColor = value; MarkDirtyRepaint(); } }
        public Color CenterColor { set { if (centerColor == value) return; centerColor = value; MarkDirtyRepaint(); } }

        public RadialCircle(float size, Color center, Color color, Color borderColor)
        {
            this.centerColor = center; this.vertexColor = color; this.borderColor = borderColor;
            pickingMode = PickingMode.Ignore;
            style.width = style.height = size;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            generateVisualContent += Generate;
        }

        private static Vector3 At(float x, float y, float w, float h) => new Vector3((x / 2 + 0.5f) * w, (y / 2 + 0.5f) * h, Vertex.nearZ);

        private void Generate(MeshGenerationContext context)
        {
            const int n = 100;
            float w = contentRect.width, h = contentRect.height, bw = w - border * 2, bh = h - border * 2;
            if (w <= 0 || h <= 0) return;
            float radial = progress * Mathf.PI * 2;
            var vertices = new Vertex[n * 2 + 1];
            vertices[0] = new Vertex { position = At(0, 0, w, h), tint = centerColor };
            for (int i = 1; i < n + 1; i++)
            {
                float angle = radial * (i - 1) / (n - 1);
                vertices[i] = new Vertex { position = At(Mathf.Sin(angle), -Mathf.Cos(angle), w, h), tint = borderColor };
                vertices[n + i] = new Vertex { position = At(Mathf.Sin(angle), -Mathf.Cos(angle), bw, bh) + new Vector3(border, border), tint = vertexColor };
            }
            var indices = new ushort[n * 6];
            for (int i = 0; i < n - 1; i++)
            {
                int j = i + n;
                indices[i * 3] = 0; indices[i * 3 + 1] = (ushort)(i + 1); indices[i * 3 + 2] = (ushort)(i + 2);
                indices[j * 3] = 0; indices[j * 3 + 1] = (ushort)(j + 1); indices[j * 3 + 2] = (ushort)(j + 2);
            }
            var mesh = context.Allocate(vertices.Length, indices.Length);
            mesh.SetAllVertices(vertices);
            mesh.SetAllIndices(indices);
        }
    }

    /// <summary>
    /// Gesture Manager's radial menu (the Action Menu as it shows it): Back (and Quick Actions on the main menu), then the
    /// items around the circle, each with its icon, type badge and name; the slice under the pointer lights up and the cursor
    /// follows it. Items are picked, opened, renamed (double click), given an icon (drop a picture) and dragged onto another
    /// slice to take its place, into a folder, or onto Back to move up.
    /// </summary>
    internal sealed class RadialMenuView : VisualElement
    {
        public const float Size = 300, MaxSize = Size / 2, InnerSize = Size / 3, MinSize = InnerSize / 2f, Clamp = Size / 3, ClampReset = Size / 2f;
        private const float Box = 340f, DragStart = 5f, SpringDelay = 700f, FoldDelay = 450f, FoldReach = 34f;

        private sealed class Slice
        {
            public MenuNode Node;
            public bool Back, Quick;
            public RadialCircle Circle;
            public VisualElement Data;
            public Label Text;
            public bool Lit;
            /// <summary>Where its icon is, from the menu's centre.</summary>
            public Vector2 Center;
        }

        private readonly IMenuEditor editor;
        private readonly VisualElement radial, sliceHolder, textHolder;
        private readonly RadialCircle cursor;
        private readonly Label hint, title;
        private readonly List<Slice> slices = new List<Slice>();
        private MenuViewData data;
        private int hovered = -1, pointer = -1, pressedIndex = -1;
        private Vector2 pressedAt;
        private MenuNode dragged;
        private VisualElement ghost;
        private int target = -1;
        private bool targetInto;
        private IVisualElementScheduledItem spring, foldTimer;
        private int springSlice = -1, fold = -1, folding = -1;
        private TextField rename;
        private string crumb;
        private VisualElement foldRing;
        // What the last click did when it changed the page: the folder it opened (its slice, among how many), or going back.
        private string opened;
        private int openedSlice = -1, openedCount;
        private bool wentBack;

        public RadialMenuView(IMenuEditor editor)
        {
            this.editor = editor;
            AddToClassList("rm");
            style.width = Box; style.height = Box;
            radial = new VisualElement { pickingMode = PickingMode.Ignore };
            radial.style.position = Position.Absolute; radial.style.left = Box / 2; radial.style.top = Box / 2;
            Add(radial);
            sliceHolder = new VisualElement { pickingMode = PickingMode.Ignore }; sliceHolder.style.position = Position.Absolute; radial.Add(sliceHolder);
            var inner = new RadialCircle(InnerSize, RadialColors.RadialInner, RadialColors.RadialInner, RadialColors.Border);
            inner.style.position = Position.Absolute; inner.style.left = -InnerSize / 2; inner.style.top = -InnerSize / 2;
            radial.Add(inner);
            textHolder = new VisualElement { pickingMode = PickingMode.Ignore }; textHolder.style.position = Position.Absolute; radial.Add(textHolder);
            cursor = new RadialCircle(50, RadialColors.Cursor, RadialColors.Cursor, RadialColors.CursorBorder);
            cursor.style.position = Position.Absolute;
            var middle = Ring(50 / 1.5f, RadialColors.CursorBorder); cursor.Add(middle);
            middle.Add(Ring(50 / 4f, RadialColors.CursorBorder));
            radial.Add(cursor);
            SetCursor(Vector2.zero);
            hint = new Label { pickingMode = PickingMode.Ignore }; hint.AddToClassList("rm-hint"); Add(hint);
            title = new Label { pickingMode = PickingMode.Ignore }; title.AddToClassList("rm-title"); Add(title);
            RegisterCallback<PointerMoveEvent>(OnMove);
            RegisterCallback<PointerDownEvent>(OnDown);
            RegisterCallback<PointerUpEvent>(OnUp);
            RegisterCallback<PointerLeaveEvent>(_ => { if (dragged == null && pointer < 0) { SetCursor(Vector2.zero); Hover(-1); } });
            RegisterCallback<PointerCaptureOutEvent>(_ => Cancel());
            RegisterCallback<DragUpdatedEvent>(OnFileDrag);
            RegisterCallback<DragPerformEvent>(OnFileDrop);
            RegisterCallback<DragLeaveEvent>(_ => Hover(-1));
        }

        private static VisualElement Ring(float size, Color color)
        {
            var ring = new VisualElement { pickingMode = PickingMode.Ignore };
            ring.style.width = ring.style.height = size;
            ring.style.alignItems = Align.Center; ring.style.justifyContent = Justify.Center;
            ring.style.borderTopLeftRadius = ring.style.borderTopRightRadius = ring.style.borderBottomLeftRadius = ring.style.borderBottomRightRadius = size;
            ring.style.borderTopWidth = ring.style.borderBottomWidth = ring.style.borderLeftWidth = ring.style.borderRightWidth = 2;
            ring.style.borderTopColor = ring.style.borderBottomColor = ring.style.borderLeftColor = ring.style.borderRightColor = color;
            return ring;
        }

        public bool Dragging => dragged != null;

        /// <summary>The same page as shown, from a newer build of the menu: the slices keep their items, newly built.</summary>
        public void Rebind(MenuViewData next)
        {
            data = next;
            foreach (var slice in slices)
                if (!slice.Back && !slice.Quick) slice.Node = next.Page?.Items.FirstOrDefault(n => n.Id == slice.Node.Id) ?? slice.Node;
        }

        /// <summary>Marks the selected item without rebuilding the slices.</summary>
        public void SetSelected(string id)
        {
            if (data == null) return;
            data.Selected = id;
            for (int i = 0; i < slices.Count; i++) Paint(i, i == hovered);
        }

        /// <summary>Ticks the select boxes of the ticked items, without rebuilding the slices.</summary>
        public void SetChecked(HashSet<string> ids)
        {
            if (data != null) data.Checked = ids;
            MenuCheck.Restyle(textHolder, ids);
        }

        public void Show(MenuViewData next)
        {
            data = next;
            title.text = data.Root ? "Expressions" : data.Title;
            Build();
        }

        // ---- Slices, laid out like Gesture Manager's -----------------------------------------------------------------------

        private void Build()
        {
            EndRename(false);
            slices.Clear();
            sliceHolder.Clear();
            textHolder.Clear();
            foldRing = null;
            hovered = -1;
            slices.Add(new Slice { Back = true, Node = new MenuNode { Name = "Back", Icon = data.Root ? MenuArt.BackHome : MenuArt.Back, Type = ControlType.Button, Id = "gm:back" } });
            if (data.Root) slices.Add(new Slice { Quick = true, Node = new MenuNode { Name = "Quick Actions", Icon = MenuArt.Gear, Type = ControlType.Button, Id = "gm:quick" } });
            foreach (var node in data.Page?.Items ?? new List<MenuNode>()) slices.Add(new Slice { Node = node });
            int count = slices.Count;
            float rad = Mathf.Rad2Deg * -Mathf.PI / count, pi = Mathf.PI;
            for (int i = 0; i < count; i++)
            {
                var slice = slices[i];
                slice.Circle = new RadialCircle(Size, RadialColors.CenterIdle, RadialColors.Main, RadialColors.Border) { Progress = 1f / count };
                slice.Circle.style.position = Position.Absolute; slice.Circle.style.left = -Size / 2; slice.Circle.style.top = -Size / 2;
                var holder = new VisualElement { pickingMode = PickingMode.Ignore }; holder.style.position = Position.Absolute; holder.Add(slice.Circle);
                holder.style.rotate = new Rotate(rad);
                sliceHolder.Add(holder);
                var line = new VisualElement { pickingMode = PickingMode.Ignore };
                line.style.position = Position.Absolute; line.style.width = MaxSize - 2; line.style.height = 2;
                line.style.backgroundColor = RadialColors.Border;
                line.style.transformOrigin = new TransformOrigin(0, 0);
                line.style.rotate = new Rotate(rad - 90);
                sliceHolder.Add(line);
                slice.Data = Data(slice);
                slice.Center = new Vector2(Mathf.Sin(pi) * Size / 3, Mathf.Cos(pi) * Size / 3);
                slice.Data.style.translate = new Translate(slice.Center.x, slice.Center.y);
                textHolder.Add(slice.Data);
                rad += 360f / count;
                pi -= Mathf.PI * 2 / count;
            }
            for (int i = 0; i < count; i++) Paint(i, false);
            if (dragged != null) hint.text = "";
        }

        // The icon, its type badge and the name, centred where Gesture Manager puts them.
        private VisualElement Data(Slice slice)
        {
            var node = slice.Node;
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.style.position = Position.Absolute; box.style.width = 100; box.style.height = 100; box.style.left = -50; box.style.top = -50;
            box.style.alignItems = Align.Center; box.style.justifyContent = Justify.Center;
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.style.width = 50; icon.style.height = 50;
            icon.style.backgroundImage = node.Icon != null ? node.Icon : MenuArt.Person;
            box.Add(icon);
            var badge = slice.Back || slice.Quick ? null : MenuArt.Badge(node.Type);
            if (badge != null)
            {
                var sub = new VisualElement { pickingMode = PickingMode.Ignore };
                sub.style.position = Position.Absolute; sub.style.top = 43; sub.style.left = 60; sub.style.width = 25; sub.style.height = 25;
                sub.style.borderTopLeftRadius = sub.style.borderTopRightRadius = sub.style.borderBottomLeftRadius = sub.style.borderBottomRightRadius = 15;
                sub.style.backgroundColor = RadialColors.SubIcon;
                sub.style.alignItems = Align.Center; sub.style.justifyContent = Justify.Center;
                var glyph = new VisualElement { pickingMode = PickingMode.Ignore };
                glyph.style.width = 20; glyph.style.height = 20; glyph.style.backgroundImage = badge;
                sub.Add(glyph);
                box.Add(sub);
            }
            slice.Text = new Label(node.Name) { pickingMode = PickingMode.Ignore, enableRichText = true };
            slice.Text.style.color = Color.white; slice.Text.style.fontSize = 12; slice.Text.style.unityTextAlign = TextAnchor.MiddleCenter;
            slice.Text.style.whiteSpace = WhiteSpace.Normal; slice.Text.style.maxWidth = 100; slice.Text.style.marginTop = 0; slice.Text.style.paddingTop = 0;
            box.Add(slice.Text);
            if (!slice.Back && !slice.Quick && data.Changed.Contains(node.Id))
            {
                var dot = new VisualElement { pickingMode = PickingMode.Ignore };
                dot.style.position = Position.Absolute; dot.style.left = 22; dot.style.top = 22; dot.style.width = 7; dot.style.height = 7;
                dot.style.borderTopLeftRadius = dot.style.borderTopRightRadius = dot.style.borderBottomLeftRadius = dot.style.borderBottomRightRadius = 4;
                dot.style.backgroundColor = (Color)new Color32(0, 218, 109, 255);
                box.Add(dot);
            }
            if (slice.Quick) box.style.opacity = 0.55f;
            if (!slice.Back && !slice.Quick && editor.CanCheck(node))
            {
                var check = MenuCheck.Create(editor, node, data.Checked.Contains(node.Id));
                check.AddToClassList("me-check--radial");
                box.Add(check);
            }
            if (!slice.Back && !slice.Quick && editor.CanDelete(node))
            {
                var cross = MenuDelete.Create(editor, node);
                cross.AddToClassList("me-delete--radial");
                box.Add(cross);
            }
            return box;
        }

        private void Paint(int index, bool on)
        {
            if (index < 0 || index >= slices.Count) return;
            var slice = slices[index];
            bool selected = on || (data?.Selected != null && slice.Node.Id == data.Selected);
            slice.Circle.CenterColor = selected ? RadialColors.CenterSelected : RadialColors.CenterIdle;
            slice.Circle.VertexColor = selected ? RadialColors.Selected : RadialColors.Main;
            if (on != slice.Lit) slice.Data.experimental.animation.Scale(on ? 1.1f : 1f, 100);
            slice.Lit = on;
        }

        private void Hover(int index)
        {
            if (index == hovered) return;
            int old = hovered;
            hovered = index;
            Paint(old, false);
            Paint(index, true);
        }

        // ---- Pointer --------------------------------------------------------------------------------------------------------

        private Vector2 Local(Vector2 world) => radial.WorldToLocal(world);

        private static float Angle(Vector2 p) => -Mathf.Atan2(p.x, p.y) * 180f / Mathf.PI + 180f;

        private int Choice(Vector2 p)
        {
            if (p.magnitude < MinSize || p.magnitude > MaxSize || slices.Count == 0) return -1;
            return (int)((Angle(p) + 180f / slices.Count) % 360 / (360f / slices.Count));
        }

        private void SetCursor(Vector2 p)
        {
            p = p.magnitude < ClampReset ? Vector2.ClampMagnitude(p, Clamp) : Vector2.zero;
            cursor.style.left = p.x - 25; cursor.style.top = p.y - 25;
        }

        private void OnDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || dragged != null) return;
            var p = Local(evt.position);
            int index = Choice(p);
            if (index < 0) return;
            var slice = slices[index];
            // After a click that changed the page: the second press of a double click where the folder it opened was (its
            // slice on the page it was on) renames that folder; any other press counts as a first click, landing on an item
            // of the page now shown.
            bool navigated = opened != null || wentBack;
            bool first = evt.clickCount < 2 || navigated;
            string folder = opened;
            bool rename = evt.clickCount >= 2 && folder != null && openedCount > 0 && p.magnitude >= MinSize && p.magnitude <= MaxSize &&
                          (int)((Angle(p) + 180f / openedCount) % 360 / (360f / openedCount)) == openedSlice;
            opened = null; openedSlice = -1; wentBack = false;
            if (rename)
            {
                if (editor.CanBack)
                {
                    editor.Back();
                    if (data?.Page?.Items.FirstOrDefault(n => n.Id == folder) is MenuNode node && editor.CanRename(node)) BeginRename(node);
                }
                evt.StopPropagation();
                return;
            }
            if (!first && !slice.Back && !slice.Quick && editor.CanRename(slice.Node)) { BeginRename(slice.Node); evt.StopPropagation(); return; }
            pressedIndex = index; pressedAt = evt.position; pointer = evt.pointerId;
            this.CapturePointer(evt.pointerId);
            slice.Data.experimental.animation.Scale(0.95f, 60);
            if (!slice.Back && !slice.Quick) editor.Select(slice.Node);
            evt.StopPropagation();
        }

        private void OnMove(PointerMoveEvent evt)
        {
            var p = Local(evt.position);
            SetCursor(p);
            if (pointer >= 0 && evt.pointerId == pointer && pressedIndex >= 0 && dragged == null)
            {
                var slice = slices[pressedIndex];
                if (!slice.Back && !slice.Quick && ((Vector2)evt.position - pressedAt).magnitude >= DragStart && editor.CanMove(slice.Node)) BeginDrag(slice.Node);
            }
            if (dragged != null) { Track(evt.position, p); return; }
            if (pointer < 0) Hover(Choice(p));
        }

        private void OnUp(PointerUpEvent evt)
        {
            if (evt.pointerId != pointer) return;
            int index = pressedIndex;
            if (dragged != null)
            {
                // Decided first; the drag ends with every slice as it was, then the menu changes (or not).
                var drop = Decide();
                Release();
                drop?.Invoke();
            }
            else
            {
                Action click = null;
                if (index >= 0 && Choice(Local(evt.position)) == index)
                {
                    var slice = slices[index];
                    slice.Data.experimental.animation.Scale(1.1f, 60);
                    if (slice.Back) { if (editor.CanBack) { wentBack = true; click = editor.Back; } }
                    else if (!slice.Quick && slice.Node.IsFolder && slice.Node.Page != null)
                    {
                        var folder = slice.Node;
                        opened = folder.Id; openedSlice = index; openedCount = slices.Count;
                        click = () => editor.Open(folder);
                    }
                }
                Release();
                click?.Invoke();
            }
            evt.StopPropagation();
        }

        private void BeginDrag(MenuNode node)
        {
            dragged = node;
            ghost = new VisualElement { pickingMode = PickingMode.Ignore };
            ghost.style.position = Position.Absolute; ghost.style.width = 50; ghost.style.height = 50; ghost.style.opacity = 0.9f;
            ghost.style.backgroundImage = node.Icon != null ? node.Icon : MenuArt.Person;
            Add(ghost);
            int own = slices.FindIndex(s => s.Node.Id == node.Id);
            if (own >= 0) slices[own].Data.style.opacity = 0.25f;
            editor.Hint(DragHint);
        }

        private const string DragHint = "Drop on another item to take its place, on a folder's middle to put it inside, hold it on an item's icon to make a folder, on Back to move it up. Esc cancels.";

        private void Track(Vector2 world, Vector2 p)
        {
            var local = this.WorldToLocal(world);
            ghost.style.left = local.x - 25; ghost.style.top = local.y - 25;
            crumb = editor.CrumbAt(world);
            int index = crumb != null ? -1 : Choice(p);
            bool intoFolder = false;
            int onIcon = -1;
            if (index >= 0)
            {
                var slice = slices[index];
                if (slice.Quick || slice.Node.Id == dragged.Id) index = -1;
                else if (!slice.Back && slice.Node.IsFolder && slice.Node.Page != null && p.magnitude < MaxSize * 0.82f) intoFolder = true;
                // On another item's icon: held there, the two make a folder.
                else if (!slice.Back && (p - slice.Center).magnitude < FoldReach && editor.CanFold(slice.Node, dragged)) onIcon = index;
            }
            target = index; targetInto = intoFolder;
            Hover(index);
            SetFoldTarget(onIcon);
            hint.text = index < 0 ? "" : folding == index ? "Make a folder" : slices[index].Back ? (editor.CanBack ? "Move up" : "") : intoFolder ? "Into " + Plain(slices[index].Node.Name) : onIcon >= 0 ? "Hold to make a folder" : "Put here";
            // Hovering Back or a folder opens it, so the item can go deeper.
            int springOn = index >= 0 && (slices[index].Back || intoFolder) ? index : -1;
            if (springOn != springSlice)
            {
                spring?.Pause();
                springSlice = springOn;
                if (springOn >= 0) spring = schedule.Execute(Spring).StartingIn((long)SpringDelay);
            }
        }

        private static string Plain(string name) => System.Text.RegularExpressions.Regex.Replace(name ?? "", "<.*?>", "").Replace("\n", " ");

        private void Spring()
        {
            if (dragged == null || springSlice < 0 || springSlice >= slices.Count) return;
            var slice = slices[springSlice];
            springSlice = -1; target = -1;
            if (slice.Back) { if (editor.CanBack) editor.Back(); }
            else if (slice.Node.IsFolder && slice.Node.Page != null) editor.Open(slice.Node);
        }

        // Held a moment on another item's icon, the dragged item makes a folder with it (iOS's "forming a folder").
        private void SetFoldTarget(int index)
        {
            if (index == fold) return;
            foldTimer?.Pause(); foldTimer = null;
            if (folding >= 0) ShowFold(-1);
            fold = index;
            if (index >= 0) foldTimer = schedule.Execute(() => { foldTimer = null; if (dragged != null && fold == index) ShowFold(index); }).StartingIn((long)FoldDelay);
        }

        private void ShowFold(int index)
        {
            folding = index;
            if (index >= 0 && index < slices.Count)
            {
                if (foldRing == null)
                {
                    foldRing = new VisualElement { pickingMode = PickingMode.Ignore };
                    foldRing.AddToClassList("rm-fold");
                    textHolder.Insert(0, foldRing);
                }
                var center = slices[index].Center;
                foldRing.style.left = center.x - 38; foldRing.style.top = center.y - 42;
                foldRing.style.display = DisplayStyle.Flex;
                slices[index].Data.experimental.animation.Scale(1.18f, 120);
                if (ghost != null) ghost.style.scale = new Scale(new Vector3(0.8f, 0.8f, 1));
                hint.text = "Make a folder";
                editor.Hint("Release to make a folder of the two items.");
            }
            else
            {
                if (foldRing != null) foldRing.style.display = DisplayStyle.None;
                foreach (var slice in slices) if (!slice.Lit) slice.Data.experimental.animation.Scale(1f, 80);
                if (ghost != null) ghost.style.scale = new Scale(Vector3.one);
                if (dragged != null) editor.Hint(DragHint);
            }
        }

        // What releasing the drag does now, decided before the drag state is cleared; null for nothing (dropped on itself,
        // outside the menu, or anywhere nothing can go).
        private Action Decide()
        {
            var node = dragged;
            if (node == null) return null;
            if (folding >= 0 && folding < slices.Count)
            {
                var item = slices[folding].Node;
                return () =>
                {
                    string made = editor.CreateFolder(new List<MenuNode> { item, node });
                    if (made != null && data?.Page?.Items.FirstOrDefault(n => n.Id == made) is MenuNode folder) BeginRename(folder);
                };
            }
            if (crumb != null) { string level = crumb; return () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Folder, FolderId = level }); }
            if (target < 0) return null;
            var slice = slices[target];
            if (slice.Back) return editor.CanBack ? () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Parent }) : (Action)null;
            if (targetInto) { var folder = slice.Node; return () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Into, Folder = folder }); }
            // Takes the place of the item it is dropped on.
            int to = data.Page.Items.FindIndex(n => n.Id == slice.Node.Id);
            if (to < 0) return null;
            return () => editor.Move(node, new MenuDrop { Kind = MenuDropKind.Index, Index = to });
        }

        private void Cancel()
        {
            if (dragged == null && pressedIndex < 0) return;
            Release();
        }

        private void Release()
        {
            int id = pointer;
            pointer = -1; pressedIndex = -1;
            spring?.Pause(); spring = null; springSlice = -1;
            foldTimer?.Pause(); foldTimer = null; fold = -1;
            if (folding >= 0) ShowFold(-1);
            ghost?.RemoveFromHierarchy(); ghost = null;
            // Every slice as it was: the dragged one shows again.
            if (dragged != null)
                foreach (var slice in slices)
                    if (slice.Node.Id == dragged.Id) slice.Data.style.opacity = 1f;
            dragged = null; target = -1; targetInto = false; crumb = null;
            Hover(-1);
            hint.text = "";
            editor.EndCrumbHover();
            editor.Hint(null);
            if (id >= 0 && this.HasPointerCapture(id)) this.ReleasePointer(id);
        }

        // ---- Pictures dropped from the project or the computer -------------------------------------------------------------

        private void OnFileDrag(DragUpdatedEvent evt)
        {
            if (!MenuArt.CanDrop()) return;
            int index = Choice(Local(evt.mousePosition));
            bool ok = index >= 0 && !slices[index].Back && !slices[index].Quick;
            DragAndDrop.visualMode = ok ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            Hover(ok ? index : -1);
            hint.text = ok ? "New icon" : "";
            evt.StopPropagation();
        }

        private void OnFileDrop(DragPerformEvent evt)
        {
            hint.text = "";
            if (!MenuArt.Dragged(out var texture, out var file)) return;
            int index = Choice(Local(evt.mousePosition));
            if (index < 0 || slices[index].Back || slices[index].Quick) return;
            DragAndDrop.AcceptDrag();
            editor.SetIcon(slices[index].Node, texture, file);
            evt.StopPropagation();
        }

        // ---- Renaming in place -----------------------------------------------------------------------------------------------

        /// <summary>Where an item's icon is, in world space (tests); null when it isn't shown.</summary>
        internal Vector2? IconOf(string id)
        {
            var slice = slices.FirstOrDefault(s => s.Node.Id == id);
            return slice == null ? (Vector2?)null : radial.LocalToWorld(slice.Center);
        }

        /// <summary>An item's select box (tests).</summary>
        internal VisualElement CheckOf(string id) => slices.FirstOrDefault(s => s.Node.Id == id)?.Data.Q<VisualElement>(className: MenuCheck.Class);

        /// <summary>Whether an item shows faded, as while it is dragged (tests).</summary>
        internal bool Faded(string id) => slices.Any(s => s.Node.Id == id && s.Data.resolvedStyle.opacity < 0.99f);

        /// <summary>The item being renamed in place, if any (tests).</summary>
        internal string Renaming => (rename?.userData as MenuNode)?.Id;

        public void BeginRename(MenuNode node)
        {
            EndRename(false);
            if (node == null) return;
            int index = slices.FindIndex(s => s.Node.Id == node.Id);
            if (index < 0) return;
            var slice = slices[index];
            rename = new TextField { value = node.Name };
            rename.AddToClassList("rm-rename");
            rename.userData = node;
            var at = this.WorldToLocal(slice.Text.worldBound.center);
            rename.style.left = at.x - 60; rename.style.top = at.y - 13;
            Add(rename);
            slice.Text.style.visibility = Visibility.Hidden;
            rename.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { EndRename(true); evt.StopPropagation(); }
                else if (evt.keyCode == KeyCode.Escape) { EndRename(false); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            rename.RegisterCallback<FocusOutEvent>(_ => EndRename(true));
            rename.schedule.Execute(() => { rename?.Focus(); rename?.SelectAll(); });
        }

        private void EndRename(bool commit)
        {
            var field = rename;
            if (field == null) return;
            rename = null;
            var node = field.userData as MenuNode;
            field.RemoveFromHierarchy();
            foreach (var slice in slices) if (node != null && slice.Node.Id == node.Id && slice.Text != null) slice.Text.style.visibility = Visibility.Visible;
            if (commit && node != null && field.value != node.Name) editor.Rename(node, field.value);
        }
    }
}
