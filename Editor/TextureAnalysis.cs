using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed class TextureStats
    {
        public float grayscale, normalColor, black, bright, mean;
        // Share of samples that are not fully opaque. Samples come from the mip chain, so small cut-outs still lower them.
        public float transparent;
        // A mostly black image with small bright areas: typical of an emission map exported beside a full albedo.
        public bool LooksEmissive => black >= .7f && bright > .002f && bright <= .35f;
    }

    internal static class TextureAnalysis
    {
        private const int Tile = 64;

        // Synchronous tiny readbacks: each costs about a millisecond and, unlike AsyncGPUReadback,
        // does not wait for Editor frames, which are throttled while Unity is in the background.
        internal static Dictionary<Texture2D, TextureStats> Measure(IEnumerable<Texture2D> textures)
        {
            var result = new Dictionary<Texture2D, TextureStats>();
            var previous = RenderTexture.active;
            var readback = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false, true);
            try
            {
                foreach (var texture in textures)
                {
                    if (!texture || result.ContainsKey(texture)) continue;
                    var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
                    bool srgb = importer == null || importer.sRGBTexture;
                    // Keep stored values: sRGB textures round-trip through an sRGB target, linear data through a linear one.
                    var rt = RenderTexture.GetTemporary(Tile, Tile, 0, RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                    try
                    {
                        Graphics.Blit(texture, rt);
                        RenderTexture.active = rt;
                        readback.ReadPixels(new Rect(0, 0, Tile, Tile), 0, 0, false);
                    }
                    finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
                    var stats = Compute(readback.GetPixels32());
                    // Imported normal maps are stored swizzled, so their samples are not tangent-space colours.
                    if (importer != null && importer.textureType == TextureImporterType.NormalMap) stats.normalColor = 1;
                    result.Add(texture, stats);
                }
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(readback); }
            return result;
        }

        internal static TextureStats Compute(Color32[] pixels)
        {
            int gray = 0, normal = 0, black = 0, bright = 0, transparent = 0; double brightness = 0;
            foreach (var p in pixels)
            {
                if (Math.Abs(p.r - p.g) < 15 && Math.Abs(p.g - p.b) < 15) gray++;
                if (p.b > 150 && p.r > 60 && p.r < 200 && p.g > 60 && p.g < 200) normal++;
                int peak = Math.Max(p.r, Math.Max(p.g, p.b));
                if (peak <= 20) black++;
                if (peak >= 128) bright++;
                if (p.a < 250) transparent++;
                brightness += (p.r + p.g + p.b) / (3 * 255.0);
            }
            float count = pixels.Length;
            return new TextureStats { grayscale = gray / count, normalColor = normal / count, black = black / count, bright = bright / count, mean = (float)(brightness / count),
                transparent = transparent / count };
        }
    }
}
