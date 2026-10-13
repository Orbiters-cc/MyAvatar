using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.MCP
{
    /// <summary>
    /// The avatar's materials that don't render, through MCP: the same detection, plan and one-step fix as the Materials card
    /// in My Avatar's Inspector, on any scene avatar (a My Avatar component is not needed).
    /// </summary>
    [McpForUnityTool("myavatar_materials", Description = "Find and fix an avatar's materials that don't render (pink: shader missing, failing to compile or unsupported, including locked Poiyomi materials whose generated shader is missing). inspect lists each one with the shader it was made for (read from its saved properties and shader GUID), the installed shader it would move to, whether every setting is kept (same family) or only texture, color, normal map, emission and blending (another family), what is not carried over, and the VPM package that would restore it exactly. fix applies the plan in one Undo step, the same as My Avatar's 'Fix materials' button: project materials are changed where they are (the original is kept beside each as 'name (old).mat', labelled MyAvatarBackup and never listed as broken), materials in packages or models are copied into Assets/Orbiters/MyAvatar/MaterialEdits for this avatar. target: instance ID, or hierarchy path 'Root/Child' or 'Scene:Root/Child'; the VRChat avatar root above it is used.")]
    public static class MaterialsTool
    {
        public sealed class Parameters
        {
            [ToolParameter("inspect or fix")] public string action { get; set; }
            [ToolParameter("The avatar: instance ID of a GameObject or component, or hierarchy path 'Root/Child' or 'Scene:Root/Child'")] public string target { get; set; }
            [ToolParameter("fix: only these materials (names or asset paths, an array or a comma-separated string); default every material that can be fixed", Required = false)] public object materials { get; set; }
        }

        public static object HandleCommand(JObject arguments)
        {
            try
            {
                var p = arguments?.ToObject<Parameters>() ?? throw new ArgumentException("Parameters are required.");
                var root = BrokenMaterials.AvatarRoot(SceneReference.Resolve(p.target));
                if (!root) throw new ArgumentException("target is not an object of an open scene: give an instance ID or a hierarchy path such as 'Avatar' or 'Scene:Avatar'.");
                var broken = BrokenMaterials.Inspect(root);
                switch (p.action)
                {
                    case "inspect":
                        return new SuccessResponse(broken.Count == 0 ? root.name + ": every material renders." : root.name + ": " + broken.Count + " material(s) don't render.",
                            new { avatar = HierarchyPath(root), materials = broken.Select(Describe).ToList() });
                    case "fix":
                    {
                        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                            throw new InvalidOperationException("Wait for Unity to be idle in edit mode.");
                        var chosen = Choose(broken, Names(p.materials));
                        int count = BrokenMaterials.Fix(root, root.GetComponentInChildren<MyAvatar>(true), chosen);
                        var plans = chosen.Select(Describe).ToList();
                        var after = BrokenMaterials.Inspect(root);
                        return new SuccessResponse(count + " material(s) fixed in one Undo step (\"Fix " + root.name + " materials\"); the Console has the details.",
                            new { avatar = HierarchyPath(root), repaired = plans, stillBroken = after.Select(Describe).ToList() });
                    }
                    default:
                        throw new ArgumentException("action must be inspect or fix.");
                }
            }
            catch (Exception ex) { return new ErrorResponse(ex.Message); }
        }

        private static object Describe(BrokenMaterial entry) => new
        {
            material = entry.Material.name,
            path = entry.AssetPath,
            renderers = entry.Renderers.Where(r => r).Select(r => r.name).Distinct().ToList(),
            problem = entry.Problem,
            madeFor = entry.MadeFor,
            family = entry.Family.ToString(),
            locked = entry.Locked,
            target = entry.Target ? entry.Target.name : null,
            keepsEverySetting = entry.Rebind,
            originalKeptAs = entry.Backup,
            copiedForThisAvatar = entry.Copy,
            kept = entry.Kept,
            notCarriedOver = entry.Lost,
            installToRestore = entry.Installable,
            change = entry.Change,
            details = entry.Details,
        };

        private static List<BrokenMaterial> Choose(List<BrokenMaterial> broken, List<string> names)
        {
            var fixable = broken.Where(b => b.Target).ToList();
            if (names.Count == 0) return fixable;
            var unknown = names.Where(n => !broken.Any(b => Matches(b, n))).ToList();
            if (unknown.Count > 0) throw new ArgumentException("Not among this avatar's materials that don't render: " + string.Join(", ", unknown) + ". inspect lists them.");
            var cannot = broken.Where(b => !b.Target && names.Any(n => Matches(b, n))).ToList();
            if (cannot.Count > 0) throw new ArgumentException(string.Join(" ", cannot.Select(b => b.Material.name + ": " + b.Reason)));
            return fixable.Where(b => names.Any(n => Matches(b, n))).ToList();
        }

        private static bool Matches(BrokenMaterial entry, string name) =>
            string.Equals(entry.Material.name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.AssetPath, name, StringComparison.OrdinalIgnoreCase);

        private static List<string> Names(object value)
        {
            // Names as written, whatever the editor's language (a number is not formatted its way).
            string Text(object item) => item is JValue v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : Convert.ToString(item, CultureInfo.InvariantCulture);
            IEnumerable<string> names = value switch
            {
                JArray array => array.Select(Text),
                string text => text.Split(','),
                JValue single when single.Type == JTokenType.String => Text(single).Split(','),
                null => Enumerable.Empty<string>(),
                _ => new[] { Text(value) },
            };
            return names.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var names = new List<string>();
            for (var t = gameObject.transform; t; t = t.parent) names.Insert(0, t.name);
            return gameObject.scene.name + ":" + string.Join("/", names);
        }
    }
}
