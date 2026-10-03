using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal static class VrcThumbnailHash
    {
        // Match the SDK's 800x600 center-cropped PNG, not the original 1200x900 file.
        // Its CropImage helper is internal; use the same installed shader and encoding with owned resources.
        internal static string Compute(string path)
        {
            var shader = Resources.Load<Shader>("CropShader");
            if (!shader) throw new InvalidOperationException("VRChat thumbnail crop shader is unavailable.");
            Texture2D source = null, cropped = null;
            Material material = null;
            RenderTexture target = null;
            var previous = RenderTexture.active;
            try
            {
                source = new Texture2D(2, 2) { wrapMode = TextureWrapMode.Clamp };
                if (!source.LoadImage(File.ReadAllBytes(path))) throw new InvalidDataException("Cannot read thumbnail.");
                material = new Material(shader);
                material.SetFloat("_TargetWidth", 800);
                material.SetFloat("_TargetHeight", 600);
                material.SetInt("_CenterCrop", 1);
                target = RenderTexture.GetTemporary(800, 600);
                Graphics.Blit(source, target, material);
                RenderTexture.active = target;
                cropped = new Texture2D(800, 600);
                cropped.ReadPixels(new Rect(0, 0, 800, 600), 0, 0);
                cropped.Apply();
                using var md5 = MD5.Create();
                return Convert.ToBase64String(md5.ComputeHash(cropped.EncodeToPNG()));
            }
            finally
            {
                RenderTexture.active = previous;
                if (target) RenderTexture.ReleaseTemporary(target);
                if (source) UnityEngine.Object.DestroyImmediate(source);
                if (cropped) UnityEngine.Object.DestroyImmediate(cropped);
                if (material) UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
