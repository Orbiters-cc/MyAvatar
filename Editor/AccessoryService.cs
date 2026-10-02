using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // One accessory drop from files to an attached accessory: open archives, import packages, pick the variant, place it
    // under the avatar root and attach it. A package with scripts recompiles Unity mid-way: each avatar's job is kept in
    // SessionState and resumes after the domain reload, with or without an Inspector open; what the drop still has to do
    // then (its images, AI help) waits for the avatar's Inspector.
    internal static class AccessoryService
    {
        internal const string JobKey = "Orbiters.MyAvatar.AccessoryJobs";
        /// <summary>The answer that adds every item a drop offered.</summary>
        internal const string AllOptions = "*";
        internal const string CodeMessage = "Orbiters does not control the content of this package or its scripts. Unity compiles and runs code as soon as it is imported.";

        // JsonUtility writes null strings as "": test hand and pick with IsNullOrEmpty.
        [Serializable]
        internal sealed class Job
        {
            public string avatar, batch, hand, pick;
            /// <summary>The avatar's instance ID: finds it again after a domain reload, also in a scene that was never saved.</summary>
            public int instance;
            /// <summary>Asset paths offered when a drop holds several different items.</summary>
            public List<string> options = new List<string>();
            public AccessoryImport.Drop drop = new AccessoryImport.Drop();
            public List<string> guids = new List<string>();
            public int nextPackage;
        }

        [Serializable] private sealed class Jobs { public List<Job> list = new List<Job>(); }

        internal sealed class Outcome
        {
            public OrbitersAttachment attachment;
            public AttachmentPlan plan;
            public AccessoryCandidates.Candidate candidate;
            public List<AccessoryImport.Doc> docs;
            public List<string> images;
            /// <summary>The drop's opened archives, released once its images are applied.</summary>
            public string staging;
            /// <summary>The avatar already wore this item (put on by hand): My Avatar took it over instead of adding a copy.</summary>
            public bool adopted;
            internal string followUp;
        }

        /// <summary>Raised whenever an avatar's accessory status or list changes, so Inspectors can refresh.</summary>
        internal static event Action<MyAvatar> Changed;
        /// <summary>Asks before a package with code is imported; replaced in tests.</summary>
        internal static Func<UntrustedCodeDialog.Request, bool> ConfirmCode = UntrustedCodeDialog.Confirm;
        private static readonly HashSet<int> Running = new HashSet<int>();
        // Drops finished after a domain reload, waiting for the avatar's Inspector to apply their images and ask AI.

        internal static bool Busy(MyAvatar avatar) => avatar && Running.Contains(avatar.GetInstanceID());
        internal static bool HasFollowUp(MyAvatar avatar) => AccessoryFollowUps.Has(avatar);

        internal static List<Outcome> TakeFollowUp(MyAvatar avatar)
        {
            return AccessoryFollowUps.Take(avatar);
        }

        internal static Task<List<Outcome>> DropAsync(MyAvatar avatar, string[] paths, Action<float, string> progress, CancellationToken cancellation) => Locked(avatar, async () =>
        {
            var job = new Job { avatar = GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString(), instance = avatar.GetInstanceID(), batch = Guid.NewGuid().ToString("N").Substring(0, 12) };
            progress(.05f, "Opening the drop…");
            string root = AccessoryImport.ProjectRoot, staging = AccessoryImport.Staging, folder = Path.Combine(staging, job.batch);
            var keep = KeptStaging();
            try
            {
                job.drop = await DedicatedTask.Run(() => { AccessoryImport.SweepStaging(staging, keep); return AccessoryImport.Expand(paths, root, folder); }, cancellation);
                if (!await ReviewPackagesAsync(avatar, job, progress, cancellation)) { AccessoryImport.DeleteStaging(folder); return new List<Outcome>(); }
                cancellation.ThrowIfCancellationRequested();
                job.drop.assets.AddRange(AccessoryImport.CopyModels(job.drop, job.batch));
                return await RunAsync(avatar, job, progress, cancellation);
            }
            catch { AccessoryImport.DeleteStaging(folder); throw; }
        });

        // Every package is read before anything is imported: its GUIDs find the entry prefab wherever Unity puts it and its
        // readmes help AI. A package that would write outside the project is refused; code asks first, and Cancel stops the drop.
        private static async Task<bool> ReviewPackagesAsync(MyAvatar avatar, Job job, Action<float, string> progress, CancellationToken cancellation)
        {
            var code = new List<(string package, List<string> files)>();
            foreach (var package in job.drop.packages.ToList())
            {
                string name = Path.GetFileName(package);
                progress(.15f, "Reading " + name + "…");
                UnityPackageIndex index;
                try { index = await Task.Run(() => AccessoryImport.ReadPackage(package), cancellation); }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                { Refuse(job, package, name + ": not imported, it is not a readable Unity package."); continue; }
                if (index.UnsafePaths.Count > 0) { Refuse(job, package, name + ": not imported, it would write files outside Assets and Packages."); continue; }
                job.guids.AddRange(index.Entries.Select(e => e.Guid));
                foreach (var doc in AccessoryImport.PackageDocs(index)) AccessoryImport.AddDoc(job.drop.docs, doc.name, () => doc.text);
                var files = index.CodeFilesIncludingExisting(AssetDatabase.GUIDToAssetPath);
                if (files.Count > 0) code.Add((name, files));
            }
            if (code.Count == 0) return true;
            string subject = code.Count == 1 ? code[0].package : $"{code.Count} packages of this drop";
            if (ConfirmCode(new UntrustedCodeDialog.Request
            {
                Subject = subject, Message = CodeMessage, ConfirmLabel = "Import anyway",
                Files = code.SelectMany(c => code.Count == 1 ? c.files : c.files.Select(f => c.package + " › " + f)).ToList(),
            })) return true;
            Status(avatar, $"Cancelled · nothing was imported, {subject} {(code.Count == 1 ? "contains" : "contain")} code.", false, Notes(job));
            return false;
        }

        private static void Refuse(Job job, string package, string note) { job.drop.packages.Remove(package); job.drop.refused.Add(note); }
        private static List<MyAvatar.AccessoryNote> Notes(Job job) => job.drop.refused.Select(r => new MyAvatar.AccessoryNote { text = r, warning = true }).ToList();

        /// <summary>The question a drop is waiting on: hands ("left", "right", "both") or items (asset paths), with labels.</summary>
        internal static List<(string label, string value)> PendingOptions(MyAvatar avatar, out bool hands)
        {
            hands = false;
            var result = new List<(string, string)>();
            var job = Pending(avatar);
            if (job == null) return result;
            hands = job.options.Count == 0;
            if (hands) return new List<(string, string)> { ("Left hand", "left"), ("Right hand", "right"), ("Both", "both") };
            foreach (var path in job.options) result.Add((Path.GetFileNameWithoutExtension(path), path));
            return result;
        }

        /// <summary>
        /// Continues a drop that waited for the user: a hand, one of several items (the others stay offered) or all of them
        /// (<see cref="AllOptions"/>).
        /// </summary>
        internal static Task<List<Outcome>> ChooseAsync(MyAvatar avatar, string answer, Action<float, string> progress, CancellationToken cancellation) => Locked(avatar, () =>
        {
            var job = Pending(avatar) ?? throw new InvalidOperationException("This choice is no longer pending.");
            if (job.options.Count == 0) { job.hand = answer; avatar.accessoryChoice = null; }
            else if (answer == AllOptions) { job.pick = AllOptions; avatar.accessoryChoice = null; }
            else
            {
                job.pick = answer;
                job.options.Remove(answer);
                avatar.accessoryChoice = job.options.Count > 0 ? JsonUtility.ToJson(new Job { avatar = job.avatar, instance = job.instance, batch = job.batch, drop = job.drop, guids = job.guids, nextPackage = job.nextPackage, options = job.options }) : null;
            }
            Dirty(avatar);
            return RunAsync(avatar, job, progress, cancellation);
        });

        internal static void DismissChoice(MyAvatar avatar)
        {
            var job = Pending(avatar);
            avatar.accessoryChoice = null; Dirty(avatar);
            if (job != null) Release(avatar, job.drop.staging);
            Changed?.Invoke(avatar);
        }

        /// <summary>Removes a drop's opened archives once nothing needs them: its images applied, no choice still offered from it.</summary>
        internal static void Release(MyAvatar avatar, string staging)
        {
            if (string.IsNullOrEmpty(staging) || avatar && Pending(avatar)?.drop.staging == staging) return;
            AccessoryImport.DeleteStaging(staging);
        }

        private static Job Pending(MyAvatar avatar) => avatar && !string.IsNullOrEmpty(avatar.accessoryChoice) ? JsonUtility.FromJson<Job>(avatar.accessoryChoice) : null;

        // One job per avatar at a time; drops on other avatars run on their own.
        internal static async Task<List<Outcome>> Locked(MyAvatar avatar, Func<Task<List<Outcome>>> work)
        {
            int id = avatar.GetInstanceID();
            if (!Running.Add(id)) throw new InvalidOperationException("My Avatar is still adding an accessory to this avatar. Try again when it is done.");
            try { return await work(); }
            finally { Running.Remove(id); Changed?.Invoke(avatar); }
        }

        private static async Task<List<Outcome>> RunAsync(MyAvatar avatar, Job job, Action<float, string> progress, CancellationToken cancellation)
        {
            // Saved before each import: a package with scripts reloads the domain, and the job resumes from the next package.
            // Anything that ends the job here (done, failed, cancelled) forgets it; a domain reload does not.
            try
            {
                while (job.nextPackage < job.drop.packages.Count)
                {
                    var package = job.drop.packages[job.nextPackage];
                    // The editor can reload while this job waits for another avatar's native import. Keep the queued
                    // package pending until it actually starts; only then may recovery advance past it.
                    Save(job);
                    progress(.25f + .4f * (job.nextPackage + 1) / job.drop.packages.Count, "Importing " + Path.GetFileNameWithoutExtension(package) + "…");
                    await AccessoryImport.ImportPackageAsync(package, cancellation, () => { job.nextPackage++; Save(job); });
                }
                // Kept until Unity is idle: the import may still trigger a recompile and domain reload.
                if (job.drop.packages.Count > 0) Save(job);
                while (EditorApplication.isCompiling || EditorApplication.isUpdating) await Task.Delay(100, cancellation);
            }
            finally { Forget(job); }
            return InstallAll(avatar, job, progress);
        }

        private static List<Outcome> InstallAll(MyAvatar avatar, Job job, Action<float, string> progress)
        {
            var outcomes = new List<Outcome>();
            if (!avatar) return outcomes;
            var assets = job.drop.assets.Concat(job.guids.Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p))).ToList();
            progress(.75f, "Choosing what to put on…");
            var choice = AccessoryCandidates.Choose(assets);
            var notes = Notes(job);
            if (choice.best == null)
            {
                Status(avatar, job.drop.images.Count > 0 ? null : "Nothing to put on the avatar in this drop: no prefab or model with a mesh.", true, notes);
                if (job.drop.images.Count > 0) outcomes.Add(new Outcome { docs = job.drop.docs, images = job.drop.images, staging = job.drop.staging });
                else Release(avatar, job.drop.staging);
                return outcomes;
            }
            if (choice.NeedsHand && string.IsNullOrEmpty(job.hand))
            {
                avatar.accessoryChoice = JsonUtility.ToJson(job); Dirty(avatar);
                Status(avatar, "Left hand, right hand or both?", false, notes);
                return outcomes;
            }
            // Several different items of equal standing (a collection, or an item and its add-on): the user picks, one or more.
            if (string.IsNullOrEmpty(job.pick) && !choice.NeedsHand && choice.rivals.Count >= 1)
            {
                job.options = new[] { choice.best }.Concat(choice.rivals).Select(c => c.path).ToList();
                avatar.accessoryChoice = JsonUtility.ToJson(job); Dirty(avatar);
                Status(avatar, $"This drop holds {job.options.Count} different items: choose what to add.", false, notes);
                return outcomes;
            }
            var chosen = new List<AccessoryCandidates.Candidate>();
            if (job.pick == AllOptions) chosen.AddRange(choice.all.Where(c => job.options.Contains(c.path)));
            else if (!string.IsNullOrEmpty(job.pick)) chosen.AddRange(choice.all.Where(c => c.path == job.pick));
            else if (choice.NeedsHand)
            {
                if (job.hand != "right") chosen.Add(choice.left);
                if (job.hand != "left") chosen.Add(choice.right);
            }
            else chosen.Add(choice.best);
            progress(.9f, "Attaching " + string.Join(" and ", chosen.Select(c => c.name)) + "…");
            foreach (var candidate in chosen)
            {
                var outcome = Install(avatar, candidate, notes);
                if (outcome == null) continue;
                outcome.docs = job.drop.docs;
                outcome.images = job.drop.images;
                outcome.staging = job.drop.staging;
                outcomes.Add(outcome);
            }
            // Without images to apply nothing reads the opened archives any more.
            if (outcomes.Count == 0 || job.drop.images.Count == 0) Release(avatar, job.drop.staging);
            // Variants left out: not the items still offered for a later pick.
            int leftOut = choice.all.Count(c => !chosen.Contains(c) && (job.pick == AllOptions || !job.options.Contains(c.path)));
            Status(avatar, Summary(outcomes, leftOut), notes.Any(n => n.warning), notes);
            return outcomes;
        }

        /// <summary>Adds one more copy of an accessory already on the avatar (the "Add another" choice).</summary>
        internal static Task<List<Outcome>> InstallPathAsync(MyAvatar avatar, string path) => Locked(avatar, () =>
        {
            var outcomes = new List<Outcome>();
            var candidate = AccessoryCandidates.Choose(new[] { path }).best;
            var notes = avatar.accessoryNotes.Where(n => n.duplicate != path).ToList();
            var outcome = candidate != null ? Install(avatar, candidate, notes, allowDuplicate: true) : null;
            if (outcome != null) outcomes.Add(outcome);
            Status(avatar, outcome != null ? outcome.attachment.name + " " + Describe(outcome.attachment, outcome.plan) : "Nothing was added.", notes.Any(n => n.warning), notes);
            return Task.FromResult(outcomes);
        });

        private static Outcome Install(MyAvatar avatar, AccessoryCandidates.Candidate candidate, List<MyAvatar.AccessoryNote> notes, bool allowDuplicate = false)
        {
            string guid = AssetDatabase.AssetPathToGUID(candidate.path);
            string origin = AccessoryCandidates.Origin(candidate.asset);
            // The same item, also when it came as its model before (or its prefab now): one prefab and the model it is made of.
            var existing = allowDuplicate ? null : AttachmentInstaller.Installed(avatar.transform)
                .FirstOrDefault(a => a.source == guid && a.variant == candidate.path || origin != null && AccessoryCandidates.Origin(a.gameObject) == origin);
            if (existing != null)
            {
                notes.Add(new MyAvatar.AccessoryNote { target = existing.gameObject, accessory = existing, text = candidate.name + " is already on this avatar.", duplicate = candidate.path });
                return null;
            }
            // Put on by hand before: the very same prefab is taken over (no second copy); another prefab of the same model
            // is asked about.
            var worn = allowDuplicate ? null : Worn(avatar.transform, origin);
            if (worn != null && Source(worn) != candidate.path)
            {
                notes.Add(new MyAvatar.AccessoryNote { target = worn, text = candidate.name + " looks like “" + worn.name + "”, already on this avatar.", duplicate = candidate.path });
                return null;
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            GameObject instance;
            if (worn != null)
            {
                Undo.SetCurrentGroupName("My Avatar: take over " + worn.name);
                instance = worn;
            }
            else
            {
                Undo.SetCurrentGroupName("My Avatar: add " + candidate.name);
                instance = (GameObject)PrefabUtility.InstantiatePrefab(candidate.asset, avatar.transform);
                Undo.RegisterCreatedObjectUndo(instance, "Add " + candidate.name);
            }
            var plan = AttachmentPlanner.Analyze(instance, avatar.transform);
            if (plan.Kind == AttachmentKind.Empty)
            {
                if (worn == null) Undo.DestroyObjectImmediate(instance);
                notes.Add(new MyAvatar.AccessoryNote { text = candidate.name + " has nothing to show on the avatar.", warning = true });
                Undo.CollapseUndoOperations(group);
                return null;
            }
            // A copy taken over counts as placed by My Avatar: dropping the item again handed it over, and Remove takes it off.
            var attachment = AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, Source = guid, Variant = candidate.path });
            // Clothing made for another body (its hips at the avatar's chest, another height): resized and moved onto this
            // avatar's armature, said in its entry with a way back. One put on by hand stays where its owner placed it.
            if (worn == null && (plan.Kind == AttachmentKind.Clothing || plan.Kind == AttachmentKind.Configured) &&
                AttachmentFit.Fit(attachment, avatar.transform) is AttachmentFit.Measure fit)
                notes.Add(new MyAvatar.AccessoryNote { accessory = attachment, armatureFit = true, text = FitText(candidate.name, fit) });
            foreach (var note in plan.Notes)
                notes.Add(new MyAvatar.AccessoryNote { target = note.Target, accessory = attachment, text = note.Reason, warning = true, modularAvatar = plan.NeedsModularAvatar && note.Target == instance });
            if (!VrcFury.Installed && plan.Kind != AttachmentKind.Configured)
                notes.Add(new MyAvatar.AccessoryNote { accessory = attachment, text = "Install VRCFury to get a menu toggle and exact armature links for accessories.", vrcFury = true });
            Undo.CollapseUndoOperations(group);
            // Close the group: at the end of a prefab drag Unity reverts whatever is in the current Undo group, which
            // would otherwise take the accessory away right after it was added.
            Undo.IncrementCurrentGroup();
            EditorGUIUtility.PingObject(instance);
            return new Outcome { attachment = attachment, plan = plan, candidate = candidate, adopted = worn != null };
        }

        internal static string FitText(string name, AttachmentFit.Measure fit) =>
            name + " was made for another body: " + (fit.Segments > 0 && Mathf.Abs(fit.Scale - 1f) > 0.005f ? (fit.Scale > 1f ? "enlarged" : "shrunk") + " to " +
            Mathf.RoundToInt(fit.Scale * 100f) + "% and " : "") + "aligned to this avatar's armature and pose.";

        // A copy of the item the avatar already wears without My Avatar: an outermost prefab instance under the avatar made
        // of the same model or prefab, outside every accessory My Avatar placed.
        private static GameObject Worn(Transform avatarRoot, string origin)
        {
            if (string.IsNullOrEmpty(origin)) return null;
            foreach (var t in avatarRoot.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                if (t == avatarRoot || !PrefabUtility.IsOutermostPrefabInstanceRoot(go) || go.GetComponentInParent<OrbitersAttachment>(true) != null) continue;
                if (AccessoryCandidates.Origin(go) == origin) return go;
            }
            return null;
        }

        private static string Source(GameObject instance)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(instance);
            return source != null ? AssetDatabase.GetAssetPath(source) : null;
        }

        private static string Summary(List<Outcome> outcomes, int leftOut)
        {
            if (outcomes.Count == 0) return "Nothing new was added.";
            var parts = outcomes.Where(o => o.attachment != null)
                .Select(o => o.attachment.name + (o.adopted ? " was already on the avatar: My Avatar manages it now and " : " ") + Describe(o.attachment, o.plan)).ToList();
            string skipped = leftOut > 0 ? $" · {leftOut} other variant{(leftOut == 1 ? "" : "s")} left out" : "";
            return string.Join(" · ", parts) + skipped;
        }

        internal static string Describe(OrbitersAttachment attachment, AttachmentPlan plan = null)
        {
            switch (attachment.mode)
            {
                case OrbitersAttachment.AttachMode.Configured: return "uses its creator's setup";
                case OrbitersAttachment.AttachMode.VrcFury: return "linked with VRCFury";
                case OrbitersAttachment.AttachMode.Merge:
                    return plan != null ? $"follows {plan.MatchedCount} of {plan.Matches.Count + plan.Unmatched.Count(u => plan.Matches.All(m => m.Source != u))} bones" : $"follows {attachment.links.Count} bones";
                default: return attachment.parent != null ? "follows " + attachment.parent.name : "needs a bone";
            }
        }

        internal static void Status(MyAvatar avatar, string status, bool warning, List<MyAvatar.AccessoryNote> notes)
        {
            if (!avatar) return;
            avatar.accessoryStatus = status;
            avatar.accessoryWarning = warning;
            avatar.accessoryNotes = notes ?? new List<MyAvatar.AccessoryNote>();
            Dirty(avatar);
            Changed?.Invoke(avatar);
        }

        // Status and notes are part of the component so they survive reloads; they stay out of Unity Undo, which would bring
        // back stale notes when the user undoes the accessory itself.
        private static void Dirty(MyAvatar avatar) => EditorUtility.SetDirty(avatar);

        // ---- Jobs across domain reloads ----------------------------------------------------------------------------

        internal static List<Job> Saved()
        {
            var json = SessionState.GetString(JobKey, null);
            return string.IsNullOrEmpty(json) ? new List<Job>() : JsonUtility.FromJson<Jobs>(json)?.list ?? new List<Job>();
        }

        internal static void Save(Job job) { var jobs = Saved(); jobs.RemoveAll(j => j.batch == job.batch); jobs.Add(job); Write(jobs); }
        internal static void Forget(Job job) { var jobs = Saved(); if (jobs.RemoveAll(j => j.batch == job.batch) > 0) Write(jobs); }

        private static void Write(List<Job> jobs)
        {
            if (jobs.Count == 0) SessionState.EraseString(JobKey);
            else SessionState.SetString(JobKey, JsonUtility.ToJson(new Jobs { list = jobs }));
        }

        // The instance ID holds for the whole editor session, also in a scene that was never saved; the global ID after the
        // scene was reopened.
        internal static MyAvatar Find(Job job)
        {
            var avatar = EditorUtility.InstanceIDToObject(job.instance) as MyAvatar;
            if (avatar) return avatar;
            return GlobalObjectId.TryParse(job.avatar, out var id) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as MyAvatar : null;
        }

        // Opened archives still needed by a pending choice or a job in progress.
        private static HashSet<string> KeptStaging()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stagings = UnityEngine.Object.FindObjectsByType<MyAvatar>(FindObjectsInactive.Include, FindObjectsSortMode.None).Select(a => Pending(a)?.drop.staging)
                .Concat(Saved().Select(j => j.drop.staging)).Concat(AccessoryFollowUps.Staging());
            foreach (var staging in stagings.Where(s => !string.IsNullOrEmpty(s)))
                try { keep.Add(Path.GetFullPath(staging)); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
            return keep;
        }

        // A job interrupted by a domain reload (a package with scripts) picks up where it was, one avatar after another.
        [InitializeOnLoadMethod]
        private static void Resume() => EditorApplication.delayCall += TryResume;

        private static async void TryResume()
        {
            var jobs = Saved();
            if (jobs.Count == 0) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += TryResume; return; }
            foreach (var job in jobs) await ResumeAsync(job);
        }

        internal static async Task ResumeAsync(Job job)
        {
            var avatar = Find(job);
            if (!avatar)
            {
                Forget(job); AccessoryImport.DeleteStaging(job.drop.staging);
                Debug.LogWarning("My Avatar could not find the avatar it was adding " + string.Join(", ", job.drop.packages.Select(Path.GetFileNameWithoutExtension)) +
                    " to. The package was imported, but nothing was put on: drop it again on the avatar.");
                return;
            }
            if (Busy(avatar)) return;
            try
            {
                Status(avatar, "Finishing the accessory import…", false, null);
                var outcomes = await Locked(avatar, () => RunAsync(avatar, job, (_, __) => { }, CancellationToken.None));
                if (outcomes.Count > 0 && avatar) { AccessoryFollowUps.Save(avatar, outcomes); Changed?.Invoke(avatar); }
            }
            catch (Exception ex) { AccessoryImport.DeleteStaging(job.drop.staging); Status(avatar, ex.Message, true, null); }
        }
    }
}
