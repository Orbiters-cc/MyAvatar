using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>A face tracking feature others can see, and the one it needs to move at all.</summary>
    internal sealed class FaceTrackingFeature
    {
        public readonly FaceTrackingFeatures Flag, Parent;
        public readonly string Label, Detail;

        private FaceTrackingFeature(FaceTrackingFeatures flag, FaceTrackingFeatures parent, string label, string detail)
        { Flag = flag; Parent = parent; Label = label; Detail = detail; }

        // Pupils and brows are weighted by EyeTrackingActive in the templates, tongue and cheeks by LipTrackingActive.
        public static readonly FaceTrackingFeature[] All =
        {
            new FaceTrackingFeature(FaceTrackingFeatures.Eyes, FaceTrackingFeatures.None, "Eyes", "Where you look, blinks and squints"),
            new FaceTrackingFeature(FaceTrackingFeatures.Pupils, FaceTrackingFeatures.Eyes, "Pupils", "Dilation, for eye trackers that measure it (iPhones don't)"),
            new FaceTrackingFeature(FaceTrackingFeatures.Brows, FaceTrackingFeatures.Eyes, "Brows", "Raised and frowning brows"),
            new FaceTrackingFeature(FaceTrackingFeatures.Mouth, FaceTrackingFeatures.None, "Mouth", "Jaw, lips, smiles and frowns"),
            new FaceTrackingFeature(FaceTrackingFeatures.Tongue, FaceTrackingFeatures.Mouth, "Tongue", "Out, side to side, up and down"),
            new FaceTrackingFeature(FaceTrackingFeatures.Cheeks, FaceTrackingFeatures.Mouth, "Cheeks", "Puffed and sucked in"),
        };

        /// <summary>Quick choices, from the lightest.</summary>
        public static readonly (string label, FaceTrackingFeatures features)[] Presets =
        {
            ("Eyes", FaceTrackingFeatures.Eyes | FaceTrackingFeatures.Brows),
            ("Eyes + mouth", FaceTrackingFeatures.Eyes | FaceTrackingFeatures.Brows | FaceTrackingFeatures.Mouth),
            ("Everything", FaceTrackingFeatures.All),
        };
    }

    /// <summary>
    /// The template's synced parameters by feature: what each costs, and what a build leaves out. VRCFaceTracking only
    /// drives the parameters it finds on the avatar, so a feature whose parameters are gone simply stays at rest and
    /// costs nothing.
    /// </summary>
    internal static class FaceTrackingFeatureSet
    {
        internal const string EyeTrackingActive = "EyeTrackingActive", LipTrackingActive = "LipTrackingActive", EyeDilationEnable = "EyeDilationEnable";

        /// <summary>The feature a template parameter belongs to ("FT/v2/JawX4" is the mouth's); None for its state and settings.</summary>
        internal static FaceTrackingFeatures FeatureOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return FaceTrackingFeatures.None;
            if (name == EyeTrackingActive) return FaceTrackingFeatures.Eyes;
            if (name == LipTrackingActive) return FaceTrackingFeatures.Mouth;
            if (name == EyeDilationEnable) return FaceTrackingFeatures.Pupils;
            int at = name.IndexOf("v2/", StringComparison.Ordinal);
            if (at < 0) return FaceTrackingFeatures.None;
            string v2 = name.Substring(at + 3);
            if (v2.StartsWith("Pupil", StringComparison.Ordinal) || v2.StartsWith("EyesDilation", StringComparison.Ordinal)) return FaceTrackingFeatures.Pupils;
            if (v2.StartsWith("Brow", StringComparison.Ordinal)) return FaceTrackingFeatures.Brows;
            if (v2.StartsWith("Eye", StringComparison.Ordinal)) return FaceTrackingFeatures.Eyes;
            if (v2.StartsWith("Tongue", StringComparison.Ordinal)) return FaceTrackingFeatures.Tongue;
            if (v2.StartsWith("Cheek", StringComparison.Ordinal)) return FaceTrackingFeatures.Cheeks;
            return FaceTrackingFeatures.Mouth;
        }

        /// <summary>The chosen features that can move: pupils and brows need the eyes, tongue and cheeks the mouth.</summary>
        internal static FaceTrackingFeatures Effective(FaceTrackingFeatures chosen)
        {
            foreach (var feature in FaceTrackingFeature.All)
                if (feature.Parent != FaceTrackingFeatures.None && (chosen & feature.Parent) == 0) chosen &= ~feature.Flag;
            return chosen;
        }

        internal static bool Kept(string parameter, FaceTrackingFeatures synced)
        {
            var feature = FeatureOf(parameter);
            return feature == FaceTrackingFeatures.None || (Effective(synced) & feature) != 0;
        }

        /// <summary>What the template on the avatar costs: the bits of its state and settings, then of each feature it has.</summary>
        internal sealed class Costs
        {
            public int Base;
            public readonly Dictionary<FaceTrackingFeatures, int> Features = new Dictionary<FaceTrackingFeatures, int>();
            public int Total(FaceTrackingFeatures synced) =>
                Base + Features.Where(f => (Effective(synced) & f.Key) != 0).Sum(f => f.Value);
            public int All => Base + Features.Values.Sum();
            public bool Has(FaceTrackingFeatures feature) => Features.ContainsKey(feature);
        }

        internal static Costs CostsOf(GameObject template)
        {
            var costs = new Costs();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in Parameters(template))
            {
                if (!parameter.networkSynced || !seen.Add(parameter.name)) continue;
                int bits = VRCExpressionParameters.TypeCost(parameter.valueType);
                var feature = FeatureOf(parameter.name);
                if (feature == FaceTrackingFeatures.None) costs.Base += bits;
                else costs.Features[feature] = (costs.Features.TryGetValue(feature, out int sum) ? sum : 0) + bits;
            }
            return costs;
        }

        /// <summary>The template's parameters a build with <paramref name="synced"/> leaves out.</summary>
        internal static HashSet<string> Removed(IEnumerable<string> parameters, FaceTrackingFeatures synced) =>
            new HashSet<string>(parameters.Where(p => !Kept(p, synced)), StringComparer.Ordinal);

        /// <summary>The parameters of the template's VRCFury Full Controllers (its face and eye rotation controllers).</summary>
        internal static IEnumerable<VRCExpressionParameters.Parameter> Parameters(GameObject template) =>
            Assets(template).SelectMany(a => a.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>()).Where(p => p != null && !string.IsNullOrEmpty(p.name));

        private static IEnumerable<VRCExpressionParameters> Assets(GameObject template)
        {
            if (template == null) yield break;
            foreach (var (_, feature, kind) in VrcFury.Features(template))
            {
                if (kind != "FullController" || !(VrcFury.Field(feature, "prms") is IEnumerable entries)) continue;
                foreach (var entry in entries)
                    if (VrcFury.ObjectReference(entry?.GetType().GetField("parameters")?.GetValue(entry)) is VRCExpressionParameters asset) yield return asset;
            }
        }

        // Every parameter budget (My Avatar's, the gallery's, the Toolkit's) counts the template as it will upload.
        [InitializeOnLoadMethod]
        private static void CountAsBuilt()
        {
            AvatarParameterBudget.BuildRemovedParameters.Add(component =>
            {
                var marker = component != null ? component.GetComponentInParent<MyAvatarFaceTracking>(true) : null;
                if (marker == null || marker.synced == FaceTrackingFeatures.All) return null;
                return Removed(Parameters(component.gameObject).Select(p => p.name), marker.synced);
            });
        }
    }
}
