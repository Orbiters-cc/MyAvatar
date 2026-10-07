using System;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// One asset of the gallery: its picture with what it costs (or that it is free, owned, included with a tier) and
    /// whether it fits the avatar, its creator, and More info beside the action that fits: Add, Buy, Installed, Update,
    /// a question to answer, Retry. While it installs, the action becomes the progress of its steps.
    /// </summary>
    internal sealed class GalleryCard : VisualElement
    {
        internal GalleryAsset Asset { get; private set; }
        internal GalleryUI.State State { get; private set; }
        /// <summary>Where the picture is shown.</summary>
        internal VisualElement Media => media;
        internal GalleryJob Job { get; private set; }
        internal OrbitersAttachment.GalleryReceipt Installed { get; private set; }

        private readonly Action<GalleryCard> info, primary;
        private readonly VisualElement media, chips, actions;
        private readonly Image image;
        private readonly Label title, creatorName, meta, status, accessChip, fitChip;
        private readonly Image creatorAvatar;
        private readonly VectorIcon trusted;
        private VisualElement shimmer;
        private GalleryUI.State shown = (GalleryUI.State)(-1);

        internal GalleryCard(GalleryAsset asset, Action<GalleryCard> info, Action<GalleryCard> primary)
        {
            this.info = info; this.primary = primary;
            AddToClassList("orb-card");
            AddToClassList("gallery-card");
            media = GalleryUI.Box("orb-card__media", this);
            image = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
            image.AddToClassList("gallery-card__image");
            media.Add(image);
            chips = GalleryUI.Box("gallery-card__chips", media);
            accessChip = GalleryUI.Text("", "gallery-chip", chips);
            fitChip = GalleryUI.Text("", "gallery-chip gallery-chip--warning", chips);
            media.RegisterCallback<ClickEvent>(_ => info(this));
            var body = GalleryUI.Box("gallery-card__body", this);
            title = GalleryUI.Text("", "gallery-card__title", body);
            var creator = GalleryUI.Box("gallery-creator", body);
            creatorAvatar = new Image { scaleMode = ScaleMode.ScaleAndCrop }; creatorAvatar.AddToClassList("gallery-creator__avatar"); creator.Add(creatorAvatar);
            creatorName = GalleryUI.Text("", "gallery-creator__name", creator);
            trusted = new VectorIcon(IconGlyph.Check) { tooltip = "Trusted creator: its code installs without a warning." };
            trusted.AddToClassList("gallery-creator__trusted"); creator.Add(trusted);
            meta = GalleryUI.Text("", "gallery-card__meta", body);
            actions = GalleryUI.Box("gallery-card__actions", body);
            status = GalleryUI.Text("", "gallery-card__status", body);
            Bind(asset, null, null);
        }

        internal void Bind(GalleryAsset asset, OrbitersAttachment.GalleryReceipt installed, GalleryJob job)
        {
            bool newPicture = Asset == null || Asset.thumbnail != asset.thumbnail;
            Asset = asset; Installed = installed; Job = job;
            title.text = asset.name;
            tooltip = string.IsNullOrEmpty(asset.shortDescription) ? asset.name : asset.name + "\n" + asset.shortDescription;
            creatorName.text = asset.creator?.username ?? "Unknown creator";
            trusted.style.display = asset.creator?.trusted == true ? DisplayStyle.Flex : DisplayStyle.None;
            if (!string.IsNullOrEmpty(asset.creator?.avatarUrl)) GalleryImages.Show(creatorAvatar, asset.creator.avatarUrl);
            if (newPicture)
            {
                image.RemoveFromClassList(GalleryImages.ShownClass);
                shimmer?.RemoveFromHierarchy();
                if (!string.IsNullOrEmpty(asset.thumbnail))
                {
                    shimmer = GalleryUI.Shimmer(media); shimmer.SendToBack(); GalleryImages.Show(image, asset.thumbnail);
                    // The placeholder's sweep stops once the picture is there.
                    var sweep = shimmer;
                    image.schedule.Execute(() => { if (image.ClassListContains(GalleryImages.ShownClass)) sweep.RemoveFromHierarchy(); }).Every(200).Until(() => sweep.parent == null);
                }
            }
            var (chipText, chipStyle) = GalleryUI.AccessChip(asset);
            accessChip.text = chipText;
            foreach (var style in new[] { "gallery-chip--free", "gallery-chip--owned", "gallery-chip--included" }) accessChip.EnableInClassList(style, style == chipStyle);
            var release = asset.fit?.release;
            var variant = asset.fit?.Compatible == true ? asset.fit.variants[0] : null;
            meta.text = release == null ? (asset.Clothing ? "Clothing" : "Accessory")
                : string.Join(" · ", new[] { release.version, variant != null && variant.sizeBytes > 0 ? GalleryUI.Size(variant.sizeBytes) : null, variant != null && variant.parameterBits > 0 ? variant.parameterBits + " bits" : null,
                    release.scope != "public" ? release.scope : null }.WithoutNulls());
            Refresh();
        }

        private void Refresh()
        {
            var state = GalleryUI.StateOf(Asset, Installed, Job, out string note);
            fitChip.text = state == GalleryUI.State.Unavailable ? note : "";
            fitChip.style.display = state == GalleryUI.State.Unavailable ? DisplayStyle.Flex : DisplayStyle.None;
            bool rebuild = state != shown || state == GalleryUI.State.Working;
            State = state;
            if (rebuild) BuildActions(state, note);
            string text = state == GalleryUI.State.Failed ? note : state == GalleryUI.State.Question ? Job?.questionSummary
                : state == GalleryUI.State.Update ? "Update available: " + note : Job != null && Job.stage == GalleryJob.Complete ? Job.status
                : Job != null && Job.stage == GalleryJob.Cancelled ? Job.status : null;
            status.text = text ?? "";
            status.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            status.EnableInClassList("error", state == GalleryUI.State.Failed);
            status.EnableInClassList("warning", state == GalleryUI.State.Question);
        }

        private Label progressLabel;
        private VisualElement progressFill;

        private void BuildActions(GalleryUI.State state, string note)
        {
            if (state == GalleryUI.State.Working && shown == GalleryUI.State.Working && progressFill != null)
            {
                progressFill.style.width = Length.Percent(Mathf.Clamp01(Job?.progress ?? 0f) * 100f);
                progressLabel.text = Job?.status ?? "Working…";
                return;
            }
            bool finishedNow = shown == GalleryUI.State.Working && state == GalleryUI.State.Installed;
            shown = state;
            actions.Clear();
            actions.Add(GalleryUI.Button("More info", () => info(this), "gallery-card__info"));
            switch (state)
            {
                case GalleryUI.State.Working:
                    var progress = GalleryUI.Box("gallery-progress", actions);
                    progressFill = GalleryUI.Box("gallery-progress__fill", progress);
                    progressFill.style.width = Length.Percent(Mathf.Clamp01(Job?.progress ?? 0f) * 100f);
                    progressLabel = GalleryUI.Text(Job?.status ?? "Working…", "gallery-progress__label", progress);
                    progress.tooltip = "Click to cancel";
                    progress.RegisterCallback<ClickEvent>(_ => primary(this));
                    return;
                case GalleryUI.State.Add: Primary("Add", "gallery-btn--add", "Download it and put it on this avatar."); break;
                case GalleryUI.State.Buy: Primary(Asset.price != null && !Asset.price.free ? "Buy · " + Asset.price.Label : "Buy", "gallery-btn--buy", "Choose a store; once bought it is added to your library."); break;
                case GalleryUI.State.Installed:
                    var installed = Primary("Installed", "gallery-btn--installed", "On this avatar (" + note + "). Click to remove it from the avatar; its files stay for Cleanup.");
                    installed.RegisterCallback<PointerEnterEvent>(_ => installed.text = "Remove");
                    installed.RegisterCallback<PointerLeaveEvent>(_ => installed.text = "Installed");
                    break;
                case GalleryUI.State.Update: Primary("Update", "gallery-btn--add", "Update to " + Asset.fit?.release?.version + ": the new version replaces this one once it is on."); break;
                case GalleryUI.State.Question: Primary("Review", "gallery-btn--question", Job?.questionSummary); break;
                case GalleryUI.State.Failed: Primary("Retry", "gallery-btn--question", note); break;
                default:
                    var off = Primary("Unavailable", "gallery-btn--off", Unavailable(Asset));
                    off.SetEnabled(false);
                    break;
            }
            if (finishedNow)
            {
                // A short pop when it lands on the avatar.
                AddToClassList("gallery-pop");
                schedule.Execute(() => RemoveFromClassList("gallery-pop")).StartingIn(180);
            }
        }

        /// <summary>Shows a picture that isn't on Orbiters yet (a creator's listing as buyers will see it).</summary>
        internal void ShowPicture(Texture picture)
        {
            shimmer?.RemoveFromHierarchy();
            shimmer = null;
            image.image = picture;
            image.EnableInClassList(GalleryImages.ShownClass, picture != null);
            image.MarkDirtyRepaint();
        }

        // Why it does not fit, in a sentence for the tooltip.
        private static string Unavailable(GalleryAsset asset)
        {
            switch (asset.fit?.state)
            {
                case "platform": return "No package for the platform this project builds for (" + AvatarPlatform.Label(AvatarPlatform.Current) + "). It is made for " + string.Join(" and ", asset.fit.platforms.Select(AvatarPlatform.Label)) + ".";
                case "base": return "Made for other avatar bases than this avatar's. More info lists them.";
                case "unknown-base": return "Made for specific avatar bases, and this avatar's base is unknown: choose it above.";
                case "none": return "No version is published yet.";
                default: return "Not available for this avatar.";
            }
        }

        private Button Primary(string text, string style, string hint)
        {
            var button = GalleryUI.Button(text, () => primary(this), "gallery-card__primary " + style);
            button.tooltip = hint;
            actions.Add(button);
            return button;
        }
    }

    internal static class GalleryEnumerable
    {
        internal static System.Collections.Generic.IEnumerable<string> WithoutNulls(this System.Collections.Generic.IEnumerable<string> values)
        {
            foreach (var value in values) if (!string.IsNullOrEmpty(value)) yield return value;
        }
    }
}
