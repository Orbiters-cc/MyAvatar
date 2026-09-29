using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // One compact row per texture that still needs a slot. Choosing a slot applies it immediately.
    internal static class MyAvatarResults
    {
        internal static void Populate(VisualElement root, MyAvatar avatar, System.Action edited, System.Action apply)
        {
            var pending = avatar.textures.Where(t => !t.applied && !avatar.textures.Any(o => o != t && o.applied && o.texture == t.texture)).ToList();
            if (pending.Count == 0) return;
            var slots = TextureChanges.Slots(avatar);
            foreach (var entry in pending)
            {
                var card = new VisualElement(); card.AddToClassList("texture-card"); root.Add(card);
                var image = new Image { image = entry.texture, scaleMode = ScaleMode.ScaleToFit }; image.AddToClassList("texture-preview"); card.Add(image);
                var text = new VisualElement(); text.AddToClassList("texture-text"); card.Add(text);
                var filename = new Label(entry.fileName); filename.AddToClassList("filename"); text.Add(filename);
                var reason = new Label(entry.reason) { tooltip = entry.reason }; reason.AddToClassList("reason"); text.Add(reason);
                void Choose(TextureSlot slot)
                {
                    edited(); Undo.RecordObject(avatar, "My Avatar: choose slot");
                    foreach (var other in avatar.textures.Where(t => !t.applied && t != entry && t.material == slot.material && t.property == slot.property)) { other.material = null; other.property = null; }
                    entry.material = slot.material; entry.property = slot.property; entry.reason = TextureMatching.ChosenReason;
                    TextureChanges.Dirty(avatar); apply();
                }
                var suggested = slots.Where(s => s.materialName == entry.suggestedMaterialName && s.property == entry.suggestedProperty).ToArray();
                void Menu()
                {
                    var menu = new GenericMenu { allowDuplicateNames = true };
                    foreach (var (path, slot) in MenuItems(entry, slots)) menu.AddItem(new GUIContent(path), false, () => Choose(slot));
                    menu.ShowAsContext();
                }
                // The best guess in one click, and every other compatible slot one menu away.
                if (suggested.Length == 1)
                {
                    var use = MyAvatarEditor.Button("Use on " + suggested[0].materialName, () => Choose(suggested[0]));
                    use.tooltip = suggested[0].Label;
                    use.AddToClassList("texture-choose"); card.Add(use);
                }
                var button = MyAvatarEditor.Button(suggested.Length == 1 ? "Other…" : "Choose slot…", Menu);
                button.tooltip = "Pick the material slot for this texture.";
                button.AddToClassList("texture-choose"); card.Add(button);
            }
        }

        // The slots an image can go to, one unique menu path each: visible objects first, packed metallic slots apart (the
        // image must already be packed for them), and materials that share a name told apart by their object's path.
        internal static List<(string path, TextureSlot slot)> MenuItems(TextureEntry entry, List<TextureSlot> slots)
        {
            bool Packed(TextureSlot s) => TextureMatching.Packed(entry.role, s.role);
            var offered = slots.Where(s => entry.role == "unknown" || s.role == "unknown" || TextureMatching.Compatible(entry.role, s.role) || Packed(s))
                .OrderBy(s => !s.active).ThenBy(Packed).ThenBy(s => s.secondary).ThenBy(s => s.materialName).ToList();
            var names = new Dictionary<Material, string>();
            foreach (var same in offered.GroupBy(s => s.material).Select(g => g.First()).GroupBy(s => s.materialName))
                foreach (var slot in same)
                {
                    string name = Item(slot.materialName) + (same.Count() > 1 ? " · " + slot.rendererPath.Replace("/", " › ") : "");
                    for (int n = 2; names.ContainsValue(name); n++) name = Item(slot.materialName) + " · " + slot.rendererPath.Replace("/", " › ") + " (" + n + ")";
                    names[slot.material] = name;
                }
            return offered.Select(s => ((s.active ? "" : "Hidden objects/") + (Packed(s) ? "Packed metallic slots (image must be packed)/" : "") +
                names[s.material] + "/" + Item(s.description) + " (" + s.property + ")", s)).ToList();
        }

        private static string Item(string text) => (text ?? "").Replace("/", " ∕ ");
    }
}
