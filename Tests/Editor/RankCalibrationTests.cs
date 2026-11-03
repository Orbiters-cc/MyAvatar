using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using UnityEngine;
using Category = VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// Reach a rank's estimates of settings never measured (<see cref="RankEstimates"/>) against what the estimates measured
    /// on the avatars of Enzo's projects (Fixtures/rank-calibration.json, written by <see cref="RankCalibrationMeasure"/>):
    /// each mesh's polygons at each budget, and Small and Big from Off on PC and mobile. The table goes to the test's output.
    /// </summary>
    public sealed class RankCalibrationTests
    {
        private const string Fixture = "Packages/orbiters.myavatar/Tests/Editor/Fixtures/rank-calibration.json";

        private static List<RankCalibrationMeasure.Avatar> avatars;

        private static List<RankCalibrationMeasure.Avatar> Avatars()
        {
            if (avatars != null) return avatars;
            string path = Path.GetFullPath(Fixture);
            Assume.That(File.Exists(path), "the calibration's measures are in " + Fixture);
            avatars = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RankCalibrationMeasure.Avatar>>(File.ReadAllText(path))
                .Where(a => a.Facts != null && a.Errors.Count == 0).ToList();
            return avatars;
        }

        private static string Key(float mm) => mm.ToString(CultureInfo.InvariantCulture);

        // Each mesh the simplifier works on, at each budget, summed per avatar. The simplifier goes by geometry the facts
        // don't hold (a mesh loses a part or nothing): what is held is that the estimate invents no polygons at any budget
        // (0.9.23 said 2,229 at ½ mm, where none go), that 8 mm's total is within a fifth (0.9.23: -59%), and that the
        // avatars' errors over every budget stay under three quarters of what was measured (0.9.23: 109%; 0.9.24: 69%).
        [Test] public void EachMeshsPolygonsAtEachBudget()
        {
            var table = new StringBuilder("mm\testimated\tmeasured\ttotal error\tper-avatar |error|\n");
            double allErrors = 0, allMeasured = 0;
            foreach (float mm in RankCalibrationMeasure.Millimetres)
            {
                float quality = RankCalibrationMeasure.Quality(mm);
                double estimated = 0, measured = 0, errors = 0;
                foreach (var avatar in Avatars())
                {
                    double avatarEstimated = 0, avatarMeasured = 0;
                    foreach (var renderer in avatar.Facts.Renderers)
                    {
                        if (!avatar.Simplified.TryGetValue(renderer.Path, out var left) || !left.TryGetValue(Key(mm), out int after)) continue;
                        avatarEstimated += RankEstimates.Removed(renderer, quality);
                        avatarMeasured += renderer.Triangles - after;
                    }
                    estimated += avatarEstimated; measured += avatarMeasured; errors += Math.Abs(avatarEstimated - avatarMeasured);
                }
                table.AppendLine($"{mm}\t{estimated:0}\t{measured:0}\t{(measured > 0 ? (estimated - measured) / measured : 0):P0}\t{errors:0}");
                allErrors += errors; allMeasured += measured;
                Assert.That(estimated, Is.LessThanOrEqualTo(measured + 500), $"no polygons invented at {mm} mm");
                if (mm >= 8f) Assert.That(Math.Abs(estimated - measured), Is.LessThanOrEqualTo(.2 * measured), "8 mm's total");
            }
            TestContext.Out.WriteLine(table);
            Assert.That(allErrors, Is.LessThanOrEqualTo(.75 * allMeasured), "the avatars' errors over every budget");
        }

        // Small and Big from Off, on PC and mobile, summed over the avatars (|estimate - measure| of each): material slots,
        // skinned meshes and meshes off by one at most in all; texture memory within a sixth of what was measured (0.9.23:
        // 76% at Small on PC, it took every texture the facts didn't hold as saved); polygons no worse than estimating
        // nothing (see EachMeshsPolygonsAtEachBudget). The table of every avatar goes to the output.
        [Test] public void LevelsFromOff()
        {
            if (!AvatarBudget.Ready) Assert.Ignore("The VRChat SDK has not loaded its performance levels yet.");
            var table = new StringBuilder("avatar\tlevel\tplatform\tcategory\testimated\tmeasured\n");
            var errors = new Dictionary<(string, string, Category), (double Error, double Measured)>();
            foreach (var avatar in Avatars())
                foreach (var level in avatar.Levels)
                    foreach (bool mobile in new[] { false, true })
                    {
                        string platform = mobile ? "mobile" : "pc";
                        var upload = new OptimizationEstimate.Snapshot { PcRank = Stats(avatar.Off["pc"], false), MobileRank = Stats(avatar.Off["mobile"], true) };
                        var off = new UploadOptimization { level = OptimizationLevel.Off };
                        var candidate = new UploadOptimization { level = (OptimizationLevel)Enum.Parse(typeof(OptimizationLevel), level.Key) };
                        var saves = RankEstimates.Saves(avatar.Facts, upload, 0, off, candidate, mobile) ?? new Dictionary<Category, float>();
                        foreach (var category in RankCalibrationMeasure.Categories)
                        {
                            float estimated = saves.TryGetValue(category, out float s) ? s : 0f;
                            float measured = avatar.Off[platform][category.ToString()] - level.Value[platform][category.ToString()];
                            table.AppendLine($"{avatar.Name}\t{level.Key}\t{platform}\t{category}\t{estimated:0.##}\t{measured:0.##}");
                            var key = (level.Key, platform, category);
                            var (error, total) = errors.TryGetValue(key, out var e) ? e : (0d, 0d);
                            errors[key] = (error + Math.Abs(estimated - measured), total + Math.Abs(measured));
                        }
                    }
            TestContext.Out.WriteLine(table);
            var failures = new List<string>();
            foreach (var pair in errors)
            {
                var (level, platform, category) = pair.Key;
                var (error, measured) = pair.Value;
                double allowed = category == Category.PolyCount ? measured : category == Category.TextureMegabytes ? measured / 6d : 1d;
                if (error > allowed + 1e-3) failures.Add($"{level} {platform} {category}: off by {error:0.#} in all, {allowed:0.#} allowed ({measured:0.#} measured)");
            }
            Assert.That(failures, Is.Empty);
        }

        private static RankStats Stats(Dictionary<string, float> values, bool mobile)
        {
            var stats = new RankStats { Mobile = mobile };
            foreach (var pair in values) stats.Values[(Category)Enum.Parse(typeof(Category), pair.Key)] = pair.Value;
            return stats;
        }
    }
}
