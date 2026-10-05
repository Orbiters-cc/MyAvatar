using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// One gallery installation, saved after every step in Library (not SessionState): it survives script reloads and
    /// closing Unity, and resumes where it stopped once its avatar is back.
    /// </summary>
    internal sealed class GalleryJob
    {
        public const string Queued = "queued", Download = "download", Validate = "validate", Dependencies = "dependencies",
            Preview = "preview", Import = "import", Attach = "attach", Verify = "verify", Complete = "complete", Failed = "failed", Cancelled = "cancelled";
        public static readonly string[] Steps = { Download, Validate, Dependencies, Import, Attach, Verify, Complete };

        public string id, avatar, scenePath, avatarName;
        public int instance;
        public int assetId, releaseId, variantId, creatorId, parameterBits;
        public string assetName, version, setupKey, setupLabel, creatorName, sha256, thumbnail;
        public bool creatorTrusted;
        public long sizeBytes;
        public List<string> platforms = new List<string>();
        public List<GalleryDependency> dependencies = new List<GalleryDependency>();
        public GalleryManifest manifest = new GalleryManifest();
        public string stage = Queued;
        /// <summary>What the job waits for the user to decide: "budget", "code", "dependencies", "conflicts"; null otherwise.</summary>
        public string waiting;
        /// <summary>Details for the question (code files, files replaced, the dependency plan's lines).</summary>
        public List<string> questionLines = new List<string>();
        public string questionSummary;
        /// <summary>Questions the user accepted; a resumed job does not ask them again.</summary>
        public List<string> accepted = new List<string>();
        /// <summary>A step changing the project started: after a reload or restart it is checked, never run twice blindly.</summary>
        public bool importing, installingDependencies, cancelRequested;
        public string packagePath, importPath;
        public List<string> importGuids = new List<string>();
        public List<GalleryFile> preexisting = new List<GalleryFile>();
        public List<string> placed = new List<string>();
        /// <summary>The installation this one updates: its objects are removed once the new ones are in place.</summary>
        public string updateOf;
        public string error, status;
        public float progress;
        public long createdAt, updatedAt;

        [JsonIgnore] public bool Terminal => stage == Complete || stage == Failed || stage == Cancelled;
        [JsonIgnore] public bool Waiting => !string.IsNullOrEmpty(waiting);
        /// <summary>0..1 over the visible steps, for the card's progress.</summary>
        [JsonIgnore] public int StepIndex => Math.Max(0, Array.IndexOf(Steps, stage == Preview ? Import : stage == Queued ? Download : stage));
    }

    internal sealed class GalleryFile
    {
        public string guid, path, hash;
        public long bytes;
    }

    internal static class GalleryJournal
    {
        private const string FileName = "gallery-jobs.json";
        private static List<GalleryJob> jobs;
        internal static event Action<GalleryJob> Changed;

        private sealed class Store { public List<GalleryJob> jobs = new List<GalleryJob>(); }

        internal static IReadOnlyList<GalleryJob> All => Load();

        internal static List<GalleryJob> Load()
        {
            if (jobs != null) return jobs;
            jobs = LibraryStore.Read<Store>(FileName).jobs ?? new List<GalleryJob>();
            // Finished jobs are kept a day for the cards to show what happened, then forgotten.
            long old = DateTime.UtcNow.AddDays(-1).Ticks;
            jobs.RemoveAll(j => j == null || (j.Terminal && j.updatedAt < old));
            return jobs;
        }

        internal static void Save(GalleryJob job, bool notify = true)
        {
            var list = Load();
            job.updatedAt = DateTime.UtcNow.Ticks;
            if (!list.Contains(job)) { list.RemoveAll(j => j.id == job.id); list.Add(job); }
            LibraryStore.Write(FileName, new Store { jobs = list });
            if (notify) Changed?.Invoke(job);
        }

        internal static void Remove(GalleryJob job)
        {
            var list = Load();
            if (list.RemoveAll(j => j.id == job.id) == 0) return;
            LibraryStore.Write(FileName, new Store { jobs = list });
            Changed?.Invoke(job);
        }

        internal static GalleryJob For(string avatarId, int assetId) =>
            Load().Where(j => j.avatar == avatarId && j.assetId == assetId).OrderByDescending(j => j.updatedAt).FirstOrDefault();

        internal static void Notify(GalleryJob job) => Changed?.Invoke(job);

        /// <summary>Tests start from an empty journal.</summary>
        internal static void ResetForTests() { jobs = new List<GalleryJob>(); }
    }

    /// <summary>
    /// What each gallery installation brought into the project: the files it imported, their content when imported and
    /// whether they were there before. Cleanup reads it to tell the gallery's files from the user's and other tools'.
    /// </summary>
    internal static class GalleryLedger
    {
        private const string FileName = "gallery-ledger.json";

        internal sealed class Entry
        {
            public string installId, avatar, avatarName, assetName, version;
            public int assetId, releaseId, variantId;
            public long installedAt, removedAt;
            public List<GalleryFile> files = new List<GalleryFile>();
            public List<GalleryFile> preexisting = new List<GalleryFile>();
        }

        private sealed class Store { public List<Entry> entries = new List<Entry>(); }

        internal static List<Entry> Entries => LibraryStore.Read<Store>(FileName).entries ?? new List<Entry>();

        internal static void Record(Entry entry)
        {
            var store = LibraryStore.Read<Store>(FileName);
            store.entries.RemoveAll(e => e.installId == entry.installId);
            store.entries.Add(entry);
            LibraryStore.Write(FileName, store);
        }

        /// <summary>The installation's objects were removed from its avatar; its files stay until Cleanup.</summary>
        internal static void MarkRemoved(string installId)
        {
            if (string.IsNullOrEmpty(installId)) return;
            var store = LibraryStore.Read<Store>(FileName);
            foreach (var entry in store.entries.Where(e => e.installId == installId)) entry.removedAt = DateTime.UtcNow.Ticks;
            LibraryStore.Write(FileName, store);
        }

        internal static void Forget(IEnumerable<string> installIds)
        {
            var ids = new HashSet<string>(installIds);
            var store = LibraryStore.Read<Store>(FileName);
            if (store.entries.RemoveAll(e => ids.Contains(e.installId)) > 0) LibraryStore.Write(FileName, store);
        }

        internal static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);
    }
}
