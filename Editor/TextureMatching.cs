using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed class TextureSlot
    {
        public string id, materialName, shader, property, description, role, existingName, rendererPath, rendererKind;
        public string existingPath;
        public int existingWidth, existingHeight;
        public Material material;
        public Texture existing;
        public List<string> renderers = new List<string>();
        // Renderer and material index (= submesh) pairs that draw this material, for UV layout comparison.
        public List<(Renderer renderer, int index)> parts = new List<(Renderer, int)>();
        public bool secondary, active;
        // Images bundled with an accessory fill gaps; they are not a request to replace its setup.
        public bool fillOnly;
        public string Label => materialName + " / " + description + " (" + property + ") · " + rendererPath;
    }

    internal static class TextureMatching
    {
        internal const string RememberedReason = "Your earlier choice for this file on this avatar.";
        internal const string ChosenReason = "Chosen by you.";
        internal const string MissingSlotReason = "The rest of this texture set matched ";
        internal const string NoClearMatchReason = "No clear material and texture-slot match.";

        internal static bool CanAutoAssign(TextureEntry entry, TextureSlot slot) => !slot.fillOnly ||
            slot.existing && slot.existing == entry.texture ||
            !slot.existing && !slot.secondary && (entry.role == slot.role || entry.role == "unknown" || MaterialSurfaceMaps.CanAssign(entry.role, slot.property));

        internal static List<TextureSlot> Slots(MyAvatar avatar, bool includeAll = false)
        {
            var result = new List<TextureSlot>();
            var byMaterial = new Dictionary<Material, List<TextureSlot>>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if ((renderer.hideFlags & HideFlags.DontSave) != 0 || (renderer.gameObject.hideFlags & HideFlags.DontSave) != 0) continue;
                string kind = renderer is SkinnedMeshRenderer ? "skinned" : renderer is MeshRenderer ? "mesh" :
                    renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer ? "effect" : "other";
                bool visible = renderer.enabled && renderer.gameObject.activeInHierarchy;
                var materials = renderer.sharedMaterials;
                for (int index = 0; index < materials.Length; index++)
                {
                    var material = materials[index];
                    // Locked/optimized shaders (e.g. Poiyomi's Hidden/Locked/...) are the norm on avatars. They keep only the features
                    // that were on when locked, so slots come from the shader they were made from (hair locked with emission off
                    // still takes an emission map); applying a texture unlocks the copy.
                    if (!material || !material.shader || material.shader.name == "Hidden/InternalErrorShader") continue;
                    if (byMaterial.TryGetValue(material, out var known))
                    {
                        foreach (var slot in known)
                        {
                            if (!slot.renderers.Contains(renderer.name)) slot.renderers.Add(renderer.name);
                            if (slot.rendererKind == "effect" && kind != "effect") slot.rendererKind = kind;
                            slot.active |= visible; slot.parts.Add((renderer, index));
                        }
                        continue;
                    }
                    var slots = byMaterial[material] = new List<TextureSlot>();
                    var shader = Orbiters.Toolkit.Editor.MaterialSurfaceMaps.OriginalShader(material) ?? material.shader;
                    string shaderName = ShaderName(material);
                    for (int i = 0; i < shader.GetPropertyCount(); i++)
                    {
                        if (shader.GetPropertyType(i) != ShaderPropertyType.Texture || !includeAll && (shader.GetPropertyTextureDimension(i) != TextureDimension.Tex2D ||
                            (shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0)) continue;
                        string property = shader.GetPropertyName(i), description = CleanDescription(shader.GetPropertyDescription(i));
                        var texture = material.HasProperty(property) ? material.GetTexture(property) : Orbiters.Toolkit.Editor.MaterialSurfaceMaps.StrippedTexture(material, property);
                        var slot = new TextureSlot { id = "s" + result.Count, material = material, materialName = material.name,
                            shader = shaderName, property = property, description = description, role = SlotRole(property, description),
                            secondary = Secondary(property, description), existing = texture, existingName = texture ? texture.name : "",
                            existingPath = texture ? AssetDatabase.GetAssetPath(texture) : "", existingWidth = texture ? texture.width : 0, existingHeight = texture ? texture.height : 0,
                            rendererPath = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform), rendererKind = kind, active = visible };
                        slot.renderers.Add(renderer.name); slot.parts.Add((renderer, index));
                        slots.Add(slot); result.Add(slot);
                    }
                }
            }
            return result;
        }

        // Locked shaders report the shader they were generated from (Poiyomi stores it in the OriginalShader tag).
        internal static string ShaderName(Material material)
        {
            string original = material.GetTag("OriginalShader", false);
            return string.IsNullOrEmpty(original) ? material.shader.name : original;
        }

        // Shader GUI frameworks append metadata to display names, e.g. Thry/Poiyomi "Normal Map--{reference_properties:[...]}".
        internal static string CleanDescription(string description)
        {
            string value = description ?? "";
            int metadata = value.IndexOf("--{", StringComparison.Ordinal);
            if (metadata >= 0) value = value.Substring(0, metadata);
            return Regex.Replace(value, @"\s*\((?:expand|click to expand)\)|\s*\[click to expand\]", "", RegexOptions.IgnoreCase).Trim();
        }

        // ---- Roles -------------------------------------------------------------------------------------------

        private static readonly (string role, string[] words)[] RoleWords = {
            ("normal", new[] { "normal", "nrm", "norm", "nor", "bump", "normalmap", "bumpmap" }),
            ("emission", new[] { "emission", "emissive", "emit", "glow", "emissionmap" }),
            ("metallic", new[] { "metallic", "metal", "metalness", "metallicsmoothness", "metallicgloss" }),
            ("roughness", new[] { "roughness", "rough" }),
            ("smoothness", new[] { "smoothness", "smooth", "gloss", "glossiness" }),
            ("occlusion", new[] { "occlusion", "ao", "ambientocclusion" }),
            ("height", new[] { "height", "displacement", "disp", "parallax", "heightmap" }),
            ("specular", new[] { "specular", "spec", "specgloss" }),
            ("mask", new[] { "mask", "opacity", "alpha", "transparency" }),
            ("color", new[] { "basecolor", "basecolour", "albedo", "diffuse", "diff", "color", "colour", "col", "basemap", "maintex", "base" }),
        };

        private static readonly Dictionary<char, string> LetterRoles = new Dictionary<char, string> {
            ['d'] = "color", ['c'] = "color", ['a'] = "color", ['n'] = "normal", ['e'] = "emission", ['m'] = "metallic", ['r'] = "roughness",
            ['s'] = "smoothness", ['h'] = "height", ['o'] = "occlusion" };

        // Role of an incoming image, from its filename tokens; the last role word wins ("Normal_Emission" → emission).
        internal static string FileRole(string fileName)
        {
            if (MaterialSurfaceMaps.IsPackedFile(fileName)) return "metallic";
            var tokens = Tokens(System.IO.Path.GetFileNameWithoutExtension(fileName ?? ""), stem: false);
            string role = "unknown"; int position = -1;
            foreach (var (candidate, words) in RoleWords)
                for (int i = tokens.Count - 1; i > position; i--)
                    if (words.Contains(tokens[i]) || words.Contains(Stem(tokens[i]))) { role = candidate; position = i; break; }
            // Game-art suffix letters as the final token: Body_D / _N / _E / _M / _R / _S / _H / _O.
            if (role == "unknown" && tokens.Count > 1 && tokens[tokens.Count - 1].Length == 1 && LetterRoles.TryGetValue(tokens[tokens.Count - 1][0], out var letterRole)) role = letterRole;
            return role;
        }

        internal static string SlotRole(string property, string description)
        {
            string s = Regex.Replace(property + " " + description, "[^a-zA-Z0-9]", "").ToLowerInvariant();
            // Masks first: "_EmissionMask" or "_DetailMask" restrict another effect, they are not emission or detail maps.
            if (s.Contains("mask") && !s.Contains("maskmap")) return "mask";
            if (s.Contains("normal") || s.Contains("bump")) return "normal";
            if (s.Contains("emission") || s.Contains("emissive")) return "emission";
            if (s.Contains("metallic") || s.Contains("packed") || s.Contains("maskmap")) return "metallic";
            if (s.Contains("occlusion") || s.Contains("aomap")) return "occlusion";
            if (s.Contains("roughness")) return "roughness";
            if (s.Contains("smoothness") || s.Contains("gloss") && !s.Contains("specgloss")) return "smoothness";
            if (s.Contains("height") || s.Contains("parallax")) return "height";
            if (s.Contains("specular") || s.Contains("specgloss")) return "specular";
            if (s.Contains("alpha") || s.Contains("opacity")) return "mask";
            if (s.Contains("basecolor") || s.Contains("basemap") || s.Contains("maintex") || s.Contains("albedo") || s.Contains("diffuse") ||
                s.Contains("colormap") || s.StartsWith("maintexture", StringComparison.Ordinal)) return "color";
            return "unknown";
        }

        private static readonly string[] SecondaryWords = { "detail", "matcap", "rim", "distortion", "decal", "flipbook", "gradation", "sdf", "shadow", "lighting",
            "cubemap", "refl", "curve", "glitter", "dissolve", "outline", "layer", "flow", "noise", "ramp", "adjust", "sparkle", "iridescence", "backface", "vertex",
            "audiolink", "secondary", "second", "overlay", "stencil", "fur", "clearcoat", "sheen", "subsurface", "thickness", "anisotropy", "panosphere" };

        // Effect layers and extra slots; a plain texture set belongs in the primary slots.
        private static bool Secondary(string property, string description)
        {
            string s = (property + " " + description).ToLowerInvariant();
            return SecondaryWords.Any(s.Contains) || Regex.IsMatch(property ?? "", @"\d+$") || Regex.IsMatch(property ?? "", @"\d+(?:Map|Tex|Texture)$");
        }

        private static readonly string[] Primary = { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_BumpMap", "_NormalMap", "_EmissionMap", "_EmissiveColorMap",
            "_MetallicGlossMap", "_MochieMetallicMaps", "_OcclusionMap", "_ParallaxMap", "_SpecGlossMap", "_MaskMap" };

        private static readonly string[] ColorFamily = { "color", "emission" };
        private static readonly string[] SurfaceFamily = { "roughness", "smoothness", "specular", "occlusion" };

        private static double RoleFit(string textureRole, string slotRole)
        {
            if (textureRole == slotRole && textureRole != "unknown") return 0;
            if (ColorFamily.Contains(textureRole) && ColorFamily.Contains(slotRole)) return -1;
            if (textureRole == "unknown" && slotRole != "normal") return -.75;
            if (slotRole == "unknown" && textureRole != "normal") return -.75;
            return double.NaN;
        }

        internal static bool Compatible(string textureRole, string slotRole) => !double.IsNaN(RoleFit(textureRole, slotRole));
        internal static bool Compatible(string role, TextureSlot slot) => !double.IsNaN(RoleFit(role, slot));
        private static double RoleFit(string role, TextureSlot slot) => MaterialSurfaceMaps.CanAssign(role, slot.property) ? 0 : RoleFit(role, slot.role);

        // A metallic slot packs several maps in its channels (Standard: metallic in red, smoothness in alpha), so a single
        // roughness, smoothness, specular or occlusion image put there as it is renders wrong. Never chosen automatically;
        // the slot menu offers it apart, for an image that is already packed that way.
        internal static bool Packed(string textureRole, string slotRole) => SurfaceFamily.Contains(textureRole) && slotRole == "metallic";

        // ---- Names ------------------------------------------------------------------------------------------

        private static readonly HashSet<string> Generic = new HashSet<string>(RoleWords.SelectMany(r => r.words).Concat(new[] {
            "t", "tx", "tex", "texture", "textures", "map", "maps", "mat", "material", "materials", "png", "jpg", "jpeg", "tga", "psd", "img", "image",
            "srgb", "linear", "dx", "gl", "ogl", "packed", "orm", "arm", "mra", "rgb", "rgba", "copy", "final", "new", "default", "the", "and", "for",
            "hi", "lo", "high", "low", "res", "lod", "udim", "tile", "mesh", "tiled", "rgb", "channel", "main" }));

        // Common avatar part names that creators use interchangeably. Synonym evidence counts less than a direct match.
        private static readonly string[][] Synonyms = { new[] { "hair", "feather", "tuft", "mane" }, new[] { "eye", "iris", "pupil" }, new[] { "body", "skin" },
            new[] { "cloth", "clothe", "clothing", "outfit", "garment" }, new[] { "teeth", "tooth" }, new[] { "shoe", "boot" } };

        internal static List<string> Tokens(string value, bool stem = true)
        {
            value = Regex.Replace(value ?? "", "([a-z])([A-Z])", "$1 $2");
            value = Regex.Replace(value, "([A-Z]+)([A-Z][a-z])", "$1 $2");
            value = Regex.Replace(value, "([A-Za-z])([0-9])", "$1 $2");
            value = Regex.Replace(value, "([0-9])([A-Za-z])", "$1 $2");
            return Regex.Split(value.ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length > 0 && !t.All(char.IsDigit)).Select(t => stem ? Stem(t) : t).ToList();
        }

        private static string Stem(string token) => token.Length > 3 && token.EndsWith("s", StringComparison.Ordinal) && !token.EndsWith("ss", StringComparison.Ordinal) &&
            !token.EndsWith("is", StringComparison.Ordinal) && !token.EndsWith("us", StringComparison.Ordinal) ? token.Substring(0, token.Length - 1) : token;

        private static HashSet<string> Identity(string name) => new HashSet<string>(Tokens(name).Where(t => t.Length > 1 && !Generic.Contains(t)));

        // 1 for equal tokens or a prefix of at least three letters ("bodymatt" ~ "body", "ulti" ~ "ultiv"), 0.6 for synonyms.
        private static double Similarity(string a, string b)
        {
            if (a == b) return 1;
            if (Math.Min(a.Length, b.Length) >= 3 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))) return 1;
            foreach (var group in Synonyms) if (group.Any(a.StartsWith) && group.Any(b.StartsWith)) return .6;
            return 0;
        }

        private static double Best(string token, IEnumerable<string> bag) => bag.Select(b => Similarity(token, b)).DefaultIfEmpty(0).Max();

        // ---- Matching ---------------------------------------------------------------------------------------

        private sealed class Candidate { public TextureSlot slot; public double evidence, score; }

        internal static void Match(List<TextureEntry> textures, List<TextureSlot> slots, Dictionary<Texture2D, TextureStats> stats, Dictionary<string, List<TextureMemory.Slot>> memory)
        {
            var materials = slots.GroupBy(s => s.material).ToList();
            // A mesh name identifies a material only when that mesh has one or two materials ("HairFrontMesh"), not a whole-avatar "Body".
            var materialsPerRenderer = slots.SelectMany(s => s.renderers.Select(r => (r, s.material))).Distinct().GroupBy(p => p.r).ToDictionary(g => g.Key, g => g.Count());
            var nameBag = materials.ToDictionary(g => g.Key, g => new HashSet<string>(Identity(g.Key.name)
                .Concat(g.First().renderers.Where(r => materialsPerRenderer[r] <= 2).SelectMany(Identity))));
            // Every mesh name still adds a little evidence: shared by all of a mesh's materials, it only separates meshes.
            var meshBag = materials.ToDictionary(g => g.Key, g => new HashSet<string>(g.First().renderers.SelectMany(Identity)));
            var contextBag = materials.ToDictionary(g => g.Key, g => new HashSet<string>(g.SelectMany(s => Identity(s.existingName))));
            // Hidden objects (disabled variants, toggled outfits) neither receive a set first nor dilute evidence for visible ones.
            var visible = materials.Where(g => g.Any(s => s.active)).Select(g => g.Key).ToList();
            var slotBag = slots.ToDictionary(s => s, s => Identity(s.existingName));
            // Tokens shared by most of the dropped files (a common export prefix) do not tell files apart.
            var identities = textures.ToDictionary(t => t, t => Identity(t.fileName));
            if (textures.Count >= 4)
                foreach (var common in identities.Values.SelectMany(i => i).GroupBy(w => w).Where(g => g.Count() > textures.Count / 2).Select(g => g.Key).ToList())
                    foreach (var identity in identities.Values) identity.Remove(common);

            var linked = new List<TextureEntry>();
            var grouped = new List<TextureEntry>();
            var layout = new UvLayouts();
            foreach (var texture in textures)
            {
                var remembered = TextureMemory.Find(memory, texture, slots);
                if (remembered.Count > 0)
                {
                    Assign(texture, remembered[0], 1f, RememberedReason);
                    foreach (var extra in remembered.Skip(1)) linked.Add(Link(texture, extra, RememberedReason));
                    continue;
                }
                var identity = identities[texture];
                // A token that appears on many materials (a base or author name) is weak evidence for any one of them.
                var weight = identity.ToDictionary(w => w, w => {
                    bool Mentions(Material m) => Best(w, nameBag[m]) > 0 || Best(w, contextBag[m]) > 0;
                    int count = visible.Count(Mentions);
                    if (count == 0) count = materials.Count(g => Mentions(g.Key));
                    return count == 0 ? 0 : 1.0 / count; });
                double Evidence(IEnumerable<string> bag) => identity.Sum(w => weight[w] * Best(w, bag));
                string plainName = System.IO.Path.GetFileNameWithoutExtension(texture.fileName).ToLowerInvariant();
                var ranked = new List<Candidate>();
                foreach (var slot in slots)
                {
                    if (!CanAutoAssign(texture, slot)) continue;
                    double fit = RoleFit(texture.role, slot);
                    if (double.IsNaN(fit)) continue;
                    double evidence = Evidence(slotBag[slot]) * 3 + Evidence(nameBag[slot.material]) * 3 + Evidence(contextBag[slot.material]) * 1.5 + Evidence(meshBag[slot.material]);
                    if (slot.existingName.ToLowerInvariant() == plainName) evidence += 10;
                    // A variant of the texture now in the slot keeps its whole name and adds to it ("Body_BaseMap" ->
                    // "Body_BaseMap_Green"): the strongest sign short of the same name.
                    else if (VariantOf(System.IO.Path.GetFileNameWithoutExtension(texture.fileName), slot.existingName)) evidence += 8;
                    ranked.Add(new Candidate { slot = slot, evidence = evidence, score = evidence + fit + Preference(texture, slot) });
                }
                ranked = ranked.OrderByDescending(c => c.score).ToList();
                var best = ranked.FirstOrDefault();
                if (best == null && slots.Any(s => Packed(texture.role, s.role)))
                { texture.reason = $"No {texture.role} slot here: metallic maps pack metallic and smoothness in their channels. Choose a slot below if this image is packed that way."; continue; }
                if (best == null || best.evidence < 1) { texture.reason = NoClearMatchReason + " Choose a slot below."; if (best != null) Suggest(texture, best.slot); continue; }
                // Parts of one mesh that share a UV layout (colour variants of the same strands, for example) are one target:
                // a single texture set covers all of them.
                var group = ranked.Skip(1).Where(c => best.score - c.score < .5 && c.slot.property == best.slot.property && c.slot.material != best.slot.material &&
                    layout.Siblings(best.slot, c.slot)).ToList();
                // Slots currently showing the same texture are one target too: replacing it everywhere is expected.
                var rival = ranked.Skip(1).FirstOrDefault(c => !group.Contains(c) && !(best.slot.existing && c.slot.existing == best.slot.existing));
                if (rival != null && best.score - rival.score < .5)
                {
                    texture.reason = rival.slot.material == best.slot.material
                        ? $"Probably {best.slot.materialName}, but its {best.slot.description} and {rival.slot.description} slots are about as likely."
                        : $"Probably {best.slot.materialName}, but {rival.slot.materialName} is about as likely.";
                    Suggest(texture, best.slot); continue;
                }
                Assign(texture, best.slot, (float)Math.Min(.99, .9 + best.evidence / 100), "Matched " + best.slot.materialName + " / " + best.slot.description + " by name.");
                foreach (var member in group) grouped.Add(Link(texture, member.slot, "Shares its mesh and UV layout with " + best.slot.materialName + "."));
            }
            SeparateEmission(textures, slots, stats);
            RejectConflicts(textures);
            // Files that share a name stem are one exported set ("Hair_Normal", "Hair_BaseColor"): an undecided member follows
            // the material its siblings matched, when that material has a free compatible slot.
            foreach (var texture in textures.Where(t => !t.material && identities[t].Count > 0).ToList())
            {
                bool SameSet(TextureEntry other) => other != texture && identities[other].SetEquals(identities[texture]);
                var siblings = textures.Where(t => t.material && SameSet(t)).Select(t => t.material)
                    .Concat(grouped.Where(g => textures.Any(t => t.texture == g.texture && SameSet(t))).Select(g => g.material)).Distinct().ToList();
                if (siblings.Count == 0) continue;
                // Siblings on several materials must be one layout group (see above); otherwise the set's home is unclear.
                var home = slots.First(s => s.material == siblings[0]);
                if (siblings.Skip(1).Any(m => !layout.Siblings(home, slots.First(s => s.material == m)))) continue;
                var targets = siblings.Select(m => slots.Where(s => s.material == m && CanAutoAssign(texture, s) && Compatible(texture.role, s) && !textures.Concat(grouped).Any(t => t.material == s.material && t.property == s.property))
                    .OrderByDescending(s => RoleFit(texture.role, s) + Preference(texture, s)).FirstOrDefault())
                    .Where(t => t != null && RoleFit(texture.role, t) + Preference(texture, t) > -1).ToList();
                if (targets.Count > 0)
                {
                    Assign(texture, targets[0], .93f, "Same texture set as the other " + string.Join(" ", identities[texture]) + " files on " + targets[0].materialName + ".");
                    foreach (var extra in targets.Skip(1)) grouped.Add(Link(texture, extra, "Same texture set, and shares its mesh and UV layout with " + targets[0].materialName + "."));
                }
                else
                {
                    // Locked/optimized shaders only keep the features that were enabled, so the slot may simply not exist yet.
                    texture.suggestedMaterialName = siblings[0].name; texture.suggestedProperty = null;
                    texture.reason = MissingSlotReason + string.Join(", ", siblings.Select(m => m.name)) + ", which has no free " + texture.role + " slot. Enable that feature on the material (unlock its shader if it is locked), then drop again, or choose a slot below.";
                }
            }
            // Layout-group members only fill slots nothing else targets.
            foreach (var member in grouped)
                if (!textures.Concat(linked).Any(t => t.material == member.material && t.property == member.property)) linked.Add(member);
            // Propagate each confident match to other slots of the same kind that currently use the texture being replaced.
            foreach (var texture in textures.Concat(linked).Where(t => t.material && !t.applied).ToList())
            {
                var slot = slots.FirstOrDefault(s => s.material == texture.material && s.property == texture.property);
                if (slot?.existing == null) continue;
                foreach (var other in slots.Where(s => s != slot && s.existing == slot.existing && s.role == slot.role &&
                    CanAutoAssign(texture, s) &&
                    !textures.Concat(linked).Any(t => t.material == s.material && t.property == s.property)))
                    linked.Add(Link(texture, other, "Replaces " + slot.existingName + " here too, like on " + slot.materialName + "."));
            }
            textures.AddRange(linked);
        }

        private static bool VariantOf(string file, string existing)
        {
            var existingTokens = Tokens(existing, stem: false);
            if (existingTokens.Count == 0 || existingTokens.All(Generic.Contains)) return false;
            var fileTokens = Tokens(file, stem: false);
            return fileTokens.Count > existingTokens.Count && existingTokens.All(fileTokens.Contains);
        }

        private static double Preference(TextureEntry texture, TextureSlot slot)
        {
            string file = texture.fileName.ToLowerInvariant();
            double value = 0;
            if (slot.secondary && !SecondaryWords.Any(w => file.Contains(w) && (slot.property + " " + slot.description).ToLowerInvariant().Contains(w))) value -= 2.5;
            else if (Primary.Contains(slot.property)) value += .5;
            if (slot.existing) value += .3;
            if (slot.rendererKind == "effect") value -= 1.5;
            if (!slot.active) value -= 3;
            return value;
        }

        // Coarse UV coverage per mesh submesh; two submeshes of one mesh with nearly the same coverage share a layout.
        private sealed class UvLayouts
        {
            private const int Size = 32;
            private readonly Dictionary<(Mesh, int), bool[]> cache = new Dictionary<(Mesh, int), bool[]>();

            internal bool Siblings(TextureSlot a, TextureSlot b)
            {
                foreach (var (renderer, indexA) in a.parts)
                foreach (var (other, indexB) in b.parts)
                {
                    if (renderer != other) continue;
                    var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (!mesh || indexA >= mesh.subMeshCount || indexB >= mesh.subMeshCount) continue;
                    bool[] first = Grid(mesh, indexA), second = Grid(mesh, indexB);
                    int both = 0, countA = 0, countB = 0;
                    for (int i = 0; i < first.Length; i++) { if (first[i]) countA++; if (second[i]) countB++; if (first[i] && second[i]) both++; }
                    if (Math.Min(countA, countB) > 0 && both >= .8 * Math.Max(countA, countB)) return true;
                }
                return false;
            }

            // The cells each triangle covers, not just its corners: complementary halves of an atlas stay apart. A tiled
            // triangle moves into the 0–1 square as a whole, so a corner at exactly 1 is never folded back onto 0.
            private bool[] Grid(Mesh mesh, int submesh)
            {
                if (cache.TryGetValue((mesh, submesh), out var grid)) return grid;
                grid = new bool[Size * Size];
                var uv = new List<Vector2>();
                mesh.GetUVs(0, uv);
                var indices = uv.Count > 0 ? mesh.GetIndices(submesh) : Array.Empty<int>();
                var topology = mesh.GetTopology(submesh);
                int corners = topology == MeshTopology.Triangles ? 3 : topology == MeshTopology.Quads ? 4 : 1;
                for (int i = 0; i + corners <= indices.Length; i += corners)
                {
                    bool valid = true;
                    for (int c = i; c < i + corners; c++) valid &= indices[c] < uv.Count;
                    if (!valid) continue;
                    var first = uv[indices[i]];
                    if (corners == 1) { Cover(grid, first, first, first); continue; }
                    Cover(grid, first, uv[indices[i + 1]], uv[indices[i + 2]]);
                    if (corners == 4) Cover(grid, first, uv[indices[i + 2]], uv[indices[i + 3]]);
                }
                return cache[(mesh, submesh)] = grid;
            }

            private static void Cover(bool[] grid, Vector2 a, Vector2 b, Vector2 c)
            {
                var tile = new Vector2(Mathf.Floor((a.x + b.x + c.x) / 3), Mathf.Floor((a.y + b.y + c.y) / 3));
                a = (a - tile) * Size; b = (b - tile) * Size; c = (c - tile) * Size;
                int Cell(float v) => Mathf.Clamp(Mathf.FloorToInt(v), 0, Size - 1);
                // Corners always count, so triangles smaller than a cell still leave a mark.
                grid[Cell(a.y) * Size + Cell(a.x)] = grid[Cell(b.y) * Size + Cell(b.x)] = grid[Cell(c.y) * Size + Cell(c.x)] = true;
                float Side(Vector2 p, Vector2 q, Vector2 r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
                float area = Side(a, b, c);
                if (Mathf.Abs(area) < 1e-6f) return;
                for (int y = Cell(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), yEnd = Cell(Mathf.Max(a.y, Mathf.Max(b.y, c.y))); y <= yEnd; y++)
                for (int x = Cell(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), xEnd = Cell(Mathf.Max(a.x, Mathf.Max(b.x, c.x))); x <= xEnd; x++)
                {
                    var center = new Vector2(x + .5f, y + .5f);
                    float u = Side(b, c, center) / area, v = Side(c, a, center) / area, w = Side(a, b, center) / area;
                    if (u > 0 && v > 0 && w > 0) grid[y * Size + x] = true;
                }
            }
        }

        private static TextureEntry Link(TextureEntry source, TextureSlot slot, string reason)
        {
            var entry = new TextureEntry { texture = source.texture, sourceKey = source.sourceKey, fileName = source.fileName, role = source.role };
            Assign(entry, slot, source.confidence > 0 ? source.confidence : .95f, reason);
            return entry;
        }

        private static void Suggest(TextureEntry texture, TextureSlot slot) { texture.suggestedMaterialName = slot.materialName; texture.suggestedProperty = slot.property; }

        private static void Assign(TextureEntry texture, TextureSlot slot, float confidence, string reason)
        {
            texture.material = slot.material; texture.property = slot.property; texture.confidence = confidence;
            texture.suggestedMaterialName = slot.materialName; texture.suggestedProperty = slot.property; texture.reason = reason;
        }

        // Two colour images aimed at one colour slot, where exactly one is mostly black with bright details and the
        // material has a free emission slot: the dark one is the emission map (e.g. Eyes_BaseColor vs Eyes_BaseColor3).
        private static void SeparateEmission(List<TextureEntry> textures, List<TextureSlot> slots, Dictionary<Texture2D, TextureStats> stats)
        {
            foreach (var group in textures.Where(t => t.material && !t.applied && t.role == "color").GroupBy(t => (t.material, t.property)).Where(g => g.Count() == 2).ToList())
            {
                var emissive = group.Where(t => stats.TryGetValue(t.texture, out var s) && s.LooksEmissive).ToList();
                if (emissive.Count != 1) continue;
                var other = group.First(t => t != emissive[0]);
                if (!stats.TryGetValue(other.texture, out var otherStats) || otherStats.black > .3f) continue;
                var target = slots.Where(s => s.material == group.Key.material && s.role == "emission" && CanAutoAssign(emissive[0], s) && !textures.Any(t => t.material == s.material && t.property == s.property))
                    .OrderByDescending(s => Preference(emissive[0], s)).FirstOrDefault();
                if (target == null) continue;
                Assign(emissive[0], target, .92f, "Mostly black with bright details: matched as " + target.materialName + " / " + target.description + ".");
            }
        }

        internal static void RejectConflicts(List<TextureEntry> textures)
        {
            foreach (var conflict in textures.Where(t => t.material && !t.applied).GroupBy(t => (t.material, t.property))
                .Where(g => g.Count() > 1 && !MaterialSurfaceMaps.CanCombine(g.Key.property, g.Select(e => e.role).ToArray())))
                foreach (var entry in conflict) { entry.material = null; entry.property = null; entry.confidence = 0;
                    entry.reason = "Several images point at " + entry.suggestedMaterialName + " / " + entry.suggestedProperty + ". Choose the target below."; }
        }
    }
}
