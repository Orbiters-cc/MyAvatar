using System;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Stops Build &amp; Publish before the build when the avatar's blueprint ID belongs to an avatar another VRChat account
    /// published: VRChat would refuse the upload, but only after the whole build and upload. The VRChat SDK then says the
    /// build was aborted, and My Avatar explains why and offers to clear the ID (<see cref="BlueprintOwnerWindow"/>).
    /// Only when the answer is known: without an Orbiters account, a linked VRChat account or an answer in time, the
    /// build goes on as before. Build &amp; Test is never stopped.
    /// </summary>
    internal sealed class BlueprintOwnershipCheck : IVRCSDKBuildRequestedCallback
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

        public int callbackOrder => -1000;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            if (requestedBuildType != VRCSDKRequestedBuildType.Avatar || VRC_SdkBuilder.ActiveBuildType != VRC_SdkBuilder.BuildType.Publish)
            {
                return true;
            }

            try
            {
                var avatar = SelectedAvatar();
                var pipeline = avatar != null ? avatar.GetComponent<PipelineManager>() : null;
                string id = pipeline != null ? pipeline.blueprintId : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    return true;
                }

                var result = BlueprintOwnership.Wait(id.Trim(), Timeout);
                if (!BlueprintOwnership.BelongsToSomeoneElse(result))
                {
                    return true;
                }

                BlueprintOwnerWindow.ShowAfterSdkDialog(pipeline, result);
                return false;
            }
            catch (Exception)
            {
                // A failed check never blocks an upload.
                return true;
            }
        }

        private static GameObject SelectedAvatar()
        {
            if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null)
            {
                return null;
            }

            try
            {
                return builder.SelectedAvatar;
            }
            catch (MissingReferenceException)
            {
                return null;
            }
        }
    }
}
