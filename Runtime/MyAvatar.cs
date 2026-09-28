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
}
