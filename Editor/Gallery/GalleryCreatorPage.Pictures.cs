using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    internal sealed partial class GalleryCreatorPage
    {
        // Square like the gallery's cards; the server keeps a 512 pixel copy for them and pages show it larger.
        private static readonly Vector2Int PictureSize = new Vector2Int(1024, 1024);
        private PhotoshootState photoshoot;
        private PhotoshootPanel studio;
        private bool studioOpen;
        private Button takeButton, sheetButton;
        private GalleryCard listingCard;
        private VisualElement picturesStrip, picturesStep, cardPlaceholder;
        private Texture livePicture;

        // ---- 4. Pictures ----

        private string PicturesSummary()
        {
            int pictures = (HasCardPicture() ? 1 : 0) + draft.previewPaths.Count(p => !GalleryPictures.IsSheet(p));
            int sheets = draft.previewPaths.Count(GalleryPictures.IsSheet);
            if (pictures + sheets == 0) return draft.newAsset ? "Optional" : "Keeps its pictures";
            var parts = new List<string>();
            if (pictures > 0) parts.Add(pictures + " picture" + (pictures == 1 ? "" : "s"));
            if (sheets > 0) parts.Add(sheets == 1 ? "ref sheet" : sheets + " ref sheets");
            return string.Join(" · ", parts);
        }

        private bool HasCardPicture() => !string.IsNullOrEmpty(draft.thumbnailPath) && File.Exists(draft.thumbnailPath);

        private void PicturesStep(VisualElement step)
        {
            picturesStep = step;
            Hint(avatar ? "Put it on " + avatar.name + ", then pose, light and frame them. The first picture is the one on the gallery card; the others, and ref sheets, open on its page."
                : "Open Publish to the gallery from My Avatar on an avatar wearing it to take its pictures here. You can still add picture files.", step);
            var row = GalleryUI.Box("creator-pictures", step);
            // The card buyers will see. While the photoshoot is open it follows the live camera, and the avatar is framed on it.
            listingCard = new GalleryCard(ListingPreview(), _ => { }, _ => { }) { tooltip = "How the gallery shows it to buyers." };
            listingCard.AddToClassList("creator-pictures__card");
            row.Add(listingCard);
            cardPlaceholder = GalleryUI.Box("creator-pictures__placeholder", listingCard.Media);
            cardPlaceholder.pickingMode = PickingMode.Ignore;
            var placeholderIcon = new VectorIcon(IconGlyph.Camera); placeholderIcon.AddToClassList("creator-pictures__placeholder-icon"); cardPlaceholder.Add(placeholderIcon);
            GalleryUI.Text("Its picture goes here", "creator-pictures__placeholder-text", cardPlaceholder).pickingMode = PickingMode.Ignore;
            var side = GalleryUI.Box("creator-pictures__side", row);
            picturesStrip = GalleryUI.Box("creator-pictures__strip", side);
            var actions = GalleryUI.Box("creator-actions creator-pictures__actions", side);
            takeButton = sheetButton = null;
            if (avatar)
            {
                takeButton = GalleryUI.Button("Take pictures", () => OpenStudio(false), "creator-pictures__take");
                actions.Add(takeButton);
            }
            var browse = GalleryUI.Button("Add a picture file…", BrowsePicture);
            browse.tooltip = "A PNG or JPEG picture of your own.";
            actions.Add(browse);
            if (avatar)
            {
                // Under them, a link to the other photoshoot: the avatar wearing it from the front, the back and the side.
                sheetButton = GalleryUI.Button("Create ref sheet", () => OpenStudio(true), "gallery-link creator-pictures__sheet");
                sheetButton.tooltip = "The avatar wearing it from the front, the back and the side on one 1920×1080 sheet, for its page.";
                side.Add(sheetButton);
            }
            RefreshStudioButtons();
            if (draft.assetId > 0)
            {
                var replace = new Toggle { text = "Replace its current previews instead of adding these before them", value = draft.replacePreviews };
                replace.AddToClassList("creator-rights");
                replace.RegisterValueChangedCallback(evt => { draft.replacePreviews = evt.newValue; draft.picturesSent = false; draft.Save(); });
                side.Add(replace);
            }
            RefreshPictures();
            if (studioOpen && avatar) step.Add(Studio());
        }

        private PhotoshootPanel Studio()
        {
            photoshoot ??= new PhotoshootState();
            studio = new PhotoshootPanel(photoshoot, new PhotoshootOptions
            {
                AvatarRoot = () => avatar ? avatar.gameObject : null,
                IncludeBanner = false,
                ThumbnailSize = PictureSize,
                ThumbnailLabel = "Picture",
                CanGenerate = () => !publishing,
                InputBlocked = () => publishing,
                // Every capture adds a picture; the panel keeps following the live camera.
                GetShot = _ => null,
                SetShot = (_, image) => AddPicture(image, false),
                RefSheet = sheet => AddPicture(sheet, true),
                ThumbnailPreview = texture => { livePicture = texture; ShowCardPicture(); },
                Changed = RefreshStudioButtons,
            });
            studio.AddToClassList("creator-studio");
            studio.AttachFraming(listingCard.Media, () => listingCard.Media.contentRect.size);
            return studio;
        }

        // Each button opens its photoshoot (pictures or ref sheet); once one is open, Take pictures (Done) closes it.
        private void OpenStudio(bool sheet)
        {
            if (studioOpen)
            {
                CloseStudio();
                Build();
                return;
            }
            studioOpen = true;
            photoshoot ??= new PhotoshootState();
            photoshoot.RefSheetOpen = sheet;
            Build();
            // The photoshoot opens under the card: bring it into view.
            picturesStep?.schedule.Execute(() => picturesStep?.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(picturesStep)).StartingIn(30);
        }

        private void RefreshStudioButtons()
        {
            bool sheet = studioOpen && photoshoot != null && photoshoot.RefSheetOpen;
            if (takeButton != null)
            {
                takeButton.text = studioOpen ? "Done" : "Take pictures";
                takeButton.tooltip = studioOpen ? (sheet ? "Close the ref sheet." : "Close the photoshoot.")
                    : "Pose, light and frame " + (avatar ? avatar.name : "the avatar") + " wearing it, then capture its pictures.";
                takeButton.EnableInClassList("mcb-button--primary", !studioOpen);
            }
            // Only a way in: the open photoshoot shows no ref sheet link.
            if (sheetButton != null) sheetButton.style.display = studioOpen ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void CloseStudio()
        {
            studioOpen = false;
            if (studio != null && listingCard != null) studio.DetachFraming(listingCard.Media);
            studio = null;
            livePicture = null;
            photoshoot?.ClosePreview();
        }

        private void AddPicture(Texture2D image, bool sheet)
        {
            if (image == null) return;
            string path;
            try { path = GalleryPictures.Save(image, sheet); }
            finally { if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(image))) UnityEngine.Object.DestroyImmediate(image); }
            AddPicturePath(path, sheet);
        }

        private void AddPicturePath(string path, bool pageOnly)
        {
            // The first picture goes on the card; the next ones and every ref sheet open on the asset page.
            if (!pageOnly && !HasCardPicture()) draft.thumbnailPath = path;
            else draft.previewPaths.Add(path);
            draft.picturesSent = false;
            draft.Save();
            RefreshPictures(path);
        }

        private void BrowsePicture()
        {
            string file = EditorUtility.OpenFilePanelWithFilters("Add a picture", "", new[] { "Pictures", "png,jpg,jpeg" });
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                var probe = new Texture2D(2, 2);
                bool wide;
                try { wide = probe.LoadImage(File.ReadAllBytes(file)) && probe.width > probe.height * WideRatio; }
                finally { UnityEngine.Object.DestroyImmediate(probe); }
                // A wide picture can't be cropped square for the card: it opens on the page, like a ref sheet.
                AddPicturePath(GalleryPictures.Import(file, false), wide);
            }
            catch (Exception ex) { Message("Could not add the picture: " + ex.Message, true); }
        }

        private void RemovePicture(string path)
        {
            if (path == draft.thumbnailPath)
            {
                // The next picture (never a ref sheet) moves onto the card.
                string next = draft.previewPaths.FirstOrDefault(p => !GalleryPictures.IsSheet(p) && !IsWide(p));
                draft.thumbnailPath = next;
                if (next != null) draft.previewPaths.Remove(next);
            }
            else draft.previewPaths.Remove(path);
            GalleryPictures.Delete(path);
            draft.picturesSent = false;
            draft.Save();
            RefreshPictures();
        }

        private void UseOnCard(string path)
        {
            if (path == draft.thumbnailPath || GalleryPictures.IsSheet(path)) return;
            int index = draft.previewPaths.IndexOf(path);
            if (index < 0) return;
            if (HasCardPicture()) draft.previewPaths[index] = draft.thumbnailPath;
            else draft.previewPaths.RemoveAt(index);
            draft.thumbnailPath = path;
            draft.picturesSent = false;
            draft.Save();
            RefreshPictures();
        }

        private static void SavePictureAs(string path)
        {
            string target = EditorUtility.SaveFilePanel("Save the picture", "", Path.GetFileName(path), Path.GetExtension(path).TrimStart('.'));
            if (string.IsNullOrEmpty(target)) return;
            File.Copy(path, target, true);
            EditorUtility.RevealInFinder(target);
        }

        private const float WideRatio = 1.4f;

        private static bool IsWide(string path)
        {
            var texture = GalleryPictures.Load(path);
            return texture && texture.width > texture.height * WideRatio;
        }

        // The strip and the card again, in place: the photoshoot stays open and keeps its framing.
        private void RefreshPictures(string added = null)
        {
            if (picturesStrip == null) return;
            draft.previewPaths.RemoveAll(p => string.IsNullOrEmpty(p) || !File.Exists(p));
            picturesStrip.Clear();
            var all = (HasCardPicture() ? new[] { draft.thumbnailPath } : new string[0]).Concat(draft.previewPaths).ToList();
            foreach (string path in all) picturesStrip.Add(Tile(path, path == draft.thumbnailPath, path == added));
            if (all.Count == 0)
            {
                var empty = GalleryUI.Box("creator-picture creator-picture--empty", picturesStrip);
                var icon = new VectorIcon(IconGlyph.Camera); icon.AddToClassList("creator-picture__empty-icon"); empty.Add(icon);
                GalleryUI.Text(avatar ? "No pictures yet" : "No pictures", "creator-picture__empty-text", empty);
                if (avatar) GalleryUI.Clickable(empty, () => { if (!studioOpen) OpenStudio(false); }, "creator-picture--pressed");
            }
            ShowCardPicture();
            var summary = picturesStep?.Q<Label>(className: "creator-step__summary");
            if (summary != null) summary.text = PicturesSummary();
            picturesStep?.EnableInClassList("creator-step--done", HasCardPicture());
            var number = picturesStep?.Q<Label>(className: "creator-step__number");
            if (number != null) number.text = HasCardPicture() ? "✓" : "4";
        }

        private VisualElement Tile(string path, bool card, bool added)
        {
            bool sheet = GalleryPictures.IsSheet(path) || IsWide(path);
            var tile = GalleryUI.Box("creator-picture", null);
            tile.EnableInClassList("creator-picture--sheet", sheet);
            tile.EnableInClassList("creator-picture--card", card);
            var image = new Image { image = GalleryPictures.Load(path), scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
            image.AddToClassList("creator-picture__image");
            tile.Add(image);
            if (card || GalleryPictures.IsSheet(path))
            {
                var badge = GalleryUI.Text(card ? "Card" : "Ref sheet", "creator-picture__badge", tile);
                badge.pickingMode = PickingMode.Ignore;
            }
            tile.tooltip = card ? "On the gallery card." : sheet ? "Opens on the asset page." : "Opens on the asset page. Click to put it on the card instead.";
            if (!card && !sheet) GalleryUI.Clickable(tile, () => UseOnCard(path), "creator-picture--pressed");
            var tools = GalleryUI.Box("creator-picture__tools", tile);
            tools.Add(GalleryUI.IconButton(IconGlyph.Download, "Save a copy…", () => SavePictureAs(path), "creator-picture__tool", "creator-picture__tool-icon"));
            var remove = GalleryUI.IconButton(IconGlyph.Close, "Remove this picture", () => RemovePicture(path), "creator-picture__tool", "creator-picture__tool-icon");
            remove.AddToClassList("creator-picture__tool--remove");
            tools.Add(remove);
            if (added)
            {
                // A new picture pops in where it lands.
                tile.AddToClassList("creator-picture--new");
                tile.schedule.Execute(() => tile.RemoveFromClassList("creator-picture--new")).StartingIn(20);
            }
            return tile;
        }

        private void ShowCardPicture()
        {
            if (listingCard == null) return;
            var picture = studioOpen && livePicture != null ? livePicture : HasCardPicture() ? GalleryPictures.Load(draft.thumbnailPath) : null;
            listingCard.ShowPicture(picture);
            if (cardPlaceholder != null) cardPlaceholder.style.display = picture == null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // The listing as the gallery will show it to buyers, from the draft and the signed-in creator.
        private GalleryAsset ListingPreview()
        {
            var auth = AuthenticationService.GetAuth();
            var existing = mine?.assets.FirstOrDefault(a => a.id == draft.assetId);
            bool free = existing != null ? existing.priceCents == 0 : draft.free;
            var report = draft.variants.Select(v => v.report).FirstOrDefault(r => r != null);
            return new GalleryAsset
            {
                id = draft.assetId,
                name = string.IsNullOrWhiteSpace(draft.name) ? "Your asset" : draft.name,
                type = draft.type,
                shortDescription = draft.shortDescription,
                creator = new GalleryCreator { username = string.IsNullOrEmpty(auth?.username) ? "you" : auth.username, avatarUrl = auth?.avatarUrl },
                price = new GalleryPrice { cents = existing?.priceCents ?? draft.priceCents, currency = existing?.currency ?? draft.currency, free = free },
                access = new GalleryAccess { state = free ? "free" : "purchase" },
                fit = new GalleryFit
                {
                    state = "compatible",
                    release = new GalleryRelease { version = draft.version, scope = draft.scope },
                    variants = new List<GalleryVariant> { new GalleryVariant { sizeBytes = report?.bytes ?? 0, parameterBits = report?.parameterBits ?? 0 } },
                },
            };
        }
    }
}
