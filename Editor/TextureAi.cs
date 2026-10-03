using System;
using System.Collections.Generic;
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

        internal sealed class Result { public List<TextureChanges.Change> changes = new List<TextureChanges.Change>(); public string[] warnings = Array.Empty<string>(); }

        // Built on the main thread from the pre-apply state; no image data is sent, only names, sizes and local pixel statistics.
        internal static object Payload(List<TextureEntry> textures, List<TextureSlot> slots, Dictionary<Texture2D, TextureStats> stats) => new
        {
            textures = textures.Select((entry, i) =>
            {
                var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(entry.texture)) as TextureImporter;
                int width = entry.texture.width, height = entry.texture.height;
                if (importer != null) importer.GetSourceTextureWidthAndHeight(out width, out height);
                var s = stats.TryGetValue(entry.texture, out var measured) ? measured : new TextureStats();
                return new { id = "t" + i, name = entry.fileName, width, height, role = entry.role,
                    grayscaleFraction = Round(s.grayscale), normalColorFraction = Round(s.normalColor), blackFraction = Round(s.black),
                    brightFraction = Round(s.bright), meanBrightness = Round(s.mean),
                    localSlotId = slots.FirstOrDefault(slot => slot.material == entry.material && slot.property == entry.property)?.id };
            }).ToArray(),
            slots = slots.Take(512).Select(s => new { s.id, s.materialName, s.shader, s.property, s.description, s.role,
                s.existingName, s.rendererPath, s.existingWidth, s.existingHeight }).ToArray(),
        };

        private static double Round(float value) => Math.Round(Mathf.Clamp01(value), 3);

        [Serializable] private sealed class Preference { public bool enabled; }
        internal static readonly TextureAiPreferences Preferences = new TextureAiPreferences((token, enabled) =>
        {
            string url = OrbitersEnvironment.ApiUrl("myavatar/ai-preferences");
            return cancellation => WritePreferenceAsync(url, token, enabled, cancellation);
        });

        static TextureAi()
        {
            AuthenticationService.Changed += Preferences.InvalidateContext;
            OrbitersEnvironment.Changed += Preferences.InvalidateContext;
        }

        /// <summary>Turns AI help on or off for the account (the same switch as on the Orbiters account page).</summary>
        internal static Task<bool> SetEnabledAsync(string token, bool enabled, CancellationToken cancellation = default) =>
            Preferences.SetAsync(token, enabled, cancellation);

        private static async Task<bool> WritePreferenceAsync(string url, string token, bool enabled, CancellationToken cancellation)
        {
            var preference = await OrbitersApi.SendAsync<Preference>(url, token,
                new { enabled }, cancellation, System.Net.Http.HttpMethod.Put);
            return preference?.enabled ?? enabled;
        }

        internal static async Task<Result> RequestAsync(string token, object payload, List<TextureEntry> textures, List<TextureSlot> slots, CancellationToken cancellation)
        {
            // The backend enforces the account's AI preference; a separate connection check would only add a round trip.
            var plan = await OrbitersApi.SendAsync<Plan>(OrbitersEnvironment.ApiUrl("myavatar/texture-plan"), token, payload, cancellation);
            var result = new Result { warnings = plan?.warnings ?? Array.Empty<string>() };
            foreach (var match in plan?.assignments ?? Array.Empty<Assignment>())
            {
                if (match.confidence < .9f || !int.TryParse((match.textureId ?? "").TrimStart('t'), out int index) || index < 0 || index >= textures.Count) continue;
                var slot = slots.FirstOrDefault(s => s.id == match.slotId);
                var entry = textures[index];
                if (slot == null) continue;
                if (!TextureMatching.Compatible(entry.role, slot)) continue;
                if (!TextureMatching.CanAutoAssign(entry, slot)) continue;
                result.changes.Add(new TextureChanges.Change { entry = entry, slot = slot, confidence = match.confidence, reason = match.reason });
            }
            return result;
        }
    }
}
