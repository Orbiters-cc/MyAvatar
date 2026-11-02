using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using VRC.SDKBase.Validation.Performance;
using Category = VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// One thing that brings the avatar closer to a rank: a setting of the Optimization card (applied in one click), an
    /// Orbiters item to remove or hide, a manual change (PhysBones), or a note (Quest shaders). What it saves comes from
    /// estimates measured already: the current one (items) and those of settings tried before (<see cref="OptimizationEstimate.Known"/>);
    /// a setting not measured yet says which categories it lowers, and is measured once it is on.
    /// </summary>
    internal sealed class RankStep
    {
        internal enum Kinds { Setting, Item, Manual, Note }

        public Kinds Kind;
        public string Title, Detail;
        /// <summary>What it saves in each category, on PC and on mobile; null where it isn't known (a setting not measured yet).</summary>
        public Dictionary<Category, float> Pc, Mobile;
        /// <summary>The categories it lowers (known or not).</summary>
        public HashSet<Category> Touches = new HashSet<Category>();
        /// <summary>For a setting: the settings it applies.</summary>
        public UploadOptimization Settings;
        /// <summary>For an item or a manual change: the object to select, from the avatar's root.</summary>
        public string Path;
        /// <summary>The share of the gap it closes, 0 to 1 (averaged over the platforms planned), and whether it reaches the target alone.</summary>
        public float Closes;
        public bool ReachesAlone;

        /// <summary>Its savings are known: measured, or estimated (<see cref="Estimated"/>).</summary>
        public bool Known => Pc != null || Mobile != null;
        /// <summary>The savings are estimated from the avatar as it is (<see cref="RankEstimates"/>), not measured yet.</summary>
        public bool Estimated;
        public Dictionary<Category, float> Saves(bool mobile) => mobile ? Mobile : Pc;
    }

    internal static class RankSteps
    {
        // What each pass of the Optimization card lowers among the categories VRChat ranks.
        private static readonly (OptimizationPasses Pass, Category[] Lowers)[] Lowers =
        {
            (OptimizationPasses.HiddenObjects, new[] { Category.PolyCount, Category.SkinnedMeshCount, Category.MeshCount, Category.MaterialCount, Category.BoneCount,
                Category.PhysBoneComponentCount, Category.PhysBoneTransformCount, Category.ContactCount, Category.ConstraintsCount, Category.LightCount,
                Category.ParticleSystemCount, Category.AudioSourceCount, Category.TextureMegabytes }),
            (OptimizationPasses.DisabledComponents, new[] { Category.SkinnedMeshCount, Category.MeshCount, Category.PhysBoneComponentCount, Category.PhysBoneTransformCount,
                Category.ContactCount, Category.ConstraintsCount, Category.LightCount, Category.ParticleSystemCount, Category.AudioSourceCount }),
            (OptimizationPasses.IdlePhysBones, new[] { Category.PhysBoneComponentCount, Category.PhysBoneTransformCount, Category.PhysBoneCollisionCheckCount }),
            (OptimizationPasses.UnusedColliders, new[] { Category.PhysBoneColliderCount, Category.PhysBoneCollisionCheckCount }),
            (OptimizationPasses.BoneReferences, new[] { Category.BoneCount }),
            (OptimizationPasses.MaterialSlots, new[] { Category.MaterialCount }),
            (OptimizationPasses.UnusedTextures, new[] { Category.TextureMegabytes }),
            (OptimizationPasses.DuplicateTextures, new[] { Category.TextureMegabytes }),
            (OptimizationPasses.MergeMeshes, new[] { Category.SkinnedMeshCount, Category.MeshCount }),
            (OptimizationPasses.MergeToggledMeshes, new[] { Category.SkinnedMeshCount, Category.MeshCount }),
            (OptimizationPasses.StaticBones, new[] { Category.BoneCount }),
            (OptimizationPasses.EndBones, new[] { Category.BoneCount, Category.PhysBoneTransformCount, Category.PhysBoneCollisionCheckCount }),
            (OptimizationPasses.BodyUnderClothing, new[] { Category.PolyCount }),
            (OptimizationPasses.FewerPolygons, new[] { Category.PolyCount }),
            (OptimizationPasses.ResizeTextures, new[] { Category.TextureMegabytes }),
            (OptimizationPasses.HalfSurfaceMaps, new[] { Category.TextureMegabytes }),
            (OptimizationPasses.QuestTextures, new[] { Category.TextureMegabytes }),
        };

        // The pass names the Custom switches show, for a step turning some on.
        private static readonly Dictionary<OptimizationPasses, string> Names = new Dictionary<OptimizationPasses, string>
        {
            { OptimizationPasses.HiddenObjects, "Hidden objects" }, { OptimizationPasses.DisabledComponents, "Disabled components" },
            { OptimizationPasses.IdlePhysBones, "PhysBones moving nothing" }, { OptimizationPasses.UnusedColliders, "Unused colliders" },
            { OptimizationPasses.BoneReferences, "Unweighted bones" }, { OptimizationPasses.MaterialSlots, "Material slots" },
            { OptimizationPasses.UnusedTextures, "Unused textures" }, { OptimizationPasses.DuplicateTextures, "Duplicate textures" },
            { OptimizationPasses.MergeMeshes, "Merge meshes" }, { OptimizationPasses.MergeToggledMeshes, "Meshes toggled together" },
            { OptimizationPasses.StaticBones, "Bones that never move" }, { OptimizationPasses.EndBones, "PhysBone endpoints" },
            { OptimizationPasses.BodyUnderClothing, "Body under clothing" }, { OptimizationPasses.FewerPolygons, "Fewer polygons" },
            { OptimizationPasses.ResizeTextures, "Textures sized to the distance" }, { OptimizationPasses.HalfSurfaceMaps, "Smaller metallic and roughness" },
            { OptimizationPasses.QuestTextures, "Quest textures" },
        };

        private static readonly HashSet<Category> PhysBoneCategories =
            new HashSet<Category> { Category.PhysBoneComponentCount, Category.PhysBoneTransformCount, Category.PhysBoneCollisionCheckCount };

        /// <summary>The categories the passes lower.</summary>
        internal static HashSet<Category> Lowered(OptimizationPasses passes, bool mobile)
        {
            var lowered = new HashSet<Category>();
            foreach (var (pass, categories) in Lowers)
                if ((passes & pass) != 0 && (pass != OptimizationPasses.QuestTextures || mobile)) lowered.UnionWith(categories);
            return lowered;
        }

        /// <summary>
        /// The steps toward each plan's target (one plan per platform shown), best first: those that reach the targets alone,
        /// then by the share of the gap they close, then the cheapest (a setting before an item, an item before a manual
        /// change). <paramref name="known"/> gives the finished estimate of other settings, or null (tests pass their own).
        /// </summary>
        /// <param name="facts">The avatar as it is (the estimate of now): settings never measured get estimated savings from it.</param>
        /// <param name="removedPolygons">The polygons the current estimate removed (it scales the polygon estimate).</param>
        internal static List<RankStep> For(UploadOptimization current, OptimizationEstimate.Snapshot upload, IReadOnlyList<RankPlan> plans,
            Func<UploadOptimization, OptimizationEstimate.Snapshot> known, RankFacts facts = null, int removedPolygons = 0)
        {
            var steps = new List<RankStep>();
            if (upload == null || plans.Count == 0 || plans.All(p => p.Reached)) return steps;
            var blocking = new HashSet<Category>(plans.SelectMany(p => p.Blocking).Select(g => g.Category));
            bool anyMobile = plans.Any(p => p.Mobile);

            // Settings: the next level, or under Custom the switches that lower what blocks.
            foreach (var candidate in Candidates(current, blocking, anyMobile))
            {
                var (settings, title, detail) = candidate;
                var step = new RankStep { Kind = RankStep.Kinds.Setting, Title = title, Detail = detail, Settings = settings };
                var added = settings.Passes & ~current.Passes;
                foreach (var plan in plans) step.Touches.UnionWith(Lowered(added == 0 ? settings.Passes : added, plan.Mobile).Where(blocking.Contains));
                var measured = known?.Invoke(settings);
                if (measured != null)
                {
                    step.Pc = Savings(upload.PcRank, measured.PcRank);
                    step.Mobile = Savings(upload.MobileRank, measured.MobileRank);
                    step.Touches.UnionWith(step.Pc.Keys.Concat(step.Mobile.Keys).Where(blocking.Contains));
                }
                else if (facts != null)
                {
                    // Never measured: estimated from the avatar as it is, until it is.
                    var pc = RankEstimates.Saves(facts, upload, removedPolygons, current, settings, false);
                    var mobile = RankEstimates.Saves(facts, upload, removedPolygons, current, settings, true);
                    if (pc != null && mobile != null)
                    {
                        step.Pc = pc; step.Mobile = mobile; step.Estimated = true;
                        step.Touches.UnionWith(pc.Keys.Concat(mobile.Keys).Where(blocking.Contains));
                    }
                }
                if (step.Touches.Count > 0) steps.Add(step);
            }

            // Items: what removing or hiding each saves, on each platform.
            var names = new HashSet<string>(upload.PcRank.Items.Select(i => i.Name).Concat(upload.MobileRank.Items.Select(i => i.Name)));
            foreach (string name in names)
            {
                var pc = upload.PcRank.Items.FirstOrDefault(i => i.Name == name);
                var mobile = upload.MobileRank.Items.FirstOrDefault(i => i.Name == name);
                var step = new RankStep
                {
                    Kind = RankStep.Kinds.Item, Title = "Remove or hide “" + name + "”", Path = (pc ?? mobile).Path,
                    Pc = pc?.Amounts.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<Category, float>(),
                    Mobile = mobile?.Amounts.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<Category, float>(),
                };
                step.Touches.UnionWith(step.Pc.Keys.Concat(step.Mobile.Keys).Where(c => blocking.Contains(c) && (step.Pc.TryGetValue(c, out float a) && a > 0 || step.Mobile.TryGetValue(c, out float b) && b > 0)));
                if (step.Touches.Count == 0) continue;
                step.Detail = Describe(step, plans) + ": it's an Orbiters item, so it can come back any time.";
                steps.Add(step);
            }

            // PhysBones no setting can bring down: the chains to shorten by hand.
            var physBones = plans.SelectMany(p => p.Blocking).Where(g => PhysBoneCategories.Contains(g.Category)).Select(g => g.Category).Distinct().ToList();
            if (physBones.Count > 0)
            {
                var costliest = upload.PcRank.Costliest.Where(c => c.Category == Category.PhysBoneTransformCount).Take(3).ToList();
                if (costliest.Count > 0)
                {
                    var step = new RankStep
                    {
                        Kind = RankStep.Kinds.Manual, Path = costliest[0].Path,
                        Title = "Shorten the PhysBones on " + string.Join(", ", costliest.Select(c => "“" + c.Name + "”")),
                        Detail = "By hand: fewer bones in the chain, or one PhysBone for several. Together they move " +
                                 Count(costliest.Sum(c => c.Amount)) + " transforms.",
                    };
                    step.Touches.UnionWith(physBones);
                    steps.Add(step);
                }
            }

            // Quest shows VRChat's mobile shaders only: no rank change, but a Quest version needs it.
            if (anyMobile && upload.DesktopShaderMaterials > 0)
                steps.Add(new RankStep
                {
                    Kind = RankStep.Kinds.Note,
                    Title = "Quest: switch " + upload.DesktopShaderMaterials + (upload.DesktopShaderMaterials == 1 ? " material" : " materials") + " to a mobile shader",
                    Detail = "Quest shows only VRChat's mobile shaders (Toon Standard, Toon Lit, Standard Lite), and " + string.Join(", ", upload.DesktopShaders.Take(3)) +
                             (upload.DesktopShaders.Count > 3 ? "…" : "") + (upload.DesktopShaders.Count == 1 ? " isn't one." : " aren't."),
                });

            foreach (var step in steps) Score(step, plans);
            return steps.OrderByDescending(s => s.ReachesAlone).ThenByDescending(s => s.Known ? s.Closes : .5f * s.Touches.Count / Math.Max(1, blocking.Count))
                .ThenBy(s => s.Kind).ThenBy(s => s.Title, StringComparer.Ordinal).ToList();
        }

        // The share of each plan's gap a step closes (counted categories only), and whether it reaches every target alone.
        private static void Score(RankStep step, IReadOnlyList<RankPlan> plans)
        {
            if (!step.Known || step.Kind == RankStep.Kinds.Note) { step.Closes = 0f; step.ReachesAlone = false; return; }
            float total = 0f; int counted = 0; bool alone = true;
            foreach (var plan in plans)
            {
                var gaps = plan.Blocking.ToList();
                if (gaps.Count == 0) continue;
                var saves = step.Saves(plan.Mobile) ?? new Dictionary<Category, float>();
                float closed = 0f;
                foreach (var gap in gaps)
                {
                    float saved = saves.TryGetValue(gap.Category, out float s) ? s : 0f;
                    float share = gap.Counted && gap.Over > 0f ? Math.Min(1f, saved / gap.Over) : 0f;
                    closed += share;
                    if (share < 1f) alone = false;
                }
                total += closed / gaps.Count;
                counted++;
            }
            step.Closes = counted > 0 ? total / counted : 0f;
            step.ReachesAlone = counted > 0 && alone;
        }

        /// <summary>
        /// How many of the steps, taken in order, reach every target (their savings added up), or -1 when those known don't;
        /// <paramref name="short"/> then says what stays over.
        /// </summary>
        internal static int Needed(IReadOnlyList<RankStep> steps, IReadOnlyList<RankPlan> plans, out string @short)
        {
            @short = null;
            var saved = plans.ToDictionary(p => p, _ => new Dictionary<Category, float>());
            bool Reached() => plans.All(p => p.Blocking.All(g => g.Counted && saved[p].TryGetValue(g.Category, out float s) && s >= g.Over));
            if (Reached()) return 0;
            int count = 0;
            foreach (var step in steps.Where(s => s.Known && s.Kind != RankStep.Kinds.Note && s.Closes > 0f))
            {
                count++;
                foreach (var plan in plans)
                    foreach (var pair in step.Saves(plan.Mobile) ?? new Dictionary<Category, float>())
                        saved[plan][pair.Key] = (saved[plan].TryGetValue(pair.Key, out float s) ? s : 0f) + pair.Value;
                if (Reached()) return count;
            }
            var left = plans.SelectMany(p => p.Blocking.Select(g => (Plan: p, Gap: g)))
                .Select(x => (x.Plan, x.Gap, Left: x.Gap.Counted ? x.Gap.Over - (saved[x.Plan].TryGetValue(x.Gap.Category, out float s) ? s : 0f) : float.PositiveInfinity))
                .Where(x => x.Left > 0f).OrderByDescending(x => x.Gap.Counted ? x.Left / Math.Max(1f, x.Gap.Limit) : 1f).ToList();
            if (left.Count > 0)
            {
                var (plan, gap, rest) = left[0];
                @short = gap.Name.ToLowerInvariant() + (gap.Counted ? " stays " + Amount(gap.Category, rest) + " over" : " stays " + RankName(gap.Rating)) +
                         (plans.Count > 1 ? " on " + (plan.Mobile ? "Quest" : "PC") : "");
            }
            return -1;
        }

        // The setting steps worth offering from the current settings, with their titles.
        private static IEnumerable<(UploadOptimization Settings, string Title, string Detail)> Candidates(UploadOptimization current, HashSet<Category> blocking, bool mobile)
        {
            UploadOptimization At(OptimizationLevel level) => new UploadOptimization
                { level = level, custom = current.custom, sharpFrom = current.sharpFrom, polygonQuality = current.polygonQuality };
            switch (current.level)
            {
                case OptimizationLevel.Off:
                    yield return (At(OptimizationLevel.Small), "Small optimization",
                        "Removes what nothing shows or uses, polygons within 4 mm, textures sized to stay sharp from 1 m. Looks the same.");
                    yield return (At(OptimizationLevel.Big), "Big optimization",
                        "Also merges meshes, removes the body under clothing and polygons within 8 mm, sizes textures from 3 m.");
                    break;
                case OptimizationLevel.Small:
                    yield return (At(OptimizationLevel.Big), "Big optimization",
                        "Merges meshes, removes the body under clothing and polygons within 8 mm, sizes textures from 3 m.");
                    break;
                case OptimizationLevel.Custom:
                    var missing = Lowers.Where(l => (current.custom & l.Pass) == 0 && (l.Pass != OptimizationPasses.QuestTextures || mobile) && l.Lowers.Any(blocking.Contains))
                        .Select(l => l.Pass).ToList();
                    if (missing.Count > 0)
                    {
                        var on = At(OptimizationLevel.Custom);
                        foreach (var pass in missing) on.custom |= pass;
                        yield return (on, "Turn on " + string.Join(", ", missing.Select(p => Names[p])), "Switches of the Custom level that lower what holds the rank.");
                    }
                    if (current.polygonQuality > UploadOptimization.BigPolygonQuality && (current.custom & OptimizationPasses.FewerPolygons) != 0 && blocking.Contains(Category.PolyCount))
                    {
                        var lighter = At(OptimizationLevel.Custom); lighter.polygonQuality = UploadOptimization.BigPolygonQuality;
                        yield return (lighter, "Polygons: Lighter (8 mm)", "Lets the surface move up to 8 mm where polygons go, as Big does.");
                    }
                    break;
            }
            // Past Big: textures from the farthest distance, under Custom (Big's other changes kept).
            bool bigLike = current.level == OptimizationLevel.Big || current.level == OptimizationLevel.Custom && (current.custom & OptimizationPasses.ResizeTextures) != 0;
            float farthest = TextureDensity.Distances[TextureDensity.Distances.Length - 1];
            if (bigLike && blocking.Contains(Category.TextureMegabytes) && current.SharpFrom < farthest)
            {
                var far = new UploadOptimization
                {
                    level = OptimizationLevel.Custom, custom = current.level == OptimizationLevel.Big ? UploadOptimization.Big : current.custom,
                    sharpFrom = farthest, polygonQuality = current.PolygonQuality,
                };
                yield return (far, "Textures sharp from " + farthest.ToString("0.#", CultureInfo.InvariantCulture) + " m",
                    "Custom level with " + (current.level == OptimizationLevel.Big ? "Big's changes" : "your switches") + ": textures upload smaller, sharp from farther away.");
            }
        }

        // What a setting saves at upload: the current estimate's values less those of the other settings' estimate.
        private static Dictionary<Category, float> Savings(RankStats now, RankStats then)
        {
            var saves = new Dictionary<Category, float>();
            if (now == null || then == null) return saves;
            foreach (var pair in now.Values)
            {
                float saved = pair.Value - then.Value(pair.Key);
                if (saved > 0f) saves[pair.Key] = saved;
            }
            return saves;
        }

        /// <summary>A step's savings in the categories that block, as a line ("−12,400 polygons · −2 skinned meshes").</summary>
        internal static string Describe(RankStep step, IReadOnlyList<RankPlan> plans)
        {
            var primary = plans[0];
            var saves = step.Saves(primary.Mobile);
            if (saves == null) return "";
            var parts = primary.Gaps.Where(g => saves.TryGetValue(g.Category, out float s) && s > 0f).OrderByDescending(g => g.Blocks)
                .Take(3).Select(g => "−" + Amount(g.Category, saves[g.Category]) + " " + Unit(g.Category, saves[g.Category])).ToList();
            return string.Join(" · ", parts);
        }

        internal static string Amount(Category category, float value) =>
            category == Category.TextureMegabytes ? value.ToString(value < 10f ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB" : Count(value);

        // The unit after an amount: what the category counts.
        internal static string Unit(Category category, float value)
        {
            bool one = Math.Abs(value - 1f) < .5f;
            switch (category)
            {
                case Category.TextureMegabytes: return "of textures";
                case Category.PolyCount: return one ? "polygon" : "polygons";
                case Category.SkinnedMeshCount: return one ? "skinned mesh" : "skinned meshes";
                case Category.MeshCount: return one ? "mesh" : "meshes";
                case Category.MaterialCount: return one ? "material slot" : "material slots";
                case Category.BoneCount: return one ? "bone" : "bones";
                case Category.PhysBoneComponentCount: return one ? "PhysBone" : "PhysBones";
                case Category.PhysBoneTransformCount: return "PhysBone " + (one ? "transform" : "transforms");
                case Category.PhysBoneColliderCount: return one ? "collider" : "colliders";
                case Category.PhysBoneCollisionCheckCount: return one ? "collision check" : "collision checks";
                case Category.ContactCount: return one ? "contact" : "contacts";
                case Category.ConstraintsCount: return one ? "constraint" : "constraints";
                default: return RankPlan.NameOf(category).ToLowerInvariant();
            }
        }

        internal static string Count(float value) => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);

        internal static string RankName(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.Excellent: return "Excellent";
                case PerformanceRating.Good: return "Good";
                case PerformanceRating.Medium: return "Medium";
                case PerformanceRating.Poor: return "Poor";
                case PerformanceRating.VeryPoor: return "Very Poor";
                default: return "Unranked";
            }
        }
    }
}
