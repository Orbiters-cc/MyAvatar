using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>A VRCFury feature that changes the menu, read from its component (or made by My Avatar to save edits).</summary>
    internal abstract class MenuSpec
    {
        /// <summary>VRCFury's feature number: items it creates sort by it.</summary>
        public int Number;
        /// <summary>The VRCFury component it comes from (null for features not saved yet).</summary>
        public Component Component;
        /// <summary>Which of the component's features (legacy components hold several).</summary>
        public int Feature;
    }

    internal sealed class FullControllerSpec : MenuSpec
    {
        public readonly List<(VRCExpressionsMenu Menu, string Prefix)> Menus = new List<(VRCExpressionsMenu, string)>();
    }

    /// <summary>A Toggle's menu item: a toggle, a hold button or a slider (radial).</summary>
    internal sealed class ToggleSpec : MenuSpec
    {
        public string Path = "";
        public ControlType Type = ControlType.Toggle;
        public Texture2D Icon;
        public bool AddItem = true;
        public bool Security;
    }

    internal sealed class PuppetSpec : MenuSpec
    {
        public string Path = "";
        /// <summary>RadialPuppet, TwoAxisPuppet, or Button when it adds no item.</summary>
        public ControlType Type = ControlType.TwoAxisPuppet;
        public bool AddItem = true;
        public Texture2D Icon;
    }

    internal sealed class GestureLockSpec : MenuSpec
    {
        public readonly List<string> Paths = new List<string>();
    }

    internal sealed class SecurityLockSpec : MenuSpec { }
    internal sealed class SecurityRestrictedSpec : MenuSpec { }

    internal sealed class MoveSpec : MenuSpec
    {
        public string From = "", To = "";
    }

    internal sealed class SetIconSpec : MenuSpec
    {
        public string Path = "";
        public Texture2D Icon;
    }

    internal sealed class ReorderSpec : MenuSpec
    {
        public string Path = "";
        public int Position;
    }

    /// <summary>Override Menu Settings: the text and icon of the "Next" items.</summary>
    internal sealed class MenuSettingsSpec : MenuSpec
    {
        public string NextText;
        public Texture2D NextIcon;
    }

    internal sealed class SpsOptionsSpec : MenuSpec
    {
        public string MenuPath;
        public Texture2D MenuIcon;
    }

    /// <summary>A VRCFury SPS socket: VRCFury gives it a toggle in the SPS menu.</summary>
    internal sealed class SocketInput
    {
        public Component Component;
        /// <summary>The socket's own name field; empty means a name made from its object's (<see cref="MenuBuild.SocketName"/>).</summary>
        public string Name;
        public bool AddMenuItem, EnableAuto;
        /// <summary>Whether it also gives the legacy lights (DPS, TPS, SPS 1): VRCFury then adds a Legacy Compatibility toggle.</summary>
        public bool UseLights;
        public Texture2D Icon;
    }

    /// <summary>
    /// VRCFury's services number their actions (the menu items they create sort by that number) in the order its injector
    /// lists them: the same scans of its types (services, then action builders) into a hash set, enumerated, as
    /// VRCFuryInjectorBuilder does.
    /// </summary>
    internal static class VrcFuryServices
    {
        private static Dictionary<string, int> numbers;

        public static int MenuChanges => Number("VF.Service.MenuChangesService", 3);
        public static int Sockets => Number("VF.Service.BakeHapticSocketsService", 1);
        public static int Plugs => Number("VF.Service.BakeHapticPlugsService", 2);

        private static int Number(string type, int fallback)
        {
            numbers ??= Compute();
            return numbers.TryGetValue(type, out int n) ? n : fallback;
        }

        private static Dictionary<string, int> Compute()
        {
            var result = new Dictionary<string, int>();
            try
            {
                var reflection = VrcFury.Find("VF.Utils.ReflectionUtils");
                var scan = reflection?.GetMethod("GetTypes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(Type) }, null);
                Type service = VrcFury.Find("VF.Injector.VFServiceAttribute"), action = VrcFury.Find("VF.Actions.ActionBuilder");
                var prototype = VrcFury.Find("VF.Injector.VFPrototypeScopeAttribute");
                if (scan == null || service == null || action == null) return result;
                var types = new HashSet<Type>();
                types.UnionWith((IEnumerable<Type>)scan.Invoke(null, new object[] { service }));
                types.UnionWith((IEnumerable<Type>)scan.Invoke(null, new object[] { action }));
                int n = 0;
                foreach (var t in types)
                    if (prototype == null || !t.IsDefined(prototype, false)) result[t.FullName] = ++n;
            }
            catch (Exception) { }
            return result;
        }
    }

    /// <summary>
    /// Everything that decides an avatar's VRChat menu: the descriptor's menu and the VRCFury features that change it, in
    /// the order VRCFury builds them. VRCFury's models are internal: they are read by reflection, the way VRCFury itself
    /// upgrades older ones.
    /// </summary>
    internal sealed class MenuInputs
    {
        /// <summary>The number of the first VRCFury feature: its services are numbered below it (<see cref="VrcFuryServices"/>).</summary>
        public const int FirstFeatureNumber = 1000;

        public GameObject Root;
        public VRCAvatarDescriptor Descriptor;
        /// <summary>The descriptor's menu; null when it has none (VRChat then shows no expressions).</summary>
        public VRCExpressionsMenu Menu;
        /// <summary>VRCFury builds this avatar (it has VRCFury components): it merges, moves and pages the menu.</summary>
        public bool UsesVrcFury;
        /// <summary>A socket adds a toggle to the SPS menu: VRCFury then puts its options in their own folder.</summary>
        public bool HasSocketItems;
        public readonly List<MenuSpec> Specs = new List<MenuSpec>();
        /// <summary>The number of a feature added after all of the avatar's own.</summary>
        public int NextNumber = FirstFeatureNumber;
        /// <summary>VRCFury SPS sockets, in VRCFury's order, and whether a plug asks for the DPS tip light.</summary>
        public readonly List<SocketInput> Sockets = new List<SocketInput>();
        public bool TipLight;
        /// <summary>A plug renders with SPS: VRCFury adds its depth pass and realtime shadow options.</summary>
        public bool SpsPlugs;
        /// <summary>Modular Avatar's menu, built before VRCFury runs; null when Modular Avatar installs nothing.</summary>
        public MaMenu ModularAvatar;

        private static Type componentBase, debugInfo, socketType, plugType;

        /// <summary>Reads <paramref name="root"/>; components <paramref name="skip"/> accepts are left out (My Avatar's own edits).</summary>
        public static MenuInputs Read(GameObject root, Func<Component, bool> skip = null)
        {
            var inputs = new MenuInputs { Root = root };
            if (root == null) return inputs;
            inputs.Descriptor = root.GetComponent<VRCAvatarDescriptor>();
            inputs.Menu = DescriptorMenu(inputs.Descriptor);
            // Modular Avatar runs first (NDMF builds at -11000, VRCFury at -10000): VRCFury starts from the menu it made.
            inputs.ModularAvatar = MaMenus.Read(root);
            var type = VrcFury.Component;
            if (type == null) return inputs;
            componentBase ??= VrcFury.Find("VF.Component.VRCFuryComponent");
            debugInfo ??= VrcFury.Find("VF.Model.VRCFuryDebugInfo");
            socketType ??= VrcFury.Find("VF.Component.VRCFuryHapticSocket");
            plugType ??= VrcFury.Find("VF.Component.VRCFuryHapticPlug");

            // Objects VRCFury removes before building: EditorOnly ones and those holding a Delete During Upload.
            var removed = new Dictionary<Transform, bool>();
            bool Removed(Transform t)
            {
                if (t == null || t == root.transform) return false;
                if (removed.TryGetValue(t, out bool known)) return known;
                bool result = t.CompareTag("EditorOnly") || t.GetComponents(type).Any(c => Kinds(c).Contains("DeleteDuringUpload")) || Removed(t.parent);
                removed[t] = result;
                return result;
            }

            if (componentBase != null)
                foreach (var c in root.GetComponentsInChildren(componentBase, true))
                {
                    if (c == null || Removed(c.transform) || (skip != null && skip(c))) continue;
                    if (debugInfo != null && debugInfo.IsInstanceOfType(c)) continue;
                    inputs.UsesVrcFury = true;
                    if (socketType != null && socketType.IsInstanceOfType(c))
                    {
                        var socket = new SocketInput
                        {
                            // Before version 1 a name holding "D P S" was a placeholder: VRCFury empties it.
                            Component = c, Name = Older(c, 1) && (VrcFury.Field(c, "name") as string ?? "").Contains("D P S") ? "" : VrcFury.Field(c, "name") as string,
                            AddMenuItem = VrcFury.Field(c, "addMenuItem") as bool? ?? true, EnableAuto = VrcFury.Field(c, "enableAuto") as bool? ?? true,
                            UseLights = VrcFury.Field(c, "useLights") as bool? ?? true,
                            Icon = Asset<Texture2D>(VrcFury.Field(c, "menuIcon")),
                        };
                        inputs.Sockets.Add(socket);
                        if (socket.AddMenuItem) inputs.HasSocketItems = true;
                    }
                    if (plugType != null && plugType.IsInstanceOfType(c))
                    {
                        if (VrcFury.Field(c, "addDpsTipLight") as bool? ?? false) inputs.TipLight = true;
                        // Before version 3 the setting was "configureSps".
                        if (VrcFury.Field(c, Older(c, 3) ? "configureSps" : "enableSps") as bool? ?? true) inputs.SpsPlugs = true;
                    }
                }

            int number = FirstFeatureNumber;
            foreach (var component in root.GetComponentsInChildren(type, true))
            {
                number++;
                if (component == null || (skip != null && skip(component)) || Removed(component.transform) || InRagdoll(component.transform)) continue;
                int index = 0;
                foreach (var feature in Features(component))
                {
                    var spec = Spec(feature);
                    if (spec != null)
                    {
                        spec.Number = number; spec.Component = component; spec.Feature = index;
                        inputs.Specs.Add(spec);
                    }
                    index++;
                }
            }
            inputs.NextNumber = number + 1;
            return inputs;
        }

        /// <summary>
        /// The menu the build starts from: the descriptor's when it customizes expressions. With Modular Avatar in the project,
        /// the descriptor's menu in any case: Modular Avatar turns customizing on for every avatar it builds.
        /// </summary>
        public static VRCExpressionsMenu DescriptorMenu(VRCAvatarDescriptor descriptor) =>
            descriptor != null && (descriptor.customExpressions || MaMenus.Installed) ? descriptor.expressionsMenu : null;

        /// <summary>The features of a VRCFury component: its content, after those of a legacy config.</summary>
        public static IEnumerable<object> Features(Component component)
        {
            var config = VrcFury.Field(component, "config");
            if (VrcFury.Field(config, "features") is IEnumerable legacy)
                foreach (var feature in legacy)
                    if (feature != null) yield return feature;
            var content = VrcFury.Content(component);
            if (content != null) yield return content;
        }

        private static IEnumerable<string> Kinds(Component component) => Features(component).Select(f => f.GetType().Name);

        // Skipped by VRCFury: other tools' clone containers.
        private static bool InRagdoll(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                string name = t.name;
                if (name == "Ragdoll System" || name == "CarbonCopy Container" || name.StartsWith("CloneContainer", StringComparison.Ordinal) ||
                    name == "DexClone_worldSpace" || name == "CCopy World Space") return true;
            }
            return false;
        }

        private static int Version(object model) => VrcFury.Field(model, "version") as int? ?? -1;
        private static string Text(object model, string field) => VrcFury.Field(model, field) as string ?? "";
        private static bool Flag(object model, string field) => VrcFury.Field(model, field) as bool? ?? false;
        private static T Asset<T>(object wrapper) where T : UnityEngine.Object => VrcFury.ObjectReference(wrapper) as T;

        // An older model is read the way VRCFury upgrades it before building.
        private static bool Older(object model, int version)
        {
            int v = Version(model);
            return v >= 0 && v < version;
        }

        private static string Renamed(string path, string from, string to) =>
            path != null && (path == from || path.StartsWith(from + "/", StringComparison.Ordinal)) ? to + path.Substring(from.Length) : path;

        private static MenuSpec Spec(object model)
        {
            switch (model.GetType().Name)
            {
                case "Toggle":
                {
                    string name = Text(model, "name");
                    bool enableIcon = Flag(model, "enableIcon");
                    var icon = enableIcon ? Asset<Texture2D>(VrcFury.Field(model, "icon")) : null;
                    bool addMenuItem = VrcFury.Field(model, "addMenuItem") as bool? ?? true;
                    bool useInt = Flag(model, "useInt");
                    var spec = new ToggleSpec
                    {
                        Path = name, Icon = icon, Security = Flag(model, "securityEnabled"),
                        Type = Flag(model, "slider") ? ControlType.RadialPuppet : Flag(model, "holdButton") ? ControlType.Button : ControlType.Toggle,
                    };
                    // A toggle needs a title or an icon to get an item; an int toggle never gets one.
                    spec.AddItem = addMenuItem && (name.Length > 0 || icon != null) && (spec.Type == ControlType.RadialPuppet || !useInt);
                    return spec;
                }
                case "FullController":
                {
                    var spec = new FullControllerSpec();
                    if (VrcFury.Field(model, "menus") is IEnumerable menus)
                        foreach (var entry in menus)
                            if (entry != null) spec.Menus.Add((Asset<VRCExpressionsMenu>(VrcFury.Field(entry, "menu")), Text(entry, "prefix")));
                    // Before version 2 a Full Controller had one menu and its submenu.
                    if (Older(model, 2) && VrcFury.Field(model, "menu") is object legacy)
                        spec.Menus.Add((Asset<VRCExpressionsMenu>(legacy), Text(model, "submenu")));
                    return spec;
                }
                case "Puppet":
                {
                    bool usesX = false;
                    if (VrcFury.Field(model, "stops") is IEnumerable stops)
                        foreach (var stop in stops)
                        {
                            if (stop == null) continue;
                            if ((VrcFury.Field(stop, "x") as float? ?? 0) != 0) usesX = true;
                        }
                    bool slider = Flag(model, "slider");
                    return new PuppetSpec
                    {
                        Path = Text(model, "name"),
                        Type = slider ? ControlType.RadialPuppet : ControlType.TwoAxisPuppet,
                        AddItem = !slider || usesX,
                        Icon = Flag(model, "enableIcon") ? Asset<Texture2D>(VrcFury.Field(model, "icon")) : null,
                    };
                }
                case "GestureDriver":
                {
                    var spec = new GestureLockSpec();
                    if (VrcFury.Field(model, "gestures") is IEnumerable gestures)
                        foreach (var gesture in gestures)
                        {
                            if (gesture == null || !Flag(gesture, "enableLockMenuItem")) continue;
                            string path = Text(gesture, "lockMenuItem");
                            if (!string.IsNullOrWhiteSpace(path) && !spec.Paths.Contains(path)) spec.Paths.Add(path);
                        }
                    return spec.Paths.Count > 0 ? spec : null;
                }
                case "SecurityLock": return new SecurityLockSpec();
                case "SecurityRestricted": return new SecurityRestrictedSpec();
                case "MoveMenuItem":
                {
                    string from = Text(model, "fromPath"), to = Text(model, "toPath");
                    if (Older(model, 1)) from = Renamed(from, "Holes", "Sockets");
                    if (Older(model, 2)) from = Renamed(from, "Sockets", "SPS");
                    if (Older(model, 3)) to = Renamed(to, "Sockets", "SPS");
                    return new MoveSpec { From = from, To = to };
                }
                case "SetIcon":
                {
                    string path = Text(model, "path");
                    if (Older(model, 1)) path = Renamed(path, "Sockets", "SPS");
                    return new SetIconSpec { Path = path, Icon = Asset<Texture2D>(VrcFury.Field(model, "icon")) };
                }
                case "ReorderMenuItem":
                    return new ReorderSpec { Path = Text(model, "path"), Position = VrcFury.Field(model, "position") as int? ?? 0 };
                case "OverrideMenuSettings":
                    return new MenuSettingsSpec { NextText = Text(model, "nextText"), NextIcon = Asset<Texture2D>(VrcFury.Field(model, "nextIcon")) };
                case "SpsOptions":
                    return new SpsOptionsSpec { MenuPath = Text(model, "menuPath"), MenuIcon = Asset<Texture2D>(VrcFury.Field(model, "menuIcon")) };
            }
            return null;
        }

        /// <summary>The asset's stable key (GUID and local id), or its instance id while it is in memory only.</summary>
        public static string Key(UnityEngine.Object asset)
        {
            if (asset == null) return "null";
            int id = asset.GetInstanceID();
            if (Keys.TryGetValue(id, out var key)) return key;
            key = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long local) && !string.IsNullOrEmpty(guid)
                ? guid.Substring(0, 12) + local.ToString("x") : "i" + id;
            Keys[id] = key;
            return key;
        }

        private static readonly Dictionary<int, string> Keys = new Dictionary<int, string>();
    }
}
