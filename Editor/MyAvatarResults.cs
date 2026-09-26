using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    internal static class MyAvatarResults
    {
        internal static void Populate(VisualElement root, MyAvatar avatar, System.Action apply)
        {
            if (avatar.textures.Count == 0) return;
            var slots = TextureMatching.Slots(avatar);
            var title = new Label($"{avatar.textures.Count(t => t.applied)} applied · {avatar.textures.Count(t => !t.applied)} need attention");
            title.AddToClassList("section-title"); root.Add(title);
            foreach (var entry in avatar.textures.Where(t => !t.applied))
            {
                var card = new VisualElement(); card.AddToClassList("texture-card"); root.Add(card);
                var heading = new VisualElement(); heading.AddToClassList("texture-heading"); card.Add(heading);
                var image = new Image { image = entry.texture, scaleMode = ScaleMode.ScaleToFit }; image.AddToClassList("texture-preview"); heading.Add(image);
                var filename = new Label(entry.fileName); filename.AddToClassList("filename"); heading.Add(filename);
                var reason = new Label(entry.reason); reason.AddToClassList("muted"); card.Add(reason);
                var choose = MyAvatarEditor.Button(entry.material ? entry.material.name + " / " + entry.property : "Choose material slot…", () => {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Leave unassigned"), !entry.material, () => { Undo.RecordObject(avatar,"My Avatar: choose slot"); entry.material = null; entry.property = null; TextureChanges.Dirty(avatar); root.Clear(); Populate(root,avatar,apply); });
                    foreach (var slot in slots.Where(s => entry.role == "unknown" || s.role == "unknown" || s.role == entry.role))
                    {
                        var selected = slot;
                        menu.AddItem(new GUIContent(selected.Label.Replace("/", " ∕ ")), entry.material == selected.material && entry.property == selected.property,
                            () => { Undo.RecordObject(avatar,"My Avatar: choose slot"); entry.material = selected.material; entry.property = selected.property; TextureChanges.Dirty(avatar); root.Clear(); Populate(root,avatar,apply); });
                    }
                    menu.ShowAsContext();
                });
                card.Add(choose);
            }
            if (avatar.textures.Any(t => !t.applied && t.material)) root.Add(MyAvatarEditor.Button("Apply selected matches", apply));
            var applied = new Foldout { text = "Applied textures", value = false }; root.Add(applied);
            foreach (var entry in avatar.textures.Where(t => t.applied))
            { var line = new Label(entry.fileName + " → " + (entry.material ? entry.material.name : "Missing material") + " / " + entry.property); line.AddToClassList("applied-line"); applied.Add(line); }
        }
    }
}
