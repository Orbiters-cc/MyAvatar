using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
#if MYAVATAR_UNITGIT
using Orbiters.UnitGit.Editor;
#endif
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal static class TextureChanges
    {
        internal sealed class Change { public TextureEntry entry; public TextureSlot slot; public float confidence; public string reason; }
        internal sealed class SlotState { public TextureSlot slot; public Material material; public Texture texture; }

        // A texture set limited to some objects (an accessory drop) only changes their renderers: other objects keep a
        // material they share with them as it is.
        internal static bool InScope(Renderer renderer, List<Transform> scope) => scope == null || scope.Count == 0 || scope.Any(t => t && renderer.transform.IsChildOf(t));

        internal static List<TextureSlot> Scoped(List<TextureSlot> slots, List<Transform> scope) =>
            scope == null || scope.Count == 0 ? slots : slots.Where(s => s.parts.Any(p => p.renderer && InScope(p.renderer, scope))).ToList();

        /// <summary>The slots of the current texture set: the whole avatar, or the objects its drop was limited to.</summary>
        internal static List<TextureSlot> Slots(MyAvatar avatar) => Scoped(TextureMatching.Slots(avatar), avatar.batchScope);

        // Applies pending entries. Applying again to the same batch extends the same logical operation: its generated
        // materials are reused and the Undo/Redo snapshot keeps the state from before the batch. A new batch passes its scope.
        internal static int Apply(MyAvatar avatar, List<TextureEntry> entries, string folder, List<Transform> scope = null, bool splitShared = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar) || !avatar.gameObject.scene.IsValid())
                throw new InvalidOperationException("Apply textures to an avatar in an open scene, outside Play Mode.");
            scope = scope ?? avatar.batchScope;
            bool extend = avatar.batchFolder == folder && !avatar.canRedo && avatar.undoMaterials.Count > 0;
            var copyToOriginal = extend ? CopyMap(avatar) : new Dictionary<Material, Material>();
            // A generated material can become shared after the first apply (duplicating a renderer or the whole avatar).
            // Reusing it would modify those excluded objects too, so split it again for the current scope.
            var outside = !splitShared ? new HashSet<Material>() : avatar.GetComponentsInChildren<Renderer>(true).Where(r => !InScope(r, scope))
                .Concat(SceneRenderers(avatar.gameObject.scene).Where(r => !r.transform.IsChildOf(avatar.transform)))
                .SelectMany(r => r.sharedMaterials).Where(m => m).ToHashSet();
            var reusable = new HashSet<Material>(copyToOriginal.Keys.Where(m => !outside.Contains(m)));
            var originalToCopy = new Dictionary<Material, Material>();
            foreach (var pair in copyToOriginal) originalToCopy[pair.Value] = pair.Key;
            foreach (var entry in entries.Where(e => !e.applied && e.material && originalToCopy.ContainsKey(e.material))) entry.material = originalToCopy[entry.material];
            var slots = Scoped(TextureMatching.Slots(avatar), scope);
            var applicable = entries.Where(e => !e.applied && e.texture && e.material && slots.Any(s => s.material == e.material && s.property == e.property)).ToList();
            if (applicable.Count == 0) return 0;
            if (applicable.GroupBy(e => (e.material, e.property)).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Choose only one texture for each material slot.");
            TextureImport.EnsureFolder(folder + "/Materials");
            var replacements = new Dictionary<Material, Material>();
            var reused = applicable.Select(e => e.material).Where(reusable.Contains).Distinct().ToArray();
            var snapshots = avatar.undoMaterials.Where(_ => extend).Select(s => new RendererSnapshot { renderer = s.renderer, before = s.before, after = s.after }).ToList();
            var previous = snapshots.Select(s => s.after).ToList();
            try
            {
                var created = new List<(Material copy, string path)>();
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in applicable.Where(e => !reusable.Contains(e.material)).GroupBy(e => e.material))
                {
                    var copy = new Material(group.Key) { name = group.Key.name };
                    foreach (var entry in group) Assign(copy, entry.property, entry.texture);
                    string name = SafeName(copy.name), path = folder + "/Materials/" + name + ".mat";
                    for (int i = 1; File.Exists(path) || !paths.Add(path); i++) path = folder + "/Materials/" + name + " " + i + ".mat";
                    created.Add((copy, path)); replacements.Add(group.Key, copy);
                }
                // Materials are complete before creation, so each is written and imported once, in one batch.
                AssetDatabase.StartAssetEditing();
                try { foreach (var (copy, path) in created) AssetDatabase.CreateAsset(copy, path); }
                finally { AssetDatabase.StopAssetEditing(); }
                var renderers = avatar.GetComponentsInChildren<Renderer>(true).Where(r => InScope(r, scope) && r.sharedMaterials.Any(m => m && replacements.ContainsKey(m))).ToList();
                Undo.IncrementCurrentGroup(); int groupId = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("My Avatar: apply textures");
                Undo.RecordObjects(new UnityEngine.Object[] { avatar }.Concat(renderers).Concat(reused).ToArray(), "My Avatar: apply textures");
                foreach (var entry in applicable.Where(e => reusable.Contains(e.material))) Assign(entry.material, entry.property, entry.texture);
                foreach (var renderer in renderers)
                {
                    var current = renderer.sharedMaterials;
                    var after = current.Select(m => m && replacements.TryGetValue(m, out var copy) ? copy : m).ToArray();
                    var snapshot = snapshots.FirstOrDefault(s => s.renderer == renderer);
                    if (snapshot == null) snapshots.Add(snapshot = new RendererSnapshot { renderer = renderer, before = current });
                    snapshot.after = after;
                    renderer.sharedMaterials = after; PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }
                avatar.undoMaterials = snapshots;
                avatar.canRedo = false;
                foreach (var entry in entries)
                {
                    if (!extend)
                    {
                        entry.appliedBeforeLast = entry.applied;
                        entry.materialBeforeLast = entry.material;
                        entry.propertyBeforeLast = entry.property;
                        entry.reasonBeforeLast = entry.reason;
                    }
                    if (entry.material && replacements.TryGetValue(entry.material, out var replacement)) entry.material = replacement;
                    if (applicable.Contains(entry)) entry.applied = true;
                    else if (entry.applied && entry.material && entry.material.GetTexture(entry.property) != entry.texture)
                    {
                        entry.applied = false; entry.material = null; entry.property = null;
                        entry.reason = "Another texture was chosen for this slot.";
                    }
                }
                avatar.textures = entries; avatar.batchFolder = folder; avatar.batchScope = scope;
                Dirty(avatar);
                foreach (var material in reused) AssetDatabase.SaveAssetIfDirty(material);
                Undo.CollapseUndoOperations(groupId);
                return applicable.Count;
            }
            catch
            {
                for (int i = 0; i < previous.Count; i++) if (snapshots[i].renderer) snapshots[i].renderer.sharedMaterials = previous[i];
                foreach (var state in snapshots.Skip(previous.Count)) if (state.renderer) state.renderer.sharedMaterials = state.before;
                // Keep generated files for recovery; never delete assets other objects may reference.
                throw;
            }
        }

        // What the slots sent to AI show when it is asked, on each material and on the copy this batch made of it.
        internal static List<SlotState> Capture(MyAvatar avatar, IEnumerable<TextureSlot> slots)
        {
            var copies = new Dictionary<Material, Material>();
            foreach (var pair in CopyMap(avatar)) copies[pair.Value] = pair.Key;
            var states = new List<SlotState>();
            foreach (var slot in slots)
            {
                states.Add(new SlotState { slot = slot, material = slot.material, texture = TextureOf(slot.material, slot.property) });
                if (slot.material && copies.TryGetValue(slot.material, out var copy)) states.Add(new SlotState { slot = slot, material = copy, texture = TextureOf(copy, slot.property) });
            }
            return states;
        }

        private static bool Unchanged(List<SlotState> states, TextureSlot slot) =>
            states.Where(s => s.slot == slot).All(s => s.material && TextureOf(s.material, slot.property) == s.texture);

        // Every renderer of the open scenes and of the avatar's own scene (a preview scene is not among the open ones).
        private static IEnumerable<Renderer> SceneRenderers(UnityEngine.SceneManagement.Scene avatarScene)
        {
            var scenes = new List<UnityEngine.SceneManagement.Scene> { avatarScene };
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (scene != avatarScene) scenes.Add(scene);
            }
            foreach (var scene in scenes.Where(s => s.IsValid() && s.isLoaded))
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) yield return renderer;
        }

        private static Texture TextureOf(Material material, string property) => material && material.HasProperty(property) ? material.GetTexture(property) : null;

        // Merges background AI answers into the batch already applied locally, as one extra Undo step. An answer for a slot
        // whose texture changed since the request (a material Inspector edit) is not applied: the newer choice stands.
        // Intended: answers edit the batch's generated materials in place, without the shared-material split of Apply.
        // An avatar duplicated while the answers were pending (a creator quickly branching one setup for two uses) shares
        // those materials and gets the same, better matches instead of being left with the first guesses. Not a bug.
        internal static int Revise(MyAvatar avatar, List<TextureEntry> entries, string folder, List<Change> changes, List<SlotState> before)
        {
            if (avatar.batchFolder != folder || avatar.canRedo || !ReferenceEquals(avatar.textures, entries) ||
                avatar.undoMaterials.Any(s => !s.renderer || !s.renderer.sharedMaterials.SequenceEqual(s.after)))
                throw new InvalidOperationException("Materials changed after the local apply, so the Orbiters AI suggestions were not applied.");
            var copyToOriginal = CopyMap(avatar);
            Material Original(Material material) => material && copyToOriginal.TryGetValue(material, out var original) ? original : material;
            Undo.IncrementCurrentGroup(); int groupId = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("My Avatar: Orbiters AI matches");
            Undo.RecordObjects(new UnityEngine.Object[] { avatar }.Concat(copyToOriginal.Keys).ToArray(), "My Avatar: Orbiters AI matches");
            // Checked before this merge changes anything itself.
            var edited = new HashSet<Change>(changes.Where(c => !Unchanged(before, c.slot)));
            int accepted = 0;
            foreach (var change in changes.Where(c => !c.entry.dismissed))
            {
                var occupant = entries.FirstOrDefault(e => e != change.entry && e.material && Original(e.material) == change.slot.material && e.property == change.slot.property);
                // Choices the user confirmed in an earlier apply outrank the model.
                if (change.entry.reason == TextureMatching.RememberedReason || occupant?.reason == TextureMatching.RememberedReason) continue;
                if (edited.Contains(change))
                {
                    if (!change.entry.applied)
                    {
                        change.entry.suggestedMaterialName = change.slot.materialName; change.entry.suggestedProperty = change.slot.property;
                        change.entry.reason = $"Orbiters AI suggested {change.slot.materialName} / {change.slot.description}, which was changed meanwhile. Choose a slot below.";
                    }
                    continue;
                }
                if (occupant != null) { Unassign(occupant, copyToOriginal); occupant.reason = "Orbiters AI placed " + change.entry.fileName + " in this slot instead."; }
                Unassign(change.entry, copyToOriginal);
                change.entry.material = change.slot.material; change.entry.property = change.slot.property; change.entry.confidence = change.confidence;
                change.entry.suggestedMaterialName = change.slot.materialName; change.entry.suggestedProperty = change.slot.property;
                change.entry.reason = "Orbiters AI · " + change.reason;
                accepted++;
            }
            if (accepted > 0)
            {
                Apply(avatar, entries, folder, splitShared: false);
                foreach (var copy in copyToOriginal.Keys) AssetDatabase.SaveAssetIfDirty(copy);
                Dirty(avatar);
            }
            Undo.CollapseUndoOperations(groupId);
            return accepted;
        }

        private static void Unassign(TextureEntry entry, Dictionary<Material, Material> copyToOriginal)
        {
            if (entry.applied && entry.material && copyToOriginal.TryGetValue(entry.material, out var original))
            {
                var copy = entry.material;
                copy.SetTexture(entry.property, original.GetTexture(entry.property));
                string keyword = Keyword(entry.property);
                if (keyword != null) { if (original.IsKeywordEnabled(keyword)) copy.EnableKeyword(keyword); else copy.DisableKeyword(keyword); }
                if (entry.property == "_EmissionMap")
                {
                    if (copy.HasProperty("_EmissionColor")) copy.SetColor("_EmissionColor", original.GetColor("_EmissionColor"));
                    copy.globalIlluminationFlags = original.globalIlluminationFlags;
                }
                EditorUtility.SetDirty(copy);
            }
            entry.applied = false; entry.material = null; entry.property = null; entry.confidence = 0;
        }

        private static void Assign(Material copy, string property, Texture texture)
        {
            copy.SetTexture(property, texture);
            string keyword = Keyword(property);
            if (keyword != null) copy.EnableKeyword(keyword);
            if (property == "_EmissionMap")
            {
                if (copy.HasProperty("_EmissionColor") && copy.GetColor("_EmissionColor").maxColorComponent == 0) copy.SetColor("_EmissionColor", Color.white);
                copy.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            EditorUtility.SetDirty(copy);
        }

        private static string Keyword(string property) => property == "_BumpMap" ? "_NORMALMAP" : property == "_MetallicGlossMap" ? "_METALLICGLOSSMAP" :
            property == "_SpecGlossMap" ? "_SPECGLOSSMAP" : property == "_EmissionMap" ? "_EMISSION" : null;

        // Generated material → the material it replaced, from the current batch snapshot.
        private static Dictionary<Material, Material> CopyMap(MyAvatar avatar)
        {
            var map = new Dictionary<Material, Material>();
            foreach (var state in avatar.undoMaterials.Where(s => s.before != null && s.after != null && s.before.Length == s.after.Length))
                for (int i = 0; i < state.after.Length; i++)
                    if (state.after[i] && state.before[i] && state.after[i] != state.before[i]) map[state.after[i]] = state.before[i];
            return map;
        }

        internal static void UndoLast(MyAvatar avatar)
        {
            var snapshots = avatar.undoMaterials;
            if (snapshots.Count == 0) return;
            bool redo = avatar.canRedo;
            if (snapshots.Any(s => !s.renderer || !s.renderer.sharedMaterials.SequenceEqual(redo ? s.before : s.after)))
                throw new InvalidOperationException("Material assignments changed since this action. Restore those assignments before using Undo or Redo, to keep your later edits.");
            Undo.RecordObjects(new UnityEngine.Object[] { avatar }.Concat(snapshots.Select(s => (UnityEngine.Object)s.renderer)).ToArray(), redo ? "My Avatar: redo textures" : "My Avatar: undo textures");
            foreach (var state in snapshots)
            {
                state.renderer.sharedMaterials = redo ? state.after : state.before; PrefabUtility.RecordPrefabInstancePropertyModifications(state.renderer);
            }
            foreach (var entry in avatar.textures)
            {
                var material = entry.material; var property = entry.property;
                var reason = entry.reason; bool applied = entry.applied;
                entry.material = entry.materialBeforeLast; entry.property = entry.propertyBeforeLast;
                entry.reason = entry.reasonBeforeLast; entry.applied = entry.appliedBeforeLast;
                entry.materialBeforeLast = material; entry.propertyBeforeLast = property;
                entry.reasonBeforeLast = reason; entry.appliedBeforeLast = applied;
            }
            avatar.canRedo = !redo;
            avatar.notice = redo ? "Last apply restored." : "Last apply undone. Click Redo to restore it without importing or matching again."; Dirty(avatar);
        }

        // Unit Git is optional: without it Save stores the scene and generated assets; with it, Save also records a local commit.
#if MYAVATAR_UNITGIT
        internal const bool Commits = true;
#else
        internal const bool Commits = false;
#endif

        internal static Task<string> SaveAsync(MyAvatar avatar)
        {
            var scene = avatar.gameObject.scene;
            if (string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save this scene in Assets first, then click Save.");
            AssetDatabase.SaveAssets();
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("The avatar scene could not be saved.");
#if MYAVATAR_UNITGIT
            string root = Path.GetDirectoryName(Application.dataPath);
            if (!File.Exists(Path.Combine(root, ".git")) && !Directory.Exists(Path.Combine(root, ".git")))
                return Task.FromResult("Saved. Initialize the project's Git repository in Unit Git to also record a checkpoint.");
            return CommitAsync(avatar, root);
#else
            return Task.FromResult("Saved.");
#endif
        }

        // What a checkpoint records: the scene, the dropped textures, generated batches, and the textures whose import
        // settings Quick optimization changed in place (their .meta holds those settings).
        internal static HashSet<string> CheckpointPaths(MyAvatar avatar)
        {
            var scene = avatar.gameObject.scene;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(scene.path))
            {
                paths.Add(scene.path); paths.Add(scene.path + ".meta");
                // Include every referenced generated batch, including materials kept for Undo.
                foreach (var dependency in AssetDatabase.GetDependencies(scene.path, true).Where(p => p.StartsWith("Assets/Orbiters/MyAvatar/", StringComparison.Ordinal)))
                { paths.Add(dependency); if (File.Exists(dependency + ".meta")) paths.Add(dependency + ".meta"); }
            }
            var textures = avatar.textures.Where(t => t.texture).Select(t => AssetDatabase.GetAssetPath(t.texture))
                .Concat(avatar.optimization.textures.Select(t => AssetDatabase.GUIDToAssetPath(t.guid)));
            foreach (var texturePath in textures)
                if (!string.IsNullOrEmpty(texturePath) && texturePath.StartsWith("Assets/", StringComparison.Ordinal))
                { paths.Add(texturePath); paths.Add(texturePath + ".meta"); }
            if (!string.IsNullOrEmpty(avatar.batchFolder) && Directory.Exists(avatar.batchFolder))
                foreach (var file in Directory.EnumerateFiles(avatar.batchFolder, "*", SearchOption.AllDirectories)) paths.Add(file.Replace('\\', '/'));
            foreach (var path in paths.ToArray())
            {
                string parent = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(parent) && parent != "Assets")
                { if (File.Exists(parent + ".meta")) paths.Add((parent + ".meta").Replace('\\', '/')); parent = Path.GetDirectoryName(parent); }
            }
            return paths;
        }

#if MYAVATAR_UNITGIT
        private static async Task<string> CommitAsync(MyAvatar avatar, string root)
        {
            var result = await UnitGitReleases.CommitProjectFilesAsync(root, "texture change", CheckpointPaths(avatar).Where(File.Exists).ToArray());
            if (!result.Success) throw new InvalidOperationException("Scene and assets saved, but the Git checkpoint failed: " + result.Message);
            if (result.NoChanges) return "Saved · no new changes to checkpoint.";
            return "Saved · commit " + result.CommitHash.Substring(0, Math.Min(8, result.CommitHash.Length));
        }
#endif

        internal static void Dirty(MyAvatar avatar)
        { EditorUtility.SetDirty(avatar); PrefabUtility.RecordPrefabInstancePropertyModifications(avatar); EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene); }
        private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
