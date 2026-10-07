using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Rexouium's own options, found by what the avatar has rather than by its name: feather groups its FX controller hides
    /// with a saved "Fthrs…Off" menu toggle, and ear size from its EarsSmall and EarsBig blendshapes. A feather group's
    /// switch sets that toggle's default (what the avatar starts with in VRChat) and shows it in the scene; the ear size is
    /// the blendshapes' values, which nothing animates. Each change is one Undo step.
    /// </summary>
    internal static class RexouiumOptions
    {
        private static readonly Regex FeatherParameter = new Regex(@"^Fthrs?(?<part>.+?)Off$", RegexOptions.CultureInvariant);

        internal sealed class FeatherGroup
        {
            public string Label, Parameter;
            // What the toggle's "off" state hides: objects and blendshapes on the avatar, relative paths.
            public readonly List<string> Objects = new List<string>();
            public readonly List<(string path, string shape)> Shapes = new List<(string, string)>();
        }

        internal sealed class Options
        {
            public VRCAvatarDescriptor Descriptor;
            public readonly List<FeatherGroup> Feathers = new List<FeatherGroup>();
            public SkinnedMeshRenderer Ears;
            public bool Any => Feathers.Count > 0 || Ears != null;
        }

        internal static Options Find(GameObject root)
        {
            var options = new Options();
            var descriptor = root ? root.GetComponent<VRCAvatarDescriptor>() : null;
            if (descriptor == null) return options;
            options.Descriptor = descriptor;
            var fx = descriptor.baseAnimationLayers?.FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController;
            var parameters = descriptor.expressionParameters?.parameters;
            if (fx != null && parameters != null)
                foreach (var parameter in parameters.Where(p => p != null && p.valueType == VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType.Bool))
                {
                    var match = FeatherParameter.Match(parameter.name ?? "");
                    if (!match.Success) continue;
                    var group = new FeatherGroup { Parameter = parameter.name, Label = ObjectNames.NicifyVariableName(match.Groups["part"].Value).Trim() };
                    // The state the toggle switches to when it is on: the clip that hides this group.
                    foreach (var layer in fx.layers)
                        foreach (var state in layer.stateMachine.states.Select(s => s.state))
                            foreach (var transition in layer.stateMachine.states.SelectMany(s => s.state.transitions).Where(t => t.destinationState == state))
                                if (transition.conditions.Any(c => c.parameter == parameter.name && c.mode == AnimatorConditionMode.If) && state.motion is AnimationClip clip)
                                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                                    {
                                        float value = AnimationUtility.GetEditorCurve(clip, binding).Evaluate(0);
                                        if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive" && value < .5f && !group.Objects.Contains(binding.path)) group.Objects.Add(binding.path);
                                        if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) && value > 50f)
                                            group.Shapes.Add((binding.path, binding.propertyName.Substring(11)));
                                    }
                    if (group.Objects.Count > 0) options.Feathers.Add(group);
                }
            options.Ears = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(r => r.sharedMesh != null && r.sharedMesh.GetBlendShapeIndex("EarsSmall") >= 0 && r.sharedMesh.GetBlendShapeIndex("EarsBig") >= 0);
            return options;
        }

        internal static bool Shown(Options options, FeatherGroup group) =>
            options.Descriptor.expressionParameters.parameters.First(p => p.name == group.Parameter).defaultValue < .5f;

        /// <summary>Shows or hides a feather group: its toggle's default, and the scene to match.</summary>
        internal static void SetShown(Options options, FeatherGroup group, bool shown)
        {
            var root = options.Descriptor.transform;
            var parameters = options.Descriptor.expressionParameters;
            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup();
            Undo.RecordObject(parameters, (shown ? "Show " : "Hide ") + group.Label + " feathers");
            parameters.parameters.First(p => p.name == group.Parameter).defaultValue = shown ? 0 : 1;
            EditorUtility.SetDirty(parameters);
            foreach (string path in group.Objects)
            {
                var target = root.Find(path);
                if (target == null) continue;
                Undo.RecordObject(target.gameObject, "Show feathers");
                target.gameObject.SetActive(shown);
            }
            foreach (var (path, shape) in group.Shapes)
            {
                var renderer = root.Find(path)?.GetComponent<SkinnedMeshRenderer>();
                int index = renderer != null && renderer.sharedMesh != null ? renderer.sharedMesh.GetBlendShapeIndex(shape) : -1;
                if (index < 0) continue;
                Undo.RecordObject(renderer, "Show feathers");
                renderer.SetBlendShapeWeight(index, shown ? 0 : 100);
            }
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            Undo.CollapseUndoOperations(undo);
        }

        /// <summary>Ear size from -100 (EarsSmall at 100) to 100 (EarsBig at 100).</summary>
        internal static float EarSize(Options options)
        {
            var mesh = options.Ears.sharedMesh;
            return options.Ears.GetBlendShapeWeight(mesh.GetBlendShapeIndex("EarsBig")) - options.Ears.GetBlendShapeWeight(mesh.GetBlendShapeIndex("EarsSmall"));
        }

        internal static void SetEarSize(Options options, float size)
        {
            var renderer = options.Ears;
            var mesh = renderer.sharedMesh;
            Undo.RecordObject(renderer, "Ear size");
            renderer.SetBlendShapeWeight(mesh.GetBlendShapeIndex("EarsBig"), Mathf.Max(0f, size));
            renderer.SetBlendShapeWeight(mesh.GetBlendShapeIndex("EarsSmall"), Mathf.Max(0f, -size));
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
        }
    }
}
