using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>Where an avatar already uses a package's assets: an instance of one of its prefabs, or a VRCFury or Modular Avatar component referring to them.</summary>
    internal static class PackageUse
    {
        // What each VRCFury or Modular Avatar component refers to, read again only once it changed (its dirty count) or the
        // project did: sections ask on every hierarchy change.
        private static readonly Dictionary<int, (int dirty, UnityEngine.Object[] references)> References = new Dictionary<int, (int, UnityEngine.Object[])>();

        [InitializeOnLoadMethod]
        private static void ForgetOnProjectChange() => EditorApplication.projectChanged += References.Clear;

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
                if (ReferencesOf(component).Any(reference => reference != null && isPackageAsset(AssetDatabase.GetAssetPath(reference)))) return component;
            }
            return null;
        }

        // Every object a component's serialized fields point at (strings and arrays of plain values are skipped).
        private static UnityEngine.Object[] ReferencesOf(MonoBehaviour component)
        {
            int id = component.GetInstanceID(), dirty = EditorUtility.GetDirtyCount(component);
            if (References.TryGetValue(id, out var known) && known.dirty == dirty) return known.references;
            var found = new HashSet<UnityEngine.Object>();
            using (var serialized = new SerializedObject(component))
            {
                var property = serialized.GetIterator();
                bool enter = true;
                while (property.Next(enter))
                {
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null) found.Add(property.objectReferenceValue);
                    enter = property.propertyType == SerializedPropertyType.ManagedReference ||
                            property.propertyType == SerializedPropertyType.Generic && !(property.isArray && Plain(property.arrayElementType));
                }
            }
            var references = found.ToArray();
            References[id] = (dirty, references);
            return references;
        }

        private static bool Plain(string type) =>
            type == "int" || type == "float" || type == "bool" || type == "char" || type == "double" || type == "long" || type == "UInt8" || type == "SInt8" ||
            type == "short" || type == "unsigned int" || type == "string" || type == "Vector2" || type == "Vector3" || type == "Vector4" || type == "Quaternion" || type == "ColorRGBA";
    }
}
