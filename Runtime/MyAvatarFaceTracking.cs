using System;
using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.MyAvatar
{
    /// <summary>How much the face tracking smooths what VRCFaceTracking sends, as the template's "Local Smoothing" starts.</summary>
    public enum FaceTrackingSmoothing { Responsive, Balanced, Smooth }

    /// <summary>The face tracking features others see: each one costs synced parameter bits.</summary>
    [Flags]
    public enum FaceTrackingFeatures
    {
        None = 0,
        Eyes = 1 << 0,
        Pupils = 1 << 1,
        Brows = 1 << 2,
        Mouth = 1 << 3,
        Tongue = 1 << 4,
        Cheeks = 1 << 5,
        All = Eyes | Pupils | Brows | Mouth | Tongue | Cheeks,
    }

    /// <summary>
    /// Adjerry91's face tracking template as My Avatar set it up: which blendshape standard it animates, the face mesh, and
    /// the quick settings applied to the upload copy (the template's own files never change). Editor only.
    /// </summary>
    [DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class MyAvatarFaceTracking : MonoBehaviour, IEditorOnly
    {
        public string standard;
        public SkinnedMeshRenderer face;
        [Tooltip("How much the face smooths VRCFaceTracking's values: the starting value of the template's Local Smoothing, and a slower mouth.")]
        public FaceTrackingSmoothing smoothing = FaceTrackingSmoothing.Balanced;
        [Tooltip("The features synced to other players. Features left out are removed from the uploaded avatar's parameters.")]
        public FaceTrackingFeatures synced = FaceTrackingFeatures.All;
        [Tooltip("The mouth tuned like the Ultirex's hand-made face tracking: smiles full at 80 %, the face's own grin and shape weights.")]
        public bool expressiveMouth = true;
    }
}
