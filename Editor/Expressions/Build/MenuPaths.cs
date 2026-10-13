using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// Where an item is in a menu: the names of the folders down to it and its own, each with which item of that name it is in
    /// its menu. Saved removals use the editor's menu (before VRCFury splits pages); the upload follows paths in the menu
    /// VRChat receives (<see cref="Step"/>), where VRCFury's "Next" items are known from the menu worked out before the build,
    /// never by their name: a creator's own folder named "Next" or Modular Avatar's "More" are folders like any other.
    /// </summary>
    internal static class MenuPaths
    {
        /// <summary>The path of <paramref name="target"/> in <paramref name="root"/>; false when it is not there.</summary>
        public static bool Of(MenuPage root, MenuNode target, List<string> names, List<int> nth)
        {
            names.Clear(); nth.Clear();
            var seen = new HashSet<MenuPage>();
            bool Find(MenuPage page)
            {
                if (page == null || !seen.Add(page)) return false;
                foreach (var node in page.Items)
                {
                    names.Add(node.Name ?? "");
                    nth.Add(page.Items.TakeWhile(n => n != node).Count(n => n.Name == node.Name));
                    if (node == target) return true;
                    if (node.IsFolder && Find(node.Page)) return true;
                    names.RemoveAt(names.Count - 1); nth.RemoveAt(nth.Count - 1);
                }
                return false;
            }
            return Find(root);
        }

        /// <summary>The item at a path in the editor's menu, or null.</summary>
        public static MenuNode Find(MenuPage root, IList<string> names, IList<int> nth)
        {
            var page = root;
            MenuNode node = null;
            for (int i = 0; i < names.Count; i++)
            {
                if (page == null) return null;
                node = page.Items.Where(n => n.Name == names[i]).Skip(i < nth.Count ? nth[i] : 0).FirstOrDefault();
                if (node == null) return null;
                page = node.IsFolder ? node.Page : null;
            }
            return node;
        }

        /// <summary>
        /// The item a saved removal leaves out of <paramref name="root"/>: the one at its path when it is still the item that was
        /// deleted (same type and parameter, and the same component behind it), else another of that name in the same menu
        /// that is (items added before it moved it). Null when none is: a new item of the same name is never taken for it.
        /// </summary>
        public static MenuNode Resolve(MenuPage root, MenuRemoval removal)
        {
            if (removal?.path == null || removal.path.Count == 0) return null;
            int last = removal.path.Count - 1;
            var page = root;
            if (last > 0)
            {
                var folder = Find(root, removal.path.Take(last).ToList(), (removal.nth ?? new List<int>()).Take(last).ToList());
                page = folder != null && folder.IsFolder ? folder.Page : null;
            }
            if (page == null) return null;
            var named = page.Items.Where(n => n.Name == removal.path[last]).ToList();
            int at = removal.nth != null && last < removal.nth.Count ? removal.nth[last] : 0;
            if (at >= 0 && at < named.Count && Same(named[at], removal)) return named[at];
            return named.FirstOrDefault(n => Same(n, removal));
        }

        /// <summary>
        /// Whether <paramref name="node"/> is the item <paramref name="removal"/> was saved from. A removal saved from a
        /// component's item whose component is deleted since matches nothing (its <see cref="MenuRemoval.source"/> reads null
        /// then, like a menu asset control's).
        /// </summary>
        public static bool Same(MenuNode node, MenuRemoval removal)
        {
            var component = node.Origin?.Component;
            return (int)node.Type == removal.type && (removal.parameter ?? "") == MenuSave.ItemParameter(node) &&
                   (component != null ? removal.fromComponent && removal.source == component : !removal.fromComponent);
        }

        /// <summary>A step of a path in the menu VRChat receives: an item by its name (the nth of that name on its page), or VRCFury's "Next" item.</summary>
        internal readonly struct Step
        {
            public readonly string Name;
            public readonly int Nth;
            /// <summary>VRCFury's "Next" item: the last of a full page, opening the rest of its menu.</summary>
            public readonly bool Next;
            public Step(string name, int nth, bool next) { Name = name ?? ""; Nth = nth; Next = next; }
            public override string ToString() => Next ? "[Next]" : Name + (Nth > 0 ? "#" + Nth : "");
        }

        /// <summary>
        /// The path of the item <paramref name="id"/> in <paramref name="paged"/> (the menu with VRCFury's pages, as
        /// <see cref="MenuBuild.Paginate"/> makes it); null when it is not there.
        /// </summary>
        public static List<Step> PagedPath(MenuPage paged, string id)
        {
            var steps = new List<Step>();
            var seen = new HashSet<MenuPage>();
            bool Visit(MenuPage page)
            {
                if (page == null || !seen.Add(page)) return false;
                foreach (var node in page.Items)
                {
                    bool next = node.Origin?.Kind == MenuOriginKind.Next;
                    steps.Add(new Step(node.Name, next ? 0 : page.Items.TakeWhile(n => n != node).Count(n => n.Name == node.Name), next));
                    if (node.Id == id) return true;
                    if (node.IsFolder && Visit(node.Page)) return true;
                    steps.RemoveAt(steps.Count - 1);
                }
                return false;
            }
            return Visit(paged) ? steps : null;
        }

        /// <summary>A control of a built menu, and the menu asset holding it.</summary>
        public readonly struct Found
        {
            public readonly VRCExpressionsMenu Menu;
            public readonly VRCExpressionsMenu.Control Control;
            public Found(VRCExpressionsMenu menu, VRCExpressionsMenu.Control control) { Menu = menu; Control = control; }
        }

        /// <summary>The control a path leads to in a built menu: "Next" steps take the last control of a full page, the others are found by name on their page.</summary>
        public static Found? Find(VRCExpressionsMenu root, IList<Step> path)
        {
            var menu = root;
            Found? found = null;
            foreach (var step in path ?? Array.Empty<Step>())
            {
                var controls = menu?.controls;
                if (controls == null) return null;
                VRCExpressionsMenu.Control control;
                if (step.Next)
                {
                    control = controls.Count == MenuBuild.MaxControls ? controls[controls.Count - 1] : null;
                    if (control == null || control.type != ControlType.SubMenu || control.subMenu == null) return null;
                }
                else control = controls.Where(c => c != null && (c.name ?? "") == step.Name).Skip(step.Nth).FirstOrDefault();
                if (control == null) return null;
                found = new Found(menu, control);
                menu = control.type == ControlType.SubMenu ? control.subMenu : null;
            }
            return found;
        }

        /// <summary>Every menu reached from <paramref name="root"/>, once each.</summary>
        public static List<VRCExpressionsMenu> Menus(VRCExpressionsMenu root)
        {
            var list = new List<VRCExpressionsMenu>();
            var stack = new Stack<VRCExpressionsMenu>();
            if (root != null) stack.Push(root);
            var seen = new HashSet<VRCExpressionsMenu>();
            while (stack.Count > 0)
            {
                var menu = stack.Pop();
                if (!seen.Add(menu)) continue;
                list.Add(menu);
                foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
                    if (control != null && control.type == ControlType.SubMenu && control.subMenu != null) stack.Push(control.subMenu);
            }
            return list;
        }
    }
}
