using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Novabeast's own options, found by what the avatar has rather than by its name: parts its FX controller hides with a
    /// "Hide…" toggle (chest fluff, collar, goggles, mane), its breasts (a slider playing their clip, 0 for none) and its
    /// tail size (a slider weighing its clip). Each sets what the avatar starts with in VRChat (the parameter's default) and
    /// shows it in the scene (<see cref="LookPreviews"/>); a part hidden here stays hidden: its menu toggle is left out of
    /// the upload. Each change is one Undo step.
    /// </summary>
    internal static class NovabeastOptions
    {
        private static readonly Regex HideParameter = new Regex(@"^Hide(?<part>.+)$", RegexOptions.CultureInvariant);

        internal sealed class Part
        {
            public string Label, Parameter;
            /// <summary>What hiding it does (played at full weight).</summary>
            public AnimationClip Clip;
        }

        internal sealed class Amount
        {
            public string Label, Parameter;
            public AnimationClip Clip;
            /// <summary>The parameter plays the clip (its time, from 0.01 on) instead of weighing it.</summary>
            public bool Timed;
        }

        internal sealed class Options
        {
            public VRCAvatarDescriptor Descriptor;
            public readonly List<Part> Parts = new List<Part>();
            public Amount Breasts, Tail;
            public bool Any => Parts.Count > 0 || Breasts != null || Tail != null;
        }

        internal static Options Find(GameObject root)
        {
            var options = new Options();
            var descriptor = root ? root.GetComponent<VRCAvatarDescriptor>() : null;
            if (descriptor == null) return options;
            options.Descriptor = descriptor;
            var fx = descriptor.baseAnimationLayers?.FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController;
            var parameters = descriptor.expressionParameters?.parameters;
            if (fx == null || parameters == null) return options;
            // What each parameter drives: a direct blend tree's child it weighs, or a state it plays.
            var weighed = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            var timed = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            foreach (var layer in fx.layers)
                foreach (var state in layer.stateMachine.states.Select(s => s.state))
                {
                    if (state.timeParameterActive && state.motion is AnimationClip played && !string.IsNullOrEmpty(state.timeParameter)) timed[state.timeParameter] = played;
                    foreach (var child in Children(state.motion))
                        if (child.motion is AnimationClip clip && !string.IsNullOrEmpty(child.directBlendParameter) && !weighed.ContainsKey(child.directBlendParameter))
                            weighed[child.directBlendParameter] = clip;
                }
            foreach (var parameter in parameters.Where(p => p != null && !string.IsNullOrEmpty(p.name)))
            {
                var match = HideParameter.Match(parameter.name);
                // A part hidden by its clip: a "hide…" blendshape raised, or something switched off.
                if (match.Success && weighed.TryGetValue(parameter.name, out var hide) && Hides(hide))
                    options.Parts.Add(new Part { Parameter = parameter.name, Clip = hide, Label = Capitalized(ObjectNames.NicifyVariableName(match.Groups["part"].Value)) });
            }
            if (Float(parameters, "Breasts") && timed.TryGetValue("Breasts", out var breasts)) options.Breasts = new Amount { Label = "Breasts", Parameter = "Breasts", Clip = breasts, Timed = true };
            if (Float(parameters, "TailSize") && weighed.TryGetValue("TailSize", out var tail)) options.Tail = new Amount { Label = "Tail size", Parameter = "TailSize", Clip = tail };
            return options;
        }

        private static bool Float(VRCExpressionParameters.Parameter[] parameters, string name) =>
            parameters.Any(p => p != null && p.name == name && p.valueType == VRCExpressionParameters.ValueType.Float);

        private static bool Hides(AnimationClip clip) => AnimationUtility.GetCurveBindings(clip).Any(b =>
        {
            float value = AnimationUtility.GetEditorCurve(clip, b).Evaluate(0);
            return (b.propertyName.StartsWith("blendShape.hide", StringComparison.OrdinalIgnoreCase) && value > 50f) ||
                   ((b.propertyName == "m_Enabled" || b.propertyName == "m_IsActive") && value < .5f);
        });

        private static string Capitalized(string label) => label.Length == 0 ? label : char.ToUpperInvariant(label[0]) + label.Substring(1).ToLowerInvariant();

        private static IEnumerable<ChildMotion> Children(Motion motion)
        {
            if (!(motion is BlendTree tree)) yield break;
            foreach (var child in tree.children)
            {
                yield return child;
                foreach (var nested in Children(child.motion)) yield return nested;
            }
        }

        private static VRCExpressionParameters.Parameter Parameter(Options options, string name) =>
            options.Descriptor.expressionParameters.parameters.First(p => p.name == name);

        internal static bool Shown(Options options, Part part) => Parameter(options, part.Parameter).defaultValue < .5f;

        /// <summary>Shows or hides a part: its toggle's default, and the scene to match.</summary>
        internal static void SetShown(Options options, Part part, bool shown) =>
            Change(options, (shown ? "Show " : "Hide ") + part.Label.ToLowerInvariant(), part.Parameter, shown ? 0f : 1f, part.Clip, shown ? 0f : 1f, false);

        internal static float Value(Options options, Amount amount) => Parameter(options, amount.Parameter).defaultValue;

        /// <summary>Where a slider starts in VRChat (0 to 1), and the scene to match.</summary>
        internal static void SetValue(Options options, Amount amount, float value) =>
            Change(options, amount.Label, amount.Parameter, Mathf.Clamp01(value), amount.Clip, Mathf.Clamp01(value), amount.Timed);

        private static void Change(Options options, string undo, string name, float value, AnimationClip clip, float weight, bool timed)
        {
            var root = options.Descriptor.gameObject;
            var parameters = options.Descriptor.expressionParameters;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.RecordObject(parameters, undo);
            Parameter(options, name).defaultValue = value;
            EditorUtility.SetDirty(parameters);
            LookPreviews.Show(root, clip, weight, timed, undo);
            EditorSceneManager.MarkSceneDirty(root.scene);
            Undo.SetCurrentGroupName(undo);
            Undo.CollapseUndoOperations(group);
        }
    }

    /// <summary>
    /// Looks chosen in My Avatar shown in the scene without changing what the avatar's animations start from: the first time
    /// a property is shown otherwise, its value is kept on the My Avatar component; the upload copy gets those values back
    /// (the look then comes from the menu's starting value, as in VRChat), and showing the rest again forgets them.
    /// </summary>
    internal static class LookPreviews
    {
        /// <summary>
        /// Shows <paramref name="clip"/> on <paramref name="root"/> at <paramref name="weight"/> (a direct blend tree's weight), or
        /// played at that time when <paramref name="timed"/> (none at 0.01 or below).
        /// </summary>
        internal static void Show(GameObject root, AnimationClip clip, float weight, bool timed, string undo)
        {
            var settings = root.GetComponentInChildren<MyAvatar>(true);
            if (settings != null) Undo.RecordObject(settings, undo);
            bool resting = timed ? weight <= .01f : weight <= 0f;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var target = binding.path.Length == 0 ? root.transform : root.transform.Find(binding.path);
                if (target == null) continue;
                var property = Property.Of(target, binding);
                if (property == null) continue;
                var record = settings?.lookRest.FirstOrDefault(r => r.path == binding.path && r.type == binding.type.FullName && r.property == binding.propertyName);
                float rest = record != null ? record.value : property.Get();
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                float value = resting ? rest : timed ? curve.Evaluate(weight * clip.length) : Mathf.Lerp(rest, curve.Evaluate(0), weight);
                Undo.RecordObject(property.Target, undo);
                property.Set(value);
                PrefabUtility.RecordPrefabInstancePropertyModifications(property.Target);
                if (settings == null) continue;
                if (resting) { if (record != null) settings.lookRest.Remove(record); }
                else if (record == null) settings.lookRest.Add(new LookRest { path = binding.path, type = binding.type.FullName, property = binding.propertyName, value = rest });
            }
            if (settings != null) { EditorUtility.SetDirty(settings); PrefabUtility.RecordPrefabInstancePropertyModifications(settings); }
        }

        /// <summary>A property an animation curve binds, read and written like the animation would.</summary>
        internal sealed class Property
        {
            public UnityEngine.Object Target;
            public Func<float> Get;
            public Action<float> Set;

            public static Property Of(Transform target, EditorCurveBinding binding)
            {
                string p = binding.propertyName;
                if (binding.type == typeof(GameObject) && p == "m_IsActive")
                    return new Property { Target = target.gameObject, Get = () => target.gameObject.activeSelf ? 1f : 0f, Set = v => target.gameObject.SetActive(v > .5f) };
                if (binding.type == typeof(Transform))
                {
                    int axis = p.EndsWith(".x") ? 0 : p.EndsWith(".y") ? 1 : p.EndsWith(".z") ? 2 : -1;
                    if (axis < 0) return null;
                    if (p.StartsWith("m_LocalPosition.")) return new Property { Target = target, Get = () => target.localPosition[axis], Set = v => { var x = target.localPosition; x[axis] = v; target.localPosition = x; } };
                    if (p.StartsWith("m_LocalScale.")) return new Property { Target = target, Get = () => target.localScale[axis], Set = v => { var x = target.localScale; x[axis] = v; target.localScale = x; } };
                    if (p.StartsWith("localEulerAnglesRaw.")) return new Property { Target = target, Get = () => Signed(target.localEulerAngles[axis]), Set = v => { var x = target.localEulerAngles; x[axis] = v; target.localEulerAngles = x; } };
                    return null;
                }
                var component = target.GetComponent(binding.type);
                if (component is SkinnedMeshRenderer skinned && p.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    int index = skinned.sharedMesh != null ? skinned.sharedMesh.GetBlendShapeIndex(p.Substring(11)) : -1;
                    return index < 0 ? null : new Property { Target = skinned, Get = () => skinned.GetBlendShapeWeight(index), Set = v => skinned.SetBlendShapeWeight(index, v) };
                }
                if (component is Behaviour behaviour && p == "m_Enabled")
                    return new Property { Target = behaviour, Get = () => behaviour.enabled ? 1f : 0f, Set = v => behaviour.enabled = v > .5f };
                return null;
            }

            private static float Signed(float angle) => angle > 180f ? angle - 360f : angle;
        }

        // Before VRCFury and everything else reads the avatar: the upload starts from the authored values.
        internal sealed class Restore : IVRCSDKPreprocessAvatarCallback
        {
            public int callbackOrder => -10060;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                var settings = avatar.GetComponentInChildren<MyAvatar>(true);
                if (settings == null || settings.lookRest.Count == 0) return true;
                foreach (var record in settings.lookRest)
                {
                    var target = record.path.Length == 0 ? avatar.transform : avatar.transform.Find(record.path);
                    var type = target != null ? TypeOf(record.type) : null;
                    if (type == null) continue;
                    Property.Of(target, EditorCurveBinding.FloatCurve(record.path, type, record.property))?.Set(record.value);
                }
                return true;
            }

            private static Type TypeOf(string name) =>
                AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null);
        }
    }
}
