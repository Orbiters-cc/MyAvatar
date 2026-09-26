using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    internal static class TextureAi
    {
        [Serializable] private sealed class Plan { public Assignment[] assignments; public string[] warnings; }
        [Serializable] private sealed class Assignment { public string textureId, slotId, reason; public float confidence; }

        internal static async Task<string> CompleteAsync(List<TextureEntry> textures, List<TextureSlot> slots, CancellationToken cancellation)
        {
            var auth = AuthenticationService.GetAuth();
            if (string.IsNullOrEmpty(auth?.token)) return "Offline matching · connect to Orbiters for AI assistance.";
            var account = await OrbitersAccountService.CheckAsync("myavatar/connection", auth.token, cancellation);
            if (!account.aiEnabled) return "AI is disabled in your Orbiters account. Local matches were used.";
            if (account.state != "connected") return "Orbiters is disconnected. Local matches were used.";
            const int tile = 112, columns = 6;
            int rows = (textures.Count + columns - 1) / columns;
            var sheet = new Texture2D(columns * tile, rows * tile, TextureFormat.RGB24, false);
            var metadata = new List<object>();
            byte[] jpeg;
            try
            {
                for (int i = 0; i < textures.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var entry = textures[i];
                    var preview = TextureImport.Preview(entry.texture, tile);
                    try
                    {
                        var pixels = preview.GetPixels32();
                        double gray = pixels.Count(p => Math.Abs(p.r - p.g) < 15 && Math.Abs(p.g - p.b) < 15) / (double)pixels.Length;
                        double normal = pixels.Count(p => p.b > 150 && p.r > 60 && p.r < 200 && p.g > 60 && p.g < 200) / (double)pixels.Length;
                        sheet.SetPixels32(i % columns * tile, (rows - 1 - i / columns) * tile, preview.width, preview.height, pixels);
                        var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(entry.texture)) as TextureImporter;
                        int width = entry.texture.width, height = entry.texture.height;
                        if (importer != null) importer.GetSourceTextureWidthAndHeight(out width, out height);
                        metadata.Add(new { id = "t" + i, name = entry.fileName, relativePath = "Textures/" + entry.fileName,
                            width, height, bytes = new FileInfo(AssetDatabase.GetAssetPath(entry.texture)).Length,
                            grayscaleFraction = Math.Round(gray, 3), normalColorFraction = Math.Round(normal, 3), role = entry.role,
                            previewRow = i / columns, previewColumn = i % columns,
                            localSlotId = slots.FirstOrDefault(s => s.material == entry.material && s.property == entry.property)?.id });
                    }
                    finally { UnityEngine.Object.DestroyImmediate(preview); }
                    await Task.Yield();
                }
                sheet.Apply(); jpeg = sheet.EncodeToJPG(75);
            }
            finally { UnityEngine.Object.DestroyImmediate(sheet); }
            if (jpeg.Length > 512 * 1024) throw new InvalidOperationException("Texture previews exceed the AI request limit. Use a smaller batch; offline matches are available.");
            var payload = new { textures = metadata, slots = slots.Take(512).Select(s => new { s.id, s.materialName, s.shader, s.property,
                s.description, s.role, s.existingName, s.existingPath, s.existingWidth, s.existingHeight, s.rendererPath }).ToArray(),
                preview = Convert.ToBase64String(jpeg), previewColumns = columns };
            var result = await OrbitersApi.SendAsync<Plan>(OrbitersEnvironment.ApiUrl("myavatar/texture-plan"), auth.token, payload, cancellation);
            var used = new HashSet<string>(slots.Where(s => textures.Any(t => t.material == s.material && t.property == s.property)).Select(s => s.id));
            foreach (var match in result?.assignments ?? Array.Empty<Assignment>())
            {
                if (match.confidence < .9f || !int.TryParse((match.textureId ?? "").TrimStart('t'), out int index) || index < 0 || index >= textures.Count) continue;
                var slot = slots.FirstOrDefault(s => s.id == match.slotId);
                var entry = textures[index];
                if (slot == null || entry.material || used.Contains(slot.id) || entry.reason?.StartsWith("Alternative textures", StringComparison.Ordinal) == true) continue;
                if (entry.role != "unknown" && slot.role != "unknown" && entry.role != slot.role) continue;
                entry.material = slot.material; entry.property = slot.property; entry.confidence = match.confidence;
                entry.reason = "Orbiters AI · " + match.reason; used.Add(slot.id);
            }
            TextureMatching.RejectConflicts(textures);
            return "Orbiters AI completed." + (result?.warnings?.Length > 0 ? " " + string.Join(" ", result.warnings) : "");
        }
    }
}
