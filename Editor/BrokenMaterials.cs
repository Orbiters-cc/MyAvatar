using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>A material of the avatar that does not render (pink), what it was made for, and what fixing it does.</summary>
    internal sealed class BrokenMaterial
    {
        public Material Material;
        public readonly List<Renderer> Renderers = new List<Renderer>();
        /// <summary>Why it does not render: its shader is missing, fails to compile or is unsupported.</summary>
        public string Problem;
        public ShaderFamily Family;
        /// <summary>The shader it was made for, as people name it ("lilToon Outline", "Poiyomi Toon"), or null when unknown.</summary>
        public string MadeFor;
        /// <summary>A Poiyomi material locked (optimized) into a generated shader.</summary>
        public bool Locked;
        /// <summary>The shader it moves to, null when it cannot be fixed (<see cref="Reason"/> says why).</summary>
        public Shader Target;
        /// <summary>The same family: every saved setting is kept as it is.</summary>
        public bool Rebind;
        /// <summary>Read-only (in a package or a model): fixed on a copy assigned to this avatar's renderers.</summary>
        public bool Copy;
        public string Reason;
        /// <summary>Once fixed where it is: the copy of the original kept beside it ("name (old).mat"), else null.</summary>
        public string Backup;
        /// <summary>The VPM package that would make it render exactly as made, when that is not in the project.</summary>
        public string Installable;
        internal Surface Surface;
        public readonly List<string> Kept = new List<string>();
        public readonly List<string> Lost = new List<string>();

        public string TargetName => Target ? MaterialShaders.Pretty(Target.name) : null;

        /// <summary>One line: what it was made for and what it becomes.</summary>
        public string Change
        {
            get
            {
                string from = Problem != BrokenMaterials.Missing ? Problem
                    : Locked ? "Locked " + (MadeFor ?? "Poiyomi") + ": its generated shader is missing"
                    : MadeFor == null ? "Its shader is missing"
                    : Rebind ? "Its version of " + MadeFor + " is missing"
                    : MadeFor + " isn't in this project";
                if (!Target) return from;
                if (Locked && Rebind) return from + " → unlocked on " + TargetName;
                return from + " → " + TargetName;
            }
        }

        /// <summary>What it keeps once fixed, and what it loses.</summary>
        public string Details
        {
            get
            {
                if (!Target) return Reason;
                var parts = new List<string>();
                if (Rebind) parts.Add(Locked ? "Every setting kept; Poiyomi locks it again when you upload" : "Every setting kept");
                else if (Kept.Count > 0) parts.Add("Keeps its " + List(Kept));
                if (Lost.Count > 0) parts.Add("not carried over: " + string.Join(", ", Lost));
                if (Copy) parts.Add("fixed on a copy (" + (AssetPath.StartsWith("Packages/", StringComparison.Ordinal) ? "it is in a package" : "it is inside a model") + ")");
                string text = string.Join(" · ", parts);
                return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
            }
        }

        internal string AssetPath => Material ? AssetDatabase.GetAssetPath(Material) ?? "" : "";

        private static string List(List<string> items) =>
            items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];
    }

    /// <summary>
    /// The avatar's materials that do not render (Unity draws them pink): their shader is missing, fails to compile or is
    /// unsupported, Poiyomi's generated shader of a locked material included. Each one is read from what it saved and moved
    /// to the best shader in the project: its own family keeps every setting, another family gets its texture, color, normal
    /// map, emission and blending. Materials in the project are fixed where they are (every avatar using one renders again),
    /// the original kept beside each as "name (old)"; those in packages or models are copied into My Avatar's folder for this
    /// avatar. One Undo step restores everything.
    /// </summary>
    internal static class BrokenMaterials
    {
        internal const string Missing = "Its shader is missing";
        /// <summary>The label of the originals kept by a fix: never listed as broken themselves.</summary>
        internal const string BackupLabel = "MyAvatarBackup";
        internal const string BackupSuffix = " (old)";

        internal static bool IsBackup(Material material) => material && AssetDatabase.GetLabels(material).Contains(BackupLabel);

        /// <summary>
        /// Keeps <paramref name="material"/>'s file as it is now beside it, as "name (old).mat" with its own GUID and the backup
        /// label (with the shader it was made for, it renders again once that shader is installed). A backup already there
        /// (fixed before, then undone) is the same original and stays. Returns its path, null for a material without a .mat file.
        /// </summary>
        internal static string Backup(Material material)
        {
            string path = material ? AssetDatabase.GetAssetPath(material) : null;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) || !AssetDatabase.IsMainAsset(material)) return null;
            string target = Path.GetDirectoryName(path).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(path) + BackupSuffix + ".mat";
            if (IsBackup(AssetDatabase.LoadAssetAtPath<Material>(target))) return target;
            target = AssetDatabase.GenerateUniqueAssetPath(target);
            AssetDatabase.SaveAssetIfDirty(material);
            if (!AssetDatabase.CopyAsset(path, target)) throw new IOException("Could not keep a copy of " + path + " before fixing it.");
            var copy = AssetDatabase.LoadAssetAtPath<Material>(target);
            AssetDatabase.SetLabels(copy, AssetDatabase.GetLabels(copy).Append(BackupLabel).Distinct().ToArray());
            return target;
        }

        /// <summary>Why <paramref name="material"/> does not render, or null when it does.</summary>
        internal static string Problem(Material material)
        {
            if (!material) return null;
            var shader = material.shader;
            if (!shader || shader.name == MaterialShaders.ErrorShader) return Missing;
            if (ShaderUtil.ShaderHasError(shader)) return MaterialShaders.Pretty(shader.name) + " has errors";
            if (!shader.isSupported) return MaterialShaders.Pretty(shader.name) + " isn't supported here";
            return null;
        }

        internal static GameObject AvatarRoot(GameObject target)
        {
            if (!target) return null;
            var descriptor = target.GetComponentInParent<VRC.SDKBase.VRC_AvatarDescriptor>(true);
            return descriptor ? descriptor.gameObject : target;
        }

        /// <summary>The materials below <paramref name="root"/> that do not render, each with its plan.</summary>
        internal static List<BrokenMaterial> Inspect(GameObject root, ShaderCatalog catalog = null)
        {
            var found = new Dictionary<Material, BrokenMaterial>();
            if (!root) return new List<BrokenMaterial>();
            var fine = new HashSet<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (AvatarMaterials.Helper(renderer.transform, root.transform)) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!material || fine.Contains(material)) continue;
                    if (!found.TryGetValue(material, out var entry))
                    {
                        // An original a fix kept is meant to stay as it was made, even when put back on a renderer.
                        string problem = IsBackup(material) ? null : Problem(material);
                        if (problem == null) { fine.Add(material); continue; }
                        found[material] = entry = new BrokenMaterial { Material = material, Problem = problem };
                    }
                    if (!entry.Renderers.Contains(renderer)) entry.Renderers.Add(renderer);
                }
            }
            catalog ??= new ShaderCatalog();
            foreach (var entry in found.Values)
            {
                MaterialShaders.Plan(entry, SavedMaterial.Read(entry.Material), catalog);
                entry.Copy = entry.Target && ReadOnly(entry.Material);
                // Particles, trails and lines need a particle shader: only their own family's shader is safe to guess.
                if (entry.Target && !entry.Rebind && entry.Renderers.Any(r => r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer))
                {
                    entry.Target = null; entry.Copy = false;
                    entry.Kept.Clear(); entry.Lost.Clear();
                    entry.Reason = "It draws a particle effect, trail or line: pick a particle shader for it.";
                }
            }
            return found.Values.OrderBy(e => e.Material.name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A material that cannot be changed where it is: inside a package or a model.</summary>
        internal static bool ReadOnly(Material material)
        {
            string path = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(path)) return false;
            return path.StartsWith("Packages/", StringComparison.Ordinal) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
                   !AssetDatabase.IsMainAsset(material);
        }

        /// <summary>
        /// Fixes <paramref name="entries"/> on the avatar below <paramref name="root"/> in one Undo step and logs what changed.
        /// <paramref name="avatar"/> (optional) keeps its texture set's Undo following copies. Returns how many were fixed.
        /// </summary>
        internal static int Fix(GameObject root, MyAvatar avatar, IEnumerable<BrokenMaterial> entries, bool copyAll = false)
        {
            if (!root) return 0;
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Fix materials outside Play Mode.");
            if (EditorUtility.IsPersistent(root)) throw new InvalidOperationException("Fix materials on an avatar in an open scene.");
            var list = entries.Where(e => e.Material && e.Target && Problem(e.Material) != null).ToList();
            if (list.Count == 0) return 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Fix " + root.name + " materials");
            var replacements = new Dictionary<Material, Material>();
            var renderers = new List<Renderer>();
            var edited = new List<Material>();
            var log = new List<string>();
            foreach (var entry in list)
            {
                var source = entry.Material;
                var saved = SavedMaterial.Read(source);
                string before = entry.MadeFor ?? MaterialShaders.Pretty(source.shader ? source.shader.name : null);
                if (entry.Copy || copyAll)
                {
                    TextureImport.EnsureFolder(AvatarMaterials.Folder);
                    var copy = new Material(source) { name = source.name };
                    MaterialShaders.Convert(copy, entry, saved);
                    AssetDatabase.CreateAsset(copy, AssetDatabase.GenerateUniqueAssetPath(AvatarMaterials.Folder + "/" + FileName(source.name) + ".mat"));
                    replacements[source] = copy;
                    renderers.AddRange(entry.Renderers);
                    log.Add(source.name + " (" + before + " → " + entry.Target.name + ", copied to " + AssetDatabase.GetAssetPath(copy) + Lost(entry) + ")");
                }
                else
                {
                    entry.Backup = Backup(source);
                    Undo.RecordObject(source, "Fix materials");
                    MaterialShaders.Convert(source, entry, saved);
                    EditorUtility.SetDirty(source);
                    if (EditorUtility.IsPersistent(source)) edited.Add(source);
                    log.Add(source.name + " (" + before + " → " + entry.Target.name + Lost(entry) + (entry.Backup != null ? "; original kept as " + entry.Backup : "") + ")");
                }
            }
            if (replacements.Count > 0) AvatarMaterials.Reassign(avatar, renderers, replacements);
            foreach (var material in edited) AssetDatabase.SaveAssetIfDirty(material);
            Undo.CollapseUndoOperations(group);
            SceneView.RepaintAll();
            Debug.Log("[My Avatar] Fixed " + list.Count + (list.Count == 1 ? " material" : " materials") + " of " + root.name + " that did not render: " +
                      string.Join("; ", log) + ". Undo restores them.");
            return list.Count;
        }

        private static string Lost(BrokenMaterial entry) => entry.Lost.Count == 0 ? "" : "; not carried over: " + string.Join(", ", entry.Lost);

        private static string FileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
