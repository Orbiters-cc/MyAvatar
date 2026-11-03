using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using Orbiters.Toolkit.VRChat;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The Optimization card's rank plan and download breakdown: what the estimate keeps for them, the steps toward a rank
    // and their order, the pieces of the download, and the real size of the last build kept apart from the estimate.
    public sealed class OptimizationPlanTests
    {
        private readonly List<Object> owned = new List<Object>();
        private Scene scene;
        private GameObject root;
        private MyAvatar settings;
        private TestCaches caches;
        private IDisposable polygons, estimates;

        [SetUp] public void SetUp()
        {
            caches = new TestCaches();
            polygons = PolygonReduction.Isolated();
            estimates = OptimizationEstimate.Isolated();
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Plan avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<VRCAvatarDescriptor>();
            settings = root.AddComponent<MyAvatar>();
        }

        [TearDown] public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            estimates.Dispose();
            polygons.Dispose();
            caches.Dispose();
        }

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        // A skinned mesh of that many triangles on one bone, with one blendshape.
        private SkinnedMeshRenderer Skinned(Transform parent, string name, int triangles)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var mesh = Own(new Mesh { name = name });
            var vertices = new List<Vector3>(); var indices = new List<int>();
            for (int i = 0; i < triangles; i++)
            {
                vertices.Add(new Vector3(i * .01f, 0, 0)); vertices.Add(new Vector3(i * .01f, .01f, 0)); vertices.Add(new Vector3(i * .01f + .01f, 0, 0));
                indices.Add(3 * i); indices.Add(3 * i + 1); indices.Add(3 * i + 2);
            }
            mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0);
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1f }, vertices.Count).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.AddBlendShapeFrame("Smile", 100f, vertices.Select(v => Vector3.up * .001f).ToArray(), null, null);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { root.transform };
            renderer.sharedMaterials = new[] { Own(new Material(Shader.Find("Standard"))) };
            return renderer;
        }

        private Transform Item(string name)
        {
            var item = new GameObject(name).transform; item.SetParent(root.transform, false);
            item.gameObject.AddComponent<OrbitersAttachment>();
            return item;
        }

        private OptimizationEstimate.Result Estimate()
        {
            OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            OptimizationEstimate.Finish();
            var result = OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            Assert.That(result?.Error, Is.Null);
            return result;
        }

        // ---- What weighs most ----

        [Test] public void ThePiecesOfTheDownloadAddUpToIt()
        {
            Skinned(root.transform, "Body", 300);
            Skinned(Item("Jacket"), "Sleeves", 120);
            var parts = DownloadSize.Of(root);
            Assert.That(parts.Pieces.Sum(p => p.Bytes), Is.EqualTo(parts.Bundle), "the rest is one last piece");
            Assert.That(Math.Abs(DownloadSize.AsShown(parts.Pieces.Sum(p => p.Bytes)) - parts.Total), Is.LessThanOrEqualTo(1), "as VRChat shows it, within rounding");
            var sleeves = parts.Pieces.First(p => p.Kind == DownloadSize.PieceKind.Mesh && p.Name == "Sleeves");
            Assert.That(sleeves.Item, Is.EqualTo("Jacket"));
            Assert.That(sleeves.Owner, Is.EqualTo("Jacket/Sleeves"));
            Assert.That(parts.Pieces.Any(p => p.Kind == DownloadSize.PieceKind.BlendShapes && p.Name.StartsWith("Body", StringComparison.Ordinal)));
        }

        [Test] public void OnlyTheBiggestPiecesAreKept()
        {
            var pieces = Enumerable.Range(1, DownloadSize.KeptPieces + 10).Select(i => new DownloadSize.Piece { Name = "P" + i, Bytes = i * 100, Owner = "" }).ToList();
            long bundle = pieces.Sum(p => p.Bytes) + 7;
            var kept = DownloadSize.Keep(pieces, bundle);
            Assert.That(kept.Count, Is.EqualTo(DownloadSize.KeptPieces + 1));
            Assert.That(kept[0].Bytes, Is.EqualTo((DownloadSize.KeptPieces + 10) * 100));
            Assert.That(kept.Last().Name, Is.EqualTo("10 smaller pieces"));
            Assert.That(kept.Sum(p => p.Bytes), Is.EqualTo(bundle));
        }

        // ---- What the estimate keeps for a plan ----

        [Test] public void TheEstimateKeepsEachCategoryAndWhatEachItemAdds()
        {
            Skinned(root.transform, "Body", 300);
            Skinned(Item("Jacket"), "Sleeves", 120);
            var result = Estimate();
            foreach (var snapshot in new[] { result.Now, result.Upload })
            {
                Assert.That(snapshot.PcRank, Is.Not.Null);
                Assert.That(snapshot.MobileRank, Is.Not.Null);
                Assert.That(snapshot.PcRank.Value(AvatarPerformanceCategory.PolyCount), Is.EqualTo(snapshot.Polys));
                Assert.That(snapshot.PcRank.Value(AvatarPerformanceCategory.SkinnedMeshCount), Is.EqualTo(snapshot.Skinned));
                Assert.That(snapshot.PcParts.Pieces.Sum(p => p.Bytes), Is.EqualTo(snapshot.Bundle));
            }
            var jacket = result.Now.PcRank.Items.Single(i => i.Name == "Jacket");
            Assert.That(jacket.Amount(AvatarPerformanceCategory.PolyCount), Is.EqualTo(120f));
            Assert.That(jacket.Amount(AvatarPerformanceCategory.SkinnedMeshCount), Is.EqualTo(1f));
            var costliest = result.Now.PcRank.Costliest.Where(c => c.Category == AvatarPerformanceCategory.PolyCount).ToList();
            Assert.That(costliest.Select(c => c.Name), Is.EqualTo(new[] { "Body", "Sleeves" }));
        }

        [Test] public void ALevelTriedBeforeIsKnownWithoutMeasuringAgain()
        {
            Skinned(root.transform, "Body", 300);
            Skinned(root.transform, "Old shirt", 50).gameObject.SetActive(false);
            settings.uploadOptimization.level = OptimizationLevel.Small;
            var small = Estimate();
            settings.uploadOptimization.level = OptimizationLevel.Off;
            Estimate();
            var asked = new UploadOptimization { level = OptimizationLevel.Small };
            Assert.That(OptimizationEstimate.Known(settings, asked), Is.SameAs(small));
            Assert.That(OptimizationEstimate.Known(settings, new UploadOptimization { level = OptimizationLevel.Big }), Is.Null, "never measured");
            root.transform.Find("Body").gameObject.SetActive(false);
            Assert.That(OptimizationEstimate.Known(settings, asked), Is.Null, "the avatar changed since");
        }

        // ---- Steps ----

        private static RankStats Stats(bool mobile, params (AvatarPerformanceCategory Category, float Value)[] values)
        {
            var stats = new RankStats { Mobile = mobile };
            foreach (var (category, value) in values) stats.Values[category] = value;
            foreach (var category in RankPlan.Categories()) stats.Ratings[category] = PerformanceRating.Excellent;
            return stats;
        }

        private static float Limit(AvatarPerformanceCategory category, PerformanceRating rating, bool mobile)
        {
            Func<AvatarPerformanceStatsLevel, int> stat;
            if (category == AvatarPerformanceCategory.PolyCount) stat = l => l.polyCount;
            else if (category == AvatarPerformanceCategory.SkinnedMeshCount) stat = l => l.skinnedMeshCount;
            else stat = l => l.boneCount;
            return AvatarBudget.Limit(stat, rating, mobile);
        }

        [Test] public void AnItemThatClosesTheGapComesFirstAndAloneReachesTheRank()
        {
            if (!AvatarBudget.Ready) Assert.Ignore("The VRChat SDK has not loaded its performance levels yet.");
            float good = Limit(AvatarPerformanceCategory.PolyCount, PerformanceRating.Good, false);
            var upload = new OptimizationEstimate.Snapshot
            {
                PcRank = Stats(false, (AvatarPerformanceCategory.PolyCount, good + 5000)),
                MobileRank = Stats(true, (AvatarPerformanceCategory.PolyCount, good + 5000)),
            };
            upload.PcRank.Items.Add(new RankItem { Name = "Hat", Path = "Hat", Amounts = { [AvatarPerformanceCategory.PolyCount] = 1000 } });
            upload.PcRank.Items.Add(new RankItem { Name = "Coat", Path = "Coat", Amounts = { [AvatarPerformanceCategory.PolyCount] = 6000 } });
            var plans = new List<RankPlan> { RankPlan.For(null, upload.PcRank, PerformanceRating.Good, false) };
            var current = new UploadOptimization { level = OptimizationLevel.Big };

            var steps = RankSteps.For(current, upload, plans, _ => null);
            Assert.That(steps[0].Title, Is.EqualTo("Remove or hide “Coat”"));
            Assert.That(steps[0].ReachesAlone, Is.True);
            Assert.That(steps[0].Closes, Is.EqualTo(1f));
            Assert.That(steps[1].Title, Is.EqualTo("Remove or hide “Hat”"));
            Assert.That(steps[1].Closes, Is.EqualTo(.2f).Within(1e-4f));
            Assert.That(RankSteps.Needed(steps, plans, out _), Is.EqualTo(1));
        }

        [Test] public void ALevelNotMeasuredYetSaysWhatItLowersAndOneMeasuredSaysWhatItSaves()
        {
            if (!AvatarBudget.Ready) Assert.Ignore("The VRChat SDK has not loaded its performance levels yet.");
            float good = Limit(AvatarPerformanceCategory.SkinnedMeshCount, PerformanceRating.Good, false);
            var upload = new OptimizationEstimate.Snapshot
            {
                PcRank = Stats(false, (AvatarPerformanceCategory.SkinnedMeshCount, good + 3)), MobileRank = Stats(true),
            };
            var plans = new List<RankPlan> { RankPlan.For(null, upload.PcRank, PerformanceRating.Good, false) };
            var off = new UploadOptimization { level = OptimizationLevel.Off };

            var untried = RankSteps.For(off, upload, plans, _ => null);
            var big = untried.Single(s => s.Title == "Big optimization");
            Assert.That(big.Known, Is.False);
            Assert.That(big.Touches, Does.Contain(AvatarPerformanceCategory.SkinnedMeshCount), "Big merges meshes");
            Assert.That(RankSteps.Needed(untried, plans, out string shortOf), Is.EqualTo(-1));
            StringAssert.Contains("skinned", shortOf);

            var measured = new OptimizationEstimate.Snapshot { PcRank = Stats(false, (AvatarPerformanceCategory.SkinnedMeshCount, good - 1)), MobileRank = Stats(true) };
            var tried = RankSteps.For(off, upload, plans, s => s.level == OptimizationLevel.Big ? measured : null);
            Assert.That(tried[0].Title, Is.EqualTo("Big optimization"));
            Assert.That(tried[0].ReachesAlone, Is.True);
            Assert.That(tried[0].Pc[AvatarPerformanceCategory.SkinnedMeshCount], Is.EqualTo(4f));
            Assert.That(RankSteps.Needed(tried, plans, out _), Is.EqualTo(1));
            Assert.That(tried[0].Settings.level, Is.EqualTo(OptimizationLevel.Big));
        }

        [Test] public void QuestsLowerLimitsAskForMoreThanPc()
        {
            if (!AvatarBudget.Ready) Assert.Ignore("The VRChat SDK has not loaded its performance levels yet.");
            float pcGood = Limit(AvatarPerformanceCategory.PolyCount, PerformanceRating.Good, false);
            var upload = new OptimizationEstimate.Snapshot
            {
                PcRank = Stats(false, (AvatarPerformanceCategory.PolyCount, pcGood)), MobileRank = Stats(true, (AvatarPerformanceCategory.PolyCount, pcGood)),
                DesktopShaderMaterials = 2, DesktopShaders = new List<string> { "Poiyomi Toon" },
            };
            upload.PcRank.Items.Add(new RankItem { Name = "Coat", Path = "Coat", Amounts = { [AvatarPerformanceCategory.PolyCount] = 100 } });
            upload.MobileRank.Items.Add(new RankItem { Name = "Coat", Path = "Coat", Amounts = { [AvatarPerformanceCategory.PolyCount] = 100 } });
            var pc = RankPlan.For(null, upload.PcRank, PerformanceRating.Good, false);
            var quest = RankPlan.For(null, upload.MobileRank, PerformanceRating.Good, true);
            Assert.That(pc.Reached, Is.True);
            Assert.That(quest.Reached, Is.False);
            var steps = RankSteps.For(new UploadOptimization { level = OptimizationLevel.Big }, upload, new List<RankPlan> { pc, quest }, _ => null);
            Assert.That(steps.Any(s => s.Kind == RankStep.Kinds.Note && s.Title.StartsWith("Quest: switch 2 materials", StringComparison.Ordinal)));
            Assert.That(steps.Single(s => s.Kind == RankStep.Kinds.Item).ReachesAlone, Is.False, "100 polygons is far from Quest's Good");
            Assert.That(RankSteps.Needed(steps, new List<RankPlan> { pc, quest }, out string shortOf), Is.EqualTo(-1));
            StringAssert.Contains("on Quest", shortOf);
        }

        // ---- Estimates of settings not measured yet ----

        private static RankFacts Facts(params RendererFact[] renderers) => new RankFacts { Renderers = renderers.ToList() };

        // Hidden and never toggled: what Hidden objects takes out (Read asks Toolkit's own rule; here it is given).
        private static RendererFact Mesh(string path, int triangles, bool skinned = true, bool shown = true, bool toggled = false, int shapes = 0) =>
            new RendererFact { Path = path, Skinned = skinned, Shown = shown, Toggled = toggled, Triangles = triangles, BlendShapes = shapes, Materials = 1, HiddenGone = !shown && !toggled };

        private static OptimizationEstimate.Snapshot Upload(int skinned, int meshes, int polygons, float textures = 0f)
        {
            var values = new[] { (AvatarPerformanceCategory.SkinnedMeshCount, (float)skinned), (AvatarPerformanceCategory.MeshCount, (float)meshes),
                (AvatarPerformanceCategory.PolyCount, (float)polygons), (AvatarPerformanceCategory.TextureMegabytes, textures) };
            return new OptimizationEstimate.Snapshot { PcRank = Stats(false, values), MobileRank = Stats(true, values) };
        }

        // The estimate sizes each texture by the upload's own rule (TextureOptimization.Copy), from what Read keeps of it.
        [Test] public void TexturesAreEstimatedByTheUploadsOwnRule()
        {
            long Bytes(TextureFormat format, int size) => TextureOptimization.FormatBytes(format, size, size, true);
            var pc = new TextureOptimization.TextureState { Changeable = true, Width = 2048, Height = 2048, Format = TextureFormat.DXT5, Before = Bytes(TextureFormat.DXT5, 2048) };
            var mobile = new TextureOptimization.TextureState { Changeable = true, Width = 2048, Height = 2048, Format = TextureFormat.ASTC_6x6, Before = Bytes(TextureFormat.ASTC_6x6, 2048) };
            var shirt = new TextureFact { Name = "Shirt", Pc = pc, Mobile = mobile };
            var off = new UploadOptimization();
            var small = new UploadOptimization { level = OptimizationLevel.Small };
            var big = new UploadOptimization { level = OptimizationLevel.Big };
            Assert.That(RankEstimates.Bytes(shirt, false, off), Is.EqualTo(pc.Before), "Off: as it is");
            // Density not measured: 512 px at 1 m for all but the body, in the format with the best quality per byte (no alpha: DXT1).
            Assert.That(RankEstimates.Bytes(shirt, false, small), Is.EqualTo(Bytes(TextureFormat.DXT1, 512)));
            pc.Body = true;
            Assert.That(RankEstimates.Bytes(shirt, false, small), Is.EqualTo(Bytes(TextureFormat.DXT1, 2048)), "the body keeps 2048 px at 1 m");
            // Measured: a texel per screen pixel from the distance (1,000 px per meter at 1 m, a tenth of slack).
            pc.Body = false; pc.Meters = 1f;
            Assert.That(RankEstimates.Bytes(shirt, false, small), Is.EqualTo(Bytes(TextureFormat.DXT1, 1024)), "909 texels needed: 1024 px");
            // Mobile keeps its own format and size; Big's Quest textures halve them again (sharp from 3 m: 512 px, then 256).
            mobile.Meters = 1f;
            Assert.That(RankEstimates.Bytes(shirt, true, small), Is.EqualTo(Bytes(TextureFormat.ASTC_6x6, 1024)));
            Assert.That(RankEstimates.Bytes(shirt, true, big), Is.EqualTo(Bytes(TextureFormat.ASTC_6x6, 256)));
            // A ramp or glyph atlas uploads as it is.
            var ramp = new TextureFact { Name = "Ramp", Pc = new TextureOptimization.TextureState { Width = 256, Height = 4, Before = 4096 } };
            Assert.That(RankEstimates.Bytes(ramp, false, big), Is.EqualTo(4096));
            // What changes is saved, and a texture only a hidden object shows leaves with it; nothing else counts (0.9.23 took
            // the rank's whole texture memory minus these, so every texture the facts don't hold counted as saved).
            var old = new TextureFact { Name = "Old shirt", Pc = new TextureOptimization.TextureState { Width = 512, Height = 512, Before = 1 << 20 }, HiddenGone = true };
            var facts = new RankFacts { Textures = { shirt, old } };
            var saves = RankEstimates.Saves(facts, Upload(0, 0, 0, 40f), 0, off, small, false);
            Assert.That(saves[AvatarPerformanceCategory.TextureMegabytes], Is.EqualTo((pc.Before - Bytes(TextureFormat.DXT1, 1024) + (1 << 20)) / 1048576f).Within(1e-4f));
        }

        [Test] public void PolygonsAreEstimatedFromTrianglesAndScaledToWhatWasMeasured()
        {
            var facts = Facts(Mesh("Body", 10000, shapes: 40), Mesh("Coat", 6000), Mesh("Ring", 500));
            var upload = Upload(3, 0, 16500);
            var small = new UploadOptimization { level = OptimizationLevel.Small };
            var saves = RankEstimates.Saves(facts, upload, 0, new UploadOptimization(), small, false);
            float share = RankEstimates.Share(UploadOptimization.SmallPolygonQuality);
            Assert.That(share, Is.EqualTo(.004f).Within(1e-5f), "4 mm");
            Assert.That(RankEstimates.Share(UploadOptimization.BigPolygonQuality), Is.EqualTo(.19f).Within(1e-4f), "8 mm");
            float body = Mathf.Exp(-1f);
            Assert.That(saves[AvatarPerformanceCategory.PolyCount], Is.EqualTo((10000 * body + 6000) * share).Within(1f),
                "40 blendshapes keep all but e^-1 of the body's share; a mesh under 2,000 triangles is never simplified");
            // Small measured 300 polygons fewer where the rule said 39: Big's estimate is scaled the same way.
            float predictedSmall = (10000 * body + 6000) * share, predictedBig = (10000 * body + 6000) * RankEstimates.Share(UploadOptimization.BigPolygonQuality);
            var big = RankEstimates.Saves(facts, upload, 300, small, new UploadOptimization { level = OptimizationLevel.Big }, false);
            Assert.That(big[AvatarPerformanceCategory.PolyCount], Is.EqualTo(predictedBig * 300f / predictedSmall - 300f).Within(1f));
            var none = RankEstimates.Saves(facts, upload, 0, small, new UploadOptimization { level = OptimizationLevel.Big }, false);
            Assert.That(none.ContainsKey(AvatarPerformanceCategory.PolyCount), Is.False, "nothing simplified at 4 mm: no more expected at 8 mm");
            // Under 2 mm next to nothing goes: ½ mm removes none.
            Assert.That(RankEstimates.Share(1f), Is.EqualTo(0f));
        }

        [Test] public void MergesAreEstimatedByMeshMergesRules()
        {
            if (!AvatarBudget.Ready) Assert.Ignore("The VRChat SDK has not loaded its performance levels yet.");
            var off = new UploadOptimization();
            var big = new UploadOptimization { level = OptimizationLevel.Big };
            // Body, hair and a prop always shown join one skinned mesh; a hat one toggle shows joins it when that rates better.
            var facts = Facts(Mesh("Body", 100), Mesh("Hair", 100), Mesh("Prop", 10, skinned: false), Mesh("Hat", 10, toggled: true));
            var saves = RankEstimates.Saves(facts, Upload(3, 1, 220), 0, off, big, false);
            Assert.That(saves[AvatarPerformanceCategory.SkinnedMeshCount], Is.EqualTo(2f), "3 → 1");
            Assert.That(saves[AvatarPerformanceCategory.MeshCount], Is.EqualTo(1f));
            // A toggled mesh whose animations change its materials, or that a constraint moves, stays apart.
            var animated = Mesh("Hat", 10, toggled: true); animated.Animated = true;
            var kept = RankEstimates.Saves(Facts(Mesh("Body", 100), Mesh("Hair", 100), animated), Upload(3, 0, 210), 0, off, big, false);
            Assert.That(kept[AvatarPerformanceCategory.SkinnedMeshCount], Is.EqualTo(1f));
            // What the descriptor or a build tool points at, or whose shapes animate, stays: the others join it.
            var body = Mesh("Body", 100); body.Referenced = true;
            var joined = RankEstimates.Saves(Facts(body, Mesh("Hair", 100)), Upload(2, 0, 200), 0, off, big, false);
            Assert.That(joined[AvatarPerformanceCategory.SkinnedMeshCount], Is.EqualTo(1f), "the hair joins the body");
            var tail = Mesh("Tail", 100); tail.Referenced = true;
            var apart = RankEstimates.Saves(Facts(body, tail), Upload(2, 0, 200), 0, off, big, false);
            Assert.That(apart.ContainsKey(AvatarPerformanceCategory.SkinnedMeshCount), Is.False, "two that stay where they are");
            // Hidden at rest and never shown: gone at upload with Small already, unless a build tool may show it.
            var toggledByTool = Mesh("Mane", 80, shown: false); toggledByTool.Referenced = true; toggledByTool.HiddenGone = false;
            var keptByTool = RankEstimates.Saves(Facts(Mesh("Body", 100), toggledByTool), Upload(2, 0, 180), 0, off,
                new UploadOptimization { level = OptimizationLevel.Small }, false);
            Assert.That(keptByTool.ContainsKey(AvatarPerformanceCategory.PolyCount), Is.False);
            // Hidden at rest and never shown: gone at upload with Small already.
            var hidden = RankEstimates.Saves(Facts(Mesh("Body", 100), Mesh("Old shirt", 50, shown: false)), Upload(2, 0, 150), 0, off,
                new UploadOptimization { level = OptimizationLevel.Small }, false);
            Assert.That(hidden[AvatarPerformanceCategory.SkinnedMeshCount], Is.EqualTo(1f));
            Assert.That(hidden[AvatarPerformanceCategory.PolyCount], Is.EqualTo(50f));
        }

        [Test] public void AStepIsEstimatedUntilItsSettingsAreMeasured()
        {
            if (!AvatarBudget.Ready) Assert.Ignore("The VRChat SDK has not loaded its performance levels yet.");
            float good = Limit(AvatarPerformanceCategory.SkinnedMeshCount, PerformanceRating.Good, false);
            int count = (int)good + 3;
            var facts = Facts(Enumerable.Range(0, count).Select(i => Mesh("Part" + i, 100)).ToArray());
            var upload = Upload(count, 0, count * 100);
            var plans = new List<RankPlan> { RankPlan.For(null, upload.PcRank, PerformanceRating.Good, false) };
            var off = new UploadOptimization();

            var estimated = RankSteps.For(off, upload, plans, _ => null, facts).Single(s => s.Title == "Big optimization");
            Assert.That(estimated.Estimated, Is.True);
            Assert.That(estimated.Pc[AvatarPerformanceCategory.SkinnedMeshCount], Is.EqualTo(count - 1f));
            Assert.That(estimated.ReachesAlone, Is.True);

            var measured = Upload(2, 0, count * 100);
            var known = RankSteps.For(off, upload, plans, s => s.level == OptimizationLevel.Big ? measured : null, facts).Single(s => s.Title == "Big optimization");
            Assert.That(known.Estimated, Is.False, "measured: the estimate gives way");
            Assert.That(known.Pc[AvatarPerformanceCategory.SkinnedMeshCount], Is.EqualTo(count - 2f));
        }

        [Test] public void TheEstimateOfTheAvatarNowKeepsItsFacts()
        {
            Skinned(root.transform, "Body", 2400);
            Skinned(root.transform, "Old shirt", 50).gameObject.SetActive(false);
            var hat = Skinned(root.transform, "Hat", 30);
            var clip = Own(new AnimationClip { name = "Hat toggle" });
            clip.SetCurve("Hat", typeof(GameObject), "m_IsActive", AnimationCurve.Constant(0, 0, 0));
            var fx = Own(new UnityEditor.Animations.AnimatorController());
            fx.AddLayer("Hat");
            fx.layers[0].stateMachine.AddState("On").motion = clip;
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            descriptor.VisemeSkinnedMesh = root.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };

            var facts = Estimate().Now.Facts;
            Assert.That(facts, Is.Not.Null);
            Assert.That(facts.Renderers.Select(r => r.Path), Is.EquivalentTo(new[] { "Body", "Old shirt", "Hat" }));
            Assert.That(facts.Renderers.Single(r => r.Path == "Hat").Toggled, Is.True);
            Assert.That(facts.Renderers.Single(r => r.Path == "Old shirt").Shown, Is.False);
            Assert.That(facts.Renderers.Single(r => r.Path == "Body").Triangles, Is.EqualTo(2400));
            Assert.That(facts.Renderers.Single(r => r.Path == "Body").BlendShapes, Is.EqualTo(1));
            Assert.That(facts.Renderers.Single(r => r.Path == "Body").Referenced, Is.True, "the descriptor's visemes");
            Assert.That(facts.Renderers.Single(r => r.Path == "Old shirt").Referenced, Is.False);
            Assert.That(facts.Renderers.Single(r => r.Path == "Old shirt").HiddenGone, Is.True, "hidden for good: Hidden objects takes it out");
            Assert.That(facts.Renderers.Single(r => r.Path == "Hat").HiddenGone, Is.False, "an animation shows it");
            Assert.That(facts.Renderers.Single(r => r.Path == "Body").HiddenGone, Is.False);
            Assert.That(facts.Textures, Is.Empty, "Standard materials without textures");
            Assert.That(hat, Is.Not.Null);
        }

        // ---- The last build, measured ----

        [Test] public void ABuildIsKeptApartFromTheEstimateAndKnowsWhenItIsOutOfDate()
        {
            Skinned(root.transform, "Body", 300);
            Estimate();
            // What the shared listener hands on when the SDK's build succeeds (not raised for every listener: Unit Git's is the user's).
            MeasuredBuilds.Built(new VrcSdkBuilds.Build
            {
                Avatar = root, AvatarName = root.name, BlueprintId = "", BundlePath = "test.vrca", Bytes = 15_000_000, Platform = AvatarPlatform.Pc, When = DateTime.UtcNow,
            });
            var record = MeasuredBuilds.For(root, AvatarPlatform.Pc);
            Assert.That(record, Is.Not.Null);
            Assert.That(record.Bytes, Is.EqualTo(15_000_000));
            Assert.That(record.Estimate, Is.EqualTo(OptimizationEstimate.Known(settings, settings.uploadOptimization).Upload.Download), "the estimate shown for those settings");
            Assert.That(MeasuredBuilds.For(root, AvatarPlatform.Android), Is.Null, "kept per platform");
            Assert.That(MeasuredBuilds.Stale(record, OptimizationEstimate.Fingerprint(settings)), Is.False);
            settings.uploadOptimization.level = OptimizationLevel.Big;
            Assert.That(MeasuredBuilds.Stale(record, OptimizationEstimate.Fingerprint(settings)), Is.True, "the settings changed since");
            record.Session = "an earlier Unity session";
            Assert.That(MeasuredBuilds.Stale(record, OptimizationEstimate.Fingerprint(settings)), Is.Null, "can't tell");
        }

        [Test] public void ANewBuildReplacesTheLastOfItsAvatarAndPlatform()
        {
            MeasuredBuilds.Keep(new MeasuredBuilds.Record { Avatar = "avtr_a", Platform = "pc", Bytes = 1, WhenTicks = 1, Session = MeasuredBuilds.Session });
            MeasuredBuilds.Keep(new MeasuredBuilds.Record { Avatar = "avtr_a", Platform = "android", Bytes = 2, WhenTicks = 2, Session = MeasuredBuilds.Session });
            MeasuredBuilds.Keep(new MeasuredBuilds.Record { Avatar = "avtr_a", Platform = "pc", Bytes = 3, WhenTicks = 3, Session = MeasuredBuilds.Session });
            var all = LibraryStore.Read<List<MeasuredBuilds.Record>>(MeasuredBuilds.FileName);
            Assert.That(all.Count, Is.EqualTo(2));
            Assert.That(all.Single(r => r.Platform == "pc").Bytes, Is.EqualTo(3));
        }
    }
}
