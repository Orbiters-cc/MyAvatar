using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed class TextureStats
    {
        public float grayscale, normalColor, black, bright, mean;
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

        // Whether any texel is less than fully opaque, read back at 512 px at most. The image is halved one step at a time,
        // each step averaging 2×2 texels, so every texel weighs in: a lone transparent texel of an 8K image or near-opaque
        // alpha (smoothness, soft edges) still reads below 255, where a single sample or a threshold would lose it.
        internal static bool UsesAlpha(Texture texture, int max = 512)
        {
            int width = texture.width, height = texture.height;
            var previous = RenderTexture.active;
            Texture source = texture;
            RenderTexture step = null;
            Texture2D read = null;
            try
            {
                // A point-filtered image would sample one texel of four: it is copied once at full size first.
                bool copy = texture.filterMode == FilterMode.Point && Mathf.Max(width, height) > max;
                do
                {
                    if (!copy && Mathf.Max(width, height) > max) { width = Mathf.Max(1, (width + 1) / 2); height = Mathf.Max(1, (height + 1) / 2); }
                    copy = false;
                    var next = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    next.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(source, next);
                    if (step) RenderTexture.ReleaseTemporary(step);
                    source = step = next;
                } while (Mathf.Max(width, height) > max);
                RenderTexture.active = step;
                read = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                var pixels = read.GetRawTextureData<Color32>();
                for (int i = 0; i < pixels.Length; i++) if (pixels[i].a < 255) return true;
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                if (step) RenderTexture.ReleaseTemporary(step);
                if (read) UnityEngine.Object.DestroyImmediate(read);
            }
        }

        internal static TextureStats Compute(Color32[] pixels)
        {
            int gray = 0, normal = 0, black = 0, bright = 0; double brightness = 0;
            foreach (var p in pixels)
            {
                if (Math.Abs(p.r - p.g) < 15 && Math.Abs(p.g - p.b) < 15) gray++;
                if (p.b > 150 && p.r > 60 && p.r < 200 && p.g > 60 && p.g < 200) normal++;
                int peak = Math.Max(p.r, Math.Max(p.g, p.b));
                if (peak <= 20) black++;
                if (peak >= 128) bright++;
                brightness += (p.r + p.g + p.b) / (3 * 255.0);
            }
            float count = pixels.Length;
            return new TextureStats { grayscale = gray / count, normalColor = normal / count, black = black / count, bright = bright / count, mean = (float)(brightness / count) };
        }
    }
}
