using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Orbiters.Toolkit.Editor.LiveLink;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// What Substance 3D Painter needs to make a project of the avatar (written next to a job file its plugin picks up):
    /// the posed meshes as one OBJ (a texture set per material, the copies in natural poses painting the same ones), each
    /// material's textures and values (base colour, opacity, roughness, metallic, emission) and its normal map, which becomes
    /// the texture set's baked normal. The texture sets are mapped to the materials at once.
    /// </summary>
    internal static class PainterProjectExport
    {
        /// <summary>A material as a texture set: its name in the OBJ and the avatar's material it paints.</summary>
        internal sealed class Set
        {
            public string Name;
            public Material Material;
            public PainterMapping.Choice Choice;
        }

        /// <summary>
        /// A texture set per material of the avatar the parts show, named after it, for each material of the parts. A build
        /// hands its own copies of the materials (VRCFury's are "VRCFury M_Skin (Body)"): the avatar's material is the one
        /// its renderer at the same place shows in the same slot, else the one of the same name. Copies of one material
        /// paint one texture set. A mesh made again for a project (<paramref name="project"/>: its texture sets) keeps their
        /// names: a material whose renderer slot a set of its own name (or that name numbered, "Skin_2") paints keeps that
        /// set, whatever order the parts come in (Painter keeps a texture set's strokes by its name).
        /// </summary>
        internal static Dictionary<Material, Set> Sets(MyAvatar avatar, IEnumerable<PainterPose.Part> parts, IList<PainterSet> project = null)
        {
            var choices = PainterMapping.Choices(avatar);
            var sets = new Dictionary<Material, Set>();
            var byAvatarMaterial = new Dictionary<Material, Set>();
            var order = new List<Material>();
            foreach (var part in parts)
                for (int slot = 0; slot < part.Materials.Length; slot++)
                {
                    var material = part.Materials[slot];
                    if (!material || sets.ContainsKey(material)) continue;
                    var shown = SceneMaterial(avatar, part.Source, slot);
                    var choice = choices.FirstOrDefault(c => c.Material == material) ?? choices.FirstOrDefault(c => shown && c.Material == shown)
                                 ?? choices.FirstOrDefault(c => c.Material.name == material.name);
                    var key = choice?.Material ?? material;
                    if (!byAvatarMaterial.TryGetValue(key, out var set))
                    {
                        byAvatarMaterial[key] = set = new Set { Material = material, Choice = choice };
                        order.Add(key);
                    }
                    sets[material] = set;
                }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // The project's names first, then new ones that no set has.
            if (project != null)
                foreach (var key in order)
                {
                    var set = byAvatarMaterial[key];
                    string own = ObjWriter.Clean(key.name);
                    var entry = set.Choice == null ? null : project.FirstOrDefault(s => s.renderer == set.Choice.Renderer && s.slot == set.Choice.Slot &&
                                                                                    Named(s.textureSet, own) && !names.Contains(s.textureSet));
                    if (entry != null && names.Add(entry.textureSet)) set.Name = entry.textureSet;
                }
            foreach (var key in order)
            {
                var set = byAvatarMaterial[key];
                if (set.Name != null) continue;
                string name = ObjWriter.Clean(key.name), unique = name;
                for (int i = 2; !names.Add(unique); i++) unique = name + "_" + i;
                set.Name = unique;
            }
            return sets;
        }

        // A texture set named after a material ("Skin"), or numbered after it when another material had the name ("Skin_2").
        private static bool Named(string set, string name) =>
            !string.IsNullOrEmpty(set) && (string.Equals(set, name, StringComparison.OrdinalIgnoreCase) ||
                                           set.Length > name.Length + 1 && set.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase) &&
                                           set.Substring(name.Length + 1).All(char.IsDigit));

        // What the avatar's renderer at the place of a copy's renderer shows in this slot: the copy is one of the avatar's root
        // (the build leaves its My Avatar out), so the same path from the roots.
        private static Material SceneMaterial(MyAvatar avatar, Renderer copied, int slot)
        {
            if (!copied) return null;
            string path = AnimationUtility.CalculateTransformPath(copied.transform, copied.transform.root);
            var root = TextureOptimization.AvatarRoot(avatar).transform;
            var place = string.IsNullOrEmpty(path) ? root : root.Find(path);
            var renderer = place ? place.GetComponent<Renderer>() : null;
            var materials = renderer ? renderer.sharedMaterials : null;
            return materials != null && slot < materials.Length ? materials[slot] : null;
        }

        /// <summary>
        /// Writes the project's files and its job, maps the texture sets on the avatar, keeps <paramref name="options"/> on it
        /// (the mesh is posed the same when the avatar's meshes change), and returns the job's file. The parts are the painted
        /// copy's, then the other copies'.
        /// </summary>
        internal static string Write(MyAvatar avatar, List<PainterPose.Part> parts, int resolution, PainterPoseOptions options)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string safe = ObjWriter.Clean(avatar.name);
            string folder = Path.Combine(PainterLinkService.LinkFolder, "projects", safe + "-" + stamp);
            Directory.CreateDirectory(folder);
            string mesh = Path.Combine(folder, safe + ".obj");
            var (obj, sets) = WriteMesh(avatar, parts, mesh);

            var slots = TextureMatching.Slots(avatar);
            var textureSets = new List<Dictionary<string, object>>();
            foreach (var set in sets.Values.Distinct().Where(s => obj.Materials.Contains(s.Name)))
            {
                var material = set.Choice?.Material ?? set.Material;
                textureSets.Add(TextureSet(set.Name, material, PainterTargets.For(material, slots), folder, resolution));
            }

            // Mapped now: the project's texture sets paint these materials from its first stroke.
            Undo.RecordObject(avatar, "My Avatar: open in Substance Painter");
            foreach (var set in sets.Values.Distinct().Where(s => obj.Materials.Contains(s.Name)))
            {
                avatar.painter.sets.RemoveAll(s => string.Equals(s.textureSet, set.Name, StringComparison.OrdinalIgnoreCase));
                avatar.painter.sets.Add(new PainterSet { textureSet = set.Name, renderer = set.Choice?.Renderer ?? "", slot = set.Choice?.Slot ?? 0 });
            }
            string project = Path.Combine(PainterLinkService.Documents, "Orbiters", "Painter",
                ObjWriter.Clean(PlayerSettings.productName), safe + ".spp");
            if (File.Exists(project)) project = Path.Combine(Path.GetDirectoryName(project), safe + " " + stamp + ".spp");
            // Kept from the home folder ("~/Documents/..."): scenes and prefabs never hold the user's name.
            avatar.painter.projectPath = PainterLinkService.Portable(project);
            avatar.painter.posed = true;
            avatar.painter.pose = PainterPose.Copy(options);
            avatar.painter.meshOutdated = false;
            EditorUtility.SetDirty(avatar);
            PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);

            var job = new Dictionary<string, object>
            {
                ["kind"] = PainterLinkProtocol.JobKind,
                ["avatarId"] = PainterLinkService.Id(avatar),
                ["avatarName"] = avatar.name,
                ["unityPid"] = Process.GetCurrentProcess().Id,
                ["unityProject"] = Path.GetDirectoryName(Application.dataPath),
                ["mesh"] = mesh.Replace('\\', '/'),
                ["project"] = project.Replace('\\', '/'),
                ["resolution"] = resolution,
                ["textureSets"] = textureSets,
            };
            string jobPath = Path.Combine(PainterLinkService.LinkFolder, "jobs", safe + "-" + stamp + ".json");
            LiveLinkServer.WriteAtomically(jobPath, JsonConvert.SerializeObject(job));
            return jobPath;
        }

        /// <summary>The posed meshes as one OBJ, a texture set (material name) per material of the parts.</summary>
        private static (ObjWriter Obj, Dictionary<Material, Set> Sets) WriteMesh(MyAvatar avatar, List<PainterPose.Part> parts, string path, IList<PainterSet> project = null)
        {
            var sets = Sets(avatar, parts, project);
            var obj = new ObjWriter();
            foreach (var part in parts)
                obj.Add(part.Name, part.Mesh, part.ToWorld, part.Materials.Select(m => m && sets.TryGetValue(m, out var set) ? set.Name : null).ToList(), part.Mirrored);
            obj.Save(path);
            return (obj, sets);
        }

        /// <summary>
        /// Writes the avatar's mesh again for its project (the same texture sets, by the names the project knows them by),
        /// in the link's meshes folder, and returns its file. The avatar's earlier ones there go: Painter keeps the mesh it
        /// loads in the project. A material new since gets a texture set of its own, mapped to it at once.
        /// </summary>
        internal static string WriteMeshUpdate(MyAvatar avatar, List<PainterPose.Part> parts)
        {
            // Named for this avatar only: another one's name may be the same (a copy, the same prefab in two scenes) or start
            // the same.
            string safe = ObjWriter.Clean(avatar.name) + "-" + Tag(avatar);
            string folder = Path.Combine(PainterLinkService.LinkFolder, "meshes");
            Directory.CreateDirectory(folder);
            var earlier = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(safe) + @"-\d{8}-\d{6}\.(obj|mtl)$");
            foreach (string file in Directory.GetFiles(folder).Where(f => earlier.IsMatch(Path.GetFileName(f))))
                try { File.Delete(file); } catch (IOException) { }
            string path = Path.Combine(folder, safe + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".obj");
            var (obj, sets) = WriteMesh(avatar, parts, path, avatar.painter.sets);
            // Bookkeeping of the link, outside Undo (as the mesh Painter loads is).
            bool mapped = false;
            foreach (var set in sets.Values.Distinct().Where(s => obj.Materials.Contains(s.Name) && s.Choice != null))
            {
                if (avatar.painter.sets.Any(s => string.Equals(s.textureSet, set.Name, StringComparison.OrdinalIgnoreCase))) continue;
                avatar.painter.sets.Add(new PainterSet { textureSet = set.Name, renderer = set.Choice.Renderer, slot = set.Choice.Slot });
                mapped = true;
            }
            if (mapped)
            {
                EditorUtility.SetDirty(avatar);
                PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
            }
            return path;
        }

        // Eight hex digits of the avatar's identity, the same in every session; with its instance while it was never saved
        // (it has no identity of its own yet).
        private static string Tag(MyAvatar avatar)
        {
            var global = GlobalObjectId.GetGlobalObjectIdSlow(avatar);
            string id = global.ToString() + (global.targetObjectId == 0 ? "#" + avatar.GetInstanceID() : "");
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id)), 0, 4).Replace("-", "").ToLowerInvariant();
        }

        // One texture set of the job: the material's maps as images, its plain values as numbers.
        private static Dictionary<string, object> TextureSet(string name, Material material, PainterTargets targets, string folder, int resolution)
        {
            var entry = new Dictionary<string, object> { ["name"] = name };
            Texture Texture(string property) => property != null && material.HasProperty(property) ? material.GetTexture(property) : null;
            string File(string kind) => Path.Combine(folder, name + "_" + kind + ".png");
            var family = MaterialShaders.FamilyOf(material.shader);

            var color = Texture(targets?.Color);
            entry["resolution"] = resolution > 0 ? resolution : Size(color);
            if (color) entry["basecolor"] = Save(color, File("basecolor"), srgb: true);
            else
            {
                var tint = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                entry["basecolor"] = new[] { tint.r, tint.g, tint.b };
            }
            var mode = MaterialShaders.Mode(SavedMaterial.Read(material), family, family == ShaderFamily.LilToon ? material.shader.name : null);
            if (color && mode != SurfaceMode.Opaque) entry["opacity"] = Save(color, File("opacity"), channel: 3);

            var normal = Texture(targets?.Normal);
            if (normal) entry["normal"] = Save(normal, File("normal"), pass: 1);

            var metal = targets?.Metallic;
            var metalTexture = Texture(metal?.Property);
            entry["metallic"] = metalTexture
                ? (object)Save(metalTexture, File("metallic"), channel: metal.Index, invert: metal.Inverted)
                : Value(material, family == ShaderFamily.Poiyomi ? "_MochieMetallicMultiplier" : family == ShaderFamily.ToonStandard ? "_MetallicStrength" : "_Metallic", 0f);
            var smooth = targets?.Smoothness;
            var smoothTexture = Texture(smooth?.Property);
            entry["roughness"] = smoothTexture
                // Painter paints roughness: a smoothness channel goes in inverted.
                ? (object)Save(smoothTexture, File("roughness"), channel: smooth.Index, invert: !smooth.Inverted)
                : 1f - Value(material, family == ShaderFamily.Poiyomi ? "_MochieRoughnessMultiplier" : family == ShaderFamily.ToonStandard ? "_GlossStrength"
                    : family == ShaderFamily.LilToon ? "_Smoothness" : "_Glossiness", .5f);

            bool emits = material.IsKeywordEnabled("_EMISSION") || Value(material, "_EnableEmission", 0f) > .5f || Value(material, "_UseEmission", 0f) > .5f;
            var emission = Texture(targets?.Emission);
            if (emits && emission) entry["emissive"] = Save(emission, File("emissive"), srgb: true);
            else if (emits && material.HasProperty("_EmissionColor"))
            {
                var glow = material.GetColor("_EmissionColor");
                entry["emissive"] = new[] { Mathf.Clamp01(glow.r), Mathf.Clamp01(glow.g), Mathf.Clamp01(glow.b) };
            }
            return entry;
        }

        private static float Value(Material material, string property, float fallback) => material.HasProperty(property) ? material.GetFloat(property) : fallback;

        // The texture's size as a texture set resolution: a power of two from 512 to 4096 (2048 without a texture).
        private static int Size(Texture texture) => texture ? Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(texture.width, texture.height)), 512, 4096) : 2048;

        /// <summary>
        /// Writes a texture as Painter reads it, through the GPU (whatever its import format): as it is (pass 0 copies), a
        /// normal map unpacked (pass 1), or one channel as gray (pass 2).
        /// </summary>
        private static string Save(Texture texture, string path, bool srgb = false, int pass = -1, int channel = -1, bool invert = false)
        {
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, !srgb);
            Material material = null;
            try
            {
                if (channel >= 0) pass = 2;
                if (pass >= 0)
                {
                    material = new Material(Shader.Find("Hidden/Orbiters/MyAvatar/PainterPack")) { hideFlags = HideFlags.HideAndDontSave };
                    var mask = Vector4.zero;
                    if (channel >= 0) mask[Mathf.Clamp(channel, 0, 3)] = 1f;
                    material.SetVector("_ChannelMask", mask);
                    material.SetFloat("_Invert", invert ? 1f : 0f);
                    Graphics.Blit(texture, target, material, pass);
                }
                else Graphics.Blit(texture, target);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                image.Apply(false);
                System.IO.File.WriteAllBytes(path, image.EncodeToPNG());
                return path.Replace('\\', '/');
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
                if (material) Object.DestroyImmediate(material);
            }
        }
    }
}
