using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;

namespace Orbiters.MyAvatar.Editor
{
    internal enum BlueprintOwnershipState
    {
        /// <summary>Not checked (not signed in to Orbiters, or the server couldn't answer).</summary>
        Unknown,
        /// <summary>The avatar was published by the uploader.</summary>
        Owned,
        /// <summary>The avatar was published by another VRChat account.</summary>
        SomeoneElse,
        /// <summary>Not visible to Orbiters: private, hidden, deleted or never uploaded.</summary>
        NotFound,
        /// <summary>The Orbiters account has no linked VRChat account.</summary>
        NotLinked
    }

    internal sealed class BlueprintOwnershipResult
    {
        public string AvatarId;
        public BlueprintOwnershipState State;
        public string OwnerId;
        public string OwnerName;
        public string AvatarName;
        public string LinkedId;
        public string LinkedName;
        public string Error;
        public DateTime CheckedUtc;
    }

    /// <summary>
    /// Who published the avatar a blueprint ID points to, asked to the Orbiters server (signed-in members with a linked
    /// VRChat account), so Build &amp; Publish can stop before a build VRChat would refuse at the end of the upload.
    /// Answers are kept ten minutes; the avatar selected in the VRChat SDK panel is checked ahead of time.
    /// </summary>
    [InitializeOnLoad]
    internal static class BlueprintOwnership
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
        private static readonly object gate = new object();
        private static readonly Dictionary<string, BlueprintOwnershipResult> results = new Dictionary<string, BlueprintOwnershipResult>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Task<BlueprintOwnershipResult>> running = new Dictionary<string, Task<BlueprintOwnershipResult>>(StringComparer.Ordinal);
        private static double nextWatch;
        private static string watched;

        static BlueprintOwnership()
        {
            EditorApplication.update += Watch;
        }

        private sealed class OwnerResponse
        {
            public bool linked;
            public bool? found;
            public Person owner;
            public Person you;
            public string name;
            public bool? ownedByYou;
        }

        private sealed class Person
        {
            public string vrchatId;
            public string displayName;
        }

        /// <summary>The VRChat account the SDK uploads with, when it is signed in.</summary>
        internal static string UploaderId
        {
            get
            {
                try
                {
                    return APIUser.CurrentUser?.id;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// True when the avatar is known to belong to another VRChat account than the one uploading (the SDK's, or the
        /// linked one when the SDK isn't signed in yet). Unknown answers never block.
        /// </summary>
        internal static bool BelongsToSomeoneElse(BlueprintOwnershipResult result)
        {
            if (result == null || (result.State != BlueprintOwnershipState.Owned && result.State != BlueprintOwnershipState.SomeoneElse) ||
                string.IsNullOrEmpty(result.OwnerId))
            {
                return false;
            }

            string uploader = UploaderId;
            return !string.IsNullOrEmpty(uploader)
                ? !string.Equals(result.OwnerId, uploader, StringComparison.OrdinalIgnoreCase)
                : result.State == BlueprintOwnershipState.SomeoneElse;
        }

        /// <summary>The answer for <paramref name="avatarId"/>, waiting up to <paramref name="timeout"/> (main thread).</summary>
        internal static BlueprintOwnershipResult Wait(string avatarId, TimeSpan timeout)
        {
            var known = Cached(avatarId);
            if (known != null)
            {
                return known;
            }

            var task = Start(avatarId);
            if (task == null)
            {
                return new BlueprintOwnershipResult { AvatarId = avatarId, State = BlueprintOwnershipState.Unknown };
            }

            var started = DateTime.UtcNow;
            try
            {
                while (!task.IsCompleted && DateTime.UtcNow - started < timeout)
                {
                    float progress = (float)((DateTime.UtcNow - started).TotalMilliseconds / timeout.TotalMilliseconds);
                    EditorUtility.DisplayProgressBar("My Avatar", "Checking who published this blueprint ID…", progress);
                    task.Wait(100);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return task.IsCompleted ? task.Result : new BlueprintOwnershipResult { AvatarId = avatarId, State = BlueprintOwnershipState.Unknown, Error = "No answer in time" };
        }

        internal static BlueprintOwnershipResult Cached(string avatarId)
        {
            lock (gate)
            {
                return results.TryGetValue(avatarId, out var result) && DateTime.UtcNow - result.CheckedUtc < Fresh ? result : null;
            }
        }

        /// <summary>Asks the server now (main thread: the account is read here), unless an answer is fresh or on its way.</summary>
        internal static Task<BlueprintOwnershipResult> Start(string avatarId)
        {
            if (string.IsNullOrWhiteSpace(avatarId))
            {
                return null;
            }

            avatarId = avatarId.Trim();
            lock (gate)
            {
                if (running.TryGetValue(avatarId, out var pending))
                {
                    return pending;
                }
            }

            string token = AuthenticationService.GetAuth()?.token;
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            string url = OrbitersEnvironment.ApiUrl("vrchat/avatars/" + Uri.EscapeDataString(avatarId) + "/owner");
            var task = Task.Run(() => Ask(avatarId, url, token));
            lock (gate)
            {
                running[avatarId] = task;
            }

            return task;
        }

        private static async Task<BlueprintOwnershipResult> Ask(string avatarId, string url, string token)
        {
            var result = new BlueprintOwnershipResult { AvatarId = avatarId, CheckedUtc = DateTime.UtcNow };
            try
            {
                var answer = await OrbitersApi.SendAsync<OwnerResponse>(url, token, failure: "Orbiters returned HTTP {0} while checking the blueprint ID.").ConfigureAwait(false);
                result.LinkedId = answer?.you?.vrchatId;
                result.LinkedName = answer?.you?.displayName;
                if (answer == null)
                {
                    result.State = BlueprintOwnershipState.Unknown;
                }
                else if (!answer.linked)
                {
                    result.State = BlueprintOwnershipState.NotLinked;
                }
                else if (answer.found != true || answer.owner == null)
                {
                    result.State = BlueprintOwnershipState.NotFound;
                }
                else
                {
                    result.OwnerId = answer.owner.vrchatId;
                    result.OwnerName = answer.owner.displayName;
                    result.AvatarName = answer.name;
                    result.State = answer.ownedByYou == true ? BlueprintOwnershipState.Owned : BlueprintOwnershipState.SomeoneElse;
                }
            }
            catch (Exception exception)
            {
                result.State = BlueprintOwnershipState.Unknown;
                result.Error = exception.Message;
            }

            lock (gate)
            {
                running.Remove(avatarId);
                // Failures are asked again at the next build; answers are kept.
                if (result.State != BlueprintOwnershipState.Unknown)
                {
                    results[avatarId] = result;
                }
            }

            return result;
        }

        // The avatar shown in the SDK panel is checked as soon as it is selected, so Build & Publish doesn't wait.
        private static void Watch()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextWatch)
            {
                return;
            }

            nextWatch = now + 1.5d;
            try
            {
                if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null)
                {
                    return;
                }

                var avatar = builder.SelectedAvatar;
                var pipeline = avatar != null ? avatar.GetComponent<PipelineManager>() : null;
                string id = pipeline != null ? pipeline.blueprintId : null;
                if (string.IsNullOrWhiteSpace(id) || id == watched)
                {
                    return;
                }

                watched = id;
                if (Cached(id) == null)
                {
                    Start(id);
                }
            }
            catch (Exception)
            {
                // A destroyed avatar or a closing panel: checked again at the next build.
            }
        }
    }
}
