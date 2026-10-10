using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>What the avatar has for face tracking: its face, the standard it fits best, and any setup already there.</summary>
    internal sealed class FaceTrackingReport
    {
        public SkinnedMeshRenderer Face;
        public FaceTrackingStandard Standard;
        public int Found, Needed;
        public List<string> Missing = new List<string>();
        /// <summary>The standard's core shapes by part: the eyes (with the brows) and the mouth (jaw, lips, cheeks, tongue).</summary>
        public FaceTrackingPart Eyes = new FaceTrackingPart("Eyes"), Mouth = new FaceTrackingPart("Mouth");
        /// <summary>The template My Avatar set up on this avatar, if any.</summary>
        public MyAvatarFaceTracking Ours;
        /// <summary>Another face tracking setup found on the avatar (what it is), or null.</summary>
        public string Existing;
        public GameObject ExistingObject;

        // Worth setting up when one part has most of its shapes: a face made for lip tracking only (or eyes only) still moves.
        public bool Ready => Standard != null && (Eyes.Ready || Mouth.Ready);
        public bool SetUp => Ours != null || Existing != null;
        /// <summary>The features this face can show: a part without a single shape of the standard is left out.</summary>
        public FaceTrackingFeatures Trackable => FaceTrackingDetection.Features(Eyes.Found > 0, Mouth.Found > 0);
    }

    /// <summary>One part of the face a standard animates: how many of its core shapes the face has, and which are missing.</summary>
    internal sealed class FaceTrackingPart
    {
        public readonly string Name;
        public int Found, Needed;
        public readonly List<string> Missing = new List<string>();
        public FaceTrackingPart(string name) { Name = name; }

        // "Most of the blendshapes": three quarters of the part's core shapes.
        public bool Ready => Needed > 0 && Found * 4 >= Needed * 3;
    }

    internal static class FaceTrackingDetection
    {
        internal const string PackageId = "adjerry91.vrcft.templates";
        internal const string PackageFolder = "Packages/" + PackageId;
        internal const string Repository = "https://github.com/Adjerry91/VRCFaceTracking-Templates";
        /// <summary>How to install VRCFaceTracking, the desktop app that sends the tracker's data to VRChat.</summary>
        internal const string VrcftInstall = "https://docs.vrcft.io/docs/vrcft-software/vrcft";

        /// <summary>The installed templates' version, or null.</summary>
        internal static string TemplatesVersion => UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageFolder)?.version;
        internal const string Credit = "Face tracking blendshapes are animated by Adjerry91’s Face Tracking Templates";
        // VRCFaceTracking's parameters: "v2/JawOpen", "FT/v2/EyeLidLeft", and the tracking state flags.
        private static readonly Regex TrackingParameter = new Regex(@"(^|/)v2/|^(Eye|Lip|Expression)TrackingActive$", RegexOptions.CultureInvariant);

        internal static bool TemplatesInstalled => AssetDatabase.IsValidFolder(PackageFolder);

        public static FaceTrackingReport Inspect(Transform root)
        {
            var report = new FaceTrackingReport();
            if (root == null) return report;
            report.Ours = root.GetComponentInChildren<MyAvatarFaceTracking>(true);
            report.Existing = ExistingSetup(root, report.Ours, out report.ExistingObject);
            report.Face = Face(root);
            if (report.Face == null) return report;
            var shapes = FaceTrackingNames.Index(Shapes(report.Face.sharedMesh));
            var ours = report.Ours != null ? FaceTrackingStandard.Find(report.Ours.standard) : null;
            // The standard whose usable parts (those with most of their shapes) cover most of it; else the best covered one.
            // Ties go to the first: Unified Expressions is what VRCFaceTracking speaks natively.
            (int usable, int found)? best = null;
            foreach (var standard in ours != null ? new[] { ours } : FaceTrackingStandard.All)
            {
                var (eyes, mouth) = Parts(standard, shapes);
                int found = eyes.Found + mouth.Found, usable = (eyes.Ready ? eyes.Found : 0) + (mouth.Ready ? mouth.Found : 0);
                if (best != null && !Better(usable, found, standard.Core.Length, best.Value.usable, best.Value.found, report.Needed)) continue;
                best = (usable, found);
                report.Standard = standard; report.Found = found; report.Needed = standard.Core.Length;
                report.Missing = eyes.Missing.Concat(mouth.Missing).ToList();
                report.Eyes = eyes; report.Mouth = mouth;
            }
            return report;
        }

        // a/aTotal against b/bTotal: more of the standard usable first, then more of it found.
        private static bool Better(int aUsable, int aFound, int aTotal, int bUsable, int bFound, int bTotal)
        {
            long usable = (long)aUsable * bTotal - (long)bUsable * aTotal;
            return usable != 0 ? usable > 0 : (long)aFound * bTotal > (long)bFound * aTotal;
        }

        /// <summary>The eye and brow shapes of a standard ("EyeClosedLeft", "browDownLeft", "Eye_Left_Blink"); the rest is the mouth's.</summary>
        internal static bool IsEyes(string coreShape) =>
            coreShape.StartsWith("eye", StringComparison.OrdinalIgnoreCase) || coreShape.StartsWith("brow", StringComparison.OrdinalIgnoreCase);

        /// <summary>How much of <paramref name="standard"/>'s eyes and mouth the face with <paramref name="shapes"/> (<see cref="FaceTrackingNames.Index"/>) has.</summary>
        internal static (FaceTrackingPart eyes, FaceTrackingPart mouth) Parts(FaceTrackingStandard standard, IReadOnlyDictionary<string, string> shapes)
        {
            var eyes = new FaceTrackingPart("Eyes"); var mouth = new FaceTrackingPart("Mouth");
            foreach (string name in standard.Core)
            {
                var part = IsEyes(name) ? eyes : mouth;
                part.Needed++;
                if (FaceTrackingNames.Resolve(name, shapes) != null) part.Found++; else part.Missing.Add(name);
            }
            return (eyes, mouth);
        }

        /// <summary>The features of the eyes (pupils and brows with them) and of the mouth (its parts, tongue and cheeks).</summary>
        internal static FaceTrackingFeatures Features(bool eyes, bool mouth) =>
            (eyes ? FaceTrackingFeatures.Eyes | FaceTrackingFeatures.Pupils | FaceTrackingFeatures.Brows : FaceTrackingFeatures.None) |
            (mouth ? FaceTrackingFeatures.All & ~(FaceTrackingFeatures.Eyes | FaceTrackingFeatures.Pupils | FaceTrackingFeatures.Brows) : FaceTrackingFeatures.None);

        /// <summary>
        /// The features <paramref name="face"/> can show with <paramref name="standard"/>'s template: a part without any of its
        /// shapes moves nothing, so its parameters are left out (they come back once the face has them). All when unknown.
        /// </summary>
        internal static FaceTrackingFeatures Trackable(FaceTrackingStandard standard, Mesh face)
        {
            if (standard == null || face == null) return FaceTrackingFeatures.All;
            var (eyes, mouth) = Parts(standard, FaceTrackingNames.Index(Shapes(face)));
            return Features(eyes.Found > 0, mouth.Found > 0);
        }

        /// <summary>The mesh with the face: the one lip sync uses, else "Body", else the one with the most blendshapes.</summary>
        internal static SkinnedMeshRenderer Face(Transform root)
        {
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            var viseme = descriptor != null ? descriptor.VisemeSkinnedMesh : null;
            if (viseme != null && viseme.sharedMesh != null && viseme.sharedMesh.blendShapeCount > 0) return viseme;
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0).ToList();
            return renderers.FirstOrDefault(r => r.name == "Body") ?? renderers.OrderByDescending(r => r.sharedMesh.blendShapeCount).FirstOrDefault();
        }

        internal static IEnumerable<string> Shapes(Mesh mesh)
        {
            for (int i = 0; mesh != null && i < mesh.blendShapeCount; i++) yield return mesh.GetBlendShapeName(i);
        }

        // Adjerry91's template (as a prefab, or referenced by a VRCFury or Modular Avatar component), or VRCFaceTracking
        // parameters already in the avatar's menu parameters or FX controller.
        private static string ExistingSetup(Transform root, MyAvatarFaceTracking ours, out GameObject found)
        {
            var skip = ours != null ? ours.transform : null;
            found = PackageUse.Prefab(root, IsTemplateAsset, skip);
            if (found != null) return "Adjerry91’s template “" + found.name + "”";
            var component = PackageUse.Component(root, IsTemplateAsset, skip);
            if (component != null) { found = component.gameObject; return "a face tracking template on “" + component.name + "”"; }
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return null;
            if (descriptor.expressionParameters != null && descriptor.expressionParameters.parameters != null &&
                descriptor.expressionParameters.parameters.Any(p => p != null && TrackingParameter.IsMatch(p.name ?? "")))
                return "VRCFaceTracking parameters in “" + descriptor.expressionParameters.name + "”";
            var fx = descriptor.baseAnimationLayers.FirstOrDefault(l => l.type == VRCAvatarDescriptor.AnimLayerType.FX).animatorController as AnimatorController;
            if (fx != null && fx.parameters.Any(p => TrackingParameter.IsMatch(p.name)))
                return "VRCFaceTracking parameters in the FX controller “" + fx.name + "”";
            return null;
        }

        private static bool IsTemplateAsset(string path) => !string.IsNullOrEmpty(path) &&
            (path.StartsWith(PackageFolder + "/", StringComparison.Ordinal) || path.StartsWith("Assets/VRCFaceTracking/", StringComparison.Ordinal));
    }
}
