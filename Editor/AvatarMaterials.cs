using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>A material of the avatar worth changing: Unity's Standard shader, or an open surface (fur cards, feathers) whose back is hidden.</summary>
    internal sealed class MaterialAdvice
    {
        public Material Material;
        public readonly List<Renderer> Renderers = new List<Renderer>();
        public bool Standard;
        public bool HiddenBack;
        // A locked Poiyomi material bakes its culling: Poiyomi must unlock it first.
        public bool Locked;
    }

    /// <summary>
    /// The avatar's materials: those on Unity's Standard shader move to VRChat's Toon Standard, which VRChat recommends for
    /// avatars, and open surfaces seen from behind (a tail of fur cards, feathers) show both sides. Changes are made on
    /// copies in My Avatar's folder, its own materials included (a duplicated avatar shares them), assigned with one Undo step.
    /// </summary>
    internal static class AvatarMaterials
    {
        internal const string Folder = "Assets/Orbiters/MyAvatar/MaterialEdits";

        internal static List<MaterialAdvice> Inspect(GameObject root)
        {
            var advice = new Dictionary<Material, MaterialAdvice>();
            if (root == null) return new List<MaterialAdvice>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer || Helper(renderer.transform, root.transform)) continue;
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (!material) continue;
                    // Toon Standard is opaque: transparent or cutout Standard materials (eye lenses, cards with alpha) stay.
                    bool standard = MaterialSides.IsStandard(material) && (!material.HasProperty("_Mode") || material.GetFloat("_Mode") == 0);
                    if (MaterialSides.IsStandard(material) && !standard) continue;
                    // Only meshes drawn with culling get measured: the large closed body usually shows both sides already.
                    bool hidden = MaterialSides.ShowsBackFaces(material) == false && mesh && i < mesh.subMeshCount && MaterialSides.IsOpen(mesh, i);
                    if (!standard && !hidden) continue;
                    if (!advice.TryGetValue(material, out var entry)) advice[material] = entry = new MaterialAdvice { Material = material };
                    entry.Standard |= standard;
                    entry.HiddenBack |= hidden;
                    entry.Locked = !standard && !MaterialSides.CanShowBackFaces(material);
                    if (!entry.Renderers.Contains(renderer)) entry.Renderers.Add(renderer);
                }
            }
            return advice.Values.OrderBy(a => a.Material.name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Editor helpers drawn on the avatar (gizmo overlays, hidden previews) are not its materials.
        internal static bool Helper(Transform transform, Transform root)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                if (t.gameObject.hideFlags != HideFlags.None || t.CompareTag("EditorOnly")) return true;
                if (t == root) break;
            }
            return false;
        }

        /// <summary>Applies <paramref name="advice"/>: Standard to Toon Standard, and both sides where a surface needs them. Returns how many materials changed.</summary>
        internal static int Apply(MyAvatar avatar, IEnumerable<MaterialAdvice> advice)
        {
            if (!avatar) return 0;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar))
                throw new InvalidOperationException("Change materials on an avatar in an open scene, outside Play Mode.");
            var list = advice.Where(a => a.Material && (a.Standard || (a.HiddenBack && !a.Locked))).ToList();
            if (list.Count == 0) return 0;
            TextureImport.EnsureFolder(Folder);
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Fix " + avatar.name + " materials");
            var replacements = new Dictionary<Material, Material>();
            foreach (var entry in list)
            {
                var source = entry.Material;
                Material result;
                if (entry.Standard)
                {
                    result = MaterialRepair.CreateToon(source, Folder + "/SurfaceMaps");
                    if (entry.HiddenBack) MaterialSides.ShowBackFaces(result);
                    AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + FileName(source.name) + " Toon.mat"));
                }
                else
                {
                    result = new Material(source);
                    MaterialSides.ShowBackFaces(result);
                    AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + FileName(source.name) + " Both Sides.mat"));
                }
                replacements.Add(source, result);
            }
            Reassign(avatar, list.SelectMany(a => a.Renderers), replacements);
            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            return list.Count;
        }

        /// <summary>
        /// Puts the copies in <paramref name="replacements"/> on <paramref name="renderers"/>, recorded for Undo; the avatar's
        /// texture set (when there is one) follows them.
        /// </summary>
        internal static void Reassign(MyAvatar avatar, IEnumerable<Renderer> renderers, Dictionary<Material, Material> replacements)
        {
            var list = renderers.Where(r => r).Distinct().ToArray();
            Undo.RecordObjects((avatar ? new UnityEngine.Object[] { avatar } : new UnityEngine.Object[0]).Concat(list).ToArray(), "Fix materials");
            foreach (var renderer in list)
            {
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m && replacements.TryGetValue(m, out var copy) ? copy : m).ToArray();
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            if (!avatar) return;
            Follow(avatar, list, replacements);
            TextureChanges.Dirty(avatar);
        }

        // The texture set's Undo and Redo follow the copies: its snapshot of what the renderers show, and its images' slots,
        // name the copy. A slot the set left as it was keeps both its states on the copy.
        private static void Follow(MyAvatar avatar, IList<Renderer> renderers, Dictionary<Material, Material> replacements)
        {
            foreach (var snapshot in avatar.undoMaterials.Where(s => s.renderer && renderers.Contains(s.renderer) && s.before != null && s.after != null))
            {
                var shown = (avatar.canRedo ? snapshot.before : snapshot.after).ToArray();
                var other = (avatar.canRedo ? snapshot.after : snapshot.before).ToArray();
                for (int i = 0; i < shown.Length; i++)
                {
                    if (!shown[i] || !replacements.TryGetValue(shown[i], out var copy)) continue;
                    if (i < other.Length && other[i] == shown[i]) other[i] = copy;
                    shown[i] = copy;
                }
                if (avatar.canRedo) { snapshot.before = shown; snapshot.after = other; } else { snapshot.after = shown; snapshot.before = other; }
            }
            foreach (var entry in avatar.textures)
            {
                if (entry.material && replacements.TryGetValue(entry.material, out var copy)) entry.material = copy;
                if (entry.materialBeforeLast && replacements.TryGetValue(entry.materialBeforeLast, out copy)) entry.materialBeforeLast = copy;
            }
        }

        private static string FileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
