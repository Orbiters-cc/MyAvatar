using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        // Hash the source path, never the texture contents. Distinct folders remain distinct even after import copies.
        internal static string SourceKey(string path)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            string project = Path.GetFullPath(Path.GetDirectoryName(UnityEngine.Application.dataPath)).Replace('\\', '/') + "/";
            if (full.StartsWith(project, StringComparison.OrdinalIgnoreCase))
            {
                string guid = AssetDatabase.AssetPathToGUID(full.Substring(project.Length));
                if (!string.IsNullOrEmpty(guid)) return "asset:" + guid;
            }
            if (Path.DirectorySeparatorChar == '\\') full = full.ToUpperInvariant();
            using var hash = SHA256.Create();
            return "source:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(full))).Replace("-", "");
        }

        internal static string Identity(TextureEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.sourceKey)) return entry.sourceKey;
            if (!entry.texture) return null;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(entry.texture));
            return !string.IsNullOrEmpty(guid) ? "asset:" + guid : "texture:" + entry.texture.GetInstanceID();
        }

        internal static Dictionary<string, List<Slot>> Load(MyAvatar avatar) =>
            LibraryStore.Read<Dictionary<string, Dictionary<string, List<Slot>>>>(File).TryGetValue(Key(avatar), out var mappings) ? mappings : new Dictionary<string, List<Slot>>();

        internal static void Record(MyAvatar avatar, IEnumerable<TextureEntry> entries)
        {
            var all = LibraryStore.Read<Dictionary<string, Dictionary<string, List<Slot>>>>(File);
            string key = Key(avatar);
            if (!all.TryGetValue(key, out var mappings)) all[key] = mappings = new Dictionary<string, List<Slot>>();
            if (RecordChoices(mappings, entries)) LibraryStore.Write(File, all);
        }

        internal static bool RecordChoices(Dictionary<string, List<Slot>> mappings, IEnumerable<TextureEntry> entries)
        {
            var chosen = entries.Where(e => e.applied && e.material && !string.IsNullOrEmpty(e.property) &&
                (e.reason == TextureMatching.ChosenReason || e.reason == TextureMatching.RememberedReason) && Identity(e) != null).GroupBy(Identity).ToList();
            foreach (var file in chosen)
                mappings[file.Key] = file.Select(e => new Slot { material = e.material.name, shader = TextureMatching.ShaderName(e.material), property = e.property }).ToList();
            return chosen.Count > 0;
        }

        internal static List<TextureSlot> Find(Dictionary<string, List<Slot>> mappings, TextureEntry entry, List<TextureSlot> slots)
        {
            string identity = Identity(entry);
            if (identity == null || !mappings.TryGetValue(identity, out var remembered)) return new List<TextureSlot>();
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
