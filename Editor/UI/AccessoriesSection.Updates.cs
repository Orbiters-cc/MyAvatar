using Orbiters.MyAvatar.Editor.Gallery;
using Orbiters.Toolkit.VRChat;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // A gallery asset on the avatar offers its newer version right in the list: its version pill shows "1.0.1 → 1.0.3" and
    // an Update button installs it in place of this one (the gallery page's own update), with its progress meanwhile.
    internal sealed partial class AccessoriesSection
    {
        private void GalleryUpdate(VisualElement top, OrbitersAttachment item)
        {
            var installed = item.gallery;
            var pill = top.Q<Label>(className: "accessory-item__gallery");
            System.Action click = null;
            var button = Small("Update", () => click());
            button.AddToClassList("accessory-button--update");
            button.style.display = DisplayStyle.None;
            top.Add(button);
            GalleryAsset offered = null;

            void Show(GalleryJob job)
            {
                if (job != null && job.updateOf == installed.installId && !job.Terminal)
                {
                    button.text = "Updating…"; button.tooltip = job.status; button.SetEnabled(false); button.style.display = DisplayStyle.Flex;
                    return;
                }
                if (job != null && job.updateOf == installed.installId && job.stage == GalleryJob.Failed)
                {
                    button.text = "Retry update"; button.tooltip = job.error; button.SetEnabled(true); button.style.display = DisplayStyle.Flex;
                    return;
                }
                string newer = GalleryUI.Newer(offered, installed);
                button.style.display = newer != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (newer == null) return;
                button.text = "Update"; button.SetEnabled(true);
                button.tooltip = "Update to " + newer + ": the new version replaces this one once it is on.";
                if (pill != null) { pill.text = installed.version + " → " + newer; pill.AddToClassList("accessory-item__gallery--update"); }
            }

            click = () =>
            {
                var job = Current();
                if (job != null && job.stage == GalleryJob.Failed && job.updateOf == installed.installId) { GalleryInstaller.Retry(job); return; }
                if (GalleryUI.Newer(offered, installed) == null || GalleryInstaller.Busy(avatar)) return;
                // Seen at once; the installation runs in the background and replaces this row when it is done.
                button.text = "Updating…"; button.SetEnabled(false);
                var started = GalleryInstaller.Update(avatar, offered, installed);
                if (started == null) { button.style.display = DisplayStyle.None; return; }
                Show(started);
            };

            GalleryJob Current() => GalleryJournal.For(GalleryInstaller.AvatarId(avatar), installed.assetId);
            void JobChanged(GalleryJob job) { if (job != null && job.assetId == installed.assetId) Show(job); }
            // Listening while on screen: docked again (or the Inspector rebuilt), it listens again and catches up.
            GalleryJournal.Changed += JobChanged;
            button.RegisterCallback<AttachToPanelEvent>(_ => { GalleryJournal.Changed -= JobChanged; GalleryJournal.Changed += JobChanged; Show(Current()); });
            button.RegisterCallback<DetachFromPanelEvent>(_ => GalleryJournal.Changed -= JobChanged);
            Show(Current());
            Load();

            async void Load()
            {
                // Often already known: then this runs before the row is even on screen.
                offered = await GalleryCache.Asset(avatar, installed.assetId);
                Show(Current());
            }
        }
    }
}
