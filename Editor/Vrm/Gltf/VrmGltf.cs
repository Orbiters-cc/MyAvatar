using UniGLTF;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Vrm.Gltf
{
    /// <summary>The UniGLTF pieces both VRM writers use.</summary>
    public static class VrmGltf
    {
        /// <summary>
        /// UniVRM's editor serializer without its step that switches every exported texture asset to uncompressed and
        /// leaves it so: a texture whose file can't be copied as it is gets read from a copy, and the avatar's import
        /// settings never change.
        /// </summary>
        public static ITextureSerializer Textures() => new KeepImportSettings();

        /// <summary>Puts the humanoid in UniVRM's T-pose, as VRM wants its rest pose.</summary>
        public static void TPose(GameObject root) =>
            UniHumanoid.HumanPoseTransfer.SetTPose(root.GetComponent<Animator>().avatar, root.transform);

        private sealed class KeepImportSettings : ITextureSerializer
        {
            private readonly EditorTextureSerializer editor = new EditorTextureSerializer();

            public bool CanExportAsEditorAssetFile(Texture texture, UniGLTF.ColorSpace exportColorSpace) => editor.CanExportAsEditorAssetFile(texture, exportColorSpace);

            public (byte[] bytes, string mime) ExportBytesWithMime(Texture2D texture, UniGLTF.ColorSpace exportColorSpace) => editor.ExportBytesWithMime(texture, exportColorSpace);

            public void ModifyTextureAssetBeforeExporting(Texture texture) { }
        }
    }
}
