using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Orbiters.MyAvatar.Editor.Expressions;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// A test avatar using every VRCFury menu rule (merged and duplicate folders, a looping submenu, rich text names, menu paths
    /// into existing folders, Full Controller menus, Move, Set Icon and Reorder, more than 8 items), in a preview scene.
    /// </summary>
    internal sealed class MenuFixture : IDisposable
    {
        public readonly UnityEngine.SceneManagement.Scene Scene;
        public readonly GameObject Root;
        public readonly List<Object> Owned = new List<Object>();
        public Texture2D IconA, IconB, IconC;

        public MenuFixture(bool vrcFury = true, bool many = true)
        {
            Scene = EditorSceneManager.NewPreviewScene();
            Root = new GameObject("Menu Fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(Root, Scene);
            Root.AddComponent<Animator>();
            IconA = Icon("IconA"); IconB = Icon("IconB"); IconC = Icon("IconC");
            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>(); Owned.Add(parameters);
            parameters.parameters = new[] { "Hat", "Glasses", "Lights", "Dance", "Wave", "Hue", "Loop" }
                .Select(n => new VRCExpressionParameters.Parameter { name = n, valueType = n == "Hue" ? VRCExpressionParameters.ValueType.Float : VRCExpressionParameters.ValueType.Bool, saved = true }).ToArray();
            var clothes = Menu("Clothes", Control("Hat", ControlType.Toggle, "Hat"), Control("Glasses", ControlType.Toggle, "Glasses", IconB));
            var fx = Menu("Effects", Control("Lights", ControlType.Toggle, "Lights"), Control("Hue", ControlType.RadialPuppet, null, sub: "Hue"));
            var fx2 = Menu("Effects 2", Control("Sparkles", ControlType.Button, "Dance"));
            var loop = Menu("Loop");
            loop.controls.Add(Control("Again", ControlType.SubMenu, null, menu: loop));
            loop.controls.Add(Control("Spin", ControlType.Toggle, "Loop"));
            var root = Menu("Root",
                Control("Clothes", ControlType.SubMenu, null, IconA, clothes),
                Control("Effects", ControlType.SubMenu, null, null, fx),
                Control("Effects", ControlType.SubMenu, null, null, fx2),
                Control("Dance", ControlType.Button, "Dance"),
                Control("<b>Wave</b>\n<size=20>hello", ControlType.Toggle, "Wave"),
                Control("Loop", ControlType.SubMenu, null, null, loop));
            var descriptor = Root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customExpressions = true;
            descriptor.expressionsMenu = root;
            descriptor.expressionParameters = parameters;
            // The playable layers a new avatar gets from the VRChat SDK: VRCFury adds to the FX one.
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { VRCAvatarDescriptor.AnimLayerType.Base, VRCAvatarDescriptor.AnimLayerType.Additive, VRCAvatarDescriptor.AnimLayerType.Gesture, VRCAvatarDescriptor.AnimLayerType.Action, VRCAvatarDescriptor.AnimLayerType.FX }
                .Select(t => new VRCAvatarDescriptor.CustomAnimLayer { type = t, isDefault = true }).ToArray();
            descriptor.specialAnimationLayers = new[] { VRCAvatarDescriptor.AnimLayerType.Sitting, VRCAvatarDescriptor.AnimLayerType.TPose, VRCAvatarDescriptor.AnimLayerType.IKPose }
                .Select(t => new VRCAvatarDescriptor.CustomAnimLayer { type = t, isDefault = true }).ToArray();
            if (!vrcFury) return;

            var props = Child("Props");
            Feature(props, "Toggle", ("name", "Clothes/Shirt"));
            Feature(props, "Toggle", ("name", "Props/Sword"), ("enableIcon", true), ("icon", Guid("GuidTexture2d", IconC)));
            Feature(props, "Toggle", ("name", "Props/Shield"), ("holdButton", true));
            Feature(props, "Toggle", ("name", "Props/Glow"), ("slider", true));
            Feature(props, "Toggle", ("name", "Props\\/Tools/Wrench"));
            Feature(props, "Toggle", ("name", "effects/Smoke"));
            var extra = Menu("Extra", Control("Spin", ControlType.Toggle, "Loop"), Control("Clothes", ControlType.SubMenu, null, IconC, Menu("Extra Clothes", Control("Scarf", ControlType.Toggle, "Hat"))));
            var extraParameters = ScriptableObject.CreateInstance<VRCExpressionParameters>(); Owned.Add(extraParameters);
            extraParameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Loop", valueType = VRCExpressionParameters.ValueType.Bool }, new VRCExpressionParameters.Parameter { name = "Hat", valueType = VRCExpressionParameters.ValueType.Bool } };
            var full = Feature(Child("Prop pack"), "FullController");
            var entryType = VrcFury.Find("VF.Model.Feature.FullController+MenuEntry");
            var entry = Activator.CreateInstance(entryType, true);
            Set(entry, "menu", Guid("GuidMenu", extra)); Set(entry, "prefix", "");
            ((IList)VrcFury.Field(full, "menus")).Add(entry);
            var paramsEntry = Activator.CreateInstance(VrcFury.Find("VF.Model.Feature.FullController+ParamsEntry"), true);
            Set(paramsEntry, "parameters", Guid("GuidParams", extraParameters));
            ((IList)VrcFury.Field(full, "prms")).Add(paramsEntry);
            var settings = Child("Settings");
            Feature(settings, "SetIcon", ("path", "Props"), ("icon", Guid("GuidTexture2d", IconB)));
            Feature(settings, "MoveMenuItem", ("fromPath", "Dance"), ("toPath", "Props/Dance"));
            Feature(settings, "ReorderMenuItem", ("path", "Props/Shield"), ("position", 0));
            if (many)
                for (int i = 1; i <= 5; i++) Feature(props, "Toggle", ("name", "Toggle " + i));
        }

        /// <summary>
        /// VRCFury SPS on the avatar: sockets named by themselves, twice the same, after their object (and its parent when
        /// that says nothing), one without an item; a plug asking for the DPS tip light; SPS Options moving the folder.
        /// </summary>
        public void AddSps(bool sockets = true, bool plug = true, string menuPath = null)
        {
            if (sockets)
            {
                Socket(Child("Mouth"), ("name", "Mouth"), ("menuIcon", Guid("GuidTexture2d", IconA)));
                Socket(Child("Mouth 2"), ("name", "Mouth"));
                var hips = Child("Hips");
                var unnamed = new GameObject("SPS Socket (1)");
                unnamed.transform.SetParent(hips.transform, false);
                Socket(unnamed);
                Socket(Child("BlowjobHole"), ("enableAuto", false));
                Socket(Child("Hidden"), ("addMenuItem", false));
            }
            if (plug)
            {
                var host = Child("Plug");
                var component = host.AddComponent(VrcFury.Find("VF.Component.VRCFuryHapticPlug"));
                foreach (var (field, value) in new (string, object)[] { ("addDpsTipLight", true), ("autoRenderer", false), ("autoLength", false), ("autoRadius", false), ("length", 0.2f), ("radius", 0.03f), ("unitsInMeters", true), ("enableSps", false) })
                    Set(component, field, value);
            }
            if (menuPath != null) Feature(Child("SPS options"), "SpsOptions", ("menuPath", menuPath));
        }

        private static void Socket(GameObject host, params (string Field, object Value)[] fields)
        {
            var component = host.AddComponent(VrcFury.Find("VF.Component.VRCFuryHapticSocket"));
            foreach (var (field, value) in fields) Set(component, field, value);
        }

        public static Texture2D Icon(string name)
        {
            var texture = new Texture2D(32, 32, TextureFormat.RGBA32, false) { name = name, hideFlags = HideFlags.DontSave };
            return texture;
        }

        private GameObject Child(string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(Root.transform, false);
            return child;
        }

        public VRCExpressionsMenu Menu(string name, params VRCExpressionsMenu.Control[] controls)
        {
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.name = name;
            menu.controls = controls.ToList();
            Owned.Add(menu);
            return menu;
        }

        public static VRCExpressionsMenu.Control Control(string name, ControlType type, string parameter, Texture2D icon = null, VRCExpressionsMenu menu = null, string sub = null) =>
            new VRCExpressionsMenu.Control
            {
                name = name, type = type, icon = icon, subMenu = menu, value = 1,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter ?? "" },
                subParameters = sub != null ? new[] { new VRCExpressionsMenu.Control.Parameter { name = sub } } : Array.Empty<VRCExpressionsMenu.Control.Parameter>(),
            };

        public static object Guid(string type, Object value)
        {
            var wrapper = Activator.CreateInstance(VrcFury.Find("VF.Model." + type), true);
            Set(wrapper, "objRef", value);
            Set(wrapper, "id", "");
            return wrapper;
        }

        public static void Set(object model, string field, object value)
        {
            for (var type = model.GetType(); type != null; type = type.BaseType)
            {
                var f = type.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) continue;
                f.SetValue(model, value);
                return;
            }
            throw new ArgumentException(model.GetType().Name + " has no " + field);
        }

        public static object Feature(GameObject host, string type, params (string Field, object Value)[] fields)
        {
            var component = host.AddComponent(VrcFury.Component);
            var model = Activator.CreateInstance(VrcFury.Find("VF.Model.Feature." + type), true);
            foreach (var (field, value) in fields) Set(model, field, value);
            VrcFury.Component.GetField("content").SetValue(component, model);
            return model;
        }

        // ---- What the avatar builds -------------------------------------------------------------------------------------------

        private static readonly Regex VrcFuryCopy = new Regex("^VRCFury[^_]*_");

        public static string Dump(MenuPage page)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<MenuPage>();
            void Visit(MenuPage p, string indent)
            {
                if (p == null || !seen.Add(p)) return;
                foreach (var n in p.Items)
                {
                    sb.Append(indent).Append(n.Name.Replace("\n", "\\n")).Append(" [").Append(n.Type).Append("] ").Append(n.Icon != null ? n.Icon.name : "-").Append('\n');
                    if (n.Type == ControlType.SubMenu) Visit(n.Page, indent + "  ");
                }
            }
            Visit(page, "");
            return sb.ToString();
        }

        public static string Dump(VRCExpressionsMenu menu)
        {
            var sb = new StringBuilder();
            var seen = new HashSet<VRCExpressionsMenu>();
            void Visit(VRCExpressionsMenu m, string indent)
            {
                if (m == null || !seen.Add(m)) return;
                foreach (var c in m.controls)
                {
                    sb.Append(indent).Append((c.name ?? "").Replace("\n", "\\n")).Append(" [").Append(c.type).Append("] ").Append(c.icon != null ? VrcFuryCopy.Replace(c.icon.name, "") : "-").Append('\n');
                    if (c.type == ControlType.SubMenu) Visit(c.subMenu, indent + "  ");
                }
            }
            Visit(menu, "");
            return sb.ToString();
        }

        /// <summary>The menu shown for <paramref name="root"/>, pages included.</summary>
        public static string Preview(GameObject root)
        {
            var built = MenuBuild.Run(MenuInputs.Read(root));
            return Dump(built.VrcFury ? MenuBuild.Paginate(built.Root, built.NextText, built.NextIcon) : built.Root);
        }

        /// <summary>What VRCFury's own build (its upload entry point) makes of a hidden copy of <paramref name="root"/>.</summary>
        public static string Build(GameObject root)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null;
            try
            {
                clone = Object.Instantiate(root);
                clone.name = root.name;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, scene);
                var builder = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VF.Builder.VRCFuryBuilder")).First(t => t != null);
                var wrapper = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VF.Utils.VFGameObject")).First(t => t != null);
                var convert = wrapper.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.Name == "op_Implicit" && m.ReturnType == wrapper && m.GetParameters()[0].ParameterType == typeof(GameObject));
                try { builder.GetMethod("RunMain", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { convert.Invoke(null, new object[] { clone }) }); }
                catch (TargetInvocationException ex) { throw ex.GetBaseException(); }
                var descriptor = clone.GetComponent<VRCAvatarDescriptor>();
                return Dump(descriptor.customExpressions ? descriptor.expressionsMenu : null);
            }
            finally
            {
                if (clone != null) Object.DestroyImmediate(clone);
                EditorSceneManager.ClosePreviewScene(scene);
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>Random edits over what the avatar builds: renames, icons, moves (into folders, out of them, around).</summary>
        public static List<MenuEdit> RandomEdits(GameObject root, System.Random random, int count, Texture2D[] icons)
        {
            var edits = new List<MenuEdit>();
            int names = 0;
            for (int k = 0; k < count; k++)
            {
                var draft = MenuSave.Draft(root, edits, null, out _);
                var all = MenuTree.Walk(draft.Root);
                if (all.Count == 0) break;
                var entry = all[random.Next(all.Count)];
                int kind = random.Next(12);
                bool Movable(MenuTree.Entry e) => e.Node.Origin?.Kind == MenuOriginKind.AvatarMenu || !MenuTree.OpenedElsewhere(draft.Root, e.Node);
                if (kind >= 10)
                {
                    // A new folder of this item and up to two others of its menu, as holding one on another or ticking them does.
                    if (!Movable(entry)) continue;
                    var others = all.Where(e => e.Parent == entry.Parent && e.Node != entry.Node && Movable(e)).OrderBy(_ => random.Next()).Take(random.Next(1, 3));
                    var items = new List<string> { entry.Node.Id };
                    items.AddRange(others.Select(e => e.Node.Id));
                    edits.Add(new MenuEdit { kind = MenuEditKind.Folder, id = "new:" + k, text = "Folder " + k, items = items });
                    continue;
                }
                if (kind < 3) edits.Add(new MenuEdit { kind = MenuEditKind.Rename, id = entry.Node.Id, text = "Name " + (++names) + (random.Next(4) == 0 ? " / slash" : "") });
                else if (kind < 5) edits.Add(new MenuEdit { kind = MenuEditKind.Icon, id = entry.Node.Id, icon = icons[random.Next(icons.Length)] });
                else if (kind < 10)
                {
                    var folders = new List<(string Id, MenuPage Page)> { (MenuTree.RootId, draft.Root) };
                    folders.AddRange(all.Where(e => e.Node.IsFolder && e.Node.Page != null && e.Node != entry.Node).Select(e => (e.Node.Id, e.Node.Page)));
                    var (id, page) = folders[random.Next(folders.Count)];
                    // As in the editor: never inside itself, and a folder whose menu another folder opens too only when it is
                    // one of the avatar's own menu assets.
                    if (entry.Node.IsFolder && page != entry.Parent && !MenuTree.Reachable(draft.Root, page, entry.Node)) continue;
                    if (entry.Node.Origin?.Kind != MenuOriginKind.AvatarMenu && MenuTree.OpenedElsewhere(draft.Root, entry.Node)) continue;
                    int max = page.Items.Count - (page == entry.Parent ? 1 : 0);
                    edits.Add(new MenuEdit { kind = MenuEditKind.Move, id = entry.Node.Id, parent = id, index = random.Next(max + 1) });
                }
            }
            return edits;
        }

        public void Dispose()
        {
            foreach (var o in Owned) if (o != null) Object.DestroyImmediate(o);
            if (IconA != null) Object.DestroyImmediate(IconA);
            if (IconB != null) Object.DestroyImmediate(IconB);
            if (IconC != null) Object.DestroyImmediate(IconC);
            if (Root != null) { Undo.ClearUndo(Root); Object.DestroyImmediate(Root); }
            EditorSceneManager.ClosePreviewScene(Scene);
        }
    }
}
