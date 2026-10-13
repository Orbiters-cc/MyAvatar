using System;
using System.Globalization;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// One review in an asset's details: its author's face and name, "Owns it" when they own the asset, when, the text (as
    /// written: reviews are plain text) and its likes. The author edits it in place, and deletes it with a second press,
    /// like a version's Withdraw.
    /// </summary>
    internal sealed class GalleryReviewRow : VisualElement
    {
        internal interface IHost
        {
            bool SignedIn { get; }
            void ToggleLike(GalleryReview review);
            /// <summary>Opens the review in place for editing, or closes it (null).</summary>
            void Edit(GalleryReview review);
            /// <summary>What the field being edited holds now: kept while the list shows again.</summary>
            void Typed(string content);
            void Save(GalleryReview review, string content);
            void Delete(GalleryReview review);
        }

        internal GalleryReview Review { get; }
        /// <summary>The review is open for editing here: the list keeps this row as it is while it shows again.</summary>
        internal bool Editing { get; }
        private readonly IHost host;
        private Button like;
        private Label likeCount;

        /// <param name="editing">The text of the review being edited, null when it is shown.</param>
        internal GalleryReviewRow(GalleryReview review, IHost host, string editing = null)
        {
            Review = review; this.host = host; Editing = editing != null;
            AddToClassList("review");
            EnableInClassList("review--pending", review.pending);
            var face = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            face.AddToClassList("review__avatar");
            if (!string.IsNullOrEmpty(review.author?.avatarUrl)) GalleryImages.ShowAvatar(face, review.author.avatarUrl);
            Add(face);
            var body = GalleryUI.Box("review__body", this);
            var header = GalleryUI.Box("review__header", body);
            GalleryUI.Text(review.author?.username ?? "Orbiters member", "review__author", header);
            if (review.isCreator) GalleryUI.Text("Creator", "review__badge review__badge--creator", header);
            else if (review.ownsAsset) GalleryUI.Text("Owns it", "review__badge", header).tooltip = "Owns this asset.";
            GalleryUI.Text(review.pending ? "Posting…" : Ago(review.createdAt) + (review.edited ? " · edited" : ""), "review__meta", header);
            if (editing != null) { Editor(body, editing); return; }
            GalleryUI.Text(review.content, "review__text", body);
            var footer = GalleryUI.Box("review__footer", body);
            like = LikePill(review.likedByCurrentUser, review.likeCount, small: true, out likeCount);
            like.tooltip = review.mine ? "Likes of your review" : !host.SignedIn ? "Sign in to Orbiters at the top of My Avatar to like reviews." : review.likedByCurrentUser ? "Unlike" : "Like";
            like.SetEnabled(host.SignedIn && !review.mine && !review.pending);
            ButtonInteraction.RegisterImmediateClick(like, () => host.ToggleLike(Review));
            footer.Add(like);
            if (!review.mine || review.pending) return;
            footer.Add(Tool(GalleryReviewIcon.Kind.Edit, "Edit your review", () => host.Edit(Review), "review__tool"));
            GalleryUI.TwoPress press = null;
            var delete = Tool(GalleryReviewIcon.Kind.Delete, "Delete your review", () => press.Press(), "review__tool review__tool--delete");
            var confirm = GalleryUI.Text("Delete?", "review__tool-label");
            press = new GalleryUI.TwoPress(delete, armed =>
            {
                delete.EnableInClassList("review__tool--armed", armed);
                if (armed) { if (confirm.parent != delete) delete.Add(confirm); }
                else confirm.RemoveFromHierarchy();
                delete.tooltip = armed ? "Press again to delete your review." : "Delete your review";
            }, () => host.Delete(Review));
            footer.Add(delete);
        }

        private void Editor(VisualElement body, string text)
        {
            var field = new TextField { multiline = true, maxLength = GalleryReviews.MaxLength, value = text };
            field.AddToClassList("review-field");
            body.Add(field);
            var actions = GalleryUI.Box("review-field__actions", body);
            actions.Add(GalleryUI.Button("Cancel", () => host.Edit(null), "gallery-link"));
            var save = GalleryUI.Button("Save", () => host.Save(Review, field.value), "gallery-btn--add");
            save.SetEnabled(!string.IsNullOrWhiteSpace(text));
            field.RegisterValueChangedCallback(evt => { host.Typed(evt.newValue ?? ""); save.SetEnabled(!string.IsNullOrWhiteSpace(evt.newValue)); });
            actions.Add(save);
            field.schedule.Execute(() => { field.Focus(); field.SelectRange(field.value.Length, field.value.Length); });
        }

        private static Button Tool(GalleryReviewIcon.Kind kind, string tooltip, Action action, string className)
        {
            var button = new Button { tooltip = tooltip };
            foreach (var name in className.Split(' ')) button.AddToClassList(name);
            button.Add(new GalleryReviewIcon(kind));
            ButtonInteraction.RegisterImmediateClick(button, action);
            return button;
        }

        /// <summary>Shows a like the user just gave or took back, before Orbiters answers.</summary>
        internal void ShowLike(bool liked, int count)
        {
            Review.likedByCurrentUser = liked; Review.likeCount = count;
            if (like == null) return;
            like.EnableInClassList("review-like--liked", liked);
            likeCount.text = count.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The thumb up with its count, as MCB shows a custom base's likes.</summary>
        internal static Button LikePill(bool liked, int count, bool small, out Label countLabel)
        {
            var pill = new Button();
            pill.AddToClassList("review-like");
            pill.EnableInClassList("review-like--small", small);
            pill.EnableInClassList("review-like--liked", liked);
            pill.Add(new GalleryReviewIcon(GalleryReviewIcon.Kind.Like));
            countLabel = GalleryUI.Text(count.ToString(CultureInfo.InvariantCulture), "review-like__count", pill);
            return pill;
        }

        internal static string Ago(DateTime? at)
        {
            if (!at.HasValue) return "";
            var span = DateTime.UtcNow - at.Value.ToUniversalTime();
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return (int)span.TotalMinutes + " min ago";
            if (span.TotalDays < 1) return (int)span.TotalHours + " h ago";
            if (span.TotalDays < 30) return (int)span.TotalDays + (span.TotalDays < 2 ? " day ago" : " days ago");
            return at.Value.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }
    }
}
