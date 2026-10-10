using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// An asset's reviews at the end of its details, and its like pill for the title row: the same likes and reviews as
    /// on its Orbiters page. Every action shows at once; likes sync in the background, sending only the latest wish, and a
    /// failure puts back what Orbiters holds.
    /// </summary>
    internal sealed class GalleryReviews : VisualElement, GalleryReviewRow.IHost
    {
        internal const int MaxLength = 2000;
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/Gallery/gallery-reviews.uss";
        private readonly GalleryAsset asset;
        private readonly Action<int> likesChanged;
        private readonly Label count, messageText;
        private readonly Label likeCount;
        private readonly VisualElement composer, list, more, message;
        private AssetInteractions data;
        private GalleryReview editing;
        private string draft = "";
        private readonly Dictionary<int, LikeSync> likes = new Dictionary<int, LikeSync>();

        /// <summary>The asset's likes, for the details' title row.</summary>
        internal Button Like { get; }

        public bool SignedIn => !string.IsNullOrEmpty(GalleryApi.Token) && data?.viewer?.signedIn != false;

        internal GalleryReviews(GalleryAsset asset, Action<int> likesChanged)
        {
            this.asset = asset; this.likesChanged = likesChanged;
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
            OnPress(Like, ToggleAssetLike);
            GalleryUI.Box("reviews__skeleton", list);
            RegisterCallback<AttachToPanelEvent>(evt => { if (data == null) _ = LoadAsync(0); });
        }

        private static void OnPress(Button button, Action action) => Orbiters.Toolkit.Editor.ButtonInteraction.RegisterImmediateClick(button, action);

        private async Task LoadAsync(int offset)
        {
            try
            {
                var answer = await GalleryApi.InteractionsAsync(asset.id, offset, CancellationToken.None);
                if (offset > 0 && data != null) { data.reviews.AddRange(answer.reviews.Where(r => data.reviews.All(shown => shown.id != r.id))); data.hasMore = answer.hasMore; }
                else data = answer;
                ShowMessage(null);
            }
            catch (Exception ex) { ShowMessage("Reviews could not be loaded: " + ex.Message, retry: true); }
            Render();
        }

        private void Render()
        {
            list.Clear(); composer.Clear(); more.Clear();
            if (data == null) return;
            count.text = data.reviewCount > 0 ? data.reviewCount.ToString(CultureInfo.InvariantCulture) : "";
            ShowLike();
            if (!SignedIn) GalleryUI.Text("Sign in to Orbiters at the top of My Avatar to like and review.", "reviews__hint", composer);
            else if (data.viewer.canReview && data.ownReview == null) Composer();
            foreach (var review in new[] { data.ownReview }.Concat(data.reviews).Where(r => r != null))
                list.Add(new GalleryReviewRow(review, this, editing != null && editing.id == review.id && !review.pending));
            if (list.childCount == 0) GalleryUI.Text("No reviews yet.", "reviews__hint", list);
            if (data.hasMore) more.Add(GalleryUI.Button("Show more reviews", () => { more.Clear(); _ = LoadAsync(data.reviews.Count); }, "gallery-link"));
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
            string content = (text ?? "").Trim();
            if (content.Length == 0 || data == null) return;
            var auth = AuthenticationService.GetAuth();
            var pending = new GalleryReview { pending = true, mine = true, content = content, createdAt = DateTime.UtcNow,
                author = new ReviewAuthor { username = auth?.username, avatarUrl = auth?.avatarUrl } };
            data.ownReview = pending; data.reviewCount++; draft = "";
            Render();
            try { data.ownReview = await GalleryApi.PostReviewAsync(asset.id, content); ShowMessage(null); }
            catch (Exception ex)
            {
                if (data.ownReview == pending) data.ownReview = null;
                data.reviewCount = Math.Max(0, data.reviewCount - 1); draft = content;
                ShowMessage("Your review was not posted: " + ex.Message);
            }
            Render();
        }

        public void Edit(GalleryReview review) { editing = review; Render(); }

        public async void Save(GalleryReview review, string text)
        {
            string content = (text ?? "").Trim(), before = review.content;
            bool wasEdited = review.edited;
            editing = null;
            if (content.Length == 0 || content == before) { Render(); return; }
            review.content = content; review.edited = true;
            Render();
            try
            {
                var saved = await GalleryApi.EditReviewAsync(asset.id, review.id, content);
                review.content = saved?.content ?? content; ShowMessage(null);
            }
            catch (Exception ex) { review.content = before; review.edited = wasEdited; ShowMessage("Your changes were not saved: " + ex.Message); }
            Render();
        }

        public async void Delete(GalleryReview review)
        {
            var before = data;
            data = new AssetInteractions { assetId = data.assetId, likeCount = data.likeCount, likedByCurrentUser = data.likedByCurrentUser, hasMore = data.hasMore,
                reviewCount = Math.Max(0, data.reviewCount - 1), viewer = data.viewer, ownReview = data.ownReview == review ? null : data.ownReview,
                reviews = data.reviews.Where(r => r != review).ToList() };
            Render();
            try { var answer = await GalleryApi.DeleteReviewAsync(asset.id, review.id); data.reviewCount = answer?.reviewCount ?? data.reviewCount; ShowMessage(null); }
            catch (Exception ex) { data = before; ShowMessage("Your review was not deleted: " + ex.Message); }
            Render();
        }

        // ---- Likes ----

        private sealed class LikeSync { public bool server, wanted, running; }

        private void ToggleAssetLike()
        {
            if (data == null || !SignedIn) return;
            bool liked = !data.likedByCurrentUser;
            data.likeCount = Math.Max(0, data.likeCount + (liked ? 1 : -1)); data.likedByCurrentUser = liked;
            ShowLike();
            Sync(0, !liked, liked, wanted => GalleryApi.SetLikeAsync(asset.id, wanted),
                state => { data.likedByCurrentUser = state.liked; data.likeCount = state.likeCount; ShowLike(); },
                server => { if (data.likedByCurrentUser != server) { data.likeCount = Math.Max(0, data.likeCount + (server ? 1 : -1)); data.likedByCurrentUser = server; } ShowLike(); });
        }

        public void ToggleLike(GalleryReview review)
        {
            if (!SignedIn || review.mine || review.pending) return;
            bool liked = !review.likedByCurrentUser;
            ShowReviewLike(review, liked, Math.Max(0, review.likeCount + (liked ? 1 : -1)));
            Sync(review.id, !liked, liked, wanted => GalleryApi.SetReviewLikeAsync(asset.id, review.id, wanted),
                state => ShowReviewLike(review, state.liked, state.likeCount),
                server => { if (review.likedByCurrentUser != server) ShowReviewLike(review, server, Math.Max(0, review.likeCount + (server ? 1 : -1))); });
        }

        private void ShowReviewLike(GalleryReview review, bool liked, int likeCount)
        {
            review.likedByCurrentUser = liked; review.likeCount = likeCount;
            Row(review)?.ShowLike(liked, likeCount);
        }

        private GalleryReviewRow Row(GalleryReview review) => list.Children().OfType<GalleryReviewRow>().FirstOrDefault(row => row.Review == review);

        // Keeps sending until Orbiters holds the latest wish; quick double presses cost at most one request each way.
        private async void Sync(int key, bool before, bool wanted, Func<bool, Task<LikeState>> send, Action<LikeState> apply, Action<bool> restore)
        {
            if (!likes.TryGetValue(key, out var sync)) likes[key] = sync = new LikeSync { server = before };
            sync.wanted = wanted;
            if (sync.running) return;
            sync.running = true;
            try
            {
                while (sync.wanted != sync.server)
                {
                    var state = await send(sync.wanted);
                    sync.server = state.liked;
                    if (sync.wanted == sync.server) apply(state);
                }
                ShowMessage(null);
            }
            catch (Exception ex) { sync.wanted = sync.server; restore(sync.server); ShowMessage("Your like was not saved: " + ex.Message); }
            finally { sync.running = false; }
        }

        private void ShowLike()
        {
            if (data == null) return;
            Like.SetEnabled(SignedIn);
            Like.EnableInClassList("review-like--liked", data.likedByCurrentUser);
            Like.tooltip = !SignedIn ? "Sign in to Orbiters at the top of My Avatar to like assets." : data.likedByCurrentUser ? "Unlike" : "Like";
            likeCount.text = data.likeCount.ToString(CultureInfo.InvariantCulture);
            likesChanged?.Invoke(data.likeCount);
        }

        private void ShowMessage(string text, bool retry = false)
        {
            messageText.text = text ?? "";
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var button in message.Query<Button>().ToList()) button.RemoveFromHierarchy();
            if (retry) message.Add(GalleryUI.Button("Retry", () => { ShowMessage(null); _ = LoadAsync(0); }, "gallery-link"));
        }
    }
}
