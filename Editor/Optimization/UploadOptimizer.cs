using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// The Optimization card at upload, on the build copy only (the scene avatar never changes). Its structure step runs
    /// once every tool that adds animations has (Modular Avatar, VRCFury, MCB's last step at -8800) and before
    /// d4rkAvatarOptimizer and AvatarOptimizer (-1025): they get an avatar already cleaned. Its material step runs after
    /// Poiyomi locks its materials (100), on the locked ones. Play mode (Gesture Manager, Av3Emulator) uploads nothing and
    /// keeps the avatar as it is.
    /// </summary>
    internal static class UploadOptimizer
    {
        internal sealed class Report
        {
            public int HiddenObjects, DisabledComponents, IdlePhysBones, Colliders, StillPhysBones, BlendShapes, HiddenTriangles, MergedMeshes, StaticBones, BoneReferences, EndBones, MaterialSlots, Textures;
            public int ToggledMeshes, Polygons, Icons, ShortObjects, ShortParameters, DuplicateTextures, ResizedTextures, Bounds;

            public override string ToString()
            {
                var parts = new List<string>();
                var changes = new List<string>();
                void Add(int n, string one, string many) { if (n > 0) parts.Add(n + " " + (n == 1 ? one : many)); }
                void Change(int n, string one, string many) { if (n > 0) changes.Add(n + " " + (n == 1 ? one : many)); }
                Change(ToggledMeshes, "mesh toggled with another merged into it", "meshes toggled with others merged into them");
                Change(ResizedTextures, "texture resized or compressed", "textures resized or compressed");
                Change(Icons, "menu icon shrunk to 64 px", "menu icons shrunk to 64 px");
                Change(Bounds, "mesh given the avatar's culling box", "meshes given the avatar's culling box");
                Change(ShortObjects, "object renamed", "objects renamed");
                Change(ShortParameters, "parameter renamed", "parameters renamed");
                Add(Polygons, "polygon that hardly changed the shape", "polygons that hardly changed the shape");
                Add(HiddenObjects, "hidden object", "hidden objects");
                Add(DisabledComponents, "disabled component", "disabled components");
                Add(IdlePhysBones, "PhysBone moving nothing", "PhysBones moving nothing");
                Add(Colliders, "unused collider", "unused colliders");
                Add(StillPhysBones, "PhysBone no longer following animations", "PhysBones no longer following animations");
                Add(BlendShapes, "unused blendshape", "unused blendshapes");
                Add(HiddenTriangles, "body triangle under clothing", "body triangles under clothing");
                Add(MergedMeshes, "mesh merged", "meshes merged");
                Add(StaticBones, "bone that never moves (folded into its parent)", "bones that never move (folded into their parents)");
                Add(BoneReferences, "unweighted bone reference", "unweighted bone references");
                Add(EndBones, "PhysBone end bone (now an endpoint)", "PhysBone end bones (now endpoints)");
                Add(MaterialSlots, "material slot", "material slots");
                Add(Textures, "unused texture reference", "unused texture references");
                Add(DuplicateTextures, "duplicate texture", "duplicate textures");
                if (parts.Count > 0) changes.Insert(0, string.Join(", ", parts) + " removed");
                return changes.Count == 0 ? "nothing to change" : string.Join("; ", changes);
            }
        }

        // What the structure step found for the material step: My Avatar's component is editor-only and may be gone by then.
        private static readonly ConditionalWeakTable<GameObject, Plan> Plans = new ConditionalWeakTable<GameObject, Plan>();

        private sealed class Plan
        {
            public OptimizationPasses Passes;
            public OptimizationLevel Level;
            public Report Report;
            /// <summary>The textures' copies, planned while My Avatar's component is still there.</summary>
            public TextureOptimization.Plan Textures;
        }

        internal sealed class Structure : IVRCSDKPreprocessAvatarCallback
        {
            public int callbackOrder => -1100;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                Plans.Remove(avatar);
                var settings = avatar.GetComponentInChildren<MyAvatar>(true);
                if (settings == null || EditorApplication.isPlayingOrWillChangePlaymode) return true;
                var passes = settings.uploadOptimization.Passes;
                if (passes == OptimizationPasses.None) return true;
                var plan = new Plan { Passes = passes, Level = settings.uploadOptimization.level, Report = new Report() };
                Plans.Add(avatar, plan);
                // Measured on the meshes as they come (before merges and simplification), as the estimate does. Without a
                // texture pass, every texture uploads as it is.
                try { if ((passes & TextureOptimization.TexturePasses) != 0) plan.Textures = TextureOptimization.Build(settings); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    Debug.LogWarning("[My Avatar] Optimization: the upload keeps its textures as they are (" + ex.Message + ").");
                }
                try { RunStructure(avatar, passes, plan.Report, polygonQuality: settings.uploadOptimization.PolygonQuality); }
                catch (PassFailed ex)
                {
                    // A half-changed avatar never uploads: the build stops, saying which change failed.
                    Plans.Remove(avatar);
                    Debug.LogException(ex.InnerException ?? ex);
                    Debug.LogError("[My Avatar] Optimization: " + ex.Message + " The upload stopped instead. Turn that change off under Custom, " +
                                   "or optimization off, to upload.");
                    return false;
                }
                return true;
            }
        }

        /// <summary>A change that failed part-way: the copy may be half changed, so nothing after it runs and it doesn't upload.</summary>
        internal sealed class PassFailed : Exception
        {
            public readonly OptimizationPasses Pass;

            public PassFailed(OptimizationPasses pass, Exception cause)
                : base(pass + " failed (" + cause.GetBaseException().Message + "): the avatar would upload half changed.", cause) => Pass = pass;
        }

        internal sealed class Materials : IVRCSDKPreprocessAvatarCallback
        {
            // After Poiyomi's locking (100), before Toolkit keeps the build's in-memory assets (10000).
            public int callbackOrder => 9000;

            public bool OnPreprocessAvatar(GameObject avatar)
            {
                if (!Plans.TryGetValue(avatar, out var plan)) return true;
                Plans.Remove(avatar);
                RunMaterials(avatar, plan.Passes, plan.Report, plan.Textures);
                Debug.Log("[My Avatar] Optimization (" + plan.Level + "): " + plan.Report + ".");
                return true;
            }
        }

        /// <summary>
        /// The changes to objects, components and meshes. They build on one another, and some rewire several objects and their
        /// animations at once (merges, folded bones): one that fails part-way can leave the copy half changed, so the steps
        /// after it don't run and <see cref="PassFailed"/> is thrown (the upload stops; the estimate shows why).
        /// </summary>
        /// <param name="unbuilt">The avatar as it is in the scene (the estimate): its VRCFury and Modular Avatar components
        /// have not made their animations yet, so what they point at counts as used. Nothing is written to the project: the
        /// animations stay as they are, and what the steps make besides meshes (menus, icons) goes in
        /// <paramref name="temporary"/> for the caller to destroy.</param>
        /// <param name="shapes">On an estimate, the blendshapes each mesh will upload with (the others are counted as removed).</param>
        /// <param name="owner">On an estimate, the avatar it is for (its instance ID): the meshes it needs stay simplified.</param>
        internal static void RunStructure(GameObject root, OptimizationPasses passes, Report report, bool unbuilt = false, float polygonQuality = 1f,
            List<Object> temporary = null, Func<SkinnedMeshRenderer, ICollection<string>> shapes = null, int owner = 0)
        {
            using (var steps = StructureSteps(root, passes, report, unbuilt, polygonQuality, temporary, shapes, owner).GetEnumerator())
                while (steps.MoveNext()) { }
        }

        /// <summary>
        /// <see cref="RunStructure"/> one step at a time: each step that ran is yielded once done, so an estimate can spread them
        /// over editor frames. Abandoned part-way (disposed), the meshes the steps made and no renderer shows are destroyed.
        /// </summary>
        internal static IEnumerable<OptimizationPasses> StructureSteps(GameObject root, OptimizationPasses passes, Report report, bool unbuilt = false,
            float polygonQuality = 1f, List<Object> temporary = null, Func<SkinnedMeshRenderer, ICollection<string>> shapes = null, int owner = 0)
        {
            var made = new HashSet<Mesh>();
            AvatarUsage usage = null;
            // The build's own copies of the animations, made the first time a step changes them.
            AttachmentAnimationBuild build = null;
            AttachmentAnimationBuild Animations() => unbuilt ? null : build ?? (build = AttachmentAnimationBuild.Prepare(root));
            PassFailed failed = null;
            try { usage = new AvatarUsage(root, unbuilt ? (Func<Component, bool>)(c => !(c is MyAvatar) && !(c is MyAvatarFaceTracking)) : null); }
            catch (Exception ex) { Debug.LogException(ex); Debug.LogWarning("[My Avatar] Optimization: the upload keeps the avatar as it is (" + ex.Message + ")."); yield break; }

            var steps = new List<(OptimizationPasses Pass, Action Run)>
            {
                (OptimizationPasses.HiddenObjects, () => report.HiddenObjects += ComponentCleanup.RemoveHiddenObjects(usage)),
                (OptimizationPasses.DisabledComponents, () => report.DisabledComponents += ComponentCleanup.RemoveDisabledComponents(usage)),
                (OptimizationPasses.IdlePhysBones, () => report.IdlePhysBones += ComponentCleanup.RemoveIdlePhysBones(usage)),
                (OptimizationPasses.UnusedColliders, () => report.Colliders += ComponentCleanup.RemoveUnusedColliders(usage)),
                (OptimizationPasses.StillPhysBones, () => report.StillPhysBones += ComponentCleanup.SettleStillPhysBones(usage)),
                (OptimizationPasses.BlendShapes, () =>
                {
                    foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        int before = renderer.sharedMesh != null ? renderer.sharedMesh.blendShapeCount : 0;
                        var copy = BlendShapePruning.Prune(root, renderer, (passes & OptimizationPasses.ConstantBlendShapes) != 0);
                        if (copy == null) continue;
                        made.Add(copy);
                        report.BlendShapes += before - copy.blendShapeCount;
                    }
                }),
                (OptimizationPasses.BodyUnderClothing, () =>
                {
                    var body = Toolkit.Editor.VRChat.Attachments.AttachmentPlanner.Body(root.transform);
                    var source = body != null ? MeshOptimization.MeshOf(body) : null;
                    var hidden = BodyCover.HideCovered(usage, body);
                    if (hidden == null) return;
                    made.Add(hidden.Mesh);
                    report.HiddenTriangles += hidden.Triangles;
                    // The body with fewer triangles: the simplifier knows it by the body's mesh and the triangles left, without
                    // reading every blendshape of a new body at each estimate.
                    if (source != null) PolygonReduction.FewerTriangles(hidden.Mesh, source);
                }),
                // An estimate doesn't wait for the simplifier: it measures again once the meshes are ready. A mesh the simplifier
                // fails on keeps its polygons (said in the console), whole.
                (OptimizationPasses.FewerPolygons, () => report.Polygons += PolygonReduction.Reduce(usage, polygonQuality, made, shapes, background: unbuilt, owner: owner)),
                (OptimizationPasses.MergeMeshes, () =>
                {
                    var merged = MeshMerge.MergeStill(usage);
                    made.UnionWith(merged.Meshes);
                    report.MergedMeshes += merged.Merged;
                }),
                (OptimizationPasses.MergeToggledMeshes, () =>
                {
                    var merged = MeshMerge.MergeTogether(usage, Animations());
                    made.UnionWith(merged.Meshes);
                    report.ToggledMeshes += merged.Merged;
                }),
                (OptimizationPasses.StaticBones, () =>
                {
                    var folded = StaticBones.Fold(usage);
                    made.UnionWith(folded.Meshes);
                    report.StaticBones += folded.Bones;
                }),
                (OptimizationPasses.BoneReferences, () =>
                {
                    foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        int before = renderer.bones.Length;
                        var copy = MeshOptimization.PruneBones(renderer);
                        if (copy == null) continue;
                        made.Add(copy);
                        report.BoneReferences += before - renderer.bones.Length;
                    }
                }),
                (OptimizationPasses.EndBones, () => report.EndBones += ComponentCleanup.TrimEndBones(usage)),
                (OptimizationPasses.MaterialSlots, () =>
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {
                        int before = renderer.sharedMaterials.Length;
                        var copy = MeshOptimization.MergeSlots(renderer, usage);
                        if (copy == null) continue;
                        made.Add(copy);
                        report.MaterialSlots += before - renderer.sharedMaterials.Length;
                    }
                }),
                (OptimizationPasses.MenuIcons, () =>
                {
                    var icons = MenuIcons.Shrink(root);
                    report.Icons += icons.Icons;
                    if (unbuilt) temporary?.AddRange(icons.Made);
                    else AttachmentAnimationBuild.Keep(root, icons.Made);
                }),
                // Measured on the meshes as the steps before left them.
                (OptimizationPasses.Bounds, () => report.Bounds += AvatarBounds.Unify(root)),
            };
            // Last: every step before finds objects and parameters by the names they had.
            if (!unbuilt)
                steps.Add((OptimizationPasses.ShortNames, () =>
                {
                    var renamed = ShortNames.Rename(usage, Animations());
                    report.ShortObjects += renamed.Objects;
                    report.ShortParameters += renamed.Parameters;
                }));

            try
            {
                foreach (var (pass, run) in steps)
                {
                    if ((passes & pass) == 0) continue;
                    try { run(); }
                    catch (Exception ex) { failed = new PassFailed(pass, ex); break; }
                    yield return pass;
                }
            }
            finally
            {
                // Copies a later step copied again are no one's any more.
                if (root)
                {
                    var used = new HashSet<Mesh>(root.GetComponentsInChildren<Renderer>(true).Select(MeshOptimization.MeshOf).Where(m => m != null));
                    foreach (var mesh in made) if (mesh != null && !used.Contains(mesh)) Object.DestroyImmediate(mesh);
                }
            }
            if (failed != null) throw failed;
        }

        /// <summary>
        /// The material step: copies of materials pointing at one of each duplicate texture, at the upload's texture copies,
        /// and without the textures their shader no longer has. Each swaps whole materials on a renderer (the texture copies
        /// all or none), so one that fails leaves materials that render as before: it is reported and the upload goes on.
        /// </summary>
        internal static void RunMaterials(GameObject root, OptimizationPasses passes, Report report, TextureOptimization.Plan textures = null)
        {
            if ((passes & OptimizationPasses.DuplicateTextures) != 0)
                try { report.DuplicateTextures += MaterialCleanup.MergeDuplicateTextures(root); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    Debug.LogWarning("[My Avatar] Optimization: the upload keeps its duplicate textures (" + ex.Message + ").");
                }
            if (textures != null)
                try { report.ResizedTextures += UploadTextures.Apply(root, textures); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    Debug.LogWarning("[My Avatar] Optimization: the upload keeps its textures as they are (" + ex.Message + ").");
                }
            if ((passes & OptimizationPasses.UnusedTextures) == 0) return;
            try { report.Textures += MaterialCleanup.DropUnusedTextures(root); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogWarning("[My Avatar] Optimization: the upload keeps its materials as they are (" + ex.Message + ").");
            }
        }
    }
}
