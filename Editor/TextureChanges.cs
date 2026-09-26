using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal static class TextureChanges
    {
        internal static int Apply(MyAvatar avatar, List<TextureEntry> entries, string folder)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
                throw new InvalidOperationException("Apply textures to an avatar in an open scene, outside Play Mode.");
            var slots = TextureMatching.Slots(avatar);
            var applicable = entries.Where(e => !e.applied && e.texture && e.material && slots.Any(s => s.material == e.material && s.property == e.property)).ToList();
            if (applicable.Count == 0) return 0;
            if (applicable.GroupBy(e => (e.material, e.property)).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Choose only one texture for each material slot.");
            Directory.CreateDirectory(folder + "/Materials"); AssetDatabase.Refresh();
            var replacements = new Dictionary<Material, Material>();
            var snapshots = new List<RendererSnapshot>();
            try
            {
                foreach (var group in applicable.GroupBy(e => e.material))
                {
                    var copy = new Material(group.Key) { name = group.Key.name };
                    string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/Materials/" + SafeName(copy.name) + ".mat");
                    AssetDatabase.CreateAsset(copy, path); replacements.Add(group.Key, copy);
                    foreach (var entry in group)
                    {
                        copy.SetTexture(entry.property, entry.texture);
                        if (entry.property == "_BumpMap") copy.EnableKeyword("_NORMALMAP");
                        if (entry.property == "_MetallicGlossMap") copy.EnableKeyword("_METALLICGLOSSMAP");
                        if (entry.property == "_SpecGlossMap") copy.EnableKeyword("_SPECGLOSSMAP");
                        if (entry.property == "_EmissionMap")
                        {
                            copy.EnableKeyword("_EMISSION");
                            if (copy.HasProperty("_EmissionColor") && copy.GetColor("_EmissionColor").maxColorComponent == 0) copy.SetColor("_EmissionColor", Color.white);
                            copy.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                        }
                    }
                    EditorUtility.SetDirty(copy);
                }
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                {
                    var before = renderer.sharedMaterials;
                    if (!before.Any(m => m && replacements.ContainsKey(m))) continue;
                    snapshots.Add(new RendererSnapshot { renderer = renderer, before = before,
                        after = before.Select(m => m && replacements.TryGetValue(m, out var copy) ? copy : m).ToArray() });
                }
                Undo.IncrementCurrentGroup(); int groupId = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("My Avatar: apply textures");
                Undo.RecordObjects(new UnityEngine.Object[] { avatar }.Concat(snapshots.Select(s => (UnityEngine.Object)s.renderer)).ToArray(), "My Avatar: apply textures");
                foreach (var state in snapshots) { state.renderer.sharedMaterials = state.after; PrefabUtility.RecordPrefabInstancePropertyModifications(state.renderer); }
                avatar.undoMaterials = snapshots;
                foreach (var entry in entries)
                {
                    entry.appliedBeforeLast = entry.applied;
                    entry.materialBeforeLast = entry.material;
                    entry.propertyBeforeLast = entry.property;
                    entry.reasonBeforeLast = entry.reason;
                    if (entry.material && replacements.TryGetValue(entry.material, out var replacement)) entry.material = replacement;
                    if (applicable.Contains(entry)) entry.applied = true;
                    else if (entry.applied && entry.material && entry.material.GetTexture(entry.property) != entry.texture)
                    {
                        entry.applied = false; entry.material = null; entry.property = null;
                        entry.reason = "Another texture was chosen for this slot.";
                    }
                }
                avatar.textures = entries; avatar.batchFolder = folder;
                Dirty(avatar); AssetDatabase.SaveAssets(); Undo.CollapseUndoOperations(groupId);
                return applicable.Count;
            }
            catch
            {
                foreach (var state in snapshots) if (state.renderer) state.renderer.sharedMaterials = state.before;
                // Keep generated files for recovery; never delete assets other objects may reference.
                throw;
            }
        }

        internal static void UndoLast(MyAvatar avatar)
        {
            var snapshots = avatar.undoMaterials;
            if (snapshots.Count == 0) return;
            if (snapshots.Any(s => !s.renderer || !s.renderer.sharedMaterials.SequenceEqual(s.after)))
                throw new InvalidOperationException("Material assignments changed since this apply. Restore those assignments before using Undo, to keep your later edits.");
            Undo.RecordObjects(new UnityEngine.Object[] { avatar }.Concat(snapshots.Select(s => (UnityEngine.Object)s.renderer)).ToArray(), "My Avatar: undo textures");
            foreach (var state in snapshots)
            {
                state.renderer.sharedMaterials = state.before; PrefabUtility.RecordPrefabInstancePropertyModifications(state.renderer);
            }
            foreach (var entry in avatar.textures)
            {
                entry.material = entry.materialBeforeLast; entry.property = entry.propertyBeforeLast;
                entry.reason = entry.reasonBeforeLast; entry.applied = entry.appliedBeforeLast;
            }
            avatar.undoMaterials = new List<RendererSnapshot>();
            avatar.notice = "Last apply undone. Imported textures remain available in the project."; Dirty(avatar);
        }

        internal static async Task<string> SaveAsync(MyAvatar avatar)
        {
            var scene = avatar.gameObject.scene;
            if (string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save this scene in Assets first, then click Save.");
            string root = Path.GetDirectoryName(Application.dataPath);
            if (!File.Exists(Path.Combine(root, ".git")) && !Directory.Exists(Path.Combine(root, ".git")))
                throw new InvalidOperationException("Initialize the Unity project's Git repository in Unit Git first, then click Save again.");
            AssetDatabase.SaveAssets();
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("The avatar scene could not be saved.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { scene.path, scene.path + ".meta" };
            // Include every referenced generated batch, including materials kept for Undo.
            foreach (var dependency in AssetDatabase.GetDependencies(scene.path, true).Where(p => p.StartsWith("Assets/Orbiters/MyAvatar/", StringComparison.Ordinal)))
            { paths.Add(dependency); if (File.Exists(dependency + ".meta")) paths.Add(dependency + ".meta"); }
            if (!string.IsNullOrEmpty(avatar.batchFolder) && Directory.Exists(avatar.batchFolder))
                foreach (var file in Directory.EnumerateFiles(avatar.batchFolder, "*", SearchOption.AllDirectories)) paths.Add(file.Replace('\\', '/'));
            foreach (var path in paths.ToArray())
            {
                string parent = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(parent) && parent != "Assets")
                { if (File.Exists(parent + ".meta")) paths.Add((parent + ".meta").Replace('\\', '/')); parent = Path.GetDirectoryName(parent); }
            }
            var result = await UnitGitReleases.CommitProjectFilesAsync(root, "texture change", paths.Where(File.Exists).ToArray());
            if (!result.Success) throw new InvalidOperationException(result.Message);
            return "Saved · commit " + result.CommitHash.Substring(0, Math.Min(8, result.CommitHash.Length));
        }

        internal static void Dirty(MyAvatar avatar)
        { EditorUtility.SetDirty(avatar); PrefabUtility.RecordPrefabInstancePropertyModifications(avatar); EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene); }
        private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
