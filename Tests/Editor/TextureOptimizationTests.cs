using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class TextureOptimizationTests
    {
        [Test] public void FormatFollowsNormalAndMeasuredAlpha()
        {
            Assert.AreEqual(TextureFormat.DXT1, TextureOptimization.Format(false, false));
            Assert.AreEqual(TextureFormat.BC7, TextureOptimization.Format(false, true));
            Assert.AreEqual(TextureFormat.BC5, TextureOptimization.Format(true, false));
            Assert.AreEqual(TextureFormat.BC5, TextureOptimization.Format(true, true));
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

        // Crunch only shrinks the download: DXT5 where BC7 or BC5 would go, the same memory as the blocks it holds.
        [Test] public void CrunchKeepsTheMemoryOfItsBlocks()
        {
            Assert.AreEqual(TextureFormat.DXT1Crunched, TextureOptimization.Crunch(TextureFormat.DXT1));
            Assert.AreEqual(TextureFormat.DXT1Crunched, TextureOptimization.Crunch(TextureFormat.DXT1Crunched));
            Assert.AreEqual(TextureFormat.DXT5Crunched, TextureOptimization.Crunch(TextureFormat.BC7));
            Assert.AreEqual(TextureFormat.DXT5Crunched, TextureOptimization.Crunch(TextureFormat.BC5));
            Assert.IsTrue(TextureOptimization.Crunched(TextureFormat.DXT5Crunched));
            Assert.IsFalse(TextureOptimization.Crunched(TextureFormat.DXT5));
            Assert.AreEqual(TextureFormat.DXT1Crunched, TextureOptimization.RuntimeFormat(TextureImporterFormat.DXT1Crunched));
            Assert.AreEqual(TextureFormat.BC5, TextureOptimization.RuntimeFormat(TextureImporterFormat.BC5));
            Assert.AreEqual(TextureOptimization.FormatBytes(TextureFormat.DXT1, 512, 512, true), TextureOptimization.FormatBytes(TextureFormat.DXT1Crunched, 512, 512, true));
            Assert.AreEqual(TextureOptimization.FormatBytes(TextureFormat.DXT5, 512, 512, true), TextureOptimization.FormatBytes(TextureFormat.DXT5Crunched, 512, 512, true));
        }
    }
}
