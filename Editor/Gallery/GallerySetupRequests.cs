using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// My Avatar's part of Orbiters - N4N0's setup requests (Toolkit's <see cref="ProjectSetupRequests"/>, contracts'
    /// Docs/project-setup.md): a gallery item installed on the avatar exactly as its Install button in the gallery does,
    /// My Avatar added to the avatar first when it has none. The job runs in My Avatar's journal, so its questions show on
    /// the avatar's My Avatar Inspector and it carries on after domain reloads; the request's token leads to the job
    /// through SessionState until the job exists.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class GallerySetupRequests : ProjectSetupRequests.IGalleryItems
    {
        private const string Pending = "Orbiters.MyAvatar.SetupRequest.";
        private static readonly Dictionary<string, Task> Starting = new Dictionary<string, Task>();

        static GallerySetupRequests() => ProjectSetupRequests.Gallery = new GallerySetupRequests();

        public string Start(GameObject root, int assetId, string version)
        {
            var avatar = root.GetComponent<MyAvatar>() ?? Undo.AddComponent<MyAvatar>(root);
            string token = "request-" + Guid.NewGuid().ToString("N");
            Starting[token] = StartAsync(token, avatar, assetId, version);
            return token;
        }

        private static async Task StartAsync(string token, MyAvatar avatar, int assetId, string version)
        {
            try
            {
                var asset = await GalleryApi.DetailAsync(assetId, AvatarPlatform.Current, null, CancellationToken.None);
                var release = asset?.fit?.release;
                if (release == null || asset.fit.variants == null || asset.fit.variants.Count == 0 || !asset.fit.Compatible)
                    throw new InvalidOperationException((asset?.name ?? "This item") + " has no version for this avatar on the gallery.");
                if (!string.IsNullOrEmpty(version) && version != release.version)
                    throw new InvalidOperationException($"{asset.name} {version} is no longer offered; its latest is {release.version}.");
                var variant = asset.fit.variants[0];
                var job = GalleryInstaller.Start(avatar, asset, release, variant, variant.manifest?.defaultSetup);
                SessionState.SetString(Pending + token, job.id);
                // Its questions, if any, are asked on the avatar's My Avatar Inspector.
                Selection.activeGameObject = avatar.gameObject;
            }
            catch (Exception ex)
            {
                SessionState.SetString(Pending + token, "failed:" + ex.Message);
            }
        }

        public (string state, string message, string version) Status(string token)
        {
            string id = SessionState.GetString(Pending + token, null);
            if (id == null) return Starting.TryGetValue(token, out var task) && !task.IsCompleted ? ("running", null, null)
                : ("failed", "Unity reloaded before the item started installing: ask Orbiters - N4N0 again.", null);
            if (id.StartsWith("failed:", StringComparison.Ordinal)) return ("failed", id.Substring("failed:".Length), null);
            var job = GalleryJournal.All.FirstOrDefault(j => j.id == id);
            if (job == null) return ("failed", "My Avatar lost track of the installation: ask Orbiters - N4N0 again.", null);
            if (!job.Terminal) return ("running", job.status, null);
            if (job.stage == GalleryJob.Complete) return ("done", job.assetName + " " + job.version, job.version);
            return ("failed", job.stage == GalleryJob.Cancelled ? job.assetName + " was not added: the installation was cancelled." : job.error ?? job.status, null);
        }

        public void Show(string token)
        {
            string id = SessionState.GetString(Pending + token, null);
            var job = id == null ? null : GalleryJournal.All.FirstOrDefault(j => j.id == id);
            var avatar = job != null ? GalleryInstaller.FindAvatar(job) : null;
            if (avatar == null) return;
            Selection.activeGameObject = avatar.gameObject;
            EditorGUIUtility.PingObject(avatar.gameObject);
        }
    }
}
