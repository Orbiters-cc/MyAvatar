using System;
using System.Linq;
using System.Threading;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// The way into the asset gallery from My Avatar's main page: a glowing card with three of the newest assets fanned
    /// out, which spread when hovered. Pressing it opens the gallery.
    /// </summary>
    internal sealed class GalleryHero : VisualElement
    {
        private readonly VisualElement[] tiles = new VisualElement[3];
        private readonly Label subtitle;
        private CancellationTokenSource loading;

        private readonly MyAvatar avatar;

        internal GalleryHero(MyAvatar avatar, Action open)
        {
            this.avatar = avatar;
            if (GalleryUI.Sheet) styleSheets.Add(GalleryUI.Sheet);
            AddToClassList("gallery-hero");
            tooltip = "Browse clothes and accessories made for your avatar and add them in one click.";
            var glow = new OrbitersGlowSurfaceElement(new Color(0.08f, 0.067f, 0.114f), 0f, 118f);
            glow.AddToClassList("gallery-hero__glow");
            Add(glow);
            var content = GalleryUI.Box("gallery-hero__content", this);
            var texts = GalleryUI.Box("gallery-hero__texts", content);
            GalleryUI.Text("ASSET GALLERY", "gallery-hero__eyebrow", texts);
            GalleryUI.Text("Dress up your avatar", "gallery-hero__title", texts);
            subtitle = GalleryUI.Text("Clothes and accessories from Orbiters creators, ready in one click.", "gallery-hero__subtitle", texts);
            var fan = GalleryUI.Box("gallery-hero__fan", content);
            for (int i = 0; i < tiles.Length; i++)
            {
                var tile = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
                tile.AddToClassList("gallery-hero__tile");
                tile.AddToClassList("gallery-hero__tile--" + i);
                tile.AddToClassList("gallery-hero__tile--placeholder");
                tiles[i] = tile;
                fan.Add(tile);
            }
            var chevron = new VectorIcon(IconGlyph.Chevron); chevron.AddToClassList("gallery-hero__chevron"); content.Add(chevron);
            GalleryUI.Clickable(this, open, "gallery-hero--pressed");
            RegisterCallback<AttachToPanelEvent>(_ => Load());
            RegisterCallback<DetachFromPanelEvent>(_ => loading?.Cancel());
        }

        private void SetBase(string baseName)
        {
            subtitle.text = string.IsNullOrEmpty(baseName)
                ? "Clothes and accessories from Orbiters creators, ready in one click."
                : $"Clothes and accessories that fit {baseName}, ready in one click.";
        }

        // The newest gallery pictures fill the fan; it keeps its placeholders when offline.
        private async void Load()
        {
            loading?.Cancel();
            var source = loading = new CancellationTokenSource();
            try
            {
                // Warming the gallery for this avatar: opening it is then immediate.
                var page = await GalleryCache.Warm(avatar);
                if (source.IsCancellationRequested) return;
                SetBase(GalleryCache.Known(avatar)?.BaseId != null ? GalleryCache.Known(avatar).BaseName : null);
                var pictures = page?.assets?.Where(a => !string.IsNullOrEmpty(a.thumbnail)).Select(a => a.thumbnail).Take(3).ToList();
                for (int i = 0; pictures != null && i < pictures.Count; i++)
                {
                    tiles[i].RemoveFromClassList("gallery-hero__tile--placeholder");
                    GalleryImages.Show((Image)tiles[i], pictures[i]);
                }
            }
            catch (Exception) { /* The card stays as it is: the gallery itself reports connection problems. */ }
        }
    }
}
