using System;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>Where an avatar already uses a package's assets: an instance of one of its prefabs, or a VRCFury or Modular Avatar component referring to them.</summary>
    internal static class PackageUse
    {
        /// <summary>The root of a prefab instance below <paramref name="root"/> whose prefab is one of the package's, or null.</summary>
        /// <param name="isPackageAsset">Whether an asset path is one of the package's.</param>
        /// <param name="skip">A part of the avatar not to look in.</param>
        internal static GameObject Prefab(Transform root, Func<string, bool> isPackageAsset, Transform skip = null)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (skip != null && transform.IsChildOf(skip)) continue;
                if (PrefabUtility.GetNearestPrefabInstanceRoot(transform.gameObject) != transform.gameObject) continue;
                if (isPackageAsset(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject))) return transform.gameObject;
            }
            return null;
        }

        /// <summary>A VRCFury or Modular Avatar component below <paramref name="root"/> referring to one of the package's assets, or null.</summary>
        internal static MonoBehaviour Component(Transform root, Func<string, bool> isPackageAsset, Transform skip = null)
        {
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null || (skip != null && component.transform.IsChildOf(skip))) continue;
                string space = component.GetType().Namespace ?? "";
                if (!space.StartsWith("VF.", StringComparison.Ordinal) && !space.StartsWith("nadena.dev", StringComparison.Ordinal)) continue;
                var property = new SerializedObject(component).GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null &&
                        isPackageAsset(AssetDatabase.GetAssetPath(property.objectReferenceValue)))
                        return component;
            }
            return null;
        }
    }
}
