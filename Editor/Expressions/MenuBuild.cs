using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>The avatar's menu as VRChat will show it, before VRCFury splits pages of more than 8 items.</summary>
    internal sealed class MenuBuildResult
    {
        public MenuPage Root = new MenuPage();
        /// <summary>VRCFury builds this avatar: it merges, moves and pages its menu.</summary>
        public bool VrcFury;
        /// <summary>Modular Avatar builds the menu first (installers add to it, menus of more than 8 items get "More").</summary>
        public bool ModularAvatar;
        public string NextText = "Next";
        public Texture2D NextIcon;
        /// <summary>The id given to each control of the avatar's menu assets (to keep them while saving moves them).</summary>
        public readonly Dictionary<VRCExpressionsMenu.Control, string> ControlIds = new Dictionary<VRCExpressionsMenu.Control, string>();
        /// <summary>What would make VRCFury's build fail (a Move Menu Item putting a menu inside itself).</summary>
        public readonly List<string> Errors = new List<string>();
    }

    /// <summary>Points of a build where <see cref="MenuBuildOptions.Inspect"/> is called with the menu as it is then.</summary>
    internal enum MenuBuildStage
    {
        /// <summary>Every item is created, nothing is moved yet.</summary>
        BeforeMoves,
        /// <summary>The avatar's own Move Menu Item features ran; the extra ones did not.</summary>
        BeforeExtraMoves,
        /// <summary>The menu is sorted and the avatar's own Reorder Menu Item features ran; the extra ones did not.</summary>
        BeforeExtraReorders,
    }

    internal sealed class MenuBuildOptions
    {
        /// <summary>Features run after all of the avatar's own (My Avatar's saved edits, as they would be saved).</summary>
        public IReadOnlyList<MenuSpec> Extra;
        /// <summary>Ids to keep for controls of the avatar's menu assets that saving moved or copied.</summary>
        public IReadOnlyDictionary<VRCExpressionsMenu.Control, string> Pins;
        public Action<MenuBuildStage, MenuBuilder> Inspect;
    }

    /// <summary>
    /// Works out the menu VRChat will show for an avatar, instantly and without building it: the descriptor's menu, then what
    /// VRCFury's build does to it. The rules follow VRCFury's menu building (written for My Avatar from its observable
    /// behaviour, then checked against VRCFury's real build in the tests): Full Controller menus merge first, then Toggles,
    /// Puppets and Gesture locks create their items in component order, creating folders for their paths; Move Menu Item
    /// and Set Icon run next, then every menu is sorted by the feature that created its items, Reorder Menu Item runs, and
    /// menus of more than 8 items get a "Next" page (<see cref="Paginate"/>).
    /// </summary>
    internal static class MenuBuild
    {
        public const int MaxControls = 8;
        private const string SpsIconPath = "Packages/com.vrcfury.vrcfury/VrcfResources/sps_icon.png", SpsOptionsIconGuid = "16e0846165acaa1429417e757c53ef9b";

        public static MenuBuildResult Run(MenuInputs inputs, MenuBuildOptions options = null)
        {
            options ??= new MenuBuildOptions();
            var result = new MenuBuildResult { VrcFury = inputs.UsesVrcFury || (options.Extra != null && options.Extra.Count > 0) };
            var builder = new MenuBuilder(result, options.Pins);
            // Modular Avatar builds first (NDMF runs before VRCFury): its menu replaces the descriptor's.
            var modular = MaMenus.Build(inputs.ModularAvatar, inputs.Menu, builder);
            result.ModularAvatar = modular != null;
            if (!result.VrcFury)
            {
                // Without VRCFury, VRChat shows the descriptor's menu (or Modular Avatar's) exactly as it is.
                if (modular != null) result.Root = modular;
                else if (inputs.Menu != null) result.Root = builder.Convert(inputs.Menu, "a", out _, avatarMenu: true);
                builder.FinishIds();
                return result;
            }

            var specs = inputs.Specs.Concat(options.Extra ?? Array.Empty<MenuSpec>()).ToList();
            // VRCFury starts from the descriptor's menu, merged into a new one: its items sort first.
            builder.Current = 0;
            if (modular != null) builder.MergePage(Array.Empty<string>(), modular);
            else if (inputs.Menu != null) builder.MergeAsset(Array.Empty<string>(), inputs.Menu, "a", avatarMenu: true);

            foreach (var full in specs.OfType<FullControllerSpec>())
            {
                builder.Current = full.Number;
                int entry = 0;
                foreach (var (menu, prefix) in full.Menus)
                {
                    if (menu != null) builder.MergeAsset(MenuRules.SplitPath(prefix), menu, "fc" + Key(full) + "." + entry, avatarMenu: false, component: full.Component);
                    entry++;
                }
            }

            // SPS: VRCFury's socket and plug services add their toggles first (its services are numbered before features).
            var sps = specs.OfType<SpsOptionsSpec>().FirstOrDefault();
            string spsPath = SpsMenuPath(sps);
            foreach (var item in SpsItems(inputs, sps))
            {
                builder.Current = item.Number;
                builder.NewItem(item.Path, item.Type, item.Icon, new MenuOrigin { Kind = MenuOriginKind.Sps, Component = item.Component, Path = item.Path }, item.Id);
            }

            // Toggles, Puppets and Gesture locks, in component order. A Security Lock adds its buttons the first time a
            // restricted toggle needs it, sorted where the lock is.
            var securityLock = specs.OfType<SecurityLockSpec>().FirstOrDefault();
            bool securityAdded = false;
            void AddSecurity()
            {
                if (securityAdded || securityLock == null) return;
                securityAdded = true;
                builder.Override = securityLock.Number;
                for (int i = 1; i < 8; i++) builder.NewItem("Security/" + i, ControlType.Button, null, Origin(MenuOriginKind.SecurityLock, securityLock, "Security/" + i), "sl" + Key(securityLock) + "." + i);
                builder.NewItem("Security/Unlocked", ControlType.Button, null, Origin(MenuOriginKind.SecurityLock, securityLock, "Security/Unlocked"), "sl" + Key(securityLock) + ".8");
                builder.Override = -1;
            }
            foreach (var spec in specs)
            {
                builder.Current = spec.Number;
                switch (spec)
                {
                    case ToggleSpec toggle:
                        if (toggle.AddItem) builder.NewItem(toggle.Path, toggle.Type, toggle.Icon, Origin(MenuOriginKind.Toggle, toggle, toggle.Path), "t" + Key(toggle));
                        if (toggle.Security) AddSecurity();
                        break;
                    case PuppetSpec puppet:
                        if (puppet.AddItem) builder.NewItem(puppet.Path, puppet.Type, puppet.Icon, Origin(MenuOriginKind.Puppet, puppet, puppet.Path), "p" + Key(puppet));
                        break;
                    case GestureLockSpec gesture:
                        foreach (var path in gesture.Paths)
                            builder.NewItem(path, ControlType.Toggle, null, Origin(MenuOriginKind.GestureLock, gesture, path), "g" + Key(gesture) + "." + MenuRules.Normalize(path));
                        break;
                }
            }
            if (specs.OfType<SecurityRestrictedSpec>().Any()) AddSecurity();

            // SPS gathers its legacy folders under its own and gives it its icon, before the avatar's moves.
            var moves = new List<MoveSpec> { new MoveSpec { From = "Holes", To = spsPath }, new MoveSpec { From = "Sockets", To = spsPath } };
            if (spsPath != "SPS") moves.Add(new MoveSpec { From = "SPS", To = spsPath });
            var icons = new List<SetIconSpec> { new SetIconSpec { Path = spsPath, Icon = sps?.MenuIcon != null ? sps.MenuIcon : SpsIcon } };
            if (inputs.HasSockets && SpsOptionsIcon != null) icons.Add(new SetIconSpec { Path = spsPath + "/<b>Options", Icon = SpsOptionsIcon });
            var extra = new HashSet<MenuSpec>(options.Extra ?? Array.Empty<MenuSpec>());
            icons.AddRange(specs.OfType<SetIconSpec>());

            builder.Current = VrcFuryServices.MenuChanges;
            options.Inspect?.Invoke(MenuBuildStage.BeforeMoves, builder);
            void BetweenSteps()
            {
                for (int i = 0; i < icons.Count; i++)
                    if (builder.SetIcon(icons[i].Path, icons[i].Icon)) { icons.RemoveAt(i); i--; }
            }
            BetweenSteps();
            bool inspectedMoves = false;
            foreach (var move in moves.Concat(specs.OfType<MoveSpec>()))
            {
                if (!inspectedMoves && extra.Contains(move)) { inspectedMoves = true; options.Inspect?.Invoke(MenuBuildStage.BeforeExtraMoves, builder); }
                if (builder.Move(move.From, move.To)) BetweenSteps();
            }
            if (!inspectedMoves) options.Inspect?.Invoke(MenuBuildStage.BeforeExtraMoves, builder);

            builder.Sort();
            bool inspectedReorders = false;
            foreach (var reorder in specs.OfType<ReorderSpec>())
            {
                if (!inspectedReorders && extra.Contains(reorder)) { inspectedReorders = true; options.Inspect?.Invoke(MenuBuildStage.BeforeExtraReorders, builder); }
                builder.Reorder(reorder.Path, reorder.Position);
            }
            if (!inspectedReorders) options.Inspect?.Invoke(MenuBuildStage.BeforeExtraReorders, builder);

            var settings = specs.OfType<MenuSettingsSpec>().FirstOrDefault();
            if (settings != null)
            {
                if (!string.IsNullOrEmpty(settings.NextText)) result.NextText = settings.NextText;
                result.NextIcon = settings.NextIcon;
            }
            // Only submenus open a menu.
            foreach (var entry in MenuTree.Walk(result.Root)) if (!entry.Node.IsFolder) entry.Node.Page = null;
            builder.FinishIds();
            return result;
        }

        private static string Key(MenuSpec spec) => (spec.Component != null ? spec.Component.GetInstanceID().ToString() : "x" + spec.Number) + (spec.Feature > 0 ? "." + spec.Feature : "");

        private static MenuOrigin Origin(MenuOriginKind kind, MenuSpec spec, string path) => new MenuOrigin { Kind = kind, Component = spec.Component, Path = path };

        private static Texture2D spsIcon, spsOptionsIcon;
        private static Texture2D SpsIcon => spsIcon != null ? spsIcon : spsIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(SpsIconPath);
        private static Texture2D SpsOptionsIcon => spsOptionsIcon != null ? spsOptionsIcon : spsOptionsIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(SpsOptionsIconGuid));

        /// <summary>An item VRCFury's SPS creates, with the number of the service creating it.</summary>
        internal sealed class SpsItem
        {
            public int Number;
            public string Path, Id;
            public ControlType Type = ControlType.Toggle;
            public Texture2D Icon;
            public Component Component;
        }

        /// <summary>The SPS folder: SPS Options' menu path, else "SPS".</summary>
        public static string SpsMenuPath(SpsOptionsSpec options) => string.IsNullOrWhiteSpace(options?.MenuPath) ? "SPS" : options.MenuPath;

        /// <summary>
        /// The items VRCFury's SPS adds, in the order its services create them: the socket service's options (Auto Mode with
        /// two automatic sockets, Stealth Mode, Dual Mode with two sockets) and a toggle per socket that asks for one, named
        /// by the socket or after its object; the plug service's DPS Tip Light. Options go in "&lt;b&gt;Options" when there are
        /// sockets.
        /// </summary>
        public static List<SpsItem> SpsItems(MenuInputs inputs, SpsOptionsSpec options)
        {
            string menuPath = SpsMenuPath(options);
            string optionsPath = inputs.HasSockets ? menuPath + "/<b>Options" : menuPath;
            var socketItems = new List<SpsItem>();
            int sockets = VrcFuryServices.Sockets, plugs = VrcFuryServices.Plugs;
            void Socket(string path, string id, ControlType type = ControlType.Toggle, Texture2D icon = null, Component component = null) =>
                socketItems.Add(new SpsItem { Number = sockets, Path = path, Id = id, Type = type, Icon = icon, Component = component });
            int withItem = inputs.Sockets.Count(s => s.AddMenuItem);
            if (inputs.Sockets.Count(s => s.AddMenuItem && s.EnableAuto) >= 2)
                Socket(optionsPath + "/<b>Auto Mode<\\/b>\n<size=20>Activates hole nearest to a VRCFury plug", "sps:auto");
            if (withItem >= 1)
                Socket(optionsPath + "/<b>Stealth Mode<\\/b>\n<size=20>Only local haptics,\nInvisible to others", "sps:stealth");
            if (withItem >= 2)
            {
                string dual = optionsPath + "/<b>Dual Mode<\\/b>\n<size=20>Allows 2 active sockets";
                Socket(dual + "/Enable Dual Mode", "sps:dual");
                Socket(dual + "/<b>WARNING<\\/b>\n<size=20>Everyone else must use SPS or TPS - NO DPS!", "sps:dual.1", ControlType.Button);
                Socket(dual + "/<b>WARNING<\\/b>\n<size=20>Nobody else can use a hole at the same time", "sps:dual.2", ControlType.Button);
                Socket(dual + "/<b>WARNING<\\/b>\n<size=20>DO NOT ENABLE MORE THAN 2", "sps:dual.3", ControlType.Button);
            }
            // Every socket takes a name, even one without an item: a later socket of the same name gets "1", "2"…
            var used = new HashSet<string>();
            foreach (var socket in inputs.Sockets)
            {
                string preferred = string.IsNullOrWhiteSpace(socket.Name) ? SocketName(socket.Component) : socket.Name;
                string name = preferred;
                for (int i = 1; !used.Add(name); i++) name = preferred + i;
                if (socket.AddMenuItem) Socket(menuPath + "/" + name, "sps:s" + socket.Component.GetInstanceID(), icon: socket.Icon, component: socket.Component);
            }
            var plugItems = new List<SpsItem>();
            if (inputs.TipLight)
                plugItems.Add(new SpsItem { Number = plugs, Path = optionsPath + "/<b>DPS Tip Light<\\/b>\n<size=20>Allows plugs to trigger old DPS animations", Id = "sps:tiplight" });
            return plugs < sockets ? plugItems.Concat(socketItems).ToList() : socketItems.Concat(plugItems).ToList();
        }

        private static System.Reflection.MethodInfo fallbackId, toVrcFuryObject;

        /// <summary>
        /// The name VRCFury gives a socket without one: made from its object's name (or its parents'), by VRCFury itself
        /// (HapticUtils.GetFallbackId) so it matches exactly; the object's name when that can't be called.
        /// </summary>
        public static string SocketName(Component socket)
        {
            if (socket == null) return "Unknown";
            try
            {
                var objectType = VrcFury.Find("VF.Utils.VFGameObject");
                fallbackId ??= VrcFury.Find("VF.Builder.Haptics.HapticUtils")?.GetMethod("GetFallbackId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { objectType }, null);
                toVrcFuryObject ??= objectType?.GetMethod("op_Implicit", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
                if (fallbackId != null && toVrcFuryObject != null && fallbackId.Invoke(null, new[] { toVrcFuryObject.Invoke(null, new object[] { socket.gameObject }) }) is string name) return name;
            }
            catch (Exception) { }
            return socket.gameObject.name;
        }

        /// <summary>
        /// The menu as VRChat receives it: VRCFury moves the end of a menu of more than 8 items to a "Next" submenu, page after
        /// page (a copy; <paramref name="root"/> is unchanged). Without VRCFury nothing is split.
        /// </summary>
        public static MenuPage Paginate(MenuPage root, string nextText, Texture2D nextIcon)
        {
            var copy = MenuTree.Copy(root);
            var seen = new HashSet<MenuPage>();
            void Split(MenuPage page, string owner)
            {
                if (page == null || !seen.Add(page)) return;
                var folders = page.Items.Where(n => n.IsFolder && n.Page != null).ToList();
                var current = page;
                int number = 2;
                while (current.Items.Count > MaxControls)
                {
                    var next = new MenuPage { Name = page.Name + " (Page " + number + ")" };
                    while (current.Items.Count > MaxControls - 1)
                    {
                        next.Items.Insert(0, current.Items[current.Items.Count - 1]);
                        current.Items.RemoveAt(current.Items.Count - 1);
                    }
                    current.Items.Add(new MenuNode
                    {
                        Id = "next:" + owner + ":" + number, Name = nextText, Icon = nextIcon, Type = ControlType.SubMenu, Page = next,
                        Origin = new MenuOrigin { Kind = MenuOriginKind.Next },
                    });
                    seen.Add(next);
                    current = next;
                    number++;
                }
                foreach (var folder in folders) Split(folder.Page, folder.Id);
            }
            Split(copy, MenuTree.RootId);
            return copy;
        }
    }

    /// <summary>VRCFury's menu path and name rules.</summary>
    internal static class MenuRules
    {
        private const string Slash = "\u0001";
        private static readonly Regex Tags = new Regex("<.*?>"), Spaces = new Regex(@"\s\s+");

        /// <summary>"Clothes/Hat" → ["Clothes", "Hat"]; "\/" is a slash inside a name.</summary>
        public static IList<string> SplitPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
            return path.Replace("\\/", Slash).Split('/').Select(s => s.Replace(Slash, "/")).ToArray();
        }

        public static string Escape(string name) => (name ?? "").Replace("/", "\\/");

        public static string JoinPath(IEnumerable<string> names) => string.Join("/", names.Select(Escape));

        /// <summary>How VRCFury compares names: lower case, without rich text tags, line breaks or repeated spaces.</summary>
        public static string Normalize(string name) =>
            Spaces.Replace(Tags.Replace((name ?? "").ToLowerInvariant(), "").Replace("\n", " ").Replace("\\n", " "), " ").Trim();

        private static string[] Slugs(string name) =>
            Tags.Replace(name ?? "", "`").Split('`').Select(Normalize).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        /// <summary>
        /// The items of <paramref name="page"/> a path names: those with exactly that name, else those with the same normalized
        /// name, else those with a rich-text piece of that name.
        /// </summary>
        public static List<MenuNode> Find(MenuPage page, string name, Func<MenuNode, bool> predicate = null)
        {
            var found = page.Items.Where(n => (predicate == null || predicate(n)) && n.Name == name).ToList();
            if (found.Count > 0) return found;
            string normalized = Normalize(name);
            if (string.IsNullOrWhiteSpace(normalized)) return found;
            found = page.Items.Where(n => (predicate == null || predicate(n)) && Normalize(n.Name) == normalized).ToList();
            if (found.Count > 0) return found;
            return page.Items.Where(n => (predicate == null || predicate(n)) && Slugs(n.Name).Contains(normalized)).ToList();
        }

        public static bool IsFolder(MenuNode node) => node.Type == ControlType.SubMenu;

        /// <summary>Whether a path segment can name an item called <paramref name="name"/> (any of the three ways).</summary>
        public static bool Names(string segment, string name)
        {
            if (segment == name) return true;
            string normalized = Normalize(segment);
            return !string.IsNullOrWhiteSpace(normalized) && (normalized == Normalize(name) || Slugs(name).Contains(normalized));
        }
    }

    /// <summary>One run of the menu build: the menu being made and VRCFury's operations on it.</summary>
    internal sealed class MenuBuilder
    {
        private readonly MenuBuildResult result;
        private readonly IReadOnlyDictionary<VRCExpressionsMenu.Control, string> pins;
        private readonly Dictionary<VRCExpressionsMenu, MenuPage> converted = new Dictionary<VRCExpressionsMenu, MenuPage>();
        /// <summary>The sort position of items created now (the running feature's number).</summary>
        public int Current;
        /// <summary>When set (≥ 0), the sort position of created items instead of <see cref="Current"/>.</summary>
        public int Override = -1;

        public MenuBuilder(MenuBuildResult result, IReadOnlyDictionary<VRCExpressionsMenu.Control, string> pins)
        {
            this.result = result;
            this.pins = pins;
        }

        public MenuPage Root => result.Root;
        public List<string> Errors => result.Errors;
        private int depth;

        private int Position => Override >= 0 ? Override : Current;

        private MenuNode Clone(MenuNode from)
        {
            var node = from.CloneShallow();
            node.Sort = Position;
            return node;
        }

        /// <summary>
        /// A menu asset as pages of items, its submenus too (each asset once, so shared and looping submenus stay so). Item ids
        /// come from the asset and the control's place in it, through <paramref name="via"/> (what merged it).
        /// </summary>
        public MenuPage Convert(VRCExpressionsMenu menu, string via, out bool created, bool avatarMenu, Component component = null)
        {
            created = false;
            if (menu == null) return null;
            if (converted.TryGetValue(menu, out var done)) return done;
            created = true;
            var page = new MenuPage { Name = menu.name, Asset = avatarMenu ? menu : null };
            converted[menu] = page;
            var controls = menu.controls ?? new List<VRCExpressionsMenu.Control>();
            for (int i = 0; i < controls.Count; i++)
            {
                var control = controls[i];
                if (control == null) continue;
                var node = Item(menu, i, control, via, avatarMenu ? MenuOriginKind.AvatarMenu : MenuOriginKind.MergedMenu, component);
                page.Items.Add(node);
                if (control.type == ControlType.SubMenu && control.subMenu != null) node.Page = Convert(control.subMenu, via, out _, avatarMenu, component);
            }
            return page;
        }

        /// <summary>
        /// The item of a menu asset's control (without its submenu). Its id comes from the asset and the control's place in
        /// it, through <paramref name="via"/>; the avatar's own controls keep the ids saving pinned when it moved or copied them.
        /// </summary>
        public MenuNode Item(VRCExpressionsMenu menu, int index, VRCExpressionsMenu.Control control, string via, MenuOriginKind kind, Component component = null)
        {
            bool avatarMenu = kind == MenuOriginKind.AvatarMenu;
            string id = avatarMenu && pins != null && pins.TryGetValue(control, out var pinned) ? pinned : via + ":" + MenuInputs.Key(menu) + "/" + index;
            if (avatarMenu && !result.ControlIds.ContainsKey(control)) result.ControlIds[control] = id;
            return new MenuNode
            {
                Id = id, Name = control.name ?? "", Type = Enum.IsDefined(typeof(ControlType), control.type) ? control.type : ControlType.Button,
                Icon = control.icon, Parameter = control.parameter?.name, Value = control.value,
                SubParameters = control.subParameters?.Select(p => p?.name ?? "").ToArray() ?? Array.Empty<string>(),
                Labels = control.labels,
                Origin = new MenuOrigin { Kind = kind, Asset = menu, Index = index, Control = control, Component = component },
            };
        }

        public void MergeAsset(IList<string> prefix, VRCExpressionsMenu menu, string via, bool avatarMenu, Component component = null)
        {
            converted.Clear();
            var page = Convert(menu, via, out _, avatarMenu, component);
            MergePage(prefix, page);
        }

        /// <summary>Adds <paramref name="from"/>'s items to the menu at <paramref name="prefix"/>, folders into folders of the same name.</summary>
        public void MergePage(IList<string> prefix, MenuPage from, Dictionary<MenuPage, MenuPage> seen = null)
        {
            // Never endless: a menu merged into itself grows forever (VRCFury's own build overflows there).
            if (depth > 64) { if (!Errors.Contains(Endless)) Errors.Add(Endless); return; }
            var to = GetSubmenu(prefix);
            seen ??= new Dictionary<MenuPage, MenuPage>();
            seen[from] = to;
            var duplicates = new Dictionary<string, int>();
            foreach (var item in from.Items.ToList())
            {
                if (item.IsFolder && item.Page != null)
                {
                    if (seen.TryGetValue(item.Page, out var loop))
                    {
                        var copy = Clone(item);
                        copy.Page = loop;
                        to.Items.Add(copy);
                        continue;
                    }
                    int duplicate = duplicates[item.Name] = duplicates.TryGetValue(item.Name, out int count) ? count + 1 : 0;
                    var path = new List<string>(prefix) { item.Name + (duplicate > 0 ? ".dup." + duplicate : "") };
                    GetSubmenu(path, true, item);
                    depth++;
                    try { MergePage(path, item.Page, seen); }
                    finally { depth--; }
                }
                else to.Items.Add(Clone(item));
            }
        }

        private const string Endless = "A menu is merged into itself: VRCFury can't build this avatar.";

        /// <summary>Whether moving <paramref name="folder"/> to <paramref name="to"/> would put its menu inside itself.</summary>
        public bool IntoItself(MenuNode folder, string to)
        {
            if (folder == null || !folder.IsFolder || folder.Page == null) return false;
            // The deepest existing menu on the way to the destination: inside the folder's own menu, the move never ends.
            var page = Root;
            foreach (var name in MenuRules.SplitPath(to).Take(Math.Max(0, MenuRules.SplitPath(to).Count - 1)))
            {
                int offset = 0, dup = name.IndexOf(".dup.", StringComparison.Ordinal);
                string plain = name;
                if (dup >= 0 && int.TryParse(name.Substring(dup + 5), out offset)) plain = name.Substring(0, dup);
                else offset = 0;
                var folders = MenuRules.Find(page, plain, MenuRules.IsFolder);
                if (offset >= folders.Count || folders[offset].Page == null) break;
                page = folders[offset].Page;
            }
            return page == folder.Page || MenuTree.Contains(folder, page);
        }

        /// <summary>The menu at <paramref name="path"/>, creating missing folders (from <paramref name="from"/> for the last one).</summary>
        public MenuPage GetSubmenu(IList<string> path, bool create = true, MenuNode from = null)
        {
            var current = Root;
            for (int i = 0; i < path.Count; i++)
            {
                string name = path[i];
                int offset = 0, dup = name.IndexOf(".dup.", StringComparison.Ordinal);
                if (dup >= 0 && int.TryParse(name.Substring(dup + 5), out offset)) name = name.Substring(0, dup);
                else offset = 0;
                var folders = MenuRules.Find(current, name, MenuRules.IsFolder);
                var folder = offset < folders.Count ? folders[offset] : null;
                if (folder == null)
                {
                    if (!create) return null;
                    if (from != null && i == path.Count - 1) folder = Clone(from);
                    else
                    {
                        string where = string.Join("/", path.Take(i + 1).Select(MenuRules.Normalize));
                        folder = new MenuNode { Id = "f:" + where, Sort = Position, Origin = new MenuOrigin { Kind = MenuOriginKind.Folder, Path = MenuRules.JoinPath(path.Take(i + 1)) } };
                    }
                    folder.Name = name;
                    folder.Type = ControlType.SubMenu;
                    folder.Page = null;
                    current.Items.Add(folder);
                }
                if (folder.Page == null)
                {
                    if (!create) return null;
                    folder.Page = new MenuPage { Name = string.Join(" » ", path.Take(i + 1)) };
                }
                current = folder.Page;
            }
            return current;
        }

        /// <summary>A new item at <paramref name="path"/> ("Folder/Name"), creating its folders.</summary>
        public MenuNode NewItem(string path, ControlType type, Texture2D icon, MenuOrigin origin, string id)
        {
            var split = MenuRules.SplitPath(path);
            if (split.Count == 0) split = new[] { "" };
            var node = new MenuNode { Id = id, Name = split[split.Count - 1], Type = type, Icon = icon, Sort = Position, Origin = origin };
            GetSubmenu(split.Take(split.Count - 1).ToList()).Items.Add(node);
            return node;
        }

        // The menu holding the item a path names, and the item's name.
        private MenuPage Parent(string path, bool create, out IList<string> split, out IList<string> prefix, out string name)
        {
            split = MenuRules.SplitPath(path);
            prefix = split.Count > 0 ? split.Take(split.Count - 1).ToList() : (IList<string>)Array.Empty<string>();
            name = split.Count > 0 ? split[split.Count - 1] : "";
            return GetSubmenu(prefix, create);
        }

        /// <summary>Set Icon: whether it changed an icon (else VRCFury tries again after the next move).</summary>
        public bool SetIcon(string path, Texture2D icon)
        {
            var parent = Parent(path, false, out _, out _, out string name);
            if (parent == null) return false;
            bool changed = false;
            foreach (var node in MenuRules.Find(parent, name))
            {
                if (node.Icon == icon) continue;
                node.Icon = icon;
                changed = true;
            }
            return changed;
        }

        /// <summary>Move Menu Item: moves (and renames) the items a path names; an empty destination deletes them.</summary>
        public bool Move(string from, string to)
        {
            var source = Parent(from, false, out _, out _, out string name);
            if (source == null) return false;
            var items = MenuRules.Find(source, name);
            if (items.Count == 0) return false;
            if (!string.IsNullOrWhiteSpace(to) && items.Any(i => IntoItself(i, to)))
            {
                Errors.Add("Moving “" + name + "” to “" + to + "” puts a menu inside itself: VRCFury can't build this avatar.");
                return false;
            }
            source.Items.RemoveAll(items.Contains);
            if (string.IsNullOrWhiteSpace(to)) return true;
            Parent(to, true, out var toPath, out var toPrefix, out string toName);
            foreach (var item in items)
            {
                if (item.IsFolder && item.Page != null)
                {
                    GetSubmenu(toPath, true, item);
                    MergePage(toPath, item.Page);
                }
                else
                {
                    item.Name = toName;
                    var single = new MenuPage();
                    single.Items.Add(item);
                    MergePage(toPrefix, single);
                }
            }
            return true;
        }

        /// <summary>Reorder Menu Item: puts the items a path names at <paramref name="position"/> (negative counts from the end).</summary>
        public bool Reorder(string path, int position)
        {
            var parent = Parent(path, false, out _, out _, out string name);
            if (parent == null) return false;
            var items = MenuRules.Find(parent, name);
            if (items.Count == 0) return false;
            parent.Items.RemoveAll(items.Contains);
            if (position < 0) position = parent.Items.Count + position;
            position = Mathf.Clamp(position, 0, parent.Items.Count);
            parent.Items.InsertRange(position, items);
            return true;
        }

        /// <summary>Orders every menu by the feature that created its items, keeping their order otherwise.</summary>
        public void Sort()
        {
            foreach (var page in Pages())
            {
                var sorted = page.Items.OrderBy(n => n.Sort).ToList();
                page.Items.Clear();
                page.Items.AddRange(sorted);
            }
        }

        public IEnumerable<MenuPage> Pages()
        {
            var seen = new HashSet<MenuPage>();
            var stack = new Stack<MenuPage>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var page = stack.Pop();
                if (page == null || !seen.Add(page)) continue;
                yield return page;
                foreach (var node in page.Items) if (node.IsFolder && node.Page != null) stack.Push(node.Page);
            }
        }

        /// <summary>Makes ids unique: one item merged twice (the same menu in two Full Controllers) gets "~2".</summary>
        public void FinishIds()
        {
            var used = new HashSet<string>();
            var seen = new HashSet<MenuNode>();
            foreach (var entry in MenuTree.Walk(Root))
            {
                var node = entry.Node;
                if (!seen.Add(node)) continue;
                node.Id ??= "x";
                if (used.Add(node.Id)) continue;
                int n = 2;
                while (!used.Add(node.Id + "~" + n)) n++;
                node.Id += "~" + n;
            }
        }
    }
}
