using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Avatar thumbnail: a compact summary that opens the shared Orbiters photoshoot, thumbnail only, in VRChat's 4:3 format,
    // with an optional line of text placed right on the card.
    internal sealed class ThumbnailSection : VisualElement
    {
        private readonly MyAvatar avatar;
        private readonly PhotoshootState state;
        private readonly VrcAvatarCard card;
        private readonly Label status;
        private readonly Button create, openSdk, refSheet;
        private readonly VisualElement studio, cardStage, actions;
        private PhotoshootPanel studioPanel;
        private Texture live;

        internal ThumbnailSection(MyAvatar avatar, PhotoshootState state)
        {
            this.avatar = avatar; this.state = state;
            AddToClassList("thumbnail-section");
            var title = new Label("Thumbnail"); title.AddToClassList("section-title"); Add(title);

            // The thumbnail is always shown on the card it will appear on in VRChat, also while it is being made.
            card = new VrcAvatarCard();
            cardStage = new VrcCardStage(card); Add(cardStage);
            // The performance rank walks the whole avatar; it is measured once the Inspector has drawn.
            schedule.Execute(() => { if (avatar) card.ShowPerformance(avatar.gameObject); });
            // Same rhythm as the texture actions above: the secondary button, then the primary one taking two thirds.
            actions = new VisualElement(); actions.AddToClassList("thumbnail-actions"); Add(actions);
            openSdk = MyAvatarEditor.Button("Open in VRChat SDK", () => VrcSdkThumbnail.Open(avatar));
            openSdk.AddToClassList("thumbnail-actions__sdk");
            openSdk.tooltip = "Show the VRChat SDK panel on this avatar with this thumbnail filled in. It is filled in whenever the SDK shows this avatar.";
            actions.Add(openSdk);
            create = MyAvatarEditor.Button("Create thumbnail", () => Open(false)); create.AddToClassList("thumbnail-actions__main"); actions.Add(create);
            // Under them, a link to the other photoshoot: the avatar from the front, the back and the side on one sheet.
            refSheet = MyAvatarEditor.Button("Create ref sheet", () => Open(true));
            refSheet.AddToClassList("avatar-link"); refSheet.AddToClassList("thumbnail-refsheet");
            refSheet.tooltip = "Pose the avatar once and get it from the front, the back and the side on one 1920×1080 sheet, saved beside the thumbnails.";
            Add(refSheet);
            status = new Label(); status.AddToClassList("thumbnail-status"); Add(status);

            studio = new VisualElement(); studio.AddToClassList("thumbnail-studio"); Add(studio);
            RegisterCallback<DetachFromPanelEvent>(_ => Close());
            Refresh();
        }

        // Each button opens its photoshoot; once one is open, the main button (Done) closes it.
        private void Open(bool sheet)
        {
            if (studioPanel != null)
            {
                Close();
                return;
            }
            if (!avatar.GetComponentInChildren<Animator>(true))
            {
                status.text = "Add an Animator with a humanoid avatar to pose this avatar.";
                status.style.display = DisplayStyle.Flex;
                return;
            }
            state.RefSheetOpen = sheet;
            // The avatar's line of text, as it was left last time.
            string textKey = TextKey;
            state.Text = PhotoshootText.FromJson(EditorPrefs.GetString(textKey, "")) ?? new PhotoshootText();
            studioPanel = new PhotoshootPanel(state, new PhotoshootOptions
            {
                TextFonts = TextFonts(),
                TextChanged = text => EditorPrefs.SetString(textKey, text.ToJson()),
                AvatarRoot = () => avatar ? avatar.gameObject : null,
                IncludeBanner = false,
                ThumbnailSize = AvatarThumbnail.Size,
                // In the studio the card always follows the live camera; a capture is saved at once and the card flashes.
                GetShot = _ => null,
                SetShot = (_, image) => { Assign(image); if (image) card.Flash(); },
                Browsed = _ => Close(),
                ThumbnailPreview = texture => { live = texture; ShowCard(); },
                Changed = Refresh,
                RefSheet = SaveRefSheet,
            });
            studio.Add(studioPanel);
            // The card and Done lead the photoshoot: beside its style when the Inspector is wide.
            var lead = new VisualElement();
            lead.AddToClassList("thumbnail-lead");
            lead.Add(cardStage); lead.Add(actions); lead.Add(status);
            studioPanel.SetLead(lead);
            Refresh();
        }

        private void Close()
        {
            if (studioPanel == null) return;
            studioPanel.DetachFraming(card.Media);
            studioPanel.SetLead(null);
            Insert(IndexOf(refSheet), cardStage); Insert(IndexOf(refSheet), actions); Insert(IndexOf(refSheet) + 1, status);
            studioPanel.RemoveFromHierarchy(); studioPanel = null; live = null;
            state.ClosePreview();
            Refresh();
        }

        private const string FontFolder = "Packages/orbiters.myavatar/Editor/Fonts";
        private string TextKey => "Orbiters.MyAvatar.ThumbnailText." + GlobalObjectId.GetGlobalObjectIdSlow(avatar);

        // Unity's default font, then the ones My Avatar ships (Fonts folder, SIL Open Font License).
        private static IReadOnlyList<string> TextFonts() => new[] { "" }
            .Concat(AssetDatabase.FindAssets("t:Font", new[] { FontFolder }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Kept beside the thumbnails, named after the avatar, and shown in the Project window.
        private void SaveRefSheet(Texture2D sheet)
        {
            try
            {
                var saved = AvatarThumbnail.Save(avatar, sheet, "ref sheet");
                EditorGUIUtility.PingObject(saved);
                state.Status = "Ref sheet saved to " + AssetDatabase.GetAssetPath(saved);
            }
            finally { Object.DestroyImmediate(sheet); }
        }

        private void Assign(Texture2D image)
        {
            Undo.RecordObject(avatar, "My Avatar: thumbnail");
            if (image == null) avatar.thumbnail = null;
            else if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(image))) avatar.thumbnail = image;
            else
            {
                avatar.thumbnail = AvatarThumbnail.Save(avatar, image);
                Object.DestroyImmediate(image);
            }
            TextureChanges.Dirty(avatar);
        }

        private void Refresh()
        {
            bool has = avatar && avatar.thumbnail;
            ShowCard();
            status.text = "";
            status.style.display = DisplayStyle.None;
            bool open = studioPanel != null;
            create.text = open ? "Done" : has ? "Edit thumbnail" : "Create thumbnail";
            create.tooltip = open ? (studioPanel.RefSheetOpen ? "Close the ref sheet." : "Close the photoshoot.")
                : has ? "Open the photoshoot to capture a new thumbnail." : "Pose, light and frame the avatar, then capture a 4:3 VRChat thumbnail.";
            create.EnableInClassList("mcb-button--primary", !open);
            // Only a way in: the open photoshoot shows no ref sheet link.
            refSheet.style.display = open ? DisplayStyle.None : DisplayStyle.Flex;
            openSdk.style.display = has && !open ? DisplayStyle.Flex : DisplayStyle.None;
            // While the card follows the live preview, the avatar is framed right on it.
            studioPanel?.AttachFraming(card.Media, () =>
            {
                float width = card.Media.contentRect.width;
                return new Vector2(width, width * AvatarThumbnail.Size.y / AvatarThumbnail.Size.x);
            });
        }

        private void ShowCard()
        {
            if (!avatar) return;
            // The live camera while the studio shows it; the saved thumbnail otherwise (also during a ref sheet).
            Texture shown = studioPanel != null && live != null ? live : avatar.thumbnail;
            card.Show(avatar.gameObject.name, shown, studioPanel != null && live != null);
        }
    }
}
