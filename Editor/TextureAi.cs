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
        [Serializable] internal sealed class Plan { public Assignment[] assignments; public string[] warnings; }
        [Serializable] internal sealed class Assignment { public string textureId, slotId, reason; public float confidence; }

        internal sealed class Result { public List<TextureChanges.Change> changes = new List<TextureChanges.Change>(); public string[] warnings = Array.Empty<string>(); }

        // The most slots one request may carry (the server's limit).
        internal const int MaxSlots = 512;

        // The open slots worth asking about, at most MaxSlots: those of the materials this drop matched or points at first,
        // then the commonly used maps (main, normal, emission, metallic and smoothness, occlusion, masks...), visible ones
        // before hidden ones. Poiyomi alone declares over a hundred texture slots per material.
        internal static List<TextureSlot> Relevant(List<TextureSlot> open, List<TextureEntry> entries)
        {
            if (open.Count <= MaxSlots) return open;
            var targets = new HashSet<Material>(entries.Where(e => e.material).Select(e => e.material));
            var named = new HashSet<string>(entries.Where(e => !e.material && !string.IsNullOrEmpty(e.suggestedMaterialName)).Select(e => e.suggestedMaterialName));
            return open.Select((slot, index) => (slot, index))
                .OrderByDescending(p => targets.Contains(p.slot.material) || named.Contains(p.slot.materialName))
                .ThenByDescending(p => TextureMatching.Commonness(p.slot)).ThenByDescending(p => p.slot.active).ThenBy(p => p.index)
                .Take(MaxSlots).OrderBy(p => p.index).Select(p => p.slot).ToList();
        }

        // Built on the main thread from the pre-apply state; no image data is sent, only names, sizes and local pixel statistics.
        // IDs are positions in these lists ("t0", "s0"), so they stay short however many slots the avatar has.
        internal static object Payload(List<TextureEntry> textures, List<TextureSlot> slots, Dictionary<Texture2D, TextureStats> stats)
        {
            slots = slots.Take(MaxSlots).ToList();
            return new
            {
                textures = textures.Select((entry, i) =>
                {
                    var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(entry.texture)) as TextureImporter;
                    int width = entry.texture.width, height = entry.texture.height;
                    if (importer != null) importer.GetSourceTextureWidthAndHeight(out width, out height);
                    var s = stats.TryGetValue(entry.texture, out var measured) ? measured : new TextureStats();
                    int local = slots.FindIndex(slot => slot.material == entry.material && slot.property == entry.property);
                    return new { id = "t" + i, name = entry.fileName, width, height, role = entry.role,
                        grayscaleFraction = Round(s.grayscale), normalColorFraction = Round(s.normalColor), blackFraction = Round(s.black),
                        brightFraction = Round(s.bright), meanBrightness = Round(s.mean), localSlotId = local < 0 ? null : "s" + local };
                }).ToArray(),
                slots = slots.Select((s, i) => new { id = "s" + i, s.materialName, s.shader, s.property, s.description, s.role,
                    s.existingName, s.rendererPath, s.existingWidth, s.existingHeight }).ToArray(),
            };
        }

        // The position an ID of the payload stands for ("s12" -> 12), or -1.
        private static int Index(string id, char prefix) => id != null && id.Length > 1 && id[0] == prefix &&
            int.TryParse(id.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index) ? index : -1;

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
            return Accept(plan, textures, slots);
        }

        // Confident answers that fit, mapped back from the payload's IDs to the textures and slots it was built from.
        internal static Result Accept(Plan plan, List<TextureEntry> textures, List<TextureSlot> slots)
        {
            var result = new Result { warnings = plan?.warnings ?? Array.Empty<string>() };
            foreach (var match in plan?.assignments ?? Array.Empty<Assignment>())
            {
                int texture = Index(match.textureId, 't'), slotIndex = Index(match.slotId, 's');
                if (match.confidence < .9f || texture < 0 || texture >= textures.Count || slotIndex < 0 || slotIndex >= Math.Min(slots.Count, MaxSlots)) continue;
                var entry = textures[texture]; var slot = slots[slotIndex];
                if (!TextureMatching.Compatible(entry.role, slot)) continue;
                if (!TextureMatching.CanAutoAssign(entry, slot)) continue;
                result.changes.Add(new TextureChanges.Change { entry = entry, slot = slot, confidence = match.confidence, reason = match.reason });
            }
            return result;
        }
    }
}
