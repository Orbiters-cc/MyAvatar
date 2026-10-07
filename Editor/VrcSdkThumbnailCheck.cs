using System;
using UnityEditor;
using UnityEngine;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.BuildPipeline;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Stops Build &amp; Publish before the build when the thumbnail waiting in the SDK panel is the very image VRChat
    /// already holds for the avatar: VRChat refuses it ("This file was already uploaded"), but only after the whole build
    /// and the bundle upload. The thumbnail is taken out of the upload, so Build &amp; Publish works when clicked again.
    /// Only on a recent answer from VRChat (<see cref="VrcSdkThumbnail.PendingDuplicate"/>): the build is never held up
    /// waiting for one. Build &amp; Test is never stopped.
    /// </summary>
    internal sealed class VrcSdkThumbnailCheck : IVRCSDKBuildRequestedCallback
    {
        public int callbackOrder => -999;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            if (requestedBuildType != VRCSDKRequestedBuildType.Avatar || VRC_SdkBuilder.ActiveBuildType != VRC_SdkBuilder.BuildType.Publish)
            {
                return true;
            }

            try
            {
                if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null)
                {
                    return true;
                }

                GameObject avatar;
                try
                {
                    avatar = builder.SelectedAvatar;
                }
                catch (MissingReferenceException)
                {
                    return true;
                }

                string id = avatar ? avatar.GetComponent<VRC.Core.PipelineManager>()?.blueprintId?.Trim() : null;
                string duplicate = VrcSdkThumbnail.PendingDuplicate(builder, id);
                if (duplicate == null || !VrcSdkThumbnail.Withdraw(builder))
                {
                    return true;
                }

                Debug.LogWarning("[My Avatar] Build & Publish stopped before the build: the thumbnail waiting in the VRChat SDK (" + duplicate +
                                 ") is already the thumbnail of " + avatar.name + " (" + id + ") on VRChat, which refuses the same image twice. " +
                                 "My Avatar took it out of the upload: click Build & Publish again.");
                // The SDK shows its "build aborted" dialog right after the callback; editor updates resume once it is closed.
                void Explain()
                {
                    EditorApplication.update -= Explain;
                    EditorUtility.DisplayDialog("My Avatar",
                        "VRChat already has this exact thumbnail for " + avatar.name + ", and refuses to upload the same image twice. " +
                        "It would only have said so at the end of the upload.\n\n" +
                        "My Avatar took the thumbnail out of this upload. Click Build & Publish again: the avatar is uploaded with its current thumbnail.",
                        "OK");
                }

                EditorApplication.update += Explain;
                return false;
            }
            catch (Exception)
            {
                // A failed check never blocks an upload.
                return true;
            }
        }
    }
}
