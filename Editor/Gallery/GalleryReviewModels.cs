using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    // An asset's likes and reviews, as Orbiters answers them for the website's asset page too
    // (backend routes/assetInteractionsRouter.js, /assets/:id/interactions). Field names follow the JSON.
    internal sealed class AssetInteractions
    {
        public int assetId, likeCount, reviewCount, offset, limit;
        public bool likedByCurrentUser, hasMore;
        public GalleryReview ownReview;
        public List<GalleryReview> reviews = new List<GalleryReview>();
        public ReviewViewer viewer = new ReviewViewer();
    }

    internal sealed class ReviewViewer
    {
        public bool signedIn, canLike, canReview, canModerate;
    }

    internal sealed class ReviewAuthor
    {
        public int? id;
        public string username, avatarUrl;
        public bool deleted;
    }

    internal sealed class GalleryReview
    {
        public int id, likeCount;
        public string content;
        public DateTime? createdAt;
        public bool edited, ownsAsset, isCreator, likedByCurrentUser, mine, canRemove;
        public ReviewAuthor author = new ReviewAuthor();
        /// <summary>Shown before Orbiters saved it.</summary>
        [JsonIgnore] public bool pending;
    }

    internal sealed class LikeState
    {
        public bool liked;
        public int likeCount;
    }

    internal sealed class ReviewCount
    {
        public int reviewCount;
    }
}
