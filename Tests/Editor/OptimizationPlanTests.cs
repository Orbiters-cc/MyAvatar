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
