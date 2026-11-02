using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// The real size of the avatar's last build on each platform, kept apart from the estimate: when the SDK builds it
    /// anyway (Build &amp; Test, Build &amp; Publish), the bundle's length on disk, with what the estimate said for the same
    /// settings then. Nothing is built for it and the estimate never takes it in. Kept in Library per avatar (its blueprint
    /// ID, or its scene and name before the first upload) and platform.
    /// </summary>
    internal static class MeasuredBuilds
    {
        internal sealed class Record
        {
            /// <summary>Which avatar (<see cref="AvatarKey"/>) and platform ("pc", "android", "ios").</summary>
            public string Avatar, Platform;
            /// <summary>The bundle the SDK built, as the file on disk (VRChat shows about 5% less).</summary>
            public long Bytes;
            public long WhenTicks;
            /// <summary>What the estimate showed for the same settings when it was built (its download as VRChat shows it), or -1.</summary>
            public long Estimate = -1;
            /// <summary>The estimate's fingerprint then, and the editor session it is from (fingerprints hold for one session).</summary>
            public string Fingerprint, Session;

            public DateTime When => new DateTime(WhenTicks, DateTimeKind.Utc);
        }

        internal const string FileName = "measured-builds.json";
        // Builds kept, the newest: one per avatar and platform.
        private const int Kept = 64;
        private const string SessionIdKey = "Orbiters.MyAvatar.MeasuredBuilds.Session";

        /// <summary>This editor session (kept across script reloads, new when Unity starts).</summary>
        internal static string Session
        {
            get
            {
                string id = SessionState.GetString(SessionIdKey, "");
                if (id.Length == 0) SessionState.SetString(SessionIdKey, id = Guid.NewGuid().ToString("N"));
                return id;
            }
        }

        [InitializeOnLoadMethod]
        private static void Listen()
        {
            VrcSdkBuilds.Starting -= Starting;
            VrcSdkBuilds.Starting += Starting;
            VrcSdkBuilds.Succeeded -= Built;
            VrcSdkBuilds.Succeeded += Built;
        }

        // What the estimate said as the build started, before the SDK's build touched the scene.
        private static (GameObject Root, string Fingerprint, OptimizationEstimate.Snapshot Upload)? atStart;

        internal static void Starting(GameObject root)
        {
            var avatar = root != null ? root.GetComponentInChildren<MyAvatar>(true) : null;
            atStart = avatar ? (root, OptimizationEstimate.Fingerprint(avatar), OptimizationEstimate.Known(avatar, avatar.uploadOptimization)?.Upload) : ((GameObject, string, OptimizationEstimate.Snapshot)?)null;
        }

        /// <summary>Raised after a build was kept (the Optimization card shows it).</summary>
        internal static event Action Changed;

        internal static void Built(VrcSdkBuilds.Build build)
        {
            if (build == null || build.Bytes <= 0) return;
            var root = build.Avatar;
            var avatar = root != null ? root.GetComponentInChildren<MyAvatar>(true) : null;
            var record = new Record
            {
                Avatar = AvatarKey(root, build.BlueprintId, build.AvatarName), Platform = build.Platform, Bytes = build.Bytes, WhenTicks = build.When.Ticks,
                Session = Session,
            };
            if (avatar)
            {
                var start = atStart.HasValue && atStart.Value.Root == root ? atStart : null;
                atStart = null;
                record.Fingerprint = start?.Fingerprint ?? OptimizationEstimate.Fingerprint(avatar);
                var shown = start != null ? start.Value.Upload : OptimizationEstimate.Known(avatar, avatar.uploadOptimization)?.Upload;
                // The estimate is measured for the platform the project builds for: the one just built.
                if (shown != null) record.Estimate = build.Platform == AvatarPlatform.Pc ? shown.Download : shown.QuestDownload;
            }
            Keep(record);
        }

        /// <summary>Keeps <paramref name="record"/> in place of the last one of its avatar and platform.</summary>
        internal static void Keep(Record record)
        {
            var all = LibraryStore.Read<List<Record>>(FileName);
            all.RemoveAll(r => r == null || r.Avatar == record.Avatar && r.Platform == record.Platform);
            all.Add(record);
            LibraryStore.Write(FileName, all.OrderByDescending(r => r.WhenTicks).Take(Kept).ToList());
            Changed?.Invoke();
        }

        /// <summary>The last build of the avatar on the platform (by its blueprint ID, else as it was known before its first upload), or null.</summary>
        internal static Record For(GameObject root, string platform)
        {
            if (root == null) return null;
            var all = LibraryStore.Read<List<Record>>(FileName);
            string blueprint = VrcSdkBuilds.BlueprintOf(root), unpublished = AvatarKey(root, "", root.name);
            return all.Where(r => r != null && r.Platform == platform && (blueprint.Length > 0 && r.Avatar == blueprint || r.Avatar == unpublished))
                .OrderByDescending(r => r.WhenTicks).FirstOrDefault();
        }

        /// <summary>
        /// Whether the avatar or its settings changed since the build: true or false when that is known (a build of this editor
        /// session), null otherwise.
        /// </summary>
        internal static bool? Stale(Record record, string fingerprint) =>
            record == null || record.Session != Session || string.IsNullOrEmpty(record.Fingerprint) ? (bool?)null : record.Fingerprint != fingerprint;

        /// <summary>The avatar by its blueprint ID, else by its scene and name (an avatar not uploaded yet).</summary>
        internal static string AvatarKey(GameObject root, string blueprintId, string name) =>
            !string.IsNullOrEmpty(blueprintId) ? blueprintId : "scene:" + (root != null ? root.scene.path : "") + "/" + name;
    }
}
