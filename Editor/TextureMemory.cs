using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor
{
    // Remembers the file → material slot choices a user made by hand, per avatar, so repeated drops reuse them.
    // Automatic matches are not stored: they are recomputed, and a wrong automatic guess must never become permanent.
    internal static class TextureMemory
    {
        internal sealed class Slot { public string material, shader, property; }
        private const string File = "slot-choices.json";
        private static string Key(MyAvatar avatar) => GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString();
        private static string Name(string fileName) => (fileName ?? "").ToLowerInvariant();

        internal static Dictionary<string, List<Slot>> Load(MyAvatar avatar) =>
            LibraryStore.Read<Dictionary<string, Dictionary<string, List<Slot>>>>(File).TryGetValue(Key(avatar), out var mappings) ? mappings : new Dictionary<string, List<Slot>>();

        internal static void Record(MyAvatar avatar, IEnumerable<TextureEntry> entries)
        {
            var chosen = entries.Where(e => e.applied && e.material && !string.IsNullOrEmpty(e.property) &&
                (e.reason == TextureMatching.ChosenReason || e.reason == TextureMatching.RememberedReason)).GroupBy(e => Name(e.fileName)).ToList();
            if (chosen.Count == 0) return;
            var all = LibraryStore.Read<Dictionary<string, Dictionary<string, List<Slot>>>>(File);
            string key = Key(avatar);
            if (!all.TryGetValue(key, out var mappings)) all[key] = mappings = new Dictionary<string, List<Slot>>();
            foreach (var file in chosen)
                mappings[file.Key] = file.Select(e => new Slot { material = e.material.name, shader = TextureMatching.ShaderName(e.material), property = e.property }).ToList();
            LibraryStore.Write(File, all);
        }

        internal static List<TextureSlot> Find(Dictionary<string, List<Slot>> mappings, TextureEntry entry, List<TextureSlot> slots)
        {
            if (!mappings.TryGetValue(Name(entry.fileName), out var remembered)) return new List<TextureSlot>();
            var found = new List<TextureSlot>();
            foreach (var slot in remembered)
            {
                var matches = slots.Where(s => s.materialName == slot.material && s.shader == slot.shader && s.property == slot.property).ToList();
                if (matches.Count == 1) found.Add(matches[0]);
            }
            return found;
        }
    }
}
