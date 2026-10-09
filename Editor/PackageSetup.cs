using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if MYAVATAR_VPM
using Orbiters.Toolkit.Editor.Vpm;
#endif

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// The downloads of a one-click setup (face tracking, GoGoLoco): the missing packages come through VPM, then the setup
    /// finishes on the avatar as soon as Unity has imported them, after a script reload too. A download that never arrives
    /// is given up on after a few minutes, so a later install does not set anything up unasked.
    /// </summary>
    internal sealed class PackageSetup
    {
        // A download is waited for this long, checked about once a second.
        private const double PendingTimeout = 300, PollSeconds = 1;
        private readonly string pendingKey, sinceKey, what, waitingFor;
        private readonly Func<bool> imported;
        private readonly Func<GameObject, string> finish;
        private double nextPoll;

        /// <param name="key">Names the setup's pending state (Orbiters.MyAvatar.&lt;key&gt;.PendingAvatar).</param>
        /// <param name="what">What is set up, as in "set up face tracking".</param>
        /// <param name="waitingFor">The packages, for the warning when they never arrive ("VRCFury or GoGoLoco").</param>
        /// <param name="imported">Whether everything the setup needs is in the project.</param>
        /// <param name="finish">Finishes on the avatar's root: null when done (it logs what it did), else why not.</param>
        internal PackageSetup(string key, string what, string waitingFor, Func<bool> imported, Func<GameObject, string> finish)
        {
            pendingKey = "Orbiters.MyAvatar." + key + ".PendingAvatar";
            sinceKey = "Orbiters.MyAvatar." + key + ".PendingSince";
            this.what = what; this.waitingFor = waitingFor; this.imported = imported; this.finish = finish;
        }

        private string What => char.ToUpperInvariant(what[0]) + what.Substring(1);

        /// <summary>Downloads <paramref name="missing"/> (package ids), then finishes on <paramref name="avatar"/>. Returns a note for the user.</summary>
        internal string Download(MyAvatar avatar, IList<string> missing)
        {
#if MYAVATAR_VPM
            var dependencies = VpmDependencies.For("orbiters.myavatar");
            var statuses = missing.Select(id => dependencies.OptionalStatus(id, true)).ToList();
            if (statuses.Any(s => s == null)) return "My Avatar does not know where to download " + string.Join(" and ", missing) + ".";
            SessionState.SetInt(pendingKey, avatar != null ? avatar.GetInstanceID() : 0);
            SessionState.SetFloat(sinceKey, (float)EditorApplication.timeSinceStartup);
            VpmDependencyInstallResult result;
            try { result = VpmDependencies.Install(statuses); }
            catch (Exception ex) { result = new VpmDependencyInstallResult { Errors = { ex.Message } }; }
            if (result.Success)
            {
                // Packages of assets only may import without a script reload: finish as soon as everything is there.
                EditorApplication.update -= Poll;
                EditorApplication.update += Poll;
                return "Downloaded " + string.Join(" and ", statuses.Select(s => s.DisplayName)) + ". " + What + " is set up as soon as Unity has imported them.";
            }
            StopWaiting();
            return "Could not download " + string.Join(" and ", statuses.Select(s => s.DisplayName)) + ": " + result.ErrorMessage;
#else
            return "Add " + string.Join(" and ", missing) + " to this project in the VRChat Creator Companion, then set up " + what + " again.";
#endif
        }

        /// <summary>Forgets a download asked before (the setup was done another way).</summary>
        internal void StopWaiting()
        {
            SessionState.EraseInt(pendingKey);
            SessionState.EraseFloat(sinceKey);
            EditorApplication.update -= Poll;
        }

        /// <summary>After a script reload, keeps waiting for a download asked before it. Called from an [InitializeOnLoadMethod].</summary>
        internal void Resume()
        {
            if (SessionState.GetInt(pendingKey, 0) == 0) return;
            if (SessionState.GetFloat(sinceKey, -1f) < 0f) SessionState.SetFloat(sinceKey, (float)EditorApplication.timeSinceStartup);
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        private void Poll()
        {
            if (SessionState.GetInt(pendingKey, 0) == 0) { EditorApplication.update -= Poll; return; }
            double now = EditorApplication.timeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollSeconds;
            TryFinish(now);
        }

        private void TryFinish(double now)
        {
            int id = SessionState.GetInt(pendingKey, 0);
            if (id == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!imported())
            {
                if (now - SessionState.GetFloat(sinceKey, (float)now) < PendingTimeout) return;
                StopWaiting();
                Debug.LogWarning("[My Avatar] " + What + " was not set up: " + waitingFor + " were still not imported after " + PendingTimeout / 60 +
                                 " minutes. Check the Creator Companion and the Console, then set it up again from My Avatar.");
                return;
            }
            StopWaiting();
            var avatar = EditorUtility.InstanceIDToObject(id) as MyAvatar;
            var root = avatar != null ? TextureOptimization.AvatarRoot(avatar) : null;
            if (root == null)
            {
                Debug.LogWarning("[My Avatar] " + What + " was not set up: the avatar it was asked for is no longer open. Set it up again from My Avatar.");
                return;
            }
            string problem = finish(root);
            if (problem != null) Debug.LogWarning("[My Avatar] " + What + " was not set up on " + root.name + ": " + problem);
        }
    }
}
