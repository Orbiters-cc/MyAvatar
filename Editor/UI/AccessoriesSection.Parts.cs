using System.Linq;
using Orbiters.MyAvatar.Editor.Gallery;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // A placed item's customizable parts on its card (Toolkit's ItemParts): a row per part with the variant it uses now
    // (its picture and name) and Browse, which opens the part's gallery. A part being switched shows its progress in its row.
    internal sealed partial class AccessoriesSection
    {
        private void Customize(VisualElement body, OrbitersAttachment attachment)
        {
            var parts = ItemParts.Of(attachment.gameObject).Where(p => p.Item == attachment.transform).ToList();
            if (parts.Count == 0) return;
            var section = new VisualElement(); section.AddToClassList("parts-section"); body.Add(section);
            var sheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>(PartGalleryPage.StyleSheetPath);
            if (sheet != null) section.styleSheets.Add(sheet);
            var title = new Label("Customize"); title.AddToClassList("parts-section__title"); section.Add(title);
            foreach (var placed in parts) section.Add(PartRow(placed));
            string note = ItemParts.Note(attachment.transform, parts[0].Definition);
            if (!string.IsNullOrEmpty(note))
            {
                var text = new Label(note); text.AddToClassList("parts-section__note"); section.Add(text);
            }
        }

        private VisualElement PartRow(ItemParts.Placed placed)
        {
            var row = new VisualElement { tooltip = placed.Part.description }; row.AddToClassList("parts-row");
            var picture = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore }; picture.AddToClassList("parts-row__picture"); row.Add(picture);
            var text = new VisualElement(); text.AddToClassList("parts-row__text"); row.Add(text);
            var label = new Label(placed.Part.Label); label.AddToClassList("parts-row__label"); text.Add(label);
            var name = new Label(); name.AddToClassList("parts-row__name"); text.Add(name);
            var progress = new VisualElement(); progress.AddToClassList("parts-row__progress"); text.Add(progress);
            var fill = new VisualElement(); fill.AddToClassList("parts-row__fill"); progress.Add(fill);
            var item = placed.Item;
            var browse = Small("Browse", () => PartGalleryWindow.Open(avatar, item, placed.Part.key));
            browse.tooltip = "Explore the variants of " + placed.Part.Label.ToLowerInvariant() + " and switch to one, or make your own.";
            row.Add(browse);

            void Show()
            {
                if (row.panel == null && row.parent != null) return;
                var current = placed.Current;
                var job = PartVariants.JobOf(item, placed.Part.key);
                bool working = job != null && job.Running;
                name.text = working ? job.Name + " · " + job.Status : job != null && job.Failed ? job.Error : current == null ? "" :
                    current.name + (current.IsBase || current.official ? " · by the creator" : !string.IsNullOrEmpty(current.author) ? " · by " + current.author : "");
                name.EnableInClassList("parts-row__name--error", job != null && job.Failed);
                progress.style.display = working ? DisplayStyle.Flex : DisplayStyle.None;
                fill.style.width = Length.Percent(Mathf.Clamp01(job?.Progress ?? 0) * 100);
                picture.image = PartVariants.CurrentThumbnail(placed, () => row.schedule.Execute(Show));
            }
            void Changed(PartVariants.Job job)
            {
                if (job.Item != item.GetInstanceID() || job.Part != placed.Part.key) return;
                // Done: what the item uses now is read back from it.
                if (!job.Running && item != null) placed.Current = ItemParts.Current(item, placed.Definition, placed.Part);
                Show();
            }
            row.RegisterCallback<AttachToPanelEvent>(_ => PartVariants.Changed += Changed);
            row.RegisterCallback<DetachFromPanelEvent>(_ => PartVariants.Changed -= Changed);
            Show();
            return row;
        }
    }
}
