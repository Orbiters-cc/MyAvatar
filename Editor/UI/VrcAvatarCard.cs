using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace Orbiters.MyAvatar.Editor
{
    // What the VRChat SDK tells Orbiters Toolkit's avatar card: who is signed in, the build target's platform badge and the
    // SDK's own performance rank.
    internal static class VrcAvatarCardSdk
    {
        // The SDK signs in on its own, also again after each script reload; the author line follows once it has.
        internal static void FollowSignedInUser(VrcAvatarCard card)
        {
            void Update() => card.SetAuthor(VRC.Core.APIUser.CurrentUser?.displayName);
            Update();
            card.schedule.Execute(Update).Every(1000);
        }

        // The Windows badge always, the Android one when building for Android, as the card is shown in the game.
        internal static void ShowBuildTarget(VrcAvatarCard card) =>
            card.SetPlatforms(true, EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android);

        // VRChat shows a warning badge on Poor and Very Poor avatars; the rating is the SDK's own, for the build target. It
        // walks the whole avatar, so it is kept for the session while the avatar stays as it is (owner: the My Avatar whose
        // optimization estimate key says so; null measures every time).
        internal static void ShowPerformance(VrcAvatarCard card, GameObject avatar, MyAvatar owner = null)
        {
            var rating = PerformanceRating.None;
            if (avatar)
            {
                bool mobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
                string key = "Orbiters.MyAvatar.CardRating." + avatar.GetInstanceID();
                string state = owner ? Optimization.OptimizationEstimate.Fingerprint(owner) + "|" + mobile + "|" : null;
                string kept = state != null ? SessionState.GetString(key, "") : "";
                if (state != null && kept.StartsWith(state, System.StringComparison.Ordinal) && int.TryParse(kept.Substring(state.Length), out int known))
                    rating = (PerformanceRating)known;
                else
                {
                    var stats = new AvatarPerformanceStats(mobile);
                    AvatarPerformance.CalculatePerformanceStats(avatar.name, avatar, stats, mobile);
                    rating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
                    if (state != null) SessionState.SetString(key, state + (int)rating);
                }
            }
            card.SetPerformance(rating == PerformanceRating.VeryPoor ? VrcPerformanceWarning.VeryPoor
                : rating == PerformanceRating.Poor ? VrcPerformanceWarning.Poor : VrcPerformanceWarning.None);
        }
    }
}
