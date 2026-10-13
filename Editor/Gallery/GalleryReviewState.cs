using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// An asset's likes and reviews as the gallery shows them, kept in step with Orbiters: every change shows at once
    /// (<see cref="Changed"/>), then is sent. Likes send only the latest wish; a failure puts back only what it changed, so
    /// likes, reviews and reloads made meanwhile stay.
    /// </summary>
    internal sealed class GalleryReviewState
    {
        /// <summary>Orbiters' interactions API (the asset's website page uses the same); replaced in tests.</summary>
        internal interface IApi
        {
            Task<AssetInteractions> Load(int assetId, int offset);
            Task<LikeState> SetLike(int assetId, bool liked);
            Task<LikeState> SetReviewLike(int assetId, int reviewId, bool liked);
            Task<GalleryReview> Post(int assetId, string content);
            Task<GalleryReview> Edit(int assetId, int reviewId, string content);
            Task<ReviewCount> Delete(int assetId, int reviewId);
        }

        private sealed class OrbitersApi : IApi
        {
            public Task<AssetInteractions> Load(int assetId, int offset) => GalleryApi.InteractionsAsync(assetId, offset, CancellationToken.None);
            public Task<LikeState> SetLike(int assetId, bool liked) => GalleryApi.SetLikeAsync(assetId, liked);
            public Task<LikeState> SetReviewLike(int assetId, int reviewId, bool liked) => GalleryApi.SetReviewLikeAsync(assetId, reviewId, liked);
            public Task<GalleryReview> Post(int assetId, string content) => GalleryApi.PostReviewAsync(assetId, content);
            public Task<GalleryReview> Edit(int assetId, int reviewId, string content) => GalleryApi.EditReviewAsync(assetId, reviewId, content);
            public Task<ReviewCount> Delete(int assetId, int reviewId) => GalleryApi.DeleteReviewAsync(assetId, reviewId);
        }

        internal static IApi Api = new OrbitersApi();

        // Per like (0: the asset's, else a review's id): what Orbiters holds, the latest wish, and whether a send runs.
        private sealed class LikeSync { public bool server, wanted, running; }

        private readonly int assetId;
        private readonly Dictionary<int, LikeSync> likes = new Dictionary<int, LikeSync>();

        internal AssetInteractions Data { get; private set; }
        /// <summary>What went wrong last, null once something worked again.</summary>
        internal string Message { get; private set; }
        /// <summary>The message is about loading: a Retry loads again.</summary>
        internal bool CanRetry { get; private set; }

        /// <summary>The reviews or their count changed: show them again.</summary>
        internal event Action Changed;
        /// <summary>The asset's like changed.</summary>
        internal event Action LikeChanged;
        /// <summary>A review's like changed (its row shows it in place).</summary>
        internal event Action<GalleryReview> ReviewLikeChanged;

        internal GalleryReviewState(int assetId) { this.assetId = assetId; }

        /// <summary>The review with this id as shown now (a reload replaces the objects).</summary>
        internal GalleryReview Find(int id) => Data == null ? null : new[] { Data.ownReview }.Concat(Data.reviews).FirstOrDefault(r => r != null && !r.pending && r.id == id);

        internal async Task LoadAsync(int offset)
        {
            try
            {
                var answer = await Api.Load(assetId, offset);
                if (offset > 0 && Data != null) { Data.reviews.AddRange(answer.reviews.Where(r => Data.reviews.All(shown => shown.id != r.id))); Data.hasMore = answer.hasMore; }
                else Data = answer;
                Say(null);
            }
            catch (Exception ex) { Say("Reviews could not be loaded: " + ex.Message, retry: true); }
            Changed?.Invoke();
            LikeChanged?.Invoke();
        }

        /// <summary>Posts a review, shown at once (pending) until Orbiters saved it. False when it was not posted.</summary>
        internal async Task<bool> PostAsync(string text, ReviewAuthor author)
        {
            string content = (text ?? "").Trim();
            if (content.Length == 0 || Data == null) return false;
            var shown = Data;
            var pending = new GalleryReview { pending = true, mine = true, content = content, createdAt = DateTime.UtcNow, author = author ?? new ReviewAuthor() };
            shown.ownReview = pending; shown.reviewCount++;
            Changed?.Invoke();
            try
            {
                var saved = await Api.Post(assetId, content);
                if (Data.ownReview == null || Data.ownReview == pending) Data.ownReview = saved;
                Say(null);
                return true;
            }
            catch (Exception ex)
            {
                if (shown.ownReview == pending) { shown.ownReview = null; shown.reviewCount = Math.Max(0, shown.reviewCount - 1); }
                Say("Your review was not posted: " + ex.Message);
                return false;
            }
            finally { Changed?.Invoke(); }
        }

        /// <summary>Saves an edited review, shown at once. False when Orbiters did not save it (the review shows its saved text again).</summary>
        internal async Task<bool> SaveAsync(GalleryReview review, string text)
        {
            string content = (text ?? "").Trim(), before = review.content;
            if (content.Length == 0 || content == before) return true;
            bool wasEdited = review.edited;
            review.content = content; review.edited = true;
            Changed?.Invoke();
            try
            {
                var saved = await Api.Edit(assetId, review.id, content);
                if (review.content == content && saved?.content != null) review.content = saved.content;
                Say(null);
                return true;
            }
            catch (Exception ex)
            {
                if (review.content == content) { review.content = before; review.edited = wasEdited; }
                Say("Your changes were not saved: " + ex.Message);
                return false;
            }
            finally { Changed?.Invoke(); }
        }

        /// <summary>Deletes a review, gone at once. When Orbiters refuses, only that review comes back.</summary>
        internal async Task<bool> DeleteAsync(GalleryReview review)
        {
            var shown = Data;
            if (shown == null) return false;
            bool own = shown.ownReview == review;
            int index = shown.reviews.IndexOf(review);
            if (own) shown.ownReview = null;
            if (index >= 0) shown.reviews.RemoveAt(index);
            if (own || index >= 0) shown.reviewCount = Math.Max(0, shown.reviewCount - 1);
            Changed?.Invoke();
            try
            {
                var answer = await Api.Delete(assetId, review.id);
                if (answer != null && Data == shown) shown.reviewCount = answer.reviewCount;
                Say(null);
                return true;
            }
            catch (Exception ex)
            {
                // Reloaded meanwhile: Orbiters' answer is shown as it is.
                if (Data == shown && Find(review.id) == null && (own || index >= 0))
                {
                    if (own && shown.ownReview == null) shown.ownReview = review;
                    else shown.reviews.Insert(Math.Max(0, Math.Min(index, shown.reviews.Count)), review);
                    shown.reviewCount++;
                }
                Say("Your review was not deleted: " + ex.Message);
                return false;
            }
            finally { Changed?.Invoke(); }
        }

        // ---- Likes ----

        internal void ToggleAssetLike()
        {
            if (Data == null) return;
            bool before = Data.likedByCurrentUser, liked = !before;
            Data.likeCount = Math.Max(0, Data.likeCount + (liked ? 1 : -1)); Data.likedByCurrentUser = liked;
            LikeChanged?.Invoke();
            Sync(0, before, liked, wanted => Api.SetLike(assetId, wanted), state =>
            {
                if (Data == null) return;
                Data.likedByCurrentUser = state.liked; Data.likeCount = state.likeCount;
                LikeChanged?.Invoke();
            }, server =>
            {
                if (Data == null || Data.likedByCurrentUser == server) return;
                Data.likeCount = Math.Max(0, Data.likeCount + (server ? 1 : -1)); Data.likedByCurrentUser = server;
                LikeChanged?.Invoke();
            });
        }

        internal void ToggleReviewLike(int reviewId)
        {
            var review = Find(reviewId);
            if (review == null || review.mine) return;
            bool before = review.likedByCurrentUser, liked = !before;
            ShowReviewLike(review, liked, Math.Max(0, review.likeCount + (liked ? 1 : -1)));
            Sync(reviewId, before, liked, wanted => Api.SetReviewLike(assetId, reviewId, wanted),
                state => { var now = Find(reviewId); if (now != null) ShowReviewLike(now, state.liked, state.likeCount); },
                server =>
                {
                    var now = Find(reviewId);
                    if (now != null && now.likedByCurrentUser != server) ShowReviewLike(now, server, Math.Max(0, now.likeCount + (server ? 1 : -1)));
                });
        }

        private void ShowReviewLike(GalleryReview review, bool liked, int likeCount)
        {
            review.likedByCurrentUser = liked; review.likeCount = likeCount;
            ReviewLikeChanged?.Invoke(review);
        }

        // Keeps sending until Orbiters holds the latest wish; quick double presses cost at most one request each way. While
        // no send runs, what was shown before this press is what Orbiters holds (a reload brought it), never a state kept from
        // an earlier send.
        private async void Sync(int key, bool before, bool wanted, Func<bool, Task<LikeState>> send, Action<LikeState> apply, Action<bool> restore)
        {
            if (!likes.TryGetValue(key, out var sync)) likes[key] = sync = new LikeSync();
            if (!sync.running) sync.server = before;
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
                Say(null);
            }
            catch (Exception ex) { sync.wanted = sync.server; restore(sync.server); Say("Your like was not saved: " + ex.Message); }
            finally { sync.running = false; }
        }

        private void Say(string text, bool retry = false)
        {
            if (Message == text && CanRetry == retry) return;
            Message = text; CanRetry = retry && text != null;
            Changed?.Invoke();
        }
    }
}
