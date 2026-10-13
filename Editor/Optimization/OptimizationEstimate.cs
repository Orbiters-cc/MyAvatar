using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// What VRChat would rank the avatar now and once the Optimization card's changes run at upload: the SDK's own stats
    /// (PC and mobile) of the scene avatar, then of a hidden copy put through the same changes. The copy is made inactive in
    /// a preview scene (no editor script on it wakes up) and measured over a few editor frames, so the Inspector never
    /// waits for it. Tools that build at upload (VRCFury, Modular Avatar) have not run on it: it is an estimate.
    /// </summary>
    internal static class OptimizationEstimate
    {
        internal sealed class Snapshot
        {
            public PerformanceRating Pc, Mobile;
            /// <summary>What sets each rank: the categories rated as low as the whole avatar, with their value.</summary>
            public readonly List<string> PcLimits = new List<string>(), MobileLimits = new List<string>();
            public int Polys, Skinned, Meshes, Materials, Bones, PhysBones, PhysBoneTransforms, Colliders, Contacts, Constraints, BlendShapes;
            /// <summary>Texture memory in the formats a PC build and a mobile build will have.</summary>
            public float TextureMegabytes, QuestTextureMegabytes;
            /// <summary>The file players download as VRChat shows it, estimated (see <see cref="DownloadSize"/>), on PC and on mobile.</summary>
            public long Download, QuestDownload;
            /// <summary>The bundle the SDK uploads, compressed and unpacked: what VRChat's limits apply to.</summary>
            public long Bundle, QuestBundle, Unpacked, QuestUnpacked;
            /// <summary>Seconds from the end of the download until the avatar shows, on a desktop PC and on a Quest.</summary>
            public float Load, QuestLoad;
        }

        internal sealed class Result
        {
            public Snapshot Now, Upload;
            public UploadOptimizer.Report Report;
            /// <summary>Why it couldn't be measured, or null. It stays until something changes or the card tries again.</summary>
            public string Error;
            /// <summary>Measured while meshes were still being simplified in the background: measured again once they are.</summary>
            public bool Partial;
        }

        private sealed class Job
        {
            public string Key;
            public MyAvatar Avatar;
            public OptimizationPasses Passes;
            public Result Result = new Result();
            public int Step;
            /// <summary>The upload's texture copies, for PC and for mobile.</summary>
            public TextureOptimization.Plan PcTextures, QuestTextures;
            /// <summary>Textures whose download is measured first, one per editor frame (null: not listed yet).</summary>
            public Queue<(Texture Texture, TextureOptimization.TexturePlan Planned)> Unmeasured;
            /// <summary>The texture being compressed on another thread for its measure.</summary>
            public (string Key, Task<long> Deflate, string Name)? Measuring;
            public readonly List<Action> Ready = new List<Action>();
        }

        // The estimates measured and being measured. Tests measure in a state of their own (Isolated): the user's estimates
        // stay as they are.
        private sealed class State
        {
            public readonly Dictionary<int, (string Key, Result Result)> Done = new Dictionary<int, (string, Result)>();
            public readonly Dictionary<int, Job> Running = new Dictionary<int, Job>();
            // The last estimate each avatar got, whatever changed since: the card shows its rank while measuring again.
            public readonly Dictionary<int, Result> Last = new Dictionary<int, Result>();
        }

        private static State state = new State();

        /// <summary>
        /// Estimates of their own until disposed (tests): the user's stay as they are, their jobs waiting meanwhile.
        /// </summary>
        internal static IDisposable Isolated()
        {
            var outer = state;
            state = new State();
            return new Scope(() =>
            {
                state = outer;
                if (state.Running.Count > 0) { EditorApplication.update -= Tick; EditorApplication.update += Tick; }
            });
        }

        private sealed class Scope : IDisposable
        {
            private Action end;
            public Scope(Action end) => this.end = end;
            public void Dispose() { end?.Invoke(); end = null; }
        }

        /// <summary>
        /// The estimate for the avatar as it is and its chosen changes, or null while it is measured; <paramref name="ready"/>
        /// runs once it is.
        /// </summary>
        internal static Result Get(MyAvatar avatar, OptimizationPasses passes, Action ready)
        {
            int id = avatar.GetInstanceID();
            string key = Key(avatar, passes);
            if (state.Done.TryGetValue(id, out var done) && done.Key == key) return done.Result;
            if (!state.Running.TryGetValue(id, out var job) || job.Key != key)
            {
                state.Running[id] = job = new Job { Key = key, Avatar = avatar, Passes = passes };
                EditorApplication.update -= Tick; EditorApplication.update += Tick;
            }
            if (ready != null && !job.Ready.Contains(ready)) job.Ready.Add(ready);
            return null;
        }

        /// <summary>The last estimate the avatar got, measured for its settings then (null before the first).</summary>
        internal static Result Latest(MyAvatar avatar) => avatar && state.Last.TryGetValue(avatar.GetInstanceID(), out var result) ? result : null;

        /// <summary>Forgets a failed estimate: the next card measures again.</summary>
        internal static void Retry(MyAvatar avatar)
        {
            if (avatar && state.Done.TryGetValue(avatar.GetInstanceID(), out var done) && done.Result.Error != null) state.Done.Remove(avatar.GetInstanceID());
        }

        // Meshes simplified in the background change what the copy uploads: what was measured without them is measured again.
        static OptimizationEstimate() => PolygonReduction.Ready += () =>
        {
            foreach (int id in state.Done.Where(p => p.Value.Result.Partial).Select(p => p.Key).ToList()) state.Done.Remove(id);
        };

        /// <summary>Forgets every estimate: the next card measures again.</summary>
        internal static void Clear()
        {
            state.Done.Clear();
            state.Last.Clear();
        }

        /// <summary>Makes estimates fail with this message (tests, and captures of the card's failure state).</summary>
        internal static string FailWith;

        /// <summary>Measures every pending estimate now, without waiting for editor frames (tests).</summary>
        internal static void Finish()
        {
            for (int guard = 0; state.Running.Count > 0 && guard < 10000; guard++)
            {
                foreach (var job in state.Running.Values) job.Measuring?.Deflate.Wait();
                Tick();
            }
        }

        // One step of one job per editor frame.
        private static void Tick()
        {
            var running = state.Running;
            var job = running.Values.FirstOrDefault();
            if (job == null) { EditorApplication.update -= Tick; return; }
            int id = running.First(p => p.Value == job).Key;
            try
            {
                if (!job.Avatar || EditorApplication.isPlayingOrWillChangePlaymode) { running.Remove(id); return; }
                if (FailWith != null) throw new InvalidOperationException(FailWith);
                var root = TextureOptimization.AvatarRoot(job.Avatar);
                // First the textures' downloads for the platform the project builds for, as they stand and as the upload's
                // copies (made now, the upload reuses them).
                bool mobile = TextureOptimization.Mobile;
                if (job.Unmeasured == null)
                {
                    if ((job.Passes & TextureOptimization.TexturePasses) != 0)
                    {
                        job.PcTextures = TextureOptimization.Build(job.Avatar, false);
                        job.QuestTextures = TextureOptimization.Build(job.Avatar, true);
                    }
                    job.Unmeasured = new Queue<(Texture, TextureOptimization.TexturePlan)>(DownloadSize.Unmeasured(root, mobile, mobile ? job.QuestTextures : job.PcTextures));
                    return;
                }
                // A texture's data is compressed on another thread: its measure is kept here once it is done.
                if (job.Measuring != null)
                {
                    var (key, deflate, name) = job.Measuring.Value;
                    if (!deflate.IsCompleted) return;
                    job.Measuring = null;
                    if (deflate.Status == TaskStatus.RanToCompletion) DownloadSize.Record(key, deflate.Result);
                    else Debug.LogWarning("[My Avatar] Optimization: " + name + "'s download is estimated, not measured (" + deflate.Exception?.GetBaseException().Message + ").");
                    return;
                }
                if (job.Unmeasured.Count > 0)
                {
                    var (texture, planned) = job.Unmeasured.Dequeue();
                    if (texture)
                        try
                        {
                            var started = DownloadSize.StartMeasure(texture, mobile, planned);
                            if (started != null) job.Measuring = (started.Value.Key, started.Value.Deflate, texture.name);
                        }
                        catch (Exception ex) { Debug.LogWarning("[My Avatar] Optimization: " + texture.name + "'s download is estimated, not measured (" + ex.Message + ")."); }
                    return;
                }
                // Written once for the job's textures.
                DownloadSize.SaveMeasures();
                if (job.Step == 0) job.Result.Now = Measure(root, Kept(root, McbPrunes), null, null);
                // Off: the upload is the avatar as it is, no copy needed.
                if (job.Step == 1 || job.Passes == OptimizationPasses.None)
                {
                    if (job.Passes == OptimizationPasses.None) { job.Result.Upload = job.Result.Now; job.Result.Report = new UploadOptimizer.Report(); }
                    // The textures' copies the upload would get, for each platform: their sizes and formats count.
                    else job.Result.Upload = Copy(root, id, job.Passes, job.Avatar.uploadOptimization.PolygonQuality, job.PcTextures, job.QuestTextures,
                        out job.Result.Report, out job.Result.Partial);
                    Finished(id, job);
                    return;
                }
                job.Step++;
            }
            catch (Exception ex)
            {
                // Kept with its settings: the card says why, and nothing measures again until something changes.
                Debug.LogException(ex);
                job.Result = new Result { Error = ex is UploadOptimizer.PassFailed ? ex.Message : ex.GetBaseException().Message };
                Finished(id, job);
            }
        }

        private static void Finished(int id, Job job)
        {
            DownloadSize.SaveMeasures();
            state.Running.Remove(id);
            state.Done[id] = (job.Key, job.Result);
            if (job.Result.Error == null) state.Last[id] = job.Result;
            // Avatars gone since leave the estimates.
            foreach (int gone in state.Done.Keys.Concat(state.Last.Keys).Where(k => EditorUtility.InstanceIDToObject(k) == null).Distinct().ToList())
            {
                state.Done.Remove(gone);
                state.Last.Remove(gone);
            }
            foreach (var callback in job.Ready)
                try { callback(); } catch (Exception ex) { Debug.LogException(ex); }
        }

        // The hidden copy, put through the structure and material steps. Blendshapes leave no mark on the rank: they are
        // counted, not removed (the slowest step on a sculpted body).
        private static Snapshot Copy(GameObject root, int owner, OptimizationPasses passes, float quality, TextureOptimization.Plan pcTextures,
            TextureOptimization.Plan questTextures, out UploadOptimizer.Report report, out bool partial)
        {
            report = new UploadOptimizer.Report();
            var scene = EditorSceneManager.NewPreviewScene();
            var before = new HashSet<Object>();
            var temporary = new List<Object>();
            GameObject copy = null;
            try
            {
                var holder = new GameObject("My Avatar optimization estimate");
                holder.SetActive(false);
                SceneManager.MoveGameObjectToScene(holder, scene);
                copy = Object.Instantiate(root, holder.transform);
                copy.name = root.name;
                foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                {
                    before.UnionWith(renderer.sharedMaterials.Where(m => m != null));
                    var mesh = Toolkit.Editor.VRChat.Optimization.MeshOptimization.MeshOf(renderer);
                    if (mesh != null) before.Add(mesh);
                }
                bool prunes = (passes & OptimizationPasses.BlendShapes) != 0;
                var kept = Kept(copy, prunes ? (Func<SkinnedMeshRenderer, bool>)(_ => true) : McbPrunes, (passes & OptimizationPasses.ConstantBlendShapes) != 0);
                UploadOptimizer.RunStructure(copy, passes & ~OptimizationPasses.BlendShapes, report, unbuilt: true, polygonQuality: quality, temporary: temporary, shapes: kept,
                    owner: owner);
                // Meshes still simplifying in the background: measured again once they are ready.
                partial = PolygonReduction.Measuring;
                // The copies the upload's textures get are counted from the plans, not made here.
                UploadOptimizer.RunMaterials(copy, passes, report);
                report.ResizedTextures = (TextureOptimization.Mobile ? questTextures : pcTextures)?.changes.Count ?? 0;
                var snapshot = Measure(copy, kept, pcTextures, questTextures);
                if (prunes)
                    foreach (var renderer in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null))
                    {
                        int unused = renderer.sharedMesh.blendShapeCount - kept(renderer).Count;
                        report.BlendShapes += unused;
                        snapshot.BlendShapes -= unused;
                    }
                return snapshot;
            }
            finally
            {
                // What the steps made lives only for this measure, also when one failed.
                if (copy != null)
                    foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                    {
                        foreach (var material in renderer.sharedMaterials)
                            if (material != null && !before.Contains(material) && !EditorUtility.IsPersistent(material)) Object.DestroyImmediate(material);
                        var mesh = Toolkit.Editor.VRChat.Optimization.MeshOptimization.MeshOf(renderer);
                        if (mesh != null && !before.Contains(mesh) && !EditorUtility.IsPersistent(mesh)) Object.DestroyImmediate(mesh);
                    }
                foreach (var value in temporary) if (value != null) Object.DestroyImmediate(value);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // MCB removes the unused blendshapes of its custom base's meshes (made under Assets/MCB/) at every upload.
        private static bool McbPrunes(SkinnedMeshRenderer renderer) =>
            renderer.sharedMesh != null && AssetDatabase.GetAssetPath(renderer.sharedMesh).StartsWith("Assets/MCB/", StringComparison.Ordinal);

        // The blendshapes a pruned mesh keeps on an avatar not built yet: what its animations use, and the shapes VRCFury's and
        // Modular Avatar's components name or animate once they have built (a toggle or slider, a merged animator). Other
        // tools' settings name shapes too (MCB's blendshape links), but don't animate them.
        private static Func<SkinnedMeshRenderer, ICollection<string>> Kept(GameObject root, Func<SkinnedMeshRenderer, bool> pruned, bool constantsUnused = false)
        {
            var named = new HashSet<string>(StringComparer.Ordinal);
            // Shapes the build tools' animations animate, by the name of the object they animate (their paths start from
            // wherever the tool merges them, so only the object's own name can be matched).
            var animated = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var clips = new HashSet<AnimationClip>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!Toolkit.Editor.VRChat.VrcFury.IsComponent(component) && !Toolkit.Editor.VRChat.ModularAvatar.IsComponent(component)) continue;
                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(property.stringValue)) named.Add(property.stringValue);
                        // The animations a build tool will merge in (a full controller, a merged animator): the shapes they animate.
                        else if (property.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            if (property.objectReferenceValue is RuntimeAnimatorController controller) clips.UnionWith(controller.animationClips.Where(c => c != null));
                            else if (property.objectReferenceValue is AnimationClip clip) clips.Add(clip);
                        }
                    }
                }
            }
            foreach (var clip in clips)
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    {
                        string target = binding.path.Substring(binding.path.LastIndexOf('/') + 1);
                        if (!animated.TryGetValue(target, out var shapes)) animated[target] = shapes = new HashSet<string>(StringComparer.Ordinal);
                        shapes.Add(binding.propertyName.Substring("blendShape.".Length));
                    }
            return renderer =>
            {
                if (!pruned(renderer)) return null;
                var kept = BlendShapePruning.Used(root, renderer, constantsUnused);
                var mesh = renderer.sharedMesh;
                animated.TryGetValue(renderer.name, out var shapes);
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (named.Contains(name) || shapes != null && shapes.Contains(name)) kept.Add(name);
                }
                return kept;
            };
        }

        // With texture plans, each texture counts as the copy the upload gets. The SDK counts texture memory on what the
        // editor holds (often uncompressed): the rank takes the build's figure instead, the upload's copies included.
        private static Snapshot Measure(GameObject root, Func<SkinnedMeshRenderer, ICollection<string>> kept, TextureOptimization.Plan pcTextures,
            TextureOptimization.Plan questTextures)
        {
            float pcMegabytes = TextureMegabytes(root, false, pcTextures), mobileMegabytes = TextureMegabytes(root, true, questTextures);
            var pc = Stats(root, false, pcMegabytes);
            var mobile = Stats(root, true, mobileMegabytes);
            var snapshot = new Snapshot
            {
                Pc = pc.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall),
                Mobile = mobile.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall),
                Polys = pc.polyCount ?? 0, Skinned = pc.skinnedMeshCount ?? 0, Meshes = pc.meshCount ?? 0, Materials = pc.materialCount ?? 0,
                Bones = pc.boneCount ?? 0, Contacts = pc.contactCount ?? 0, Constraints = pc.constraintsCount ?? 0,
                TextureMegabytes = pcMegabytes, QuestTextureMegabytes = mobileMegabytes,
                PhysBones = pc.physBone?.componentCount ?? 0, PhysBoneTransforms = pc.physBone?.transformCount ?? 0,
                Colliders = pc.physBone?.colliderCount ?? 0,
                BlendShapes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).Sum(r => r.sharedMesh.blendShapeCount),
            };
            var pcParts = DownloadSize.Of(root, false, pcTextures == null ? null : (Func<Texture, TextureOptimization.TexturePlan>)pcTextures.For, kept);
            var questParts = DownloadSize.Of(root, true, questTextures == null ? null : (Func<Texture, TextureOptimization.TexturePlan>)questTextures.For, kept);
            snapshot.Download = pcParts.Total; snapshot.QuestDownload = questParts.Total;
            snapshot.Bundle = pcParts.Bundle; snapshot.QuestBundle = questParts.Bundle;
            snapshot.Unpacked = pcParts.Raw; snapshot.QuestUnpacked = questParts.Raw;
            snapshot.Load = DownloadSize.LoadSeconds(pcParts, root, false);
            snapshot.QuestLoad = DownloadSize.LoadSeconds(questParts, root, true);
            Limits(pc, snapshot.Pc, snapshot.PcLimits);
            Limits(mobile, snapshot.Mobile, snapshot.MobileLimits);
            return snapshot;
        }

        // Every texture the renderers' materials point at, as VRChat counts them, in the formats the build will have (or as
        // the upload's copies): the SDK's own figure in the editor counts the uncompressed copies the editor keeps until a build.
        private static float TextureMegabytes(GameObject root, bool mobile, TextureOptimization.Plan plan)
        {
            var textures = new HashSet<Texture>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var material in renderer.sharedMaterials)
                    if (material != null)
                        foreach (int id in material.GetTexturePropertyNameIDs())
                        {
                            var texture = material.GetTexture(id);
                            if (texture != null) textures.Add(texture);
                        }
            return textures.Sum(t => plan?.For(t)?.after ?? TextureOptimization.BuildBytes(t, mobile)) / 1048576f;
        }

        private static void Limits(AvatarPerformanceStats stats, PerformanceRating overall, List<string> limits)
        {
            if (overall <= PerformanceRating.Excellent) return;
            foreach (AvatarPerformanceCategory category in Enum.GetValues(typeof(AvatarPerformanceCategory)))
            {
                if (category == AvatarPerformanceCategory.None || category == AvatarPerformanceCategory.Overall || category == AvatarPerformanceCategory.AvatarPerformanceCategoryCount) continue;
                if (stats.GetPerformanceRatingForCategory(category) != overall) continue;
                string value = Value(stats, category);
                limits.Add(AvatarPerformanceStats.GetPerformanceCategoryDisplayName(category).ToLowerInvariant() + (value != null ? " (" + value + ")" : ""));
            }
        }

        private static string Value(AvatarPerformanceStats stats, AvatarPerformanceCategory category)
        {
            int? value;
            switch (category)
            {
                case AvatarPerformanceCategory.PolyCount: value = stats.polyCount; break;
                case AvatarPerformanceCategory.SkinnedMeshCount: value = stats.skinnedMeshCount; break;
                case AvatarPerformanceCategory.MeshCount: value = stats.meshCount; break;
                case AvatarPerformanceCategory.MaterialCount: value = stats.materialCount; break;
                case AvatarPerformanceCategory.BoneCount: value = stats.boneCount; break;
                case AvatarPerformanceCategory.ContactCount: value = stats.contactCount; break;
                case AvatarPerformanceCategory.ConstraintsCount: value = stats.constraintsCount; break;
                case AvatarPerformanceCategory.PhysBoneComponentCount: value = stats.physBone?.componentCount; break;
                case AvatarPerformanceCategory.PhysBoneTransformCount: value = stats.physBone?.transformCount; break;
                case AvatarPerformanceCategory.PhysBoneColliderCount: value = stats.physBone?.colliderCount; break;
                case AvatarPerformanceCategory.PhysBoneCollisionCheckCount: value = stats.physBone?.collisionCheckCount; break;
                case AvatarPerformanceCategory.TextureMegabytes:
                    return stats.textureMegabytes is float megabytes ? megabytes.ToString(megabytes < 10f ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB" : null;
                default: return null;
            }
            return value?.ToString("N0", CultureInfo.InvariantCulture);
        }

        // The SDK's stats, its texture memory replaced by the build's and every rating recomputed with it.
        private static AvatarPerformanceStats Stats(GameObject root, bool mobile, float textureMegabytes)
        {
            var stats = new AvatarPerformanceStats(mobile);
            AvatarPerformance.CalculatePerformanceStats(root.name, root, stats, mobile);
            stats.textureMegabytes = textureMegabytes;
            stats.CalculateAllPerformanceRatings(mobile);
            return stats;
        }

        // Measured again once the avatar's objects, components, meshes, materials or imported assets change.
        private static string Key(MyAvatar avatar, OptimizationPasses passes)
        {
            var key = new System.Text.StringBuilder().Append((int)passes).Append('|').Append(TextureOptimization.ChangeKey(avatar));
            if ((passes & OptimizationPasses.FewerPolygons) != 0) key.Append('|').Append(avatar.uploadOptimization.PolygonQuality.ToString("R", CultureInfo.InvariantCulture));
            if ((passes & OptimizationPasses.ResizeTextures) != 0) key.Append('|').Append(avatar.uploadOptimization.SharpFrom.ToString("R", CultureInfo.InvariantCulture));
            // The platform the textures are planned and measured for.
            key.Append('|').Append(TextureOptimization.PlatformName(TextureOptimization.Mobile));
            foreach (var component in TextureOptimization.AvatarRoot(avatar).GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                key.Append(',').Append(component.GetInstanceID());
                if (component is Transform t) key.Append(t.gameObject.activeSelf ? '+' : '-');
                else if (component is Behaviour b) key.Append(b.enabled ? '+' : '-');
                else if (component is Renderer r) key.Append(r.enabled ? '+' : '-');
            }
            return key.ToString();
        }
    }
}
