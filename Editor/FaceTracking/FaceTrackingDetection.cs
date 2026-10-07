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
        /// <summary>The template My Avatar set up on this avatar, if any.</summary>
        public MyAvatarFaceTracking Ours;
        /// <summary>Another face tracking setup found on the avatar (what it is), or null.</summary>
        public string Existing;
        public GameObject ExistingObject;

        // "Most of the blendshapes": three quarters of the standard's core shapes.
        public bool Ready => Standard != null && Needed > 0 && Found * 4 >= Needed * 3;
        public bool SetUp => Ours != null || Existing != null;
    }

    internal static class FaceTrackingDetection
    {
        internal const string PackageId = "adjerry91.vrcft.templates";
        internal const string PackageFolder = "Packages/" + PackageId;
        internal const string Repository = "https://github.com/Adjerry91/VRCFaceTracking-Templates";
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
            foreach (var standard in ours != null ? new[] { ours } : FaceTrackingStandard.All)
            {
                var missing = standard.Core.Where(name => FaceTrackingNames.Resolve(name, shapes) == null).ToList();
                int found = standard.Core.Length - missing.Count;
                if (report.Standard != null && found * report.Needed <= report.Found * standard.Core.Length) continue;
                report.Standard = standard; report.Found = found; report.Needed = standard.Core.Length; report.Missing = missing;
            }
            return report;
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
            found = null;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (ours != null && transform.IsChildOf(ours.transform)) continue;
                string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject);
                if (IsTemplateAsset(source) && PrefabUtility.GetNearestPrefabInstanceRoot(transform.gameObject) == transform.gameObject)
                { found = transform.gameObject; return "Adjerry91’s template “" + transform.name + "”"; }
            }
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null || (ours != null && component.transform.IsChildOf(ours.transform))) continue;
                string space = component.GetType().Namespace ?? "";
                if (!space.StartsWith("VF.", StringComparison.Ordinal) && !space.StartsWith("nadena.dev", StringComparison.Ordinal)) continue;
                var property = new SerializedObject(component).GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null &&
                        IsTemplateAsset(AssetDatabase.GetAssetPath(property.objectReferenceValue)))
                    { found = component.gameObject; return "a face tracking template on “" + component.name + "”"; }
            }
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
