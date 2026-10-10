using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.MyAvatar.Editor
{
    internal enum ShaderFamily { Unknown, Poiyomi, LilToon, ToonStandard, Standard }
    internal enum SurfaceMode { Opaque, Cutout, Fade, Transparent }

    internal struct SavedTexture
    {
        public Texture Texture;
        public Vector2 Scale, Offset;
        public static readonly SavedTexture None = new SavedTexture { Scale = Vector2.one };
    }

    /// <summary>
    /// What a material keeps of its shader once that shader is gone: Unity saves every property value, tag, keyword and the
    /// render queue with the material, and the shader reference (its GUID) even when nothing in the project answers to it.
    /// </summary>
    internal sealed class SavedMaterial
    {
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>(StringComparer.Ordinal);
        public readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        public readonly Dictionary<string, SavedTexture> Textures = new Dictionary<string, SavedTexture>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Tags = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<string> Keywords = new List<string>();
        public int Queue = -1;
        /// <summary>The GUID the material's shader reference names, resolved or not.</summary>
        public string ShaderGuid;

        public bool Has(string name) => Floats.ContainsKey(name) || Colors.ContainsKey(name) || Textures.ContainsKey(name);
        public float Float(string name, float fallback = 0) => Floats.TryGetValue(name, out float value) ? value : fallback;
        public bool On(string name) => Float(name) > .5f;
        public Color Color(string name, Color fallback) => Colors.TryGetValue(name, out var value) ? value : fallback;
        public string Tag(string name) => Tags.TryGetValue(name, out string value) ? value ?? "" : "";

        public SavedTexture Texture(string name)
        {
            Textures.TryGetValue(name, out var saved);
            if (!Textures.ContainsKey(name)) saved = SavedTexture.None;
            if (saved.Texture) return saved;
            // A locked Poiyomi material keeps aside the textures its locked shader did not need.
            string guid = Tag("_stripped_tex_" + name);
            if (!string.IsNullOrEmpty(guid)) saved.Texture = AssetDatabase.LoadAssetAtPath<Texture>(AssetDatabase.GUIDToAssetPath(guid));
            return saved;
        }

        internal static SavedMaterial Read(Material material)
        {
            var saved = new SavedMaterial();
            using (var serialized = new SerializedObject(material))
            {
                var properties = serialized.FindProperty("m_SavedProperties");
                Pairs(properties?.FindPropertyRelative("m_Floats"), (name, value) => saved.Floats[name] = value.floatValue);
                Pairs(properties?.FindPropertyRelative("m_Ints"), (name, value) => saved.Floats[name] = value.intValue);
                Pairs(properties?.FindPropertyRelative("m_Colors"), (name, value) => saved.Colors[name] = value.colorValue);
                Pairs(properties?.FindPropertyRelative("m_TexEnvs"), (name, value) => saved.Textures[name] = new SavedTexture
                {
                    Texture = value.FindPropertyRelative("m_Texture")?.objectReferenceValue as Texture,
                    Scale = value.FindPropertyRelative("m_Scale")?.vector2Value ?? Vector2.one,
                    Offset = value.FindPropertyRelative("m_Offset")?.vector2Value ?? Vector2.zero,
                });
                Pairs(serialized.FindProperty("stringTagMap"), (name, value) => saved.Tags[name] = value.stringValue);
                foreach (string list in new[] { "m_ValidKeywords", "m_InvalidKeywords" })
                {
                    var array = serialized.FindProperty(list);
                    for (int i = 0; array != null && array.isArray && i < array.arraySize; i++) saved.Keywords.Add(array.GetArrayElementAtIndex(i).stringValue);
                }
                saved.Queue = serialized.FindProperty("m_CustomRenderQueue")?.intValue ?? -1;
                // A missing shader still has its place in the asset database: the reference keeps its GUID.
                var shader = serialized.FindProperty("m_Shader");
                if (shader != null && shader.objectReferenceInstanceIDValue != 0 &&
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(shader.objectReferenceInstanceIDValue, out string guid, out long _))
                    saved.ShaderGuid = guid;
            }
            if (string.IsNullOrEmpty(saved.ShaderGuid)) saved.ShaderGuid = FileShaderGuid(material);
            return saved;
        }

        private static void Pairs(SerializedProperty array, Action<string, SerializedProperty> read)
        {
            for (int i = 0; array != null && array.isArray && i < array.arraySize; i++)
            {
                var pair = array.GetArrayElementAtIndex(i);
                var first = pair.FindPropertyRelative("first");
                var second = pair.FindPropertyRelative("second");
                if (first != null && second != null && !string.IsNullOrEmpty(first.stringValue)) read(first.stringValue, second);
            }
        }

        private static readonly Regex ShaderLine = new Regex(@"m_Shader:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-f]{32})", RegexOptions.Compiled);

        /// <summary>The shader GUID written in the material's .mat file (the project serializes assets as text), or null.</summary>
        internal static string FileShaderGuid(Material material)
        {
            string path = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) return null;
            string file = FileUtil.GetPhysicalPath(path);
            if (!File.Exists(file)) return null;
            try
            {
                var match = ShaderLine.Match(File.ReadAllText(file));
                return match.Success ? match.Groups[1].Value : null;
            }
            catch (IOException) { return null; }
        }
    }

    /// <summary>The shaders a fix may use: those in the project that load and compile. Tests hide some to try other paths.</summary>
    internal sealed class ShaderCatalog
    {
        private readonly Func<string, bool> hidden;
        private string[] names;
        private readonly Dictionary<Shader, HashSet<string>> properties = new Dictionary<Shader, HashSet<string>>();

        internal ShaderCatalog(Func<string, bool> hidden = null) { this.hidden = hidden; }

        internal Shader Find(string name) => string.IsNullOrEmpty(name) || Hidden(name) ? null : Usable(Shader.Find(name));

        internal Shader Load(string guid)
        {
            string path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            var shader = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Shader>(path);
            return shader && !Hidden(shader.name) ? Usable(shader) : null;
        }

        internal IEnumerable<string> Names(Func<string, bool> match)
        {
            names ??= ShaderUtil.GetAllShaderInfo().Where(i => i.supported && !i.hasErrors).Select(i => i.name).ToArray();
            return names.Where(n => match(n) && !Hidden(n));
        }

        internal HashSet<string> Properties(Shader shader)
        {
            if (properties.TryGetValue(shader, out var known)) return known;
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < shader.GetPropertyCount(); i++) set.Add(shader.GetPropertyName(i));
            properties[shader] = set;
            return set;
        }

        private bool Hidden(string name) => hidden != null && hidden(name);

        internal static Shader Usable(Shader shader) =>
            shader && shader.name != MaterialShaders.ErrorShader && shader.isSupported && !ShaderUtil.ShaderHasError(shader) ? shader : null;
    }

    /// <summary>The part of a material every shader can show: base texture and color, normal map, emission, culling and blending.</summary>
    internal sealed class Surface
    {
        public SavedTexture Main = SavedTexture.None, Normal = SavedTexture.None, EmissionMap = SavedTexture.None;
        public Color Color = UnityEngine.Color.white, Emission = UnityEngine.Color.black;
        public float NormalScale = 1, EmissionStrength = 1, Cutoff = .5f, Cull = 2;
        /// <summary>How much the emission takes the base color (0 its own color, 1 tinted by the base texture).</summary>
        public float EmissionTint;
        public bool Emits;
        public SurfaceMode Mode;
        // Settings lilToon and Poiyomi share under the same names: Poiyomi's Multilayer Math shading, lilToon-style rim
        // light and backlight are lilToon's own.
        public bool Shadows, FlatLit, Rim, Backlight;
        /// <summary>What the old shader drew that no conversion carries over (outline, matcap...).</summary>
        public readonly List<string> Features = new List<string>();
    }

    /// <summary>
    /// Shader families avatars are made with, how to recognize a material made for one from what it saved, and how to move it
    /// to another shader: the same family keeps every saved value; another family gets the surface everyone shares.
    /// </summary>
    internal static class MaterialShaders
    {
        internal const string ErrorShader = "Hidden/InternalErrorShader";
        internal const string PoiyomiToon = ".poiyomi/Poiyomi Toon";
        internal const string LilToonPackage = "jp.lilxyzw.liltoon", PoiyomiPackage = "com.poiyomi.toon";
        /// <summary>The GUID shader references to Unity's built-in shaders (Standard among them) carry.</summary>
        internal const string BuiltinShaders = "0000000000000000f000000000000000";

        // lilToon's shader files keep their GUIDs across versions: a material names its variant even with lilToon missing.
        internal static readonly Dictionary<string, string> LilToonShaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["df12117ecd77c31469c224178886498e"] = "lilToon",
            ["85d6126cae43b6847aff4b13f4adb8ec"] = "Hidden/lilToonCutout",
            ["165365ab7100a044ca85fc8c33548a62"] = "Hidden/lilToonTransparent",
            ["b269573b9937b8340b3e9e191a3ba5a8"] = "Hidden/lilToonOnePassTransparent",
            ["6a77405f7dfdc1447af58854c7f43f39"] = "Hidden/lilToonTwoPassTransparent",
            ["efa77a80ca0344749b4f19fdd5891cbe"] = "Hidden/lilToonOutline",
            ["3b4aa19949601f046a20ca8bdaee929f"] = "Hidden/lilToonCutoutOutline",
            ["3c79b10c7e0b2784aaa4c2f8dd17d55e"] = "Hidden/lilToonTransparentOutline",
            ["7171688840c632447b22ec14e2bdef7e"] = "Hidden/lilToonOnePassTransparentOutline",
            ["9cf054060007d784394b8b0bb703e441"] = "Hidden/lilToonTwoPassTransparentOutline",
            ["381af8ba8e1740a41b9768ccfb0416c2"] = "Hidden/lilToonLite",
            ["b957dce3d03ff5445ac989f8de643c7f"] = "Hidden/lilToonLiteCutout",
            ["0e3ece1bd59542743bccadb21f68318e"] = "Hidden/lilToonLiteTransparent",
            ["34c2907eba944ed45a43970e0c11bcfd"] = "Hidden/lilToonLiteOnePassTransparent",
            ["82226adb1a0b8c4418f574cfdcf523da"] = "Hidden/lilToonLiteTwoPassTransparent",
            ["583a88005abb81a4ebbce757b4851a0d"] = "Hidden/lilToonLiteOutline",
            ["8cf5267d397b04846856f6d3d9561da0"] = "Hidden/lilToonLiteCutoutOutline",
            ["1c12a37046f07ac4486881deaf0187ea"] = "Hidden/lilToonLiteTransparentOutline",
            ["701268c07d37f5441b25b2cb99fae4b3"] = "Hidden/lilToonLiteOnePassTransparentOutline",
            ["62df797f407281640a224388953448cc"] = "Hidden/lilToonLiteTwoPassTransparentOutline",
            ["55706696b2bdb5d4d8541b89e17085c8"] = "Hidden/lilToonFur",
            ["544c75f56e9af8048b29a6ace5f52091"] = "Hidden/lilToonFurCutout",
            ["54bc8b41278802d4a81b27fe402994e2"] = "Hidden/lilToonFurTwoPass",
            ["a8d94439709469942bc7dcc9156ba110"] = "Hidden/lilToonGem",
            ["dce3f3e248acc7b4daeda00daf616b4d"] = "Hidden/lilToonRefraction",
            ["3fb94a39b2685ee4d9817dcaf6542d99"] = "Hidden/lilToonRefractionBlur",
            ["3eef4aee6ba0de047b0d40409ea2891c"] = "Hidden/lilToonTessellation",
            ["bbfffd5515b843c41a85067191cbf687"] = "Hidden/lilToonTessellationCutout",
            ["5ba517885727277409feada18effa4a6"] = "Hidden/lilToonTessellationCutoutOutline",
            ["c6d605ee23b18fc46903f38c67db701f"] = "Hidden/lilToonTessellationOutline",
            ["90f83c35b0769a748abba5d0880f36d5"] = "Hidden/lilToonTessellationOnePassTransparent",
            ["67ed0252d63362a4ab23707a720508b7"] = "Hidden/lilToonTessellationOnePassTransparentOutline",
            ["afa1a194f5a2fd243bda3a17bca1b36e"] = "Hidden/lilToonTessellationTransparent",
            ["9b0c2630b12933248922527d4507cfa9"] = "Hidden/lilToonTessellationTransparentOutline",
            ["7e398ea50f9b70045b1774e05b46a39f"] = "Hidden/lilToonTessellationTwoPassTransparent",
            ["7e61dbad981ad4f43a03722155db1c6a"] = "Hidden/lilToonTessellationTwoPassTransparentOutline",
            ["9294844b15dca184d914a632279b24e1"] = "_lil/lilToonMulti",
            ["51b2dee0ab07bd84d8147601ff89e511"] = "Hidden/lilToonMultiOutline",
            ["1e50f1bc4d1b0e34cbf16b82589f6407"] = "Hidden/lilToonMultiFur",
            ["69f861c14129e724096c0955f8079012"] = "Hidden/lilToonMultiGem",
            ["d7af54cdd86902d41b8c240e06b93009"] = "Hidden/lilToonMultiRefraction",
            ["00795bf598b44dc4e9bd363348e77085"] = "_lil/[Optional] lilToonFakeShadow",
            ["fba17785d6b2c594ab6c0303c834da65"] = "_lil/[Optional] lilToonOutlineOnly",
            ["3b3957e6c393b114bab6f835b4ed8f5d"] = "_lil/[Optional] lilToonOutlineOnlyCutout",
            ["0c762f24b85918a49812fc5690619178"] = "_lil/[Optional] lilToonOutlineOnlyTransparent",
            ["33aad051c4a3a844a8f9330addb86a97"] = "_lil/[Optional] lilToonFurOnlyTransparent",
            ["7ec9f85eb7ee04943adfe19c2ba5901f"] = "_lil/[Optional] lilToonFurOnlyCutout",
            ["f8d9dfac6dbfaaf4c9c3aaf4bd8c955f"] = "_lil/[Optional] lilToonFurOnlyTwoPass",
            ["94274b8ef5d3af842b9427384cba3a8f"] = "_lil/[Optional] lilToonOverlay",
            ["33e950d038b8dfd4f824f3985c2abfb7"] = "_lil/[Optional] lilToonOverlayOnePass",
            ["d28e4b78ba8368e49a44f86c0291df58"] = "_lil/[Optional] lilToonLiteOverlay",
            ["dc9ded9f9d6f16c4e92cbb8f4269ae31"] = "_lil/[Optional] lilToonLiteOverlayOnePass",
        };

        // lilToon layers and effects no other shader draws the same way.
        private static readonly (string toggle, string label)[] LilToonFeatures =
        {
            ("_UseMain2ndTex", "2nd texture layer"), ("_UseMain3rdTex", "3rd texture layer"), ("_UseBump2ndMap", "2nd normal map"),
            ("_UseAnisotropy", "anisotropy"), ("_UseRimShade", "rim shade"), ("_UseReflection", "reflections"), ("_UseMatCap", "matcap"),
            ("_UseMatCap2nd", "2nd matcap"), ("_UseGlitter", "glitter"), ("_UseEmission2nd", "2nd emission"), ("_UseParallax", "parallax"),
            ("_UseAudioLink", "AudioLink"),
        };

        private static readonly (string toggle, string label)[] PoiyomiFeatures =
        {
            ("_EnableOutlines", "outline"), ("_MatcapEnable", "matcap"), ("_Matcap2Enable", "2nd matcap"), ("_EnableRim2Lighting", "2nd rim light"),
            ("_DecalEnabled", "decals"), ("_DetailEnabled", "detail textures"), ("_MainColorAdjustToggle", "color adjustments"),
            ("_EnableDissolve", "dissolve"), ("_GlitterEnable", "glitter"), ("_EnableEmission1", "2nd emission"), ("_MochieBRDF", "reflections and specular"),
            ("_StylizedSpecular", "stylized specular"), ("_EnableFlipbook", "flipbook"), ("_PoiParallax", "parallax"), ("_BackFaceEnabled", "back face"),
            ("_EnableTouchGlow", "touch glow"), ("_EnablePathing", "pathing"), ("_EnableAudioLink", "AudioLink"),
        };

        // ---- What a material was made for ----------------------------------------------------------------------

        internal static ShaderFamily Detect(SavedMaterial saved)
        {
            if (saved.ShaderGuid != null && LilToonShaders.ContainsKey(saved.ShaderGuid)) return ShaderFamily.LilToon;
            if (saved.Tag("OriginalShader").IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0) return ShaderFamily.Poiyomi;
            if (saved.Has("shader_master_label") && (saved.Has("_LightingMode") || saved.Has("_ShaderOptimizerEnabled"))) return ShaderFamily.Poiyomi;
            if (saved.Has("_lilToonVersion")) return ShaderFamily.LilToon;
            if (saved.Has("_ShadowBoost") && saved.Has("_LimitBrightness")) return ShaderFamily.ToonStandard;
            // Most materials start on Standard (a model's import) and keep its values: they only say Standard when the missing
            // shader was one of Unity's own, never a project shader.
            if ((saved.ShaderGuid == null || saved.ShaderGuid == BuiltinShaders) &&
                saved.Has("_GlossyReflections") && saved.Has("_SpecularHighlights") && saved.Has("_UVSec")) return ShaderFamily.Standard;
            return ShaderFamily.Unknown;
        }

        internal static ShaderFamily FamilyOf(Shader shader)
        {
            string name = shader ? shader.name : "";
            if (name.StartsWith(".poiyomi/", StringComparison.Ordinal) || name.StartsWith("Hidden/Locked/.poiyomi/", StringComparison.Ordinal)) return ShaderFamily.Poiyomi;
            if (name.IndexOf("lilToon", StringComparison.Ordinal) >= 0) return ShaderFamily.LilToon;
            if (name == Orbiters.Toolkit.Editor.MaterialRepair.ToonShader) return ShaderFamily.ToonStandard;
            if (name == "Standard" || name == "Standard (Specular setup)") return ShaderFamily.Standard;
            return ShaderFamily.Unknown;
        }

        internal static string Name(ShaderFamily family) => family switch
        {
            ShaderFamily.Poiyomi => "Poiyomi Toon",
            ShaderFamily.LilToon => "lilToon",
            ShaderFamily.ToonStandard => "VRChat Toon Standard",
            ShaderFamily.Standard => "Standard",
            _ => "another shader",
        };

        /// <summary>The shader's name as people know it: "lilToon Transparent Outline", "Poiyomi Toon", "VRChat Toon Standard".</summary>
        internal static string Pretty(string shader)
        {
            if (string.IsNullOrEmpty(shader)) return "another shader";
            if (shader == Orbiters.Toolkit.Editor.MaterialRepair.ToonShader) return "VRChat Toon Standard";
            string name = shader.Replace("Hidden/Locked/", "").Replace("Hidden/", "").Replace("_lil/[Optional] ", "").Replace("_lil/", "");
            if (name.StartsWith(".poiyomi/", StringComparison.Ordinal)) name = name.Substring(".poiyomi/".Length);
            if (name.StartsWith("Old Versions/", StringComparison.Ordinal))
            {
                var parts = name.Split('/');
                name = parts[parts.Length - 1] + " " + parts[1];
            }
            if (name.StartsWith("lilToon", StringComparison.Ordinal) && name.Length > 7 && char.IsUpper(name[7]))
                name = "lilToon " + Regex.Replace(name.Substring(7), "(?<=[a-z])(?=[A-Z])", " ");
            return name;
        }

        internal static SurfaceMode Mode(SavedMaterial saved, ShaderFamily family, string lilToon)
        {
            if (lilToon != null)
            {
                if (lilToon.IndexOf("Multi", StringComparison.Ordinal) >= 0)
                {
                    int multi = (int)saved.Float("_TransparentMode");
                    return multi == 0 ? SurfaceMode.Opaque : multi == 1 || multi == 5 ? SurfaceMode.Cutout : SurfaceMode.Transparent;
                }
                if (lilToon.IndexOf("Cutout", StringComparison.Ordinal) >= 0) return SurfaceMode.Cutout;
                if (Regex.IsMatch(lilToon, "Transparent|Fur|Gem|Refraction|Overlay|FakeShadow")) return SurfaceMode.Transparent;
                return SurfaceMode.Opaque;
            }
            if ((family == ShaderFamily.Poiyomi || family == ShaderFamily.Standard) && saved.Has("_Mode"))
            {
                int mode = (int)saved.Float("_Mode");
                if (family == ShaderFamily.Standard) return mode == 1 ? SurfaceMode.Cutout : mode == 2 ? SurfaceMode.Fade : mode == 3 ? SurfaceMode.Transparent : SurfaceMode.Opaque;
                return mode == 0 ? SurfaceMode.Opaque : mode == 1 || mode == 9 ? SurfaceMode.Cutout : mode == 2 ? SurfaceMode.Fade : SurfaceMode.Transparent;
            }
            if (family == ShaderFamily.ToonStandard) return SurfaceMode.Opaque;
            // What any shader leaves behind: VRChat's fallback tag, the render type and the render queue.
            string fallback = saved.Tag("VRCFallback"), type = saved.Tag("RenderType");
            if (fallback.Contains("Fade")) return SurfaceMode.Fade;
            if (fallback.Contains("Transparent") || type == "Transparent" || saved.Queue >= 2900) return SurfaceMode.Transparent;
            if (fallback.Contains("Cutout") || type == "TransparentCutout" || saved.Queue >= 2450) return SurfaceMode.Cutout;
            return SurfaceMode.Opaque;
        }

        /// <summary>The surface a conversion carries over, and what the old shader drew beyond it.</summary>
        internal static Surface Read(SavedMaterial saved, ShaderFamily family, string lilToon, SurfaceMode mode)
        {
            var s = new Surface { Mode = mode };
            s.Main = First(saved, "_MainTex", "_BaseMap", "_BaseColorMap", "_MainTexture", "_Albedo");
            s.Color = FirstColor(saved, Color.white, "_Color", "_BaseColor", "_MainColor");
            s.Cull = FirstFloat(saved, 2, "_Cull", "_Culling", "_CullMode");
            s.Cutoff = FirstFloat(saved, .5f, "_Cutoff", "_AlphaCutoff");
            switch (family)
            {
                case ShaderFamily.LilToon:
                    if (saved.On("_UseBumpMap")) { s.Normal = saved.Texture("_BumpMap"); s.NormalScale = saved.Float("_BumpScale", 1); }
                    if (saved.On("_UseEmission"))
                    {
                        // lilToon adds color × map × alpha × blend: the alpha is the strength.
                        var color = saved.Color("_EmissionColor", Color.white);
                        s.Emits = true; s.EmissionMap = saved.Texture("_EmissionMap");
                        s.Emission = new Color(color.r, color.g, color.b, 1); s.EmissionStrength = color.a * saved.Float("_EmissionBlend", 1);
                        // Eyes glowing in their own colors: without it a white mask glows white over them.
                        s.EmissionTint = saved.Float("_EmissionMainStrength");
                    }
                    s.Shadows = saved.On("_UseShadow"); s.FlatLit = !s.Shadows;
                    s.Rim = saved.On("_UseRim"); s.Backlight = saved.On("_UseBacklight");
                    foreach (var feature in LilToonFeatures) if (saved.On(feature.toggle)) s.Features.Add(feature.label);
                    if (lilToon != null)
                    {
                        if (lilToon.Contains("Outline")) s.Features.Add("outline");
                        if (lilToon.Contains("Fur")) s.Features.Add("fur");
                        if (lilToon.Contains("Gem")) s.Features.Add("gem effect");
                        if (lilToon.Contains("Refraction")) s.Features.Add("refraction");
                        if (lilToon.Contains("Tessellation")) s.Features.Add("tessellation");
                    }
                    if (saved.Float("_AlphaMaskMode") != 0 && saved.Texture("_AlphaMask").Texture) s.Features.Add("alpha mask");
                    break;
                case ShaderFamily.Poiyomi:
                    s.Normal = saved.Texture("_BumpMap"); s.NormalScale = saved.Float("_BumpScale", 1);
                    if (saved.On("_EnableEmission"))
                    {
                        var color = saved.Color("_EmissionColor", Color.white);
                        s.Emits = true; s.EmissionMap = saved.Texture("_EmissionMap");
                        s.Emission = new Color(color.r, color.g, color.b, 1); s.EmissionStrength = saved.Float("_EmissionStrength", 1);
                        s.EmissionTint = saved.Float("_EmissionBaseColorAsMap");
                    }
                    int lighting = (int)saved.Float("_LightingMode", 5);
                    s.Shadows = lighting == 1; s.FlatLit = lighting == 5;
                    if (!s.Shadows && !s.FlatLit) s.Features.Add("Poiyomi's shading");
                    s.Rim = saved.On("_EnableRimLighting") && (int)saved.Float("_RimStyle") == 2;
                    if (saved.On("_EnableRimLighting") && !s.Rim) s.Features.Add("rim light");
                    s.Backlight = saved.On("_BacklightEnabled");
                    foreach (var feature in PoiyomiFeatures) if (saved.On(feature.toggle)) s.Features.Add(feature.label);
                    int blend = (int)saved.Float("_Mode");
                    if (blend >= 4 && blend <= 7) s.Features.Add("additive or multiplicative blending");
                    break;
                case ShaderFamily.ToonStandard:
                    s.Normal = saved.Texture("_BumpMap"); s.NormalScale = saved.Float("_BumpScale", 1);
                    var glow = saved.Color("_EmissionColor", Color.black);
                    s.Emits = glow.maxColorComponent > 0; s.EmissionMap = saved.Texture("_EmissionMap");
                    s.Emission = new Color(glow.r, glow.g, glow.b, 1); s.EmissionStrength = saved.Float("_EmissionStrength", 1);
                    if (saved.Texture("_Matcap").Texture) s.Features.Add("matcap");
                    if (saved.Texture("_MetallicMap").Texture || saved.Float("_MetallicStrength") > 0) s.Features.Add("metallic and gloss");
                    break;
                default:
                    s.Normal = First(saved, "_BumpMap", "_NormalMap", "_NormalTex"); s.NormalScale = FirstFloat(saved, 1, "_BumpScale", "_NormalScale");
                    var emission = FirstColor(saved, Color.black, "_EmissionColor", "_EmissiveColor");
                    // Standard's emission is on with its keyword; elsewhere a non-black emission color is the sign.
                    s.Emits = emission.maxColorComponent > 0 && (family != ShaderFamily.Standard || saved.Keywords.Contains("_EMISSION"));
                    s.EmissionMap = First(saved, "_EmissionMap", "_EmissiveMap", "_EmissionTex");
                    s.Emission = new Color(emission.r, emission.g, emission.b, 1);
                    if (saved.Texture("_MetallicGlossMap").Texture || saved.Float("_Metallic") > 0) s.Features.Add("metallic and smoothness");
                    break;
            }
            return s;
        }

        private static SavedTexture First(SavedMaterial saved, params string[] names)
        {
            foreach (string name in names) { var texture = saved.Texture(name); if (texture.Texture) return texture; }
            return SavedTexture.None;
        }

        private static Color FirstColor(SavedMaterial saved, Color fallback, params string[] names)
        {
            foreach (string name in names) if (saved.Colors.TryGetValue(name, out var color)) return color;
            return fallback;
        }

        private static float FirstFloat(SavedMaterial saved, float fallback, params string[] names)
        {
            foreach (string name in names) if (saved.Floats.TryGetValue(name, out float value)) return value;
            return fallback;
        }

        // ---- Which shader to use -------------------------------------------------------------------------------

        /// <summary>The shader a locked Poiyomi material was generated from, when it is in the project.</summary>
        internal static Shader PoiyomiOriginal(SavedMaterial saved, ShaderCatalog catalog)
        {
            var shader = catalog.Load(saved.Tag("OriginalShaderGUID"));
            string name = saved.Tag("OriginalShader");
            if (!shader) shader = catalog.Find(name);
            // Older versions moved: ".poiyomi/Poiyomi 8.1/Poiyomi Toon" is ".poiyomi/Old Versions/8.1/Poiyomi Toon" today.
            var version = Regex.Match(name, @"(\d+\.\d+)/([^/]+)$");
            if (!shader && version.Success) shader = catalog.Find(".poiyomi/Old Versions/" + version.Groups[1].Value + "/" + version.Groups[2].Value);
            return shader;
        }

        /// <summary>
        /// The current Poiyomi shader whose properties match most of the material's (Two Pass, Grab Pass, Lil Fur and World
        /// variants have their own); Poiyomi Toon on a tie.
        /// </summary>
        internal static Shader BestPoiyomi(SavedMaterial saved, ShaderCatalog catalog)
        {
            bool pro = saved.Tag("OriginalShader").IndexOf("Pro", StringComparison.OrdinalIgnoreCase) >= 0;
            Shader best = null; int score = -1;
            foreach (string name in catalog.Names(n => n.StartsWith(".poiyomi/", StringComparison.Ordinal) &&
                         n.IndexOf("/Old Versions/", StringComparison.Ordinal) < 0 && n.IndexOf("/Extras/", StringComparison.Ordinal) < 0 &&
                         (pro || n.IndexOf("Poiyomi Pro", StringComparison.OrdinalIgnoreCase) < 0)))
            {
                var shader = catalog.Find(name);
                if (!shader) continue;
                int matches = catalog.Properties(shader).Count(saved.Has);
                if (matches > score || (matches == score && name == PoiyomiToon)) { best = shader; score = matches; }
            }
            return best;
        }

        internal static Shader LilToonFor(SurfaceMode mode, bool outline, ShaderCatalog catalog)
        {
            string name = mode == SurfaceMode.Cutout ? "Hidden/lilToonCutout" : mode == SurfaceMode.Opaque ? "Hidden/lilToon" : "Hidden/lilToonTransparent";
            var shader = outline ? catalog.Find(name + "Outline") : null;
            return shader ? shader : catalog.Find(mode == SurfaceMode.Opaque ? "lilToon" : name);
        }

        /// <summary>lilToon shaders that only add to another material (fake shadows, outline or fur alone, overlays).</summary>
        internal static bool Additive(string lilToon) => lilToon != null && Regex.IsMatch(lilToon, "FakeShadow|OutlineOnly|FurOnly|Overlay");

        /// <summary>Picks the shader for <paramref name="entry"/> and describes what moving to it keeps.</summary>
        internal static void Plan(BrokenMaterial entry, SavedMaterial saved, ShaderCatalog catalog)
        {
            var family = entry.Family = Detect(saved);
            string lilToon = saved.ShaderGuid != null && LilToonShaders.TryGetValue(saved.ShaderGuid, out string known) ? known : null;
            entry.Locked = family == ShaderFamily.Poiyomi && saved.On("_ShaderOptimizerEnabled");
            entry.MadeFor = family == ShaderFamily.LilToon ? (lilToon != null ? Pretty(lilToon) : "lilToon")
                : family == ShaderFamily.Poiyomi ? (string.IsNullOrEmpty(saved.Tag("OriginalShader")) ? "Poiyomi" : Pretty(saved.Tag("OriginalShader")))
                : family == ShaderFamily.Unknown ? null : Name(family);
            var mode = Mode(saved, family, lilToon);
            entry.Surface = Read(saved, family, lilToon, mode);
            entry.Installable = family == ShaderFamily.LilToon && !catalog.Find("lilToon") ? LilToonPackage
                : family == ShaderFamily.Poiyomi && !catalog.Names(n => n.StartsWith(".poiyomi/", StringComparison.Ordinal)).Any() ? PoiyomiPackage : null;

            // 1. The very shader it was made for: a locked Poiyomi material's original, or lilToon's variant by its GUID.
            Shader same = family == ShaderFamily.Poiyomi ? PoiyomiOriginal(saved, catalog) : lilToon != null ? catalog.Find(lilToon) : null;
            // 2. Its family in another version or variant.
            if (!same) same = family switch
            {
                ShaderFamily.Poiyomi => BestPoiyomi(saved, catalog),
                ShaderFamily.LilToon => Additive(lilToon) ? null : LilToonFor(mode, lilToon != null && lilToon.Contains("Outline"), catalog),
                ShaderFamily.ToonStandard => catalog.Find(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader),
                ShaderFamily.Standard => catalog.Find("Standard"),
                _ => null,
            };
            if (same)
            {
                entry.Target = same; entry.Rebind = true;
                if (family == ShaderFamily.Poiyomi && saved.Tag("OriginalShader").IndexOf("Poiyomi Pro", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    same.name.IndexOf("Poiyomi Pro", StringComparison.OrdinalIgnoreCase) < 0)
                    entry.Lost.Add("Poiyomi Pro features");
                return;
            }
            if (Additive(lilToon))
            {
                entry.Reason = Pretty(lilToon) + " only adds to other materials: install lilToon to use it.";
                return;
            }
            // 3. Another family: Poiyomi Toon, else lilToon, else VRChat's Toon Standard (opaque only), else Standard.
            var target = catalog.Find(PoiyomiToon);
            if (!target) target = LilToonFor(mode, false, catalog);
            if (!target && mode == SurfaceMode.Opaque) target = catalog.Find(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader);
            if (!target) target = catalog.Find("Standard");
            if (!target) { entry.Reason = "No shader to switch it to: Standard is missing too."; return; }
            entry.Target = target;
            Describe(entry, family, FamilyOf(target));
        }

        // What a conversion keeps and what it leaves behind, in the words the UI shows.
        private static void Describe(BrokenMaterial entry, ShaderFamily from, ShaderFamily to)
        {
            var s = entry.Surface;
            if (s.Main.Texture) entry.Kept.Add("texture");
            if (s.Color != Color.white) entry.Kept.Add("color");
            if (s.Normal.Texture) entry.Kept.Add("normal map");
            if (s.Emits) entry.Kept.Add("emission");
            bool shared = SharesLilToonSettings(from, to);
            foreach (var (on, label) in new[] { (s.Shadows, "shadow colors"), (s.Rim, "rim light"), (s.Backlight, "backlight") })
                if (on) (shared ? entry.Kept : entry.Lost).Add(label);
            entry.Lost.AddRange(s.Features);
            if (to == ShaderFamily.Standard && s.Cull != 2) entry.Lost.Add(s.Cull == 0 ? "both sides" : "front-face culling");
        }

        private static bool SharesLilToonSettings(ShaderFamily from, ShaderFamily to) =>
            (from == ShaderFamily.LilToon && to == ShaderFamily.Poiyomi) || (from == ShaderFamily.Poiyomi && to == ShaderFamily.LilToon);

        // ---- Moving a material to its new shader -----------------------------------------------------------------

        /// <summary>Moves <paramref name="material"/> to the planned shader. Record it for Undo first.</summary>
        internal static void Convert(Material material, BrokenMaterial entry, SavedMaterial saved)
        {
            if (entry.Rebind) Rebind(material, entry, saved);
            else Remap(material, entry, saved);
        }

        private static bool LockTag(string tag) => tag == "OriginalShader" || tag == "OriginalShaderGUID" || tag == "AllLockedGUIDS" ||
                                                   tag.StartsWith("_stripped_tex_", StringComparison.Ordinal);

        // The same family: every saved value stays as it is. A locked Poiyomi material is unlocked the way Poiyomi does it
        // (Poiyomi locks it again when the avatar is uploaded).
        private static void Rebind(Material material, BrokenMaterial entry, SavedMaterial saved)
        {
            var shader = entry.Target;
            material.shader = shader;
            Restore(material, saved);
            foreach (var tag in saved.Tags) material.SetOverrideTag(tag.Key, entry.Locked && LockTag(tag.Key) ? "" : tag.Value);
            material.renderQueue = saved.Queue;
            string original = entry.Locked ? saved.Tag("OriginalKeywords") : "";
            material.shaderKeywords = string.IsNullOrEmpty(original) ? saved.Keywords.Distinct().ToArray() : original.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (entry.Locked)
            {
                foreach (string name in material.GetTexturePropertyNames())
                {
                    var texture = saved.Texture(name);
                    if (texture.Texture && !material.GetTexture(name)) material.SetTexture(name, texture.Texture);
                }
                Set(material, "_ShaderOptimizerEnabled", 0);
            }
            var family = FamilyOf(shader);
            if (family == ShaderFamily.Poiyomi || family == ShaderFamily.LilToon) MaterialEditor.ApplyMaterialPropertyDrawers(material);
            else if (family == ShaderFamily.ToonStandard) Orbiters.Toolkit.Editor.MaterialRepair.ConfigureTextureFeatures(material);
        }

        // Every saved value the shader has: a copy of a material only keeps what its own shader had.
        private static void Restore(Material material, SavedMaterial saved)
        {
            var shader = material.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Texture:
                        if (!saved.Textures.TryGetValue(name, out var texture)) break;
                        material.SetTexture(name, texture.Texture);
                        if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.NoScaleOffset) == 0)
                        { material.SetTextureScale(name, texture.Scale); material.SetTextureOffset(name, texture.Offset); }
                        break;
                    case ShaderPropertyType.Color:
                    case ShaderPropertyType.Vector:
                        if (saved.Colors.TryGetValue(name, out var color)) material.SetColor(name, color);
                        break;
                    case ShaderPropertyType.Int:
                        if (saved.Floats.TryGetValue(name, out float integer)) material.SetInteger(name, Mathf.RoundToInt(integer));
                        break;
                    default:
                        if (saved.Floats.TryGetValue(name, out float value)) material.SetFloat(name, value);
                        break;
                }
            }
        }

        // Another family: values saved under names the new shader also uses mean something else there and go back to its
        // defaults, except the settings both shaders share; then the surface is written in the new shader's terms.
        private static void Remap(Material material, BrokenMaterial entry, SavedMaterial saved)
        {
            var shader = entry.Target;
            var s = entry.Surface;
            var family = FamilyOf(shader);
            material.shader = shader;
            material.shaderKeywords = new string[0];
            var prefixes = new List<string>();
            if (SharesLilToonSettings(entry.Family, family))
            {
                if (s.Shadows) prefixes.Add("_Shadow");
                if (s.Rim) prefixes.Add("_Rim");
                if (s.Backlight) prefixes.Add("_Backlight");
            }
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                if (!saved.Has(name) || prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) continue;
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Texture:
                        material.SetTexture(name, null);
                        if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.NoScaleOffset) == 0)
                        { material.SetTextureScale(name, Vector2.one); material.SetTextureOffset(name, Vector2.zero); }
                        break;
                    case ShaderPropertyType.Color:
                        material.SetColor(name, shader.GetPropertyDefaultVectorValue(i));
                        break;
                    case ShaderPropertyType.Vector:
                        material.SetVector(name, shader.GetPropertyDefaultVectorValue(i));
                        break;
                    case ShaderPropertyType.Int:
                        material.SetInteger(name, shader.GetPropertyDefaultIntValue(i));
                        break;
                    default:
                        material.SetFloat(name, shader.GetPropertyDefaultFloatValue(i));
                        break;
                }
            }
            // VRChat's fallback and other tags still describe the material; its render type is the new shader's.
            foreach (var tag in saved.Tags) material.SetOverrideTag(tag.Key, tag.Key == "RenderType" || LockTag(tag.Key) ? "" : tag.Value);
            switch (family)
            {
                case ShaderFamily.Poiyomi: WritePoiyomi(material, s); break;
                case ShaderFamily.LilToon: WriteLilToon(material, s); break;
                case ShaderFamily.ToonStandard: WriteToonStandard(material, s); break;
                default: WriteStandard(material, s); break;
            }
            // A render queue the author set (to sort it against other materials) stays.
            if (saved.Queue >= 0) material.renderQueue = saved.Queue;
        }

        private static void WritePoiyomi(Material m, Surface s)
        {
            string preset = s.Mode == SurfaceMode.Opaque ? "Opaque" : s.Mode == SurfaceMode.Cutout ? "Cutout" : s.Mode == SurfaceMode.Fade ? "Fade" : "Transparent";
            if (!ThryPreset(m, "_Mode", preset)) Blend(m, s.Mode);
            SetTexture(m, "_MainTex", s.Main); SetColor(m, "_Color", s.Color);
            SetTexture(m, "_BumpMap", s.Normal); Set(m, "_BumpScale", s.NormalScale);
            Set(m, "_Cull", s.Cull);
            if (s.Mode == SurfaceMode.Cutout) Set(m, "_Cutoff", s.Cutoff);
            Set(m, "_EnableEmission", s.Emits ? 1 : 0);
            if (s.Emits)
            {
                SetTexture(m, "_EmissionMap", s.EmissionMap); SetColor(m, "_EmissionColor", s.Emission);
                Set(m, "_EmissionStrength", s.EmissionStrength); Set(m, "_EmissionBaseColorAsMap", s.EmissionTint);
            }
            // Multilayer Math is lilToon's shading; Flat is lilToon without shadows.
            if (s.Shadows) Set(m, "_LightingMode", 1); else if (s.FlatLit) Set(m, "_LightingMode", 5);
            if (s.Rim) { Set(m, "_EnableRimLighting", 1); Set(m, "_RimStyle", 2); }
            if (s.Backlight) Set(m, "_BacklightEnabled", 1);
            MaterialEditor.ApplyMaterialPropertyDrawers(m);
        }

        private static void WriteLilToon(Material m, Surface s)
        {
            SetTexture(m, "_MainTex", s.Main); SetColor(m, "_Color", s.Color); Set(m, "_Cull", s.Cull);
            if (s.Mode == SurfaceMode.Cutout) Set(m, "_Cutoff", s.Cutoff);
            Set(m, "_UseBumpMap", s.Normal.Texture ? 1 : 0); SetTexture(m, "_BumpMap", s.Normal); Set(m, "_BumpScale", s.NormalScale);
            Set(m, "_UseEmission", s.Emits ? 1 : 0);
            if (s.Emits)
            {
                var color = s.Emission * s.EmissionStrength; color.a = 1;
                SetTexture(m, "_EmissionMap", s.EmissionMap); SetColor(m, "_EmissionColor", color);
                Set(m, "_EmissionMainStrength", s.EmissionTint);
            }
            Set(m, "_UseShadow", s.Shadows ? 1 : 0); Set(m, "_UseRim", s.Rim ? 1 : 0); Set(m, "_UseBacklight", s.Backlight ? 1 : 0);
            m.renderQueue = -1;
            MaterialEditor.ApplyMaterialPropertyDrawers(m);
        }

        private static void WriteToonStandard(Material m, Surface s)
        {
            SetTexture(m, "_MainTex", s.Main); SetColor(m, "_Color", s.Color); Set(m, "_Culling", s.Cull);
            SetTexture(m, "_BumpMap", s.Normal); Set(m, "_BumpScale", s.NormalScale);
            SetColor(m, "_EmissionColor", s.Emits ? s.Emission : Color.black);
            if (s.Emits) { SetTexture(m, "_EmissionMap", s.EmissionMap); Set(m, "_EmissionStrength", Mathf.Clamp(s.EmissionStrength, 0, 2)); }
            m.renderQueue = -1;
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.DefaultRough(m);
            Orbiters.Toolkit.Editor.MaterialRepair.ConfigureTextureFeatures(m);
        }

        private static void WriteStandard(Material m, Surface s)
        {
            Blend(m, s.Mode);
            SetTexture(m, "_MainTex", s.Main); SetColor(m, "_Color", s.Color);
            if (s.Mode == SurfaceMode.Cutout) Set(m, "_Cutoff", s.Cutoff);
            SetTexture(m, "_BumpMap", s.Normal); Set(m, "_BumpScale", s.NormalScale);
            Keyword(m, "_NORMALMAP", s.Normal.Texture);
            var emission = s.Emits ? s.Emission * s.EmissionStrength : Color.black; emission.a = 1;
            SetColor(m, "_EmissionColor", emission);
            if (s.Emits) SetTexture(m, "_EmissionMap", s.EmissionMap);
            Keyword(m, "_EMISSION", s.Emits);
            m.globalIlluminationFlags = s.Emits ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.DefaultRough(m);
        }

        // Standard's blend modes, and Poiyomi's when it does not describe its presets.
        private static void Blend(Material m, SurfaceMode mode)
        {
            bool poiyomi = FamilyOf(m.shader) == ShaderFamily.Poiyomi;
            int standard = mode == SurfaceMode.Opaque ? 0 : mode == SurfaceMode.Cutout ? 1 : mode == SurfaceMode.Fade ? 2 : 3;
            Set(m, "_Mode", poiyomi ? (mode == SurfaceMode.Opaque ? 0 : mode == SurfaceMode.Cutout ? 1 : mode == SurfaceMode.Fade ? 2 : 3) : standard);
            var source = mode == SurfaceMode.Fade ? BlendMode.SrcAlpha : BlendMode.One;
            var destination = mode == SurfaceMode.Opaque || mode == SurfaceMode.Cutout ? BlendMode.Zero : BlendMode.OneMinusSrcAlpha;
            Set(m, "_SrcBlend", (float)source); Set(m, "_DstBlend", (float)destination);
            Set(m, "_ZWrite", mode == SurfaceMode.Opaque || mode == SurfaceMode.Cutout ? 1 : 0);
            Set(m, "_AlphaPremultiply", mode == SurfaceMode.Transparent ? 1 : 0);
            Keyword(m, "_ALPHATEST_ON", mode == SurfaceMode.Cutout && !poiyomi);
            Keyword(m, "_ALPHABLEND_ON", mode == SurfaceMode.Fade && !poiyomi);
            Keyword(m, "_ALPHAPREMULTIPLY_ON", mode == SurfaceMode.Transparent && !poiyomi);
            m.SetOverrideTag("RenderType", mode == SurfaceMode.Opaque ? "" : mode == SurfaceMode.Cutout ? "TransparentCutout" : "Transparent");
            m.renderQueue = mode == SurfaceMode.Opaque ? -1 : mode == SurfaceMode.Cutout ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Transparent;
        }

        private static readonly Regex PresetBlock = new Regex(@"\{\s*value\s*:\s*(?<value>-?[0-9.]+)\s*,\s*actions\s*:\s*\[(?<actions>.*?)\]\s*\}", RegexOptions.Singleline);
        private static readonly Regex SetAction = new Regex(@"type\s*:\s*SET_PROPERTY\s*,\s*data\s*:\s*(?<key>[A-Za-z0-9_]+)\s*=\s*(?<value>[^,}\]]+)");

        /// <summary>
        /// Chooses <paramref name="option"/> of a ThryEditor enum (Poiyomi's Rendering Preset) and applies what choosing it in
        /// Poiyomi's inspector sets: blend factors, depth writes, render queue and type, read from the shader itself.
        /// </summary>
        internal static bool ThryPreset(Material m, string property, string option)
        {
            var shader = m.shader;
            int index = shader.FindPropertyIndex(property);
            if (index < 0) return false;
            float? value = null;
            foreach (string attribute in shader.GetPropertyAttributes(index))
            {
                int open = attribute.IndexOf('('), close = attribute.LastIndexOf(')');
                if (attribute.IndexOf("Enum", StringComparison.Ordinal) < 0 || open < 0 || close <= open) continue;
                var parts = attribute.Substring(open + 1, close - open - 1).Split(',').Select(p => p.Trim()).ToArray();
                for (int i = 0; i + 1 < parts.Length; i += 2)
                    if (string.Equals(parts[i], option, StringComparison.OrdinalIgnoreCase) &&
                        float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) value = parsed;
            }
            if (value == null) return false;
            foreach (Match block in PresetBlock.Matches(shader.GetPropertyDescription(index) ?? ""))
            {
                if (!float.TryParse(block.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float blockValue) || blockValue != value.Value) continue;
                m.SetFloat(property, value.Value);
                foreach (Match action in SetAction.Matches(block.Groups["actions"].Value))
                {
                    string key = action.Groups["key"].Value, text = action.Groups["value"].Value.Trim();
                    if (key == "render_queue" && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int queue)) m.renderQueue = queue;
                    else if (key == "render_type") m.SetOverrideTag("RenderType", text);
                    else if (m.HasProperty(key) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)) m.SetFloat(key, number);
                }
                return true;
            }
            return false;
        }

        private static void Set(Material m, string name, float value) { if (m.HasProperty(name)) m.SetFloat(name, value); }
        private static void SetColor(Material m, string name, Color value) { if (m.HasProperty(name)) m.SetColor(name, value); }

        private static void SetTexture(Material m, string name, SavedTexture texture)
        {
            if (!m.HasProperty(name)) return;
            m.SetTexture(name, texture.Texture);
            m.SetTextureScale(name, texture.Scale); m.SetTextureOffset(name, texture.Offset);
        }

        private static void Keyword(Material m, string keyword, bool on) { if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword); }
    }
}
