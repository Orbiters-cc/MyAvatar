using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class TextureOptimizationTests
    {
        [Test] public void FormatFollowsNormalAndMeasuredAlpha()
        {
            Assert.AreEqual(TextureImporterFormat.DXT1, TextureOptimization.Format(false, false));
            Assert.AreEqual(TextureImporterFormat.BC7, TextureOptimization.Format(false, true));
            Assert.AreEqual(TextureImporterFormat.BC5, TextureOptimization.Format(true, false));
            Assert.AreEqual(TextureImporterFormat.BC5, TextureOptimization.Format(true, true));
        }

        [Test] public void BitsPerPixelMatchVramFormats()
        {
            Assert.AreEqual(4, TextureOptimization.BitsPerPixel(TextureFormat.DXT1));
            Assert.AreEqual(4, TextureOptimization.BitsPerPixel(TextureFormat.DXT1Crunched));
            Assert.AreEqual(8, TextureOptimization.BitsPerPixel(TextureFormat.DXT5));
            Assert.AreEqual(8, TextureOptimization.BitsPerPixel(TextureFormat.BC5));
            Assert.AreEqual(8, TextureOptimization.BitsPerPixel(TextureFormat.BC7));
            Assert.AreEqual(32, TextureOptimization.BitsPerPixel(TextureFormat.RGBA32));
        }

        [Test] public void MemoryEstimateAddsAThirdForMips()
        {
            Assert.AreEqual(131072, TextureOptimization.Bytes(512, 512, 4, false));
            Assert.AreEqual(174762, TextureOptimization.Bytes(512, 512, 4, true));
            // A 4K RGBA32 texture with mips, as VRChat counts it: about 85 MB.
            Assert.AreEqual(89478485, TextureOptimization.Bytes(4096, 4096, 32, true));
        }

        [Test] public void CapHalvesByMipLevelsAndNeverUpscales()
        {
            Assert.AreEqual(new Vector2Int(512, 512), TextureOptimization.Capped(4096, 4096, 512));
            Assert.AreEqual(new Vector2Int(512, 256), TextureOptimization.Capped(2048, 1024, 512));
            Assert.AreEqual(new Vector2Int(256, 256), TextureOptimization.Capped(256, 256, 512));
            Assert.AreEqual(new Vector2Int(300, 200), TextureOptimization.Capped(300, 200, 512));
        }

        [Test] public void DataTexturesAreExcluded()
        {
            Assert.IsNull(TextureOptimization.Exclusion("Assets/Body_BaseColor.png", "Body_BaseColor", "_MainTex Main Texture", 2048, 2048));
            Assert.IsNull(TextureOptimization.Exclusion("Assets/Eye.png", "Eye", "_MatcapTex Matcap", 512, 512));
            Assert.IsNotNull(TextureOptimization.Exclusion("Assets/Toon.png", "Toon", "_ShadowRampTex Shadow Ramp", 256, 256));
            Assert.IsNotNull(TextureOptimization.Exclusion("Assets/Grade.png", "ColorLUT", "_MainTex Main", 1024, 1024));
            Assert.IsNotNull(TextureOptimization.Exclusion("Assets/Sky.png", "SkyGradient", "_MainTex Main", 1024, 1024));
            Assert.IsNotNull(TextureOptimization.Exclusion("Assets/Face.png", "FaceSDF", "_MainTex Main", 1024, 1024));
            Assert.IsNotNull(TextureOptimization.Exclusion("Assets/strip.png", "strip", "_MainTex Main", 256, 8));
            Assert.IsNotNull(TextureOptimization.Exclusion("Assets/strip.png", "strip", "_MainTex Main", 1024, 32));
            Assert.IsNotNull(TextureOptimization.Exclusion("Assets/Glow.exr", "Glow", "_EmissionMap Emission", 1024, 1024));
            // Words containing "lut" are not lookup tables.
            Assert.IsNull(TextureOptimization.Exclusion("Assets/Flute.png", "Flute", "_MainTex Main", 1024, 1024));
        }

        [Test] public void SameComparesOnlyWhatMatters()
        {
            var target = TextureOptimization.Target(TextureImporterFormat.DXT1, 512);
            Assert.IsTrue(target.overridden && target.mipmaps && target.streaming && !target.crunched && target.quality == 100 && target.maxSize == 512);
            var compression = target; compression.compression = 0;
            Assert.IsTrue(TextureOptimization.Same(target, compression));
            var size = target; size.maxSize = 1024;
            Assert.IsFalse(TextureOptimization.Same(target, size));
            var original = new ImporterState { overridden = false, maxSize = 2048, format = -1, mipmaps = true };
            var drifted = original; drifted.maxSize = 4096;
            Assert.IsTrue(TextureOptimization.Same(original, drifted));
            drifted.streaming = true;
            Assert.IsFalse(TextureOptimization.Same(original, drifted));
        }

        [Test] public void TransparentSamplesAreCounted()
        {
            var opaque = new[] { new Color32(10, 20, 30, 255), new Color32(200, 200, 200, 255) };
            Assert.AreEqual(0f, TextureAnalysis.Compute(opaque).transparent);
            var cutout = new[] { new Color32(10, 20, 30, 255), new Color32(200, 200, 200, 0) };
            Assert.AreEqual(.5f, TextureAnalysis.Compute(cutout).transparent);
        }

        [Test] public void EmptyRecordIsNotApplied()
        {
            var record = new TextureOptimizationRecord();
            Assert.IsFalse(TextureOptimization.Applied(record));
            record.textures.Add(new OptimizedTexture { applied = false });
            Assert.IsFalse(TextureOptimization.Applied(record));
            record.textures[0].applied = true;
            Assert.IsTrue(TextureOptimization.Applied(record));
            Assert.AreEqual(1, TextureOptimization.Changed(record));
        }
    }
}
