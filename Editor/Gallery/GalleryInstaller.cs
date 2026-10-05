using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
#if MYAVATAR_VPM
using Orbiters.Toolkit.Editor.Vpm;
#endif

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Installs gallery assets: download (staged in Library, outside Assets), validate (checksum, safe paths, code from
    /// untrusted creators), dependencies (the complete VPM plan), preview (what the import would replace), import, attach
    /// (exactly what the creator's manifest declares), verify. Each step is saved in the <see cref="GalleryJournal"/> first;
    /// questions wait for the user's answer in My Avatar; a reload or restart resumes the job once its avatar is back.
    /// Unity's native import cannot be undone file by file, so nothing here promises an atomic install: a failed attach
    /// reports the imported files, which Cleanup can remove.
    /// </summary>
    internal static partial class GalleryInstaller
    {
        private static readonly HashSet<string> Running = new HashSet<string>();
        private static readonly Dictionary<string, CancellationTokenSource> Cancellations = new Dictionary<string, CancellationTokenSource>();

        /// <summary>Replaced in tests: the server's dependency catalog and the download.</summary>
        internal static Func<CancellationToken, Task<KnownCatalog>> KnownDependencies = GalleryApi.KnownDependenciesAsync;
        internal static Func<GalleryJob, string, Toolkit.Editor.Net.OrbitersTransfer.Progress, CancellationToken, Task<Toolkit.Editor.Net.OrbitersTransfer.Result>> Download =
            (job, path, progress, cancellation) => GalleryApi.DownloadAsync(job.assetId, job.variantId, path, job.sha256, progress, cancellation);

        internal static bool Busy(MyAvatar avatar)
        {
            if (!avatar) return false;
            string id = AvatarId(avatar);
            return GalleryJournal.All.Any(j => j.avatar == id && !j.Terminal && !j.Waiting && Running.Contains(j.id));
        }

        internal static string AvatarId(MyAvatar avatar) => GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString();

        internal static MyAvatar FindAvatar(GalleryJob job)
        {
            var avatar = EditorUtility.InstanceIDToObject(job.instance) as MyAvatar;
            if (avatar && AvatarId(avatar) == job.avatar) return avatar;
            return GlobalObjectId.TryParse(job.avatar, out var id) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as MyAvatar : null;
        }

        /// <summary>Starts installing a package of a gallery asset on the avatar (or updating an installation).</summary>
        internal static GalleryJob Start(MyAvatar avatar, GalleryAsset asset, GalleryRelease release, GalleryVariant variant, string setupKey, string updateOf = null)
        {
            var setup = variant.manifest?.Setup(setupKey);
            var job = new GalleryJob
            {
                id = Guid.NewGuid().ToString("N"), avatar = AvatarId(avatar), instance = avatar.GetInstanceID(), avatarName = avatar.name,
                scenePath = avatar.gameObject.scene.path,
                assetId = asset.id, releaseId = release.id, variantId = variant.id, assetName = asset.name, version = release.version,
                creatorId = asset.creator?.id ?? 0, creatorName = asset.creator?.username, creatorTrusted = asset.creator?.trusted == true,
                thumbnail = asset.thumbnail, sha256 = variant.sha256, sizeBytes = variant.sizeBytes, parameterBits = variant.parameterBits,
                platforms = variant.platforms ?? new List<string>(), dependencies = variant.dependencies ?? new List<GalleryDependency>(),
                manifest = variant.manifest ?? new GalleryManifest(), setupKey = setup?.key, setupLabel = setup?.label, updateOf = updateOf,
                createdAt = DateTime.UtcNow.Ticks, status = "Starting…",
            };
            // A new install of the same asset replaces an old failed or cancelled one on this avatar.
            foreach (var old in GalleryJournal.All.Where(j => j.avatar == job.avatar && j.assetId == asset.id && j.Terminal).ToList()) GalleryJournal.Remove(old);
            GalleryJournal.Save(job);
            _ = RunAsync(job);
            return job;
        }

        /// <summary>
        /// A creator's test of a package before publishing it: the same steps as a gallery installation from the built
        /// package (no download), so what buyers get is what the creator saw working on this avatar.
        /// </summary>
        internal static GalleryJob StartLocal(MyAvatar avatar, string name, string packagePath, string sha256, GalleryManifest manifest, string setupKey,
            List<GalleryDependency> dependencies, int parameterBits)
        {
            var setup = manifest?.Setup(setupKey);
            var job = new GalleryJob
            {
                id = Guid.NewGuid().ToString("N"), avatar = AvatarId(avatar), instance = avatar.GetInstanceID(), avatarName = avatar.name,
                scenePath = avatar.gameObject.scene.path, assetName = name + " (test)", version = "test", sha256 = sha256,
                creatorId = ContentTrust.SignedInUserId(), creatorTrusted = true, packagePath = packagePath, parameterBits = parameterBits,
                platforms = new List<string> { AvatarPlatform.Current }, dependencies = dependencies ?? new List<GalleryDependency>(),
                manifest = manifest ?? new GalleryManifest(), setupKey = setup?.key, setupLabel = setup?.label,
                stage = GalleryJob.Validate, createdAt = DateTime.UtcNow.Ticks, status = "Checking the package…", progress = 0.4f,
            };
            GalleryJournal.Save(job);
            _ = RunAsync(job);
            return job;
        }

        /// <summary>The user's answer to the job's question: continue, or cancel the installation.</summary>
        internal static void Answer(GalleryJob job, bool accept)
        {
            if (job == null || !job.Waiting) return;
            if (!accept) { Finish(job, GalleryJob.Cancelled, CancelledText(job)); return; }
            job.accepted.Add(job.waiting);
            job.waiting = null; job.questionLines.Clear(); job.questionSummary = null;
            GalleryJournal.Save(job);
            _ = RunAsync(job);
        }

        internal static void Cancel(GalleryJob job)
        {
            if (job == null || job.Terminal) return;
            if (job.Waiting) { Answer(job, false); return; }
            job.cancelRequested = true;
            job.status = job.importing ? "Stopping once Unity finishes this import (Unity's importer cannot be interrupted)…" : "Cancelling…";
            GalleryJournal.Save(job);
            if (Cancellations.TryGetValue(job.id, out var source) && !job.importing) source.Cancel();
        }

        /// <summary>Forgets a finished job (its card goes back to Add, Update or Installed).</summary>
        internal static void Dismiss(GalleryJob job)
        {
            if (job == null || !job.Terminal) return;
            DeleteStaged(job);
            GalleryJournal.Remove(job);
        }

        /// <summary>Retries a failed job from the step it stopped at; nothing done before is done twice.</summary>
        internal static void Retry(GalleryJob job)
        {
            if (job == null || job.stage != GalleryJob.Failed) return;
            job.stage = job.placed.Count > 0 ? GalleryJob.Verify : job.importGuids.Count > 0 && !job.importing ? GalleryJob.Attach : GalleryJob.Download;
            job.error = null; job.cancelRequested = false;
            GalleryJournal.Save(job);
            _ = RunAsync(job);
        }

        private static string CancelledText(GalleryJob job) => job.importGuids.Count > 0 && job.placed.Count == 0
            ? "Cancelled. Its files were imported but nothing was added to the avatar: Cleanup can remove them."
            : "Cancelled. Nothing was added to the avatar.";

        internal static async Task RunAsync(GalleryJob job)
        {
            if (job == null || job.Terminal || job.Waiting || !Running.Add(job.id)) return;
            var source = new CancellationTokenSource();
            Cancellations[job.id] = source;
            try
            {
                while (!job.Terminal && !job.Waiting)
                {
                    if (job.cancelRequested && !job.importing) { Finish(job, GalleryJob.Cancelled, CancelledText(job)); break; }
                    var avatar = FindAvatar(job);
                    if (!avatar)
                    {
                        // The avatar's scene is closed: the job waits for it instead of failing.
                        job.status = "Waiting for " + job.avatarName + " (open " + (string.IsNullOrEmpty(job.scenePath) ? "its scene" : Path.GetFileNameWithoutExtension(job.scenePath)) + ")";
                        GalleryJournal.Save(job);
                        break;
                    }
                    while (EditorApplication.isCompiling || EditorApplication.isUpdating) await Task.Delay(150, source.Token);
                    await Step(job, avatar, source.Token);
                }
            }
            catch (OperationCanceledException) { if (!job.Terminal) Finish(job, GalleryJob.Cancelled, CancelledText(job)); }
            catch (Exception ex)
            {
                Debug.LogWarning("[My Avatar] Gallery installation of " + job.assetName + " stopped: " + ex);
                Fail(job, ex.Message);
            }
            finally
            {
                Running.Remove(job.id);
                Cancellations.Remove(job.id);
                source.Dispose();
                AccessoryService.NotifyChanged(FindAvatar(job));
            }
        }

        private static Task Step(GalleryJob job, MyAvatar avatar, CancellationToken cancellation)
        {
            switch (job.stage)
            {
                case GalleryJob.Queued: return Check(job, avatar);
                case GalleryJob.Download: return DownloadPackage(job, cancellation);
                case GalleryJob.Validate: return Validate(job, cancellation);
                case GalleryJob.Dependencies: return ResolveDependencies(job, cancellation);
                case GalleryJob.Preview: return PreviewImport(job, cancellation);
                case GalleryJob.Import: return ImportPackage(job, cancellation);
                case GalleryJob.Attach: Attach(job, avatar); return Task.CompletedTask;
                case GalleryJob.Verify: Verify(job, avatar); return Task.CompletedTask;
                default: Fail(job, "Unknown installation step " + job.stage + "."); return Task.CompletedTask;
            }
        }

        private static void Move(GalleryJob job, string stage, string status, float progress)
        {
            job.stage = stage; job.status = status; job.progress = progress;
            GalleryJournal.Save(job);
        }

        private static void Ask(GalleryJob job, string question, string summary, IEnumerable<string> lines)
        {
            job.waiting = question; job.questionSummary = summary;
            job.questionLines = lines?.Take(400).ToList() ?? new List<string>();
            job.status = summary;
            GalleryJournal.Save(job);
        }

        internal static void Fail(GalleryJob job, string error)
        {
            job.error = error;
            Finish(job, GalleryJob.Failed, error);
        }

        private static void Finish(GalleryJob job, string stage, string status)
        {
            job.stage = stage; job.status = status; job.waiting = null; job.importing = false; job.installingDependencies = false;
            if (stage != GalleryJob.Failed) DeleteStaged(job);
            GalleryJournal.Save(job);
        }

        // ---- Checks before anything is downloaded ----

        private static Task Check(GalleryJob job, MyAvatar avatar)
        {
            string platform = AvatarPlatform.Current;
            if (job.platforms.Count > 0 && !job.platforms.Contains(platform))
            {
                Fail(job, $"This package is for {string.Join(" and ", job.platforms.Select(AvatarPlatform.Label))}; the project builds for {AvatarPlatform.Label(platform)}.");
                return Task.CompletedTask;
            }
            var budget = AvatarParameterBudget.Estimate(avatar.gameObject);
            if (job.parameterBits > 0 && job.parameterBits > budget.Free && !job.accepted.Contains("budget"))
            {
                Ask(job, "budget", $"{job.assetName} needs {job.parameterBits} bits of parameter memory; this avatar has {budget.Free} of 256 left.",
                    new[] { budget.VrcFuryPresent ? budget.CompressionStatus : "Without VRCFury, the avatar cannot compress its parameters at build time.",
                        "VRChat refuses to upload an avatar whose synced parameters exceed 256 bits." });
                return Task.CompletedTask;
            }
            Move(job, GalleryJob.Download, "Downloading…", 0.02f);
            return Task.CompletedTask;
        }

        // ---- Download, staged outside Assets ----

        private static async Task DownloadPackage(GalleryJob job, CancellationToken cancellation)
        {
            Directory.CreateDirectory(GalleryApi.StagingFolder);
            string path = Path.Combine(GalleryApi.StagingFolder, job.id + ".unitypackage");
            if (File.Exists(path) && !string.IsNullOrEmpty(job.sha256) &&
                string.Equals(await Task.Run(() => UnityPackageFiles.FileHash(path), cancellation), job.sha256, StringComparison.OrdinalIgnoreCase))
            {
                job.packagePath = path;
                Move(job, GalleryJob.Validate, "Checking the package…", 0.4f);
                return;
            }
            double lastSave = 0;
            var result = await Download(job, path, (bytes, total) =>
            {
                long size = total > 0 ? total : job.sizeBytes;
                job.progress = 0.02f + 0.36f * (size > 0 ? Mathf.Clamp01(bytes / (float)size) : 0f);
                job.status = "Downloading " + Toolkit.Editor.Net.OrbitersTransfer.Describe(bytes, size);
                if (EditorApplication.timeSinceStartup - lastSave > 0.1) { lastSave = EditorApplication.timeSinceStartup; GalleryJournal.Notify(job); }
            }, cancellation);
            if (result.Cancelled) throw new OperationCanceledException();
            if (!result.Success) { Fail(job, result.Error); return; }
            // The download repeats whether the creator is trusted now: the card's answer may be older.
            string trusted = result.Header("X-Orbiters-Creator-Trusted");
            if (!string.IsNullOrEmpty(trusted)) job.creatorTrusted = trusted == "true";
            job.packagePath = path;
            Move(job, GalleryJob.Validate, "Checking the package…", 0.4f);
        }

        // ---- Validation: structure, paths, the declared prefabs, code ----

        private static async Task Validate(GalleryJob job, CancellationToken cancellation)
        {
            if (string.IsNullOrEmpty(job.packagePath) || !File.Exists(job.packagePath)) { Move(job, GalleryJob.Download, "Downloading again…", 0.02f); return; }
            UnityPackageIndex index;
            try { index = await Task.Run(() => UnityPackageIndex.Read(job.packagePath), cancellation); }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is EndOfStreamException)
            { Fail(job, "The package is not a readable Unity package: " + ex.Message); return; }
            if (index.UnsafePaths.Count > 0) { Fail(job, "The package would write files outside Assets and Packages: nothing was imported."); return; }
            var guids = new HashSet<string>(index.Entries.Select(e => e.Guid), StringComparer.OrdinalIgnoreCase);
            var setup = job.manifest.Setup(job.setupKey);
            if (setup == null || setup.prefabs.Count == 0 || setup.prefabs.Any(p => !guids.Contains(p.guid)))
            { Fail(job, "The package does not hold the prefabs its creator declared. Ask the creator to publish it again."); return; }
            var code = ContentTrust.PackageCode(index, AssetDatabase.GUIDToAssetPath);
            bool trusted = ContentTrust.IsTrusted(job.creatorTrusted, job.creatorId, ContentTrust.SignedInUserId());
            if (code.Count > 0 && !trusted && !job.accepted.Contains("code"))
            {
                Ask(job, "code", $"{job.assetName} contains code from {(string.IsNullOrEmpty(job.creatorName) ? "its creator" : job.creatorName)}. " + ContentTrust.UntrustedCodeMessage, code);
                return;
            }
            Move(job, GalleryJob.Dependencies, "Checking what it needs…", 0.45f);
        }

        // ---- Dependencies: one plan, shown before any package changes ----

        private static async Task ResolveDependencies(GalleryJob job, CancellationToken cancellation)
        {
            if (job.dependencies.Count == 0) { Move(job, GalleryJob.Preview, "Checking your project…", 0.5f); return; }
#if MYAVATAR_VPM
            var catalog = await KnownDependencies(cancellation);
            var candidates = (catalog?.packages ?? new List<KnownPackage>()).SelectMany(p => p.versions.Select(v => new VpmCandidate
            {
                Id = p.id, Version = v.version, DisplayName = p.displayName, RepositoryUrl = p.repositoryUrl,
                Dependencies = new Dictionary<string, string>(v.dependencies ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            }));
            var plan = VpmDependencyPlan.Resolve(job.dependencies.Select(d => new VpmDependencyPlan.Requirement(d.id, d.range, job.assetName)), new VpmProjectCatalog(candidates));
            if (job.installingDependencies)
            {
                // Back after the install reloaded Unity: anything still missing failed.
                job.installingDependencies = false;
                if (!plan.NothingToDo) { Fail(job, "Some packages it needs could not be installed: " + string.Join(", ", plan.Steps.Where(s => s.Action != VpmStepAction.Keep).Select(VpmDependencyPlan.Describe)) + "."); return; }
            }
            if (plan.NothingToDo) { Move(job, GalleryJob.Preview, "Checking your project…", 0.5f); return; }
            var lines = plan.Steps.Select(s => VpmDependencyPlan.Describe(s) + (s.Action == VpmStepAction.Keep ? "" : " — " + (s.Reason ?? "needed by " + string.Join(", ", s.RequiredBy))));
            if (!plan.CanApply) { Fail(job, "It needs packages My Avatar cannot install: " + string.Join("; ", plan.Steps.Where(s => !s.Changes && s.Action != VpmStepAction.Keep).Select(s => VpmDependencyPlan.Describe(s) + " (" + s.Reason + ")")) + "."); return; }
            if (!job.accepted.Contains("dependencies"))
            {
                int count = plan.Changes.Count();
                Ask(job, "dependencies", $"{job.assetName} needs {count} package{(count == 1 ? "" : "s")} this project does not have yet.", lines);
                return;
            }
            job.installingDependencies = true;
            job.status = "Installing " + string.Join(", ", plan.Changes.Select(s => s.DisplayName)) + "…";
            GalleryJournal.Save(job);
            await Task.Yield();
            var result = VpmPlanInstaller.Apply(plan);
            if (!result.Success) { job.installingDependencies = false; Fail(job, result.ErrorMessage ?? "The packages could not be installed."); return; }
            // Unity reloads its scripts now; the job resumes and checks the plan again.
            job.accepted.Remove("dependencies");
            GalleryJournal.Save(job);
#else
            await Task.Yield();
            var missing = job.dependencies.Where(d => !AssetDatabase.IsValidFolder("Packages/" + d.id)).ToList();
            if (missing.Count > 0) { Fail(job, "Add " + string.Join(", ", missing.Select(d => d.displayName ?? d.id)) + " to this project with the VRChat Creator Companion first."); return; }
            Move(job, GalleryJob.Preview, "Checking your project…", 0.5f);
#endif
        }

        internal static void DeleteStaged(GalleryJob job)
        {
            foreach (string path in new[] { job.packagePath, job.importPath })
            {
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    string full = Path.GetFullPath(path), root = Path.GetFullPath(GalleryApi.StagingFolder);
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) File.Delete(full);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // ---- Resume after a reload or restart ----

        [InitializeOnLoadMethod]
        private static void ResumeAfterReload()
        {
            EditorApplication.delayCall += Resume;
            // A job waiting for its avatar's scene continues when the scene opens.
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += (scene, mode) => EditorApplication.delayCall += Resume;
        }

        internal static void Resume()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) { EditorApplication.delayCall += Resume; return; }
            foreach (var job in GalleryJournal.All.Where(j => !j.Terminal && !j.Waiting).ToList()) _ = RunAsync(job);
        }
    }
}
