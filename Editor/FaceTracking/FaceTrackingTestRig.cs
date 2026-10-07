using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// The built avatar's playable layers, played the way VRChat plays them for its wearer, outside Play Mode: a
    /// PlayableGraph with the avatar's pose as the base, the Additive controller added on top (the template's eye
    /// rotation) and the FX controller over it (the face). Parameters are shared by every layer and start at VRChat's
    /// built-in values and the expression parameters' defaults. The VRChat behaviours a layer's states carry run when the
    /// state is entered: parameter drivers (the template's FacialExpressionsDisabled), animator and playable layer
    /// controls. Tracking controls only matter to VRChat's own eye and mouth tracking, which the test does not run.
    /// </summary>
    internal sealed class FaceTrackingTestRig : IDisposable
    {
        private sealed class Layer
        {
            public VRCAvatarDescriptor.AnimLayerType Type;
            public AnimatorControllerPlayable Playable;
            public int Input;
            public Dictionary<string, AnimatorControllerParameterType> Types;
            public Dictionary<int, StateMachineBehaviour[]>[] States;
            public int[] Entered;
        }

        private sealed class Blend { public Layer Layer; public int Index = -1; public float From, To, Duration, Time; }

        private readonly PlayableGraph graph;
        private readonly AnimationLayerMixerPlayable mixer;
        private readonly List<Layer> layers = new List<Layer>();
        private readonly List<Blend> blends = new List<Blend>();
        private readonly Dictionary<string, float> values = new Dictionary<string, float>(StringComparer.Ordinal);
        private readonly HashSet<string> dirty = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, VRCExpressionParameters.Parameter> expression = new Dictionary<string, VRCExpressionParameters.Parameter>(StringComparer.Ordinal);
        private readonly AnimationClip pose;
        private readonly System.Random random = new System.Random();

        /// <summary>The parameters VRCFaceTracking would drive on this avatar.</summary>
        public readonly VrcftAvatarParameters Vrcft;

        /// <summary>VRChat's built-in parameters for the wearer in VR, standing still.</summary>
        private static readonly (string name, float value)[] BuiltIns =
        {
            ("IsLocal", 1f), ("PreviewMode", 0f), ("Viseme", 0f), ("Voice", 0f), ("GestureLeft", 0f), ("GestureRight", 0f),
            ("GestureLeftWeight", 0f), ("GestureRightWeight", 0f), ("AngularY", 0f), ("VelocityX", 0f), ("VelocityY", 0f), ("VelocityZ", 0f),
            ("VelocityMagnitude", 0f), ("Upright", 1f), ("Grounded", 1f), ("Seated", 0f), ("AFK", 0f), ("TrackingType", 3f), ("VRMode", 1f),
            ("MuteSelf", 0f), ("InStation", 0f), ("Earmuffs", 0f), ("IsOnFriendsList", 0f), ("AvatarVersion", 3f), ("IsAnimatorEnabled", 1f),
            ("ScaleModified", 0f), ("ScaleFactor", 1f), ("ScaleFactorInverse", 1f),
        };

        public FaceTrackingTestRig(GameObject copy, VRCAvatarDescriptor descriptor, AnimatorController additive, AnimatorController fx)
        {
            var animator = copy.GetComponent<Animator>();
            if (animator == null) throw new InvalidOperationException("The built avatar has no Animator.");
            // Nothing sees the preview scene's avatar: without this the animator would cull every update.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.applyRootMotion = false;
            graph = PlayableGraph.Create("My Avatar face tracking test");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var output = AnimationPlayableOutput.Create(graph, "Avatar", animator);
            mixer = AnimationLayerMixerPlayable.Create(graph, 3);
            output.SetSourcePlayable(mixer);
            // VRChat's base layer stands the avatar; here its pose as it is in the scene holds it still.
            if (animator.isHuman && animator.avatar != null)
            {
                pose = Pose(animator);
                var clip = AnimationClipPlayable.Create(graph, pose);
                graph.Connect(clip, 0, mixer, 0);
                mixer.SetInputWeight(0, 1f);
            }
            if (additive != null)
            {
                Add(VRCAvatarDescriptor.AnimLayerType.Additive, additive, 1);
                mixer.SetLayerAdditive(1, true);
            }
            Add(VRCAvatarDescriptor.AnimLayerType.FX, fx, 2);

            foreach (var (name, value) in BuiltIns) Set(name, value);
            var parameters = descriptor != null ? descriptor.expressionParameters : null;
            foreach (var parameter in parameters != null && parameters.parameters != null ? parameters.parameters : Array.Empty<VRCExpressionParameters.Parameter>())
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.name) || expression.ContainsKey(parameter.name)) continue;
                expression[parameter.name] = parameter;
                Set(parameter.name, parameter.defaultValue);
            }
            Vrcft = VrcftAvatarParameters.For(parameters);
            Push();
        }

        private void Add(VRCAvatarDescriptor.AnimLayerType type, AnimatorController controller, int input)
        {
            var playable = AnimatorControllerPlayable.Create(graph, controller);
            graph.Connect(playable, 0, mixer, input);
            mixer.SetInputWeight(input, 1f);
            var layer = new Layer
            {
                Type = type, Playable = playable, Input = input,
                Types = controller.parameters.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().type, StringComparer.Ordinal),
                States = controller.layers.Select(l => Behaviours(l)).ToArray(),
                Entered = Enumerable.Repeat(0, controller.layers.Length).ToArray(),
            };
            layers.Add(layer);
        }

        /// <summary>A parameter as every layer will read it from the next evaluation (bools are 0 or 1).</summary>
        public void Set(string name, float value)
        {
            if (string.IsNullOrEmpty(name) || (values.TryGetValue(name, out float current) && current == value)) return;
            values[name] = value;
            dirty.Add(name);
        }

        public float Get(string name) => values.TryGetValue(name, out float value) ? value : 0f;

        /// <summary>A parameter back to its starting value (the expression parameter's default).</summary>
        public void Reset(string name) => Set(name, expression.TryGetValue(name, out var parameter) ? parameter.defaultValue : 0f);

        public bool Has(string name) => expression.ContainsKey(name);

        /// <summary>Plays <paramref name="seconds"/> of the layers, then runs the behaviours of the states they entered.</summary>
        public void Evaluate(float seconds)
        {
            Push();
            graph.Evaluate(seconds);
            foreach (var layer in layers) Enter(layer);
            Advance(seconds);
        }

        private void Push()
        {
            if (dirty.Count == 0) return;
            foreach (string name in dirty)
            {
                float value = values[name];
                foreach (var layer in layers)
                {
                    if (!layer.Types.TryGetValue(name, out var type)) continue;
                    // VRChat converts a parameter to the type each controller declares.
                    switch (type)
                    {
                        case AnimatorControllerParameterType.Float: layer.Playable.SetFloat(name, value); break;
                        case AnimatorControllerParameterType.Int: layer.Playable.SetInteger(name, Mathf.RoundToInt(value)); break;
                        case AnimatorControllerParameterType.Bool: layer.Playable.SetBool(name, value != 0f); break;
                        case AnimatorControllerParameterType.Trigger: if (value != 0f) layer.Playable.SetTrigger(name); break;
                    }
                }
            }
            dirty.Clear();
        }

        // ---- VRChat's state behaviours ----

        // Each state's behaviours by its full path hash: the state machines' names down to the state ("Machine.Sub.State";
        // the root machine keeps its name when VRCFury renames the layer), and by its name when no full path matches.
        private static Dictionary<int, StateMachineBehaviour[]> Behaviours(AnimatorControllerLayer layer)
        {
            var states = new Dictionary<int, StateMachineBehaviour[]>();
            var names = new Dictionary<int, StateMachineBehaviour[]>();
            void Walk(AnimatorStateMachine machine, string path)
            {
                if (machine == null) return;
                foreach (var child in machine.states)
                {
                    if (child.state == null || child.state.behaviours.Length == 0) continue;
                    states[Animator.StringToHash(path + "." + child.state.name)] = child.state.behaviours;
                    names[Animator.StringToHash(child.state.name)] = child.state.behaviours;
                }
                foreach (var sub in machine.stateMachines) Walk(sub.stateMachine, path + "." + sub.stateMachine.name);
            }
            if (layer.stateMachine != null) Walk(layer.stateMachine, layer.stateMachine.name);
            foreach (var pair in names) states[ShortName(pair.Key)] = pair.Value;
            return states;
        }

        // Short name hashes live beside the full path ones, apart from them.
        private static int ShortName(int hash) => hash ^ 0x5bd1e995;

        private void Enter(Layer layer)
        {
            for (int i = 0; i < layer.States.Length; i++)
            {
                // A state is entered when the transition to it starts.
                var state = layer.Playable.IsInTransition(i) ? layer.Playable.GetNextAnimatorStateInfo(i) : layer.Playable.GetCurrentAnimatorStateInfo(i);
                if (state.fullPathHash == layer.Entered[i]) continue;
                layer.Entered[i] = state.fullPathHash;
                if (!layer.States[i].TryGetValue(state.fullPathHash, out var behaviours) && !layer.States[i].TryGetValue(ShortName(state.shortNameHash), out behaviours)) continue;
                foreach (var behaviour in behaviours) Run(behaviour);
            }
        }

        private void Run(StateMachineBehaviour behaviour)
        {
            switch (behaviour)
            {
                case VRCAvatarParameterDriver driver:
                    foreach (var change in driver.parameters) Drive(change);
                    break;
                case VRCAnimatorLayerControl control:
                    var target = layers.FirstOrDefault(l => Same(l.Type, control.playable));
                    if (target != null && control.layer >= 0 && control.layer < target.States.Length)
                        Start(new Blend { Layer = target, Index = control.layer, From = target.Playable.GetLayerWeight(control.layer), To = control.goalWeight, Duration = control.blendDuration });
                    break;
                case VRCPlayableLayerControl playable:
                    var whole = layers.FirstOrDefault(l => Same(l.Type, playable.layer));
                    if (whole != null) Start(new Blend { Layer = whole, From = mixer.GetInputWeight(whole.Input), To = playable.goalWeight, Duration = playable.blendDuration });
                    break;
            }
        }

        private static bool Same(VRCAvatarDescriptor.AnimLayerType type, VRC_AnimatorLayerControl.BlendableLayer layer) =>
            (type == VRCAvatarDescriptor.AnimLayerType.FX && layer == VRC_AnimatorLayerControl.BlendableLayer.FX) ||
            (type == VRCAvatarDescriptor.AnimLayerType.Additive && layer == VRC_AnimatorLayerControl.BlendableLayer.Additive);

        private static bool Same(VRCAvatarDescriptor.AnimLayerType type, VRC_PlayableLayerControl.BlendableLayer layer) =>
            (type == VRCAvatarDescriptor.AnimLayerType.FX && layer == VRC_PlayableLayerControl.BlendableLayer.FX) ||
            (type == VRCAvatarDescriptor.AnimLayerType.Additive && layer == VRC_PlayableLayerControl.BlendableLayer.Additive);

        // VRC_AvatarParameterDriver: set, add, random or copy, as VRChat applies them for the wearer.
        private void Drive(VRC_AvatarParameterDriver.Parameter change)
        {
            if (change == null || string.IsNullOrEmpty(change.name)) return;
            switch (change.type)
            {
                case VRC_AvatarParameterDriver.ChangeType.Set: Set(change.name, change.value); break;
                case VRC_AvatarParameterDriver.ChangeType.Add: Set(change.name, Get(change.name) + change.value); break;
                case VRC_AvatarParameterDriver.ChangeType.Copy:
                    float source = Get(change.source);
                    if (change.convertRange)
                    {
                        float t = Mathf.Approximately(change.sourceMax, change.sourceMin) ? 0f : (source - change.sourceMin) / (change.sourceMax - change.sourceMin);
                        source = Mathf.LerpUnclamped(change.destMin, change.destMax, t);
                    }
                    Set(change.name, source);
                    break;
                case VRC_AvatarParameterDriver.ChangeType.Random:
                    var type = TypeOf(change.name);
                    if (type == AnimatorControllerParameterType.Bool || type == AnimatorControllerParameterType.Trigger)
                        Set(change.name, random.NextDouble() < change.chance ? 1f : 0f);
                    else if (type == AnimatorControllerParameterType.Int)
                    {
                        int value, tries = 0, previous = Mathf.RoundToInt(Get(change.name));
                        do value = random.Next(Mathf.RoundToInt(change.valueMin), Mathf.RoundToInt(change.valueMax) + 1);
                        while (change.preventRepeats && value == previous && change.valueMax > change.valueMin && ++tries < 8);
                        Set(change.name, value);
                    }
                    else Set(change.name, Mathf.Lerp(change.valueMin, change.valueMax, (float)random.NextDouble()));
                    break;
            }
        }

        private AnimatorControllerParameterType TypeOf(string name)
        {
            if (expression.TryGetValue(name, out var parameter))
                return parameter.valueType == VRCExpressionParameters.ValueType.Bool ? AnimatorControllerParameterType.Bool
                    : parameter.valueType == VRCExpressionParameters.ValueType.Int ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Float;
            foreach (var layer in layers) if (layer.Types.TryGetValue(name, out var type)) return type;
            return AnimatorControllerParameterType.Float;
        }

        private void Start(Blend blend)
        {
            blends.RemoveAll(b => b.Layer == blend.Layer && b.Index == blend.Index);
            blends.Add(blend);
            Advance(0f);
        }

        private void Advance(float seconds)
        {
            for (int i = blends.Count - 1; i >= 0; i--)
            {
                var blend = blends[i];
                blend.Time += seconds;
                float weight = blend.Duration <= 0f ? blend.To : Mathf.Lerp(blend.From, blend.To, blend.Time / blend.Duration);
                if (blend.Index >= 0) blend.Layer.Playable.SetLayerWeight(blend.Index, weight);
                else mixer.SetInputWeight(blend.Layer.Input, weight);
                if (blend.Duration <= 0f || blend.Time >= blend.Duration) blends.RemoveAt(i);
            }
        }

        // ---- The base pose ----

        // The avatar's pose as a humanoid clip (every muscle and the body), so the layers above move only what they animate.
        private static AnimationClip Pose(Animator animator)
        {
            var handler = new HumanPoseHandler(animator.avatar, animator.transform);
            var human = new HumanPose();
            handler.GetHumanPose(ref human);
            handler.Dispose();
            var clip = new AnimationClip { name = "Face tracking test pose", hideFlags = HideFlags.HideAndDontSave };
            void Curve(string property, float value) =>
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), property), AnimationCurve.Constant(0f, 1f, value));
            for (int i = 0; i < HumanTrait.MuscleCount; i++) Curve(HumanTrait.MuscleName[i], human.muscles[i]);
            Curve("RootT.x", human.bodyPosition.x); Curve("RootT.y", human.bodyPosition.y); Curve("RootT.z", human.bodyPosition.z);
            Curve("RootQ.x", human.bodyRotation.x); Curve("RootQ.y", human.bodyRotation.y); Curve("RootQ.z", human.bodyRotation.z); Curve("RootQ.w", human.bodyRotation.w);
            return clip;
        }

        public void Dispose()
        {
            if (graph.IsValid()) graph.Destroy();
            if (pose != null) Object.DestroyImmediate(pose);
        }
    }
}
