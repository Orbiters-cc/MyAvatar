using System;
using System.Collections.Generic;
using Orbiters.Toolkit.VRChat;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// A gallery asset on an avatar and its newer version: what the gallery offers it now, its update running or failed,
    /// and starting that update. The asset's line in My Avatar's list and other views of the avatar (Mixer's Hierarchy)
    /// share it.
    /// </summary>
    internal static class GalleryUpdates
    {
        // A read that ended (even a failed one, offline) is not tried again for this long: views ask on every refresh.
        private static readonly TimeSpan ReadAgainAfter = TimeSpan.FromMinutes(2);
        private static readonly HashSet<string> reading = new HashSet<string>();
        private static readonly Dictionary<string, DateTime> lastRead = new Dictionary<string, DateTime>();

        /// <summary>Raised when a read in the background changed what the gallery offers an asset (its newer version).</summary>
        internal static event Action Read;

        /// <summary>
        /// The asset as the gallery offers it to the avatar, from what is known now (null until a first read ends); an old
        /// answer is read again in the background, and <see cref="Read"/> follows when it changed.
        /// </summary>
        internal static GalleryAsset Known(MyAvatar avatar, int assetId)
        {
            var offered = GalleryCache.Peek(avatar, assetId, out bool fresh);
            string key = avatar.GetInstanceID() + "|" + assetId;
            if (!fresh && !reading.Contains(key) && (!lastRead.TryGetValue(key, out var at) || DateTime.UtcNow - at >= ReadAgainAfter))
                ReadAgain(avatar, assetId, key, Offered(offered));
            return offered;
        }

        /// <summary>
        /// Reads again what the gallery offers each of its assets on the avatar (the gallery's reload button), so its lines
        /// in My Avatar and Mixer's Hierarchy show a version published a moment ago without waiting for their next read.
        /// </summary>
        internal static void ReadAgainNow(MyAvatar avatar)
        {
            GalleryCache.Expire();
            var assetIds = new HashSet<int>();
            foreach (var (receipt, _) in GalleryInstaller.Installed(avatar))
            {
                string key = avatar.GetInstanceID() + "|" + receipt.assetId;
                if (assetIds.Add(receipt.assetId) && !reading.Contains(key))
                    ReadAgain(avatar, receipt.assetId, key, Offered(GalleryCache.Peek(avatar, receipt.assetId, out _)));
            }
        }

        private static async void ReadAgain(MyAvatar avatar, int assetId, string key, string before)
        {
            reading.Add(key);
            lastRead[key] = DateTime.UtcNow;
            GalleryAsset offered;
            try { offered = await GalleryCache.Asset(avatar, assetId); }
            finally { reading.Remove(key); }
            if (Offered(offered) != before) Read?.Invoke();
        }

        // What a view shows of an offer: its newest version and whether it fits.
        private static string Offered(GalleryAsset asset) => asset?.fit == null ? null : asset.fit.state + "|" + asset.fit.release?.version;

        /// <summary>The job updating this installation (under way, failed or just done), or null.</summary>
        internal static GalleryJob Job(MyAvatar avatar, OrbitersAttachment.GalleryReceipt installed)
        {
            var job = GalleryJournal.For(GalleryInstaller.AvatarId(avatar), installed.assetId);
            return job != null && job.updateOf == installed.installId ? job : null;
        }

        /// <summary>
        /// Updates the installation to the version <paramref name="offered"/> (in the background, replacing it once the new one
        /// is on), or tries its failed update again. Null when nothing started: no newer version, or the avatar is busy.
        /// </summary>
        internal static GalleryJob Start(MyAvatar avatar, GalleryAsset offered, OrbitersAttachment.GalleryReceipt installed)
        {
            var job = Job(avatar, installed);
            if (job != null && job.stage == GalleryJob.Failed)
            {
                GalleryInstaller.Retry(job);
                return job;
            }
            if (GalleryUI.Newer(offered, installed) == null || GalleryInstaller.Busy(avatar)) return null;
            return GalleryInstaller.Update(avatar, offered, installed);
        }
    }
}
