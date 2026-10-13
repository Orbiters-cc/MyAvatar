using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// Which avatars' menus a batch of Unity's object changes can change: objects under an avatar (or moved into or out of
    /// it), and the assets menus are made of (menus, parameters, controllers and what they hold). Anything else, like a
    /// material dragged in the Inspector or another avatar's object, leaves their menus as they are: nothing is rebuilt then.
    /// </summary>
    internal sealed class MenuChanges
    {
        /// <summary>A change that may touch any avatar: a scene, a menu asset, an object destroyed with what held it.</summary>
        public bool All;
        private readonly List<Transform> objects = new List<Transform>();

        public static MenuChanges Read(ref ObjectChangeEventStream stream)
        {
            var changes = new MenuChanges();
            for (int i = 0; i < stream.length && !changes.All; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties); changes.Object(properties.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure); changes.Object(structure.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var hierarchy); changes.Object(hierarchy.instanceId); break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created); changes.Object(created.instanceId); break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var order); changes.Object(order.instanceId); break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        changes.Object(parent.instanceId);
                        changes.Object(parent.previousParentInstanceId, optional: true);
                        changes.Object(parent.newParentInstanceId, optional: true);
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        // Gone: what held it tells whose it was (nothing held a destroyed avatar).
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed); changes.Object(destroyed.parentInstanceId); break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var prefabs);
                        foreach (int id in prefabs.instanceIds) changes.Object(id);
                        break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var asset); changes.Asset(asset.instanceId); break;
                    case ObjectChangeKind.CreateAssetObject:
                        stream.GetCreateAssetObjectEvent(i, out var made); changes.Asset(made.instanceId); break;
                    case ObjectChangeKind.DestroyAssetObject:
                        stream.GetDestroyAssetObjectEvent(i, out var deleted); changes.Asset(deleted.instanceId); break;
                    default:
                        changes.All = true; break;
                }
            }
            return changes;
        }

        /// <summary>
        /// The files <paramref name="root"/>'s menu is made of as they are on disk now (its menus and parameters, its Full
        /// Controllers' and Modular Avatar installers', the prefab it is an instance of): a file imported, moved or deleted
        /// that changes it changes this.
        /// </summary>
        public static string Assets(GameObject root)
        {
            if (root == null) return "";
            var assets = new HashSet<Object>();
            void Tree(VRCExpressionsMenu menu) { foreach (var m in MenuPaths.Menus(menu)) assets.Add(m); }
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor != null) { Tree(descriptor.expressionsMenu); assets.Add(descriptor.expressionParameters); }
            foreach (var (_, feature, kind) in Orbiters.Toolkit.Editor.VRChat.VrcFury.Features(root))
            {
                if (kind != "FullController") continue;
                foreach (var list in new[] { "menus", "prms" })
                    if (Orbiters.Toolkit.Editor.VRChat.VrcFury.Field(feature, list) is System.Collections.IEnumerable entries)
                        foreach (var entry in entries)
                        {
                            var asset = Orbiters.Toolkit.Editor.VRChat.VrcFury.ObjectReference(Orbiters.Toolkit.Editor.VRChat.VrcFury.Field(entry, list == "menus" ? "menu" : "parameters"));
                            if (asset is VRCExpressionsMenu menu) Tree(menu); else if (asset != null) assets.Add(asset);
                        }
            }
            foreach (var installer in MaMenus.Read(root)?.Installers ?? new List<MaInstaller>()) Tree(installer.Menu);
            var paths = assets.Where(a => a != null).Select(AssetDatabase.GetAssetPath).Where(p => p.Length > 0).ToList();
            if (PrefabUtility.IsPartOfPrefabInstance(root)) paths.Add(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root));
            return string.Join("|", paths.Distinct().OrderBy(p => p, System.StringComparer.Ordinal).Select(p => p + ":" + AssetDatabase.GetAssetDependencyHash(p)));
        }

        /// <summary>Whether the changes can change <paramref name="root"/>'s menu.</summary>
        public bool Touches(GameObject root) =>
            root != null && (All || objects.Any(t => t != null && t.IsChildOf(root.transform)));

        private void Object(int id, bool optional = false)
        {
            if (id == 0) { if (!optional) All = true; return; }
            var target = EditorUtility.InstanceIDToObject(id);
            var transform = target is GameObject go ? go.transform : target is Component component ? component.transform : null;
            if (transform != null) objects.Add(transform);
            // An object that is neither (the menu editor's own pending edits): what it is decides, as for assets.
            else if (target != null) Asset(id);
            else if (!optional) All = true;
        }

        // Only the kinds of assets a menu is made of; a destroyed one may have been one.
        private void Asset(int id)
        {
            var target = EditorUtility.InstanceIDToObject(id);
            if (target == null || target is VRCExpressionsMenu || target is VRCExpressionParameters || target is AnimatorController || target is AnimatorStateMachine ||
                target is AnimatorState || target is AnimatorTransitionBase || target is BlendTree || target is AnimationClip || target is StateMachineBehaviour ||
                target is GameObject || target is MonoBehaviour)
                All = true;
        }
    }
}
