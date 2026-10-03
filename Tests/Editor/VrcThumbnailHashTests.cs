using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class VrcThumbnailHashTests
    {
        [TestCase(1200, 900)]
        [TestCase(700, 1100)]
        [TestCase(800, 600)]
        public void HashMatchesActualSdkUploadCrop(int width, int height)
        {
            string sourcePath = Path.Combine(Path.GetTempPath(), "orbiters-thumbnail-test-" + Guid.NewGuid() + ".png");
            string croppedPath = null;
            var source = new Texture2D(width, height);
            var previous = RenderTexture.active;
            var retained = new RenderTexture(16, 16, 0);
            var texturesBefore = Resources.FindObjectsOfTypeAll<Texture2D>();
            try
            {
                var pixels = new Color32[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        pixels[y * width + x] = new Color32((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256), (byte)(x % 256));
                source.SetPixels32(pixels); source.Apply();
                var bytes = source.EncodeToPNG(); File.WriteAllBytes(sourcePath, bytes);
                // Exercise the installed SDK implementation as the oracle; it has no public crop API.
                var sdkType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRC.SDKBase.VRC_EditorTools")).First(t => t != null);
                var crop = sdkType.GetMethod("CropImage", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(crop);
                croppedPath = (string)crop.Invoke(null, new object[] { sourcePath, 800f, 600f, true, false, false });
                string expected;
                using (var md5 = MD5.Create()) expected = Convert.ToBase64String(md5.ComputeHash(File.ReadAllBytes(croppedPath)));
                RenderTexture.active = retained;
                Assert.AreEqual(expected, VrcThumbnailHash.Compute(sourcePath));
                Assert.AreSame(retained, RenderTexture.active);
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(sourcePath));
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(retained);
                // The SDK crop leaks its two readable textures; this test owns and cleans them too.
                foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>().Except(texturesBefore))
                    if (texture && string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(texture))) UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(source);
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
                if (croppedPath != null && File.Exists(croppedPath)) File.Delete(croppedPath);
            }
        }
    }
}
