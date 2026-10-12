using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Vrm
{
    /// <summary>Writes a VRM file of one version with UniVRM; registered by an assembly compiled only when UniVRM is there.</summary>
    internal interface IVrmWriter
    {
        /// <summary>0 for VRM 0.x, 1 for VRM 1.0.</summary>
        int Version { get; }
        /// <summary>UniVRM's version, for the section.</summary>
        string UniVrm { get; }
        /// <summary>The .vrm file's bytes. The model's root is a copy: the writer may change it.</summary>
        byte[] Write(VrmModel model);
    }

    /// <summary>
    /// Export to VRM: the avatar built the way an upload builds it (VRCFury, Modular Avatar, MCB, and the Optimization level
    /// chosen, without what only VRChat uses), read for VRM (<see cref="VrmModel"/>), then written by UniVRM. The avatar
    /// never changes.
    /// </summary>
    internal static class VrmExport
    {
        internal static readonly List<IVrmWriter> Writers = new List<IVrmWriter>();

        internal static IVrmWriter Writer(int version) => Writers.FirstOrDefault(w => w.Version == version);

        /// <summary>A running or finished export.</summary>
        internal sealed class Job
        {
            public string Path;
            public HiddenAvatarBuild Build;
            public string Error;
            public long Bytes;
            public bool Done;
            public event Action Completed;
            internal void Finish() { Done = true; try { Completed?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); } }
        }

        // What an upload changes that a VRM has no use for: short names (the file keeps readable ones), menu icons, and
        // texture formats only VRChat reads (VRM stores PNG and JPEG).
        private const OptimizationPasses VrChatOnly = OptimizationPasses.ShortNames | OptimizationPasses.MenuIcons |
                                                      OptimizationPasses.CrunchTextures | OptimizationPasses.QuestTextures;

        /// <summary>
        /// Exports the avatar <paramref name="avatar"/> is on to <paramref name="path"/> in VRM <paramref name="version"/>, with
        /// what the file says about the model from <paramref name="settings"/> (the avatar's own when null).
        /// </summary>
        internal static Job Start(MyAvatar avatar, int version, string path, VrmExportSettings settings = null)
        {
            var job = new Job { Path = path };
            var writer = Writer(version);
            var root = TextureOptimization.AvatarRoot(avatar);
            if (writer == null) { job.Error = "UniVRM isn't installed for VRM " + (version == 0 ? "0.x" : "1.0") + "."; job.Finish(); return job; }
            settings = settings ?? avatar.vrmExport;
            string title = string.IsNullOrWhiteSpace(settings.title) ? root.name : settings.title;
            var thumbnail = avatar.thumbnail;
            var passes = avatar.uploadOptimization.Passes & ~VrChatOnly;
            GameObject built = null;
            job.Build = HiddenAvatarBuild.Start(root, type => type.Name == "ParameterCompressorHook", copy =>
            {
                foreach (var own in copy.GetComponentsInChildren<MyAvatar>(true))
                {
                    own.uploadOptimization.level = passes == OptimizationPasses.None ? OptimizationLevel.Off : OptimizationLevel.Custom;
                    own.uploadOptimization.custom = passes;
                }
                // The shapes the VRM's expressions set survive the build steps that remove unused ones.
                built = copy;
                BlendShapePruning.Keep[copy] = VrmModel.Shapes(copy);
            });
            void Built()
            {
                try
                {
                    if (job.Build.Error != null) { job.Error = job.Build.Error; return; }
                    var model = VrmModel.From(job.Build.Copy, settings, title, thumbnail);
                    var bytes = writer.Write(model);
                    File.WriteAllBytes(path, bytes);
                    job.Bytes = bytes.LongLength;
                    Debug.Log("[My Avatar] Exported “" + title + "” to VRM " + (version == 0 ? "0.x" : "1.0") + ": " + path + " (" + VrcLoadingBar.Format(job.Bytes) + ").");
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    job.Error = ex.Message;
                }
                finally
                {
                    if (built != null) BlendShapePruning.Keep.Remove(built);
                    job.Build.Release();
                    job.Finish();
                }
            }
            job.Build.Completed += Built;
            if (job.Build.Finished) Built();
            return job;
        }
    }
}
