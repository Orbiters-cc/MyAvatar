using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>
    /// The template's mouth tuned like the Ultirex's hand-made face tracking (OSCmooth), measured on the same face:
    /// <list type="bullet">
    /// <item>smiles and frowns are full at 0.8 instead of 1: VRCFaceTracking seldom reports more;</item>
    /// <item>smiles, frowns, the lower lip, jaw and tongue keep 70 % of the last frame like the original, not 30 %: the
    /// template's single smoothing weight suits the eyes, not a mouth that flickers between expressions;</item>
    /// <item>on a face sculpted with its own grin (Rexouium, Ultirex), a smile with the upper lip raised becomes that grin,
    /// which also lifts the cheeks and lower lids; its other shapes take the original's weights. Any other face grins with
    /// its own cheek and eye squints.</item>
    /// </list>
    /// The slower mouth follows the smoothing setting; the rest is the "expressive mouth" setting.
    /// </summary>
    internal static partial class FaceTrackingBuild
    {
        internal const float FullSmile = .8f;
        // The template's per-frame weight of the new value (about 0.71 at 90 fps) over this: 0.3, as in the original.
        internal const float SlowSmoothing = 2.35f;
        // The slower mouth trees made here end with this.
        private const string SlowerMouth = " (slower mouth)";
        internal const string LocalSmoothing = "OSCm/Local/FloatSmoothing";
        private static readonly string[] Slow = { "SmileSadLeft", "SmileSadRight", "MouthLowerDown", "JawX", "JawForward", "TongueX", "TongueY" };

        // The original's weights for Rexouium's lip tracking shapes (sculpted stronger than SRanipal's), and shapes it moves
        // along: a pout narrows the mouth, wide eyes raise the brows (half per eye), the lids follow the eyes.
        private static readonly Dictionary<string, (float scale, string with, float factor)> Rules = new Dictionary<string, (float, string, float)>(StringComparer.Ordinal)
        {
            ["LipTrack_LipCornerLowDown_L"] = (.2f, null, 0f), ["LipTrack_LipCornerLowDown_R"] = (.2f, null, 0f),
            ["LipTrack_CheekPuff_L"] = (.714f, null, 0f), ["LipTrack_CheekPuff_R"] = (.714f, null, 0f),
            ["LipTrack_LowerLipLeft"] = (.426f, null, 0f), ["LipTrack_LowerLipRight"] = (.426f, null, 0f),
            ["LipTrack_UpperLipLeft"] = (.105f, null, 0f), ["LipTrack_UpperLipRight"] = (.105f, null, 0f),
            ["LipTrack_Pout_Pucker"] = (.2f, "MouthNarrow", .6f),
            ["EyesWide_L"] = (1f, "BrowUp", .138f), ["EyesWide_R"] = (1f, "BrowUp", .138f),
            ["Eye_LookUp_L"] = (1f, "LookUp_L", 1f), ["Eye_LookUp_R"] = (1f, "LookUp_R", 1f),
            ["Eye_LookDown_L"] = (1f, "LookDown_L", 1f), ["Eye_LookDown_R"] = (1f, "LookDown_R", 1f),
        };

        internal sealed class Tuning
        {
            public int Grins, Gains, Slowed, Shapes;
            public float Slowdown;
            public bool Expressive;
            public override string ToString() => (Expressive
                ? $"Tuned like the Ultirex: {Gains} smile/frown ranges end at {FullSmile}, {Grins} grins, {Shapes} weighted shape curves; "
                : "The template's own mouth (expressive mouth off); ") + $"{Slowed} mouth inputs follow {Slowdown:0.##}x slower.";
        }

        /// <summary>
        /// Tunes the template's layers in <paramref name="fx"/> (VRCFury's copy), after its shapes were renamed: the mouth
        /// <paramref name="slowdown"/> times slower than the eyes, and the Ultirex's mouth when <paramref name="expressive"/>.
        /// </summary>
        internal static Tuning Tune(AnimatorController fx, string facePath, Mesh face, bool expressive = true, float slowdown = SlowSmoothing)
        {
            var result = new Tuning { Expressive = expressive, Slowdown = slowdown };
            var mesh = new HashSet<string>(FaceTrackingDetection.Shapes(face), StringComparer.Ordinal);
            var roots = TrackingTrees(fx);
            var trees = roots.SelectMany(Trees).Distinct().Where(t => Owned(t, fx)).ToList();
            if (expressive)
            {
                foreach (var side in new[] { "Left", "Right" }) result.Grins += Grin(fx, trees, facePath, mesh, side);
                trees = roots.SelectMany(Trees).Distinct().Where(t => Owned(t, fx)).ToList();
                result.Gains = Gain(trees);
            }
            result.Slowed = SlowDown(fx, trees, slowdown);
            if (expressive) result.Shapes = Weigh(fx, trees, facePath, mesh);
            return result;
        }

        private static List<BlendTree> TrackingTrees(AnimatorController fx) =>
            fx.layers.Where(l => l.stateMachine != null && IsTracking(l.stateMachine)).SelectMany(l => States(l.stateMachine))
                .Select(s => s.motion).OfType<BlendTree>().Distinct().ToList();

        /// <summary>
        /// Changes how much slower the mouth follows in an FX copy tuned before (the face tracking test switching smoothing
        /// without building again). Returns the slower trees changed.
        /// </summary>
        internal static int SetMouthSlowdown(AnimatorController fx, float slowdown)
        {
            int changed = 0;
            foreach (var tree in TrackingTrees(fx).SelectMany(Trees).Distinct().Where(t => t.name.EndsWith(SlowerMouth, StringComparison.Ordinal) && t.children.Length == 2))
            {
                var children = tree.children;
                children[1].threshold = slowdown;
                tree.children = children;
                changed++;
            }
            return changed;
        }

        private static IEnumerable<BlendTree> Trees(BlendTree tree)
        {
            yield return tree;
            foreach (var child in tree.children)
                if (child.motion is BlendTree nested)
                    foreach (var t in Trees(nested)) yield return t;
        }

        // ---- Smile and frown range ----

        private static int Gain(IEnumerable<BlendTree> trees)
        {
            int gains = 0;
            foreach (var tree in trees.Where(t => t.blendType == BlendTreeType.Simple1D && t.blendParameter != null && t.blendParameter.StartsWith("OSCm/Proxy/", StringComparison.Ordinal) &&
                                                  (t.blendParameter.EndsWith("FT/v2/SmileSadLeft", StringComparison.Ordinal) || t.blendParameter.EndsWith("FT/v2/SmileSadRight", StringComparison.Ordinal))))
            {
                var children = tree.children;
                var thresholds = children.Select(c => c.threshold).OrderBy(x => x).ToArray();
                // Only the expression's own range: the smoothing trees on these values span -1 to 1.
                float edge = thresholds.SequenceEqual(new[] { 0f, 1f }) ? 1f : thresholds.SequenceEqual(new[] { -1f, 0f }) ? -1f : 0f;
                if (edge == 0f) continue;
                for (int i = 0; i < children.Length; i++) if (children[i].threshold == edge) children[i].threshold = edge * FullSmile;
                tree.useAutomaticThresholds = false;
                tree.children = children;
                EditorUtility.SetDirty(tree);
                gains++;
            }
            return gains;
        }

        // ---- Grin ----

        // The original's smile: no smile and the lip raised lifts the corners half; a full smile is the lip tracking smile,
        // and raising the lip as well turns it into the face's grin (with 61.4 of the smile, as the grin also smiles).
        // A face without its own grin gets one from its cheek and eye squints, which is what a sculpted grin adds: the full
        // smile with the lip raised lifts the cheeks and the lower lids.
        private static int Grin(AnimatorController fx, List<BlendTree> trees, string facePath, HashSet<string> mesh, string side)
        {
            string grin = "Grin_" + side[0];
            bool sculpted = mesh.Contains(grin);
            var index = FaceTrackingNames.Index(mesh);
            string cheek = sculpted ? null : FaceTrackingNames.Resolve("CheekSquint" + side, index);
            string squint = sculpted ? null : FaceTrackingNames.Resolve("EyeSquint" + side, index);
            if (!sculpted && cheek == null && squint == null) return 0;
            int grins = 0;
            foreach (var parent in trees.Where(t => t.blendType == BlendTreeType.Direct).ToList())
            {
                var children = parent.children.ToList();
                int smileAt = children.FindIndex(c => c.motion is BlendTree t && TemplateName(t) == "Mouth Smile " + side + " Blend");
                int upAt = children.FindIndex(c => c.motion is BlendTree t && TemplateName(t) == "Mouth Upper Up " + side + " Blend");
                if (smileAt < 0 || upAt < 0) continue;
                var smileTree = (BlendTree)children[smileAt].motion;
                var upTree = (BlendTree)children[upAt].motion;
                string smile = TopShape(smileTree, facePath), up = TopShape(upTree, facePath);
                if (smile == null || up == null || !mesh.Contains(smile) || !mesh.Contains(up)) continue;
                AnimationClip Pose(string name, float smiling, float raised, float grinning) => Keep(fx, sculpted
                    ? Clip(name, facePath, (smile, smiling), (up, raised), (grin, grinning))
                    : Clip(name, facePath, new[] { (smile, smiling), (up, raised), (cheek, grinning * .5f), (squint, grinning * .2f) }.Where(s => s.Item1 != null).ToArray()));
                var rest = Pose("Grin " + side + " rest", 0, 0, 0);
                var lipUp = Pose("Grin " + side + " lip up", 0, 50, 0);
                var smiled = Pose("Grin " + side + " smile", 100, 0, 0);
                // The sculpted grin smiles itself (61.4 of the smile, as on the Ultirex); a made one keeps the smile and the lip.
                var grinned = sculpted ? Pose("Grin " + side, 61.4f, 0, 100) : Pose("Grin " + side, 100, 60, 100);
                var neutral = Keep(fx, Tree("Grin " + side + " no smile", upTree.blendParameter, (0f, rest), (1f, lipUp)));
                var full = Keep(fx, Tree("Grin " + side + " full smile", upTree.blendParameter, (0f, smiled), (1f, grinned)));
                var replaced = children[smileAt];
                replaced.motion = Keep(fx, Tree("Mouth Smile " + side + " Grin", smileTree.blendParameter, (0f, neutral), (FullSmile, full)));                children[smileAt] = replaced;
                children.RemoveAt(upAt);
                parent.children = children.ToArray();
                EditorUtility.SetDirty(parent);
                grins++;
            }
            return grins;
        }

        // VRCFury names its copies "Copied from <controller>/<name>".
        private static string TemplateName(Object value) => value.name.Substring(value.name.LastIndexOf('/') + 1);

        // The face shape a 1D tree's top clip raises most.
        private static string TopShape(BlendTree tree, string facePath)
        {
            var top = tree.children.OrderByDescending(c => c.threshold).Select(c => c.motion).FirstOrDefault();
            while (top is BlendTree nested) top = nested.children.OrderByDescending(c => c.threshold).Select(c => c.motion).FirstOrDefault();
            if (!(top is AnimationClip clip)) return null;
            return AnimationUtility.GetCurveBindings(clip)
                .Where(b => b.path == facePath && b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                .OrderByDescending(b => AnimationUtility.GetEditorCurve(clip, b).Evaluate(0)).Select(b => b.propertyName.Substring(11)).FirstOrDefault();
        }

        private static AnimationClip Clip(string name, string path, params (string shape, float value)[] shapes)
        {
            var clip = new AnimationClip { name = name };
            foreach (var (shape, value) in shapes)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), "blendShape." + shape), AnimationCurve.Constant(0, 1, value));
            return clip;
        }

        private static BlendTree Tree(string name, string parameter, params (float threshold, Motion motion)[] children) => new BlendTree
        {
            name = name, blendType = BlendTreeType.Simple1D, blendParameter = parameter, useAutomaticThresholds = false,
            children = children.Select(c => new ChildMotion { motion = c.motion, threshold = c.threshold, timeScale = 1 }).ToArray(),
        };

        // Objects made for this build live in the FX copy's asset: the SDK saves the avatar as a prefab.
        private static T Keep<T>(AnimatorController fx, T value) where T : Object
        {
            value.hideFlags = HideFlags.HideInHierarchy;
            if (AssetDatabase.Contains(fx)) AssetDatabase.AddObjectToAsset(value, fx);
            return value;
        }

        // ---- Smoothing ----

        // A sibling of the template's local smoothing tree on the same weight, with the inputs at the slowdown (SlowSmoothing
        // by default) instead of 1: each frame takes a third of the new value it would.
        private static int SlowDown(AnimatorController fx, List<BlendTree> trees, float slowdown)
        {
            int slowed = 0;
            foreach (var parent in trees.Where(t => t.blendType == BlendTreeType.Direct).ToList())
            {
                var children = parent.children.ToList();
                foreach (var entry in children.ToList())
                {
                    if (!(entry.motion is BlendTree smoothing) || smoothing.blendType != BlendTreeType.Simple1D || smoothing.blendParameter != LocalSmoothing || smoothing.children.Length != 2) continue;
                    var ordered = smoothing.children.OrderBy(c => c.threshold).ToArray();
                    // The template's tree has its inputs at 1; a slower one made before is left as it is.
                    if (ordered[1].threshold != 1f || !(ordered[0].motion is BlendTree drivers) || !(ordered[1].motion is BlendTree inputs) || !Owned(drivers, fx) || !Owned(inputs, fx)) continue;
                    var slowDrivers = drivers.children.Where(c => Slow.Any(s => Driven(c) == "OSCm/Smooth/FT/v2/" + s)).ToArray();
                    var slowInputs = inputs.children.Where(c => Slow.Any(s => Driven(c) == "FT/v2/" + s)).ToArray();
                    if (slowDrivers.Length == 0 || slowDrivers.Length != slowInputs.Length) continue;
                    drivers.children = drivers.children.Except(slowDrivers).ToArray();
                    inputs.children = inputs.children.Except(slowInputs).ToArray();
                    EditorUtility.SetDirty(drivers); EditorUtility.SetDirty(inputs);
                    var slow = Keep(fx, Tree(smoothing.name + SlowerMouth, LocalSmoothing,
                        (0f, Keep(fx, new BlendTree { name = drivers.name, blendType = BlendTreeType.Direct, children = slowDrivers })),
                        (slowdown, Keep(fx, new BlendTree { name = inputs.name, blendType = BlendTreeType.Direct, children = slowInputs }))));
                    children.Add(new ChildMotion { motion = slow, directBlendParameter = entry.directBlendParameter, timeScale = 1 });
                    slowed += slowDrivers.Length;
                }
                if (children.Count != parent.children.Length) { parent.children = children.ToArray(); EditorUtility.SetDirty(parent); }
            }
            return slowed;
        }

        // The value a smoothing entry drives: its tree's parameter, or the direct weight of a clip.
        private static string Driven(ChildMotion child) => child.motion is BlendTree tree ? tree.blendParameter : child.directBlendParameter;

        // ---- Shape weights ----

        private static int Weigh(AnimatorController fx, List<BlendTree> trees, string facePath, HashSet<string> mesh)
        {
            int weighed = 0;
            foreach (var clip in trees.SelectMany(t => t.children).Select(c => c.motion).OfType<AnimationClip>().Distinct().Where(c => Owned(c, fx)))
            {
                var bindings = AnimationUtility.GetCurveBindings(clip);
                var animated = new HashSet<string>(bindings.Where(b => b.path == facePath && b.type == typeof(SkinnedMeshRenderer)).Select(b => b.propertyName));
                bool changed = false;
                foreach (var binding in bindings)
                {
                    if (binding.path != facePath || binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                    if (!Rules.TryGetValue(binding.propertyName.Substring(11), out var rule)) continue;
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (rule.with != null && mesh.Contains(rule.with) && animated.Add("blendShape." + rule.with))
                        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(facePath, typeof(SkinnedMeshRenderer), "blendShape." + rule.with), Scaled(curve, rule.factor));
                    if (rule.scale != 1f) AnimationUtility.SetEditorCurve(clip, binding, Scaled(curve, rule.scale));
                    changed = true;
                    weighed++;
                }
                if (changed) EditorUtility.SetDirty(clip);
            }
            return weighed;
        }

        private static AnimationCurve Scaled(AnimationCurve curve, float factor) =>
            new AnimationCurve(curve.keys.Select(k => new Keyframe(k.time, k.value * factor, k.inTangent * factor, k.outTangent * factor, k.inWeight, k.outWeight) { weightedMode = k.weightedMode }).ToArray())
            { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode };
    }
}
