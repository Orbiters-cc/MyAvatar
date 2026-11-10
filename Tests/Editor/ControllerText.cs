using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// Everything a controller holds and plays, written out: two controllers a build step changed compare as text. Its
    /// parameters; each layer's settings, mask contents and synced overrides; each state machine, state and transition
    /// with every setting playback reads; behaviours by their serialized fields; blend trees and clips whole (keys with
    /// tangents and weights, wrap modes, clip settings). Each state machine, state and motion has an identity (its order
    /// of first appearance), so what points where and what is shared is compared, not only names.
    /// </summary>
    internal static class ControllerText
    {
        public static string Describe(AnimatorController controller)
        {
            var text = new StringBuilder();
            var ids = new Dictionary<Object, string>();
            string Id(Object value, string kind)
            {
                if (value == null) return "none";
                if (!ids.TryGetValue(value, out var id)) ids[value] = id = kind + ids.Count;
                return id;
            }
            // Identities in the order of the layers' walk, before anything refers to them.
            void Number(AnimatorStateMachine machine)
            {
                if (machine == null || ids.ContainsKey(machine)) return;
                Id(machine, "machine");
                foreach (var child in machine.states) Id(child.state, "state");
                foreach (var sub in machine.stateMachines) Number(sub.stateMachine);
            }
            var layers = controller.layers;
            foreach (var layer in layers) Number(layer.stateMachine);

            foreach (var p in controller.parameters) text.Append($"param {p.name} {p.type} {p.defaultFloat} {p.defaultInt} {p.defaultBool}\n");
            void WriteTransition(AnimatorTransitionBase t)
            {
                text.Append($"  -> {(t.destinationState != null ? Id(t.destinationState, "state") : t.destinationStateMachine != null ? Id(t.destinationStateMachine, "machine") : t.isExit ? "exit" : "none")}");
                text.Append($" {t.name} mute {t.mute} solo {t.solo} exit {t.isExit}");
                if (t is AnimatorStateTransition s)
                    text.Append($" exitTime {s.hasExitTime} {s.exitTime} fixed {s.hasFixedDuration} {s.duration} offset {s.offset} interrupt {s.interruptionSource} ordered {s.orderedInterruption} self {s.canTransitionToSelf}");
                foreach (var c in t.conditions) text.Append($" [{c.parameter} {c.mode} {c.threshold}]");
                text.Append('\n');
            }
            void WriteBehaviours(StateMachineBehaviour[] behaviours)
            {
                foreach (var behaviour in behaviours)
                    text.Append($"  behaviour {(behaviour != null ? behaviour.GetType().Name + " " + EditorJsonUtility.ToJson(behaviour) : "missing")}\n");
            }
            void WriteCurve(AnimationCurve curve)
            {
                if (curve == null) { text.Append(" none"); return; }
                text.Append($" wrap {curve.preWrapMode} {curve.postWrapMode}");
                foreach (var k in curve.keys) text.Append($" ({k.time} {k.value} {k.inTangent} {k.outTangent} {k.inWeight} {k.outWeight} {k.weightedMode})");
            }
            void WriteMotion(Motion motion, string indent)
            {
                if (motion == null) { text.Append($"{indent}none\n"); return; }
                bool seen = ids.ContainsKey(motion);
                string id = Id(motion, "motion");
                if (seen) { text.Append($"{indent}{id} again\n"); return; }
                if (motion is BlendTree tree)
                {
                    text.Append($"{indent}{id} tree {tree.name} {tree.blendType} {tree.blendParameter} {tree.blendParameterY} auto {tree.useAutomaticThresholds} {tree.minThreshold} {tree.maxThreshold}\n");
                    foreach (var child in tree.children)
                    {
                        text.Append($"{indent} child {child.threshold} {child.position} {child.timeScale} {child.cycleOffset} {child.directBlendParameter} {child.mirror}\n");
                        WriteMotion(child.motion, indent + "  ");
                    }
                }
                else if (motion is AnimationClip clip)
                {
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    text.Append($"{indent}{id} clip {clip.name} loop {settings.loopTime} {settings.loopBlend} {settings.cycleOffset} {clip.frameRate} {clip.wrapMode} {clip.legacy}\n");
                    foreach (var b in AnimationUtility.GetCurveBindings(clip).OrderBy(b => b.path + "|" + b.type.Name + "|" + b.propertyName))
                    {
                        text.Append($"{indent} {b.path} {b.type.Name} {b.propertyName}");
                        WriteCurve(AnimationUtility.GetEditorCurve(clip, b));
                        text.Append('\n');
                    }
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip).OrderBy(b => b.path + "|" + b.type.Name + "|" + b.propertyName))
                        text.Append($"{indent} {b.path} {b.type.Name} {b.propertyName} {string.Join(",", AnimationUtility.GetObjectReferenceCurve(clip, b).Select(k => k.time + ":" + (k.value != null ? k.value.name : "none")))}\n");
                }
                else text.Append($"{indent}{id} {motion.GetType().Name} {motion.name}\n");
            }
            void WriteState(AnimatorState state)
            {
                text.Append($" {Id(state, "state")} state {state.name} tag {state.tag} speed {state.speed} {state.speedParameterActive} {state.speedParameter}" +
                            $" mirror {state.mirror} {state.mirrorParameterActive} {state.mirrorParameter} cycle {state.cycleOffset} {state.cycleOffsetParameterActive} {state.cycleOffsetParameter}" +
                            $" time {state.timeParameterActive} {state.timeParameter} writeDefaults {state.writeDefaultValues} ik {state.iKOnFeet}\n");
                WriteBehaviours(state.behaviours);
                WriteMotion(state.motion, "  ");
                foreach (var t in state.transitions) WriteTransition(t);
            }
            void WriteMachine(AnimatorStateMachine machine)
            {
                text.Append($" {Id(machine, "machine")} machine {machine.name} default {Id(machine.defaultState, "state")}\n");
                WriteBehaviours(machine.behaviours);
                foreach (var t in machine.anyStateTransitions) WriteTransition(t);
                foreach (var t in machine.entryTransitions) WriteTransition(t);
                foreach (var child in machine.states) WriteState(child.state);
                foreach (var sub in machine.stateMachines)
                {
                    foreach (var t in machine.GetStateMachineTransitions(sub.stateMachine)) WriteTransition(t);
                    WriteMachine(sub.stateMachine);
                }
            }
            void WriteMask(AvatarMask mask)
            {
                if (mask == null) { text.Append(" mask none\n"); return; }
                text.Append($" mask {mask.name}");
                for (int i = 0; i < mask.transformCount; i++) text.Append($" {mask.GetTransformPath(i)}={mask.GetTransformActive(i)}");
                for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++) text.Append(mask.GetHumanoidBodyPartActive(part) ? '1' : '0');
                text.Append('\n');
            }
            foreach (var layer in layers)
            {
                text.Append($"layer {layer.name} weight {layer.defaultWeight} blend {layer.blendingMode} ik {layer.iKPass} synced {layer.syncedLayerIndex} timing {layer.syncedLayerAffectsTiming}\n");
                WriteMask(layer.avatarMask);
                if (layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < layers.Length)
                {
                    // A synced layer plays the source layer's states with its own motions and behaviours.
                    var source = layers[layer.syncedLayerIndex].stateMachine;
                    var states = new List<AnimatorState>();
                    void Collect(AnimatorStateMachine machine)
                    {
                        if (machine == null) return;
                        states.AddRange(machine.states.Select(s => s.state));
                        foreach (var sub in machine.stateMachines) Collect(sub.stateMachine);
                    }
                    Collect(source);
                    foreach (var state in states)
                    {
                        text.Append($" override {Id(state, "state")}\n");
                        WriteMotion(layer.GetOverrideMotion(state), "  ");
                        WriteBehaviours(layer.GetOverrideBehaviours(state));
                    }
                }
                else if (layer.stateMachine != null) WriteMachine(layer.stateMachine);
            }
            return text.ToString();
        }
    }
}
