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
            var slots = TextureMatching.Slots(avatar);
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
                var button = suggested.Length == 1
                    ? MyAvatarEditor.Button("Use on " + suggested[0].materialName, () => Choose(suggested[0]))
                    : MyAvatarEditor.Button("Choose slot…", null);
                button.tooltip = suggested.Length == 1 ? suggested[0].Label : "Pick the material slot for this texture.";
                if (suggested.Length != 1)
                    button.clicked += () => {
                        var menu = new GenericMenu();
                        foreach (var slot in slots.Where(s => entry.role == "unknown" || s.role == "unknown" || TextureMatching.Compatible(entry.role, s.role))
                            .OrderBy(s => !s.active).ThenBy(s => s.secondary).ThenBy(s => s.materialName))
                        {
                            var selected = slot;
                            menu.AddItem(new GUIContent((selected.active ? "" : "Hidden objects/") + selected.materialName.Replace("/", " ∕ ") + "/" + selected.description.Replace("/", " ∕ ") + " (" + selected.property + ")"),
                                false, () => Choose(selected));
                        }
                        menu.ShowAsContext();
                    };
                button.AddToClassList("texture-choose"); card.Add(button);
            }
        }
    }
}
