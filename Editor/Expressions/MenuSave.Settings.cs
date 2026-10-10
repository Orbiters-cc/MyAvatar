using System;
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
    /// <summary>What an item is and which of its settings the editor can change, where they are written.</summary>
    internal sealed class MenuItemInfo
    {
        /// <summary>The parameter the item drives (a slider's value when it sets nothing else), empty when none.</summary>
        public string Parameter = "";
        public VRCExpressionParameters.ValueType? ParameterType;
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

        // The project's own assets, or ones only in memory (made by a tool for this avatar): never a package's.
        private static bool InProject(UnityEngine.Object asset)
        {
            if (asset == null) return false;
            string path = AssetDatabase.GetAssetPath(asset);
            return path.Length == 0 || path.StartsWith("Assets/", StringComparison.Ordinal);
        }

        /// <summary>What <paramref name="node"/> is (with <paramref name="draft"/>'s changes) and what the editor can change.</summary>
        internal static MenuItemInfo Info(GameObject root, MenuNode node, MenuDraft draft)
        {
            var info = new MenuItemInfo { Parameter = ItemParameter(node), Value = node.Value };
            if (node == null || root == null) return info;
            var origin = node.Origin;
            var parameters = root.GetComponent<VRCAvatarDescriptor>()?.expressionParameters;
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
            var parameter = parameters != null ? parameters.parameters?.FirstOrDefault(p => p != null && p.name == info.Parameter) : null;
            if (parameter != null)
            {
                info.ParameterType = parameter.valueType;
                info.Default = overrides?.Default ?? parameter.defaultValue;
                info.Saved = overrides?.Saved ?? parameter.saved;
                info.Synced = overrides?.Synced ?? parameter.networkSynced;
                info.DefaultEditable = info.SavedEditable = info.SyncedEditable = InProject(parameters);
            }
            bool control = (origin?.Kind == MenuOriginKind.AvatarMenu || origin?.Kind == MenuOriginKind.MergedMenu) && origin.Control != null && InProject(origin.Asset);
            if (control && (node.Type == ControlType.Toggle || node.Type == ControlType.Button))
            {
                info.Types.AddRange(new[] { ControlType.Toggle, ControlType.Button });
                if (parameters != null && InProject(parameters)) info.Parameters = parameters.parameters.Where(p => p != null && !string.IsNullOrEmpty(p.name)).ToList();
                info.ValueEditable = parameter != null && parameter.valueType != VRCExpressionParameters.ValueType.Bool;
            }
            if (info.Types.Count == 0 && !info.DefaultEditable && node.Type != ControlType.SubMenu)
                info.Why = origin?.Asset != null && !InProject(origin.Asset) ? "It comes from a package: its settings stay as its creator made them."
                    : parameter == null && info.Parameter.Length > 0 ? "Its parameter is made by the tool that adds it, not one of the avatar's own."
                    : null;
            return info;
        }

        // Type, parameter and value of the items, and their parameters' settings, written where they live (one Undo group).
        private static void WriteSettings(GameObject root, IReadOnlyList<MenuEdit> edits, IReadOnlyDictionary<VRCExpressionsMenu.Control, string> pins, Outcome outcome)
        {
            var settings = edits.Where(e => e.kind >= MenuEditKind.Type).ToList();
            if (settings.Count == 0) return;
            var index = MenuTree.Index(MenuBuild.Run(MenuInputs.Read(root), new MenuBuildOptions { Pins = pins }).Root);
            var parameters = root.GetComponent<VRCAvatarDescriptor>()?.expressionParameters;
            foreach (var edit in settings)
            {
                if (edit.id == null || !index.TryGetValue(edit.id, out var entry)) throw new Failure("An item whose settings changed is no longer in the menu.");
                var node = entry.Node;
                var origin = node.Origin;
                if (origin?.Kind == MenuOriginKind.Toggle && origin.Component != null)
                {
                    if (!UsersOwn(origin.Component)) throw new Failure("“" + Plain(node.Name) + "” comes from a package's prefab: its settings can't change here.");
                    var serialized = new SerializedObject(origin.Component);
                    switch (edit.kind)
                    {
                        case MenuEditKind.Type:
                            var type = (ControlType)edit.index;
                            serialized.FindProperty("content.slider").boolValue = type == ControlType.RadialPuppet;
                            serialized.FindProperty("content.holdButton").boolValue = type == ControlType.Button;
                            break;
                        case MenuEditKind.Default:
                            if (node.Type == ControlType.RadialPuppet) serialized.FindProperty("content.defaultSliderValue").floatValue = Mathf.Clamp01(edit.number);
                            else serialized.FindProperty("content.defaultOn").boolValue = edit.number > .5f;
                            break;
                        case MenuEditKind.Saved:
                            serialized.FindProperty("content.saved").boolValue = edit.flag;
                            break;
                        default:
                            throw new Failure("A VRCFury toggle makes its own parameter: “" + Plain(node.Name) + "” can't drive another one.");
                    }
                    serialized.ApplyModifiedProperties();
                    outcome.Components++;
                    continue;
                }
                if (edit.kind == MenuEditKind.Type || edit.kind == MenuEditKind.Parameter || edit.kind == MenuEditKind.Value)
                {
                    if ((origin?.Kind != MenuOriginKind.AvatarMenu && origin?.Kind != MenuOriginKind.MergedMenu) || origin.Control == null || !InProject(origin.Asset))
                        throw new Failure("“" + Plain(node.Name) + "” comes from a package: its type and parameter can't change here.");
                    Undo.RecordObject(origin.Asset, "Save menu");
                    var control = origin.Control;
                    if (edit.kind == MenuEditKind.Type) control.type = (ControlType)edit.index;
                    else if (edit.kind == MenuEditKind.Parameter) control.parameter = new VRCExpressionsMenu.Control.Parameter { name = edit.text ?? "" };
                    else control.value = edit.number;
                    EditorUtility.SetDirty(origin.Asset);
                    Touch(outcome, origin.Asset);
                    continue;
                }
                var parameter = parameters != null ? parameters.parameters?.FirstOrDefault(p => p != null && p.name == ItemParameter(node)) : null;
                if (parameter == null || !InProject(parameters)) throw new Failure("“" + Plain(node.Name) + "” drives a parameter that isn't the avatar's own: its settings can't change here.");
                Undo.RecordObject(parameters, "Save menu");
                if (edit.kind == MenuEditKind.Default) parameter.defaultValue = edit.number;
                else if (edit.kind == MenuEditKind.Saved) parameter.saved = edit.flag;
                else parameter.networkSynced = edit.flag;
                EditorUtility.SetDirty(parameters);
                Touch(outcome, parameters);
            }
        }

        /// <summary>What the upload leaves out, saved on the avatar: items by where they are in the menu, and the default menu.</summary>
        internal sealed class Removals
        {
            public readonly List<MenuRemoval> Items = new List<MenuRemoval>();
            public bool DefaultMenu;

            /// <summary>Those of <paramref name="draft"/>: its deleted items found where it puts them.</summary>
            public static Removals Of(MenuDraft draft)
            {
                var removals = new Removals { DefaultMenu = draft.DefaultRemoved };
                var index = MenuTree.Index(draft.Root);
                foreach (var id in draft.Deleted)
                {
                    if (!index.TryGetValue(id, out var entry)) continue;
                    var removal = new MenuRemoval { type = (int)entry.Node.Type, icon = entry.Node.Icon, parameter = ItemParameter(entry.Node) };
                    if (MenuPaths.Of(draft.Root, entry.Node, removal.path, removal.nth)) removals.Items.Add(removal);
                }
                return removals;
            }
        }

        private static void WriteRemovals(GameObject root, Removals removals)
        {
            var settings = root.GetComponentInChildren<MyAvatar>(true);
            if (settings == null)
            {
                if (removals.Items.Count == 0 && !removals.DefaultMenu) return;
                settings = Undo.AddComponent<MyAvatar>(root);
            }
            Undo.RecordObject(settings, "Save menu");
            settings.menuRemovals = removals.Items;
            settings.removeDefaultMenu = removals.DefaultMenu;
            EditorUtility.SetDirty(settings);
            PrefabUtility.RecordPrefabInstancePropertyModifications(settings);
        }

        /// <summary>The items <paramref name="root"/>'s upload leaves out, by id in <paramref name="built"/> (those still found).</summary>
        internal static List<string> SavedRemovals(GameObject root, MenuBuildResult built, out bool defaultMenu)
        {
            var settings = root != null ? root.GetComponentInChildren<MyAvatar>(true) : null;
            defaultMenu = settings != null && settings.removeDefaultMenu;
            var ids = new List<string>();
            if (settings == null) return ids;
            foreach (var removal in settings.menuRemovals)
            {
                var node = removal != null ? MenuPaths.Find(built.Root, removal.path, removal.nth) : null;
                if (node != null) ids.Add(node.Id);
            }
            return ids;
        }
    }
}
