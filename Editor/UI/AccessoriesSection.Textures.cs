using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed partial class AccessoriesSection
    {
        private readonly HashSet<int> closedTextures = new HashSet<int>();

        private void TextureCard(VisualElement entry, OrbitersAttachment item)
        {
            var slots = TextureChanges.Scoped(TextureMatching.Slots(avatar, includeAll: true), new List<Transform> { item.transform })
                .Where(s => s.existing).ToList();
            if (slots.Count == 0) return;
            int id = item.GetInstanceID();
            var fold = new Foldout { text = "Textures · " + slots.Count, value = !closedTextures.Contains(id) };
            fold.AddToClassList("accessory-textures");
            fold.RegisterValueChangedCallback(e => { if (e.newValue) closedTextures.Remove(id); else closedTextures.Add(id); });
            entry.Add(fold);
            foreach (var group in slots.GroupBy(s => s.material))
            {
                var title = new Label(group.Key.name); title.AddToClassList("accessory-textures__material"); fold.Add(title);
                foreach (var slot in group)
                {
                    var row = new VisualElement(); row.AddToClassList("accessory-textures__row"); fold.Add(row);
                    var image = new Image { image = slot.existing as Texture2D, scaleMode = ScaleMode.ScaleToFit };
                    image.AddToClassList("accessory-textures__image"); row.Add(image);
                    var text = new VisualElement(); text.AddToClassList("accessory-textures__text"); row.Add(text);
                    var role = new Label(slot.description) { tooltip = slot.property }; role.AddToClassList("accessory-textures__role"); text.Add(role);
                    var name = new Label(slot.existingName) { tooltip = slot.existingPath }; name.AddToClassList("accessory-textures__name"); text.Add(name);
                    var remove = Small("Remove", () =>
                    {
                        row.style.display = DisplayStyle.None;
                        // Paint the immediate response before asset creation and Inspector refresh.
                        schedule.Execute(() =>
                        {
                            try { TextureChanges.RemoveSlot(avatar, item.transform, slot.material, slot.property); }
                            finally { RefreshList(); }
                        });
                    });
                    remove.tooltip = "Remove this map from this item only. Undo restores it.";
                    row.Add(remove);
                }
            }
        }
    }
}
