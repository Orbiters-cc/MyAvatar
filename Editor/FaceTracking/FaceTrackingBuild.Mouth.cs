using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using Orbiters.Toolkit.Editor.Animations;
using UnityEngine;
using VRC.Dynamics;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// The mouth a tracked face moves is VRCFaceTracking's alone: the avatar's own hand-gesture layers that pause while
    /// tracking (<see cref="Gate"/>) are the expression layers, but others move the mouth too while they move ears or
    /// feathers (Rexouium's right fist sticks the tongue out from its ear layers). While lip tracking is on, those layers
    /// leave the mouth alone and keep moving the rest: the jaw, the tongue's bones and PhysBones, and the face's tracked
    /// shapes go back to rest in them.
    /// </summary>
    internal static partial class FaceTrackingBuild
    {
        internal const string LipTracking = "LipTrackingActive";
        // The trees made here end with this.
        private const string QuietMouth = " (mouth while tracked)";

        /// <summary>The jaw and the tongue of an avatar, and the PhysBones moving them, by path from its root.</summary>
        internal sealed class MouthParts
        {
            /// <summary>The jaw and everything under it, and every tongue bone.</summary>
            public readonly HashSet<string> Bones = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Objects holding a PhysBone of those bones: any of their properties belongs to the mouth.</summary>
            public readonly HashSet<string> Physics = new HashSet<string>(StringComparer.Ordinal);

            public static MouthParts Of(Transform root)
            {
                var parts = new MouthParts();
                if (root == null) return parts;
                var animator = root.GetComponent<Animator>();
                var jaw = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Jaw) : null;
                if (jaw == null)
                {
                    var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                    jaw = (head != null ? head : root).GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.Equals("Jaw", StringComparison.OrdinalIgnoreCase));
                }
                var bones = new HashSet<Transform>();
                if (jaw != null) bones.UnionWith(jaw.GetComponentsInChildren<Transform>(true));
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name.IndexOf("tongue", StringComparison.OrdinalIgnoreCase) >= 0 && t.GetComponent<Renderer>() == null && t.GetComponent<VRCPhysBoneBase>() == null)
                        bones.UnionWith(t.GetComponentsInChildren<Transform>(true));
                foreach (var bone in bones) parts.Bones.Add(AnimationUtility.CalculateTransformPath(bone, root));
                foreach (var physBone in root.GetComponentsInChildren<VRCPhysBoneBase>(true))
                {
                    var start = physBone.rootTransform != null ? physBone.rootTransform : physBone.transform;
                    if (bones.Contains(start)) parts.Physics.Add(AnimationUtility.CalculateTransformPath(physBone.transform, root));
                }
                return parts;
            }

            /// <summary>Whether a binding moves the mouth: a tracked shape of the face, the jaw or tongue, or their PhysBones.</summary>
            public bool Moves(EditorCurveBinding binding, string facePath, HashSet<string> tracked) =>
                (binding.type == typeof(Transform) && Bones.Contains(binding.path)) || Physics.Contains(binding.path) ||
                (binding.path == facePath && binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                 tracked.Contains(binding.propertyName.Substring(11)));
        }

        // Every hand-gesture layer not resting while tracked: states moving the mouth get it only while lip tracking is off.
        private static int Quiet(AnimatorController fx, Plan plan, HashSet<string> tracked, HashSet<AnimatorStateMachine> resting)
        {
            var parameter = fx.parameters.FirstOrDefault(p => p.name == LipTracking);
            // The template's parameter is a float (a blend tree needs one); an avatar declaring it otherwise keeps its gestures.
            if (parameter == null || parameter.type != AnimatorControllerParameterType.Float) return 0;
            var parts = MouthParts.Of(plan.Root);
            bool Mouth(EditorCurveBinding b) => parts.Moves(b, plan.FacePath, tracked);
            var quiet = new Dictionary<Motion, Motion>();
            int count = 0;
            foreach (var layer in fx.layers)
            {
                var machine = layer.stateMachine;
                if (machine == null || resting.Contains(machine) || IsTracking(machine) || !Gestures(machine)) continue;
                foreach (var state in States(machine))
                {
                    if (state.motion == null || state.motion.name.EndsWith(QuietMouth, StringComparison.Ordinal)) continue;
                    if (!quiet.TryGetValue(state.motion, out var wrapped))
                    {
                        var without = Without(fx, state.motion, Mouth, new Dictionary<Motion, Motion>());
                        wrapped = without == state.motion ? state.motion
                            : Keep(fx, Tree(state.motion.name + QuietMouth, LipTracking, (0f, state.motion), (1f, without)));
                        quiet[state.motion] = wrapped;
                    }
                    if (wrapped == state.motion) continue;
                    state.motion = wrapped;
                    count++;
                }
            }
            return count;
        }

        // A layer the hands drive: its transitions or its blend trees read a gesture.
        private static bool Gestures(AnimatorStateMachine machine) =>
            Transitions(machine).Any(t => t.conditions.Any(c => GestureParameters.Contains(c.parameter))) ||
            States(machine).SelectMany(s => TreeParameters(s.motion)).Any(GestureParameters.Contains);

        // A copy of a motion without the bindings <paramref name="drop"/> picks (itself when it has none).
        private static Motion Without(AnimatorController fx, Motion motion, Func<EditorCurveBinding, bool> drop, Dictionary<Motion, Motion> done)
        {
            if (motion == null) return null;
            if (done.TryGetValue(motion, out var result)) return result;
            result = motion;
            if (motion is AnimationClip clip)
            {
                var floats = AnimationUtility.GetCurveBindings(clip).Where(drop).ToList();
                var references = AnimationUtility.GetObjectReferenceCurveBindings(clip).Where(drop).ToList();
                if (floats.Count + references.Count > 0)
                {
                    var copy = Object.Instantiate(clip);
                    copy.name = clip.name + QuietMouth;
                    foreach (var binding in floats) AnimationUtility.SetEditorCurve(copy, binding, null);
                    foreach (var binding in references) AnimationUtility.SetObjectReferenceCurve(copy, binding, null);
                    result = Keep(fx, copy);
                }
            }
            else if (motion is BlendTree tree)
            {
                var children = tree.children;
                bool changed = false;
                for (int i = 0; i < children.Length; i++)
                {
                    var child = Without(fx, children[i].motion, drop, done);
                    if (child != children[i].motion) { children[i].motion = child; changed = true; }
                }
                if (changed)
                {
                    var copy = AnimatorControllerCopy.Tree(tree);
                    copy.name = tree.name + QuietMouth;
                    copy.children = children;
                    result = Keep(fx, copy);
                }
            }
            done[motion] = result;
            return result;
        }
    }
}
