using System;
using System.Collections.Generic;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// The menu of a real build, for avatars whose menu tools My Avatar's preview doesn't model (lilycalInventory, FaceEmo…,
    /// <see cref="ToolConflicts.MenuTools"/>): a hidden copy built the way Checks builds it (every build step, without
    /// My Avatar's optimization and VRCFury's parameter compressor), its menu read before the copy closes. Slower than the
    /// preview, so only on request; kept per avatar until the avatar changes (<see cref="AvatarSignature"/>).
    /// </summary>
    internal sealed class MenuRealBuild
    {
        public GameObject Root;
        public string Signature;
        public HiddenAvatarBuild Build;
        /// <summary>The menu as built, paged as VRChat shows it; null while building or when the build stopped.</summary>
        public MenuPage Page;
        public string Error;
        public DateTime When;
        public bool Running => Build != null && Page == null && Error == null;
        private readonly List<Texture2D> icons = new List<Texture2D>();

        private static readonly Dictionary<GameObject, MenuRealBuild> Last = new Dictionary<GameObject, MenuRealBuild>();

        /// <summary>The last real build of <paramref name="root"/>, running or done, while the avatar is as it was then.</summary>
        public static MenuRealBuild Of(GameObject root)
        {
            if (root == null || !Last.TryGetValue(root, out var real)) return null;
            if (real.Running || real.Signature == null || real.Signature == AvatarSignature.Of(root)) return real;
            return null;
        }

        /// <summary>Starts building <paramref name="root"/> (a build of it running already stops); <paramref name="done"/> runs when it ends.</summary>
        public static MenuRealBuild Start(GameObject root, Action done)
        {
            if (Last.TryGetValue(root, out var old)) old.Dispose();
            var real = new MenuRealBuild { Root = root, When = DateTime.Now };
            Last[root] = real;
            try
            {
                real.Build = HiddenAvatarBuild.Start(root, Skip);
                real.Build.Completed += () => real.Built(done);
                if (real.Build.Finished) real.Built(done);
            }
            catch (Exception ex)
            {
                real.Error = ex.Message;
                real.Build?.Release();
                done?.Invoke();
            }
            return real;
        }

        // As Checks: names and parameters stay as the creator made them.
        private static bool Skip(Type type) =>
            type == typeof(UploadOptimizer.Structure) || type == typeof(UploadOptimizer.Materials) || type.FullName == "VF.Hooks.ParameterCompressorHook";

        private void Built(Action done)
        {
            if (Page != null || Error != null) return;
            try
            {
                if (Build.Cancelled) Error = "the build was stopped";
                else if (Build.Error != null) Error = Build.Error;
                else if (Build.Copy == null) Error = "the copy was closed";
                else
                {
                    Signature = Root != null ? AvatarSignature.Of(Root) : null;
                    var descriptor = Build.Copy.GetComponent<VRCAvatarDescriptor>();
                    Page = Read(descriptor != null && descriptor.customExpressions ? descriptor.expressionsMenu : null, icons);
                    When = DateTime.Now;
                }
            }
            catch (Exception ex) { Debug.LogException(ex); Error = ex.Message; }
            finally
            {
                Build.Release();
                done?.Invoke();
            }
        }

        /// <summary>
        /// A built menu as the menu views show it: names, types, icons (copied: the copy's own go with it) and submenus, each
        /// item known by its place. Nothing in it points at the copy.
        /// </summary>
        internal static MenuPage Read(VRCExpressionsMenu menu, List<Texture2D> icons = null)
        {
            var pages = new Dictionary<VRCExpressionsMenu, MenuPage>();
            var copies = new Dictionary<Texture2D, Texture2D>();
            MenuPage Visit(VRCExpressionsMenu m, string id, string name)
            {
                var page = new MenuPage { Name = name };
                if (m == null) return page;
                pages[m] = page;
                int index = 0;
                foreach (var control in m.controls ?? new List<VRCExpressionsMenu.Control>())
                {
                    if (control == null) continue;
                    string itemId = id + "." + index++;
                    var node = new MenuNode
                    {
                        Id = itemId, Name = control.name ?? "", Type = control.type, Icon = Copy(control.icon, copies, icons),
                        Parameter = control.parameter?.name, Value = control.value,
                        SubParameters = Array.ConvertAll(control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>(), p => p?.name),
                        Labels = control.labels,
                    };
                    if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu && control.subMenu != null)
                        // A menu opened twice (a loop back up) is the same page.
                        node.Page = pages.TryGetValue(control.subMenu, out var known) ? known : Visit(control.subMenu, itemId, node.Name);
                    page.Items.Add(node);
                }
                return page;
            }
            return Visit(menu, "real", "Expressions");
        }

        private static Texture2D Copy(Texture2D icon, Dictionary<Texture2D, Texture2D> copies, List<Texture2D> icons)
        {
            if (icon == null) return null;
            if (copies.TryGetValue(icon, out var copy)) return copy;
            copy = Object.Instantiate(icon);
            copy.name = icon.name;
            copy.hideFlags = HideFlags.DontSave;
            copies[icon] = copy;
            icons?.Add(copy);
            return copy;
        }

        public void Dispose()
        {
            if (Running) Build.Release();
            foreach (var icon in icons) if (icon != null) Object.DestroyImmediate(icon);
            icons.Clear();
            if (Root != null && Last.TryGetValue(Root, out var now) && now == this) Last.Remove(Root);
        }
    }
}
