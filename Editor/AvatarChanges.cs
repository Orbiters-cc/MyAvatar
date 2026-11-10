using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

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
    /// The one change detector of My Avatar's Inspector sections. It reads Toolkit's <see cref="EditorChangeBus"/> (Unity's
    /// change streams decoded once for every Orbiters tool), works out which avatars a change touches and how, and tells
    /// each watcher once things settle (one notification for a burst of changes). A change elsewhere in the scene (renaming
    /// a light, another avatar's edits) reaches no section of this avatar and does not hold its notifications back.
    /// <para>
    /// Changes the editor reports no object for: <see cref="EditorChanges.Hierarchy"/> (Unity's hierarchyChanged, which says
    /// nothing about where; a script renaming or hiding an object without Undo raises only that) is checked against a
    /// numeric signature of the avatar's objects, components, renderers and their materials. When the signature moved, the
    /// avatar's <see cref="AvatarSnapshot"/> is invalidated before the watchers hear: the snapshot only follows changes the
    /// editor reports, and the sections read their facts from it.
    /// </para>
    /// </summary>
    internal static class AvatarChanges
    {
        private const double Delay = .3;
        // However long a burst goes on (Play mode, a tool editing every frame), its watchers hear by then.
        internal const double MaxLatency = 1.5;

        /// <summary>The time in seconds (tests set their own).</summary>
        internal static Func<double> Clock = () => EditorApplication.timeSinceStartup;

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
            // When the pending change of this avatar is told (-1: none), and when its burst started.
            public double due = -1, first = -1;
            // The avatar's root while one bus value is read (its descriptor's object, or the avatar's own).
            public Transform root;
        }

        private static readonly Dictionary<int, Watched> Avatars = new Dictionary<int, Watched>();
        private static bool hooked;
        private static IDisposable ticking;

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
            EditorChangeBus.Changed += Changed;
        }

        private static void Unhook()
        {
            if (!hooked) return;
            hooked = false;
            EditorChangeBus.Changed -= Changed;
            StopTicking();
        }

        private static void StopTicking()
        {
            ticking?.Dispose();
            ticking = null;
        }

        // A change of this avatar moves its own flush: its watchers hear once its burst settles, as the sections' own timers
        // did, and at most MaxLatency after the burst started. Changes elsewhere move nothing.
        private static void Arm(Watched watched)
        {
            double now = Clock();
            if (watched.first < 0) watched.first = now;
            watched.due = now + Delay;
            // The host's frame pump also runs while Unity compiles or is in the background (no repaint needed).
            ticking ??= OrbitersEditorHost.EveryFrame("MyAvatar.AvatarChanges", Tick);
        }

        private static void Tick() => Flush(false);

        // ---- What the bus reports, per avatar ----

        /// <summary>One value of the change bus: each avatar gets the facets of the changes at or below its root.</summary>
        internal static void Changed(EditorChanges changes)
        {
            if (Avatars.Count == 0 || changes == null) return;
            foreach (var watched in Avatars.Values) watched.root = watched.avatar ? TextureOptimization.AvatarRoot(watched.avatar).transform : null;
            try
            {
                if (changes.UndoRedo) Undone();
                if (changes.Everything) All(AvatarFacets.Hierarchy);
                var destroyed = changes.Destroyed as ICollection<int> ?? new HashSet<int>(changes.Destroyed);
                // A destroyed hierarchy's parent is reported as a changed subtree; the destroyed root itself can't be found.
                foreach (int id in changes.Structure) if (!destroyed.Contains(id)) Mark(changes.Find(id), AvatarFacets.Hierarchy);
                foreach (int id in changes.Subtrees) if (!destroyed.Contains(id)) Mark(changes.Find(id), AvatarFacets.Hierarchy);
                foreach (int id in changes.Properties)
                {
                    if (destroyed.Contains(id)) continue;
                    var edited = changes.Find(id);
                    Mark(edited, edited is GameObject ? AvatarFacets.Hierarchy : edited is Renderer ? AvatarFacets.Renderers : AvatarFacets.Components);
                }
                foreach (string guid in changes.Assets)
                    if (IsMaterial(guid)) { All(AvatarFacets.Materials); break; }
                if (Matter(changes.Imported) || Matter(changes.Deleted) || Matter(changes.Moved)) All(AvatarFacets.Imports);
                if (changes.Hierarchy) HierarchyChanged();
            }
            finally { foreach (var watched in Avatars.Values) watched.root = null; }
        }

        // A material asset edited in the editor (the bus names assets by GUID). Materials are main assets in their .mat.
        private static bool IsMaterial(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return !string.IsNullOrEmpty(path) && AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(Material);
        }

        private static bool Matter(IReadOnlyList<string> paths)
        {
            foreach (string path in paths) if (TextureOptimization.ImportMatters(path)) return true;
            return false;
        }

        /// <summary>
        /// Unity's hierarchyChanged: every avatar compares its signature at its next flush. One already waiting keeps its
        /// time (a hierarchy change elsewhere holds no avatar back); one at rest is told after the usual delay.
        /// </summary>
        internal static void HierarchyChanged()
        {
            foreach (var watched in Avatars.Values)
            {
                watched.check = true;
                if (watched.due < 0) Arm(watched);
            }
        }

        private static void Undone()
        {
            foreach (var watched in Avatars.Values) { watched.pending |= AvatarFacets.Undo; Arm(watched); }
        }

        private static void All(AvatarFacets facet)
        {
            foreach (var watched in Avatars.Values) { watched.pending |= facet; Arm(watched); }
        }

        /// <summary>
        /// An object changed: the avatars holding it get the facet; an object that cannot be found any more may have been
        /// under any of them.
        /// </summary>
        internal static void Touched(int instanceId, AvatarFacets facet) => Mark(EditorUtility.InstanceIDToObject(instanceId), facet);

        private static void Mark(Object found, AvatarFacets facet)
        {
            var transform = found is GameObject go ? go.transform : found is Component component ? component.transform : null;
            if (!transform) { All(facet); return; }
            foreach (var watched in Avatars.Values)
            {
                if (!watched.avatar) continue;
                var root = watched.root ? watched.root : TextureOptimization.AvatarRoot(watched.avatar).transform;
                if (transform.IsChildOf(root)) { watched.pending |= facet; Arm(watched); }
            }
        }

        private static readonly List<Watcher> Calling = new List<Watcher>();
        private static readonly List<Watched> Flushing = new List<Watched>();

        /// <summary>
        /// Tells the watchers of each avatar whose burst settled or went on for MaxLatency; of every avatar with a pending
        /// change with <paramref name="now"/>.
        /// </summary>
        internal static void Flush(bool now)
        {
            double time = Clock();
            bool waiting = false;
            Flushing.Clear();
            foreach (var watched in Avatars.Values)
            {
                if (watched.due < 0) continue;
                if (now || time >= watched.due || time - watched.first >= MaxLatency) Flushing.Add(watched);
                else waiting = true;
            }
            if (!waiting) StopTicking();
            foreach (var watched in Flushing)
            {
                watched.due = watched.first = -1;
                if (!watched.avatar) continue;
                if (watched.check)
                {
                    watched.check = false;
                    long signature = Signature(watched.avatar);
                    if (signature != watched.signature)
                    {
                        watched.signature = signature;
                        watched.pending |= AvatarFacets.Hierarchy;
                        // A change the bus may not have reported (made without Undo): the snapshot's facts go too.
                        AvatarSnapshot.Of(TextureOptimization.AvatarRoot(watched.avatar))?.Invalidate();
                    }
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
        private static readonly List<Component> Components = new List<Component>();

        /// <summary>
        /// A number that changes with the avatar's objects (their names, whether shown, children, and which components they
        /// hold: one replaced by another counts, whatever their number) and its
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
                Add((go.activeSelf ? 1 : 0) | t.childCount << 1);
                Components.Clear();
                go.GetComponents(Components);
                Add(Components.Count);
                foreach (var component in Components) Add(component ? component.GetInstanceID() : 0);
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
            Transforms.Clear(); Renderers.Clear(); Materials.Clear(); Components.Clear();
            return (long)hash;
        }
    }
}
