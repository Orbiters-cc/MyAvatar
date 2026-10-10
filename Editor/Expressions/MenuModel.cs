using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>What defines a menu item, so an edit can be written back where it belongs.</summary>
    internal enum MenuOriginKind
    {
        /// <summary>A control of the avatar's own menu assets (the descriptor's menu and its submenus).</summary>
        AvatarMenu,
        /// <summary>A control of a menu a VRCFury Full Controller merges in.</summary>
        MergedMenu,
        /// <summary>A VRCFury Toggle (or slider, or hold button) and its menu path.</summary>
        Toggle,
        /// <summary>A VRCFury Puppet.</summary>
        Puppet,
        /// <summary>The lock item of a VRCFury Gesture.</summary>
        GestureLock,
        /// <summary>The buttons of a VRCFury Security Lock.</summary>
        SecurityLock,
        /// <summary>A folder VRCFury creates for menu paths ("Clothes/Hat" creates "Clothes").</summary>
        Folder,
        /// <summary>The "Next" item VRCFury adds when a menu holds more than 8 items.</summary>
        Next,
        /// <summary>An item VRCFury's SPS adds: a socket's toggle (its component) or one of the SPS options.</summary>
        Sps,
        /// <summary>A Modular Avatar Menu Item (its component).</summary>
        MaItem,
        /// <summary>A control of a menu a Modular Avatar Menu Installer adds (the installer, the asset and the control).</summary>
        MaMenu,
        /// <summary>The "More" item Modular Avatar adds when a menu holds more than 8 items.</summary>
        MaMore,
        /// <summary>
        /// A folder made in the menu editor: saved as a new menu asset in the avatar's menu when its place is one, else made by
        /// VRCFury's Move Menu Item moving its items to its path.
        /// </summary>
        NewFolder,
    }

    internal sealed class MenuOrigin
    {
        public MenuOriginKind Kind;
        /// <summary>AvatarMenu and MergedMenu: the asset holding the control, its index and the control itself.</summary>
        public VRCExpressionsMenu Asset;
        public int Index;
        public VRCExpressionsMenu.Control Control;
        /// <summary>The VRCFury component behind the item (Full Controller, Toggle, Puppet, Gesture, Security Lock).</summary>
        public Component Component;
        /// <summary>Toggle, Puppet and GestureLock: the menu path they declare; Folder: where it was created.</summary>
        public string Path;

        public MenuOrigin Clone() => (MenuOrigin)MemberwiseClone();
    }

    /// <summary>One control of the menu as VRChat will show it.</summary>
    internal sealed class MenuNode
    {
        /// <summary>Stable across rebuilds of the same avatar: edits refer to items by it.</summary>
        public string Id;
        public string Name = "";
        public ControlType Type = ControlType.Button;
        public Texture2D Icon;
        public string Parameter;
        public float Value = 1;
        public string[] SubParameters = Array.Empty<string>();
        public VRCExpressionsMenu.Control.Label[] Labels;
        /// <summary>The submenu (SubMenu items only; null when it opens nothing).</summary>
        public MenuPage Page;
        public MenuOrigin Origin;
        /// <summary>VRCFury's sort position: the number of the feature (or service) that created the control.</summary>
        internal int Sort;

        public bool IsFolder => Type == ControlType.SubMenu;

        public MenuNode CloneShallow()
        {
            var copy = (MenuNode)MemberwiseClone();
            copy.Origin = Origin?.Clone();
            return copy;
        }

        public override string ToString() => Name + " [" + Type + "] " + Id;
    }

    /// <summary>One menu (VRCExpressionsMenu): the root, or the inside of a submenu.</summary>
    internal sealed class MenuPage
    {
        public string Name = "";
        /// <summary>The avatar menu asset this page shows, when it is one (descriptor menu tree, avatar without VRCFury).</summary>
        public VRCExpressionsMenu Asset;
        public readonly List<MenuNode> Items = new List<MenuNode>();
        /// <summary>Shown pages: the folder whose items this page shows (<see cref="MenuTree.RootId"/> for the main menu)…</summary>
        public string Folder = MenuTree.RootId;
        /// <summary>…and the place of its first item there (7, 14… on the pages after "Next").</summary>
        public int Offset;
    }

    /// <summary>Walks and searches menus. Every page is visited once: menus may share or loop back to a submenu.</summary>
    internal static class MenuTree
    {
        public const string RootId = "root";

        public readonly struct Entry
        {
            public readonly MenuNode Node;
            public readonly MenuPage Parent;
            /// <summary>The folder holding the item (null at the root).</summary>
            public readonly MenuNode Folder;
            public readonly int Depth;
            public Entry(MenuNode node, MenuPage parent, MenuNode folder, int depth) { Node = node; Parent = parent; Folder = folder; Depth = depth; }
        }

        /// <summary>Every item, parents before their contents, each page once.</summary>
        public static List<Entry> Walk(MenuPage root)
        {
            var list = new List<Entry>();
            if (root == null) return list;
            var seen = new HashSet<MenuPage>();
            void Visit(MenuPage page, MenuNode folder, int depth)
            {
                if (page == null || !seen.Add(page)) return;
                foreach (var node in page.Items)
                {
                    list.Add(new Entry(node, page, folder, depth));
                    if (node.IsFolder && node.Page != null) Visit(node.Page, node, depth + 1);
                }
            }
            Visit(root, null, 0);
            return list;
        }

        /// <summary>Items by id (the first one when a shared submenu shows it twice).</summary>
        public static Dictionary<string, Entry> Index(MenuPage root)
        {
            var map = new Dictionary<string, Entry>();
            foreach (var entry in Walk(root))
                if (entry.Node.Id != null && !map.ContainsKey(entry.Node.Id)) map[entry.Node.Id] = entry;
            return map;
        }

        /// <summary>The page a folder id opens: the root for <see cref="RootId"/>.</summary>
        public static MenuPage PageOf(MenuPage root, string folderId, Dictionary<string, Entry> index = null)
        {
            if (folderId == RootId) return root;
            index ??= Index(root);
            return index.TryGetValue(folderId, out var entry) && entry.Node.IsFolder ? entry.Node.Page : null;
        }

        /// <summary>A copy that can be edited without touching <paramref name="root"/>; shared submenus stay shared.</summary>
        public static MenuPage Copy(MenuPage root)
        {
            var pages = new Dictionary<MenuPage, MenuPage>();
            MenuPage CopyPage(MenuPage page)
            {
                if (page == null) return null;
                if (pages.TryGetValue(page, out var done)) return done;
                var copy = new MenuPage { Name = page.Name, Asset = page.Asset };
                pages[page] = copy;
                foreach (var node in page.Items)
                {
                    var c = node.CloneShallow();
                    c.Page = CopyPage(node.Page);
                    copy.Items.Add(c);
                }
                return copy;
            }
            return CopyPage(root);
        }

        /// <summary>Whether <paramref name="page"/> is <paramref name="folder"/>'s submenu or inside it.</summary>
        public static bool Contains(MenuNode folder, MenuPage page)
        {
            if (folder?.Page == null || page == null) return false;
            var seen = new HashSet<MenuPage>();
            var stack = new Stack<MenuPage>();
            stack.Push(folder.Page);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current)) continue;
                if (current == page) return true;
                foreach (var node in current.Items) if (node.IsFolder && node.Page != null) stack.Push(node.Page);
            }
            return false;
        }

        public static int Count(MenuPage root) => Walk(root).Count;

        /// <summary>
        /// Whether <paramref name="page"/> can be reached from <paramref name="root"/> without going through
        /// <paramref name="skip"/>: a folder put there stays in the menu (else it would only be inside itself).
        /// </summary>
        public static bool Reachable(MenuPage root, MenuPage page, MenuNode skip)
        {
            if (root == null || page == null) return false;
            var seen = new HashSet<MenuPage>();
            var stack = new Stack<MenuPage>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!seen.Add(current)) continue;
                if (current == page) return true;
                foreach (var node in current.Items) if (node != skip && node.IsFolder && node.Page != null) stack.Push(node.Page);
            }
            return false;
        }

        /// <summary>
        /// Whether another folder, outside <paramref name="folder"/>, opens the same menu (a submenu used twice, or a folder
        /// looping back to a menu above it). VRCFury copies such a menu when the folder is moved, instead of moving it.
        /// </summary>
        public static bool OpenedElsewhere(MenuPage root, MenuNode folder)
        {
            if (folder == null || !folder.IsFolder || folder.Page == null) return false;
            if (folder.Page == root) return true;
            foreach (var entry in Walk(root))
            {
                var node = entry.Node;
                if (node == folder || !node.IsFolder || node.Page != folder.Page) continue;
                // A loop inside the folder's own menu moves with it.
                if (entry.Parent == folder.Page || Contains(folder, entry.Parent)) continue;
                return true;
            }
            return false;
        }
    }
}
