using System;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// Where an item is in the menu VRChat shows: the names of the folders down to it and its own, each with which item of
    /// that name it is in its menu. The same in the editor's menu (before VRCFury splits pages) and in the built menu assets,
    /// whose "Next" (or "More") pages are followed as the rest of their menu.
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

        /// <summary>A control of a built menu, and the menu asset holding it.</summary>
        public readonly struct Found
        {
            public readonly VRCExpressionsMenu Menu;
            public readonly VRCExpressionsMenu.Control Control;
            public Found(VRCExpressionsMenu menu, VRCExpressionsMenu.Control control) { Menu = menu; Control = control; }
        }

        /// <summary>
        /// The control at a path in a built menu, following "Next" pages: a full menu's last item, a submenu named as
        /// <paramref name="nextNames"/> (VRCFury's text, Modular Avatar's "More").
        /// </summary>
        public static Found? Find(VRCExpressionsMenu root, IList<string> names, IList<int> nth, ICollection<string> nextNames)
        {
            var menu = root;
            Found? found = null;
            for (int i = 0; i < names.Count; i++)
            {
                if (menu == null) return null;
                var match = Items(menu, nextNames).Where(f => (f.Control.name ?? "") == names[i]).Skip(i < nth.Count ? nth[i] : 0).ToList();
                if (match.Count == 0) return null;
                found = match[0];
                menu = found.Value.Control.type == ControlType.SubMenu ? found.Value.Control.subMenu : null;
            }
            return found;
        }

        /// <summary>A menu's controls as VRChat shows them over its pages: the "Next" items stand for the controls they open.</summary>
        public static IEnumerable<Found> Items(VRCExpressionsMenu menu, ICollection<string> nextNames)
        {
            var seen = new HashSet<VRCExpressionsMenu>();
            while (menu != null && menu.controls != null && seen.Add(menu))
            {
                var controls = menu.controls;
                VRCExpressionsMenu next = null;
                for (int i = 0; i < controls.Count; i++)
                {
                    var control = controls[i];
                    if (control == null) continue;
                    if (i == controls.Count - 1 && IsNext(menu, control, nextNames)) { next = control.subMenu; break; }
                    yield return new Found(menu, control);
                }
                menu = next;
            }
        }

        /// <summary>Whether <paramref name="control"/> is the "Next" (or "More") item of a full menu.</summary>
        public static bool IsNext(VRCExpressionsMenu menu, VRCExpressionsMenu.Control control, ICollection<string> nextNames) =>
            control != null && control.type == ControlType.SubMenu && control.subMenu != null && menu.controls.Count == MenuBuild.MaxControls &&
            menu.controls[menu.controls.Count - 1] == control && nextNames.Any(n => MenuRules.Normalize(n) == MenuRules.Normalize(control.name));

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
