using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal static class AccessoryMaterials
    {
        internal static int Repair(MyAvatar avatar, OrbitersAttachment item, string folder = null)
        {
            if (!avatar || !item || !item.transform.IsChildOf(avatar.transform)) return 0;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar))
                throw new InvalidOperationException("Repair materials on an avatar in an open scene, outside Play Mode.");
            var renderers = item.GetComponentsInChildren<Renderer>(true);
            var sources = renderers.SelectMany(r => r.sharedMaterials).Where(m => m).Distinct()
                .Select(m => (material: m, authored: MaterialRepair.AuthoredReplacement(m)))
                .Where(p => p.authored || MaterialRepair.Reason(p.material) != null || MaterialSurfaceMaps.NeedsRoughDefault(p.material)).ToArray();
            if (sources.Length == 0) return 0;
            folder = folder ?? "Assets/Orbiters/MyAvatar/MaterialEdits";
            TextureImport.EnsureFolder(folder);
            var replacements = new Dictionary<Material, Material>();
            foreach (var pair in sources)
            {
                var material = pair.material;
                var source = pair.authored ? pair.authored : material;
                var copy = MaterialRepair.Reason(source) != null ? MaterialRepair.CreateToon(source, folder + "/SurfaceMaps") : new Material(source);
                if (MaterialSurfaceMaps.NeedsRoughDefault(copy)) MaterialSurfaceMaps.MakeEditable(copy);
                MaterialSurfaceMaps.DefaultRough(copy);
                string name = string.Concat(material.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + " Repaired.mat"));
                replacements.Add(material, copy);
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Repair " + item.name + " materials");
            Undo.RecordObjects(renderers.Cast<UnityEngine.Object>().ToArray(), "Repair clothing materials");
            foreach (var renderer in renderers)
            {
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => m && replacements.TryGetValue(m, out var copy) ? copy : m).ToArray();
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            return replacements.Count;
        }
    }
}
