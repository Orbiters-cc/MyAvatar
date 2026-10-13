using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>What an item is and which of its settings the editor can change, where they are written.</summary>
    internal sealed class MenuItemInfo
    {
        /// <summary>The parameter the item drives (a slider's value when it sets nothing else), empty when none.</summary>
        public string Parameter = "";
        public ValueType? ParameterType;
        public float Default, Value;
        public bool Saved, Synced;
        /// <summary>A VRCFury toggle: it makes its own parameter; its default is on or off (a slider's, where it starts).</summary>
        public bool Toggle;
        /// <summary>Types it can switch to (empty: it keeps its own), and the avatar's parameters it can drive (null: its own).</summary>
        public readonly List<ControlType> Types = new List<ControlType>();
        public List<VRCExpressionParameters.Parameter> Parameters;
        public bool ValueEditable, DefaultEditable, SavedEditable, SyncedEditable;
        /// <summary>Why settings shown can't change here, or null.</summary>
        public string Why;
    }

    internal static partial class MenuSave
    {
        /// <summary>The parameter an item drives: its own, else the value a puppet sets.</summary>
        internal static string ItemParameter(MenuNode node) =>
            !string.IsNullOrEmpty(node?.Parameter) ? node.Parameter : node?.SubParameters?.FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "";

        /// <summary>
        /// A value as VRChat keeps it for a parameter of that type: on or off, a whole number from 0 to 255, a decimal from -1
        /// to 1.
        /// </summary>
        internal static float Clamped(float value, ValueType? type) =>
            type == ValueType.Bool ? (value > .5f ? 1f : 0f) : type == ValueType.Int ? Mathf.Clamp(Mathf.Round(value), 0, 255) : type == ValueType.Float ? Mathf.Clamp(value, -1f, 1f) : value;

        private static VRCExpressionParameters.Parameter[] ParametersOf(GameObject root)
        {
            var asset = root != null ? root.GetComponent<VRCAvatarDescriptor>()?.expressionParameters : null;
            return asset != null ? asset.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>() : Array.Empty<VRCExpressionParameters.Parameter>();
        }

        /// <summary>
        /// Whether the item's parameter is one of the avatar's own: a Full Controller's items set its own parameters (VRCFury
        /// gives them their own names at upload) unless the Full Controller shares them with the avatar.
        /// </summary>
        internal static bool AvatarsParameter(MenuNode node, string name) =>
            node.Origin?.Kind != MenuOriginKind.MergedMenu || node.Origin.Component == null || VrcFury.IsGlobalParameter(VrcFury.Content(node.Origin.Component), name);

        /// <summary>What <paramref name="node"/> is (with <paramref name="draft"/>'s changes) and what the editor can change.</summary>
        internal static MenuItemInfo Info(GameObject root, MenuNode node, MenuDraft draft)
        {
            var info = new MenuItemInfo { Parameter = ItemParameter(node), Value = node?.Value ?? 0f };
            if (node == null || root == null) return info;
            var origin = node.Origin;
            var all = ParametersOf(root);
            MenuItemSettings overrides = null;
            draft?.Settings.TryGetValue(MenuDraft.SettingsKey(node), out overrides);
            if (origin?.Kind == MenuOriginKind.Toggle && origin.Component != null)
            {
                var model = VrcFury.Content(origin.Component);
                info.Toggle = true;
                info.Parameter = "";
                bool slider = node.Type == ControlType.RadialPuppet;
                info.Default = overrides?.Default ?? (slider ? VrcFury.Field(model, "defaultSliderValue") as float? ?? 0f : (VrcFury.Field(model, "defaultOn") as bool? ?? false) ? 1f : 0f);
                info.Saved = overrides?.Saved ?? VrcFury.Field(model, "saved") as bool? ?? false;
                info.Synced = true;
                if (UsersOwn(origin.Component))
                {
                    info.Types.AddRange(new[] { ControlType.Toggle, ControlType.Button, ControlType.RadialPuppet });
                    info.DefaultEditable = info.SavedEditable = true;
                }
                else info.Why = "Its VRCFury toggle comes from a package's prefab: its settings stay as its creator made them.";
                return info;
            }
            bool own = AvatarsParameter(node, info.Parameter);
            var parameter = own && info.Parameter.Length > 0 ? all.FirstOrDefault(p => p != null && p.name == info.Parameter) : null;
            if (parameter != null)
            {
                info.ParameterType = parameter.valueType;
                info.Default = overrides?.Default ?? parameter.defaultValue;
                info.Saved = overrides?.Saved ?? parameter.saved;
                info.Synced = overrides?.Synced ?? parameter.networkSynced;
                // Written into the avatar's parameters asset (a copy of it when it is not the avatar's alone).
                info.DefaultEditable = info.SavedEditable = info.SyncedEditable = true;
            }
            // Only the avatar's own menu assets are written (copied first when shared); a Full Controller's or Modular Avatar's
            // menus stay as their creator made them.
            if (origin?.Kind == MenuOriginKind.AvatarMenu && origin.Control != null && (node.Type == ControlType.Toggle || node.Type == ControlType.Button))
            {
                info.Types.AddRange(new[] { ControlType.Toggle, ControlType.Button });
                info.Parameters = all.Where(p => p != null && !string.IsNullOrEmpty(p.name)).ToList();
                info.ValueEditable = parameter != null && parameter.valueType != ValueType.Bool;
            }
            if (info.Types.Count == 0 && !info.DefaultEditable && node.Type != ControlType.SubMenu)
                info.Why = origin?.Kind == MenuOriginKind.MergedMenu
                        ? "It comes from the VRCFury Full Controller on “" + (origin.Component != null ? origin.Component.gameObject.name : "?") + "”: its settings stay as its creator made them" +
                          (!own && info.Parameter.Length > 0 ? " (VRCFury gives its parameter its own name at upload)." : ".")
                    : origin?.Kind == MenuOriginKind.MaMenu || origin?.Kind == MenuOriginKind.MaItem ? "It comes from Modular Avatar: its type and parameter stay as its creator made them."
                    : parameter == null && info.Parameter.Length > 0 ? "Its parameter is made by the tool that adds it, not one of the avatar's own."
                    : null;
            return info;
        }

        /// <summary>
        /// The synced bits the pending settings add (or free, below zero): parameters switched to synced or not, before
        /// <paramref name="extra"/> (one more being switched on, of that type).
        /// </summary>
        internal static int PendingSyncedBits(GameObject root, MenuDraft draft, string extra = null)
        {
            var all = ParametersOf(root);
            int bits = 0;
            foreach (var pair in draft?.Settings ?? new Dictionary<string, MenuItemSettings>())
            {
                if (pair.Value.Synced == null || !pair.Key.StartsWith(ParameterKey, StringComparison.Ordinal)) continue;
                string name = pair.Key.Substring(ParameterKey.Length);
                if (name == extra) continue;
                var parameter = all.FirstOrDefault(p => p != null && p.name == name);
                if (parameter == null || parameter.networkSynced == pair.Value.Synced.Value) continue;
                bits += (pair.Value.Synced.Value ? 1 : -1) * VRCExpressionParameters.TypeCost(parameter.valueType);
            }
            return bits;
        }

        private const string ParameterKey = "parameter:", ItemKey = "item:";

        /// <summary>
        /// Why <paramref name="extra"/> more synced bits (a parameter synced, a toggle made a slider) don't fit, or null: the
        /// avatar's budget now with the changes not saved yet, by the rule Save checks (VRChat's 256 bits once VRCFury
        /// compressed what it can; over them, VRCFury compresses unless its settings forbid it, and Save checks the result).
        /// </summary>
        internal static string SyncedBitsProblem(GameObject root, MenuDraft draft, int extra, string except = null)
        {
            if (extra <= 0) return null;
            var budget = AvatarParameterBudget.Estimate(root);
            const int max = ParameterBudget.MaxSyncedBits;
            if (budget.OverBudget) return "The avatar already needs " + budget.BuiltBits + " synced bits, more than VRChat's " + max + ": sync fewer parameters first.";
            int total = budget.TotalBeforeCompression + PendingSyncedBits(root, draft, except) + extra;
            if (total <= max || budget.VrcFuryPresent && budget.CompressionAllowed) return null;
            return "That makes " + total + " synced bits, more than VRChat's " + max + (budget.VrcFuryPresent ? " (VRCFury's compression is off in its settings)" : "") + ": sync fewer parameters first.";
        }

        // Type, parameter and value of the items, and their parameters' and toggles' settings, written where they live (in
        // the save's Undo group), each as the edits leave it: the edits apply in order, so a default set after the item changed
        // its parameter goes to the new parameter, and a toggle made a slider gets its slider's starting value.
        private static void WriteSettings(GameObject root, IReadOnlyList<MenuEdit> edits, MenuDraft draft, Writer writer, Outcome outcome)
        {
            var wanted = MenuTree.Index(draft.Root);
            // Settings of an item no longer in the menu (deleted since) no longer apply: the editor says how many.
            var changes = edits.Where(e => e.kind >= MenuEditKind.Type && e.id != null && wanted.ContainsKey(e.id)).ToList();
            if (changes.Count == 0 && draft.Settings.Count == 0) return;
            var live = writer.Live();
            MenuNode Wanted(string id) => id != null && wanted.TryGetValue(id, out var entry) ? entry.Node : throw new Failure("An item whose settings changed is no longer in the menu.");
            MenuNode Live(string id) => id != null && live.TryGetValue(id, out var entry) ? entry.Node : throw new Failure("An item whose settings changed is no longer in the menu.");

            // The avatar's own menu assets (each copied first when it is not the avatar's alone), then the controls in them.
            var controls = changes.Where(e => e.kind == MenuEditKind.Type || e.kind == MenuEditKind.Parameter || e.kind == MenuEditKind.Value)
                .Where(e => Live(e.id).Origin?.Kind != MenuOriginKind.Toggle).GroupBy(e => e.id).ToList();
            foreach (var group in controls)
            {
                var node = Live(group.Key);
                if (node.Origin?.Kind != MenuOriginKind.AvatarMenu || node.Origin.Control == null || node.Origin.Asset == null)
                    throw new Failure("“" + Plain(node.Name) + "” comes from " + (node.Origin?.Kind == MenuOriginKind.MergedMenu ? "a VRCFury Full Controller" : "a tool") + ": its type and parameter can't change here.");
                writer.Own(node.Origin.Asset);
            }
            if (controls.Count > 0) live = writer.Live();
            foreach (var group in controls)
            {
                var target = Wanted(group.Key);
                var node = Live(group.Key);
                var control = node.Origin.Control;
                var kinds = new HashSet<MenuEditKind>(group.Select(e => e.kind));
                Undo.RecordObject(node.Origin.Asset, "Save menu");
                if (kinds.Contains(MenuEditKind.Type)) control.type = target.Type;
                if (kinds.Contains(MenuEditKind.Parameter)) control.parameter = new VRCExpressionsMenu.Control.Parameter { name = target.Parameter ?? "" };
                if (kinds.Contains(MenuEditKind.Value)) control.value = Clamped(target.Value, ParametersOf(root).FirstOrDefault(p => p != null && p.name == target.Parameter)?.valueType);
                EditorUtility.SetDirty(node.Origin.Asset);
                Touch(outcome, node.Origin.Asset);
            }

            // The user's own VRCFury toggles: their type and settings, as the edits leave them.
            foreach (var group in changes.Where(e => Live(e.id).Origin?.Kind == MenuOriginKind.Toggle).GroupBy(e => e.id))
            {
                var node = Live(group.Key);
                var component = node.Origin.Component;
                if (component == null || !UsersOwn(component)) throw new Failure("“" + Plain(node.Name) + "” comes from a package's prefab: its settings can't change here.");
                if (group.Any(e => e.kind == MenuEditKind.Parameter || e.kind == MenuEditKind.Value || e.kind == MenuEditKind.Synced))
                    throw new Failure("A VRCFury toggle makes its own parameter: “" + Plain(node.Name) + "” can't drive another one.");
                var target = Wanted(group.Key);
                draft.Settings.TryGetValue(ItemKey + group.Key, out var settings);
                var serialized = new SerializedObject(component);
                if (group.Any(e => e.kind == MenuEditKind.Type))
                {
                    serialized.FindProperty("content.slider").boolValue = target.Type == ControlType.RadialPuppet;
                    serialized.FindProperty("content.holdButton").boolValue = target.Type == ControlType.Button;
                }
                if (settings?.Default != null)
                {
                    if (target.Type == ControlType.RadialPuppet) serialized.FindProperty("content.defaultSliderValue").floatValue = Mathf.Clamp01(settings.Default.Value);
                    else serialized.FindProperty("content.defaultOn").boolValue = settings.Default.Value > .5f;
                }
                if (settings?.Saved != null) serialized.FindProperty("content.saved").boolValue = settings.Saved.Value;
                serialized.ApplyModifiedProperties();
                outcome.Components++;
            }

            // The avatar's parameters: what each ends with (copied first when the asset is not the avatar's alone).
            foreach (var pair in draft.Settings)
            {
                var settings = pair.Value;
                if (settings.Default == null && settings.Saved == null && settings.Synced == null || pair.Key.StartsWith(ItemKey, StringComparison.Ordinal)) continue;
                if (!pair.Key.StartsWith(ParameterKey, StringComparison.Ordinal))
                    throw new Failure("A VRCFury Full Controller's parameter has its settings in the Full Controller: they can't change here.");
                string name = pair.Key.Substring(ParameterKey.Length);
                var asset = writer.OwnParameters();
                var parameter = name.Length > 0 ? asset.parameters?.FirstOrDefault(p => p != null && p.name == name) : null;
                if (parameter == null) throw new Failure("“" + name + "” isn't one of the avatar's own parameters: its settings can't change here.");
                Undo.RecordObject(asset, "Save menu");
                if (settings.Default != null) parameter.defaultValue = Clamped(settings.Default.Value, parameter.valueType);
                if (settings.Saved != null) parameter.saved = settings.Saved.Value;
                if (settings.Synced != null) parameter.networkSynced = settings.Synced.Value;
                EditorUtility.SetDirty(asset);
                Touch(outcome, asset);
            }
        }

        // The first setting the avatar doesn't end with as edited (its type, parameter or value, a parameter's or a toggle's
        // settings), or null.
        private static string SettingsDifference(GameObject root, IReadOnlyList<MenuEdit> edits, MenuDraft draft, MenuBuildResult after)
        {
            var wanted = MenuTree.Index(draft.Root);
            var changes = edits.Where(e => e.kind >= MenuEditKind.Type && e.id != null && wanted.ContainsKey(e.id)).ToList();
            if (changes.Count == 0 && draft.Settings.Count == 0) return null;
            var now = MenuTree.Index(after.Root);
            var all = ParametersOf(root);
            foreach (var group in changes.Where(e => e.kind <= MenuEditKind.Value).GroupBy(e => e.id))
            {
                if (group.Key == null || !wanted.TryGetValue(group.Key, out var x) || !now.TryGetValue(group.Key, out var y)) return "an item is missing";
                MenuNode a = x.Node, b = y.Node;
                if (a.Type != b.Type) return "“" + Plain(a.Name) + "” is a " + b.Type + ", not a " + a.Type;
                if (group.Any(e => e.kind == MenuEditKind.Parameter) && (a.Parameter ?? "") != (b.Parameter ?? ""))
                    return "“" + Plain(a.Name) + "” sets “" + b.Parameter + "”, not “" + a.Parameter + "”";
                if (group.Any(e => e.kind == MenuEditKind.Value) && !Mathf.Approximately(Clamped(a.Value, all.FirstOrDefault(p => p != null && p.name == a.Parameter)?.valueType), b.Value))
                    return "the value of “" + Plain(a.Name) + "”";
            }
            foreach (var pair in draft.Settings)
            {
                var settings = pair.Value;
                if (pair.Key.StartsWith(ItemKey, StringComparison.Ordinal))
                {
                    string id = pair.Key.Substring(ItemKey.Length);
                    if (!now.TryGetValue(id, out var entry) || entry.Node.Origin?.Component == null) return "a toggle is missing";
                    var model = VrcFury.Content(entry.Node.Origin.Component);
                    bool slider = entry.Node.Type == ControlType.RadialPuppet;
                    float start = slider ? VrcFury.Field(model, "defaultSliderValue") as float? ?? 0f : (VrcFury.Field(model, "defaultOn") as bool? ?? false) ? 1f : 0f;
                    if (settings.Default != null && !Mathf.Approximately(start, slider ? Mathf.Clamp01(settings.Default.Value) : settings.Default.Value > .5f ? 1f : 0f))
                        return "where “" + Plain(entry.Node.Name) + "” starts";
                    if (settings.Saved != null && (VrcFury.Field(model, "saved") as bool? ?? false) != settings.Saved.Value) return "whether “" + Plain(entry.Node.Name) + "” is saved";
                    continue;
                }
                if (!pair.Key.StartsWith(ParameterKey, StringComparison.Ordinal)) continue;
                string name = pair.Key.Substring(ParameterKey.Length);
                var parameter = all.FirstOrDefault(p => p != null && p.name == name);
                if (parameter == null) return "“" + name + "” is missing";
                if (settings.Default != null && !Mathf.Approximately(parameter.defaultValue, Clamped(settings.Default.Value, parameter.valueType))) return "the default of “" + name + "”";
                if (settings.Saved != null && parameter.saved != settings.Saved.Value) return "whether “" + name + "” is saved";
                if (settings.Synced != null && parameter.networkSynced != settings.Synced.Value) return "whether “" + name + "” is synced";
            }
            return null;
        }

        /// <summary>What the upload leaves out, saved on the avatar: items by where they are in the menu, and the default menu.</summary>
        internal sealed class Removals
        {
            public readonly List<MenuRemoval> Items = new List<MenuRemoval>();
            public bool DefaultMenu;

            /// <summary>Those of <paramref name="draft"/>: its deleted items found where it puts them, with what tells them from a new item.</summary>
            public static Removals Of(MenuDraft draft)
            {
                var removals = new Removals { DefaultMenu = draft.DefaultRemoved };
                var index = MenuTree.Index(draft.Root);
                foreach (var id in draft.Deleted)
                {
                    if (!index.TryGetValue(id, out var entry)) continue;
                    var node = entry.Node;
                    var component = node.Origin?.Component;
                    var removal = new MenuRemoval { type = (int)node.Type, icon = node.Icon, parameter = ItemParameter(node), source = component, fromComponent = component != null };
                    if (MenuPaths.Of(draft.Root, node, removal.path, removal.nth)) removals.Items.Add(removal);
                }
                return removals;
            }
        }

        /// <summary>
        /// The saved removals no item of <paramref name="built"/> answers to now (an accessory left out of the build, an item
        /// deleted since): saving keeps them, so they act again once their item is back; the editor lists them to forget them.
        /// </summary>
        internal static List<MenuRemoval> Unresolved(GameObject root, MenuBuildResult built)
        {
            var settings = SettingsOf(root);
            if (settings == null || settings.menuRemovals.Count == 0) return new List<MenuRemoval>();
            return settings.menuRemovals.Where(r => r != null && MenuPaths.Resolve(built.Root, r) == null).ToList();
        }

        private static void WriteRemovals(GameObject root, Removals removals, List<MenuRemoval> kept)
        {
            var items = removals.Items.Concat(kept ?? new List<MenuRemoval>()).ToList();
            var settings = SettingsOf(root);
            if (settings == null)
            {
                if (items.Count == 0 && !removals.DefaultMenu) return;
                settings = Undo.AddComponent<MyAvatar>(root);
            }
            Undo.RecordObject(settings, "Save menu");
            settings.menuRemovals = items;
            settings.removeDefaultMenu = removals.DefaultMenu;
            EditorUtility.SetDirty(settings);
            PrefabUtility.RecordPrefabInstancePropertyModifications(settings);
        }

        /// <summary>The items <paramref name="root"/>'s upload leaves out, by id in <paramref name="built"/> (those still found).</summary>
        internal static List<string> SavedRemovals(GameObject root, MenuBuildResult built, out bool defaultMenu)
        {
            var settings = SettingsOf(root);
            defaultMenu = settings != null && settings.removeDefaultMenu;
            var ids = new List<string>();
            if (settings == null) return ids;
            foreach (var removal in settings.menuRemovals)
            {
                var node = MenuPaths.Resolve(built.Root, removal);
                if (node != null && !ids.Contains(node.Id)) ids.Add(node.Id);
            }
            return ids;
        }
    }
}
