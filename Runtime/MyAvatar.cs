using System;
using System.Collections.Generic;
using UnityEngine;

namespace Orbiters.MyAvatar
{
    [DisallowMultipleComponent, AddComponentMenu("Orbiters/My Avatar")]
    [HelpURL("https://orbiters.cc")]
    public sealed class MyAvatar : MonoBehaviour
#if UNITY_EDITOR
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [HideInInspector] public string batchFolder;
        /// <summary>The objects the current texture set is limited to (accessories a drop just added); empty for the whole avatar.</summary>
        [HideInInspector] public List<Transform> batchScope = new List<Transform>();
        [HideInInspector] public string notice;
        [HideInInspector] public List<TextureEntry> textures = new List<TextureEntry>();
        [HideInInspector] public List<RendererSnapshot> undoMaterials = new List<RendererSnapshot>();
        [HideInInspector] public bool canRedo;
        [HideInInspector] public Texture2D thumbnail;
        [HideInInspector] public string accessoryStatus, accessoryChoice;
        [HideInInspector] public bool accessoryWarning;
        [HideInInspector] public List<AccessoryNote> accessoryNotes = new List<AccessoryNote>();
        /// <summary>Accessories whose custom base question was answered "Not now", with the custom base asked about.</summary>
        [HideInInspector] public List<string> fitDismissed = new List<string>();
        /// <summary>
        /// Menu items deleted in the menu editor: left out of the upload with what only they drive (their parameters, the
        /// layers switching them, the objects and materials only those layers show).
        /// </summary>
        [HideInInspector] public List<MenuRemoval> menuRemovals = new List<MenuRemoval>();
        /// <summary>The avatar's own menu (its descriptor's, as its creator made it) is left out of the upload the same way.</summary>
        [HideInInspector] public bool removeDefaultMenu;
        /// <summary>
        /// Values the scene shows for a look chosen in My Avatar (a Novabeast's tail size) and what they were before: the upload
        /// gets the values its animations start from back, and the look comes from the menu's starting value.
        /// </summary>
        [HideInInspector] public List<LookRest> lookRest = new List<LookRest>();
        /// <summary>What the Optimization card does to the uploaded copy, and how close its textures stay sharp.</summary>
        [HideInInspector] public UploadOptimization uploadOptimization = new UploadOptimization();
        /// <summary>The Export to VRM section's choices: the VRM version and what the file says about the model.</summary>
        [HideInInspector] public VrmExportSettings vrmExport = new VrmExportSettings();

        // One line under the accessory drop field: something done, or a manual step with the object to look at.
        [Serializable]
        public sealed class AccessoryNote
        {
            public UnityEngine.Object target, accessory;
            public string text, duplicate;
            public bool warning, modularAvatar, vrcFury;
            /// <summary>The accessory's armature was fitted to the avatar's (made for another body): offers to cancel it.</summary>
            public bool armatureFit;
            /// <summary>
            /// The accessory against the avatar's custom base: "ask" (does it fit?), "shapes" (add the missing blendshapes),
            /// "refit" (made for the original base), "place" (being lined up with the original base), "install" (waiting for
            /// ReFit, then <see cref="fitNext"/>), "done" or "failed". Empty for other notes.
            /// </summary>
            public string fit, fitNext;
            /// <summary>The custom base's name, version and key when asked, and the blendshapes the accessory lacks.</summary>
            public string fitBase, fitVersion, fitKey;
            public int fitShapes;
            public List<string> fitShapeNames = new List<string>();
            /// <summary>The refit has spots a creator could fit better by hand.</summary>
            public bool fitRough;
        }
    }

    /// <summary>How much of the Optimization card runs when the avatar uploads.</summary>
    public enum OptimizationLevel { Off, Small, Big, Custom }

    /// <summary>The changes the uploaded copy can get; the scene avatar never changes.</summary>
    [Flags]
    public enum OptimizationPasses
    {
        None = 0,
        HiddenObjects = 1 << 0,
        DisabledComponents = 1 << 1,
        UnusedColliders = 1 << 2,
        StillPhysBones = 1 << 3,
        BlendShapes = 1 << 4,
        BoneReferences = 1 << 5,
        MaterialSlots = 1 << 6,
        UnusedTextures = 1 << 7,
        MergeMeshes = 1 << 8,
        IdlePhysBones = 1 << 9,
        EndBones = 1 << 10,
        /// <summary>A Quest (Android) upload gets its textures in ASTC, at half the PC size, 1024 px at most.</summary>
        QuestTextures = 1 << 11,
        BodyUnderClothing = 1 << 12,
        StaticBones = 1 << 13,
        /// <summary>With <see cref="BlendShapes"/>: shapes animations only ever set to the weight they have are baked too.</summary>
        ConstantBlendShapes = 1 << 14,
        /// <summary>The upload's colour and mask textures are crunched: a smaller download for the same memory, slightly lossy.</summary>
        CrunchTextures = 1 << 15,
        /// <summary>Metallic, roughness and smoothness maps upload at half the size the others get.</summary>
        HalfSurfaceMaps = 1 << 16,
        /// <summary>The menu's icons upload at 64 px at most.</summary>
        MenuIcons = 1 << 17,
        /// <summary>Objects and the parameters only animators use upload with the shortest names.</summary>
        ShortNames = 1 << 18,
        /// <summary>Meshes every animation shows and hides together become one.</summary>
        MergeToggledMeshes = 1 << 19,
        /// <summary>AlterMesh removes polygons that hardly change the shape.</summary>
        FewerPolygons = 1 << 20,
        /// <summary>The same image imported twice under two names uploads once.</summary>
        DuplicateTextures = 1 << 21,
        /// <summary>
        /// Each texture uploads at the size that keeps it sharp from the level's distance (never larger than it is), in the
        /// format with the best quality per byte for the platform.
        /// </summary>
        ResizeTextures = 1 << 22,
        /// <summary>
        /// Every skinned mesh that moves with the hips gets the same culling box, around the whole avatar with room for its
        /// limbs: no part vanishes at the edge of the view.
        /// </summary>
        Bounds = 1 << 23,
    }

    [Serializable]
    public sealed class UploadOptimization
    {
        /// <summary>
        /// What nothing shows or uses goes, polygons that change the surface by 4 mm at most go, and textures are sized to
        /// stay sharp from a meter away.
        /// </summary>
        public const OptimizationPasses Small = OptimizationPasses.HiddenObjects | OptimizationPasses.DisabledComponents |
            OptimizationPasses.IdlePhysBones | OptimizationPasses.UnusedColliders | OptimizationPasses.StillPhysBones |
            OptimizationPasses.BlendShapes | OptimizationPasses.ConstantBlendShapes | OptimizationPasses.BoneReferences | OptimizationPasses.MaterialSlots |
            OptimizationPasses.UnusedTextures | OptimizationPasses.DuplicateTextures | OptimizationPasses.FewerPolygons | OptimizationPasses.ResizeTextures |
            OptimizationPasses.Bounds;
        /// <summary>
        /// Small, and meshes that are always shown (props too) or toggled together become one, PhysBones end on an endpoint
        /// instead of a bone, the body under clothing that is always worn goes, polygons that change the surface by 8 mm at
        /// most go, names and menu icons shrink, and textures are sized to stay sharp from 3 m, crunched, metallic and
        /// roughness maps halved, ASTC for Quest.
        /// </summary>
        public const OptimizationPasses Big = Small | OptimizationPasses.MergeMeshes | OptimizationPasses.EndBones |
            OptimizationPasses.BodyUnderClothing | OptimizationPasses.QuestTextures | OptimizationPasses.StaticBones |
            OptimizationPasses.CrunchTextures | OptimizationPasses.HalfSurfaceMaps | OptimizationPasses.MenuIcons | OptimizationPasses.ShortNames |
            OptimizationPasses.MergeToggledMeshes;

        // What the levels set; Custom has its own sliders.
        public const float SmallSharpFrom = 1f, BigSharpFrom = 3f, SmallPolygonQuality = .25f, BigPolygonQuality = 0f;

        public OptimizationLevel level;
        /// <summary>What Custom runs; it starts as Small.</summary>
        public OptimizationPasses custom = Small;
        /// <summary>Custom's texture slider: from how close, in meters, textures stay sharp in game.</summary>
        public float sharpFrom = SmallSharpFrom;
        /// <summary>
        /// Custom's polygon slider: how close to the original the meshes stay. 1 lets no point move more than half a
        /// millimetre, each quarter lower doubles that (.25 is 4 mm, 0 is 8 mm).
        /// </summary>
        public float polygonQuality = SmallPolygonQuality;

        public OptimizationPasses Passes => level == OptimizationLevel.Small ? Small : level == OptimizationLevel.Big ? Big :
            level == OptimizationLevel.Custom ? custom : OptimizationPasses.None;

        /// <summary>From how close, in meters, the upload's textures stay sharp: 1 m for Small, 3 m for Big, the slider for Custom.</summary>
        public float SharpFrom => level == OptimizationLevel.Small ? SmallSharpFrom : level == OptimizationLevel.Big ? BigSharpFrom : sharpFrom;

        /// <summary>How close to the original the upload's meshes stay: 4 mm for Small, 8 mm for Big, the slider for Custom.</summary>
        public float PolygonQuality => level == OptimizationLevel.Small ? SmallPolygonQuality : level == OptimizationLevel.Big ? BigPolygonQuality : polygonQuality;
    }

    /// <summary>A property a look shown in the scene changed, and its value before (an animation's rest value).</summary>
    [Serializable]
    public sealed class LookRest
    {
        public string path, type, property;
        public float value;
    }

    /// <summary>A menu item left out of the upload, found where it is in the menu VRChat shows.</summary>
    [Serializable]
    public sealed class MenuRemoval
    {
        /// <summary>The names of the folders down to the item, and its own.</summary>
        public List<string> path = new List<string>();
        /// <summary>For each name of <see cref="path"/>: which item of that name it is in its menu (0 for the first).</summary>
        public List<int> nth = new List<int>();
        /// <summary>
        /// What it was when it was deleted: its control type, icon and parameter. Only an item of that type and parameter at
        /// its path is left out (a new item of the same name is not).
        /// </summary>
        public int type;
        public Texture2D icon;
        public string parameter;
        /// <summary>The component behind the item (a VRCFury Toggle, a Modular Avatar Menu Item…), null for a menu asset's control.</summary>
        public Component source;
        /// <summary>Whether a component was behind the item: once it is deleted, <see cref="source"/> reads null, and the item is gone.</summary>
        public bool fromComponent;

        public string Name => path.Count > 0 ? path[path.Count - 1] : "";
    }

    [Serializable]
    public sealed class TextureEntry
    {
        public string sourceKey;
        public Texture2D texture;
        // Generated channel-packed/inverted map; the source texture remains available for reassignment.
        public Texture2D appliedTexture, appliedTextureBeforeLast;
        public string fileName;
        public string role;
        public string reason;
        public string suggestedMaterialName;
        public string suggestedProperty;
        public Material material;
        public string property;
        public bool applied;
        // Set aside by the user from the "needs a slot" list (an unrelated image of the dropped folder, for example).
        public bool dismissed;
        public bool appliedBeforeLast;
        public Material materialBeforeLast;
        public string propertyBeforeLast;
        public string reasonBeforeLast;
        public float confidence;
    }

    /// <summary>
    /// What a VRM file says about the model (its meta) and which VRM version it is written in. Permissions start at the
    /// most restrictive: the model's own license decides what may be allowed.
    /// </summary>
    [Serializable]
    public sealed class VrmExportSettings
    {
        /// <summary>0 for VRM 0.x (VSeeFace and most apps), 1 for VRM 1.0.</summary>
        public int version;
        public string title, modelVersion = "1.0", authors, contact, reference;
        /// <summary>Who may use the model as an avatar: 0 only the author, 1 people the author allows, 2 everyone.</summary>
        public int allowedUsers;
        public bool violentUse, sexualUse, commercialUse, redistribution, modification;
        /// <summary>The exporter confirmed the model's license allows converting it to VRM.</summary>
        public bool licenseAllows;
    }

    [Serializable]
    public sealed class RendererSnapshot
    {
        public Renderer renderer;
        public Material[] before;
        public Material[] after;
    }
}
