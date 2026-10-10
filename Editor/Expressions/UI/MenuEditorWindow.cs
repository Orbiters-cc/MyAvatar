using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// The avatar's VRChat menu, shown as VRChat will show it once built (Modular Avatar and VRCFury included), in the Quick
    /// Menu and the Action Menu side by side, and edited in place in either. Items can be left out of the upload (one by one
    /// with their red cross, or the whole default menu): they wait under the Action Menu, a click puts them back. Every
    /// change is a Unity Undo step; nothing is written until Save, which is one Undo step too and then saves the avatar's
    /// scene and assets.
    /// </summary>
    internal sealed class MenuEditorWindow : EditorWindow, IMenuEditor
    {
        private const string StyleSheet = "Packages/orbiters.myavatar/Editor/Expressions/UI/menu-editor.uss";

        [SerializeField] private GameObject root;
        [SerializeField] private MyAvatar owner;
        [SerializeField] private MenuEditState state;
        [SerializeField] private List<string> path = new List<string>();
        [SerializeField] private string selected;
        [SerializeField] private List<string> ticked = new List<string>();
        [SerializeField] private bool opened;

        // Built menu (before edits), the edited one and its pages; rebuilt when the avatar or the edits change.
        [NonSerialized] private MenuBuildResult built;
        [NonSerialized] private MenuDraft draft;
        // The edited menu without what the upload leaves out, and its pages.
        [NonSerialized] private MenuPage visible;
        [NonSerialized] private MenuPage paged;
        // What the avatar's upload leaves out now (saved before), by id in the built menu.
        [NonSerialized] private List<string> savedRemovals = new List<string>();
        [NonSerialized] private bool savedDefault;
        // The item being put back whose place is full, and the suggestions shown for it.
        [NonSerialized] private string restoring;
        [NonSerialized] private bool dirty = true;
        [NonSerialized] private int shownRevision = -1;
        [NonSerialized] private string shownSignature, shownSelected;
        [NonSerialized] private IVisualElementScheduledItem pending;
        [NonSerialized] private string status;
        [NonSerialized] private bool statusWarning, saving;

        private VisualElement stage, quickHolder, radialHolder, crumbs, side, history, deleted, defaultMenu;
        private QuickMenuView quick;
        private RadialMenuView radial;
        private Label subtitle, hint, statusText;
        private VisualElement statusDot, undoButton, redoButton, notice;
        private Label noticeText;
        private Button save, cancel;
        private readonly List<(VisualElement Element, int Level)> crumbElements = new List<(VisualElement, int)>();

        /// <summary>
        /// Opens the editor on <paramref name="avatar"/> (the one already open for it, if any); <paramref name="myAvatar"/>
        /// adds My Avatar's files to the checkpoint Save records.
        /// </summary>
        public static MenuEditorWindow Open(GameObject avatar, MyAvatar myAvatar = null)
        {
            // Only a window opened here is reused: copies made off-screen to capture or test it are not the user's.
            var window = Resources.FindObjectsOfTypeAll<MenuEditorWindow>().FirstOrDefault(w => w.root == avatar && w.opened);
            if (window == null)
            {
                window = CreateInstance<MenuEditorWindow>();
                window.root = avatar;
                window.opened = true;
                window.minSize = new Vector2(820, 600);
                window.position = new Rect(120, 80, DefaultSize.x, DefaultSize.y);
            }
            if (myAvatar != null) window.owner = myAvatar;
            window.titleContent = new GUIContent("Edit menu");
            window.Show();
            window.Focus();
            return window;
        }

        /// <summary>The window open on <paramref name="avatar"/> with changes not saved yet, if any.</summary>
        public static int PendingChanges(GameObject avatar)
        {
            var window = Resources.FindObjectsOfTypeAll<MenuEditorWindow>().FirstOrDefault(w => w.root == avatar);
            return window != null && window.state != null ? window.state.edits.Count : 0;
        }

        public static event Action Changed;

        /// <summary>What the avatar's upload leaves out changed outside the editor (My Avatar's card): the open editor shows it.</summary>
        public static void Refreshed(GameObject avatar)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<MenuEditorWindow>().Where(w => w.root == avatar)) window.MarkDirty();
        }

        /// <summary>Room for both menus and the side panel.</summary>
        public static readonly Vector2 DefaultSize = new Vector2(1220, 860);

        private void OnEnable()
        {
            if (state == null) state = MenuEditState.Create();
            saveChangesMessage = "The avatar's menu has changes that are not saved. Save them?";
            Undo.undoRedoPerformed += OnUndo;
            EditorApplication.hierarchyChanged += MarkDirty;
            EditorApplication.projectChanged += MarkDirty;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndo;
            EditorApplication.hierarchyChanged -= MarkDirty;
            EditorApplication.projectChanged -= MarkDirty;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
        }

        private void OnDestroy()
        {
            if (state != null) Undo.ClearUndo(state);
            Changed?.Invoke();
        }

        public override void SaveChanges()
        {
            Save();
            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            state?.Clear("Discard menu edits");
            base.DiscardChanges();
        }

        private void OnObjectChanges(ref ObjectChangeEventStream stream) => MarkDirty();

        private void OnUndo() { dirty = true; Schedule(); }

        private void MarkDirty() { dirty = true; Schedule(); }

        private void Schedule()
        {
            if (rootVisualElement == null || pending != null) return;
            pending = rootVisualElement.schedule.Execute(() => { pending = null; Refresh(); });
        }

        // ---- Layout ---------------------------------------------------------------------------------------------------------

        private void CreateGUI()
        {
            var ui = rootVisualElement;
            foreach (var sheetPath in new[] { "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss", "Packages/orbiters.myavatar/Editor/UI/myavatar.uss", StyleSheet })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(sheetPath);
                if (sheet) ui.styleSheets.Add(sheet);
            }
            ui.AddToClassList("me-window");
            ui.focusable = true;
            ui.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);

            var toolbar = new VisualElement(); toolbar.AddToClassList("me-toolbar"); ui.Add(toolbar);
            var texts = new VisualElement(); texts.AddToClassList("me-toolbar__texts"); toolbar.Add(texts);
            var eyebrow = new Label("VRCHAT MENU"); eyebrow.AddToClassList("me-toolbar__eyebrow"); texts.Add(eyebrow);
            subtitle = new Label(); subtitle.AddToClassList("me-toolbar__title"); texts.Add(subtitle);
            defaultMenu = new VisualElement(); defaultMenu.AddToClassList("me-default"); toolbar.Add(defaultMenu);
            defaultMenu.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || defaultMenu.ClassListContains("me-default--off")) return;
                defaultMenu.AddToClassList("me-default--pressed");
                ToggleDefaultMenu();
                evt.StopPropagation();
            });
            defaultMenu.RegisterCallback<PointerUpEvent>(_ => defaultMenu.RemoveFromClassList("me-default--pressed"));
            defaultMenu.RegisterCallback<PointerLeaveEvent>(_ => defaultMenu.RemoveFromClassList("me-default--pressed"));
            history = new VisualElement(); history.AddToClassList("me-history"); toolbar.Add(history);
            undoButton = Round(IconGlyph.Refresh, "Undo (Ctrl+Z)", () => Undo.PerformUndo(), mirror: true); history.Add(undoButton);
            redoButton = Round(IconGlyph.Refresh, "Redo (Ctrl+Y)", () => Undo.PerformRedo(), mirror: false); history.Add(redoButton);

            crumbs = new VisualElement(); crumbs.AddToClassList("me-crumbs"); ui.Add(crumbs);

            var body = new VisualElement(); body.AddToClassList("me-body"); ui.Add(body);
            stage = new VisualElement(); stage.AddToClassList("me-stage"); body.Add(stage);
            var backdrop = new StageBackdrop(); backdrop.AddToClassList("me-stage__backdrop"); stage.Add(backdrop);
            // The Quick Menu and the Action Menu side by side, on the same page: an edit in one shows in the other at once.
            var menus = new VisualElement { pickingMode = PickingMode.Ignore }; menus.AddToClassList("me-menus"); stage.Add(menus);
            quick = new QuickMenuView(this);
            radial = new RadialMenuView(this);
            quickHolder = Column(menus, "QUICK MENU", "The Expressions page of VRChat's Quick Menu.", quick);
            radialHolder = Column(menus, "ACTION MENU", "The radial menu, as Gesture Manager shows it.", radial);
            // What the upload leaves out, under the Action Menu: a click puts an item back.
            deleted = new VisualElement(); deleted.AddToClassList("me-deleted"); radialHolder.parent.Add(deleted);
            deleted.RegisterCallback<GeometryChangedEvent>(evt => { if (Mathf.Abs(evt.oldRect.height - evt.newRect.height) > .5f) Fit(); });
            hint = new Label { pickingMode = PickingMode.Ignore }; hint.AddToClassList("me-stage__hint"); stage.Add(hint);
            notice = new VisualElement(); notice.AddToClassList("me-stage__notice"); stage.Add(notice);
            noticeText = new Label(); noticeText.AddToClassList("me-stage__notice-text"); notice.Add(noticeText);
            notice.style.display = DisplayStyle.None;
            stage.RegisterCallback<GeometryChangedEvent>(_ => Fit());
            stage.RegisterCallback<PointerDownEvent>(evt => { if (evt.target == stage || evt.target is StageBackdrop) Select(null); });

            side = new ScrollView(ScrollViewMode.Vertical); side.AddToClassList("me-side"); body.Add(side);

            var footer = new VisualElement(); footer.AddToClassList("me-footer"); ui.Add(footer);
            var statusRow = new VisualElement(); statusRow.AddToClassList("me-status"); footer.Add(statusRow);
            statusDot = new VisualElement(); statusDot.AddToClassList("me-status__dot"); statusRow.Add(statusDot);
            statusText = new Label(); statusText.AddToClassList("me-status__text"); statusRow.Add(statusText);
            var actions = new VisualElement(); actions.AddToClassList("me-actions"); footer.Add(actions);
            cancel = MyAvatarEditor.Button("Cancel", null); cancel.AddToClassList("me-cancel");
            cancel.tooltip = "Forget the changes and close. Edit > Undo brings them back.";
            ButtonInteraction.RegisterImmediateClick(cancel, CancelEdits);
            actions.Add(cancel);
            save = MyAvatarEditor.Button("Save", null); save.AddToClassList("me-save");
            save.tooltip = "Write the changes into the avatar's menu (one Undo step), then save its scene and assets" +
                           (TextureChanges.Commits ? " and record a “menu change” checkpoint in Unit Git, like My Avatar's texture Save. Nothing is pushed." : ".");
            ButtonInteraction.RegisterImmediateClick(save, () =>
            {
                // The press shows at once; the save runs on the next frame.
                save.text = "Saving…"; save.SetEnabled(false); saving = true;
                rootVisualElement.schedule.Execute(Save);
            });
            actions.Add(save);

            dirty = true;
            Hint(null);
            Refresh();
            Fit();
        }

        private static VisualElement Column(VisualElement parent, string caption, string tip, VisualElement view)
        {
            var column = new VisualElement { pickingMode = PickingMode.Ignore }; column.AddToClassList("me-menu");
            var label = new Label(caption) { tooltip = tip }; label.AddToClassList("me-menu__caption"); column.Add(label);
            var holder = new VisualElement { pickingMode = PickingMode.Ignore }; holder.AddToClassList("me-menu__holder"); column.Add(holder);
            // The menu keeps its own size inside the scaled holder (it is drawn scaled, not laid out smaller).
            view.style.flexShrink = 0;
            holder.Add(view);
            parent.Add(column);
            return holder;
        }

        private VisualElement Round(IconGlyph glyph, string tip, Action action, bool mirror)
        {
            var button = new VisualElement { tooltip = tip, focusable = false }; button.AddToClassList("me-round");
            var icon = new VectorIcon(glyph); icon.AddToClassList("me-round__glyph");
            if (mirror) icon.style.scale = new Scale(new Vector3(-1, 1, 1));
            button.Add(icon);
            button.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || button.ClassListContains("me-round--off")) return;
                button.AddToClassList("me-round--pressed");
                action();
                evt.StopPropagation();
            });
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("me-round--pressed"));
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("me-round--pressed"));
            return button;
        }

        private void OnKey(KeyDownEvent evt)
        {
            if (evt.target is TextField || (evt.target as VisualElement)?.GetFirstAncestorOfType<TextField>() != null) return;
            bool ctrl = evt.actionKey || evt.ctrlKey;
            if (ctrl && evt.keyCode == KeyCode.Z && evt.shiftKey) { Undo.PerformRedo(); evt.StopPropagation(); evt.PreventDefault(); }
            else if (ctrl && evt.keyCode == KeyCode.Z) { Undo.PerformUndo(); evt.StopPropagation(); evt.PreventDefault(); }
            else if (ctrl && evt.keyCode == KeyCode.Y) { Undo.PerformRedo(); evt.StopPropagation(); evt.PreventDefault(); }
            else if (evt.keyCode == KeyCode.F2 && Node(selected) is MenuNode node && CanRename(node)) { BeginRename(node); evt.StopPropagation(); }
            else if (evt.keyCode == KeyCode.Backspace && CanBack && !Dragging) { Back(); evt.StopPropagation(); }
        }

        private bool Dragging => (quick != null && quick.Dragging) || (radial != null && radial.Dragging);

        // For tests: the two views, the changes not saved yet, the ticked items.
        internal QuickMenuView Quick => quick;
        internal RadialMenuView Radial => radial;
        internal MenuEditState State => state;
        internal IReadOnlyList<string> Ticked => ticked;
        internal IReadOnlyList<string> Path => path;
        internal string SideTitle => side?.Q<Label>(className: "me-card__title")?.text;
        internal Button SideFolderButton => side?.Q<Button>(className: "me-make-folder");

        /// <summary>Forgets the changes and closes, without asking (tests).</summary>
        internal void CloseWithoutSaving() => CancelEdits();

        // Both menus are scaled to the stage (their text and frames stay sharp): the Quick Menu to its height (at most about
        // half the width), the radial to what is left beside it.
        private void Fit()
        {
            if (stage == null || quickHolder == null) return;
            var area = stage.contentRect;
            if (area.width <= 0 || area.height <= 0) return;
            // Room for the captions, and above and below the menus for the hint (two lines in a narrow window). What the
            // upload leaves out sits under the Action Menu: only the Action Menu makes room for it.
            const float caption = 26, margin = 84, gaps = 48;
            float height = area.height - caption - margin;
            float left = deleted != null && deleted.style.display.value == DisplayStyle.Flex ? Mathf.Max(DeletedHeight, deleted.layout.height + 12) : 0;
            float quickScale = Mathf.Clamp(Mathf.Min(height / QuickMenuView.Height, area.width * 0.45f / QuickMenuView.Width), 0.5f, 1.4f);
            float quickWidth = QuickMenuView.Width * quickScale;
            float radialScale = Mathf.Clamp(Mathf.Min((area.width - quickWidth - gaps) / 340f, (height - left) / 340f), 0.5f, 1.5f);
            quick.style.scale = new Scale(new Vector3(quickScale, quickScale, 1));
            quickHolder.style.width = quickWidth;
            quickHolder.style.height = QuickMenuView.Height * quickScale;
            radial.style.scale = new Scale(new Vector3(radialScale, radialScale, 1));
            radial.style.transformOrigin = new TransformOrigin(0, 0);
            radialHolder.style.width = 340 * radialScale;
            radialHolder.style.height = 340 * radialScale;
            // The shorter menu is centred against the taller one (as far as the Action Menu's column still fits), and both
            // columns end up as tall, so both captions sit on one line.
            float quickHeight = QuickMenuView.Height * quickScale, radialHeight = 340 * radialScale;
            float quickTop = Mathf.Max(0, (radialHeight - quickHeight) / 2);
            float radialTop = Mathf.Min(Mathf.Max(0, (quickHeight - radialHeight) / 2), Mathf.Max(0, height - radialHeight - left));
            // Balanced with the room the area really takes once laid out (the reserve above is for before).
            float taken = left > 0 && deleted.layout.height > 0 ? deleted.layout.height + 12 : left;
            float quickColumn = quickTop + quickHeight, radialColumn = radialTop + radialHeight + taken, tallest = Mathf.Max(quickColumn, radialColumn);
            quickHolder.style.marginTop = quickTop;
            quickHolder.style.marginBottom = tallest - quickColumn;
            radialHolder.style.marginTop = radialTop;
            radialHolder.style.marginBottom = 0;
            radialHolder.parent.style.paddingBottom = tallest - radialColumn;
        }

        // ---- The menu: built, edited, paged ---------------------------------------------------------------------------------

        private void Refresh()
        {
            if (rootVisualElement == null || subtitle == null) return;
            // A drag in progress is never rebuilt under the pointer; the next change after it catches up.
            if (Dragging) { Schedule(); return; }
            if (root == null)
            {
                subtitle.text = "The avatar is no longer open";
                SetNotice("Open the scene with the avatar, then open the menu editor again from My Avatar.");
                return;
            }
            subtitle.text = root.name;
            if (dirty || built == null)
            {
                built = MenuBuild.Run(MenuInputs.Read(root));
                savedRemovals = MenuSave.SavedRemovals(root, built, out savedDefault);
                dirty = false;
                shownRevision = -1;
            }
            if (shownRevision != state.revision || draft == null)
            {
                draft = MenuDraft.Apply(built, state.edits, savedRemovals, savedDefault);
                visible = draft.Visible();
                // VRCFury's "Next" pages without an icon get the arrow at upload, as Orbiters' items get theirs.
                var nextIcon = draft.NextIcon != null ? draft.NextIcon : Orbiters.Toolkit.Editor.VRChat.OrbitersMenuIcons.Next;
                paged = built.VrcFury ? MenuBuild.Paginate(visible, draft.NextText, nextIcon) : MenuTree.Copy(visible);
                Defaults(paged);
                Offsets(paged);
                shownRevision = state.revision;
            }
            hasUnsavedChanges = state.edits.Count > 0;
            var page = Current(out string title);
            // Ticks of items no longer shown go.
            if (ticked.RemoveAll(id => page == null || page.Items.All(n => n.Id != id)) > 0) shownSignature = null;
            MenuViewData Data() => new MenuViewData { Page = page, Title = title, Root = path.Count == 0, Selected = selected, Changed = draft.Changed, Pages = built.VrcFury, Checked = new HashSet<string>(ticked) };
            var data = Data();
            string signature = Signature(data);
            if (signature != shownSignature)
            {
                shownSignature = signature;
                shownSelected = selected;
                quick.Show(data);
                radial.Show(Data());
                ShowCrumbs();
                ShowDetails();
            }
            else
            {
                // A newer build of the same page: the items are new objects, the tiles stay.
                quick.Rebind(data);
                radial.Rebind(Data());
                if (shownSelected != selected)
                {
                    shownSelected = selected;
                    quick.SetSelected(selected);
                    radial.SetSelected(selected);
                    ShowDetails();
                }
            }
            ShowDeleted();
            ShowDefaultMenu();
            ShowStatus();
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            SetNotice(descriptor == null ? "This object has no VRChat avatar descriptor."
                : built.Root.Items.Count == 0 && state.edits.Count == 0 ? "This avatar has no expressions menu yet: add one to its avatar descriptor, VRCFury toggles or Modular Avatar menu items."
                : !built.VrcFury && Overfull(visible) ? "A menu has more than 8 items: VRChat refuses it unless VRCFury splits it into pages." : null);
            Changed?.Invoke();
        }

        // Orbiters' items without an icon show the one they get at upload.
        private static void Defaults(MenuPage root)
        {
            foreach (var entry in MenuTree.Walk(root))
            {
                var icon = MenuDefaults.Icon(entry.Node, root);
                if (icon != null) entry.Node.Icon = icon;
            }
        }

        private static bool Overfull(MenuPage root) => root.Items.Count > MenuBuild.MaxControls || MenuTree.Walk(root).Any(e => e.Node.IsFolder && e.Node.Page != null && e.Node.Page.Items.Count > MenuBuild.MaxControls);

        // Each shown page knows the folder it belongs to and the place of its first item there (pages after "Next").
        private static void Offsets(MenuPage root)
        {
            var seen = new HashSet<MenuPage>();
            void Walk(MenuPage page, string folder, int offset)
            {
                if (page == null || !seen.Add(page)) return;
                page.Folder = folder; page.Offset = offset;
                foreach (var node in page.Items)
                {
                    if (!node.IsFolder || node.Page == null) continue;
                    if (node.Origin?.Kind == MenuOriginKind.Next) Walk(node.Page, folder, offset + MenuBuild.MaxControls - 1);
                    else Walk(node.Page, node.Id, 0);
                }
            }
            Walk(root, MenuTree.RootId, 0);
        }

        // The page the path leads to; a path an edit made stale stops where it still leads.
        private MenuPage Current(out string title)
        {
            title = "Expressions";
            var page = paged;
            for (int i = 0; i < path.Count; i++)
            {
                var node = page.Items.FirstOrDefault(n => n.Id == path[i] && n.IsFolder && n.Page != null);
                if (node == null) { path.RemoveRange(i, path.Count - i); break; }
                page = node.Page;
                title = node.Name;
            }
            return page;
        }

        private List<(MenuNode Node, MenuPage Page)> Trail()
        {
            var trail = new List<(MenuNode, MenuPage)>();
            var page = paged;
            foreach (var id in path)
            {
                var node = page.Items.FirstOrDefault(n => n.Id == id && n.IsFolder && n.Page != null);
                if (node == null) break;
                trail.Add((node, node.Page));
                page = node.Page;
            }
            return trail;
        }

        private static string Signature(MenuViewData data)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(data.Title).Append('|').Append(data.Root).Append('|');
            foreach (var n in data.Page?.Items ?? new List<MenuNode>())
                sb.Append(n.Id).Append(':').Append(n.Name).Append(':').Append(n.Type).Append(':').Append(n.Icon != null ? n.Icon.GetInstanceID() : 0).Append(':').Append(data.Changed.Contains(n.Id)).Append(':').Append(n.Page?.Items.Count ?? -1).Append(';');
            return sb.ToString();
        }

        private MenuNode Node(string id)
        {
            if (id == null || paged == null) return null;
            return MenuTree.Walk(paged).Select(e => e.Node).FirstOrDefault(n => n.Id == id);
        }

        // ---- IMenuEditor --------------------------------------------------------------------------------------------------

        // Selecting restyles the item and shows it beside the menu; nothing is rebuilt under the pointer.
        public void Select(MenuNode node)
        {
            string id = node?.Id;
            if (id == selected) return;
            selected = id;
            quick.SetSelected(id);
            radial.SetSelected(id);
            ShowDetails();
            shownSelected = id;
        }

        public void Open(MenuNode folder)
        {
            if (folder?.Page == null) return;
            path.Add(folder.Id);
            selected = null;
            ticked.Clear();
            shownSignature = null;
            Refresh();
        }

        public bool CanBack => path.Count > 0;

        public void Back()
        {
            if (path.Count == 0) return;
            selected = path[path.Count - 1];
            path.RemoveAt(path.Count - 1);
            ticked.Clear();
            shownSignature = null;
            Refresh();
        }

        private void GoTo(int level)
        {
            if (level < 0 || level >= path.Count) return;
            selected = path[level];
            path.RemoveRange(level, path.Count - level);
            ticked.Clear();
            shownSignature = null;
            Refresh();
        }

        private static bool IsNext(MenuNode node) => node?.Origin?.Kind == MenuOriginKind.Next;

        public bool CanRename(MenuNode node) => node != null && node.Id != null && !node.Id.StartsWith("vrc:", StringComparison.Ordinal) && !node.Id.StartsWith("gm:", StringComparison.Ordinal);

        public bool CanMove(MenuNode node)
        {
            if (Movable(node, out string reason)) return true;
            if (reason != null) Hint(reason);
            return false;
        }

        // Why an item can't be moved (null: it can't, with nothing to say).
        private bool Movable(MenuNode node, out string reason)
        {
            reason = null;
            if (!CanRename(node) || IsNext(node)) return false;
            // A folder opening a menu another folder opens too (a loop back up, a shared submenu) moves with the avatar's own
            // menu assets, which keep the sharing. Elsewhere only VRCFury's Move Menu Item can move it, and it copies the menu.
            if (node.Origin?.Kind != MenuOriginKind.AvatarMenu && paged != null && MenuTree.OpenedElsewhere(paged, node))
            {
                reason = "“" + Plain(node.Name) + "” opens the same menu as another folder, and it comes from " + From(node) +
                         ": VRCFury's Move Menu Item would copy that menu instead of moving it, so it stays here.";
                return false;
            }
            return true;
        }

        public bool CanCheck(MenuNode node) => CanRename(node) && !IsNext(node);

        public bool CanFold(MenuNode target, MenuNode dragged) =>
            target != null && dragged != null && target.Id != dragged.Id && !target.IsFolder && Movable(target, out _) && Movable(dragged, out _);

        public void Check(MenuNode node, bool on)
        {
            if (node?.Id == null) return;
            ticked.Remove(node.Id);
            if (on && CanCheck(node)) ticked.Add(node.Id);
            var set = new HashSet<string>(ticked);
            quick.SetChecked(set);
            radial.SetChecked(set);
            ShowDetails();
        }

        /// <summary>
        /// A new folder ("New folder") at the first item's place, holding the items: one Undo step. Its id, or null when the
        /// items can't go in one folder.
        /// </summary>
        public string CreateFolder(IList<MenuNode> items)
        {
            var list = items?.Where(n => n != null && Movable(n, out _)).GroupBy(n => n.Id).Select(g => g.First()).ToList();
            if (list == null || list.Count == 0 || built == null) return null;
            string id = "new:" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var edit = new MenuEdit { kind = MenuEditKind.Folder, id = id, text = "New folder", items = list.Select(n => n.Id).ToList() };
            var trial = MenuDraft.Apply(built, state.edits.Concat(new[] { edit }).ToList());
            if (!MenuTree.Index(trial.Root).ContainsKey(id)) { SetStatus("These items can't go in one folder.", true); return null; }
            state.Add(edit, list.Count == 1 ? "New folder with “" + Plain(list[0].Name) + "”" : "New folder with " + list.Count + " items");
            ticked.Clear();
            selected = id;
            shownSignature = null;
            Refresh();
            return id;
        }

        public void Rename(MenuNode node, string name)
        {
            if (!CanRename(node) || name == node.Name) return;
            if (IsNext(node)) state.Add(new MenuEdit { kind = MenuEditKind.NextText, text = name }, "Rename “Next”");
            else state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = node.Id, text = name }, "Rename “" + Plain(node.Name) + "”");
            selected = node.Id;
            Refresh();
        }

        public void SetIcon(MenuNode node, Texture2D texture, string file)
        {
            if (!CanRename(node)) return;
            var edit = new MenuEdit { kind = IsNext(node) ? MenuEditKind.NextIcon : MenuEditKind.Icon, id = node.Id, icon = texture, file = texture == null ? file : null };
            state.Add(edit, (texture == null && file == null ? "Default icon for “" : "Icon of “") + Plain(node.Name) + "”");
            selected = node.Id;
            Refresh();
        }

        public void Move(MenuNode node, MenuDrop drop)
        {
            if (!CanMove(node) || draft == null) { Refresh(); return; }
            // Places count the items shown; the move is saved among every item, those left out of the upload included.
            var logical = MenuTree.Index(visible);
            if (!logical.TryGetValue(node.Id, out var entry)) { Refresh(); return; }
            string fromFolder = entry.Folder?.Id ?? MenuTree.RootId;
            int fromIndex = entry.Parent.Items.IndexOf(entry.Node);
            string folder; int index;
            var page = Current(out _);
            switch (drop.Kind)
            {
                case MenuDropKind.Into when IsNext(drop.Folder):
                    // The "Next" item: first on the next page (the pages are made again around it).
                    folder = page.Folder; index = drop.Folder.Page.Offset;
                    break;
                case MenuDropKind.Into:
                    folder = drop.Folder.Id;
                    index = MenuTree.PageOf(draft.Root, folder, logical)?.Items.Count ?? 0;
                    break;
                case MenuDropKind.Parent:
                case MenuDropKind.Folder:
                {
                    var trail = Trail();
                    int level = drop.Kind == MenuDropKind.Parent ? trail.Count - 1 : int.Parse(drop.FolderId);
                    var target = level <= 0 ? paged : trail[level - 1].Page;
                    folder = target.Folder;
                    // Back to an earlier page of the same folder: last on that page; another folder: last in it.
                    index = folder == fromFolder ? target.Offset + Mathf.Max(0, target.Items.Count(n => !IsNext(n)) - 1)
                        : MenuTree.PageOf(draft.Root, folder, logical)?.Items.Count ?? 0;
                    break;
                }
                default:
                    // A place among this page's other items; one leaving an earlier page of the folder moves them up.
                    folder = page.Folder;
                    index = page.Offset + drop.Index;
                    if (folder == fromFolder && fromIndex < page.Offset) index--;
                    break;
            }
            var targetPage = MenuTree.PageOf(visible, folder, logical);
            // A folder can go anywhere the menu still reaches without it: inside itself it would vanish from the menu.
            if (folder != fromFolder && (folder == node.Id || (node.IsFolder && targetPage != null && !MenuTree.Reachable(visible, targetPage, entry.Node))))
            {
                Hint("A folder can't go inside itself."); Refresh(); return;
            }
            if (!built.VrcFury && folder != fromFolder && (targetPage?.Items.Count ?? 0) >= MenuBuild.MaxControls)
            {
                SetStatus("That menu is full: VRChat menus hold 8 items, and this avatar has no VRCFury to add pages.", true);
                shownSignature = null; Refresh(); return;
            }
            if (folder == fromFolder && index == fromIndex) { shownSignature = null; Refresh(); return; }
            string where = folder == MenuTree.RootId ? "the main menu" : "“" + Plain(logical.TryGetValue(folder, out var f) ? f.Node.Name : "") + "”";
            state.Add(new MenuEdit { kind = MenuEditKind.Move, id = node.Id, parent = folder, index = Logical(folder, node.Id, Mathf.Max(0, index)) },
                folder == fromFolder ? "Move “" + Plain(node.Name) + "”" : "Move “" + Plain(node.Name) + "” to " + where);
            selected = node.Id;
            shownSignature = null;
            Refresh();
        }

        // A place among a folder's shown items (the moved one aside) as a place among all of them.
        private int Logical(string folder, string moved, int index)
        {
            var all = MenuTree.PageOf(draft.Root, folder)?.Items.Where(n => n.Id != moved).ToList();
            var shown = MenuTree.PageOf(visible, folder)?.Items.Where(n => n.Id != moved).ToList();
            if (all == null || shown == null) return index;
            if (index < shown.Count) return Mathf.Max(0, all.FindIndex(n => n.Id == shown[index].Id));
            return shown.Count == 0 ? all.Count : all.FindIndex(n => n.Id == shown[shown.Count - 1].Id) + 1;
        }

        // ---- Left out of the upload ---------------------------------------------------------------------------------------

        public bool CanDelete(MenuNode node) => CanRename(node) && !IsNext(node) && node.Origin?.Kind != MenuOriginKind.NewFolder;

        /// <summary>Leaves an item (a folder with everything in it) out of the upload, once saved: one Undo step.</summary>
        public void Delete(MenuNode node)
        {
            if (!CanDelete(node) || draft == null) return;
            state.Add(new MenuEdit { kind = MenuEditKind.Delete, id = node.Id }, "Leave out “" + Plain(node.Name) + "”");
            ticked.Remove(node.Id);
            if (selected == node.Id) selected = null;
            restoring = null;
            shownSignature = null;
            Refresh();
        }

        // Puts an item back where it was. A place already holding 8 items goes on over a "Next" page: VRCFury makes it, else
        // a "Next" folder made here takes the place's last item and this one. Failing that, other folders are offered.
        private void Restore(MenuNode node)
        {
            var entry = MenuTree.Index(draft.Root).TryGetValue(node.Id, out var e) ? e : default;
            if (entry.Node == null) return;
            var place = MenuTree.PageOf(visible, entry.Folder?.Id ?? MenuTree.RootId);
            if (place == null || place.Items.Count >= MenuBuild.MaxControls && !built.VrcFury && !BackOnNextPage(node, place))
            {
                restoring = restoring == node.Id ? null : node.Id;
                ShowDeleted();
                return;
            }
            if (place.Items.Count < MenuBuild.MaxControls || built.VrcFury) Back(node, null);
        }

        private bool BackOnNextPage(MenuNode node, MenuPage place)
        {
            var last = place.Items.LastOrDefault(n => Movable(n, out _));
            if (last == null) return false;
            string id = "new:" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var next = new List<MenuEdit>
            {
                new MenuEdit { kind = MenuEditKind.Folder, id = id, text = string.IsNullOrEmpty(draft.NextText) ? "Next" : draft.NextText, items = new List<string> { last.Id, node.Id } },
                new MenuEdit { kind = MenuEditKind.Icon, id = id, icon = Orbiters.Toolkit.Editor.VRChat.OrbitersMenuIcons.Next },
            };
            var trial = MenuDraft.Apply(built, state.edits.Concat(BackEdits(node)).Concat(next).ToList());
            if (!MenuTree.Index(trial.Root).ContainsKey(id)) return false;
            Back(node, null, next);
            return true;
        }

        // The item back in the menu: where it was, or at the end of another folder (one Undo step, with any edits that
        // make room for it).
        private void Back(MenuNode node, string folder, List<MenuEdit> room = null)
        {
            var edits = BackEdits(node);
            if (folder != null)
            {
                int end = MenuTree.PageOf(draft.Root, folder)?.Items.Count(n => n.Id != node.Id) ?? 0;
                edits.Add(new MenuEdit { kind = MenuEditKind.Move, id = node.Id, parent = folder, index = end });
            }
            if (room != null) edits.AddRange(room);
            state.Add(edits, "Put back “" + Plain(node.Name) + "”");
            restoring = null;
            selected = node.Id;
            shownSignature = null;
            Refresh();
        }

        private List<MenuEdit> BackEdits(MenuNode node)
        {
            var edits = new List<MenuEdit>();
            if (draft.DefaultRemoved && MenuUploadBuild.DefaultItems(draft.Root).Any(n => n.Id == node.Id))
            {
                // One item of the default menu: the rest stays out, each saved as its own.
                edits.Add(new MenuEdit { kind = MenuEditKind.DefaultMenu, flag = false });
                edits.AddRange(MenuUploadBuild.DefaultItems(draft.Root).Where(n => n.Id != node.Id && !draft.Deleted.Contains(n.Id)).Select(n => new MenuEdit { kind = MenuEditKind.Delete, id = n.Id }));
            }
            edits.Add(new MenuEdit { kind = MenuEditKind.Restore, id = node.Id });
            return edits;
        }

        private void ToggleDefaultMenu()
        {
            if (draft == null) return;
            bool remove = !draft.DefaultRemoved;
            state.Add(new MenuEdit { kind = MenuEditKind.DefaultMenu, flag = remove }, remove ? "Remove the default menu" : "Put the default menu back");
            restoring = null;
            shownSignature = null;
            Refresh();
        }

        private const float DeletedHeight = 78;

        private void ShowDeleted()
        {
            if (deleted == null || draft == null) return;
            deleted.Clear();
            var defaults = draft.DefaultRemoved ? MenuUploadBuild.DefaultItems(draft.Root) : new List<MenuNode>();
            var defaultIds = new HashSet<string>(defaults.Select(n => n.Id));
            // Items inside a folder left out go with it: the folder stands for them.
            var left = draft.Left().Where(e => !defaultIds.Contains(e.Node.Id)).ToList();
            var inside = new HashSet<string>();
            foreach (var folder in left.Select(e => e.Node).Concat(defaults).Where(n => n.IsFolder && n.Page != null))
                foreach (var w in MenuTree.Walk(folder.Page)) inside.Add(w.Node.Id);
            left = left.Where(e => !inside.Contains(e.Node.Id)).ToList();
            bool any = left.Count > 0 || defaults.Count > 0;
            bool was = deleted.style.display.value == DisplayStyle.Flex;
            deleted.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            // The menus make room for it, or take it back.
            if (any != was) rootVisualElement.schedule.Execute(Fit);
            if (!any) return;
            var caption = new Label("LEFT OUT OF THE UPLOAD") { tooltip = "These items, and what only they switch, are not uploaded. Click one to put it back." };
            caption.AddToClassList("me-deleted__caption"); deleted.Add(caption);
            var row = new VisualElement(); row.AddToClassList("me-deleted__row"); deleted.Add(row);
            if (defaults.Count > 0)
                row.Add(Chip(null, "Default menu · " + defaults.Count + (defaults.Count == 1 ? " item" : " items"),
                    "The avatar's own menu, as its creator made it. Click to put it back.", ToggleDefaultMenu, true));
            foreach (var entry in left)
            {
                var node = entry.Node;
                var chip = Chip(node.Icon != null ? node.Icon : MenuArt.Person, Plain(node.Name), "Left out of the upload" +
                    (entry.Folder != null ? " (was in “" + Plain(entry.Folder.Name) + "”)" : "") + ". Click to put it back.", () => Restore(node), false);
                chip.EnableInClassList("me-chip--open", restoring == node.Id);
                row.Add(chip);
            }
            var pending = restoring != null ? left.FirstOrDefault(e => e.Node.Id == restoring) : default;
            if (pending.Node == null) { restoring = null; return; }
            // Its place is full: other folders with room.
            var panel = new VisualElement(); panel.AddToClassList("me-restore"); deleted.Add(panel);
            string where = pending.Folder != null ? "“" + Plain(pending.Folder.Name) + "”" : "the main menu";
            var text = new Label("“" + Plain(pending.Node.Name) + "” was in " + where + ", which is full now (" + MenuBuild.MaxControls + " items). Put it in:");
            text.AddToClassList("me-restore__text"); panel.Add(text);
            var options = new VisualElement(); options.AddToClassList("me-deleted__row"); panel.Add(options);
            var rooms = new List<(string Id, string Name)>();
            if (visible.Items.Count < MenuBuild.MaxControls) rooms.Add((MenuTree.RootId, "Main menu"));
            foreach (var e in MenuTree.Walk(visible))
                if (e.Node.IsFolder && e.Node.Page != null && e.Node.Page.Items.Count < MenuBuild.MaxControls && e.Node.Id != pending.Node.Id && e.Node.Origin?.Kind != MenuOriginKind.Next &&
                    rooms.All(r => r.Id != e.Node.Id) && !(pending.Node.IsFolder && MenuTree.Contains(pending.Node, e.Node.Page)))
                    rooms.Add((e.Node.Id, Plain(e.Node.Name)));
            foreach (var (id, name) in rooms.Take(6))
                options.Add(Chip(MenuArt.Badge(ControlType.SubMenu), name, "Put “" + Plain(pending.Node.Name) + "” back at the end of " + (id == MenuTree.RootId ? "the main menu." : "“" + name + "”."), () => Back(pending.Node, id), false));
            if (rooms.Count == 0)
            {
                var none = new Label("Every folder is full: make room in one first."); none.AddToClassList("me-restore__text"); options.Add(none);
            }
        }

        // A small tile under the Action Menu: an item left out, a suggested folder.
        private static VisualElement Chip(Texture2D icon, string text, string tip, Action action, bool group)
        {
            var chip = new VisualElement { tooltip = tip }; chip.AddToClassList("me-chip");
            chip.EnableInClassList("me-chip--group", group);
            if (icon != null) { var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore }; image.AddToClassList("me-chip__icon"); chip.Add(image); }
            var label = new Label(text) { pickingMode = PickingMode.Ignore }; label.AddToClassList("me-chip__text"); chip.Add(label);
            chip.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                chip.AddToClassList("me-chip--pressed");
                evt.StopPropagation();
                action();
            });
            chip.RegisterCallback<PointerUpEvent>(_ => chip.RemoveFromClassList("me-chip--pressed"));
            chip.RegisterCallback<PointerLeaveEvent>(_ => chip.RemoveFromClassList("me-chip--pressed"));
            return chip;
        }

        private void ShowDefaultMenu()
        {
            if (defaultMenu == null || draft == null) return;
            defaultMenu.Clear();
            bool removed = draft.DefaultRemoved;
            int count = MenuUploadBuild.DefaultItems(draft.Root).Count;
            var icon = new VectorIcon(removed ? IconGlyph.Check : IconGlyph.Close); icon.AddToClassList("me-default__glyph"); defaultMenu.Add(icon);
            var label = new Label(removed ? "Default menu removed" : "Remove default menu") { pickingMode = PickingMode.Ignore }; label.AddToClassList("me-default__text"); defaultMenu.Add(label);
            defaultMenu.EnableInClassList("me-default--on", removed);
            defaultMenu.EnableInClassList("me-default--off", count == 0 && !removed);
            defaultMenu.tooltip = count == 0 && !removed ? "This avatar's descriptor has no menu of its own."
                : removed ? "The avatar's own menu (as its creator made it) is left out of the upload, with the layers only its items switch: what they don't touch keeps working, and what they could only show (other materials, hidden objects) isn't uploaded. Click to put it back."
                : "Leave the avatar's own menu (as its creator made it) out of the upload, with the layers only its items switch: what they don't touch keeps working (a petting reaction stays, its on/off switch goes), and what they could only show (other materials, hidden objects) isn't uploaded. Takes effect at upload, once saved.";
        }

        public string CrumbAt(Vector2 world)
        {
            string found = null;
            foreach (var (element, level) in crumbElements)
            {
                bool over = level < path.Count && element.worldBound.Contains(world);
                element.EnableInClassList("me-crumb--drop", over);
                if (over) found = level.ToString();
            }
            return found;
        }

        public void EndCrumbHover()
        {
            foreach (var (element, _) in crumbElements) element.RemoveFromClassList("me-crumb--drop");
        }

        public void Hint(string text)
        {
            if (hint != null) hint.text = text ?? DefaultHint;
        }

        private const string DefaultHint = "Click a folder to open it · drag between items to move, onto a folder to put it inside, hold on an item to make a folder · double-click to rename · drop a picture to change an icon";

        private void BeginRename(MenuNode node) => quick.BeginRename(node);

        private static string Plain(string name) => System.Text.RegularExpressions.Regex.Replace(name ?? "", "<.*?>", "").Replace("\n", " ").Trim();

        // ---- Breadcrumb ---------------------------------------------------------------------------------------------------

        private void ShowCrumbs()
        {
            crumbs.Clear();
            crumbElements.Clear();
            var trail = Trail();
            for (int level = 0; level <= trail.Count; level++)
            {
                if (level > 0) { var sep = new Label("›"); sep.AddToClassList("me-crumb__sep"); crumbs.Add(sep); }
                string name = level == 0 ? "Expressions" : Plain(trail[level - 1].Node.Name);
                var crumb = new Label(name); crumb.AddToClassList("me-crumb");
                bool current = level == trail.Count;
                crumb.EnableInClassList("me-crumb--current", current);
                int to = level;
                if (!current)
                {
                    crumb.tooltip = "Go back to " + name + ". Drop an item here to move it there.";
                    crumb.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { GoTo(to); evt.StopPropagation(); } });
                }
                crumbs.Add(crumb);
                crumbElements.Add((crumb, level));
            }
        }

        // ---- Selected item --------------------------------------------------------------------------------------------------

        private void ShowDetails()
        {
            side.Clear();
            var card = new VisualElement(); card.AddToClassList("me-card"); side.Add(card);
            // Ticked items first (one shows like a selected item, a folder without opening it), else the selected item.
            var checkedNodes = ticked.Select(Node).Where(n => n != null).ToList();
            if (checkedNodes.Count > 1) { ShowSelection(card, checkedNodes); return; }
            var node = checkedNodes.Count == 1 ? checkedNodes[0] : Node(selected);
            if (node == null || !CanRename(node))
            {
                var heading = new Label("Edit the menu"); heading.AddToClassList("me-card__title"); card.Add(heading);
                var intro = new Label("This is the menu VRChat will show for " + root.name + Builders(built, ", with everything ", " adds", " add") + ". Changes are kept here until you save them.");
                intro.AddToClassList("me-card__text"); card.Add(intro);
                Tip(card, "Click", "an item to rename it or change its icon; a folder to open it.");
                Tip(card, "Drag", "an item to another place, onto a folder to put it inside, or onto back or a breadcrumb to move it up.");
                Tip(card, "Hold", "a dragged item on another item to make a folder of the two.");
                Tip(card, "Tick", "the box at an item's top left to select it (several, folders too), then make a folder of them.");
                Tip(card, "Drop", "a picture from your computer or the project on an item to make it its icon.");
                Tip(card, "×", "the red cross leaves an item out of the upload; it waits under the Action Menu, a click puts it back.");
                Tip(card, "Ctrl Z", "undoes a change, Ctrl Y redoes it.");
                return;
            }
            bool next = IsNext(node);
            var logical = next ? null : MenuTree.Index(draft.Root).TryGetValue(node.Id, out var e) ? e.Node : null;

            var well = new VisualElement { tooltip = "Drop a picture here, or choose one below." }; well.AddToClassList("me-icon-well");
            var image = new Image { image = node.Icon != null ? node.Icon : MenuArt.Person, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            image.AddToClassList("me-icon-well__image"); well.Add(image);
            well.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (!MenuArt.Dragged(out _, out _)) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                well.AddToClassList("me-icon-well--drop");
            });
            well.RegisterCallback<DragLeaveEvent>(_ => well.RemoveFromClassList("me-icon-well--drop"));
            well.RegisterCallback<DragPerformEvent>(_ =>
            {
                well.RemoveFromClassList("me-icon-well--drop");
                if (!MenuArt.Dragged(out var texture, out var file)) return;
                DragAndDrop.AcceptDrag();
                SetIcon(node, texture, file);
            });
            well.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) { well.AddToClassList("me-icon-well--pressed"); Browse(node); } });
            well.RegisterCallback<PointerUpEvent>(_ => well.RemoveFromClassList("me-icon-well--pressed"));
            well.RegisterCallback<PointerLeaveEvent>(_ => well.RemoveFromClassList("me-icon-well--pressed"));
            card.Add(well);

            var iconActions = new VisualElement(); iconActions.AddToClassList("me-icon-actions"); card.Add(iconActions);
            iconActions.Add(Link("Choose picture…", "A picture from your computer: it is added to the project when you save.", () => Browse(node)));
            if (node.Icon != null) iconActions.Add(Link("Default", "VRChat's default icon.", () => SetIcon(node, null, null)));
            // A texture already in the project, with Unity's own picker.
            var project = new ObjectField { objectType = typeof(Texture2D), allowSceneObjects = false, value = node.Icon != null && EditorUtility.IsPersistent(node.Icon) ? node.Icon : null };
            project.AddToClassList("me-project-icon");
            project.tooltip = "A texture from the project.";
            project.RegisterValueChangedCallback(evt => { if (evt.newValue != evt.previousValue) SetIcon(node, evt.newValue as Texture2D, null); });
            card.Add(project);

            var label = new Label("NAME"); label.AddToClassList("me-field-label"); card.Add(label);
            var name = new TextField { value = node.Name, isDelayed = true }; name.AddToClassList("me-name");
            name.RegisterValueChangedCallback(evt => Rename(node, evt.newValue));
            card.Add(name);

            var facts = new VisualElement(); facts.AddToClassList("me-facts"); card.Add(facts);
            Fact(facts, "Type", next ? "More items (VRCFury adds it when a menu holds more than 8)" : Kind(node, logical));
            if (!string.IsNullOrEmpty(node.Parameter)) Fact(facts, "Parameter", node.Parameter);
            Fact(facts, "From", next ? "VRCFury’s pages: its name and icon are the same on every page" : From(node), muted: true);
            var saves = new Label(next ? "Saved with VRCFury’s Override Menu Settings." : SavedAs(node)); saves.AddToClassList("me-card__text"); saves.AddToClassList("me-saves");
            card.Add(saves);
            if (!next && logical != null) Settings(card, logical);
            var source = SourceObject(node);
            var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; row.style.marginTop = 8; card.Add(row);
            if (source != null) row.Add(Link("Show source", "Select where this item is defined.", () => { Selection.activeObject = source; EditorGUIUtility.PingObject(source); }));
            if (CanDelete(node))
            {
                var leave = Link("Leave out", "Leave this item out of the upload (with what only it switches). It waits under the Action Menu; a click puts it back.", () => Delete(node));
                leave.AddToClassList("me-link--danger");
                row.Add(leave);
            }
            if (!next && Movable(node, out _)) card.Add(FolderButton(new List<MenuNode> { node }));
        }

        // ---- Settings of the selected item -----------------------------------------------------------------------------------

        // Its type, parameter and value, and its parameter's default, saved and synced: editable where they are the avatar's own.
        private void Settings(VisualElement card, MenuNode node)
        {
            var info = MenuSave.Info(root, node, draft);
            bool any = info.Types.Count > 0 || info.Parameters != null || info.ValueEditable || info.DefaultEditable || info.SavedEditable || info.SyncedEditable;
            if (!any && info.Parameter.Length == 0 && info.Why == null) return;
            var label = new Label("SETTINGS"); label.AddToClassList("me-field-label"); label.AddToClassList("me-settings__title"); card.Add(label);
            var box = new VisualElement(); box.AddToClassList("me-settings"); card.Add(box);
            if (info.Types.Count > 1)
            {
                var names = info.Types.Select(t => t == ControlType.RadialPuppet ? "Slider" : t == ControlType.Button ? (info.Toggle ? "Hold" : "Button") : "Toggle").ToList();
                var tips = info.Types.Select(t => t == ControlType.RadialPuppet ? "A radial slider." : t == ControlType.Button ? "On while held." : "On until pressed again.").ToList();
                var types = new SegmentedControl(names.Select((n, i) => new SegmentedControl.Option(n, null, tips[i])), i =>
                {
                    if (info.Types[i] == node.Type) return;
                    state.Add(new MenuEdit { kind = MenuEditKind.Type, id = node.Id, index = (int)info.Types[i] }, "Type of “" + Plain(node.Name) + "”");
                    shownSignature = null; Refresh();
                });
                types.SetIndex(Mathf.Max(0, info.Types.IndexOf(node.Type)));
                types.AddToClassList("me-settings__types");
                Setting(box, "Type", types);
            }
            if (info.Parameters != null)
            {
                var choices = new List<string> { "(none)" };
                choices.AddRange(info.Parameters.Select(p => p.name));
                string current = string.IsNullOrEmpty(info.Parameter) ? "(none)" : info.Parameter;
                if (!choices.Contains(current)) choices.Add(current);
                var popup = new PopupField<string>(choices, current) { tooltip = "The parameter it sets (the avatar's expression parameters)." };
                popup.AddToClassList("me-settings__popup");
                popup.RegisterValueChangedCallback(evt =>
                {
                    state.Add(new MenuEdit { kind = MenuEditKind.Parameter, id = node.Id, text = evt.newValue == "(none)" ? "" : evt.newValue }, "Parameter of “" + Plain(node.Name) + "”");
                    shownSignature = null; Refresh();
                });
                Setting(box, "Parameter", popup);
            }
            else if (info.Parameter.Length > 0) Setting(box, "Parameter", Value(info.Parameter + (info.ParameterType != null ? " · " + TypeName(info.ParameterType.Value) : "")));
            if (info.ValueEditable)
            {
                var field = new FloatField { value = info.Value, isDelayed = true, tooltip = "The value it sets its parameter to." };
                field.AddToClassList("me-settings__number");
                field.RegisterValueChangedCallback(evt => { state.Add(new MenuEdit { kind = MenuEditKind.Value, id = node.Id, number = evt.newValue }, "Value of “" + Plain(node.Name) + "”"); Refresh(); });
                Setting(box, "Value", field);
            }
            if (info.DefaultEditable || info.Parameter.Length > 0 || info.Toggle)
            {
                bool slider = info.Toggle ? node.Type == ControlType.RadialPuppet : info.ParameterType == VRCExpressionParameters.ValueType.Float;
                bool integer = !info.Toggle && info.ParameterType == VRCExpressionParameters.ValueType.Int;
                VisualElement field;
                if (slider)
                {
                    var s = new Slider(0f, 1f) { value = info.Default, tooltip = "Where it starts when the avatar loads." };
                    s.RegisterValueChangedCallback(evt => state.Add(new MenuEdit { kind = MenuEditKind.Default, id = node.Id, number = evt.newValue }, "Default of “" + Plain(node.Name) + "”"));
                    field = s;
                }
                else if (integer)
                {
                    var n = new IntegerField { value = Mathf.RoundToInt(info.Default), isDelayed = true, tooltip = "What it starts at when the avatar loads." };
                    n.RegisterValueChangedCallback(evt => state.Add(new MenuEdit { kind = MenuEditKind.Default, id = node.Id, number = Mathf.Clamp(evt.newValue, 0, 255) }, "Default of “" + Plain(node.Name) + "”"));
                    field = n;
                }
                else
                {
                    var t = new Toggle("Starts on") { value = info.Default > .5f, tooltip = "Whether it is on when the avatar loads." };
                    t.RegisterValueChangedCallback(evt => { state.Add(new MenuEdit { kind = MenuEditKind.Default, id = node.Id, number = evt.newValue ? 1f : 0f }, "Default of “" + Plain(node.Name) + "”"); Refresh(); });
                    field = t;
                }
                field.AddToClassList("me-settings__field");
                field.SetEnabled(info.DefaultEditable);
                Setting(box, "Default", field);
                var flags = new VisualElement(); flags.AddToClassList("me-settings__flags");
                var saved = new Toggle("Saved") { value = info.Saved, tooltip = "VRChat keeps its value from one session to the next." };
                saved.SetEnabled(info.SavedEditable);
                saved.RegisterValueChangedCallback(evt => { state.Add(new MenuEdit { kind = MenuEditKind.Saved, id = node.Id, flag = evt.newValue }, (evt.newValue ? "Save “" : "Don't save “") + Plain(node.Name) + "”"); Refresh(); });
                flags.Add(saved);
                if (!info.Toggle)
                {
                    int bits = info.ParameterType == VRCExpressionParameters.ValueType.Bool ? 1 : 8;
                    var synced = new Toggle("Synced") { value = info.Synced, tooltip = "Others see it (" + bits + (bits == 1 ? " bit" : " bits") + " of the avatar's 256). Off: only you see its effect." };
                    synced.SetEnabled(info.SyncedEditable);
                    synced.RegisterValueChangedCallback(evt => { state.Add(new MenuEdit { kind = MenuEditKind.Synced, id = node.Id, flag = evt.newValue }, (evt.newValue ? "Sync “" : "Don't sync “") + Plain(node.Name) + "”"); Refresh(); });
                    flags.Add(synced);
                }
                Setting(box, "", flags);
            }
            if (info.Why != null) { var why = new Label(info.Why); why.AddToClassList("me-card__text"); why.AddToClassList("me-settings__why"); box.Add(why); }
        }

        private static void Setting(VisualElement parent, string key, VisualElement field)
        {
            var row = new VisualElement(); row.AddToClassList("me-setting");
            var k = new Label(key); k.AddToClassList("me-fact__key"); row.Add(k);
            field.AddToClassList("me-setting__field"); row.Add(field);
            parent.Add(row);
        }

        private static Label Value(string text) { var v = new Label(text); v.AddToClassList("me-fact__value"); return v; }

        private static string TypeName(VRCExpressionParameters.ValueType type) =>
            type == VRCExpressionParameters.ValueType.Bool ? "on/off" : type == VRCExpressionParameters.ValueType.Int ? "whole number" : "decimal";

        // Several ticked items: what they are, and making a folder of them.
        private void ShowSelection(VisualElement card, List<MenuNode> nodes)
        {
            var heading = new Label(nodes.Count + " items selected"); heading.AddToClassList("me-card__title"); card.Add(heading);
            var list = new VisualElement(); list.AddToClassList("me-selection"); card.Add(list);
            foreach (var n in nodes.Take(10))
            {
                var row = new Label((n.IsFolder ? "▸ " : "• ") + Plain(n.Name)); row.AddToClassList("me-selection__item"); list.Add(row);
            }
            if (nodes.Count > 10) { var more = new Label("and " + (nodes.Count - 10) + " more"); more.AddToClassList("me-selection__item"); list.Add(more); }
            var movable = nodes.Where(n => Movable(n, out _)).ToList();
            if (movable.Count > 0) card.Add(FolderButton(movable));
            var actions = new VisualElement(); actions.AddToClassList("me-icon-actions"); actions.style.marginTop = 8; card.Add(actions);
            actions.Add(Link("Clear selection", "Untick every item.", () =>
            {
                ticked.Clear();
                var none = new HashSet<string>();
                quick.SetChecked(none); radial.SetChecked(none);
                ShowDetails();
            }));
        }

        // "Create folder (submenu)": the items go in a new folder at the first one's place, named in place right away.
        private Button FolderButton(List<MenuNode> items)
        {
            var button = MyAvatarEditor.Button("Create folder (submenu)", null);
            button.AddToClassList("me-make-folder");
            button.tooltip = items.Count == 1 ? "Put this item in a new folder, where it is now." : "Put the " + items.Count + " selected items in a new folder, at the first one's place.";
            ButtonInteraction.RegisterImmediateClick(button, () =>
            {
                string id = CreateFolder(items);
                if (id != null) quick.BeginRename(Node(id));
            });
            return button;
        }

        private static void Tip(VisualElement parent, string key, string text)
        {
            var row = new VisualElement(); row.AddToClassList("me-tip");
            var k = new Label(key); k.AddToClassList("me-tip__key"); row.Add(k);
            var t = new Label(text); t.AddToClassList("me-tip__text"); row.Add(t);
            parent.Add(row);
        }

        private static void Fact(VisualElement parent, string key, string value, bool muted = false)
        {
            var row = new VisualElement(); row.AddToClassList("me-fact");
            var k = new Label(key); k.AddToClassList("me-fact__key"); row.Add(k);
            var v = new Label(value); v.AddToClassList("me-fact__value"); v.EnableInClassList("me-fact__value--muted", muted); row.Add(v);
            parent.Add(row);
        }

        private static Button Link(string text, string tip, Action action)
        {
            var button = MyAvatarEditor.Button(text, null);
            button.AddToClassList("me-link");
            button.tooltip = tip;
            ButtonInteraction.RegisterImmediateClick(button, action);
            return button;
        }

        private static string Kind(MenuNode node, MenuNode logical)
        {
            switch (node.Type)
            {
                case ControlType.SubMenu:
                    int count = logical?.Page?.Items.Count ?? node.Page?.Items.Count ?? 0;
                    return "Folder · " + count + (count == 1 ? " item" : " items");
                case ControlType.Toggle: return "Toggle";
                case ControlType.Button: return "Button";
                case ControlType.RadialPuppet: return "Radial (slider)";
                case ControlType.TwoAxisPuppet: return "Two-axis puppet";
                case ControlType.FourAxisPuppet: return "Four-axis puppet";
                default: return node.Type.ToString();
            }
        }

        private static string From(MenuNode node)
        {
            var origin = node.Origin;
            if (origin == null) return "VRCFury";
            string owner = origin.Component != null ? "“" + origin.Component.gameObject.name + "”" : "the avatar";
            switch (origin.Kind)
            {
                case MenuOriginKind.AvatarMenu: return "The avatar's menu, " + (origin.Asset != null ? origin.Asset.name : "") + (AssetDatabase.GetAssetPath(origin.Asset).StartsWith("Packages/", StringComparison.Ordinal) ? " (in a package)" : "");
                case MenuOriginKind.MergedMenu: return "VRCFury Full Controller on " + owner + ", menu " + (origin.Asset != null ? origin.Asset.name : "");
                case MenuOriginKind.Toggle: return "VRCFury Toggle on " + owner;
                case MenuOriginKind.Puppet: return "VRCFury Puppet on " + owner;
                case MenuOriginKind.GestureLock: return "VRCFury Gesture on " + owner;
                case MenuOriginKind.SecurityLock: return "VRCFury Security Lock on " + owner;
                case MenuOriginKind.Folder: return "A folder VRCFury makes for menu paths (“" + origin.Path + "”)";
                case MenuOriginKind.Sps: return origin.Component != null ? "VRCFury SPS socket on " + owner : "VRCFury SPS options";
                case MenuOriginKind.MaItem: return "Modular Avatar Menu Item on " + owner;
                case MenuOriginKind.MaMenu: return "Modular Avatar Menu Installer on " + owner + ", menu " + (origin.Asset != null ? origin.Asset.name : "");
                case MenuOriginKind.MaMore: return "Modular Avatar (a menu of more than 8 items continues in “More”)";
                case MenuOriginKind.NewFolder: return "A folder made here";
                default: return "VRCFury";
            }
        }

        private static string SavedAs(MenuNode node)
        {
            var origin = node.Origin;
            switch (origin?.Kind)
            {
                case MenuOriginKind.AvatarMenu:
                    string path = AssetDatabase.GetAssetPath(origin.Asset);
                    return path.StartsWith("Assets/", StringComparison.Ordinal) ? "Changes are written into " + origin.Asset.name + "." : "Saving copies " + (origin.Asset != null ? origin.Asset.name : "this menu") + " into the project first, then edits the copy.";
                case MenuOriginKind.Toggle:
                case MenuOriginKind.Puppet:
                    return MenuSave.UsersOwn(origin.Component) ? "Changes are written into its VRCFury component (menu path and icon)." : "Its prefab comes from a package: changes are saved with VRCFury's Move, Set Icon and Reorder features instead.";
                case MenuOriginKind.NewFolder:
                    return "Saving makes it a new menu in the avatar's menu when it is there, else VRCFury's Move Menu Item moves its items into it.";
                case MenuOriginKind.MaItem:
                    return MenuSave.UsersOwn(origin.Component) ? "Its name and icon are written into its Menu Item; moves are saved with VRCFury's Move and Reorder features." : "Its prefab comes from a package: changes are saved with VRCFury's Move, Set Icon and Reorder features instead.";
                default:
                    return "Changes are saved with VRCFury's Move, Set Icon and Reorder features on “" + MenuSave.EditsName + "”, leaving its source untouched.";
            }
        }

        private static UnityEngine.Object SourceObject(MenuNode node)
        {
            var origin = node.Origin;
            if (origin == null) return null;
            if (origin.Component != null) return origin.Component.gameObject;
            return origin.Asset;
        }

        private void Browse(MenuNode node)
        {
            string file = EditorUtility.OpenFilePanelWithFilters("Choose an icon for “" + Plain(node.Name) + "”", "",
                new[] { "Images", "png,jpg,jpeg,tga,psd,bmp,tif,tiff,gif", "All files", "*" });
            if (string.IsNullOrEmpty(file)) return;
            if (!MenuIcons.IsImage(file)) { SetStatus("That file is not a picture.", true); return; }
            // A picture already in the project is used as it is.
            string project = System.IO.Path.GetFullPath(System.IO.Directory.GetCurrentDirectory()).Replace('\\', '/') + "/";
            string normalized = System.IO.Path.GetFullPath(file).Replace('\\', '/');
            var asset = normalized.StartsWith(project, StringComparison.OrdinalIgnoreCase) ? AssetDatabase.LoadAssetAtPath<Texture2D>(normalized.Substring(project.Length)) : null;
            SetIcon(node, asset, asset == null ? file : null);
        }

        // ---- Status, Save, Cancel -------------------------------------------------------------------------------------------

        private void SetStatus(string text, bool warning)
        {
            status = text; statusWarning = warning;
            ShowStatus();
        }

        private void SetNotice(string text)
        {
            if (notice == null) return;
            noticeText.text = text ?? "";
            notice.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void ShowStatus()
        {
            if (statusText == null) return;
            int count = state != null ? state.edits.Count : 0;
            string text = status;
            if (text == null)
                text = count == 0 ? "No changes. What you see is what VRChat will show." + Builders(built, " ", " builds this menu.", " build this menu.")
                    : count + (count == 1 ? " change" : " changes") + " not saved yet" + (draft != null && draft.Stale > 0 ? " (" + draft.Stale + " no longer apply)" : "") + ".";
            statusText.text = text;
            statusText.EnableInClassList("warning", statusWarning && status != null);
            statusDot.EnableInClassList("me-status__dot--changes", count > 0 && !(statusWarning && status != null));
            statusDot.EnableInClassList("me-status__dot--warning", statusWarning && status != null);
            if (!saving) { save.text = "Save"; save.SetEnabled(count > 0 && root != null); }
            cancel.text = count > 0 ? "Cancel" : "Close";
            undoButton.EnableInClassList("me-round--off", false);
            redoButton.EnableInClassList("me-round--off", false);
            if (string.IsNullOrEmpty(hint.text)) hint.text = DefaultHint;
        }

        private void Save()
        {
            try
            {
                if (root == null || state.edits.Count == 0) return;
                var outcome = MenuSave.Save(root, state.edits, state, MenuSave.Removals.Of(draft));
                if (outcome.Saved)
                {
                    var parts = new List<string>();
                    if (outcome.Assets > 0) parts.Add(outcome.Assets + (outcome.Assets == 1 ? " menu asset" : " menu assets"));
                    if (outcome.Components > 0) parts.Add(outcome.Components + (outcome.Components == 1 ? " component" : " components"));
                    if (outcome.Features > 0) parts.Add(outcome.Features + " VRCFury " + (outcome.Features == 1 ? "feature" : "features") + " on “" + MenuSave.EditsName + "”");
                    int left = draft.Left().Count;
                    if (left > 0) parts.Add(left + (left == 1 ? " item" : " items") + " left out of the upload");
                    string summary = "Saved" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
                    SetStatus(summary + ". Saving the scene…", false);
                    WriteToDisk(summary, outcome.Touched);
                }
                else SetStatus(outcome.Message, true);
            }
            finally
            {
                saving = false;
                dirty = true;
                Refresh();
                rootVisualElement.schedule.Execute(() => { if (status != null && !statusWarning) { status = null; ShowStatus(); } }).StartingIn(9000);
            }
        }

        // Like My Avatar's texture Save: the assets and the avatar's scene go to disk (with Unit Git, a "menu change"
        // checkpoint too). Ctrl+Z still undoes the save in the editor.
        private async void WriteToDisk(string summary, IEnumerable<string> touched)
        {
            const string undo = " Ctrl+Z puts the menu back as it was.";
            try
            {
                var scene = root != null ? root.scene : default;
                if (!scene.IsValid() || EditorSceneManager.IsPreviewScene(scene) || string.IsNullOrEmpty(scene.path))
                {
                    SetStatus(summary + ". Save the scene to keep it on disk." + undo, false);
                    return;
                }
                string note = await TextureChanges.SaveAsync(scene, owner, "menu change", touched);
                string rest = (note.StartsWith("Saved", StringComparison.Ordinal) ? note.Substring(5) : note).Trim(' ', '.', '·');
                SetStatus(summary + (rest.Length > 0 ? " · " + rest : "") + "." + undo, false);
            }
            catch (Exception ex)
            {
                SetStatus(summary + " in the editor, but not on disk: " + ex.Message, true);
            }
        }

        // The tools that build this menu in a sentence (", with everything VRCFury adds"), or nothing.
        private static string Builders(MenuBuildResult result, string before, string one, string both)
        {
            if (result == null || (!result.VrcFury && !result.ModularAvatar)) return "";
            if (result.VrcFury && result.ModularAvatar) return before + "VRCFury and Modular Avatar" + both;
            return before + (result.VrcFury ? "VRCFury" : "Modular Avatar") + one;
        }

        private void CancelEdits()
        {
            if (state != null && state.edits.Count > 0) state.Clear("Cancel menu edits");
            hasUnsavedChanges = false;
            Close();
        }

        /// <summary>A soft dark stage behind the menu, like VRChat's world behind its menus.</summary>
        private sealed class StageBackdrop : VisualElement
        {
            public StageBackdrop() { generateVisualContent += Draw; }

            private void Draw(MeshGenerationContext context)
            {
                var r = contentRect;
                if (r.width <= 0) return;
                var top = new Color32(26, 22, 34, 255);
                var bottom = new Color32(14, 16, 20, 255);
                var mesh = context.Allocate(4, 6);
                mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMin, r.yMin, Vertex.nearZ), tint = top });
                mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMax, r.yMin, Vertex.nearZ), tint = top });
                mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMax, r.yMax, Vertex.nearZ), tint = bottom });
                mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMin, r.yMax, Vertex.nearZ), tint = bottom });
                mesh.SetNextIndex(0); mesh.SetNextIndex(1); mesh.SetNextIndex(2);
                mesh.SetNextIndex(0); mesh.SetNextIndex(2); mesh.SetNextIndex(3);
            }
        }
    }
}
