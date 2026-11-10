using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>What changed on an avatar, as <see cref="AvatarChanges"/> tells its watchers.</summary>
    [Flags]
    internal enum AvatarFacets
    {
        None = 0,
        /// <summary>Objects under the avatar created, destroyed, moved, renamed, shown or hidden; components added or removed.</summary>
        Hierarchy = 1,
        /// <summary>A renderer's settings under the avatar edited (with Undo).</summary>
        Renderers = 2,
        /// <summary>Another component's settings under the avatar edited (with Undo).</summary>
        Components = 4,
        /// <summary>A material asset edited (any avatar may show it).</summary>
        Materials = 8,
        /// <summary>An asset imported, moved or deleted that may change what an avatar shows.</summary>
        Imports = 16,
        /// <summary>An Undo or Redo (anything may have gone back).</summary>
        Undo = 32,
    }

    /// <summary>
    /// The one change detector of My Avatar's Inspector sections: it listens to the editor once for every section and every
    /// avatar, works out which avatars a change touches and how, and tells each watcher once things settle (one notification
    /// for a burst of changes). A change elsewhere in the scene (renaming a light, another avatar's edits) reaches no
    /// section of this avatar: <see cref="EditorApplication.hierarchyChanged"/>, which says nothing about where, is checked
    /// against a numeric signature of the avatar's objects, components, renderers and their materials.
    ///
    /// Kept behind <see cref="Watch"/> on purpose: Toolkit's avatar snapshot and change bus can back it later without the
    /// sections changing.
    /// </summary>
    internal static class AvatarChanges
    {
        private const double Delay = .3;

        private sealed class Watcher
        {
            public Watched watched;
            public AvatarFacets facets;
            public Action<AvatarFacets> changed;
        }

        private sealed class Watched
        {
            public MyAvatar avatar;
            public int id;
            public readonly List<Watcher> watchers = new List<Watcher>();
            public AvatarFacets pending;
            // The hierarchy changed somewhere: compare the signature at the flush.
            public bool check;
            public long signature;
        }

        private static readonly Dictionary<int, Watched> Avatars = new Dictionary<int, Watched>();
        private static double due = -1;
        private static bool hooked, updating;

        /// <summary>
        /// Calls <paramref name="changed"/> with the facets that changed among <paramref name="facets"/>, a moment after the
        /// last change of a burst, until disposed.
        /// </summary>
        internal static IDisposable Watch(MyAvatar avatar, AvatarFacets facets, Action<AvatarFacets> changed)
        {
            if (!avatar || changed == null) return new Subscription(null);
            Hook();
            int id = avatar.GetInstanceID();
            if (!Avatars.TryGetValue(id, out var watched)) Avatars[id] = watched = new Watched { avatar = avatar, id = id, signature = Signature(avatar) };
            var watcher = new Watcher { watched = watched, facets = facets, changed = changed };
            watched.watchers.Add(watcher);
            return new Subscription(watcher);
        }

        private sealed class Subscription : IDisposable
        {
            private Watcher watcher;
            public Subscription(Watcher watcher) { this.watcher = watcher; }

            public void Dispose()
            {
                if (watcher == null) return;
                var watched = watcher.watched;
                watched.watchers.Remove(watcher);
                watcher = null;
                if (watched.watchers.Count == 0) Avatars.Remove(watched.id);
                if (Avatars.Count == 0) Unhook();
            }
        }

        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            EditorApplication.hierarchyChanged += HierarchyChanged;
            ObjectChangeEvents.changesPublished += Published;
            Undo.undoRedoPerformed += Undone;
        }

        private static void Unhook()
        {
            if (!hooked) return;
            hooked = false;
            EditorApplication.hierarchyChanged -= HierarchyChanged;
            ObjectChangeEvents.changesPublished -= Published;
            Undo.undoRedoPerformed -= Undone;
            if (updating) { EditorApplication.update -= Flush; updating = false; }
            due = -1;
        }

        // Each change moves the flush: watchers hear once a burst settles, as the sections' own timers did.
        private static void Arm()
        {
            due = EditorApplication.timeSinceStartup + Delay;
            if (updating) return;
            updating = true;
            EditorApplication.update += Flush;
        }

        internal static void HierarchyChanged()
        {
            foreach (var watched in Avatars.Values) watched.check = true;
            Arm();
        }

        private static void Undone()
        {
            foreach (var watched in Avatars.Values) watched.pending |= AvatarFacets.Undo;
            Arm();
        }

        private static void All(AvatarFacets facet)
        {
            foreach (var watched in Avatars.Values) watched.pending |= facet;
        }

        // The avatars holding this object get the facet; an object that cannot be found any more may have been under any.
        private static void Mark(int instanceId, AvatarFacets facet)
        {
            var found = EditorUtility.InstanceIDToObject(instanceId);
            var transform = found is GameObject go ? go.transform : found is Component component ? component.transform : null;
            if (!transform) { All(facet); return; }
            foreach (var watched in Avatars.Values)
                if (watched.avatar && transform.IsChildOf(TextureOptimization.AvatarRoot(watched.avatar).transform)) watched.pending |= facet;
        }

        private static void Published(ref ObjectChangeEventStream stream)
        {
            if (Avatars.Count == 0) return;
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created); Mark(created.instanceId, AvatarFacets.Hierarchy); break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed);
                        if (destroyed.parentInstanceId != 0) Mark(destroyed.parentInstanceId, AvatarFacets.Hierarchy);
                        else All(AvatarFacets.Hierarchy);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var structureHierarchy); Mark(structureHierarchy.instanceId, AvatarFacets.Hierarchy); break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure); Mark(structure.instanceId, AvatarFacets.Hierarchy); break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        Mark(parent.instanceId, AvatarFacets.Hierarchy);
                        if (parent.previousParentInstanceId != 0) Mark(parent.previousParentInstanceId, AvatarFacets.Hierarchy);
                        break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                        var edited = EditorUtility.InstanceIDToObject(properties.instanceId);
                        Mark(properties.instanceId, edited is GameObject ? AvatarFacets.Hierarchy : edited is Renderer ? AvatarFacets.Renderers : AvatarFacets.Components);
                        break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var asset);
                        if (EditorUtility.InstanceIDToObject(asset.instanceId) is Material) All(AvatarFacets.Materials);
                        break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var prefabs);
                        foreach (int id in prefabs.instanceIds) Mark(id, AvatarFacets.Hierarchy);
                        break;
                    case ObjectChangeKind.ChangeScene:
                        All(AvatarFacets.Hierarchy); break;
                }
            }
            Arm();
        }

        /// <summary>Imports that may change what an avatar shows (see <see cref="TextureOptimization.ImportMatters"/>).</summary>
        internal sealed class ImportWatcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (Avatars.Count == 0) return;
                if (!Array.Exists(imported, TextureOptimization.ImportMatters) && !Array.Exists(deleted, TextureOptimization.ImportMatters) &&
                    !Array.Exists(moved, TextureOptimization.ImportMatters)) return;
                All(AvatarFacets.Imports);
                Arm();
            }
        }

        private static readonly List<Watcher> Calling = new List<Watcher>();
        private static readonly List<Watched> Flushing = new List<Watched>();

        private static void Flush() => Flush(false);

        /// <summary>Tells the watchers now (tests), or once the burst settled.</summary>
        internal static void Flush(bool now)
        {
            if (!now && EditorApplication.timeSinceStartup < due) return;
            EditorApplication.update -= Flush; updating = false; due = -1;
            Flushing.Clear(); Flushing.AddRange(Avatars.Values);
            foreach (var watched in Flushing)
            {
                if (!watched.avatar) continue;
                if (watched.check)
                {
                    watched.check = false;
                    long signature = Signature(watched.avatar);
                    if (signature != watched.signature) { watched.signature = signature; watched.pending |= AvatarFacets.Hierarchy; }
                }
                var pending = watched.pending;
                watched.pending = AvatarFacets.None;
                if (pending == AvatarFacets.None) continue;
                Calling.Clear(); Calling.AddRange(watched.watchers);
                foreach (var watcher in Calling)
                {
                    var facets = pending & watcher.facets;
                    if (facets == AvatarFacets.None || !watched.watchers.Contains(watcher)) continue;
                    try { watcher.changed(facets); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            Flushing.Clear(); Calling.Clear();
        }

        private static readonly List<Transform> Transforms = new List<Transform>();
        private static readonly List<Renderer> Renderers = new List<Renderer>();
        private static readonly List<Material> Materials = new List<Material>();

        /// <summary>
        /// A number that changes with the avatar's objects (their names, whether shown, children and components) and its
        /// renderers (shown, meshes, materials). Reads values only.
        /// </summary>
        internal static long Signature(MyAvatar avatar)
        {
            var root = TextureOptimization.AvatarRoot(avatar).transform;
            ulong hash = 14695981039346656037UL;
            void Add(long value) { hash = (hash ^ (ulong)value) * 1099511628211UL; hash ^= hash >> 31; }
            Transforms.Clear();
            root.GetComponentsInChildren(true, Transforms);
            Add(Transforms.Count);
            foreach (var t in Transforms)
            {
                var go = t.gameObject;
                Add(t.GetInstanceID()); Add(go.name.GetHashCode());
                Add((go.activeSelf ? 1 : 0) | t.childCount << 1 | go.GetComponentCount() << 16);
            }
            Renderers.Clear();
            root.GetComponentsInChildren(true, Renderers);
            foreach (var renderer in Renderers)
            {
                Add(renderer.GetInstanceID()); Add(renderer.enabled ? 1 : 0);
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                Add(mesh ? mesh.GetInstanceID() : 0);
                Materials.Clear();
                renderer.GetSharedMaterials(Materials);
                Add(Materials.Count);
                foreach (var material in Materials) Add(material ? material.GetInstanceID() : 0);
            }
            Transforms.Clear(); Renderers.Clear(); Materials.Clear();
            return (long)hash;
        }
    }
}
