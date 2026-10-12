using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// The menu at upload, on the build copy only:
    /// <list type="bullet">
    /// <item>items deleted in the menu editor, the avatar's own menu when "Remove default menu" is on, and the toggles of
    /// looks hidden in My Avatar (Rexouium's feathers, Novabeast's parts) leave the menu, with what only they drive
    /// (<see cref="ParameterFreeze"/>);</item>
    /// <item>Orbiters' items without an icon get Orbiters' default ones, and "Next" (or "More") pages the arrow.</item>
    /// </list>
    /// What to change is worked out before VRCFury builds (where each item is, with the menu the editor shows); it is done
    /// after, in VRCFury's copies of the menu, parameters and controllers.
    /// </summary>
    internal static class MenuUploadBuild
    {
        private sealed class Plan
        {
            public readonly List<(List<string> Names, List<int> Nth)> Remove = new List<(List<string>, List<int>)>();
            /// <summary>Parameters whose every menu control goes (looks hidden for good).</summary>
            public readonly HashSet<string> Parameters = new HashSet<string>(StringComparer.Ordinal);
            public readonly List<(List<string> Names, List<int> Nth, Texture2D Icon)> Icons = new List<(List<string>, List<int>, Texture2D)>();
            public readonly HashSet<string> NextNames = new HashSet<string>(StringComparer.Ordinal) { "Next", "More" };
            public Texture2D NextIcon;
            public bool Empty => Remove.Count == 0 && Parameters.Count == 0 && Icons.Count == 0 && NextIcon == null;
        }

        private static readonly ConditionalWeakTable<GameObject, Plan> Plans = new ConditionalWeakTable<GameObject, Plan>();

        // After Modular Avatar (-11000) and My Avatar's face tracking capture, before VRCFury (-10000).
        internal sealed class Capture : IVRCSDKPreprocessAvatarCallback
        {
            public int callbackOrder => -10045;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                Plans.Remove(avatar);
                try
                {
                    var plan = PlanFor(avatar);
                    if (plan != null && !plan.Empty) Plans.Add(avatar, plan);
                }
                catch (Exception ex) { Debug.LogException(ex); Debug.LogWarning("[My Avatar] Menu: the upload keeps the menu as it is (" + ex.Message + ")."); }
                return true;
            }
        }

        // After VRCFury and My Avatar's face tracking (-9010), before MCB's corrective links (-9000).
        internal sealed class Apply : IVRCSDKPreprocessAvatarCallback
        {
            public int callbackOrder => -9005;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                if (!Plans.TryGetValue(avatar, out var plan)) return true;
                Plans.Remove(avatar);
                try { Debug.Log("[My Avatar] Menu: " + Run(avatar, plan)); }
                // What was done stays (each step leaves the avatar whole); the rest is uploaded as VRCFury built it.
                catch (Exception ex) { Debug.LogException(ex); Debug.LogWarning("[My Avatar] Menu: the upload goes on without the rest of the menu clean-up (" + ex.Message + ")."); }
                return true;
            }
        }

        // ---- Before VRCFury ----

        private static Plan PlanFor(GameObject avatar)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return null;
            var settings = avatar.GetComponentInChildren<MyAvatar>(true);
            var inputs = MenuInputs.Read(avatar);
            var built = MenuBuild.Run(inputs);
            var plan = new Plan();
            plan.NextNames.Add(built.NextText);
            foreach (var (names, nth) in Removals(settings, built))
                plan.Remove.Add((names, nth));
            plan.Parameters.UnionWith(HiddenLooks(avatar));
            // Icons: Orbiters' items without one, and the "Next" pages unless the avatar gives them one.
            foreach (var entry in MenuTree.Walk(built.Root))
            {
                var icon = MenuDefaults.Icon(entry.Node, built.Root);
                if (icon == null) continue;
                var names = new List<string>(); var nth = new List<int>();
                if (MenuPaths.Of(built.Root, entry.Node, names, nth)) plan.Icons.Add((names, nth, icon));
            }
            if (built.NextIcon == null) plan.NextIcon = OrbitersMenuIcons.Next;
            return plan;
        }

        /// <summary>
        /// The items an upload leaves out of <paramref name="built"/>: those deleted in the menu editor, and with "Remove
        /// default menu" the avatar's own items (its folders too, unless they hold others).
        /// </summary>
        internal static List<(List<string> Names, List<int> Nth)> Removals(MyAvatar settings, MenuBuildResult built)
        {
            var list = new List<(List<string>, List<int>)>();
            if (settings == null) return list;
            foreach (var removal in settings.menuRemovals)
                if (removal != null && MenuPaths.Find(built.Root, removal.path, removal.nth) != null) list.Add((removal.path.ToList(), removal.nth.ToList()));
            if (settings.removeDefaultMenu)
                foreach (var node in DefaultItems(built.Root))
                {
                    var names = new List<string>(); var nth = new List<int>();
                    if (MenuPaths.Of(built.Root, node, names, nth)) list.Add((names, nth));
                }
            return list;
        }

        /// <summary>
        /// Looks hidden in My Avatar stay hidden: the parameters of their toggles (Rexouium's feathers, Novabeast's parts),
        /// whose every menu control leaves the upload with what only shows them.
        /// </summary>
        internal static IEnumerable<string> HiddenLooks(GameObject avatar)
        {
            var options = RexouiumOptions.Find(avatar);
            foreach (var group in options.Feathers.Where(g => !RexouiumOptions.Shown(options, g))) yield return group.Parameter;
            var nova = NovabeastOptions.Find(avatar);
            foreach (var part in nova.Parts.Where(p => !NovabeastOptions.Shown(nova, p))) yield return part.Parameter;
        }

        /// <summary>The avatar's own items (its descriptor's menu): whole folders when they hold nothing else.</summary>
        internal static List<MenuNode> DefaultItems(MenuPage root)
        {
            var list = new List<MenuNode>();
            var seen = new HashSet<MenuPage>();
            bool AllDefault(MenuNode node, HashSet<MenuPage> visiting)
            {
                if (node.Origin?.Kind != MenuOriginKind.AvatarMenu) return false;
                if (!node.IsFolder || node.Page == null) return true;
                if (!visiting.Add(node.Page)) return true;
                return node.Page.Items.All(n => AllDefault(n, visiting));
            }
            void Walk(MenuPage page)
            {
                if (page == null || !seen.Add(page)) return;
                foreach (var node in page.Items)
                {
                    if (AllDefault(node, new HashSet<MenuPage>())) list.Add(node);
                    else if (node.IsFolder) Walk(node.Page);
                }
            }
            Walk(root);
            return list;
        }

        /// <summary>Both steps at once, on an avatar no other tool builds (tests).</summary>
        internal static string Upload(GameObject avatar)
        {
            var plan = PlanFor(avatar);
            return plan == null || plan.Empty ? "" : Run(avatar, plan);
        }

        // ---- After VRCFury ----

        private static string Run(GameObject avatar, Plan plan)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return "the avatar has no descriptor.";
            var menu = descriptor.expressionsMenu != null ? Editable(avatar, descriptor) : null;
            // Everything is found first: taking controls out shifts the pages after them.
            var gone = new List<MenuPaths.Found>();
            int missing = 0;
            foreach (var (names, nth) in plan.Remove)
            {
                var found = MenuPaths.Find(menu, names, nth, plan.NextNames);
                if (found == null) missing++; else gone.Add(found.Value);
            }
            foreach (var found in MenuPaths.Menus(menu).SelectMany(m => (m.controls ?? new List<VRCExpressionsMenu.Control>()).Select(c => new MenuPaths.Found(m, c))))
                if (found.Control != null && found.Control.type != ControlType.SubMenu && found.Control.parameter != null && plan.Parameters.Contains(found.Control.parameter.name))
                    gone.Add(found);
            var icons = new List<(VRCExpressionsMenu.Control Control, Texture2D Icon)>();
            foreach (var (names, nth, icon) in plan.Icons)
            {
                var found = MenuPaths.Find(menu, names, nth, plan.NextNames);
                if (found != null && found.Value.Control.icon == null) icons.Add((found.Value.Control, icon));
            }
            var nexts = plan.NextIcon == null ? new List<VRCExpressionsMenu.Control>()
                : MenuPaths.Menus(menu).Select(m => m.controls?.LastOrDefault(c => MenuPaths.IsNext(m, c, plan.NextNames))).Where(c => c != null && c.icon == null).ToList();
            var chains = Chains(menu, plan.NextNames);

            // The parameters of what goes, folders' contents included.
            var parameters = new List<string>(plan.Parameters);
            int controls = 0;
            foreach (var found in gone.Distinct())
            {
                foreach (var control in Inside(found.Control))
                {
                    if (control.parameter != null && !string.IsNullOrEmpty(control.parameter.name)) parameters.Add(control.parameter.name);
                    foreach (var sub in control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
                        if (sub != null && !string.IsNullOrEmpty(sub.name)) parameters.Add(sub.name);
                }
                if (found.Menu.controls.Remove(found.Control)) { controls++; EditorUtility.SetDirty(found.Menu); }
            }
            // VRCFury split its pages before: they are made again as it makes them, without the gaps.
            if (controls > 0) Repaginate(chains);
            int emptied = RemoveEmptyFolders(menu);
            foreach (var (control, icon) in icons) control.icon = icon;
            foreach (var next in nexts) next.icon = plan.NextIcon;
            foreach (var m in MenuPaths.Menus(menu)) EditorUtility.SetDirty(m);

            string freeze = "";
            if (controls > 0 || plan.Parameters.Count > 0)
            {
                Controllers(avatar, descriptor);
                Parameters(avatar, descriptor);
                freeze = " " + new ParameterFreeze(avatar, AttachmentAnimationBuild.IsBuildData).Run(parameters);
            }
            return $"{controls} items left out of the upload ({emptied} folders emptied{(missing > 0 ? ", " + missing + " deleted items no longer found" : "")}), " +
                   $"{icons.Count + nexts.Count} default icons.{freeze}";
        }

        // A control and, for a folder, everything in it.
        private static IEnumerable<VRCExpressionsMenu.Control> Inside(VRCExpressionsMenu.Control control)
        {
            yield return control;
            if (control.type != ControlType.SubMenu || control.subMenu == null) yield break;
            foreach (var menu in MenuPaths.Menus(control.subMenu))
                foreach (var c in menu.controls ?? new List<VRCExpressionsMenu.Control>())
                    if (c != null) yield return c;
        }

        // The pages of each menu VRCFury split, each with its "Next" item (null on the last page): found while the pages are
        // still full, before anything is taken out of them.
        private static List<List<(VRCExpressionsMenu Menu, VRCExpressionsMenu.Control Next)>> Chains(VRCExpressionsMenu root, ICollection<string> nextNames)
        {
            var chains = new List<List<(VRCExpressionsMenu, VRCExpressionsMenu.Control)>>();
            if (root == null) return chains;
            var menus = MenuPaths.Menus(root);
            var continued = new HashSet<VRCExpressionsMenu>(menus.Select(m => m.controls?.LastOrDefault(c => MenuPaths.IsNext(m, c, nextNames))?.subMenu).Where(m => m != null));
            foreach (var head in menus.Where(m => !continued.Contains(m)))
            {
                var chain = new List<(VRCExpressionsMenu, VRCExpressionsMenu.Control)>();
                var seen = new HashSet<VRCExpressionsMenu>();
                for (var page = head; page != null && seen.Add(page);)
                {
                    var next = page.controls?.LastOrDefault(c => MenuPaths.IsNext(page, c, nextNames));
                    chain.Add((page, next));
                    page = next?.subMenu;
                }
                if (chain.Count > 1) chains.Add(chain);
            }
            return chains;
        }

        // A split menu's items over its pages again: seven and "Next" on each page but the last, which holds up to eight.
        private static void Repaginate(List<List<(VRCExpressionsMenu Menu, VRCExpressionsMenu.Control Next)>> chains)
        {
            foreach (var chain in chains)
            {
                var items = chain.SelectMany(p => p.Menu.controls.Where(c => c != p.Next)).ToList();
                int pages = items.Count <= MenuBuild.MaxControls ? 1 : Mathf.CeilToInt((items.Count - 1) / (float)(MenuBuild.MaxControls - 1));
                pages = Mathf.Min(pages, chain.Count);
                int at = 0;
                for (int i = 0; i < pages; i++)
                {
                    bool last = i == pages - 1;
                    int take = last ? items.Count - at : MenuBuild.MaxControls - 1;
                    var controls = items.Skip(at).Take(take).ToList();
                    at += take;
                    if (!last) controls.Add(chain[i].Next);
                    chain[i].Menu.controls = controls;
                    EditorUtility.SetDirty(chain[i].Menu);
                }
            }
        }

        // Folders left empty go (VRChat would show them empty).
        private static int RemoveEmptyFolders(VRCExpressionsMenu root)
        {
            int count = 0;
            for (bool again = true; again;)
            {
                again = false;
                foreach (var menu in MenuPaths.Menus(root))
                {
                    // A folder setting a parameter while open still does something.
                    int n = menu.controls.RemoveAll(c => c != null && c.type == ControlType.SubMenu && c.subMenu != null &&
                                                         (c.subMenu.controls == null || c.subMenu.controls.Count == 0) && string.IsNullOrEmpty(c.parameter?.name));
                    if (n > 0) { count += n; again = true; EditorUtility.SetDirty(menu); }
                }
            }
            return count;
        }

        // ---- The build's own copies ----

        // VRCFury's working copy of the menu, or a copy made here (the avatar's own menu is never edited).
        private static VRCExpressionsMenu Editable(GameObject avatar, VRCAvatarDescriptor descriptor)
        {
            var menu = descriptor.expressionsMenu;
            if (MenuPaths.Menus(menu).All(AttachmentAnimationBuild.IsBuildData)) return menu;
            var copies = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();
            VRCExpressionsMenu Copy(VRCExpressionsMenu source)
            {
                if (source == null) return null;
                if (copies.TryGetValue(source, out var done)) return done;
                var copy = Object.Instantiate(source);
                copy.name = source.name;
                copies[source] = copy;
                copy.controls = (source.controls ?? new List<VRCExpressionsMenu.Control>()).Select(c => c == null ? null : new VRCExpressionsMenu.Control
                {
                    name = c.name, icon = c.icon, type = c.type, parameter = c.parameter == null ? null : new VRCExpressionsMenu.Control.Parameter { name = c.parameter.name },
                    value = c.value, style = c.style, labels = c.labels,
                    subParameters = c.subParameters?.Select(p => p == null ? null : new VRCExpressionsMenu.Control.Parameter { name = p.name }).ToArray(),
                }).ToList();
                for (int i = 0; i < copy.controls.Count; i++)
                    if (copy.controls[i] != null && copy.controls[i].type == ControlType.SubMenu) copy.controls[i].subMenu = Copy(source.controls[i].subMenu);
                return copy;
            }
            descriptor.expressionsMenu = Copy(menu);
            AttachmentAnimationBuild.Keep(avatar, copies.Values.Cast<Object>().ToList());
            return descriptor.expressionsMenu;
        }

        private static void Parameters(GameObject avatar, VRCAvatarDescriptor descriptor)
        {
            var parameters = descriptor.expressionParameters;
            if (parameters == null || AttachmentAnimationBuild.IsBuildData(parameters)) return;
            var copy = Object.Instantiate(parameters);
            copy.name = parameters.name;
            descriptor.expressionParameters = copy;
            AttachmentAnimationBuild.Keep(avatar, new Object[] { copy });
        }

        // The playable layers as build copies (VRCFury's, else cloned for this build).
        private static void Controllers(GameObject avatar, VRCAvatarDescriptor descriptor)
        {
            bool own = (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Any(l => !l.isDefault && l.animatorController is AnimatorController c && !AttachmentAnimationBuild.IsBuildData(c));
            if (own) AttachmentAnimationBuild.Prepare(avatar);
        }
    }

    /// <summary>
    /// The icons an item gets when it has none: Orbiters' white dot for its toggles and buttons, its gauge for its sliders,
    /// its folder for its submenus (and for folders VRCFury makes for their menu paths), and an arrow for the "Next" and
    /// "More" pages.
    /// </summary>
    internal static class MenuDefaults
    {
        public const string McbSliders = "mcb sliders";

        /// <summary>The icon <paramref name="node"/> gets at upload, or null when it keeps its own (or VRChat's).</summary>
        public static Texture2D Icon(MenuNode node, MenuPage root)
        {
            if (node == null || node.Icon != null) return null;
            var kind = node.Origin?.Kind;
            if (kind == MenuOriginKind.Next || kind == MenuOriginKind.MaMore) return null;
            if (kind == MenuOriginKind.Folder)
                return node.Page != null && node.Page.Items.Count > 0 && node.Page.Items.All(n => Orbiters(n) || (n.IsFolder && n.Origin?.Kind == MenuOriginKind.Folder))
                    ? OrbitersMenuIcons.Submenu : null;
            if (!Orbiters(node)) return null;
            return node.IsFolder ? OrbitersMenuIcons.Submenu : node.Type == ControlType.RadialPuppet ? OrbitersMenuIcons.Slider : OrbitersMenuIcons.Toggle;
        }

        /// <summary>
        /// Whether an Orbiters tool made <paramref name="node"/>: a prop's menu, an accessory's toggle (Toolkit's components
        /// mark them), MCB's sliders. Packages My Avatar sets up (face tracking, GoGoLoco) keep their authors' icons.
        /// </summary>
        public static bool Orbiters(MenuNode node)
        {
            var origin = node?.Origin;
            if (origin == null) return false;
            if (origin.Asset != null)
            {
                string path = AssetDatabase.GetAssetPath(origin.Asset);
                if (path.StartsWith("Assets/Orbiters/", StringComparison.Ordinal) || path.StartsWith("Packages/orbiters.", StringComparison.Ordinal)) return true;
            }
            for (var t = origin.Component != null ? origin.Component.transform : null; t != null; t = t.parent)
            {
                if (t.name == McbSliders) return true;
                if (t.GetComponents<MonoBehaviour>().Any(b => b != null && (b.GetType().Namespace ?? "").StartsWith("Orbiters.Toolkit", StringComparison.Ordinal))) return true;
                if (t.GetComponent<VRCAvatarDescriptor>() != null) break;
            }
            return false;
        }
    }
}
