using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // One accessory drop from files to an attached accessory: open archives, import packages, pick the variant, place it
    // under the avatar root and attach it. A package with scripts recompiles Unity mid-way: the job is kept in SessionState
    // and resumes after the domain reload, with or without an Inspector open.
    internal static class AccessoryService
    {
        private const string JobKey = "Orbiters.MyAvatar.AccessoryJob";

        // JsonUtility writes null strings as "": test hand and pick with IsNullOrEmpty.
        [Serializable]
        internal sealed class Job
        {
            public string avatar, batch, hand, pick;
            /// <summary>Asset paths offered when a drop holds several different items.</summary>
            public List<string> options = new List<string>();
            public AccessoryImport.Drop drop = new AccessoryImport.Drop();
            public List<string> guids = new List<string>();
            public int nextPackage;
        }

        internal sealed class Outcome
        {
            public OrbitersAttachment attachment;
            public AttachmentPlan plan;
            public AccessoryCandidates.Candidate candidate;
            public List<AccessoryImport.Doc> docs;
            public List<string> images;
        }

        /// <summary>Raised whenever an avatar's accessory status or list changes, so Inspectors can refresh.</summary>
        internal static event Action<MyAvatar> Changed;
        internal static bool Busy { get; private set; }

        internal static async Task<List<Outcome>> DropAsync(MyAvatar avatar, string[] paths, Func<AccessoryCandidates.Choice, List<AccessoryImport.Doc>, Task<AccessoryCandidates.Candidate>> pickRival,
            Action<float, string> progress, CancellationToken cancellation)
        {
            var job = new Job { avatar = GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString(), batch = Guid.NewGuid().ToString("N").Substring(0, 12) };
            progress(.05f, "Opening the drop…");
            string root = AccessoryImport.ProjectRoot, staging = AccessoryImport.Staging;
            job.drop = await Task.Run(() => AccessoryImport.Expand(paths, root, staging), cancellation);
            foreach (var package in job.drop.packages)
            {
                progress(.15f, "Reading " + System.IO.Path.GetFileName(package) + "…");
                var index = await Task.Run(() => AccessoryImport.ReadPackage(package), cancellation);
                job.guids.AddRange(index.guids);
                foreach (var doc in index.docs) AccessoryImport.AddDoc(job.drop.docs, doc.name, () => doc.text);
            }
            cancellation.ThrowIfCancellationRequested();
            job.drop.assets.AddRange(AccessoryImport.CopyModels(job.drop, job.batch));
            return await RunAsync(avatar, job, pickRival, progress, cancellation);
        }

        /// <summary>The question a drop is waiting on: hands ("left", "right", "both") or items (asset paths), with labels.</summary>
        internal static List<(string label, string value)> PendingOptions(MyAvatar avatar, out bool hands)
        {
            hands = false;
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(avatar.accessoryChoice)) return result;
            var job = JsonUtility.FromJson<Job>(avatar.accessoryChoice);
            hands = job.options.Count == 0;
            if (hands) return new List<(string, string)> { ("Left hand", "left"), ("Right hand", "right"), ("Both", "both") };
            foreach (var path in job.options) result.Add((System.IO.Path.GetFileNameWithoutExtension(path), path));
            return result;
        }

        /// <summary>Continues a drop that waited for the user: a hand, or one of several items (the others stay offered).</summary>
        internal static Task<List<Outcome>> ChooseAsync(MyAvatar avatar, string answer, Action<float, string> progress, CancellationToken cancellation)
        {
            var job = JsonUtility.FromJson<Job>(avatar.accessoryChoice);
            if (job.options.Count == 0) { job.hand = answer; avatar.accessoryChoice = null; }
            else
            {
                job.pick = answer;
                job.options.Remove(answer);
                avatar.accessoryChoice = job.options.Count > 0 ? JsonUtility.ToJson(new Job { avatar = job.avatar, batch = job.batch, drop = job.drop, guids = job.guids, nextPackage = job.nextPackage, options = job.options }) : null;
            }
            Dirty(avatar);
            return RunAsync(avatar, job, null, progress, cancellation);
        }

        internal static void DismissChoice(MyAvatar avatar)
        {
            avatar.accessoryChoice = null; Dirty(avatar);
            Changed?.Invoke(avatar);
        }

        private static async Task<List<Outcome>> RunAsync(MyAvatar avatar, Job job, Func<AccessoryCandidates.Choice, List<AccessoryImport.Doc>, Task<AccessoryCandidates.Candidate>> pickRival,
            Action<float, string> progress, CancellationToken cancellation)
        {
            Busy = true;
            try
            {
                while (job.nextPackage < job.drop.packages.Count)
                {
                    var package = job.drop.packages[job.nextPackage++];
                    SessionState.SetString(JobKey, JsonUtility.ToJson(job));
                    progress(.25f + .4f * job.nextPackage / job.drop.packages.Count, "Importing " + System.IO.Path.GetFileNameWithoutExtension(package) + "…");
                    await AccessoryImport.ImportPackageAsync(package, cancellation);
                }
                // Kept until Unity is idle: the import may still trigger a recompile and domain reload.
                if (job.drop.packages.Count > 0) SessionState.SetString(JobKey, JsonUtility.ToJson(job));
                while (EditorApplication.isCompiling || EditorApplication.isUpdating) await Task.Delay(100, cancellation);
                SessionState.EraseString(JobKey);
                return await InstallAsync(avatar, job, pickRival, progress);
            }
            finally
            {
                Busy = false;
                Changed?.Invoke(avatar);
            }
        }

        private static async Task<List<Outcome>> InstallAsync(MyAvatar avatar, Job job, Func<AccessoryCandidates.Choice, List<AccessoryImport.Doc>, Task<AccessoryCandidates.Candidate>> pickRival,
            Action<float, string> progress)
        {
            var outcomes = new List<Outcome>();
            if (!avatar) return outcomes;
            var assets = job.drop.assets.Concat(job.guids.Select(AssetDatabase.GUIDToAssetPath).Where(p => !string.IsNullOrEmpty(p))).ToList();
            progress(.75f, "Choosing what to put on…");
            var choice = AccessoryCandidates.Choose(assets);
            var notes = job.drop.refused.Select(r => new MyAvatar.AccessoryNote { text = r, warning = true }).ToList();
            if (choice.best == null)
            {
                Status(avatar, job.drop.images.Count > 0 ? null : "Nothing to put on the avatar in this drop: no prefab or model with a mesh.", true, notes);
                if (job.drop.images.Count > 0) outcomes.Add(new Outcome { docs = job.drop.docs, images = job.drop.images });
                return outcomes;
            }
            if (choice.NeedsHand && string.IsNullOrEmpty(job.hand))
            {
                avatar.accessoryChoice = JsonUtility.ToJson(job); Dirty(avatar);
                Status(avatar, "Left hand, right hand or both?", false, notes);
                return outcomes;
            }
            // Several different items of equal standing (a clothing collection): the user picks, one or more.
            if (string.IsNullOrEmpty(job.pick) && !choice.NeedsHand && choice.rivals.Count >= 2)
            {
                job.options = new[] { choice.best }.Concat(choice.rivals).Select(c => c.path).ToList();
                avatar.accessoryChoice = JsonUtility.ToJson(job); Dirty(avatar);
                Status(avatar, $"This drop holds {job.options.Count} different items: choose what to add.", false, notes);
                return outcomes;
            }
            var chosen = new List<AccessoryCandidates.Candidate>();
            if (!string.IsNullOrEmpty(job.pick)) chosen.AddRange(choice.all.Where(c => c.path == job.pick));
            else if (choice.NeedsHand)
            {
                if (job.hand != "right") chosen.Add(choice.left);
                if (job.hand != "left") chosen.Add(choice.right);
            }
            else
            {
                var best = choice.best;
                if (choice.rivals.Count > 0 && pickRival != null) best = await pickRival(choice, job.drop.docs) ?? best;
                chosen.Add(best);
            }
            progress(.9f, "Attaching " + string.Join(" and ", chosen.Select(c => c.name)) + "…");
            foreach (var candidate in chosen)
            {
                var outcome = Install(avatar, candidate, notes);
                if (outcome == null) continue;
                outcome.docs = job.drop.docs;
                outcome.images = job.drop.images;
                outcomes.Add(outcome);
            }
            Status(avatar, Summary(outcomes, choice.all.Count - chosen.Count), notes.Any(n => n.warning), notes);
            return outcomes;
        }

        /// <summary>Adds one more copy of an accessory already on the avatar (the "Add another" choice).</summary>
        internal static Task<List<Outcome>> InstallPathAsync(MyAvatar avatar, string path)
        {
            var outcomes = new List<Outcome>();
            var candidate = AccessoryCandidates.Choose(new[] { path }).best;
            var notes = avatar.accessoryNotes.Where(n => n.duplicate != path).ToList();
            var outcome = candidate != null ? Install(avatar, candidate, notes, allowDuplicate: true) : null;
            if (outcome != null) outcomes.Add(outcome);
            Status(avatar, outcome != null ? outcome.attachment.name + " " + Describe(outcome.attachment, outcome.plan) : "Nothing was added.", notes.Any(n => n.warning), notes);
            return Task.FromResult(outcomes);
        }

        private static Outcome Install(MyAvatar avatar, AccessoryCandidates.Candidate candidate, List<MyAvatar.AccessoryNote> notes, bool allowDuplicate = false)
        {
            string guid = AssetDatabase.AssetPathToGUID(candidate.path);
            var existing = allowDuplicate ? null : AttachmentInstaller.Installed(avatar.transform).FirstOrDefault(a => a.source == guid && a.variant == candidate.path);
            if (existing != null)
            {
                notes.Add(new MyAvatar.AccessoryNote { target = existing.gameObject, accessory = existing, text = candidate.name + " is already on this avatar.", duplicate = candidate.path });
                return null;
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("My Avatar: add " + candidate.name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(candidate.asset, avatar.transform);
            Undo.RegisterCreatedObjectUndo(instance, "Add " + candidate.name);
            var plan = AttachmentPlanner.Analyze(instance, avatar.transform);
            if (plan.Kind == AttachmentKind.Empty)
            {
                Undo.DestroyObjectImmediate(instance);
                notes.Add(new MyAvatar.AccessoryNote { text = candidate.name + " has nothing to show on the avatar.", warning = true });
                Undo.CollapseUndoOperations(group);
                return null;
            }
            var attachment = AttachmentInstaller.Install(plan, new AttachmentOptions { Created = true, Source = guid, Variant = candidate.path });
            foreach (var note in plan.Notes)
                notes.Add(new MyAvatar.AccessoryNote { target = note.Target, accessory = attachment, text = note.Reason, warning = true, modularAvatar = plan.NeedsModularAvatar && note.Target == instance });
            if (!VrcFury.Installed && plan.Kind != AttachmentKind.Configured)
                notes.Add(new MyAvatar.AccessoryNote { accessory = attachment, text = "Install VRCFury to get a menu toggle and exact armature links for accessories.", vrcFury = true });
            Undo.CollapseUndoOperations(group);
            // Close the group: at the end of a prefab drag Unity reverts whatever is in the current Undo group, which
            // would otherwise take the accessory away right after it was added.
            Undo.IncrementCurrentGroup();
            EditorGUIUtility.PingObject(instance);
            return new Outcome { attachment = attachment, plan = plan, candidate = candidate };
        }

        private static string Summary(List<Outcome> outcomes, int leftOut)
        {
            if (outcomes.Count == 0) return "Nothing new was added.";
            var parts = outcomes.Where(o => o.attachment != null).Select(o => o.attachment.name + " " + Describe(o.attachment, o.plan)).ToList();
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

        // A job interrupted by a domain reload (a package with scripts) picks up where it was.
        [InitializeOnLoadMethod]
        private static void Resume() => EditorApplication.delayCall += TryResume;

        private static async void TryResume()
        {
            var json = SessionState.GetString(JobKey, null);
            if (string.IsNullOrEmpty(json)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += TryResume; return; }
            SessionState.EraseString(JobKey);
            var job = JsonUtility.FromJson<Job>(json);
            if (!GlobalObjectId.TryParse(job.avatar, out var id) || !(GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is MyAvatar avatar)) return;
            try
            {
                Status(avatar, "Finishing the accessory import…", false, null);
                await RunAsync(avatar, job, null, (_, text) => { }, CancellationToken.None);
            }
            catch (Exception ex) { Status(avatar, ex.Message, true, null); }
        }
    }
}
