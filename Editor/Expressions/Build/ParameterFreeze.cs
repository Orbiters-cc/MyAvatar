using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Expressions
{
    /// <summary>
    /// What menu controls left out of an upload take with them, on the build copy (VRCFury's working copies of its
    /// controllers, menu and parameters; the avatar's own assets are never edited):
    /// <list type="bullet">
    /// <item>a parameter nothing else sets (no other menu control, parameter driver, animation, contact or PhysBone) keeps
    /// its default: it leaves the expression parameters (its synced bits are free), and every condition on it is decided:
    /// transitions that can't happen any more go, conditions always true are dropped (the transition's other conditions
    /// stay, so logic mixed with the switch keeps working);</item>
    /// <item>a parameter other controls still set (an int several buttons share) only loses the values of the controls
    /// that went;</item>
    /// <item>states nothing reaches any more go, and blend trees a fixed parameter drives keep what it shows;</item>
    /// <item>a layer left in one unchanging state is written into the avatar (objects shown or hidden, materials,
    /// blendshapes, component settings, transforms that aren't humanoid bones) and goes, when no other layer animates the
    /// same things;</item>
    /// <item>objects that can never show any more (and nothing refers to) are deleted, so their meshes, materials and
    /// textures stay out of the upload, as do the animations nothing plays any more.</item>
    /// </list>
    /// </summary>
    internal sealed class ParameterFreeze
    {
        internal sealed class Report
        {
            public int Parameters, Bits, Conditions, Transitions, States, Trees, Layers, Baked, Objects, Deleted;
            /// <summary>Parameters left as they are, and why.</summary>
            public readonly List<string> Kept = new List<string>();

            public override string ToString() =>
                $"{Parameters} parameters fixed ({Bits} synced bits freed), {Transitions} transitions and {Conditions} conditions decided, {States} states, " +
                $"{Trees} blend tree branches and {Layers} layers removed ({Baked} values written into the avatar), {Objects} hidden objects and {Deleted} unused animations deleted" +
                (Kept.Count > 0 ? "; kept: " + string.Join(", ", Kept) : "") + ".";
        }

        // VRChat's own parameters, those VRCFaceTracking drives, and those apps commonly set over OSC (heart rate, chatbox and
        // text displays, OSCmooth, VRCOSC…): never fixed, a menu item that also set one going or not.
        internal static bool Protected(string name) => Toolkit.Editor.VRChat.Parameters.VrcParameters.Protected(name);

        private readonly GameObject root;
        private readonly VRCAvatarDescriptor descriptor;
        private readonly Func<Object, bool> editable;
        private readonly List<(VRCAvatarDescriptor.AnimLayerType Type, AnimatorController Controller)> controllers = new List<(VRCAvatarDescriptor.AnimLayerType, AnimatorController)>();
        private readonly Dictionary<string, HashSet<float>> domains = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);
        private readonly HashSet<Object> removed = new HashSet<Object>();
        // Transitions whose conditions all hold for good: taken as soon as their state is.
        private readonly HashSet<AnimatorTransitionBase> taken = new HashSet<AnimatorTransitionBase>();
        private readonly Report report = new Report();

        /// <param name="editable">Whether a controller, menu, parameters asset or motion may be changed (the build's copies).</param>
        public ParameterFreeze(GameObject root, Func<Object, bool> editable)
        {
            this.root = root;
            this.editable = editable;
            descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return;
            foreach (var layer in (descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()).Concat(descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>()))
                if (!layer.isDefault && layer.animatorController is AnimatorController controller && controllers.All(c => c.Controller != controller))
                    controllers.Add((layer.type, controller));
        }

        /// <summary>
        /// Fixes what <paramref name="candidates"/> (the parameters of controls already taken out of the menu) still decide,
        /// and cleans up after them.
        /// </summary>
        public Report Run(IEnumerable<string> candidates)
        {
            if (descriptor == null) return report;
            var parameters = descriptor.expressionParameters;
            var writers = Writers(controllers.Select(c => c.Controller), root);
            var menu = MenuPaths.Menus(descriptor.expressionsMenu).SelectMany(m => m.controls ?? new List<VRCExpressionsMenu.Control>()).Where(c => c != null).ToList();
            foreach (string name in candidates.Distinct())
            {
                var parameter = parameters != null ? parameters.parameters?.FirstOrDefault(p => p != null && p.name == name) : null;
                if (Protected(name))
                {
                    if (parameter != null && Toolkit.Editor.VRChat.Parameters.VrcParameters.OscDriven(name)) report.Kept.Add(name + " (apps set it over OSC)");
                    continue;
                }
                if (parameter == null) continue;
                if (writers.Unknown.Contains(name)) { report.Kept.Add(name + " (also set by an animation, a driver or a contact)"); continue; }
                if (menu.Any(c => c.subParameters != null && c.subParameters.Any(s => s?.name == name))) { report.Kept.Add(name + " (a puppet still sets it)"); continue; }
                // Only controllers this build may change use it.
                if (controllers.Any(c => !editable(c.Controller) && Uses(c.Controller, name))) { report.Kept.Add(name + " (used by an asset this build doesn't change)"); continue; }
                var domain = new HashSet<float> { parameter.defaultValue };
                if (writers.Values.TryGetValue(name, out var driven)) domain.UnionWith(driven);
                foreach (var control in menu.Where(c => c.parameter != null && c.parameter.name == name)) { domain.Add(control.value); domain.Add(0f); }
                domains[name] = domain;
            }
            if (domains.Count == 0) return report;

            // A fixed parameter no control uses any more leaves the expression parameters; the controllers start it where it stays.
            var fixedOnes = domains.Where(d => d.Value.Count == 1).ToDictionary(d => d.Key, d => d.Value.First());
            var unused = new HashSet<string>(fixedOnes.Keys.Where(n => menu.All(c => c.parameter == null || c.parameter.name != n)), StringComparer.Ordinal);
            if (unused.Count > 0 && editable(parameters))
            {
                foreach (var p in parameters.parameters.Where(p => p != null && unused.Contains(p.name)))
                {
                    report.Parameters++;
                    if (p.networkSynced) report.Bits += VRCExpressionParameters.TypeCost(p.valueType);
                }
                parameters.parameters = parameters.parameters.Where(p => p == null || !unused.Contains(p.name)).ToArray();
                EditorUtility.SetDirty(parameters);
            }
            foreach (var (_, controller) in controllers.Where(c => editable(c.Controller)))
            {
                var list = controller.parameters;
                bool changed = false;
                foreach (var p in list)
                {
                    if (!fixedOnes.TryGetValue(p.name, out float value)) continue;
                    p.defaultFloat = value; p.defaultInt = Mathf.RoundToInt(value); p.defaultBool = value != 0f;
                    changed = true;
                }
                if (changed) controller.parameters = list;
            }

            foreach (var (_, controller) in controllers.Where(c => editable(c.Controller))) Simplify(controller);
            Bake();
            DeleteHidden();
            DeleteUnused();
            return report;
        }

        // ---- What sets parameters ----

        internal sealed class WriterSet
        {
            /// <summary>Parameters set in ways that can't be known before (animations, Add, Random, Copy, contacts, PhysBones).</summary>
            public readonly HashSet<string> Unknown = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Values parameter drivers set.</summary>
            public readonly Dictionary<string, HashSet<float>> Values = new Dictionary<string, HashSet<float>>(StringComparer.Ordinal);
        }

        /// <summary>What sets parameters besides the menu: parameter drivers, animations, contacts and PhysBones.</summary>
        internal static WriterSet Writers(IEnumerable<AnimatorController> controllers, GameObject root)
        {
            var set = new WriterSet();
            foreach (var controller in controllers)
            {
                if (controller == null) continue;
                foreach (var machine in Machines(controller))
                    foreach (var behaviour in machine.behaviours.Concat(machine.states.SelectMany(s => s.state.behaviours)))
                        if (behaviour is VRCAvatarParameterDriver driver)
                            foreach (var change in driver.parameters ?? new List<VRC_AvatarParameterDriver.Parameter>())
                            {
                                if (change == null || string.IsNullOrEmpty(change.name)) continue;
                                if (change.type == VRC_AvatarParameterDriver.ChangeType.Set)
                                {
                                    if (!set.Values.TryGetValue(change.name, out var values)) set.Values[change.name] = values = new HashSet<float>();
                                    values.Add(change.value);
                                }
                                else set.Unknown.Add(change.name);
                            }
                foreach (var clip in Clips(controller))
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(Animator)) set.Unknown.Add(binding.propertyName);
            }
            // Contacts and PhysBones set their parameters (a PhysBone's are its name with suffixes).
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().Name;
                if (!type.Contains("ContactReceiver") && !type.Contains("PhysBone")) continue;
                var parameter = new SerializedObject(component).FindProperty("parameter");
                if (parameter == null || parameter.propertyType != SerializedPropertyType.String || string.IsNullOrEmpty(parameter.stringValue)) continue;
                if (type.Contains("PhysBone"))
                    foreach (var suffix in new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" }) set.Unknown.Add(parameter.stringValue + suffix);
                else set.Unknown.Add(parameter.stringValue);
            }
            return set;
        }

        private static bool Uses(AnimatorController controller, string name) =>
            controller.parameters.Any(p => p.name == name) &&
            (Machines(controller).Any(m => Transitions(m).Any(t => t.conditions.Any(c => c.parameter == name))) ||
             Machines(controller).SelectMany(m => m.states).Any(s => TreeParameters(s.state.motion).Contains(name)));

        // ---- Controllers ----

        private void Simplify(AnimatorController controller)
        {
            var types = controller.parameters.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().type, StringComparer.Ordinal);
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                foreach (var machine in Machines(layer.stateMachine)) Decide(machine, types);
                if (layer.stateMachine.stateMachines.Length == 0) { Pass(layer.stateMachine); Prune(layer.stateMachine); }
                foreach (var machine in Machines(layer.stateMachine))
                    foreach (var child in machine.states)
                    {
                        var folded = Fold(child.state.motion);
                        if (folded != child.state.motion) child.state.motion = folded;
                    }
            }
        }

        // Conditions on fixed parameters are decided: always true ones are dropped, transitions that can't happen go.
        private void Decide(AnimatorStateMachine machine, Dictionary<string, AnimatorControllerParameterType> types)
        {
            bool Keep(AnimatorTransitionBase transition)
            {
                var kept = new List<AnimatorCondition>();
                AnimatorCondition? spare = null;
                foreach (var condition in transition.conditions)
                {
                    bool? always = Always(condition, types);
                    if (always == false) { report.Transitions++; removed.Add(transition); return false; }
                    if (always == true) { spare ??= condition; report.Conditions++; continue; }
                    kept.Add(condition);
                }
                // A state transition needs a condition or an exit time: one always true is kept then.
                bool exitTime = transition is AnimatorStateTransition state && state.hasExitTime;
                if (kept.Count == 0 && spare != null && !exitTime) { kept.Add(spare.Value); report.Conditions--; taken.Add(transition); }
                if (kept.Count != transition.conditions.Length) transition.conditions = kept.ToArray();
                return true;
            }
            machine.anyStateTransitions = machine.anyStateTransitions.Where(Keep).ToArray();
            machine.entryTransitions = machine.entryTransitions.Where(Keep).ToArray();
            foreach (var child in machine.states)
            {
                var transitions = child.state.transitions;
                var kept = transitions.Where(Keep).ToArray();
                if (kept.Length != transitions.Length) child.state.transitions = kept;
            }
            foreach (var sub in machine.stateMachines)
            {
                var transitions = machine.GetStateMachineTransitions(sub.stateMachine);
                var kept = transitions.Where(Keep).ToArray();
                if (kept.Length != transitions.Length) machine.SetStateMachineTransitions(sub.stateMachine, kept);
            }
        }

        // Whether a condition always holds (true), never does (false), or depends (null).
        private bool? Always(AnimatorCondition condition, Dictionary<string, AnimatorControllerParameterType> types)
        {
            if (!domains.TryGetValue(condition.parameter, out var domain) || !types.TryGetValue(condition.parameter, out var type)) return null;
            bool any = false, all = true;
            foreach (float value in domain)
            {
                bool holds = Holds(condition, type, value);
                any |= holds; all &= holds;
            }
            return all ? true : any ? (bool?)null : false;
        }

        // VRChat converts the parameter to the type the controller declares.
        private static bool Holds(AnimatorCondition condition, AnimatorControllerParameterType type, float value)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    bool on = value != 0f;
                    return condition.mode == AnimatorConditionMode.If ? on : condition.mode == AnimatorConditionMode.IfNot ? !on : true;
                case AnimatorControllerParameterType.Int:
                    int i = Mathf.RoundToInt(value);
                    switch (condition.mode)
                    {
                        case AnimatorConditionMode.Greater: return i > condition.threshold;
                        case AnimatorConditionMode.Less: return i < condition.threshold;
                        case AnimatorConditionMode.Equals: return i == Mathf.RoundToInt(condition.threshold);
                        case AnimatorConditionMode.NotEqual: return i != Mathf.RoundToInt(condition.threshold);
                    }
                    return true;
                default:
                    switch (condition.mode)
                    {
                        case AnimatorConditionMode.Greater: return value > condition.threshold;
                        case AnimatorConditionMode.Less: return value < condition.threshold;
                    }
                    return true;
            }
        }

        // A start the layer leaves at once for good (a toggle switched on by default) is skipped: it starts where it goes.
        private void Pass(AnimatorStateMachine machine)
        {
            // Only the first transition: an earlier one, taken first when it can, could lead elsewhere.
            var any = machine.anyStateTransitions.FirstOrDefault();
            if (any != null && taken.Contains(any) && any.destinationState != null) { machine.defaultState = any.destinationState; return; }
            var seen = new HashSet<AnimatorState>();
            for (var state = machine.defaultState; state != null && seen.Add(state) && state.behaviours.Length == 0;)
            {
                var first = state.transitions.FirstOrDefault();
                if (first == null || !taken.Contains(first) || first.destinationState == null) break;
                machine.defaultState = state = first.destinationState;
            }
        }

        // States nothing reaches any more go (layers without sub-state machines).
        private void Prune(AnimatorStateMachine machine)
        {
            var states = machine.states.Select(s => s.state).ToList();
            if (states.Count < 2) return;
            var reached = new HashSet<AnimatorState>();
            var queue = new Queue<AnimatorState>();
            void Reach(AnimatorState state) { if (state != null && reached.Add(state)) queue.Enqueue(state); }
            Reach(machine.defaultState);
            foreach (var t in machine.anyStateTransitions) Reach(t.destinationState);
            foreach (var t in machine.entryTransitions) Reach(t.destinationState);
            while (queue.Count > 0)
                foreach (var t in queue.Dequeue().transitions)
                {
                    Reach(t.destinationState);
                    // Leaving through Exit enters the machine again.
                    if (t.isExit) { Reach(machine.defaultState); foreach (var e in machine.entryTransitions) Reach(e.destinationState); }
                }
            if (reached.Count == states.Count) return;
            foreach (var state in states.Where(s => !reached.Contains(s)))
            {
                report.States++;
                removed.Add(state);
                removed.UnionWith(state.transitions);
                removed.UnionWith(state.behaviours);
                if (state.motion != null) removed.Add(state.motion);
            }
            machine.states = machine.states.Where(s => reached.Contains(s.state)).ToArray();
        }

        // Blend trees: a direct child weighted by a parameter fixed at 0 goes; a 1D tree on a fixed parameter at one of its
        // thresholds (or past its ends) is the motion it shows.
        private Motion Fold(Motion motion)
        {
            if (!(motion is BlendTree tree) || !editable(tree)) return motion;
            var children = tree.children;
            bool changed = false;
            for (int i = 0; i < children.Length; i++)
            {
                var folded = Fold(children[i].motion);
                if (folded != children[i].motion) { children[i].motion = folded; changed = true; }
            }
            if (tree.blendType == BlendTreeType.Direct)
            {
                var kept = children.Where(c => !(Fixed(c.directBlendParameter, out float weight) && weight == 0f)).ToArray();
                if (kept.Length != children.Length)
                {
                    foreach (var gone in children.Except(kept)) if (gone.motion != null) removed.Add(gone.motion);
                    report.Trees += children.Length - kept.Length;
                    children = kept; changed = true;
                }
            }
            if (changed) tree.children = children;
            if (tree.blendType == BlendTreeType.Simple1D && children.Length > 0 && Fixed(tree.blendParameter, out float at))
            {
                var ordered = children.OrderBy(c => c.threshold).ToList();
                ChildMotion? shown = at <= ordered[0].threshold ? ordered[0] : at >= ordered[ordered.Count - 1].threshold ? ordered[ordered.Count - 1]
                    : ordered.Where(c => Mathf.Approximately(c.threshold, at)).Cast<ChildMotion?>().FirstOrDefault();
                if (shown != null)
                {
                    foreach (var other in children.Where(c => c.motion != shown.Value.motion && c.motion != null)) removed.Add(other.motion);
                    removed.Add(tree);
                    report.Trees += children.Length - 1;
                    return shown.Value.motion;
                }
            }
            return tree;
        }

        private bool Fixed(string parameter, out float value)
        {
            value = 0f;
            if (parameter == null || !domains.TryGetValue(parameter, out var domain) || domain.Count != 1) return false;
            value = domain.First();
            return true;
        }

        // ---- Layers left in one state ----

        private sealed class Binding
        {
            public string Path; public Type Type; public string Property;
            public float Value; public Object Reference; public bool IsReference;
        }

        private void Bake()
        {
            // What every layer of every playable layer animates, to bake only what no other layer touches.
            var animated = new Dictionary<(string, Type, string), int>();
            // By controller and layer index: each read of a controller's layers makes new layer objects.
            var perLayer = new Dictionary<(AnimatorController, int), List<(string, Type, string)>>();
            foreach (var (_, controller) in controllers)
            {
                var all = controller.layers;
                for (int i = 0; i < all.Length; i++)
                {
                    var keys = Motions(all[i].stateMachine).OfType<AnimationClip>()
                        .SelectMany(c => AnimationUtility.GetCurveBindings(c).Concat(AnimationUtility.GetObjectReferenceCurveBindings(c)))
                        .Select(b => (b.path, b.type, b.propertyName)).Distinct().ToList();
                    perLayer[(controller, i)] = keys;
                    foreach (var key in keys) animated[key] = animated.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }
            var animator = root.GetComponent<Animator>();
            var humanoid = new HashSet<Transform>();
            if (animator != null && animator.isHuman)
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                    if (bone != HumanBodyBones.LastBone && animator.GetBoneTransform(bone) is Transform t) humanoid.Add(t);

            foreach (var (type, controller) in controllers.Where(c => editable(c.Controller)))
            {
                var layers = controller.layers;
                var gone = new HashSet<int>();
                // The first layer stays: the next one would become the base layer, always at full weight.
                for (int i = 1; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    var machine = layer.stateMachine;
                    if (machine == null || machine.stateMachines.Length > 0 || machine.states.Length != 1 || machine.behaviours.Length > 0) continue;
                    // A layer at part weight, added on top, or whose weight a layer control changes isn't simply its state.
                    if (layer.defaultWeight < 1f || layer.blendingMode == AnimatorLayerBlendingMode.Additive || Controlled(type, i)) continue;
                    if (layers.Any(l => l.syncedLayerIndex == i)) continue;
                    var state = machine.states[0].state;
                    if (state.behaviours.Length > 0 || (state.motion != null && !(state.motion is AnimationClip))) continue;
                    var values = state.motion is AnimationClip clip ? Constant(clip) : new List<Binding>();
                    if (values == null) continue;
                    // A mask or humanoid bones: VRChat would not have shown those transforms anyway.
                    if (values.Any(v => v.Type == typeof(Transform)) && (layer.avatarMask != null || values.Any(v => v.Type == typeof(Transform) && root.transform.Find(v.Path) is Transform t && (humanoid.Contains(t) || v.Path.Length == 0))))
                        continue;
                    if (values.Any(v => animated.TryGetValue((v.Path, v.Type, v.Property), out int n) && n > 1)) continue;
                    if (!values.All(CanApply)) continue;
                    foreach (var value in values) Apply(value);
                    report.Baked += values.Count;
                    foreach (var key in perLayer[(controller, i)]) animated[key]--;
                    gone.Add(i);
                    removed.Add(machine); removed.Add(state);
                    if (state.motion != null) removed.Add(state.motion);
                }
                if (gone.Count == 0) continue;
                RemoveLayers(type, controller, gone);
                report.Layers += gone.Count;
            }
        }

        // The values of a clip that holds them still (each curve one value); null when it moves.
        private static List<Binding> Constant(AnimationClip clip)
        {
            var list = new List<Binding>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var keys = AnimationUtility.GetEditorCurve(clip, binding).keys;
                if (keys.Length == 0) continue;
                if (keys.Any(k => !Mathf.Approximately(k.value, keys[0].value))) return null;
                list.Add(new Binding { Path = binding.path, Type = binding.type, Property = binding.propertyName, Value = keys[0].value });
            }
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys.Length == 0) continue;
                if (keys.Any(k => k.value != keys[0].value)) return null;
                list.Add(new Binding { Path = binding.path, Type = binding.type, Property = binding.propertyName, Reference = keys[0].value, IsReference = true });
            }
            return list;
        }

        private bool CanApply(Binding binding) => Applier(binding) != null;

        private void Apply(Binding binding) => Applier(binding)?.Invoke();

        // How a value is written into the avatar; null when it can't be (a material property). What it animates may be gone
        // (an object, a component, a blendshape a custom base's mesh no longer has): the animation did nothing, nor does this.
        private Action Applier(Binding binding)
        {
            Action nothing = () => { };
            var target = binding.Path.Length == 0 ? root.transform : root.transform.Find(binding.Path);
            if (target == null) return nothing;
            if (binding.Type == typeof(GameObject))
                return binding.Property == "m_IsActive" && !binding.IsReference ? () => target.gameObject.SetActive(binding.Value > .5f) : (Action)null;
            if (binding.Type == typeof(Animator)) return null;
            var component = target.GetComponent(binding.Type);
            if (component == null) return nothing;
            if (component is SkinnedMeshRenderer skinned && binding.Property.StartsWith("blendShape.", StringComparison.Ordinal))
            {
                int index = skinned.sharedMesh != null ? skinned.sharedMesh.GetBlendShapeIndex(binding.Property.Substring(11)) : -1;
                return index < 0 ? nothing : () => skinned.SetBlendShapeWeight(index, binding.Value);
            }
            if (component is Transform transform && !binding.IsReference) return TransformApplier(transform, binding);
            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(binding.Property);
            if (property == null) return null;
            if (binding.IsReference)
                return property.propertyType == SerializedPropertyType.ObjectReference
                    ? () => { property.objectReferenceValue = binding.Reference; serialized.ApplyModifiedPropertiesWithoutUndo(); } : (Action)null;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: return () => { property.floatValue = binding.Value; serialized.ApplyModifiedPropertiesWithoutUndo(); };
                case SerializedPropertyType.Integer: return () => { property.intValue = Mathf.RoundToInt(binding.Value); serialized.ApplyModifiedPropertiesWithoutUndo(); };
                case SerializedPropertyType.Boolean: return () => { property.boolValue = binding.Value > .5f; serialized.ApplyModifiedPropertiesWithoutUndo(); };
                case SerializedPropertyType.Enum: return () => { property.intValue = Mathf.RoundToInt(binding.Value); serialized.ApplyModifiedPropertiesWithoutUndo(); };
            }
            return null;
        }

        private static Action TransformApplier(Transform transform, Binding binding)
        {
            string p = binding.Property;
            int axis = p.EndsWith(".x") ? 0 : p.EndsWith(".y") ? 1 : p.EndsWith(".z") ? 2 : p.EndsWith(".w") ? 3 : -1;
            if (axis < 0) return null;
            if (p.StartsWith("m_LocalPosition.") && axis < 3) return () => { var v = transform.localPosition; v[axis] = binding.Value; transform.localPosition = v; };
            if (p.StartsWith("m_LocalScale.") && axis < 3) return () => { var v = transform.localScale; v[axis] = binding.Value; transform.localScale = v; };
            if (p.StartsWith("localEulerAnglesRaw.") && axis < 3) return () => { var v = transform.localEulerAngles; v[axis] = binding.Value; transform.localEulerAngles = v; };
            if (p.StartsWith("m_LocalRotation.")) return () => { var q = transform.localRotation; q[axis] = binding.Value; transform.localRotation = q; };
            return null;
        }

        // Removes layers, keeping layer controls (VRCAnimatorLayerControl) and synced layers pointing at the same layers.
        private void RemoveLayers(VRCAvatarDescriptor.AnimLayerType type, AnimatorController controller, HashSet<int> gone)
        {
            var layers = controller.layers;
            var map = new Dictionary<int, int>();
            for (int i = 0, n = 0; i < layers.Length; i++) if (!gone.Contains(i)) map[i] = n++;
            var kept = layers.Where((_, i) => !gone.Contains(i)).ToArray();
            foreach (var layer in kept) if (layer.syncedLayerIndex >= 0 && map.TryGetValue(layer.syncedLayerIndex, out int synced)) layer.syncedLayerIndex = synced;
            controller.layers = kept;
            foreach (var (_, other) in controllers.Where(c => editable(c.Controller)))
                foreach (var machine in Machines(other))
                    foreach (var child in machine.states)
                    {
                        var behaviours = child.state.behaviours;
                        var keep = new List<StateMachineBehaviour>();
                        foreach (var behaviour in behaviours)
                        {
                            if (behaviour is VRCAnimatorLayerControl control && Same(type, control.playable))
                            {
                                if (!map.TryGetValue(control.layer, out int to)) { removed.Add(control); continue; }
                                control.layer = to;
                            }
                            keep.Add(behaviour);
                        }
                        if (keep.Count != behaviours.Length) child.state.behaviours = keep.ToArray();
                    }
        }

        private bool Controlled(VRCAvatarDescriptor.AnimLayerType type, int index) =>
            controllers.SelectMany(c => Machines(c.Controller)).SelectMany(m => m.states).SelectMany(s => s.state.behaviours)
                .OfType<VRCAnimatorLayerControl>().Any(c => Same(type, c.playable) && c.layer == index);

        private static bool Same(VRCAvatarDescriptor.AnimLayerType type, VRC_AnimatorLayerControl.BlendableLayer layer) =>
            type.ToString() == layer.ToString() || (type == VRCAvatarDescriptor.AnimLayerType.Gesture && layer == VRC_AnimatorLayerControl.BlendableLayer.Gesture);

        // ---- Objects that can never show ----

        private void DeleteHidden()
        {
            // Objects some animation can still show.
            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, controller) in controllers)
                foreach (var clip in Clips(controller))
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive" && AnimationUtility.GetEditorCurve(clip, binding).keys.Any(k => k.value > .5f))
                            shown.Add(binding.path);
            var hidden = new List<Transform>();
            void Walk(Transform t)
            {
                foreach (Transform child in t)
                {
                    string path = AnimationUtility.CalculateTransformPath(child, root.transform);
                    if (!child.gameObject.activeSelf && !shown.Contains(path)) hidden.Add(child);
                    else Walk(child);
                }
            }
            Walk(root.transform);
            if (hidden.Count == 0) return;
            // What the avatar's components refer to, and from where: an object something outside it uses stays.
            var references = new List<(Transform Target, Transform From)>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                // Editor-only components (My Avatar's own, markers) leave the upload: what they refer to isn't used by it.
                if (component == null || component is Transform || component is IEditorOnly) continue;
                var property = new SerializedObject(component).GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null)
                    {
                        var value = property.objectReferenceValue;
                        var target = value is GameObject go ? go.transform : value is Component c ? c.transform : null;
                        if (target != null && target.IsChildOf(root.transform)) references.Add((target, component.transform));
                    }
            }
            // Decided before anything goes.
            var gone = hidden.Where(t => !references.Any(r => r.Target.IsChildOf(t) && !r.From.IsChildOf(t))).ToList();
            foreach (var t in gone)
            {
                report.Objects++;
                Object.DestroyImmediate(t.gameObject);
            }
        }

        // ---- Animations nothing plays ----

        private void DeleteUnused()
        {
            if (removed.Count == 0) return;
            var used = new HashSet<Object>();
            foreach (var (_, controller) in controllers)
                foreach (var machine in Machines(controller))
                {
                    used.Add(machine);
                    used.UnionWith(machine.behaviours);
                    used.UnionWith(Transitions(machine));
                    foreach (var child in machine.states)
                    {
                        used.Add(child.state);
                        used.UnionWith(child.state.behaviours);
                        foreach (var motion in Tree(child.state.motion)) used.Add(motion);
                    }
                }
            foreach (var item in removed)
            {
                if (item == null || used.Contains(item) || !editable(item)) continue;
                if (item is AnimationClip) report.Deleted++;
                Object.DestroyImmediate(item, true);
            }
        }

        // ---- Walking controllers ----

        private static IEnumerable<AnimatorStateMachine> Machines(AnimatorController controller) =>
            controller.layers.Where(l => l.stateMachine != null).SelectMany(l => Machines(l.stateMachine));

        private static IEnumerable<AnimatorStateMachine> Machines(AnimatorStateMachine machine)
        {
            if (machine == null) yield break;
            yield return machine;
            foreach (var sub in machine.stateMachines)
                foreach (var m in Machines(sub.stateMachine)) yield return m;
        }

        private static IEnumerable<AnimatorTransitionBase> Transitions(AnimatorStateMachine machine)
        {
            foreach (var t in machine.anyStateTransitions) yield return t;
            foreach (var t in machine.entryTransitions) yield return t;
            foreach (var child in machine.states)
                foreach (var t in child.state.transitions) yield return t;
            foreach (var sub in machine.stateMachines)
                foreach (var t in machine.GetStateMachineTransitions(sub.stateMachine)) yield return t;
        }

        private static IEnumerable<Motion> Tree(Motion motion)
        {
            if (motion == null) yield break;
            yield return motion;
            if (motion is BlendTree tree)
                foreach (var child in tree.children)
                    foreach (var m in Tree(child.motion)) yield return m;
        }

        private static IEnumerable<Motion> Motions(AnimatorStateMachine machine) =>
            Machines(machine).SelectMany(m => m.states).SelectMany(s => Tree(s.state.motion));

        private static IEnumerable<AnimationClip> Clips(AnimatorController controller) =>
            Machines(controller).SelectMany(m => m.states).SelectMany(s => Tree(s.state.motion)).OfType<AnimationClip>().Distinct();

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
    }
}
