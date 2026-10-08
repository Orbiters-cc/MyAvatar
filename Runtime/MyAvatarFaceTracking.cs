using System;
using UnityEngine;
using VRC.SDKBase;

namespace Orbiters.MyAvatar
{
    /// <summary>How much the face tracking smooths what VRCFaceTracking sends, as the template's "Local Smoothing" starts.</summary>
    public enum FaceTrackingSmoothing { Responsive, Balanced, Smooth }

    /// <summary>
    /// The face tracker the quick settings are for: a tracker keeps only the features it sends, Custom lets the creator
    /// choose; Everything is the template as it comes, until a tracker is chosen.
    /// </summary>
    public enum FaceTrackingPreset { Everything, MetaQuest, Vive, ARKit, Custom }

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
        Nose = 1 << 6,
        TongueDirections = 1 << 7,
        LipPress = 1 << 8,
        LipTighteners = 1 << 9,
        All = Eyes | Pupils | Brows | Mouth | Tongue | Cheeks | Nose | TongueDirections | LipPress | LipTighteners,
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
        [Tooltip("The face tracker the quick settings are for. Custom shows every setting.")]
        public FaceTrackingPreset preset = FaceTrackingPreset.Everything;
        [Tooltip("How much the face smooths VRCFaceTracking's values: the starting value of the template's Local Smoothing, and a slower mouth.")]
        public FaceTrackingSmoothing smoothing = FaceTrackingSmoothing.Balanced;
        [Tooltip("The features synced to other players. Features left out are removed from the uploaded avatar's parameters.")]
        public FaceTrackingFeatures synced = FaceTrackingFeatures.All;
        [Tooltip("Smiles and frowns full at 80 %, and a smile with the lip raised becomes a grin (the face's own, or one made from its cheek and eye squints).")]
        public bool expressiveMouth = true;
    }
}
