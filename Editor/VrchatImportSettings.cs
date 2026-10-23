using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Validation;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// The import settings VRChat's SDK panel asks of an avatar's files before an upload ("Review Any Alerts"): models
    /// imported with Read/Write, skinned meshes whose calculated blendshape normals use Unity's legacy computation (a much
    /// smaller upload), mipmapped textures with Streaming Mip Maps. The same checks as the SDK's own
    /// (VRCSdkControlPanelAvatarBuilder: skinned, filtered and particle meshes and renderers' materials, none under an
    /// EditorOnly object), so its alerts go once these are fixed. Only files in Assets are changed: a package's
    /// files are not the project's to edit.
    /// <para>
    /// By itself (Orbiters settings: "Fix VRChat's import alerts by itself", on by default) My Avatar fixes them when its
    /// card looks at an avatar, and when VRChat's SDK panel shows an avatar My Avatar looks after: the panel then has
    /// nothing to report.
    /// </para>
    /// </summary>
    internal static class VrchatImportSettings
    {
        internal const string Feature = "MyAvatar.FixSdkImportAlerts";

        /// <summary>What an avatar's files lack, by asset path.</summary>
        internal sealed class Plan
        {
            public readonly List<string> ReadWrite = new List<string>();
            public readonly List<string> BlendShapeNormals = new List<string>();
            public readonly List<string> StreamingMips = new List<string>();
            /// <summary>Files outside Assets that lack them too (left as they are).</summary>
            public readonly List<string> Elsewhere = new List<string>();
            public int Count => ReadWrite.Union(BlendShapeNormals).Count() + StreamingMips.Count;
            public bool Empty => Count == 0;
            public string Key => string.Join("|", ReadWrite) + "#" + string.Join("|", BlendShapeNormals) + "#" + string.Join("|", StreamingMips);

            /// <summary>What it changes, in a sentence: "Read/Write on 2 models, …".</summary>
            public string Describe()
            {
                var parts = new List<string>();
                if (ReadWrite.Count > 0) parts.Add("Read/Write on " + Many(ReadWrite.Count, "model"));
                if (BlendShapeNormals.Count > 0) parts.Add("legacy blendshape normals on " + Many(BlendShapeNormals.Count, "model"));
                if (StreamingMips.Count > 0) parts.Add("streaming mip maps on " + Many(StreamingMips.Count, "texture"));
                return string.Join(", ", parts);
            }

            private static string Many(int n, string what) => n + " " + what + (n == 1 ? "" : "s");
        }

        // ModelImporter's "Legacy Blendshape Normals", not public (the SDK sets it the same way).
        private static readonly PropertyInfo LegacyBlendShapeNormals =
            typeof(ModelImporter).GetProperty("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes", BindingFlags.Instance | BindingFlags.NonPublic);

        // Files a fix was tried on this session: one that didn't take (a locked file) is not tried again and again.
        private static readonly HashSet<string> Tried = new HashSet<string>();
        // Per avatar (instance id): what was fixed by itself this session, for its card to say.
        internal static readonly Dictionary<int, string> FixedNotes = new Dictionary<int, string>();
        internal static event Action<int> Fixed;

        [InitializeOnLoadMethod]
        private static void Register()
        {
            OrbitersFeatures.Register(new OrbitersFeature
            {
                Key = Feature, Product = "My Avatar", Label = "Fix VRChat's import alerts by itself",
                Description = "VRChat's SDK panel asks models to be imported with Read/Write and legacy blendshape normals, and mipmapped textures to " +
                              "stream their mip maps. My Avatar changes those import settings as soon as it finds them, so the panel has nothing to report. " +
                              "Switch it off to be asked first.",
                Stage = FeatureStage.Stable, Default = true,
            });
            EditorApplication.update += Watch;
        }

        /// <summary>What the files of the avatar under <paramref name="root"/> lack, as VRChat's SDK panel checks them.</summary>
        internal static Plan Inspect(GameObject root)
        {
            var plan = new Plan();
            if (root == null) return plan;
            // The meshes the SDK looks at: skinned meshes, mesh filters' meshes and particle meshes.
            var meshes = new HashSet<Mesh>();
            foreach (var skinned in root.GetComponentsInChildrenExcludingEditorOnly<SkinnedMeshRenderer>(true))
                if (skinned && skinned.sharedMesh) meshes.Add(skinned.sharedMesh);
            foreach (var filter in root.GetComponentsInChildrenExcludingEditorOnly<MeshFilter>(true))
                if (filter && filter.sharedMesh) meshes.Add(filter.sharedMesh);
            foreach (var particles in root.GetComponentsInChildrenExcludingEditorOnly<ParticleSystemRenderer>(true))
            {
                if (!particles) continue;
                var shapes = new Mesh[particles.meshCount];
                particles.GetMeshes(shapes);
                foreach (var shape in shapes) if (shape) meshes.Add(shape);
            }
            var models = new HashSet<string>();
            foreach (var mesh in meshes)
            {
                if (!AssetDatabase.Contains(mesh)) continue;
                string path = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(path) || !models.Add(path) || !(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;
                bool readWrite = !importer.isReadable;
                bool normals = importer.importBlendShapeNormals == ModelImporterNormals.Calculate && LegacyBlendShapeNormals != null &&
                               !(bool)LegacyBlendShapeNormals.GetValue(importer);
                if (!readWrite && !normals) continue;
                if (!Writable(path)) { plan.Elsewhere.Add(path); continue; }
                if (readWrite) plan.ReadWrite.Add(path);
                if (normals) plan.BlendShapeNormals.Add(path);
            }
            var textures = new HashSet<string>();
            var renderers = root.GetComponentsInChildrenExcludingEditorOnly<Renderer>(true).Where(r => r);
            foreach (var material in renderers.SelectMany(r => r.sharedMaterials).Where(m => m).Distinct())
            foreach (int id in material.GetTexturePropertyNameIDs())
            {
                var texture = material.GetTexture(id);
                if (!texture) continue;
                string path = AssetDatabase.GetAssetPath(texture);
                if (string.IsNullOrEmpty(path) || !textures.Add(path)) continue;
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer) || !importer.mipmapEnabled || importer.streamingMipmaps) continue;
                if (Writable(path)) plan.StreamingMips.Add(path);
                else plan.Elsewhere.Add(path);
            }
            return plan;
        }

        /// <summary>
        /// Changes the import settings <paramref name="plan"/> lists, every file reimported once at the end. Returns what
        /// changed (empty when nothing did).
        /// </summary>
        internal static Plan Fix(Plan plan)
        {
            var done = new Plan();
            if (plan == null || plan.Empty) return done;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in plan.ReadWrite.Union(plan.BlendShapeNormals))
                {
                    Tried.Add(path);
                    if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;
                    if (!importer.isReadable) { importer.isReadable = true; done.ReadWrite.Add(path); }
                    if (importer.importBlendShapeNormals == ModelImporterNormals.Calculate && LegacyBlendShapeNormals != null &&
                        !(bool)LegacyBlendShapeNormals.GetValue(importer))
                    {
                        LegacyBlendShapeNormals.SetValue(importer, true);
                        done.BlendShapeNormals.Add(path);
                    }
                    importer.SaveAndReimport();
                }
                foreach (string path in plan.StreamingMips)
                {
                    Tried.Add(path);
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter importer) || importer.streamingMipmaps) continue;
                    importer.streamingMipmaps = true;
                    importer.streamingMipmapsPriority = 0;
                    importer.SaveAndReimport();
                    done.StreamingMips.Add(path);
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            return done;
        }

        /// <summary>
        /// Fixes what the avatar's files lack by themselves (when the feature is on), once per file and session; records
        /// what changed for the avatar's card. Returns what changed.
        /// </summary>
        internal static Plan AutoFix(MyAvatar avatar, GameObject root)
        {
            var none = new Plan();
            if (!avatar || root == null || !OrbitersFeatures.IsEnabled(Feature) || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating || EditorUtility.IsPersistent(root)) return none;
            var plan = Inspect(root);
            plan.ReadWrite.RemoveAll(Tried.Contains); plan.BlendShapeNormals.RemoveAll(Tried.Contains); plan.StreamingMips.RemoveAll(Tried.Contains);
            if (plan.Empty) return none;
            var done = Fix(plan);
            if (done.Empty) return done;
            string note = "VRChat's SDK asked for " + done.Describe() + ": fixed in their import settings.";
            FixedNotes[avatar.GetInstanceID()] = note;
            Debug.Log("[My Avatar] " + root.name + ": " + note);
            Fixed?.Invoke(avatar.GetInstanceID());
            return done;
        }

        // The avatar VRChat's SDK panel shows, looked at every few seconds: one My Avatar looks after is fixed before the
        // panel lists its alerts (it checks them again as it draws).
        private static double nextWatch;
        private static void Watch()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < nextWatch) return;
            nextWatch = now + 2;
            try
            {
                if (VRCSdkControlPanel.window == null || !OrbitersFeatures.IsEnabled(Feature) ||
                    !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null) return;
                var selected = builder.SelectedAvatar;
                if (selected == null) return;
                var avatar = selected.GetComponentInChildren<MyAvatar>(true);
                if (!avatar) return;
                AutoFix(avatar, selected);
            }
            catch (Exception)
            {
                // A destroyed avatar or a closing panel: looked at again shortly.
            }
        }

        private static bool Writable(string path) => path.StartsWith("Assets/", StringComparison.Ordinal);
    }
}
