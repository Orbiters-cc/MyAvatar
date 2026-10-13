using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// The synced parameters an upload frees by leaving menu items out (<see cref="MenuUploadBuild"/>), worked out on the
    /// avatar in the scene with the menu the editor shows, so every parameter budget (XRay Gizmos, My Avatar's) counts the
    /// avatar as it will upload. A parameter is freed as the upload frees it: every menu item setting it goes, and nothing
    /// else sets it (no parameter driver setting another value, animation, contact or PhysBone).
    /// </summary>
    internal static class MenuUploadEstimate
    {
        internal sealed class Freed
        {
            /// <summary>The avatar's own expression parameters.</summary>
            public readonly HashSet<string> Descriptor = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Each VRCFury Full Controller's own parameters.</summary>
            public readonly Dictionary<Component, HashSet<string>> Controllers = new Dictionary<Component, HashSet<string>>();
            /// <summary>VRCFury Toggles whose item goes.</summary>
            public readonly HashSet<Component> Toggles = new HashSet<Component>();

            public bool Empty => Descriptor.Count == 0 && Controllers.Count == 0 && Toggles.Count == 0;
        }

        private static readonly Freed Nothing = new Freed();

        // Worked out once per change of the avatar or of the assets its menu is made of (a budget asks once per component),
        // and again as soon as My Avatar's choices change: Unity reports changes a frame later, after a budget shown beside
        // them was counted. Changes elsewhere (another avatar, a material) keep what was worked out.
        private static readonly Dictionary<GameObject, (string Choices, Freed Freed)> Cache = new Dictionary<GameObject, (string, Freed)>();
        // Hidden looks are read from the avatar's parameters (a few milliseconds): once per change of the avatar.
        private static readonly Dictionary<GameObject, string> Looks = new Dictionary<GameObject, string>();

        [InitializeOnLoadMethod]
        private static void CountAsUploaded()
        {
            ObjectChangeEvents.changesPublished += (ref ObjectChangeEventStream stream) =>
            {
                if (Cache.Count == 0 && Looks.Count == 0) return;
                var changes = MenuChanges.Read(ref stream);
                foreach (var avatar in Cache.Keys.Concat(Looks.Keys).Distinct().ToList())
                    if (avatar == null || changes.Touches(avatar)) Forget(avatar);
            };
            Undo.undoRedoPerformed += ForgetAll;
            EditorApplication.projectChanged += ForgetAll;
            AvatarParameterBudget.BuildRemovedParameters.Add(component =>
            {
                var freed = For(component);
                if (component is VRCAvatarDescriptor) return freed.Descriptor;
                return freed.Controllers.TryGetValue(component, out var names) ? names : null;
            });
            AvatarParameterBudget.BuildRemovedToggles.Add(component => For(component).Toggles.Contains(component));
        }

        private static void Forget(GameObject avatar) { Cache.Remove(avatar); Looks.Remove(avatar); }

        private static void ForgetAll() { Cache.Clear(); Looks.Clear(); }

        private static Freed For(Component component)
        {
            var descriptor = component != null ? component.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (descriptor == null) return Nothing;
            var avatar = descriptor.gameObject;
            string choices = Choices(avatar);
            if (Cache.TryGetValue(avatar, out var cached) && cached.Choices == choices) return cached.Freed;
            foreach (var gone in Cache.Keys.Where(k => k == null).ToList()) Forget(gone);
            Freed freed;
            try { freed = Of(avatar); }
            catch (Exception ex) { Debug.LogWarning("[My Avatar] Could not count what the upload leaves out of the menu: " + ex.Message); freed = Nothing; }
            Cache[avatar] = (choices, freed);
            return freed;
        }

        // What My Avatar leaves out: items, the default menu, hidden looks.
        private static string Choices(GameObject avatar)
        {
            var settings = MenuSave.SettingsOf(avatar);
            if (settings == null) return "";
            var text = new System.Text.StringBuilder(settings.removeDefaultMenu ? "default" : "");
            foreach (var removal in settings.menuRemovals)
                if (removal != null)
                    text.Append('|').Append(string.Join("/", removal.path)).Append('#').Append(string.Join(",", removal.nth)).Append('#').Append(removal.type)
                        .Append('#').Append(removal.parameter).Append('#').Append(removal.source != null ? removal.source.GetInstanceID() : 0);
            if (!Looks.TryGetValue(avatar, out var looks)) Looks[avatar] = looks = string.Join("|", MenuUploadBuild.HiddenLooks(avatar));
            return text.Append("|looks:").Append(looks).ToString();
        }

        internal static Freed Of(GameObject avatar)
        {
            var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
            var settings = MenuSave.SettingsOf(avatar);
            var hidden = new HashSet<string>(MenuUploadBuild.HiddenLooks(avatar), StringComparer.Ordinal);
            if (descriptor == null || settings == null || settings.menuRemovals.Count == 0 && !settings.removeDefaultMenu && hidden.Count == 0) return Nothing;

            var built = MenuBuild.Run(MenuInputs.Read(avatar));
            var all = MenuTree.Walk(built.Root).Select(e => e.Node).ToList();
            // The items that go, and the hidden looks' toggles of the avatar's own menu…
            var removed = new HashSet<MenuNode>(MenuUploadBuild.Removals(settings, built, out _));
            removed.UnionWith(all.Where(n => !n.IsFolder && hidden.Contains(n.Parameter ?? "") && Scope(n, n.Parameter, descriptor) == descriptor));
            // …with what the menu no longer reaches without them: a submenu another folder still opens stays, as at upload.
            var reached = new HashSet<MenuNode>();
            var seen = new HashSet<MenuPage>();
            void Visit(MenuPage page)
            {
                if (page == null || !seen.Add(page)) return;
                foreach (var node in page.Items)
                {
                    if (removed.Contains(node)) continue;
                    reached.Add(node);
                    if (node.IsFolder) Visit(node.Page);
                }
            }
            Visit(built.Root);
            var gone = all.Where(n => !reached.Contains(n)).ToList();

            // What the items that stay still set, by scope (the avatar's own parameters, or a Full Controller's).
            var staying = new HashSet<(Component, string)>();
            foreach (var node in reached)
                foreach (var name in Parameters(node)) staying.Add((Scope(node, name, descriptor), name));

            var freed = new Freed();
            var candidates = new HashSet<(Component Scope, string Name)>(hidden.Select(h => ((Component)descriptor, h)));
            foreach (var node in gone)
            {
                if (node.Origin?.Kind == MenuOriginKind.Toggle && node.Origin.Component != null) { Toggle(freed, node.Origin.Component, staying, descriptor); continue; }
                foreach (var name in Parameters(node)) candidates.Add((Scope(node, name, descriptor), name));
            }
            var writers = new Dictionary<Component, ParameterFreeze.WriterSet>();
            foreach (var (scope, name) in candidates)
            {
                if (ParameterFreeze.Protected(name) || staying.Contains((scope, name))) continue;
                var parameter = Declared(scope, name);
                if (parameter == null) continue;
                if (!writers.TryGetValue(scope, out var set)) writers[scope] = set = ParameterFreeze.Writers(Controllers(scope), avatar);
                if (set.Unknown.Contains(name)) continue;
                if (set.Values.TryGetValue(name, out var values) && values.Any(v => v != parameter.defaultValue)) continue;
                if (scope == descriptor) freed.Descriptor.Add(name);
                else
                {
                    if (!freed.Controllers.TryGetValue(scope, out var names)) freed.Controllers[scope] = names = new HashSet<string>(StringComparer.Ordinal);
                    names.Add(name);
                }
            }
            return freed.Empty ? Nothing : freed;
        }

        // A Toggle's parameter is its own, unless it shares a global one something that stays uses, or another toggle of its
        // exclusive tag switches it off (a parameter driver the upload keeps it for).
        private static void Toggle(Freed freed, Component toggle, HashSet<(Component, string)> staying, VRCAvatarDescriptor descriptor)
        {
            var model = VrcFury.Content(toggle);
            if (VrcFury.Field(model, "enableExclusiveTag") as bool? == true && !string.IsNullOrWhiteSpace(VrcFury.Field(model, "exclusiveTag") as string)) return;
            if (VrcFury.Field(model, "useGlobalParam") as bool? == true)
            {
                string global = VrcFury.Field(model, "globalParam") as string ?? "";
                if (staying.Contains((descriptor, global)) || ParameterFreeze.Protected(global) || ParameterFreeze.Writers(Controllers(descriptor), descriptor.gameObject).Unknown.Contains(global)) return;
            }
            freed.Toggles.Add(toggle);
        }

        private static IEnumerable<string> Parameters(MenuNode node)
        {
            if (!string.IsNullOrEmpty(node.Parameter)) yield return node.Parameter;
            foreach (var sub in node.SubParameters ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(sub)) yield return sub;
        }

        // A Full Controller's items set its own parameters, unless it shares them with the avatar (global ones); everything
        // else sets the avatar's.
        private static Component Scope(MenuNode node, string name, VRCAvatarDescriptor descriptor) =>
            node.Origin?.Kind == MenuOriginKind.MergedMenu && node.Origin.Component != null && !VrcFury.IsGlobalParameter(VrcFury.Content(node.Origin.Component), name)
                ? node.Origin.Component : descriptor;

        // Where a parameter is declared: the avatar's own are in its expression parameters, or global in a Full Controller's.
        private static VRCExpressionParameters.Parameter Declared(Component scope, string name)
        {
            if (!(scope is VRCAvatarDescriptor descriptor)) return Declared(VrcFury.Content(scope), name);
            var own = descriptor.customExpressions && descriptor.expressionParameters != null ? descriptor.expressionParameters.parameters?.FirstOrDefault(p => p != null && p.name == name) : null;
            if (own != null) return own;
            foreach (var controller in FullControllers(descriptor).Where(f => VrcFury.IsGlobalParameter(f, name)))
                if (Declared(controller, name) is VRCExpressionParameters.Parameter shared) return shared;
            return null;
        }

        private static VRCExpressionParameters.Parameter Declared(object fullController, string name)
        {
            if (!(VrcFury.Field(fullController, "prms") is IEnumerable entries)) return null;
            foreach (var entry in entries)
                if (VrcFury.ObjectReference(entry?.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters asset)
                {
                    var parameter = asset.parameters?.FirstOrDefault(p => p != null && p.name == name);
                    if (parameter != null) return parameter;
                }
            return null;
        }

        private static IEnumerable<object> FullControllers(VRCAvatarDescriptor descriptor) =>
            VrcFury.Features(descriptor.gameObject).Where(f => f.kind == "FullController").Select(f => f.feature);

        // The controllers that may set a parameter of the scope: a Full Controller's own, or for the avatar's parameters its
        // playable layers and every Full Controller's (they may share them as globals).
        private static IEnumerable<AnimatorController> Controllers(Component scope)
        {
            if (!(scope is VRCAvatarDescriptor descriptor)) return Controllers(VrcFury.Content(scope));
            return (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Where(l => !l.isDefault).Select(l => l.animatorController as AnimatorController).Where(c => c != null)
                .Concat(FullControllers(descriptor).SelectMany(Controllers)).Distinct().ToList();
        }

        private static IEnumerable<AnimatorController> Controllers(object fullController)
        {
            if (VrcFury.Field(fullController, "controllers") is IEnumerable entries)
                foreach (var entry in entries)
                    if (VrcFury.ObjectReference(entry?.GetType().GetField("controller")?.GetValue(entry)) is AnimatorController controller)
                        yield return controller;
        }
    }
}
