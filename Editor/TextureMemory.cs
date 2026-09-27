using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor
{
    // Remembers applied file → material slot choices per avatar, so repeated drops of a texture set skip matching.
    internal static class TextureMemory
    {
        internal sealed class Slot { public string material, shader, property; }
        private const string File = "mappings.json";
        private static string Key(MyAvatar avatar) => GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString();
        private static string Name(string fileName) => (fileName ?? "").ToLowerInvariant();

        internal static Dictionary<string, Slot> Load(MyAvatar avatar) =>
            LibraryStore.Read<Dictionary<string, Dictionary<string, Slot>>>(File).TryGetValue(Key(avatar), out var mappings) ? mappings : new Dictionary<string, Slot>();

        internal static void Record(MyAvatar avatar, IEnumerable<TextureEntry> entries)
        {
            var applied = entries.Where(e => e.applied && e.material && !string.IsNullOrEmpty(e.property)).ToList();
            if (applied.Count == 0) return;
            var all = LibraryStore.Read<Dictionary<string, Dictionary<string, Slot>>>(File);
            string key = Key(avatar);
            if (!all.TryGetValue(key, out var mappings)) all[key] = mappings = new Dictionary<string, Slot>();
            foreach (var entry in applied)
                mappings[Name(entry.fileName)] = new Slot { material = entry.material.name, shader = entry.material.shader ? entry.material.shader.name : "", property = entry.property };
            LibraryStore.Write(File, all);
        }

        internal static TextureSlot Find(Dictionary<string, Slot> mappings, TextureEntry entry, List<TextureSlot> slots)
        {
            if (!mappings.TryGetValue(Name(entry.fileName), out var remembered)) return null;
            var matches = slots.Where(s => s.materialName == remembered.material && s.shader == remembered.shader && s.property == remembered.property).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
