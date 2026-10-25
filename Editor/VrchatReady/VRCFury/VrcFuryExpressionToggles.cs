using System.Collections.Generic;
using System.Linq;
using com.vrcfury.api;
using Orbiters.MyAvatar.Editor.Vrm;
using UnityEditor;
using UnityEngine;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;

namespace Orbiters.MyAvatar.Editor.VrchatReady.VRCFury
{
    /// <summary>
    /// A VRM's expressions as VRCFury menu toggles (VRCFury's public API): one per expression in one folder, one at a time
    /// (an exclusive tag), not saved (a face shouldn't come back angry in the next world), each setting the expression's
    /// blend shapes at its weights. My Avatar's menu editor shows them like the avatar's other toggles.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class VrcFuryExpressionToggles : IExpressionToggles
    {
        static VrcFuryExpressionToggles() => ReadyConversion.Toggles = new VrcFuryExpressionToggles();

        public GameObject Write(GameObject root, string menuFolder, IReadOnlyList<VrmModel.Expression> expressions)
        {
            var holder = new GameObject(menuFolder);
            holder.transform.SetParent(root.transform, false);
            foreach (var expression in expressions)
            {
                var toggle = FuryComponents.CreateToggle(holder);
                toggle.SetMenuPath(menuFolder + "/" + expression.Name.Replace("/", "\\/"));
                toggle.AddExclusiveTag(menuFolder);
                var actions = toggle.GetActions();
                foreach (var (renderer, shape, weight) in expression.Shapes)
                    if (renderer != null && renderer.sharedMesh != null && renderer.sharedMesh.GetBlendShapeIndex(shape) >= 0)
                        actions.AddBlendshape(shape, Mathf.Clamp01(weight) * 100f, renderer);
            }
            // Orbiters' toggle icon, as My Avatar's other toggles have (VRCFury's API has none).
            foreach (var component in holder.GetComponents(VrcFury.Component).Where(c => c != null))
            {
                var serialized = new SerializedObject(component);
                var enable = serialized.FindProperty("content.enableIcon");
                var icon = serialized.FindProperty("content.icon.objRef");
                if (enable == null || icon == null) continue;
                enable.boolValue = true;
                icon.objectReferenceValue = Toolkit.Editor.VRChat.OrbitersMenuIcons.Toggle;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            // Registered once complete: Undo removes the object and its toggles, Redo brings them back.
            Undo.RegisterCreatedObjectUndo(holder, ReadyConversion.UndoName);
            return holder;
        }
    }
}
