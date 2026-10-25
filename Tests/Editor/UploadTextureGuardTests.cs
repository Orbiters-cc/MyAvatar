using System;
using System.IO;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Optimization;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Textures the upload must keep as they are: pixel data (N4N0's screen layout, a point-filtered 160 x 23 table) is
    // never resized or compressed, and a copy whose size its format can't have leaves that texture alone instead of
    // stopping every texture's copy.
    public sealed class UploadTextureGuardTests
    {
        private string folder;

        [SetUp] public void SetUp()
        {
            folder = "Assets/MyAvatarTextureGuardTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        }

        [TearDown] public void TearDown() => AssetDatabase.DeleteAsset(folder);

        [Test] public void PointFilteredPixelDataIsNeverResizedOrCompressed()
        {
            var layout = Image("N4N0 screen layout", 160, 23, FilterMode.Point);
            Assert.False(TextureOptimization.Compressible(layout, out _), "a table read texel by texel");
            var photo = Image("Jacket_BaseColor", 256, 256, FilterMode.Bilinear);
            Assert.True(TextureOptimization.Compressible(photo, out _));
        }

        [Test] public void ACopyItsFormatCantHaveKeepsThatTextureAsItIs()
        {
            var layout = Image("Strip", 160, 22, FilterMode.Bilinear);
            var change = new TextureOptimization.TexturePlan
            {
                texture = layout, path = AssetDatabase.GetAssetPath(layout), format = TextureFormat.BC7, width = 80, height = 11, linear = true,
            };
            Assert.Null(UploadTextures.Copy(change), "80 x 11 isn't whole BC7 blocks: no copy, no exception");
        }

        private Texture2D Image(string name, int width, int height, FilterMode filter)
        {
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32((byte)(i % 251), (byte)(i / width % 253), 90, 255);
            image.SetPixels32(pixels); image.Apply();
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.filterMode = filter;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
