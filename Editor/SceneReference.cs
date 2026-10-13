using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// A scene object named by an MCP tool's caller: an instance ID, or a path from a scene root ("Root/Child"), optionally
    /// after its scene's name or path ("Scene:Root/Child"). Read the same whatever the editor's language; a number that is
    /// no object's ID is tried as a name, and a colon only names a scene that is open. A path several objects answer to is
    /// refused, naming them, rather than one picked.
    /// </summary>
    internal static class SceneReference
    {
        /// <summary>The object, or null when none answers. Throws when several do.</summary>
        /// <param name="scenes">Where paths are looked for: the open scenes unless given.</param>
        internal static GameObject Resolve(string reference, IEnumerable<Scene> scenes = null)
        {
            if (string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("Provide target (instance ID or hierarchy path).");
            reference = reference.Trim();
            if (int.TryParse(reference, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int id))
            {
                var found = EditorUtility.InstanceIDToObject(id);
                var gameObject = found as GameObject ?? (found as Component)?.gameObject;
                if (gameObject && gameObject.scene.IsValid()) return gameObject;
            }
            var open = (scenes ?? Loaded()).ToList();
            var matches = new List<GameObject>();
            int colon = reference.IndexOf(':');
            if (colon > 0)
            {
                string name = reference.Substring(0, colon);
                var named = open.Where(s => s.name == name || s.path == name).ToList();
                if (named.Count > 0) matches.AddRange(named.SelectMany(s => Find(s, reference.Substring(colon + 1))));
            }
            // No open scene by that name: the colon belongs to the object's name.
            if (matches.Count == 0) matches.AddRange(open.SelectMany(s => Find(s, reference)));
            matches = matches.Distinct().ToList();
            if (matches.Count > 1)
                throw new ArgumentException(matches.Count + " objects answer to “" + reference + "”: give an instance ID instead (" +
                                            string.Join(", ", matches.Take(5).Select(m => m.GetInstanceID().ToString(CultureInfo.InvariantCulture) + " in " + m.scene.name)) + ").");
            return matches.FirstOrDefault();
        }

        private static IEnumerable<Scene> Loaded()
        {
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (scene.isLoaded) yield return scene;
            }
        }

        // Every object of the scene at that path (siblings may share a name).
        private static IEnumerable<GameObject> Find(Scene scene, string path)
        {
            var parts = path.Split('/');
            IEnumerable<Transform> level = scene.GetRootGameObjects().Where(r => r.name == parts[0]).Select(r => r.transform);
            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i];
                level = level.SelectMany(t => t.Cast<Transform>().Where(c => c.name == part)).ToList();
            }
            return level.Select(t => t.gameObject);
        }
    }
}
