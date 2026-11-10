using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// Everything a controller holds and plays, written out (its parameters, layers, states, transitions, layer controls and
    /// motions): two controllers a build step changed compare as text.
    /// </summary>
    internal static class ControllerText
    {
        public static string Describe(AnimatorController controller)
        {
            var text = new StringBuilder();
            foreach (var p in controller.parameters) text.Append($"param {p.name} {p.type} {p.defaultFloat} {p.defaultInt} {p.defaultBool}\n");
            void WriteTransition(AnimatorTransitionBase t)
            {
                text.Append($"  -> {(t.destinationState != null ? t.destinationState.name : t.destinationStateMachine != null ? "machine " + t.destinationStateMachine.name : t.isExit ? "exit" : "none")}");
                if (t is AnimatorStateTransition s) text.Append($" exit {s.hasExitTime} {s.duration} self {s.canTransitionToSelf}");
                foreach (var c in t.conditions) text.Append($" [{c.parameter} {c.mode} {c.threshold}]");
                text.Append('\n');
            }
            void WriteBehaviours(StateMachineBehaviour[] behaviours)
            {
                foreach (var behaviour in behaviours)
                    text.Append(behaviour is VRCAnimatorLayerControl control ? $"  control {control.playable} {control.layer} {control.goalWeight}\n" : $"  behaviour {behaviour.GetType().Name}\n");
            }
            void WriteMotion(Motion motion, string indent)
            {
                if (motion is BlendTree tree)
                {
                    text.Append($"{indent}tree {tree.name} {tree.blendType} {tree.blendParameter}\n");
                    foreach (var child in tree.children)
                    {
                        text.Append($"{indent} child {child.threshold} {child.directBlendParameter}\n");
                        WriteMotion(child.motion, indent + "  ");
                    }
                }
                else if (motion is AnimationClip clip)
                {
                    text.Append($"{indent}clip {clip.name}\n");
                    foreach (var b in AnimationUtility.GetCurveBindings(clip).OrderBy(b => b.path + "|" + b.propertyName))
                        text.Append($"{indent} {b.path} {b.type.Name} {b.propertyName} {string.Join(",", AnimationUtility.GetEditorCurve(clip, b).keys.Select(k => k.time + ":" + k.value))}\n");
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip).OrderBy(b => b.path + "|" + b.propertyName))
                        text.Append($"{indent} {b.path} {b.type.Name} {b.propertyName} {string.Join(",", AnimationUtility.GetObjectReferenceCurve(clip, b).Select(k => k.time + ":" + (k.value != null ? k.value.name : "none")))}\n");
                }
                else text.Append($"{indent}{(motion == null ? "none" : motion.name)}\n");
            }
            void WriteMachine(AnimatorStateMachine machine)
            {
                text.Append($" machine {machine.name} default {(machine.defaultState != null ? machine.defaultState.name : "none")}\n");
                WriteBehaviours(machine.behaviours);
                foreach (var t in machine.anyStateTransitions) WriteTransition(t);
                foreach (var t in machine.entryTransitions) WriteTransition(t);
                foreach (var child in machine.states)
                {
                    text.Append($" state {child.state.name}\n");
                    WriteBehaviours(child.state.behaviours);
                    WriteMotion(child.state.motion, "  ");
                    foreach (var t in child.state.transitions) WriteTransition(t);
                }
                foreach (var sub in machine.stateMachines)
                {
                    foreach (var t in machine.GetStateMachineTransitions(sub.stateMachine)) WriteTransition(t);
                    WriteMachine(sub.stateMachine);
                }
            }
            foreach (var layer in controller.layers)
            {
                text.Append($"layer {layer.name} weight {layer.defaultWeight} synced {layer.syncedLayerIndex} mask {(layer.avatarMask != null ? layer.avatarMask.name : "none")}\n");
                if (layer.stateMachine != null) WriteMachine(layer.stateMachine);
            }
            return text.ToString();
        }
    }
}
