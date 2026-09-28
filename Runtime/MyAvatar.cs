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
        [HideInInspector] public string notice;
        [HideInInspector] public List<TextureEntry> textures = new List<TextureEntry>();
        [HideInInspector] public List<RendererSnapshot> undoMaterials = new List<RendererSnapshot>();
        [HideInInspector] public bool canRedo;
        [HideInInspector] public Texture2D thumbnail;
        [HideInInspector] public TextureOptimizationRecord optimization = new TextureOptimizationRecord();
        [HideInInspector] public string accessoryStatus, accessoryChoice;
        [HideInInspector] public bool accessoryWarning;
        [HideInInspector] public List<AccessoryNote> accessoryNotes = new List<AccessoryNote>();

        // One line under the accessory drop field: something done, or a manual step with the object to look at.
        [Serializable]
        public sealed class AccessoryNote
        {
            public UnityEngine.Object target, accessory;
            public string text, duplicate;
            public bool warning, modularAvatar, vrcFury;
        }
    }

    [Serializable]
    public sealed class TextureEntry
    {
        public string sourceKey;
        public Texture2D texture;
        public string fileName;
        public string role;
        public string reason;
        public string suggestedMaterialName;
        public string suggestedProperty;
        public Material material;
        public string property;
        public bool applied;
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
