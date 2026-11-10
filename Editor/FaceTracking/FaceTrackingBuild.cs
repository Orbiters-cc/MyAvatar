using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;
using Orbiters.Toolkit.Editor.Animations;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Build;
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
    /// (FacialExpressionsDisabled, set by the template), its other hand gestures leave a tracked mouth alone (jaw and
    /// tongue included), and blinks it animates wait while eye tracking is on;</item>
    /// <item>the marker's quick settings: the smoothing (for the wearer and for others, <see cref="Pace"/>), the mouth's
    /// tuning, and the features others see (the parameters of the others are left out of VRCFury's copy of the expression
    /// parameters and menu).</item>
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
            /// <summary>The avatar (the build copy): where its jaw and tongue are. Null leaves them out.</summary>
            public Transform Root;
            public FaceTrackingSmoothing Smoothing; public bool Expressive;
            /// <summary>The template's parameters of the features others don't see.</summary>
            public HashSet<string> Removed;

            private HashSet<string> shapes;
            private Dictionary<string, string> index;
            /// <summary>The face's blendshapes, read once for every step.</summary>
            public HashSet<string> Shapes => shapes ??= new HashSet<string>(FaceTrackingDetection.Shapes(Face), StringComparer.Ordinal);
            /// <summary>The face's blendshapes by normalized name (<see cref="FaceTrackingNames.Resolve"/>).</summary>
            public Dictionary<string, string> Index => index ??= FaceTrackingNames.Index(Shapes);
        }

        /// <summary>
        /// The FX controller's layers, their states and transitions, read once before the edits: inside
        /// <see cref="ControllerEdits.Batch"/> the controller's layers show an empty stand-in, so every step reaches them
        /// through this. The steps add no state, sub-state machine or layer, so it stays true while they run; what states
        /// play is read from the states themselves. The parameters are changed here and written once.
        /// </summary>
        private sealed class FxLayers
        {
            internal sealed class Layer
            {
                public string Name;
                public AnimatorStateMachine Machine;
                public List<AnimatorState> States;
                // Any State's, Entry's, the states', and those leaving its sub-state machines, nested ones included.
                public List<AnimatorTransitionBase> Transitions;
                /// <summary>One of the template's own layers (it reads VRCFaceTracking's parameters).</summary>
                public bool Tracking;
            }

            public readonly AnimatorController Controller;
            /// <summary>Every layer, by index in the controller.</summary>
            public readonly List<Layer> Layers = new List<Layer>();
            private readonly List<AnimatorControllerParameter> parameters;
            private bool parametersChanged;

            public FxLayers(AnimatorController controller)
            {
                Controller = controller;
                foreach (var layer in controller.layers)
                {
                    var machine = layer.stateMachine;
                    var entry = new Layer { Name = layer.name, Machine = machine, States = States(machine).ToList(), Transitions = Transitions(machine).ToList() };
                    entry.Tracking = machine != null && IsTracking(entry);
                    Layers.Add(entry);
                }
                parameters = controller.parameters.ToList();
            }

            /// <summary>The layers with a state machine.</summary>
            public IEnumerable<Layer> Machines => Layers.Where(l => l.Machine != null);

            public IReadOnlyList<AnimatorControllerParameter> Parameters => parameters;

            public AnimatorControllerParameter Find(string name) => parameters.FirstOrDefault(p => p.name == name);

            /// <summary>The parameter of that name, added with that type when the controller has none.</summary>
            public AnimatorControllerParameter Parameter(string name, AnimatorControllerParameterType type)
            {
                var existing = Find(name);
                if (existing != null) return existing;
                var parameter = new AnimatorControllerParameter { name = name, type = type };
                parameters.Add(parameter);
                parametersChanged = true;
                return parameter;
            }

            /// <summary>A parameter of <see cref="Parameters"/> was changed.</summary>
            public void ParametersChanged() => parametersChanged = true;

            /// <summary>Writes the parameters into the controller, once, when something changed them.</summary>
            public void WriteParameters()
            {
                if (!parametersChanged) return;
                Controller.parameters = parameters.ToArray();
                parametersChanged = false;
            }
        }

        /// <summary>
        /// The template's Local Smoothing to start with: 0 snaps, 1 smooths most. Each frame takes 15 × the frame time plus
        /// 0.69 − 0.59 × this of the new value: at 90 fps the template's own 0.25 keeps 29 % of the last frame (the face jumps
        /// from one tracker frame to the next), 0.6 keeps half of it like OSCmooth's local default, 0.85 two thirds.
        /// </summary>
        internal static float StartingSmoothing(FaceTrackingSmoothing smoothing) =>
            smoothing == FaceTrackingSmoothing.Responsive ? .25f : smoothing == FaceTrackingSmoothing.Smooth ? .85f : .6f;

        /// <summary>How much slower than the eyes the mouth follows (1: as fast): it keeps 70 % of the last frame at 90 fps, 80 % when smooth.</summary>
        internal static float MouthSlowdown(FaceTrackingSmoothing smoothing) =>
            smoothing == FaceTrackingSmoothing.Responsive ? 1f : smoothing == FaceTrackingSmoothing.Smooth ? SmoothSlowdown : SlowSmoothing;
        // What the capture found for the apply, kept by the build (BuildContext): a new build of the same object starts empty.
        private sealed class Planned { public Plan Plan; }

        // Before VRCFury (-10000): its components, and the marker's face reference, are still there.
        internal sealed class Capture : IVRCSDKPreprocessAvatarCallback
        {
            public int callbackOrder => -10050;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                var marker = avatar.GetComponentInChildren<MyAvatarFaceTracking>(true);
                var planned = BuildContext.For(avatar).State<Planned>();
                planned.Plan = null;
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
                    planned.Plan = new Plan
                    {
                        Standard = standard, FacePath = facePath, Face = face.sharedMesh, Root = avatar.transform,
                        Smoothing = marker.smoothing, Expressive = marker.expressiveMouth,
                        // A part of the face without its shapes (eyes on a face made for lip tracking) costs no bits.
                        Removed = FaceTrackingFeatureSet.Removed(FaceTrackingFeatureSet.Parameters(marker.gameObject).Select(p => p.name),
                            marker.synced & FaceTrackingDetection.Trackable(standard, face.sharedMesh)),
                    };
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
                var context = BuildContext.For(avatar);
                var planned = context.State<Planned>();
                var plan = planned.Plan;
                if (plan == null) return true;
                planned.Plan = null;
                var descriptor = avatar.GetComponent<VRCAvatarDescriptor>();
                AnimatorController Fx() => descriptor != null ? descriptor.baseAnimationLayers.FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController : null;
                var fx = Fx();
                if (fx == null) return true;
                // An avatar VRCFury did not build: the build's own copies of its controllers, as the menu step takes them (MAF-4).
                if (!Editable(fx))
                {
                    _ = context.Animations;
                    fx = Fx();
                }
                if (fx == null || !Editable(fx))
                {
                    Debug.LogWarning("[My Avatar] Face tracking: the FX controller “" + (fx != null ? fx.name : "") + "” could not be copied for this build, so its blendshape names and gestures were left as they are.");
                    return true;
                }
                var (renamed, gated, quieted, tuned, paced) = Build(fx, plan.Standard, plan.FacePath, plan.Face, plan.Root, plan.Expressive, plan.Smoothing);
                string settings = Configure(descriptor, StartingSmoothing(plan.Smoothing), plan.Removed);
                Debug.Log($"[My Avatar] Face tracking ({plan.Standard.Label}): {renamed} template blendshape curves animate this face's own shapes, {gated} expression layers wait while tracking, {quieted} other gesture states leave the tracked mouth alone. {tuned} {paced} {settings}");
                return true;
            }
        }

        /// <summary>
        /// Everything a build changes in <paramref name="fx"/> (VRCFury's copy, or the face tracking test's): <see cref="Finish"/>,
        /// <see cref="Tune"/> and <see cref="Pace"/>, in one <see cref="ControllerEdits.Batch"/>. Each state, transition, blend
        /// tree or played clip changed would otherwise rebuild the whole merged controller, the avatar's biggest; in the batch
        /// it is rebuilt once. The layers are read before it, no layer is added or removed, and the parameters are written
        /// once (inside the batch: a parameter list written there costs no rebuild of its own).
        /// </summary>
        internal static (int renamed, int gated, int quieted, Tuning tuning, string paced) Build(AnimatorController fx, FaceTrackingStandard standard, string facePath, Mesh face,
            Transform root, bool expressive, FaceTrackingSmoothing smoothing)
        {
            var plan = new Plan { Standard = standard, FacePath = facePath, Face = face, Root = root };
            var layers = new FxLayers(fx);
            using (ControllerEdits.Batch(new[] { fx }))
            {
                var (renamed, gated, quieted) = Finish(layers, plan);
                var tuning = Tune(layers, plan, expressive, MouthSlowdown(smoothing));
                string paced = Pace(layers, smoothing);
                layers.WriteParameters();
                return (renamed, gated, quieted, tuning, paced);
            }
        }

        /// <summary>
        /// Renames the template's blendshape curves and gates gestures and blinks in <paramref name="fx"/> (VRCFury's copy);
        /// <paramref name="root"/> (the build copy) has the jaw and tongue the hands no longer move while the mouth is tracked.
        /// </summary>
        internal static (int renamed, int gated, int quieted) Finish(AnimatorController fx, FaceTrackingStandard standard, string facePath, Mesh face, Transform root = null)
        {
            var layers = new FxLayers(fx);
            var result = Finish(layers, new Plan { Standard = standard, FacePath = facePath, Face = face, Root = root });
            layers.WriteParameters();
            return result;
        }

        private static (int renamed, int gated, int quieted) Finish(FxLayers fx, Plan plan)
        {
            int renamed = Rename(fx, plan);
            return (renamed, Gate(fx, plan, out int quieted), quieted);
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

        /// <summary>Build data (<see cref="AttachmentAnimationBuild.IsBuildData"/>): anything else (the avatar's own assets) is never edited.</summary>
        internal static bool Editable(Object asset) => Owned(asset, null);

        // The build's data (VRCFury's working copies, the build's own copies and what it made) can be edited, and what is
        // in the controller's own file; anything else is copied first. One definition with the menu step's (MAF-4).
        private static bool Owned(Object asset, AnimatorController fx) =>
            AttachmentAnimationBuild.IsBuildData(asset) || (fx != null && AssetDatabase.GetAssetPath(asset) == AssetDatabase.GetAssetPath(fx));

        private static T Own<T>(T asset, AnimatorController fx) where T : Object
        {
            if (Owned(asset, fx)) return asset;
            var copy = Object.Instantiate(asset);
            copy.name = asset.name;
            if (AssetDatabase.Contains(fx)) AssetDatabase.AddObjectToAsset(copy, fx);
            return copy;
        }

        // ---- Blendshape names ----

        private static int Rename(FxLayers fx, Plan plan)
        {
            var mesh = plan.Shapes;
            var index = plan.Index;
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
                        var owned = Own(clip, fx.Controller);
                        var existing = new HashSet<string>(AnimationUtility.GetCurveBindings(owned).Select(b => b.path + "|" + b.propertyName));
                        var targets = new List<EditorCurveBinding>();
                        var curves = new List<AnimationCurve>();
                        foreach (var (binding, shape) in moves)
                        {
                            var target = EditorCurveBinding.FloatCurve(binding.path, typeof(SkinnedMeshRenderer), "blendShape." + shape);
                            // The face's own animation of that shape wins. A target is a shape of the face, a moved curve
                            // never is: no curve is both.
                            if (existing.Add(target.path + "|" + target.propertyName)) { targets.Add(target); curves.Add(AnimationUtility.GetEditorCurve(owned, binding)); }
                            renamed++;
                        }
                        // All of a clip's curves at once: set one by one, each rebuilds the clip.
                        AnimationUtility.SetEditorCurves(owned, moves.Select(m => m.binding).ToArray(), new AnimationCurve[moves.Count]);
                        if (targets.Count > 0) AnimationUtility.SetEditorCurves(owned, targets.ToArray(), curves.ToArray());
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
                    if (changed) { var owned = Own(tree, fx.Controller); owned.children = children; result = owned; }
                }
                done[motion] = result;
                return result;
            }
            foreach (var layer in fx.Layers)
                foreach (var state in layer.States)
                {
                    var motion = Visit(state.motion);
                    if (motion != state.motion) state.motion = motion;
                }
            return renamed;
        }

        // ---- Hand gestures and blinks ----

        private static int Gate(FxLayers fx, Plan plan, out int quieted)
        {
            fx.Parameter(ExpressionsDisabled, AnimatorControllerParameterType.Bool);
            var eye = fx.Parameter(EyeTracking, AnimatorControllerParameterType.Float);
            var blinks = new HashSet<string>(plan.Shapes.Where(IsBlink), StringComparer.Ordinal);
            foreach (string name in new[] { "EyeClosedLeft", "EyeClosedRight" }) { var shape = FaceTrackingNames.Resolve(name, plan.Index); if (shape != null) blinks.Add(shape); }
            // The shapes face tracking drives: gestures that move other shapes of the face mesh (ears, feathers) keep working.
            var tracked = new HashSet<string>(fx.Machines.Where(l => l.Tracking)
                .SelectMany(l => l.States).SelectMany(s => Shapes(s.motion, plan.FacePath)), StringComparer.Ordinal);
            int gated = 0;
            var resting = new HashSet<AnimatorStateMachine>();
            // The avatar's own Bool "EyeTrackingActive" is the template's Float once merged: its If and IfNot read it as over or
            // under a half, in every layer (eye props and eye bones too, not only the face's).
            if (eye.type == AnimatorControllerParameterType.Float)
                foreach (var layer in fx.Machines)
                    foreach (var transition in layer.Transitions.Where(t => t.conditions.Any(c => c.parameter == EyeTracking && (c.mode == AnimatorConditionMode.If || c.mode == AnimatorConditionMode.IfNot))))
                        transition.conditions = transition.conditions.Select(c => c.parameter != EyeTracking || c.mode != AnimatorConditionMode.If && c.mode != AnimatorConditionMode.IfNot ? c
                            : new AnimatorCondition { parameter = EyeTracking, mode = c.mode == AnimatorConditionMode.If ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less, threshold = .5f }).ToArray();
            foreach (var layer in fx.Machines)
            {
                var machine = layer.Machine;
                if (layer.Tracking) continue;
                var states = layer.States;
                var transitions = layer.Transitions;
                var shapes = new HashSet<string>(states.SelectMany(s => Shapes(s.motion, plan.FacePath)), StringComparer.Ordinal);
                if (shapes.Count == 0) continue;
                // Expressions: the face's shapes tracking drives, or its own expression shapes ("smile", "furrow"), not a body
                // part's (ears, tail, an arm flex) nor blinks alone. While the face is tracked, or "Disable Facial Expressions"
                // is on, the layer rests. Nothing leaves the rest state (hand gestures, menu toggles, Entry and Exit alike),
                // and Any State brings an expression shown back to it.
                bool expression = shapes.Any(tracked.Contains) || shapes.Any(s => !IsBlink(s) && !BodyPart(s));
                var rest = machine.defaultState;
                if (expression && rest != null && transitions.Any(t => t.conditions.Any(c => GestureParameters.Contains(c.parameter))))
                {
                    foreach (var transition in transitions)
                        if (transition.destinationState != rest && !transition.conditions.Any(c => c.parameter == ExpressionsDisabled))
                            Require(transition, AnimatorConditionMode.IfNot, 0, ExpressionsDisabled);
                    var back = Keep(fx.Controller, new AnimatorStateTransition
                    {
                        destinationState = rest, hasExitTime = false, hasFixedDuration = true, duration = .1f, canTransitionToSelf = false,
                        conditions = new[] { new AnimatorCondition { mode = AnimatorConditionMode.If, parameter = ExpressionsDisabled } },
                    });
                    machine.anyStateTransitions = machine.anyStateTransitions.Append(back).ToArray();
                    resting.Add(machine);
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
            // The hands' other layers (ears, feathers) keep moving, without the mouth.
            quieted = Quiet(fx, plan, tracked, resting);
            return gated;
        }

        // Setters, not AddCondition, AddParameter or AddAnyStateTransition: those record Undo steps, and the face tracking
        // test edits its controllers in memory.
        private static void Require(AnimatorTransitionBase transition, AnimatorConditionMode mode, float threshold, string parameter) =>
            transition.conditions = transition.conditions.Append(new AnimatorCondition { mode = mode, threshold = threshold, parameter = parameter }).ToArray();

        private static bool IsBlink(string shape) => shape.IndexOf("blink", StringComparison.OrdinalIgnoreCase) >= 0;

        private static readonly HashSet<string> BodyParts = new HashSet<string>(StringComparer.Ordinal)
        {
            "arm", "leg", "hand", "finger", "foot", "feet", "toe", "claw", "tail", "wing", "ear", "feather", "fthr", "horn", "hair",
            "mane", "fluff", "chest", "breast", "belly", "butt", "hip", "muscle", "flex", "bicep", "pec", "fat", "thigh", "calf",
            "shoulder", "neck", "spine", "body", "collar", "goggle", "glasses", "hat",
        };

        // A shape of a body part rather than of the face's expression: "ultra arm flex right", "EarsUp", "Tail_FurStandUp".
        private static bool BodyPart(string shape) =>
            System.Text.RegularExpressions.Regex.Split(System.Text.RegularExpressions.Regex.Replace(shape, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant(), "[^a-z]+")
                .Any(w => BodyParts.Contains(w) || w.Length > 3 && w.EndsWith("s", StringComparison.Ordinal) && BodyParts.Contains(w.Substring(0, w.Length - 1)));

        // The template's own layers read VRCFaceTracking's parameters.
        private static bool IsTracking(FxLayers.Layer layer) =>
            layer.Transitions.SelectMany(t => t.conditions).Select(c => c.parameter)
                .Concat(layer.States.SelectMany(s => TreeParameters(s.motion)))
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
