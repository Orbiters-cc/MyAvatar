using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    internal enum MenuEditKind
    {
        Rename, Icon, Move, NextText, NextIcon, Folder,
        /// <summary>The item (and a folder's contents) is left out of the upload; Restore puts it back.</summary>
        Delete, Restore,
        /// <summary>The avatar's own menu is left out of the upload (<see cref="MenuEdit.flag"/>), or put back.</summary>
        DefaultMenu,
        /// <summary>The item's control type (<see cref="MenuEdit.index"/>), parameter (<see cref="MenuEdit.text"/>) and value (<see cref="MenuEdit.number"/>).</summary>
        Type, Parameter, Value,
        /// <summary>What the item's parameter starts at (<see cref="MenuEdit.number"/>), and whether it is saved and synced (<see cref="MenuEdit.flag"/>).</summary>
        Default, Saved, Synced,
    }

    /// <summary>One change made in the menu editor, applied over the menu the avatar builds today.</summary>
    [Serializable]
    internal sealed class MenuEdit
    {
        public MenuEditKind kind;
        /// <summary>The item (<see cref="MenuNode.Id"/>).</summary>
        public string id;
        /// <summary>Rename, NextText: the new name.</summary>
        public string text;
        /// <summary>Icon, NextIcon: a project texture, or an image file imported on Save (<see cref="file"/>); neither clears it.</summary>
        public Texture2D icon;
        public string file;
        /// <summary>Move: the folder id (<see cref="MenuTree.RootId"/> for the main menu) and the place in it.</summary>
        public string parent;
        public int index;
        /// <summary>
        /// Folder: the items a new folder takes (<see cref="id"/> is the new folder's, <see cref="text"/> its name); it takes
        /// the first one's place.
        /// </summary>
        public List<string> items = new List<string>();
        /// <summary>Value, Default: the number; DefaultMenu, Saved, Synced: on or off.</summary>
        public float number;
        public bool flag;

        public MenuEdit Copy() => (MenuEdit)MemberwiseClone();

        /// <summary>Edits that change the menu's items, places, names or icons (the others are settings and deletions).</summary>
        public bool Structural => kind <= MenuEditKind.Folder;
    }

    /// <summary>
    /// The pending edits of one avatar's menu. Each change is a Unity Undo step on this object, so Ctrl+Z and Ctrl+Y (and
    /// Edit > Undo) step through them, and undoing a Save brings the edits back.
    /// </summary>
    internal sealed class MenuEditState : ScriptableObject
    {
        public List<MenuEdit> edits = new List<MenuEdit>();
        /// <summary>Bumped by every change, Undo and Redo included: the window rebuilds when it differs.</summary>
        public int revision;

        public static MenuEditState Create()
        {
            var state = CreateInstance<MenuEditState>();
            state.hideFlags = HideFlags.DontSave;
            return state;
        }

        public void Add(MenuEdit edit, string undoName)
        {
            Undo.RecordObject(this, undoName);
            // Consecutive renames, icons or settings of one item are one change (a slider dragged, a name typed).
            var last = edits.Count > 0 ? edits[edits.Count - 1] : null;
            if (last != null && last.kind == edit.kind && last.id == edit.id && (edit.kind == MenuEditKind.Rename || edit.kind == MenuEditKind.Icon || edit.kind >= MenuEditKind.Type))
                edits[edits.Count - 1] = edit;
            else edits.Add(edit);
            revision++;
            // One Undo step per change, even when several happen in one frame.
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
        }

        /// <summary>Several edits as one change (one Undo step).</summary>
        public void Add(IEnumerable<MenuEdit> list, string undoName)
        {
            Undo.RecordObject(this, undoName);
            edits.AddRange(list);
            revision++;
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
        }

        public void Clear(string undoName)
        {
            if (edits.Count == 0) return;
            Undo.RecordObject(this, undoName);
            edits.Clear();
            revision++;
        }
    }

    /// <summary>An item's settings changed in the editor: its parameter's default, whether it is saved and synced.</summary>
    internal sealed class MenuItemSettings
    {
        public float? Default;
        public bool? Saved, Synced;
    }

    /// <summary>The menu with the pending edits applied: what Save makes the avatar build.</summary>
    internal sealed class MenuDraft
    {
        /// <summary>The menu the avatar builds once saved, items left out of the upload included.</summary>
        public MenuPage Root;
        /// <summary>Items left out of the upload (deleted here, or saved so before and not put back).</summary>
        public readonly HashSet<string> Deleted = new HashSet<string>();
        /// <summary>The avatar's own menu is left out of the upload.</summary>
        public bool DefaultRemoved;
        /// <summary>Settings changed here, by <see cref="SettingsKey"/>.</summary>
        public readonly Dictionary<string, MenuItemSettings> Settings = new Dictionary<string, MenuItemSettings>();
        public string NextText;
        public Texture2D NextIcon;
        public bool NextIconChanged, NextTextChanged;
        /// <summary>Edits that no longer find their item (it was removed from the avatar meanwhile).</summary>
        public int Stale;
        /// <summary>Items an edit renamed, gave an icon or moved.</summary>
        public readonly HashSet<string> Changed = new HashSet<string>();
        /// <summary>Folders VRCFury creates for menu paths that the edits emptied: they disappear with their last item.</summary>
        public readonly HashSet<string> Dropped = new HashSet<string>();

        /// <summary>
        /// Applies <paramref name="edits"/> to a copy of <paramref name="built"/>; <paramref name="deleted"/> and
        /// <paramref name="defaultRemoved"/> are what is saved on the avatar (items left out of the upload, by id).
        /// </summary>
        public static MenuDraft Apply(MenuBuildResult built, IReadOnlyList<MenuEdit> edits, IEnumerable<string> deleted = null, bool defaultRemoved = false)
        {
            var draft = new MenuDraft { Root = MenuTree.Copy(built.Root), NextText = built.NextText, NextIcon = built.NextIcon, DefaultRemoved = defaultRemoved };
            if (deleted != null) draft.Deleted.UnionWith(deleted);
            if (edits == null || edits.Count == 0) return draft;
            var index = MenuTree.Index(draft.Root);
            var nonEmpty = new HashSet<MenuNode>(MenuTree.Walk(draft.Root).Select(e => e.Node).Where(n => n.IsFolder && n.Page != null && n.Page.Items.Count > 0));
            foreach (var edit in edits)
            {
                switch (edit.kind)
                {
                    case MenuEditKind.NextText:
                        draft.NextText = string.IsNullOrEmpty(edit.text) ? "Next" : edit.text; draft.NextTextChanged = true; continue;
                    case MenuEditKind.NextIcon:
                        draft.NextIcon = MenuIcons.Resolve(edit); draft.NextIconChanged = true; continue;
                    case MenuEditKind.Folder:
                        var made = NewFolder(draft, edit, ref index);
                        if (made == null) draft.Stale++;
                        else { nonEmpty.Add(made); draft.Changed.Add(made.Id); }
                        continue;
                    case MenuEditKind.DefaultMenu:
                        draft.DefaultRemoved = edit.flag; continue;
                    case MenuEditKind.Delete:
                        draft.Deleted.Add(edit.id); continue;
                    case MenuEditKind.Restore:
                        draft.Deleted.Remove(edit.id); continue;
                }
                if (edit.id == null || !index.TryGetValue(edit.id, out var entry)) { draft.Stale++; continue; }
                var node = entry.Node;
                switch (edit.kind)
                {
                    case MenuEditKind.Rename:
                        node.Name = edit.text ?? "";
                        break;
                    case MenuEditKind.Icon:
                        node.Icon = MenuIcons.Resolve(edit);
                        break;
                    case MenuEditKind.Type:
                        node.Type = (ControlType)edit.index;
                        break;
                    case MenuEditKind.Parameter:
                        node.Parameter = edit.text ?? "";
                        break;
                    case MenuEditKind.Value:
                        node.Value = edit.number;
                        break;
                    case MenuEditKind.Default:
                    case MenuEditKind.Saved:
                    case MenuEditKind.Synced:
                        string key = SettingsKey(node);
                        if (!draft.Settings.TryGetValue(key, out var settings)) draft.Settings[key] = settings = new MenuItemSettings();
                        if (edit.kind == MenuEditKind.Default) settings.Default = edit.number;
                        else if (edit.kind == MenuEditKind.Saved) settings.Saved = edit.flag;
                        else settings.Synced = edit.flag;
                        break;
                    case MenuEditKind.Move:
                        var target = MenuTree.PageOf(draft.Root, edit.parent, index);
                        if (target == null && edit.parent != MenuTree.RootId && index.TryGetValue(edit.parent, out var folder) && folder.Node.IsFolder)
                            target = folder.Node.Page = new MenuPage { Name = folder.Node.Name };
                        // Never into itself: a folder goes where the menu still reaches without it (a folder looping back to a menu
                        // above it can go inside that menu, which other folders still open).
                        if (target == null || (node.IsFolder && target != entry.Parent && !MenuTree.Reachable(draft.Root, target, node))) { draft.Stale++; continue; }
                        entry.Parent.Items.Remove(node);
                        int at = Mathf.Clamp(edit.index, 0, target.Items.Count);
                        target.Items.Insert(at, node);
                        index = MenuTree.Index(draft.Root);
                        break;
                }
                draft.Changed.Add(node.Id);
            }
            // A folder made only of menu paths exists for its items: emptied by the edits, it goes.
            bool removed;
            do
            {
                removed = false;
                foreach (var e in MenuTree.Walk(draft.Root))
                {
                    var n = e.Node;
                    var kind = n.Origin?.Kind;
                    if ((kind != MenuOriginKind.Folder && kind != MenuOriginKind.NewFolder) || !nonEmpty.Contains(n) || n.Page == null || n.Page.Items.Count > 0) continue;
                    e.Parent.Items.Remove(n);
                    draft.Dropped.Add(n.Id);
                    removed = true;
                    break;
                }
            } while (removed);
            return draft;
        }

        /// <summary>
        /// Where an item's settings live: its parameter's, shared by every item driving it, when the parameter is the avatar's
        /// own; the item's own for a VRCFury toggle (it makes its parameter).
        /// </summary>
        public static string SettingsKey(MenuNode node) =>
            node.Origin?.Kind == MenuOriginKind.Toggle ? "item:" + node.Id : "parameter:" + MenuSave.ItemParameter(node);

        /// <summary>The menu as shown in the editor and uploaded: without the items left out of the upload.</summary>
        public MenuPage Visible()
        {
            var hidden = new HashSet<string>(Deleted);
            if (DefaultRemoved) hidden.UnionWith(MenuUploadBuild.DefaultItems(Root).Select(n => n.Id));
            var copy = MenuTree.Copy(Root);
            var seen = new HashSet<MenuPage>();
            void Strip(MenuPage page)
            {
                if (page == null || !seen.Add(page)) return;
                page.Items.RemoveAll(n => hidden.Contains(n.Id));
                foreach (var n in page.Items) if (n.IsFolder) Strip(n.Page);
            }
            Strip(copy);
            return copy;
        }

        /// <summary>The items left out of the upload, each once: deleted ones, then the default menu's.</summary>
        public List<MenuTree.Entry> Left()
        {
            var index = MenuTree.Index(Root);
            var list = Deleted.Where(index.ContainsKey).Select(id => index[id]).ToList();
            if (DefaultRemoved)
                foreach (var node in MenuUploadBuild.DefaultItems(Root))
                    if (!Deleted.Contains(node.Id) && index.TryGetValue(node.Id, out var entry)) list.Add(entry);
            return list;
        }

        // A new folder at the first item's place, holding the items (those that can go in it, in the edit's order).
        private static MenuNode NewFolder(MenuDraft draft, MenuEdit edit, ref Dictionary<string, MenuTree.Entry> index)
        {
            if (string.IsNullOrEmpty(edit.id) || index.ContainsKey(edit.id) || edit.items == null || edit.items.Count == 0) return null;
            if (!index.TryGetValue(edit.items[0], out var first)) return null;
            var place = first.Parent;
            var folder = new MenuNode
            {
                Id = edit.id, Name = edit.text ?? "", Type = ControlType.SubMenu, Sort = first.Node.Sort,
                Page = new MenuPage { Name = edit.text ?? "" }, Origin = new MenuOrigin { Kind = MenuOriginKind.NewFolder },
            };
            place.Items.Insert(place.Items.IndexOf(first.Node), folder);
            foreach (var id in edit.items)
            {
                if (!index.TryGetValue(id, out var item) || item.Node == folder) continue;
                // A folder whose menu leads to this place would leave the menu: it stays.
                if (item.Node.IsFolder && !MenuTree.Reachable(draft.Root, place, item.Node)) continue;
                item.Parent.Items.Remove(item.Node);
                folder.Page.Items.Add(item.Node);
                draft.Changed.Add(item.Node.Id);
                index = MenuTree.Index(draft.Root);
            }
            if (folder.Page.Items.Count == 0) { place.Items.Remove(folder); return null; }
            index = MenuTree.Index(draft.Root);
            return folder;
        }
    }

    /// <summary>Menu icons: the textures picked in the editor, and image files, read for the preview and imported on Save.</summary>
    internal static class MenuIcons
    {
        public const string Folder = "Assets/Orbiters/MyAvatar/Menu Icons";
        public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".tif", ".tiff", ".gif", ".exr" };
        private static readonly Dictionary<string, Texture2D> Files = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public static bool IsImage(string path) => !string.IsNullOrEmpty(path) && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>The icon an edit gives: its texture, or its image file read for the preview.</summary>
        public static Texture2D Resolve(MenuEdit edit)
        {
            if (edit.icon != null) return edit.icon;
            return string.IsNullOrEmpty(edit.file) ? null : Load(edit.file);
        }

        /// <summary>An image file outside the project, read once into a texture that is never saved.</summary>
        public static Texture2D Load(string file)
        {
            if (Files.TryGetValue(file, out var texture) && texture != null) return texture;
            try
            {
                if (!File.Exists(file)) return null;
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = Path.GetFileNameWithoutExtension(file) };
                if (!texture.LoadImage(File.ReadAllBytes(file))) { UnityEngine.Object.DestroyImmediate(texture); return null; }
                texture.wrapMode = TextureWrapMode.Clamp;
                Files[file] = texture;
                return texture;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Imports an image file as a menu icon texture: VRChat wants menu icons compressed and at most 256 pixels; they are
        /// drawn over the menu, so alpha is transparency and there are no mipmaps.
        /// </summary>
        public static Texture2D Import(string file)
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                string parent = "Assets";
                foreach (var part in Folder.Substring("Assets/".Length).Split('/'))
                {
                    if (!AssetDatabase.IsValidFolder(parent + "/" + part)) AssetDatabase.CreateFolder(parent, part);
                    parent += "/" + part;
                }
            }
            string name = Path.GetFileNameWithoutExtension(file);
            string path = AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + name + Path.GetExtension(file).ToLowerInvariant());
            File.Copy(file, path, false);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.maxTextureSize = 256;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.npotScale = TextureImporterNPOTScale.ToNearest;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>A texture the menu can use as it is, or the reason it can't (VRChat's limits on menu icons).</summary>
        public static string Problem(Texture2D icon)
        {
            if (icon == null) return null;
            if (icon.width > 256 || icon.height > 256) return "larger than 256 pixels";
            return null;
        }
    }
}
