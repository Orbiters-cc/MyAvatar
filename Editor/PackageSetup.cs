using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if MYAVATAR_VPM
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Vpm;
#endif

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// The downloads of a one-click setup (face tracking, GoGoLoco): the missing packages are shown as a plan first (every
    /// package, and the VPM repositories it adds to the Creator Companion), come through VPM once accepted, then the setup
    /// finishes on each avatar it was asked for as soon as Unity has imported them, after a script reload too. A download
    /// that never arrives is given up on after a few minutes, so a later install does not set anything up unasked; what
    /// happened is kept for the avatar's section to say (<see cref="TakeNote"/>, <see cref="Finished"/>).
    /// </summary>
    internal sealed class PackageSetup
    {
        // A download is waited for this long, checked about once a second.
        internal const double PendingTimeout = 300;
        private const double PollSeconds = 1;
        private readonly string pendingKey, noteKey, what, waitingFor;
        private readonly Func<bool> imported;
        private readonly Func<GameObject, string> finish;
        private double nextPoll;

        /// <summary>Raised with the avatar's instance ID once its setup finished, failed or was given up on.</summary>
        internal static event Action<int> Finished;

        /// <param name="key">Names the setup's pending state (Orbiters.MyAvatar.&lt;key&gt;.PendingAvatars).</param>
        /// <param name="what">What is set up, as in "set up face tracking".</param>
        /// <param name="waitingFor">The packages, for the note when they never arrive ("VRCFury or GoGoLoco").</param>
        /// <param name="imported">Whether everything the setup needs is in the project.</param>
        /// <param name="finish">Finishes on the avatar's root: null when done (it logs what it did), else why not.</param>
        internal PackageSetup(string key, string what, string waitingFor, Func<bool> imported, Func<GameObject, string> finish)
        {
            pendingKey = "Orbiters.MyAvatar." + key + ".PendingAvatars";
            noteKey = "Orbiters.MyAvatar." + key + ".Note.";
            this.what = what; this.waitingFor = waitingFor; this.imported = imported; this.finish = finish;
        }

        private string What => char.ToUpperInvariant(what[0]) + what.Substring(1);

#if MYAVATAR_VPM
        /// <summary>
        /// What downloading <paramref name="missing"/> (package ids) installs, read from their repositories before anything
        /// changes: each package with what it needs, and the repositories added to the Creator Companion.
        /// </summary>
        internal async Task<VpmDependencyPlan> PlanAsync(IList<string> missing)
        {
            var dependencies = VpmDependencies.For("orbiters.myavatar");
            var statuses = missing.Select(id => dependencies.OptionalStatus(id, true)).ToList();
            if (statuses.Any(s => s == null)) throw new InvalidOperationException("My Avatar does not know where to download " + string.Join(" and ", missing) + ".");
            return await VpmDependencies.PlanAsync(statuses, "My Avatar");
        }

        /// <summary>Installs the plan the user accepted, then finishes on <paramref name="avatar"/>. Returns a note for the user.</summary>
        internal string Install(MyAvatar avatar, VpmDependencyPlan plan)
        {
            string names = string.Join(" and ", plan.Changes.Select(s => s.DisplayName ?? s.Id));
            Expect(avatar);
            VpmDependencyInstallResult result;
            try { result = VpmPlanInstaller.Apply(plan); }
            catch (Exception ex) { result = new VpmDependencyInstallResult { Errors = { ex.Message } }; }
            if (result.Success)
                return (names.Length > 0 ? "Downloaded " + names + ". " : "") + What + " is set up as soon as Unity has imported " + (names.Length > 0 ? "them." : "everything.");
            Forget(avatar);
            return "Could not download " + (names.Length > 0 ? names : waitingFor) + ": " + result.ErrorMessage;
        }
#endif

        /// <summary>Without the VRChat Creator Companion's resolver: what the user does instead.</summary>
        internal string Unavailable(IList<string> missing) =>
            "Add " + string.Join(" and ", missing) + " to this project in the VRChat Creator Companion, then set up " + what + " again.";

        /// <summary>
        /// Waits for the downloads, then finishes on <paramref name="avatar"/> too (each avatar asked for is set up). Each avatar
        /// is given up on <see cref="PendingTimeout"/> after it was asked for.
        /// </summary>
        internal void Expect(MyAvatar avatar)
        {
            int id = avatar != null ? avatar.GetInstanceID() : 0;
            if (id == 0) return;
            var pending = Pending();
            pending[id] = EditorApplication.timeSinceStartup;
            Save(pending);
            SessionState.EraseString(noteKey + id);
            Arm();
        }

        /// <summary>Forgets a download asked for <paramref name="avatar"/> (the setup was done another way, or the download failed).</summary>
        internal void Forget(MyAvatar avatar)
        {
            if (avatar == null) return;
            var pending = Pending();
            pending.Remove(avatar.GetInstanceID());
            Save(pending);
        }

        /// <summary>Whether a download is awaited for <paramref name="avatar"/>.</summary>
        internal bool Waiting(MyAvatar avatar) => avatar != null && Pending().ContainsKey(avatar.GetInstanceID());

        /// <summary>What happened to the setup asked for <paramref name="avatar"/> since (done, or why not), once; null when nothing new.</summary>
        internal string TakeNote(MyAvatar avatar)
        {
            if (avatar == null) return null;
            string key = noteKey + avatar.GetInstanceID();
            string note = SessionState.GetString(key, null);
            SessionState.EraseString(key);
            return string.IsNullOrEmpty(note) ? null : note;
        }

        /// <summary>Forgets every download asked before.</summary>
        internal void StopWaiting()
        {
            SessionState.EraseString(pendingKey);
            EditorApplication.update -= Poll;
        }

        /// <summary>After a script reload, keeps waiting for a download asked before it. Called from an [InitializeOnLoadMethod].</summary>
        internal void Resume()
        {
            if (Pending().Count == 0) return;
            Arm();
        }

        private void Arm()
        {
            // Packages of assets only may import without a script reload: finish as soon as everything is there.
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        // Each avatar waiting and when it was asked for ("id@seconds,...", the editor's clock, kept across script reloads).
        private Dictionary<int, double> Pending()
        {
            var pending = new Dictionary<int, double>();
            foreach (string entry in SessionState.GetString(pendingKey, "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('@');
                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id != 0)
                    pending[id] = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double since) ? since : EditorApplication.timeSinceStartup;
            }
            return pending;
        }

        private void Save(Dictionary<int, double> pending)
        {
            if (pending.Count == 0) { StopWaiting(); return; }
            SessionState.SetString(pendingKey, string.Join(",", pending.Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + "@" + p.Value.ToString("R", CultureInfo.InvariantCulture))));
        }

        private void Poll()
        {
            if (Pending().Count == 0) { EditorApplication.update -= Poll; return; }
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            TryFinish(now);
        }

        /// <summary>Finishes on every avatar waiting once the packages are imported, or gives up after <see cref="PendingTimeout"/>.</summary>
        internal void TryFinish(double now)
        {
            var pending = Pending();
            if (pending.Count == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!imported())
            {
                var late = pending.Where(p => now - p.Value >= PendingTimeout).Select(p => p.Key).ToList();
                if (late.Count == 0) return;
                foreach (int id in late) pending.Remove(id);
                Save(pending);
                string note = What + " was not set up: " + waitingFor + " were still not imported after " + PendingTimeout / 60 +
                              " minutes. Check the Creator Companion and the Console, then set it up again.";
                Debug.LogWarning("[My Avatar] " + note);
                foreach (int id in late) Tell(id, note);
                return;
            }
            StopWaiting();
            foreach (int id in pending.Keys)
            {
                var avatar = EditorUtility.InstanceIDToObject(id) as MyAvatar;
                var root = avatar != null ? TextureOptimization.AvatarRoot(avatar) : null;
                if (root == null)
                {
                    Debug.LogWarning("[My Avatar] " + What + " was not set up: the avatar it was asked for is no longer open. Set it up again from My Avatar.");
                    continue;
                }
                // A step of its own, never added to whatever the user did last.
                Undo.IncrementCurrentGroup();
                int group = Undo.GetCurrentGroup();
                string problem;
                try { problem = finish(root); }
                catch (Exception ex) { Debug.LogException(ex); problem = ex.Message; }
                Undo.SetCurrentGroupName(What + " on " + root.name);
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
                if (problem != null) Debug.LogWarning("[My Avatar] " + What + " was not set up on " + root.name + ": " + problem);
                Tell(id, problem == null ? What + " is on " + root.name + " now. Undo takes it off." : What + " was not set up: " + problem);
            }
        }

        private void Tell(int id, string note)
        {
            SessionState.SetString(noteKey + id, note);
            Finished?.Invoke(id);
        }
    }
}
