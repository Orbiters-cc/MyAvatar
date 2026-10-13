using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// An asset's reviews at the end of its details, and its like pill for the title row: the same likes and reviews as
    /// on its Orbiters page. Every action shows at once; <see cref="GalleryReviewState"/> sends it and puts back what a
    /// failure changed. What is being written (a new review, an edit) stays in its field while the list shows again.
    /// </summary>
    internal sealed class GalleryReviews : VisualElement, GalleryReviewRow.IHost
    {
        internal const int MaxLength = 2000;
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/Gallery/gallery-reviews.uss";
        private readonly GalleryReviewState state;
        private readonly Action<int> likesChanged;
        private readonly Label count, messageText;
        private readonly Label likeCount;
        private readonly VisualElement composer, list, more, message;
        private string draft = "";
        private bool composing;
        // The review being edited (by id: a reload replaces the objects) and what its field holds.
        private int? editing;
        private string editText;

        /// <summary>The asset's likes, for the details' title row.</summary>
        internal Button Like { get; }

        private AssetInteractions Data => state.Data;
        internal GalleryReviewState State => state;

        public bool SignedIn => !string.IsNullOrEmpty(GalleryApi.Token) && Data?.viewer?.signedIn != false;

        internal GalleryReviews(GalleryAsset asset, Action<int> likesChanged)
        {
            this.likesChanged = likesChanged;
            state = new GalleryReviewState(asset.id);
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("reviews");
            var header = GalleryUI.Box("reviews__header", this);
            GalleryUI.Text("Reviews", "detail-section", header);
            count = GalleryUI.Text("", "reviews__count", header);
            composer = GalleryUI.Box("reviews__composer", this);
            list = GalleryUI.Box("reviews__list", this);
            more = GalleryUI.Box("reviews__more", this);
            message = GalleryUI.Box("reviews__message", this);
            messageText = GalleryUI.Text("", "reviews__message-text", message);
            message.style.display = DisplayStyle.None;
            Like = GalleryReviewRow.LikePill(false, asset.likeCount, small: false, out likeCount);
            if (sheet) Like.styleSheets.Add(sheet);
            Like.SetEnabled(false);
            Orbiters.Toolkit.Editor.ButtonInteraction.RegisterImmediateClick(Like, () => { if (SignedIn) state.ToggleAssetLike(); });
            GalleryUI.Box("reviews__skeleton", list);
            state.Changed += Render;
            state.LikeChanged += ShowLike;
            state.ReviewLikeChanged += review => Row(review)?.ShowLike(review.likedByCurrentUser, review.likeCount);
            RegisterCallback<AttachToPanelEvent>(evt => { if (Data == null) _ = state.LoadAsync(0); });
        }

        // What is being written stays as it is while the list shows again: the composer and the row being edited are kept
        // (their fields keep the cursor, the selection and what an input method is composing); the rest is made again.
        private void Render()
        {
            ShowMessage();
            more.Clear();
            if (Data == null) { list.Clear(); composer.Clear(); composing = false; return; }
            count.text = Data.reviewCount > 0 ? Data.reviewCount.ToString(CultureInfo.InvariantCulture) : "";
            ShowLike();
            bool compose = SignedIn && Data.viewer.canReview && Data.ownReview == null;
            if (!compose || !composing) { composer.Clear(); composing = false; }
            if (!SignedIn) GalleryUI.Text("Sign in to Orbiters at the top of My Avatar to like and review.", "reviews__hint", composer);
            else if (compose && !composing) { Composer(); composing = true; }
            var kept = editing != null ? list.Children().OfType<GalleryReviewRow>().FirstOrDefault(r => r.Editing && r.Review.id == editing) : null;
            foreach (var child in list.Children().ToList()) if (child != kept) child.RemoveFromHierarchy();
            bool beforeKept = kept != null, keptShown = false;
            foreach (var review in new[] { Data.ownReview }.Concat(Data.reviews).Where(r => r != null))
            {
                if (kept != null && review.id == kept.Review.id && !review.pending) { beforeKept = false; keptShown = true; continue; }
                var row = new GalleryReviewRow(review, this, editing == review.id && !review.pending ? editText ?? review.content ?? "" : null);
                if (beforeKept) list.Insert(list.IndexOf(kept), row); else list.Add(row);
            }
            if (kept != null && !keptShown) kept.RemoveFromHierarchy();
            if (list.childCount == 0) GalleryUI.Text("No reviews yet.", "reviews__hint", list);
            if (Data.hasMore) more.Add(GalleryUI.Button("Show more reviews", () => { more.Clear(); _ = state.LoadAsync(Data.reviews.Count); }, "gallery-link"));
        }

        // "Write a review" opens into a text field with Post; the review shows at once, dimmed until Orbiters saved it.
        private void Composer()
        {
            var field = new TextField { multiline = true, maxLength = MaxLength, value = draft };
            field.AddToClassList("review-field");
            var hint = GalleryUI.Text("Write a review…", "gallery-search__hint review-field__hint", field);
            hint.pickingMode = PickingMode.Ignore;
            hint.style.display = string.IsNullOrEmpty(draft) ? DisplayStyle.Flex : DisplayStyle.None;
            composer.Add(field);
            var actions = GalleryUI.Box("review-field__actions", composer);
            var post = GalleryUI.Button("Post review", () => Post(field.value), "gallery-btn--add");
            post.SetEnabled(!string.IsNullOrWhiteSpace(draft));
            actions.Add(post);
            actions.style.display = string.IsNullOrEmpty(draft) ? DisplayStyle.None : DisplayStyle.Flex;
            field.RegisterValueChangedCallback(evt =>
            {
                draft = evt.newValue ?? "";
                hint.style.display = draft.Length == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                actions.style.display = draft.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
                post.SetEnabled(!string.IsNullOrWhiteSpace(draft));
            });
        }

        private async void Post(string text)
        {
            if (Data == null || string.IsNullOrWhiteSpace(text)) return;
            var auth = AuthenticationService.GetAuth();
            draft = "";
            // Not posted: what was written comes back in the field.
            if (!await state.PostAsync(text, new ReviewAuthor { username = auth?.username, avatarUrl = auth?.avatarUrl }))
            {
                draft = text; composing = false;
                Render();
            }
        }

        public void Edit(GalleryReview review)
        {
            editing = review?.id; editText = review?.content;
            // Opened or closed: that row is made again.
            foreach (var row in list.Children().OfType<GalleryReviewRow>().Where(r => r.Editing).ToList()) row.RemoveFromHierarchy();
            Render();
        }

        public void Typed(string content) => editText = content;

        public async void Save(GalleryReview review, string text)
        {
            // The review as shown now: a reload while editing replaced it.
            review = state.Find(review.id) ?? review;
            editing = null; editText = null;
            // Not saved: the editor opens again with what was typed.
            if (!await state.SaveAsync(review, text))
            {
                editing = review.id; editText = text;
                Render();
            }
            else Render();
        }

        public void Delete(GalleryReview review) => _ = state.DeleteAsync(review);

        public void ToggleLike(GalleryReview review)
        {
            if (SignedIn && !review.pending) state.ToggleReviewLike(review.id);
        }

        private GalleryReviewRow Row(GalleryReview review) => list.Children().OfType<GalleryReviewRow>().FirstOrDefault(row => row.Review == review || row.Review.id == review.id && !row.Review.pending);

        private void ShowLike()
        {
            if (Data == null) return;
            Like.SetEnabled(SignedIn);
            Like.EnableInClassList("review-like--liked", Data.likedByCurrentUser);
            Like.tooltip = !SignedIn ? "Sign in to Orbiters at the top of My Avatar to like assets." : Data.likedByCurrentUser ? "Unlike" : "Like";
            likeCount.text = Data.likeCount.ToString(CultureInfo.InvariantCulture);
            likesChanged?.Invoke(Data.likeCount);
        }

        private void ShowMessage()
        {
            string text = state.Message;
            messageText.text = text ?? "";
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var button in message.Query<Button>().ToList()) button.RemoveFromHierarchy();
            if (state.CanRetry) message.Add(GalleryUI.Button("Retry", () => { message.style.display = DisplayStyle.None; _ = state.LoadAsync(0); }, "gallery-link"));
        }
    }
}
