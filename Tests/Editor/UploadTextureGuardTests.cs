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

        // The copies' mips keep a cut-out's coverage and a normal map's X moves to red as the per-texel passes did (kept here
        // as the reference): the same texels, read where the texture keeps them and counted once per mip.
        [Test] public void CoverageAndNormalSwizzleMatchThePerTexelPasses()
        {
            var random = new System.Random(7);
            Texture2D Make()
            {
                var image = new Texture2D(64, 64, TextureFormat.RGBA32, true, true);
                var pixels = new Color32[64 * 64];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(i % 64 < 20 ? 255 : random.Next(140)));
                image.SetPixels32(pixels, 0);
                return image;
            }
            var made = new System.Collections.Generic.List<Texture2D>();
            Texture2D Copy(Texture2D from) { var copy = Object.Instantiate(from); made.Add(copy); return copy; }
            try
            {
                var source = Make(); made.Add(source);
                // A cut-out (opaque columns, sparse alpha elsewhere): mips that lose coverage get their alpha scaled.
                var expected = Copy(source); expected.Apply(true); ReferencePreserveCoverage(expected, .5f);
                var actual = Copy(source); actual.Apply(true); UploadTextures.PreserveCoverage(actual, .5f);
                Assert.Greater(actual.mipmapCount, 4);
                var plain = Copy(source); plain.Apply(true);
                Assert.AreNotEqual(plain.GetPixels32(1), actual.GetPixels32(1), "the fixture's mips need their coverage kept");
                for (int mip = 0; mip < actual.mipmapCount; mip++)
                    CollectionAssert.AreEqual(expected.GetPixels32(mip), actual.GetPixels32(mip), "coverage, mip " + mip);
                // A DXT5nm normal map into BC5: X from red times alpha.
                var swizzled = Copy(source); ReferenceXToRed(swizzled);
                var mine = Copy(source); UploadTextures.XToRed(mine);
                CollectionAssert.AreEqual(swizzled.GetPixels32(0), mine.GetPixels32(0), "swizzle");
            }
            finally
            {
                foreach (var texture in made) Object.DestroyImmediate(texture);
            }
        }

        private static void ReferenceXToRed(Texture2D image)
        {
            var pixels = image.GetPixels32(0);
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                pixels[i] = new Color32((byte)((p.r * p.a + 127) / 255), p.g, 0, 255);
            }
            image.SetPixels32(pixels, 0);
        }

        private static void ReferencePreserveCoverage(Texture2D image, float cutoff)
        {
            int limit = Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 0, 254);
            float Coverage(Color32[] pixels, float scale)
            {
                int passing = 0;
                for (int i = 0; i < pixels.Length; i++) if (Mathf.Min(255f, pixels[i].a * scale) > limit) passing++;
                return passing / (float)pixels.Length;
            }
            float wanted = Coverage(image.GetPixels32(0), 1f);
            bool changed = false;
            for (int mip = 1; mip < image.mipmapCount; mip++)
            {
                var pixels = image.GetPixels32(mip);
                float now = Coverage(pixels, 1f);
                if (Mathf.Abs(now - wanted) * pixels.Length < 1f) continue;
                float low = now < wanted ? 1f : 0f, high = now < wanted ? 16f : 1f;
                for (int i = 0; i < 12; i++)
                {
                    float middle = (low + high) / 2f;
                    if (Coverage(pixels, middle) < wanted) low = middle; else high = middle;
                }
                float scale = (low + high) / 2f;
                for (int i = 0; i < pixels.Length; i++) pixels[i].a = (byte)Mathf.Min(255, Mathf.RoundToInt(pixels[i].a * scale));
                image.SetPixels32(pixels, mip);
                changed = true;
            }
            if (changed) image.Apply(false);
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
