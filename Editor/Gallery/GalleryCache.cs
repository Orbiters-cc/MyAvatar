using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// What the gallery already knows, so it opens at once: the avatar bases, whether the account can publish, each
    /// avatar's detected base, and the last cards shown for each avatar and filter. The entry card on the main page warms
    /// it; the gallery shows what it has, then refreshes in the background. Cleared when the account or server changes.
    /// </summary>
    internal static class GalleryCache
    {
        private static readonly TimeSpan BasesLife = TimeSpan.FromMinutes(10), ContextLife = TimeSpan.FromMinutes(5), PageLife = TimeSpan.FromMinutes(2);
        private static Task<List<GalleryBase>> bases;
        private static DateTime basesAt;
        private static (string token, Task<bool> task, DateTime at) creator;
        private static readonly Dictionary<string, (Task<GalleryAvatarContext> task, DateTime at)> contexts = new Dictionary<string, (Task<GalleryAvatarContext>, DateTime)>();
        private static readonly Dictionary<string, (GalleryPage page, DateTime at)> pages = new Dictionary<string, (GalleryPage, DateTime)>();

        [InitializeOnLoadMethod]
        private static void Listen()
        {
            AuthenticationService.Changed += Clear;
            OrbitersEnvironment.Changed += Clear;
        }

        internal static void Clear()
        {
            bases = null; creator = default;
            contexts.Clear(); pages.Clear();
        }

        internal static Task<List<GalleryBase>> Bases()
        {
            if (bases == null || bases.IsFaulted || bases.IsCanceled || DateTime.UtcNow - basesAt > BasesLife)
            {
                basesAt = DateTime.UtcNow;
                bases = LoadBases();
            }
            return bases;
        }

        private static async Task<List<GalleryBase>> LoadBases()
        {
            try { return (await GalleryApi.BasesAsync(CancellationToken.None))?.bases ?? new List<GalleryBase>(); }
            catch (Exception) { basesAt = DateTime.MinValue; return new List<GalleryBase>(); }
        }

        /// <summary>Whether this account may publish to the gallery (its creator tools are on).</summary>
        internal static Task<bool> IsCreator()
        {
            string token = GalleryApi.Token;
            if (string.IsNullOrEmpty(token)) return Task.FromResult(false);
            if (creator.task == null || creator.token != token || creator.task.IsFaulted || DateTime.UtcNow - creator.at > BasesLife)
                creator = (token, CheckCreator(), DateTime.UtcNow);
            return creator.task;
        }

        private static async Task<bool> CheckCreator()
        {
            try { return await GalleryApi.CreatorAssetsAsync(CancellationToken.None) != null; }
            catch (Exception) { return false; }
        }

        /// <summary>The avatar's context, detected once and reused for a few minutes (its parameter memory is measured again).</summary>
        internal static Task<GalleryAvatarContext> Context(MyAvatar avatar, bool refresh = false)
        {
            string key = GalleryInstaller.AvatarId(avatar) + "|" + avatar.GetInstanceID();
            if (!refresh && contexts.TryGetValue(key, out var known) && !known.task.IsFaulted && DateTime.UtcNow - known.at < ContextLife)
                return Remeasure(known.task, avatar);
            var task = Detect(avatar);
            contexts[key] = (task, DateTime.UtcNow);
            return task;
        }

        /// <summary>The context when it is already known, without waiting.</summary>
        internal static GalleryAvatarContext Known(MyAvatar avatar)
        {
            string key = GalleryInstaller.AvatarId(avatar) + "|" + avatar.GetInstanceID();
            return contexts.TryGetValue(key, out var known) && known.task.Status == TaskStatus.RanToCompletion ? known.task.Result : null;
        }

        private static async Task<GalleryAvatarContext> Detect(MyAvatar avatar) => await GalleryAvatarContext.DetectAsync(avatar, await Bases(), CancellationToken.None);

        private static async Task<GalleryAvatarContext> Remeasure(Task<GalleryAvatarContext> task, MyAvatar avatar)
        {
            var context = await task;
            if (context != null && avatar) context.Budget = Orbiters.Toolkit.Editor.VRChat.Parameters.AvatarParameterBudget.Estimate(avatar.gameObject);
            return context;
        }

        internal static string PageKey(MyAvatar avatar, GalleryAvatarContext context, string type, string query) =>
            GalleryInstaller.AvatarId(avatar) + "|" + context?.Platform + "|" + context?.BaseId + "|" + type + "|" + (query ?? "").Trim().ToLowerInvariant();

        internal static GalleryPage Page(string key) => pages.TryGetValue(key, out var known) && DateTime.UtcNow - known.at < PageLife ? known.page : null;

        internal static void Store(string key, GalleryPage page) { if (page != null) pages[key] = (page, DateTime.UtcNow); }

        /// <summary>Loads the avatar's first page in the background (the entry card shows its pictures).</summary>
        internal static async Task<GalleryPage> Warm(MyAvatar avatar)
        {
            var context = await Context(avatar);
            string key = PageKey(avatar, context, null, null);
            var cached = Page(key);
            if (cached != null) return cached;
            var page = await GalleryApi.ListAsync(context.Platform, context.BaseId, null, null, false, 0, CancellationToken.None);
            Store(key, page);
            _ = IsCreator();
            return page;
        }
    }
}
