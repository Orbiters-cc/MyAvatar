using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // The textures that still need a slot, grouped by their most likely material: one compact row each, with the best
    // guess one click away. Images that belong nowhere (screenshots, icons of the dropped folder) can be dismissed one by
    // one, by group or all at once. Choosing a slot applies it immediately.
    internal static class MyAvatarResults
    {
        private const string NoTarget = "No clear target";

        internal static void Populate(VisualElement root, MyAvatar avatar, System.Action edited, System.Action apply, System.Action refresh)
        {
            var pending = avatar.textures.Where(t => !t.applied && !t.dismissed && !avatar.textures.Any(o => o != t && o.applied && o.texture == t.texture)).ToList();
            if (pending.Count == 0) return;
            var slots = TextureChanges.Slots(avatar);

            void Dismiss(IEnumerable<TextureEntry> entries)
            {
                var textures = new HashSet<Texture2D>(entries.Select(e => e.texture));
                Undo.RecordObject(avatar, "My Avatar: dismiss textures");
                foreach (var entry in avatar.textures.Where(t => !t.applied && textures.Contains(t.texture))) entry.dismissed = true;
                TextureChanges.Dirty(avatar);
                refresh();
            }

            var list = new VisualElement(); list.AddToClassList("texture-list"); root.Add(list);
            var header = new VisualElement(); header.AddToClassList("texture-list__header"); list.Add(header);
            var heading = new Label("Choose where these go"); heading.AddToClassList("texture-list__title"); header.Add(heading);
            var all = MyAvatarEditor.Button("Dismiss all", () => Dismiss(pending));
            all.tooltip = "Leave all these images out. Undo brings them back.";
            all.AddToClassList("avatar-link"); all.AddToClassList("texture-list__dismiss-all"); header.Add(all);

            // Most likely material first by size; guesses without evidence last.
            var groups = pending.GroupBy(Target).OrderBy(g => g.Key == null).ThenByDescending(g => g.Count()).ThenBy(g => g.Key);
            foreach (var group in groups)
            {
                var box = new VisualElement(); box.AddToClassList("texture-group"); list.Add(box);
                var top = new VisualElement(); top.AddToClassList("texture-group__header"); box.Add(top);
                var title = new Label(group.Key ?? NoTarget); title.AddToClassList("texture-group__title"); top.Add(title);
                title.tooltip = group.Key != null ? "The material these images most likely belong to." : "My Avatar found no material these images clearly belong to.";
                var count = new Label(group.Count().ToString()); count.AddToClassList("texture-group__count"); top.Add(count);
                var entries = group.ToList();
                top.Add(Close("Dismiss these " + entries.Count + " images", () => Dismiss(entries)));
                foreach (var entry in entries) box.Add(Card(entry, slots, avatar, edited, apply, () => Dismiss(new[] { entry }), group.Key != null));
            }
        }

        // The material an image most likely belongs to, or null when nothing clearly fits.
        private static string Target(TextureEntry entry) =>
            string.IsNullOrEmpty(entry.suggestedMaterialName) || (entry.reason ?? "").StartsWith(TextureMatching.NoClearMatchReason, System.StringComparison.Ordinal)
                ? null : entry.suggestedMaterialName;

        // Under a material's heading the best guess just says "Use": the heading already names the material.
        private static VisualElement Card(TextureEntry entry, List<TextureSlot> slots, MyAvatar avatar, System.Action edited, System.Action apply, System.Action dismiss, bool underTarget)
        {
            var card = new VisualElement(); card.AddToClassList("texture-card");
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
                var use = MyAvatarEditor.Button(underTarget ? "Use" : "Use on " + suggested[0].materialName, () => Choose(suggested[0]));
                use.tooltip = "Use on " + suggested[0].Label;
                use.AddToClassList("texture-choose"); card.Add(use);
            }
            var button = MyAvatarEditor.Button(suggested.Length == 1 ? "Other…" : "Choose slot…", Menu);
            button.tooltip = "Pick the material slot for this texture.";
            button.AddToClassList("texture-choose"); card.Add(button);
            card.Add(Close("Dismiss this image", dismiss));
            return card;
        }

        private static Button Close(string tooltip, System.Action action)
        {
            var close = MyAvatarEditor.Button("", action);
            close.tooltip = tooltip;
            close.AddToClassList("texture-dismiss");
            var icon = new VectorIcon(IconGlyph.Close); icon.AddToClassList("texture-dismiss__icon"); close.Add(icon);
            return close;
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
