using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.Vrm;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.VrchatReady
{
    /// <summary>
    /// VRM's spring bones as VRChat PhysBones: which PhysBones (their root, what they leave out, their tip) and how they
    /// feel. Roots of one spring that hang from the same bone become one PhysBone on that bone, which stays still (VRChat
    /// counts PhysBone components toward the performance rank: a VRoid head's twenty hair strands are one PhysBone, not
    /// twenty); a lone root swings itself, as VRM turns it. Humanoid bones never move (VRChat's tracking does), and a bone
    /// two springs reach moves with the first one.
    /// </summary>
    internal static class SpringMapping
    {
        /// <summary>VRChat's limit of transforms one PhysBone moves.</summary>
        internal const int MaxTransforms = 256;
        // UniVRM 0.x gives a bone without children a tip 7 cm on along it.
        private const float VrmTip = .07f;

        internal sealed class Group
        {
            public string Name;
            /// <summary>The PhysBone's root transform.</summary>
            public Transform Root;
            /// <summary>The root is a bone the strands hang from: it stays still (MultiChildType.Ignore); else it swings.</summary>
            public bool StillRoot;
            public readonly List<Transform> Ignored = new List<Transform>();
            /// <summary>The tip past each last bone, in its space (VRM 0.x), or zero.</summary>
            public Vector3 Endpoint;
            public float Pull, Spring, Stiffness, Gravity, Radius;
            public readonly List<VrmModel.Collider> Colliders = new List<VrmModel.Collider>();
            /// <summary>How many transforms it moves.</summary>
            public int Transforms;
        }

        /// <summary>
        /// A VRM spring's feel as a PhysBone's (version 1.1): the inverse of <see cref="VrmModel.Behaviour"/>, which turns a
        /// PhysBone into a VRM spring for Export to VRM, so a round trip gives the same values. VRM's stiffness pulls the
        /// bone back to its rest direction each frame: it is shared equally by Pull and Stiffness (VRM's usual 1 gives
        /// 0.17 each; export added pull·4 + stiffness·2). VRM's drag removes momentum each frame: Spring (the momentum a
        /// PhysBone keeps) is what drag leaves (drag 0.4, VRoid's usual, gives 0.67; export used 1 − spring·0.9).
        /// Gravity is VRM's gravity power along its direction (down: positive), as export carries it; a sideways
        /// direction has no PhysBone equivalent and only its downward part stays.
        /// </summary>
        internal static (float Pull, float Spring, float Stiffness, float Gravity) Behaviour(float stiffness, float drag, float gravityPower, Vector3 gravityDirection)
        {
            float shared = Mathf.Clamp01(Mathf.Max(0f, stiffness) / 6f);
            float spring = Mathf.Clamp01((1f - Mathf.Clamp01(drag)) / .9f);
            var direction = gravityDirection.sqrMagnitude > 1e-8f ? gravityDirection.normalized : Vector3.down;
            float gravity = Mathf.Clamp(gravityPower * -direction.y, -1f, 1f);
            return (shared, spring, shared, gravity);
        }

        // What one root of a spring moves, and what it leaves out under it.
        private sealed class Part
        {
            public Transform Root;
            public readonly HashSet<Transform> Moved = new HashSet<Transform>();
            public readonly List<Transform> Ignored = new List<Transform>();
            public VrmSource.Spring Spring;
        }

        /// <summary>The PhysBones for the source's springs, on an avatar whose humanoid bones are <paramref name="humanoid"/>.</summary>
        internal static List<Group> Groups(VrmSource source, ICollection<Transform> humanoid)
        {
            var groups = new List<Group>();
            if (source == null || source.Springs.Count == 0) return groups;
            humanoid = humanoid ?? new HashSet<Transform>();
            // Every spring's parts first: a bone strands hang from can be their still root only when nothing moves it.
            var taken = new HashSet<Transform>();
            var parts = new List<Part>();
            foreach (var spring in source.Springs)
            {
                if (spring.Chain != null)
                {
                    var part = Chain(spring, humanoid, taken);
                    if (part != null) { parts.Add(part); taken.UnionWith(part.Moved); }
                    continue;
                }
                // Outer roots first: a root under another one's bones moves with it already.
                foreach (var start in spring.Roots.Where(r => r != null).Distinct().OrderBy(Depth))
                    foreach (var root in Roots(start, humanoid))
                    {
                        if (taken.Contains(root) || root.GetComponentsInParent<Transform>(true).Any(taken.Contains)) continue;
                        var part = new Part { Root = root, Spring = spring };
                        Subtree(root, part, humanoid, taken);
                        parts.Add(part);
                        taken.UnionWith(part.Moved);
                    }
            }

            // Strands hanging from the same bone, which nothing else moves, share a PhysBone there when they feel the same
            // and hit the same colliders (VRM 1.0 makes a spring per strand: a VRoid head's are one PhysBone again).
            foreach (var same in parts.GroupBy(p => (Parent: p.Root.parent != null && !taken.Contains(p.Root.parent) ? p.Root.parent : null, Key: Signature(p.Spring))))
            {
                var list = same.ToList();
                var parent = same.Key.Parent;
                if (parent == null || list.Count < 2)
                {
                    foreach (var part in list) groups.Add(Make(part.Spring, part.Root, false, new[] { part }));
                    continue;
                }
                // VRChat moves at most 256 transforms per PhysBone: a bigger set is split.
                var chunk = new List<Part>();
                int count = 0;
                foreach (var part in list)
                {
                    if (chunk.Count > 0 && count + part.Moved.Count > MaxTransforms)
                    {
                        groups.Add(Shared(parent, chunk));
                        chunk = new List<Part>(); count = 0;
                    }
                    chunk.Add(part); count += part.Moved.Count;
                }
                groups.Add(Shared(parent, chunk));
            }
            return groups;
        }

        private static Group Shared(Transform parent, List<Part> parts)
        {
            var spring = parts[0].Spring;
            if (parts.Count == 1) return Make(spring, parts[0].Root, false, parts.ToArray());
            var group = Make(spring, parent, true, parts.ToArray());
            // The bone's other children stay out (eyes, other springs' strands, the next humanoid bone).
            var roots = new HashSet<Transform>(parts.Select(p => p.Root));
            group.Ignored.InsertRange(0, parent.Cast<Transform>().Where(c => !roots.Contains(c)));
            return group;
        }

        // What makes two springs one PhysBone: the PhysBone they become, to two decimals (VRoid writes strands of one look a
        // thousandth apart), their reach to the millimetre, their colliders and their kind (a VRM 0.x subtree gets a tip, a
        // VRM 1.0 chain has its own).
        private static string Signature(VrmSource.Spring spring)
        {
            var (pull, momentum, stiffness, gravity) = Behaviour(spring.Stiffness, spring.Drag, spring.GravityPower, spring.GravityDirection);
            return string.Join("|", new[] { Mathf.Round(pull * 100f), Mathf.Round(momentum * 100f), Mathf.Round(stiffness * 100f), Mathf.Round(gravity * 100f), Mathf.Round(spring.Radius * 1000f) }
                       .Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) +
                   "|" + (spring.Chain == null ? "tree" : "chain") + "|" +
                   string.Join(",", spring.Colliders.Where(c => c != null).Distinct().Select(c => c.GetHashCode()).OrderBy(h => h));
        }

        private static Group Make(VrmSource.Spring spring, Transform root, bool still, Part[] parts)
        {
            var (pull, momentum, stiffness, gravity) = Behaviour(spring.Stiffness, spring.Drag, spring.GravityPower, spring.GravityDirection);
            var group = new Group
            {
                Name = Name(spring, root, parts),
                Root = root, StillRoot = still,
                Pull = pull, Spring = momentum, Stiffness = stiffness, Gravity = gravity,
                // VRM's radius is in metres; a PhysBone's scales with its root.
                Radius = spring.Radius / Mathf.Max(1e-4f, Mathf.Abs(root.lossyScale.x)),
                Transforms = parts.Sum(p => p.Moved.Count),
            };
            foreach (var part in parts) group.Ignored.AddRange(part.Ignored);
            group.Colliders.AddRange(spring.Colliders.Where(c => c != null && c.Transform != null).Distinct());
            if (spring.Chain == null) group.Endpoint = Tip(parts);
            return group;
        }

        private static string Name(VrmSource.Spring spring, Transform root, Part[] parts)
        {
            string name = string.IsNullOrWhiteSpace(spring.Name) ? null : spring.Name.Trim();
            string bones = parts.Length == 1 ? parts[0].Root.name : root.name + " (" + parts.Length + ")";
            return name == null || name == bones ? bones : name + " · " + bones;
        }

        // VRM 1.0: the chain from its root to its tip; what hangs off a joint outside the chain stays out.
        private static Part Chain(VrmSource.Spring spring, ICollection<Transform> humanoid, HashSet<Transform> taken)
        {
            var chain = spring.Chain.Where(t => t != null).TakeWhile(t => !humanoid.Contains(t) && !taken.Contains(t)).ToList();
            if (chain.Count == 0) return null;
            var part = new Part { Root = chain[0], Spring = spring };
            part.Moved.UnionWith(chain);
            for (int i = 0; i < chain.Count; i++)
            {
                var next = i + 1 < chain.Count ? chain[i + 1] : null;
                foreach (Transform child in chain[i])
                    if (child != next) part.Ignored.Add(child);
            }
            return part;
        }

        // VRM 0.x: everything under the root moves, but humanoid bones and bones another spring moves stay out.
        private static void Subtree(Transform t, Part part, ICollection<Transform> humanoid, HashSet<Transform> taken)
        {
            part.Moved.Add(t);
            foreach (Transform child in t)
            {
                if (humanoid.Contains(child) || taken.Contains(child)) { part.Ignored.Add(child); continue; }
                Subtree(child, part, humanoid, taken);
            }
        }

        // A root holding humanoid bones (a spring on the hips for a skirt) gives way to its children that hold none, as
        // Export to VRM does the other way (Vrm0Writer.Roots).
        private static IEnumerable<Transform> Roots(Transform bone, ICollection<Transform> humanoid)
        {
            if (!humanoid.Contains(bone) && !humanoid.Any(h => h != null && h != bone && h.IsChildOf(bone))) { yield return bone; yield break; }
            foreach (Transform child in bone)
                foreach (var root in Roots(child, humanoid)) yield return root;
        }

        // UniVRM 0.x's tip for a bone without children: 7 cm on along the bone from its parent (scaled as the bone is),
        // in the bone's space. A PhysBone has one endpoint for all its last bones: their average.
        private static Vector3 Tip(Part[] parts)
        {
            var sum = Vector3.zero;
            int count = 0;
            foreach (var part in parts)
                foreach (var leaf in part.Moved)
                {
                    if (leaf.parent == null || leaf.Cast<Transform>().Any(part.Moved.Contains)) continue;
                    var along = leaf.position - leaf.parent.position;
                    if (along.sqrMagnitude < 1e-10f) continue;
                    float scale = Mathf.Abs(leaf.lossyScale.x);
                    sum += leaf.InverseTransformPoint(leaf.position + along.normalized * VrmTip * scale);
                    count++;
                }
            return count == 0 ? Vector3.zero : sum / count;
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (; t != null; t = t.parent) depth++;
            return depth;
        }
    }
}
