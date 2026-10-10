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
        [HideInInspector] public TextureOptimizationRecord optimization = new TextureOptimizationRecord();
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
        /// <summary>What it was when it was deleted, to show it among the deleted items: its control type, icon and parameter.</summary>
        public int type;
        public Texture2D icon;
        public string parameter;

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

    [Serializable]
    public sealed class RendererSnapshot
    {
        public Renderer renderer;
        public Material[] before;
        public Material[] after;
    }

    // What Quick optimization changed, so it can be undone after a restart. Entries stay after an undo (applied = false):
    // Unity's Undo restores this record, and the editor brings the importers back in line with it.
    [Serializable]
    public sealed class TextureOptimizationRecord
    {
        public string folder;
        /// <summary>The texture drop this optimization followed: a newer drop offers optimizing again.</summary>
        public string batch;
        public long bytesBefore, bytesAfter;
        public List<OptimizedTexture> textures = new List<OptimizedTexture>();
        public List<Texture2D> duplicates = new List<Texture2D>();
        public List<MaterialSwap> swaps = new List<MaterialSwap>();
    }

    [Serializable]
    public sealed class OptimizedTexture
    {
        public string guid;
        public bool applied;
        public ImporterState before, after;
    }

    // The PC (Standalone) platform settings and importer flags Quick optimization writes; enums are stored as ints.
    [Serializable]
    public struct ImporterState
    {
        public bool overridden, crunched, mipmaps, streaming;
        public int maxSize, format, quality, compression;
    }

    [Serializable]
    public sealed class MaterialSwap
    {
        public Renderer renderer;
        public int index;
        public Material before, after;
    }
}
