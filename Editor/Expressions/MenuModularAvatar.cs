using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>A Modular Avatar Menu Installer: a menu (or the menu source on its object) added to the avatar's menu.</summary>
    internal sealed class MaInstaller
    {
        public Component Component;
        public bool Enabled;
        /// <summary>The menu it adds (when its object holds no menu source) and the menu it adds it to (null: the main menu).</summary>
        public VRCExpressionsMenu Menu, Target;
        public GameObject Object => Component != null ? Component.gameObject : null;
    }

    /// <summary>A component Modular Avatar takes menu items from: a Menu Item, a Menu Group or a Menu Install Target.</summary>
    internal abstract class MaSource
    {
        public Component Component;
        public GameObject Object => Component != null ? Component.gameObject : null;
    }

    internal sealed class MaItem : MaSource
    {
        public VRCExpressionsMenu.Control Control;
        /// <summary>Its name; empty means its object's name.</summary>
        public string Label;
        /// <summary>A submenu made of the menu sources under <see cref="ChildrenOf"/> (or its own object), not of a menu asset.</summary>
        public bool FromChildren;
        public GameObject ChildrenOf;
    }

    /// <summary>The menu sources under an object (its own, or <see cref="Target"/>), as items of the menu holding the group.</summary>
    internal sealed class MaGroup : MaSource
    {
        public GameObject Target;
    }

    /// <summary>Places an installer's items here instead of where the installer says.</summary>
    internal sealed class MaInstallTarget : MaSource
    {
        public MaInstaller Installer;
    }

    /// <summary>What Modular Avatar builds the menu from, read from its components (or made by tests).</summary>
    internal sealed class MaMenu
    {
        /// <summary>Every installer, in hierarchy order, enabled or not (Modular Avatar installs disabled ones too).</summary>
        public readonly List<MaInstaller> Installers = new List<MaInstaller>();
        public readonly List<MaInstallTarget> InstallTargets = new List<MaInstallTarget>();
        /// <summary>The first menu source on an object (null when none, or when the object is removed before the build).</summary>
        public Func<GameObject, MaSource> SourceOf = _ => null;

        /// <summary>Modular Avatar changes the menu only when an enabled installer exists.</summary>
        public bool Runs => Installers.Any(i => i.Enabled);
    }

    /// <summary>
    /// Modular Avatar's menu, worked out without building the avatar. Modular Avatar is MIT licensed; its rules
    /// (VirtualMenu, MenuInstallHook) are followed here, read through reflection so My Avatar never depends on it:
    /// <list type="bullet">
    /// <item>the main menu holds the descriptor's controls, then installers aimed at the descriptor's menu asset, then
    /// installers aimed at no menu; a menu asset holds its controls, then the installers aimed at it;</item>
    /// <item>an installer adds the menu source on its object (Menu Item, Menu Group, Install Target) or else its menu's
    /// controls; an installer an Install Target names is added only there;</item>
    /// <item>a Menu Item is one control named by its label or its object; its submenu is a menu asset or the menu sources of
    /// its (or another object's) children, each made once and shared, except that a menu asset reached through two enabled
    /// installers or two submenu Menu Items makes two menus (each renames its parameters);</item>
    /// <item>each menu of more than 8 items keeps 7 and moves the rest to a "More" submenu, again and again.</item>
    /// </list>
    /// NDMF runs it before VRCFury, which then starts from this menu.
    /// </summary>
    internal static class MaMenus
    {
        private const string Core = "nadena.dev.modular_avatar.core.";
        /// <summary>The platform name NDMF gives VRChat avatar builds (Platform Filter components name it).</summary>
        public const string VrchatPlatform = "nadena.dev.ndmf.vrchat.avatar3";
        private const string MoreIconPath = "Packages/nadena.dev.modular-avatar/Runtime/Icons/Icon_More_A.png";

        private static bool looked;
        private static Type installerType, itemType, groupType, targetType, sourceType, filterType;

        private static void Look()
        {
            if (looked) return;
            looked = true;
            // VrcFury.Find looks in every loaded assembly.
            installerType = VrcFury.Find(Core + "ModularAvatarMenuInstaller");
            itemType = VrcFury.Find(Core + "ModularAvatarMenuItem");
            groupType = VrcFury.Find(Core + "ModularAvatarMenuGroup");
            targetType = VrcFury.Find(Core + "ModularAvatarMenuInstallTarget");
            sourceType = VrcFury.Find(Core + "menu.MenuSource");
            filterType = VrcFury.Find(Core + "ModularAvatarPlatformFilter");
        }

        /// <summary>Modular Avatar is in the project: it then builds every avatar before VRCFury.</summary>
        public static bool Installed { get { Look(); return installerType != null; } }

        private static Texture2D moreIcon;
        public static Texture2D MoreIcon => moreIcon != null ? moreIcon : moreIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(MoreIconPath);

        /// <summary>The avatar's Modular Avatar menu components; null when Modular Avatar leaves the menu alone.</summary>
        public static MaMenu Read(GameObject root)
        {
            if (root == null || !Installed) return null;
            // Removed before Modular Avatar runs: EditorOnly objects (NDMF) and objects a Platform Filter leaves out of VRChat.
            var filtered = new HashSet<GameObject>();
            if (filterType != null)
                foreach (var group in root.GetComponentsInChildren(filterType, true).Where(f => f != null).GroupBy(f => f.gameObject))
                {
                    bool Exclude(Component f) => VrcFury.Field(f, "m_excludePlatform") as bool? ?? true;
                    bool Here(Component f) => (VrcFury.Field(f, "m_platform") as string) == VrchatPlatform;
                    bool hasIncludes = group.Any(f => !Exclude(f));
                    if ((hasIncludes && !group.Any(f => Here(f) && !Exclude(f))) || group.Any(f => Here(f) && Exclude(f))) filtered.Add(group.Key);
                }
            var removed = new Dictionary<Transform, bool>();
            bool Removed(Transform t)
            {
                if (t == null) return false;
                if (removed.TryGetValue(t, out bool known)) return known;
                bool result = t.CompareTag("EditorOnly") || filtered.Contains(t.gameObject) || (t != root.transform && Removed(t.parent));
                removed[t] = result;
                return result;
            }

            var ma = new MaMenu();
            var installers = new Dictionary<Component, MaInstaller>();
            foreach (var c in root.GetComponentsInChildren(installerType, true))
            {
                if (c == null || Removed(c.transform)) continue;
                var installer = new MaInstaller
                {
                    Component = c, Enabled = c is Behaviour behaviour && behaviour.enabled,
                    Menu = VrcFury.Field(c, "menuToAppend") as VRCExpressionsMenu, Target = VrcFury.Field(c, "installTargetMenu") as VRCExpressionsMenu,
                };
                installers[c] = installer;
                ma.Installers.Add(installer);
            }
            if (!ma.Runs) return null;

            var targets = new Dictionary<Component, MaInstallTarget>();
            MaInstallTarget Target(Component c)
            {
                if (targets.TryGetValue(c, out var done)) return done;
                var named = VrcFury.Field(c, "installer") as Component;
                return targets[c] = new MaInstallTarget { Component = c, Installer = named != null && installers.TryGetValue(named, out var installer) ? installer : null };
            }
            if (targetType != null)
                foreach (var c in root.GetComponentsInChildren(targetType, true))
                    if (c != null && !Removed(c.transform)) ma.InstallTargets.Add(Target(c));

            var sources = new Dictionary<GameObject, MaSource>();
            GameObject Kept(object value) => value is GameObject go && go != null && !Removed(go.transform) ? go : null;
            ma.SourceOf = go =>
            {
                if (go == null || sourceType == null || Removed(go.transform)) return null;
                if (sources.TryGetValue(go, out var done)) return done;
                var c = go.GetComponent(sourceType);
                MaSource source = null;
                if (c == null) { }
                else if (itemType != null && itemType.IsInstanceOfType(c))
                {
                    var from = VrcFury.Field(c, "MenuSource");
                    source = new MaItem
                    {
                        Component = c, Control = VrcFury.Field(c, "Control") as VRCExpressionsMenu.Control, Label = VrcFury.Field(c, "label") as string,
                        FromChildren = from != null && System.Convert.ToInt32(from) == 1, ChildrenOf = Kept(VrcFury.Field(c, "menuSource_otherObjectChildren")),
                    };
                }
                else if (groupType != null && groupType.IsInstanceOfType(c)) source = new MaGroup { Component = c, Target = Kept(VrcFury.Field(c, "targetObject")) };
                else if (targetType != null && targetType.IsInstanceOfType(c)) source = Target(c);
                return sources[go] = source;
            };
            return ma;
        }

        /// <summary>The menu Modular Avatar makes from <paramref name="rootMenu"/> (the descriptor's); null when it changes nothing.</summary>
        public static MenuPage Build(MaMenu ma, VRCExpressionsMenu rootMenu, MenuBuilder builder) =>
            ma == null || !ma.Runs ? null : new Run(ma, rootMenu, builder).Build();

        private sealed class Run
        {
            // One menu of Modular Avatar's: the main menu, a menu asset's, or the menu sources under an object.
            private sealed class Node
            {
                public string Key;
                public MenuPage Page;
            }

            // What a menu is being filled from: a source already being added is skipped (loops end there). The scope is the
            // installer or Menu Item whose parameter renaming applies (null: none): Modular Avatar keys a menu asset's menu by
            // asset and scope, so an asset reached through two of them makes two menus.
            private sealed class Context
            {
                public Node Node;
                public object Scope;
                public readonly HashSet<object> Visiting = new HashSet<object>();
            }

            private static readonly object Untargeted = new object();
            private readonly MaMenu ma;
            private readonly VRCExpressionsMenu rootMenu;
            private readonly MenuBuilder builder;
            private readonly Dictionary<object, List<MaInstaller>> installed = new Dictionary<object, List<MaInstaller>>();
            private readonly Dictionary<object, Node> nodes = new Dictionary<object, Node>();
            private readonly Queue<Action> pending = new Queue<Action>();
            private readonly HashSet<VRCExpressionsMenu> avatarMenus = new HashSet<VRCExpressionsMenu>();
            private readonly Dictionary<VRCExpressionsMenu, Component> broughtBy = new Dictionary<VRCExpressionsMenu, Component>();

            public Run(MaMenu ma, VRCExpressionsMenu rootMenu, MenuBuilder builder)
            {
                this.ma = ma;
                this.rootMenu = rootMenu;
                this.builder = builder;
            }

            public MenuPage Build()
            {
                Collect(rootMenu, avatarMenus, null);
                foreach (var installer in ma.Installers) Collect(installer.Menu, null, installer.Component);
                var placed = new HashSet<MaInstaller>(ma.InstallTargets.Where(t => t.Installer != null).Select(t => t.Installer));
                foreach (var installer in ma.Installers)
                {
                    if ((installer.Menu == null && ma.SourceOf(installer.Object) == null) || placed.Contains(installer)) continue;
                    object key = installer.Target != null ? installer.Target : Untargeted;
                    if (!installed.TryGetValue(key, out var list)) installed[key] = list = new List<MaInstaller>();
                    list.Add(installer);
                }

                var root = new Node { Key = "root", Page = new MenuPage { Name = rootMenu != null ? rootMenu.name : "" } };
                if (rootMenu != null) nodes[(rootMenu, (object)null)] = root;
                var context = new Context { Node = root };
                if (rootMenu != null)
                {
                    var controls = rootMenu.controls ?? new List<VRCExpressionsMenu.Control>();
                    for (int i = 0; i < controls.Count; i++)
                        if (controls[i] != null) PushControl(context, rootMenu, i, controls[i]);
                    if (installed.TryGetValue(rootMenu, out var atRoot)) foreach (var installer in atRoot) PushInstaller(context, installer);
                }
                if (installed.TryGetValue(Untargeted, out var untargeted)) foreach (var installer in untargeted) PushInstaller(context, installer);
                while (pending.Count > 0) pending.Dequeue()();

                foreach (var node in new[] { root }.Concat(nodes.Values).Distinct().ToList()) Split(node);
                return root.Page;
            }

            // The menus of a tree: the avatar's own, or (with an owner) those an installer brings.
            private void Collect(VRCExpressionsMenu menu, HashSet<VRCExpressionsMenu> into, Component owner)
            {
                var seen = new HashSet<VRCExpressionsMenu>();
                var stack = new Stack<VRCExpressionsMenu>();
                if (menu != null) stack.Push(menu);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!seen.Add(current)) continue;
                    if (into != null) into.Add(current);
                    else if (!avatarMenus.Contains(current) && !broughtBy.ContainsKey(current)) broughtBy[current] = owner;
                    foreach (var control in current.controls ?? new List<VRCExpressionsMenu.Control>())
                        if (control != null && control.type == ControlType.SubMenu && control.subMenu != null) stack.Push(control.subMenu);
                }
            }

            private void PushControl(Context context, VRCExpressionsMenu asset, int index, VRCExpressionsMenu.Control control)
            {
                bool own = avatarMenus.Contains(asset);
                broughtBy.TryGetValue(asset, out var owner);
                var node = builder.Item(asset, index, control, own ? "a" : "ma", own ? MenuOriginKind.AvatarMenu : MenuOriginKind.MaMenu, own ? null : owner);
                if (control.type == ControlType.SubMenu && control.subMenu != null) node.Page = NodeFor(control.subMenu, context.Scope)?.Page;
                context.Node.Page.Items.Add(node);
            }

            private void PushMenuContents(Context context, VRCExpressionsMenu menu)
            {
                if (menu == null || !context.Visiting.Add(menu)) return;
                try
                {
                    var controls = menu.controls ?? new List<VRCExpressionsMenu.Control>();
                    for (int i = 0; i < controls.Count; i++)
                        if (controls[i] != null) PushControl(context, menu, i, controls[i]);
                    if (installed.TryGetValue(menu, out var list)) foreach (var installer in list) PushInstaller(context, installer);
                }
                finally { context.Visiting.Remove(menu); }
            }

            private void PushInstaller(Context context, MaInstaller installer)
            {
                if (installer == null || installer.Component == null || !context.Visiting.Add(installer)) return;
                var scope = context.Scope;
                // Modular Avatar renames the parameters of an enabled installer's menu (its own scope).
                if (installer.Enabled && installer.Menu != null) context.Scope = installer;
                try
                {
                    var source = ma.SourceOf(installer.Object);
                    if (source != null) PushSource(context, source);
                    else if (installer.Menu != null) PushMenuContents(context, installer.Menu);
                }
                finally { context.Visiting.Remove(installer); context.Scope = scope; }
            }

            private void PushSource(Context context, MaSource source)
            {
                if (source == null || source.Component == null || !context.Visiting.Add(source)) return;
                var scope = context.Scope;
                // …and of a submenu Menu Item's.
                if (source is MaItem submenu && submenu.Control != null && submenu.Control.type == ControlType.SubMenu) context.Scope = submenu;
                try
                {
                    switch (source)
                    {
                        case MaItem item: PushItem(context, item); break;
                        case MaGroup group: PushChildren(context, group.Target != null ? group.Target : group.Object); break;
                        case MaInstallTarget target: PushInstaller(context, target.Installer); break;
                    }
                }
                finally { context.Visiting.Remove(source); context.Scope = scope; }
            }

            private void PushChildren(Context context, GameObject parent)
            {
                if (parent == null || !context.Visiting.Add(parent)) return;
                try
                {
                    foreach (Transform child in parent.transform)
                    {
                        var source = ma.SourceOf(child.gameObject);
                        if (source != null) PushSource(context, source);
                    }
                }
                finally { context.Visiting.Remove(parent); }
            }

            private void PushItem(Context context, MaItem item)
            {
                var control = item.Control ?? new VRCExpressionsMenu.Control();
                var type = Enum.IsDefined(typeof(ControlType), control.type) ? control.type : ControlType.Button;
                int keep = type == ControlType.RadialPuppet ? 1 : type == ControlType.TwoAxisPuppet ? 2 : type == ControlType.FourAxisPuppet ? 4 : 0;
                var node = new MenuNode
                {
                    Id = "mi:" + item.Component.GetInstanceID(), Name = string.IsNullOrEmpty(item.Label) ? item.Object.name : item.Label, Type = type,
                    Icon = control.icon, Parameter = control.parameter?.name, Value = control.value,
                    SubParameters = (control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>()).Take(keep).Select(p => p?.name ?? "").ToArray(),
                    Labels = control.labels,
                    Origin = new MenuOrigin { Kind = MenuOriginKind.MaItem, Component = item.Component },
                };
                if (control.type == ControlType.SubMenu)
                {
                    if (item.FromChildren) node.Page = NodeFor(item.ChildrenOf != null ? item.ChildrenOf : item.Object, context.Scope)?.Page;
                    else if (control.subMenu != null)
                    {
                        Collect(control.subMenu, null, item.Component);
                        node.Page = NodeFor(control.subMenu, context.Scope)?.Page;
                    }
                }
                context.Node.Page.Items.Add(node);
            }

            // Each menu asset (in each scope), and the children of each object, make one menu, filled once every earlier one is
            // and in the scope it was first asked for in.
            private Node NodeFor(UnityEngine.Object source, object scope)
            {
                if (source == null) return null;
                var menu = source as VRCExpressionsMenu;
                var parent = source as GameObject;
                object key = menu != null ? (object)(menu, scope) : parent;
                if (nodes.TryGetValue(key, out var node)) return node;
                node = new Node
                {
                    Key = menu != null ? MenuInputs.Key(menu) + (scope is MaInstaller i ? "@" + i.Component.GetInstanceID() : scope is MaItem m ? "@" + m.Component.GetInstanceID() : "") : "g" + parent.GetInstanceID(),
                    Page = new MenuPage { Name = menu != null ? menu.name : parent.name },
                };
                nodes[key] = node;
                pending.Enqueue(() =>
                {
                    var context = new Context { Node = node, Scope = scope };
                    if (menu != null) PushMenuContents(context, menu);
                    else PushChildren(context, parent);
                });
                return node;
            }

            private static void Split(Node node)
            {
                var current = node.Page;
                int count = 1;
                const int keep = MenuBuild.MaxControls - 1;
                while (current.Items.Count > MenuBuild.MaxControls)
                {
                    var more = new MenuPage { Name = node.Page.Name };
                    more.Items.AddRange(current.Items.Skip(keep));
                    current.Items.RemoveRange(keep, current.Items.Count - keep);
                    current.Items.Add(new MenuNode
                    {
                        Id = "more:" + node.Key + ":" + count++, Name = "More", Type = ControlType.SubMenu, Icon = MoreIcon, Page = more,
                        Origin = new MenuOrigin { Kind = MenuOriginKind.MaMore },
                    });
                    current = more;
                }
            }
        }
    }
}
