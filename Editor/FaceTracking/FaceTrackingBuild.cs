using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// Finishes the face tracking template My Avatar set up, on the build copy only, once VRCFury has merged it into the FX
    /// controller (VRCFury's own copy):
    /// <list type="bullet">
    /// <item>blendshapes the template animates under its standard's names animate the face's own shapes for them
    /// (<see cref="FaceTrackingNames"/>), so an avatar named its own way tracks without renaming anything;</item>
    /// <item>the avatar's hand-gesture expressions hold the face still while VRCFaceTracking drives it
    /// (FacialExpressionsDisabled, set by the template), and blinks it animates wait while eye tracking is on;</item>
    /// <item>the marker's quick settings: the smoothing, the mouth's tuning, and the features others see (the parameters
    /// of the others are left out of VRCFury's copy of the expression parameters and menu).</item>
    /// </list>
    /// The template's files and the avatar's own controllers are never changed.
    /// </summary>
    internal static partial class FaceTrackingBuild
    {
        internal const string ExpressionsDisabled = "FacialExpressionsDisabled", EyeTracking = "EyeTrackingActive";
        private static readonly string[] GestureParameters = { "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight" };

        internal const string LocalSmoothingParameter = "Smoothing/Local";

        private sealed class Plan
        {
            public FaceTrackingStandard Standard; public string FacePath; public Mesh Face;
            public FaceTrackingSmoothing Smoothing; public bool Expressive;
            /// <summary>The template's parameters of the features others don't see.</summary>
            public HashSet<string> Removed;
        }

        /// <summary>The template's Local Smoothing to start with: 0 snaps, 1 smooths most (0.25 is the template's own).</summary>
        internal static float StartingSmoothing(FaceTrackingSmoothing smoothing) =>
            smoothing == FaceTrackingSmoothing.Responsive ? 0f : smoothing == FaceTrackingSmoothing.Smooth ? .6f : .25f;

        /// <summary>How much slower than the eyes the mouth follows (1: as fast).</summary>
        internal static float MouthSlowdown(FaceTrackingSmoothing smoothing) => smoothing == FaceTrackingSmoothing.Responsive ? 1f : SlowSmoothing;
        private static readonly ConditionalWeakTable<GameObject, Plan> Plans = new ConditionalWeakTable<GameObject, Plan>();

        // Before VRCFury (-10000): its components, and the marker's face reference, are still there.
        internal sealed class Capture : IVRCSDKPreprocessAvatarCallback
        {
            public int callbackOrder => -10050;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                var marker = avatar.GetComponentInChildren<MyAvatarFaceTracking>(true);
                Plans.Remove(avatar);
                if (marker == null) return true;
                var face = FaceTrackingSetup.CurrentFace(marker, avatar.transform);
                if (face == null)
                {
                    Debug.LogWarning("[My Avatar] Face tracking: the template's face mesh is missing and no face was found on " + avatar.name + ": face tracking will not move the face.");
                    return true;
                }
                if (face != marker.face)
                {
                    Debug.LogWarning("[My Avatar] Face tracking: the template's face mesh is missing; it animates “" + face.name + "”, the face found on " + avatar.name + ".");
                    marker.face = face;
                }
                // The template animates the face where it is now, also when it moved or was renamed since the setup (this copy only).
                string facePath = AnimationUtility.CalculateTransformPath(face.transform, avatar.transform);
                if (!FaceTrackingSetup.PointBody(marker.gameObject, facePath, undo: false))
                    Debug.LogWarning("[My Avatar] Face tracking: this template version has no path rewrite to point it at “" + facePath + "”.");
                var standard = FaceTrackingStandard.Find(marker.standard);
                if (standard != null)
                    Plans.Add(avatar, new Plan
                    {
                        Standard = standard, FacePath = facePath, Face = face.sharedMesh,
                        Smoothing = marker.smoothing, Expressive = marker.expressiveMouth,
                        Removed = FaceTrackingFeatureSet.Removed(FaceTrackingFeatureSet.Parameters(marker.gameObject).Select(p => p.name), marker.synced),
                    });
                return true;
            }
        }

        // After VRCFury merged the template into its copy of the FX controller, and before MCB's corrective blendshape links
        // (-9000): they wrap the clips that move the face's own shapes, so the template's must be renamed to them first.
        internal sealed class Apply : IVRCSDKPreprocessAvatarCallback
        {
            public int callbackOrder => -9010;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                if (!Plans.TryGetValue(avatar, out var plan)) return true;
                Plans.Remove(avatar);
                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                var fx = descriptor != null ? descriptor.baseAnimationLayers.FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController : null;
                if (fx == null) return true;
                if (!Editable(fx))
                {
                    Debug.LogWarning("[My Avatar] Face tracking: the FX controller “" + fx.name + "” is the avatar's own asset (VRCFury did not build this avatar), so its blendshape names and gestures were left as they are.");
                    return true;
                }
                var (renamed, gated) = Finish(fx, plan.Standard, plan.FacePath, plan.Face);
                var tuned = Tune(fx, plan.FacePath, plan.Face, plan.Expressive, MouthSlowdown(plan.Smoothing));
                string settings = Configure(descriptor, StartingSmoothing(plan.Smoothing), plan.Removed);
                Debug.Log($"[My Avatar] Face tracking ({plan.Standard.Label}): {renamed} template blendshape curves animate this face's own shapes, {gated} expression layers wait while tracking. {tuned} {settings}");
                return true;
            }
        }

        /// <summary>Renames the template's blendshape curves and gates gestures and blinks in <paramref name="fx"/> (VRCFury's copy).</summary>
        internal static (int renamed, int gated) Finish(AnimatorController fx, FaceTrackingStandard standard, string facePath, Mesh face)
        {
            var plan = new Plan { Standard = standard, FacePath = facePath, Face = face };
            return (Rename(fx, plan), Gate(fx, plan));
        }

        /// <summary>
        /// The quick settings on VRCFury's copies of the expression parameters and menu: the Local Smoothing to start with,
        /// and the features others don't see left out (their parameters, and the menu toggles that drive them).
        /// </summary>
        internal static string Configure(VRCAvatarDescriptor descriptor, float localSmoothing, ICollection<string> removed)
        {
            var parameters = descriptor != null ? descriptor.expressionParameters : null;
            if (parameters == null || parameters.parameters == null) return "";
            if (!Owned(parameters, null)) return "The expression parameters are the avatar's own asset: the smoothing and features were left as they are.";
            foreach (var parameter in parameters.parameters)
                if (parameter != null && parameter.name == LocalSmoothingParameter) parameter.defaultValue = localSmoothing;
            int left = 0, bits = 0, controls = 0;
            if (removed != null && removed.Count > 0)
            {
                var kept = new List<VRCExpressionParameters.Parameter>();
                foreach (var parameter in parameters.parameters)
                {
                    if (parameter == null || !removed.Contains(parameter.name)) { kept.Add(parameter); continue; }
                    left++;
                    if (parameter.networkSynced) bits += VRCExpressionParameters.TypeCost(parameter.valueType);
                }
                parameters.parameters = kept.ToArray();
                controls = RemoveControls(descriptor.expressionsMenu, removed, new HashSet<VRCExpressionsMenu>());
            }
            EditorUtility.SetDirty(parameters);
            return left == 0 ? $"Local smoothing starts at {localSmoothing:0.##}."
                : $"Local smoothing starts at {localSmoothing:0.##}; {left} parameters of features others don't see were left out ({bits} bits, {controls} menu controls).";
        }

        // VRCFury's menu copy: controls driving a removed parameter go (the avatar's own menus are never edited).
        private static int RemoveControls(VRCExpressionsMenu menu, ICollection<string> removed, HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || menu.controls == null || !visited.Add(menu)) return 0;
            int count = 0;
            if (Owned(menu, null))
            {
                count = menu.controls.RemoveAll(c => c != null && c.type != VRCExpressionsMenu.Control.ControlType.SubMenu && c.parameter != null && removed.Contains(c.parameter.name));
                if (count > 0) EditorUtility.SetDirty(menu);
            }
            foreach (var control in menu.controls)
                if (control != null && control.type == VRCExpressionsMenu.Control.ControlType.SubMenu) count += RemoveControls(control.subMenu, removed, visited);
            return count;
        }

        /// <summary>VRCFury's working copies and objects in memory: anything else (the avatar's own assets) is never edited.</summary>
        internal static bool Editable(Object asset) => Owned(asset, null);

        // VRCFury's working copies, and objects made during this build, can be edited; anything else is copied first.
        private static bool Owned(Object asset, AnimatorController fx)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) || path.StartsWith("Packages/com.vrcfury.temp/", StringComparison.Ordinal) || (fx != null && path == AssetDatabase.GetAssetPath(fx));
        }

        private static T Own<T>(T asset, AnimatorController fx) where T : Object
        {
            if (Owned(asset, fx)) return asset;
            var copy = Object.Instantiate(asset);
            copy.name = asset.name;
            if (AssetDatabase.Contains(fx)) AssetDatabase.AddObjectToAsset(copy, fx);
            return copy;
        }

        // ---- Blendshape names ----

        private static int Rename(AnimatorController fx, Plan plan)
        {
            var mesh = new HashSet<string>(FaceTrackingDetection.Shapes(plan.Face), StringComparer.Ordinal);
            var index = FaceTrackingNames.Index(mesh);
            var done = new Dictionary<Motion, Motion>();
            int renamed = 0;
            Motion Visit(Motion motion)
            {
                if (motion == null) return null;
                if (done.TryGetValue(motion, out var result)) return result;
                result = motion;
                if (motion is AnimationClip clip)
                {
                    var moves = AnimationUtility.GetCurveBindings(clip)
                        .Where(b => b.path == plan.FacePath && b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        .Select(b => (binding: b, shape: FaceTrackingNames.Resolve(b.propertyName.Substring(11), index)))
                        .Where(m => !mesh.Contains(m.binding.propertyName.Substring(11)) && m.shape != null).ToList();
                    if (moves.Count > 0)
                    {
                        var owned = Own(clip, fx);
                        var existing = new HashSet<string>(AnimationUtility.GetCurveBindings(owned).Select(b => b.path + "|" + b.propertyName));
                        foreach (var (binding, shape) in moves)
                        {
                            var curve = AnimationUtility.GetEditorCurve(owned, binding);
                            AnimationUtility.SetEditorCurve(owned, binding, null);
                            var target = EditorCurveBinding.FloatCurve(binding.path, typeof(SkinnedMeshRenderer), "blendShape." + shape);
                            // The face's own animation of that shape wins.
                            if (existing.Add(target.path + "|" + target.propertyName)) AnimationUtility.SetEditorCurve(owned, target, curve);
                            renamed++;
                        }
                        result = owned;
                    }
                }
                else if (motion is BlendTree tree)
                {
                    var children = tree.children;
                    bool changed = false;
                    for (int i = 0; i < children.Length; i++)
                    {
                        var child = Visit(children[i].motion);
                        if (child != children[i].motion) { children[i].motion = child; changed = true; }
                    }
                    if (changed) { var owned = Own(tree, fx); owned.children = children; result = owned; }
                }
                done[motion] = result;
                return result;
            }
            foreach (var layer in fx.layers)
                foreach (var state in States(layer.stateMachine))
                {
                    var motion = Visit(state.motion);
                    if (motion != state.motion) state.motion = motion;
                }
            return renamed;
        }

        // ---- Hand gestures and blinks ----

        private static int Gate(AnimatorController fx, Plan plan)
        {
            Parameter(fx, ExpressionsDisabled, AnimatorControllerParameterType.Bool);
            var eye = Parameter(fx, EyeTracking, AnimatorControllerParameterType.Float);
            var blinks = new HashSet<string>(FaceTrackingDetection.Shapes(plan.Face).Where(IsBlink), StringComparer.Ordinal);
            var index = FaceTrackingNames.Index(FaceTrackingDetection.Shapes(plan.Face));
            foreach (string name in new[] { "EyeClosedLeft", "EyeClosedRight" }) { var shape = FaceTrackingNames.Resolve(name, index); if (shape != null) blinks.Add(shape); }
            // The shapes face tracking drives: gestures that move other shapes of the face mesh (ears, feathers) keep working.
            var tracked = new HashSet<string>(fx.layers.Where(l => l.stateMachine != null && IsTracking(l.stateMachine))
                .SelectMany(l => States(l.stateMachine)).SelectMany(s => Shapes(s.motion, plan.FacePath)), StringComparer.Ordinal);
            int gated = 0;
            foreach (var layer in fx.layers)
            {
                var machine = layer.stateMachine;
                if (machine == null || IsTracking(machine)) continue;
                var states = States(machine).ToList();
                var transitions = Transitions(machine).ToList();
                if (!states.Any(s => Shapes(s.motion, plan.FacePath).Any(tracked.Contains))) continue;
                // Expressions: while the face is tracked the layer rests. Nothing leaves the rest state (hand gestures, menu
                // toggles, Entry and Exit alike), and Any State brings an expression shown back to it.
                var rest = machine.defaultState;
                if (rest != null && transitions.Any(t => t.conditions.Any(c => GestureParameters.Contains(c.parameter))))
                {
                    foreach (var transition in transitions)
                        if (transition.destinationState != rest && !transition.conditions.Any(c => c.parameter == ExpressionsDisabled))
                            Require(transition, AnimatorConditionMode.IfNot, 0, ExpressionsDisabled);
                    var back = Keep(fx, new AnimatorStateTransition
                    {
                        destinationState = rest, hasExitTime = false, hasFixedDuration = true, duration = .1f, canTransitionToSelf = false,
                        conditions = new[] { new AnimatorCondition { mode = AnimatorConditionMode.If, parameter = ExpressionsDisabled } },
                    });
                    machine.anyStateTransitions = machine.anyStateTransitions.Append(back).ToArray();
                    gated++;
                }
                // Blinks the avatar animates itself wait while the eyes are tracked: transitions into a state that closes
                // the eyes, not those back to one holding them open.
                foreach (var transition in transitions.Where(t => t.destinationState != null && Raised(t.destinationState.motion, plan.FacePath).Any(blinks.Contains)))
                    if (!transition.conditions.Any(c => c.parameter == EyeTracking))
                    {
                        if (eye.type == AnimatorControllerParameterType.Bool) Require(transition, AnimatorConditionMode.IfNot, 0, EyeTracking);
                        else Require(transition, AnimatorConditionMode.Less, .5f, EyeTracking);
                    }
            }
            return gated;
        }

        // Setters, not AddCondition, AddParameter or AddAnyStateTransition: those record Undo steps, and the face tracking
        // test edits its controllers in memory.
        private static void Require(AnimatorTransitionBase transition, AnimatorConditionMode mode, float threshold, string parameter) =>
            transition.conditions = transition.conditions.Append(new AnimatorCondition { mode = mode, threshold = threshold, parameter = parameter }).ToArray();

        private static bool IsBlink(string shape) => shape.IndexOf("blink", StringComparison.OrdinalIgnoreCase) >= 0;

        // The template's own layers read VRCFaceTracking's parameters.
        private static bool IsTracking(AnimatorStateMachine machine) =>
            Transitions(machine).SelectMany(t => t.conditions).Select(c => c.parameter)
                .Concat(States(machine).SelectMany(s => TreeParameters(s.motion)))
                .Any(p => p != null && (p.Contains("v2/") || p.StartsWith("OSCm/", StringComparison.Ordinal) || p.StartsWith("FT/", StringComparison.Ordinal)));

        private static IEnumerable<string> TreeParameters(Motion motion)
        {
            if (!(motion is BlendTree tree)) yield break;
            yield return tree.blendParameter; yield return tree.blendParameterY;
            foreach (var child in tree.children)
            {
                yield return child.directBlendParameter;
                foreach (var p in TreeParameters(child.motion)) yield return p;
            }
        }

        // The face's shapes a motion moves above 0: a rest clip holding a blink at 0 keeps the eyes open.
        private static IEnumerable<string> Raised(Motion motion, string facePath)
        {
            if (motion is AnimationClip clip)
                return AnimationUtility.GetCurveBindings(clip)
                    .Where(b => b.path == facePath && b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal) &&
                                AnimationUtility.GetEditorCurve(clip, b).keys.Any(k => k.value > 0f))
                    .Select(b => b.propertyName.Substring(11));
            if (motion is BlendTree tree) return tree.children.SelectMany(c => Raised(c.motion, facePath));
            return Enumerable.Empty<string>();
        }

        private static IEnumerable<string> Shapes(Motion motion, string facePath)
        {
            if (motion is AnimationClip clip)
                return AnimationUtility.GetCurveBindings(clip)
                    .Where(b => b.path == facePath && b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    .Select(b => b.propertyName.Substring(11));
            if (motion is BlendTree tree) return tree.children.SelectMany(c => Shapes(c.motion, facePath));
            return Enumerable.Empty<string>();
        }

        private static AnimatorControllerParameter Parameter(AnimatorController fx, string name, AnimatorControllerParameterType type)
        {
            var existing = fx.parameters.FirstOrDefault(p => p.name == name);
            if (existing != null) return existing;
            var parameter = new AnimatorControllerParameter { name = name, type = type };
            fx.parameters = fx.parameters.Append(parameter).ToArray();
            return parameter;
        }

        private static IEnumerable<AnimatorState> States(AnimatorStateMachine machine)
        {
            if (machine == null) yield break;
            foreach (var child in machine.states) yield return child.state;
            foreach (var sub in machine.stateMachines)
                foreach (var state in States(sub.stateMachine)) yield return state;
        }

        // Every transition of a layer: Any State's, Entry's, the states', and those leaving its sub-state machines.
        private static IEnumerable<AnimatorTransitionBase> Transitions(AnimatorStateMachine machine)
        {
            if (machine == null) yield break;
            foreach (var transition in machine.anyStateTransitions) yield return transition;
            foreach (var transition in machine.entryTransitions) yield return transition;
            foreach (var state in machine.states)
                foreach (var transition in state.state.transitions) yield return transition;
            foreach (var sub in machine.stateMachines)
            {
                foreach (var transition in machine.GetStateMachineTransitions(sub.stateMachine)) yield return transition;
                foreach (var transition in Transitions(sub.stateMachine)) yield return transition;
            }
        }
    }
}
