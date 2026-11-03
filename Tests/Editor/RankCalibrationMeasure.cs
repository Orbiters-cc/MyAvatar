using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Budget;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Category = VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// Measures what Reach a rank's estimates are calibrated on (<see cref="RankCalibrationTests"/>): for each avatar of the
    /// scenes in ORBITERS_RANKCAL_SCENES (";"-separated, project paths), its facts as the estimate of Off reads them, each
    /// mesh's triangles after the simplifier at ½, 1, 2, 4 and 8 mm (as estimates run it, every blendshape kept), and the
    /// estimates of Off, Small and Big on PC and mobile. Written as JSON to ORBITERS_RANKCAL_OUT. Opens the scenes, so it
    /// runs only in a batch editor (Unity -batchmode -runTests -testFilter RankCalibrationMeasure).
    /// </summary>
    public sealed class RankCalibrationMeasure
    {
        internal static readonly float[] Millimetres = { .5f, 1f, 2f, 4f, 8f };
        internal static readonly Category[] Categories =
            { Category.PolyCount, Category.SkinnedMeshCount, Category.MeshCount, Category.MaterialCount, Category.TextureMegabytes };

        internal sealed class Avatar
        {
            public string Scene, Name;
            public RankFacts Facts;
            public Dictionary<string, Dictionary<string, float>> Off = new Dictionary<string, Dictionary<string, float>>();
            /// <summary>Level name ("Small", "Big") → platform ("pc", "mobile") → category → value at upload.</summary>
            public Dictionary<string, Dictionary<string, Dictionary<string, float>>> Levels = new Dictionary<string, Dictionary<string, Dictionary<string, float>>>();
            /// <summary>Renderer path → millimetres (as "0.5") → triangles left.</summary>
            public Dictionary<string, Dictionary<string, int>> Simplified = new Dictionary<string, Dictionary<string, int>>();
            public List<string> Errors = new List<string>();
        }

        internal static float Quality(float millimetres) => 1f - Mathf.Log(millimetres / .5f, 2f) / 4f;

        [UnityTest, Explicit("Opens scenes: batch editor only, for calibration.")]
        public IEnumerator Measure()
        {
            if (!Application.isBatchMode) Assert.Ignore("Opens scenes: run in a batch editor only.");
            string scenes = Environment.GetEnvironmentVariable("ORBITERS_RANKCAL_SCENES"), output = Environment.GetEnvironmentVariable("ORBITERS_RANKCAL_OUT");
            if (string.IsNullOrEmpty(scenes) || string.IsNullOrEmpty(output)) Assert.Ignore("ORBITERS_RANKCAL_SCENES and ORBITERS_RANKCAL_OUT name what to measure.");
            bool factsOnly = Environment.GetEnvironmentVariable("ORBITERS_RANKCAL_FACTS") == "1";
            // The simplifier says in the Console what it can't simplify: that is measured too, not a failure.
            LogAssert.ignoreFailingMessages = true;
            // Avatars measured whole by an earlier run are kept as they were.
            var avatars = File.Exists(output)
                ? (Newtonsoft.Json.JsonConvert.DeserializeObject<List<Avatar>>(File.ReadAllText(output)) ?? new List<Avatar>()).Where(a => a.Facts != null && a.Errors.Count == 0).ToList()
                : new List<Avatar>();
            foreach (string path in scenes.Split(';').Where(s => s.Trim().Length > 0))
            {
                var scene = EditorSceneManager.OpenScene(path.Trim(), OpenSceneMode.Single);
                // Avatars, not the descriptors some keep inside them (fallback stand-ins under the avatar).
                var roots = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true))
                    .Where(d => d.transform.parent == null || d.transform.parent.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true) == null)
                    .Select(d => d.gameObject).ToList();
                // One at a time, the others hidden (each one's estimate reads its own hierarchy only, but a hidden copy is cheaper alone).
                foreach (var root in roots)
                {
                    foreach (var other in roots) other.SetActive(other == root);
                    var known = avatars.FirstOrDefault(a => a.Scene == path.Trim() && a.Name == root.name);
                    // ORBITERS_RANKCAL_FACTS=1: an avatar measured before only reads its facts again (what the estimate of Off keeps
                    // of it changed), its simplified meshes and levels kept.
                    if (known != null && !factsOnly) continue;
                    var avatar = known ?? new Avatar { Scene = path.Trim(), Name = root.name };
                    if (known == null) avatars.Add(avatar);
                    Debug.Log("[RankCal] " + avatar.Scene + " : " + avatar.Name);
                    var enumerator = MeasureAvatar(root, avatar, factsOnly && known != null);
                    while (true)
                    {
                        bool next;
                        try { next = enumerator.MoveNext(); }
                        catch (Exception ex) { avatar.Errors.Add(ex.ToString()); Debug.LogException(ex); break; }
                        if (!next) break;
                        yield return enumerator.Current;
                    }
                    File.WriteAllText(output, Newtonsoft.Json.JsonConvert.SerializeObject(avatars, Newtonsoft.Json.Formatting.Indented));
                }
            }
            Debug.Log("[RankCal] wrote " + avatars.Count + " avatars to " + output);
        }

        private static IEnumerator MeasureAvatar(GameObject root, Avatar avatar, bool factsOnly)
        {
            var my = root.GetComponent<MyAvatar>() ?? root.AddComponent<MyAvatar>();
            // The import settings My Avatar fixes by itself in the editor (Read/Write on, so the simplifier can read the meshes).
            VrchatImportSettings.Fix(VrchatImportSettings.Inspect(root));
            // Each mesh at each budget, on a copy, as an estimate simplifies it (owner 0: one budget at a time stays wanted).
            foreach (float mm in factsOnly ? new float[0] : Millimetres)
            {
                for (int attempt = 0; ; attempt++)
                {
                    var preview = EditorSceneManager.NewPreviewScene();
                    var copy = Object.Instantiate(root);
                    SceneManager.MoveGameObjectToScene(copy, preview);
                    var made = new List<Mesh>();
                    PolygonReduction.Reduce(new AvatarUsage(copy), Quality(mm), made, null, background: true, owner: 0);
                    bool waiting = PolygonReduction.Measuring;
                    if (!waiting || attempt > 2)
                        foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                        {
                            var mesh = MeshOptimization.MeshOf(renderer);
                            if (mesh == null || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                            string path = AnimationUtility.CalculateTransformPath(renderer.transform, copy.transform);
                            if (!avatar.Simplified.TryGetValue(path, out var byBudget)) avatar.Simplified[path] = byBudget = new Dictionary<string, int>();
                            byBudget[mm.ToString(System.Globalization.CultureInfo.InvariantCulture)] = RankContributors.Triangles(mesh);
                        }
                    foreach (var mesh in made) Object.DestroyImmediate(mesh);
                    Object.DestroyImmediate(copy);
                    EditorSceneManager.ClosePreviewScene(preview);
                    if (!waiting || attempt > 2) break;
                    double start = EditorApplication.timeSinceStartup;
                    while (PolygonReduction.Measuring && EditorApplication.timeSinceStartup - start < 1800) yield return null;
                }
            }

            var off = new UploadOptimization { level = OptimizationLevel.Off };
            OptimizationEstimate.Result measured = null;
            foreach (var e in Estimate(my, off, r => measured = r)) yield return e;
            if (measured?.Upload == null) { avatar.Errors.Add("Off: " + measured?.Error); yield break; }
            avatar.Facts = measured.Upload.Facts ?? measured.Now?.Facts;
            avatar.Off["pc"] = Values(measured.Upload.PcRank);
            avatar.Off["mobile"] = Values(measured.Upload.MobileRank);
            if (factsOnly) yield break;
            foreach (var level in new[] { OptimizationLevel.Small, OptimizationLevel.Big })
            {
                measured = null;
                foreach (var e in Estimate(my, new UploadOptimization { level = level }, r => measured = r)) yield return e;
                if (measured?.Upload == null) { avatar.Errors.Add(level + ": " + measured?.Error); continue; }
                avatar.Levels[level.ToString()] = new Dictionary<string, Dictionary<string, float>>
                {
                    ["pc"] = Values(measured.Upload.PcRank), ["mobile"] = Values(measured.Upload.MobileRank),
                };
            }
        }

        // The finished, whole estimate of the settings (measured again once the simplifier's meshes are ready).
        private static IEnumerable<object> Estimate(MyAvatar avatar, UploadOptimization settings, Action<OptimizationEstimate.Result> done)
        {
            avatar.uploadOptimization = settings;
            double start = EditorApplication.timeSinceStartup;
            while (EditorApplication.timeSinceStartup - start < 3600)
            {
                var result = OptimizationEstimate.Get(avatar, settings.Passes, null);
                if (result == null) { OptimizationEstimate.Finish(); result = OptimizationEstimate.Get(avatar, settings.Passes, null); }
                if (result != null && (result.Error != null || !result.Partial && !PolygonReduction.Measuring)) { done(result); yield break; }
                yield return null;
            }
            done(null);
        }

        private static Dictionary<string, float> Values(RankStats stats) =>
            Categories.ToDictionary(c => c.ToString(), c => stats?.Value(c) ?? 0f);
    }
}
