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
    /// copies in My Avatar's folder (its own copies are changed in place), assigned with one Undo step.
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
        private static bool Helper(Transform transform, Transform root)
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
                bool own = AssetDatabase.GetAssetPath(source).StartsWith("Assets/Orbiters/MyAvatar/", StringComparison.Ordinal);
                Material result;
                if (entry.Standard)
                {
                    result = MaterialRepair.CreateToon(source, Folder + "/SurfaceMaps");
                    if (entry.HiddenBack) MaterialSides.ShowBackFaces(result);
                    AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + FileName(source.name) + " Toon.mat"));
                    replacements.Add(source, result);
                }
                else if (own)
                {
                    Undo.RecordObject(source, "Show both sides");
                    MaterialSides.ShowBackFaces(source);
                    EditorUtility.SetDirty(source);
                }
                else
                {
                    result = new Material(source);
                    MaterialSides.ShowBackFaces(result);
                    AssetDatabase.CreateAsset(result, AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + FileName(source.name) + " Both Sides.mat"));
                    replacements.Add(source, result);
                }
            }
            var renderers = list.SelectMany(a => a.Renderers).Where(r => r).Distinct().ToArray();
            Undo.RecordObjects(renderers.Cast<UnityEngine.Object>().ToArray(), "Fix materials");
            foreach (var renderer in renderers)
            {
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m && replacements.TryGetValue(m, out var copy) ? copy : m).ToArray();
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            return list.Count;
        }

        private static string FileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
