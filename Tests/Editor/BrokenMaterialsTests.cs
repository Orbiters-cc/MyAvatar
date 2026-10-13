using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Materials that don't render: detection from what they saved, the shader they move to, and the one-step fix (in place,
    // or on a copy for read-only materials). Broken materials are real assets whose shader GUID nothing answers to.
    public sealed class BrokenMaterialsTests
    {
        private const string LilToonOutline = "efa77a80ca0344749b4f19fdd5891cbe", LilToon = "df12117ecd77c31469c224178886498e", LilToonTransparent = "165365ab7100a044ca85fc8c33548a62";
        private Scene scene;
        private string folder;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly List<string> assets = new List<string>();

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            folder = "Assets/Orbiters/MyAvatar/BrokenTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var value in owned) if (value) Undo.ClearUndo(value);
            foreach (string path in assets) { var asset = AssetDatabase.LoadMainAssetAtPath(path); if (asset) Undo.ClearUndo(asset); AssetDatabase.DeleteAsset(path); }
            foreach (var material in AssetDatabase.FindAssets("t:Material", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<Material>))
                if (material) Undo.ClearUndo(material);
            foreach (var value in owned) if (value) UnityEngine.Object.DestroyImmediate(value);
            owned.Clear(); assets.Clear();
            AssetDatabase.DeleteAsset(folder);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        [Test]
        public void MissingShadersAreFoundAndReadFromWhatTheMaterialSaved()
        {
            Assume.That(AssetDatabase.GUIDToAssetPath(LilToonOutline), Is.Empty, "lilToon is installed: its materials render");
            var body = Broken("Body", LilToonOutline, m =>
            {
                m.SetFloat("_lilToonVersion", 36); m.SetFloat("_UseShadow", 1); m.SetFloat("_UseMain2ndTex", 1);
                m.SetOverrideTag("VRCFallback", "MobileToon");
            });
            var fine = Own(new Material(Shader.Find("Standard")) { name = "Fine" });
            var root = Avatar();
            Part(root, "Body", body, fine);
            Part(root, "Hair", body);
            Part(root, "Preview", Broken("Helper", LilToon, m => m.SetFloat("_lilToonVersion", 36))).tag = "EditorOnly";

            var broken = BrokenMaterials.Inspect(root);

            Assert.AreEqual(1, broken.Count, "materials that render and editor helpers are left alone");
            var entry = broken[0];
            Assert.AreEqual(body, entry.Material);
            Assert.AreEqual(2, entry.Renderers.Count);
            Assert.AreEqual(BrokenMaterials.Missing, entry.Problem);
            Assert.AreEqual(LilToonOutline, SavedMaterial.Read(body).ShaderGuid, "the missing shader's GUID is read from the material");
            Assert.AreEqual(LilToonOutline, SavedMaterial.FileShaderGuid(body), "and from its .mat file");
            Assert.AreEqual(ShaderFamily.LilToon, entry.Family);
            Assert.AreEqual("lilToon Outline", entry.MadeFor);
            Assert.AreEqual(SurfaceMode.Opaque, entry.Surface.Mode);
            Assert.AreEqual(MaterialShaders.LilToonPackage, entry.Installable);
            Assert.NotNull(entry.Target, "Standard at least is always there");
            Assert.False(entry.Rebind);
            CollectionAssert.Contains(entry.Lost, "outline");
            CollectionAssert.Contains(entry.Lost, "2nd texture layer");
            StringAssert.Contains("lilToon Outline isn't in this project → ", entry.Change);
        }

        [Test]
        public void FamiliesAndVariantsAreRecognized()
        {
            Assert.AreEqual(ShaderFamily.LilToon, MaterialShaders.Detect(new SavedMaterial { ShaderGuid = LilToonTransparent }));
            Assert.AreEqual(ShaderFamily.LilToon, MaterialShaders.Detect(Values("_lilToonVersion")));
            Assert.AreEqual(ShaderFamily.Poiyomi, MaterialShaders.Detect(Values("shader_master_label", "_LightingMode", "_lilToonVersion")), "Poiyomi converted from lilToon keeps lilToon's values");
            var locked = Values(); locked.Tags["OriginalShader"] = ".poiyomi/Poiyomi Toon";
            Assert.AreEqual(ShaderFamily.Poiyomi, MaterialShaders.Detect(locked));
            Assert.AreEqual(ShaderFamily.ToonStandard, MaterialShaders.Detect(Values("_ShadowBoost", "_LimitBrightness")));
            Assert.AreEqual(ShaderFamily.Standard, MaterialShaders.Detect(Values("_GlossyReflections", "_SpecularHighlights", "_UVSec")));
            Assert.AreEqual(ShaderFamily.Unknown, MaterialShaders.Detect(Values("_MainTex")));
            var leftovers = Values("_GlossyReflections", "_SpecularHighlights", "_UVSec"); leftovers.ShaderGuid = Guid.NewGuid().ToString("N");
            Assert.AreEqual(ShaderFamily.Unknown, MaterialShaders.Detect(leftovers), "Standard's values left from a model import don't make a project shader Standard");

            Assert.AreEqual(SurfaceMode.Transparent, MaterialShaders.Mode(Values(), ShaderFamily.LilToon, "Hidden/lilToonTransparentOutline"));
            Assert.AreEqual(SurfaceMode.Cutout, MaterialShaders.Mode(Values(), ShaderFamily.LilToon, "Hidden/lilToonLiteCutout"));
            Assert.AreEqual(SurfaceMode.Opaque, MaterialShaders.Mode(new SavedMaterial { Queue = 3000 }, ShaderFamily.LilToon, "Hidden/lilToonLite"), "the variant wins over a raised queue");
            var fallback = Values(); fallback.Tags["VRCFallback"] = "ToonTransparent";
            Assert.AreEqual(SurfaceMode.Transparent, MaterialShaders.Mode(fallback, ShaderFamily.Unknown, null));
            var poiyomi = Values(); poiyomi.Floats["_Mode"] = 9;
            Assert.AreEqual(SurfaceMode.Cutout, MaterialShaders.Mode(poiyomi, ShaderFamily.Poiyomi, null), "TransClipping");

            Assert.AreEqual("lilToon One Pass Transparent Outline", MaterialShaders.Pretty("Hidden/lilToonOnePassTransparentOutline"));
            Assert.AreEqual("Poiyomi Toon", MaterialShaders.Pretty(".poiyomi/Poiyomi Toon"));
            Assert.AreEqual("Poiyomi Toon 8.1", MaterialShaders.Pretty(".poiyomi/Old Versions/8.1/Poiyomi Toon"));
            Assert.AreEqual("VRChat Toon Standard", MaterialShaders.Pretty("VRChat/Mobile/Toon Standard"));
            Assert.True(MaterialShaders.Additive("_lil/[Optional] lilToonFakeShadow"));
        }

        [Test]
        public void LilToonMovesToPoiyomiWithItsSurfaceAndShadingInOneUndoStep()
        {
            Assume.That(Shader.Find(MaterialShaders.PoiyomiToon), Is.Not.Null, "Poiyomi Toon is not installed");
            Assume.That(AssetDatabase.GUIDToAssetPath(LilToonTransparent), Is.Empty, "lilToon is installed: its materials render");
            var albedo = Png("Albedo", new Color32(200, 120, 60, 128));
            var normal = Png("Normal", new Color32(128, 128, 255, 255));
            var glow = Png("Glow", new Color32(255, 255, 255, 255));
            var visor = Broken("Visor", LilToonTransparent, m =>
            {
                m.SetFloat("_lilToonVersion", 36);
                m.SetTexture("_MainTex", albedo); m.SetTextureScale("_MainTex", new Vector2(2, 3));
                m.SetColor("_Color", new Color(1, 1, 1, .75f));
                m.SetFloat("_UseBumpMap", 1); m.SetTexture("_BumpMap", normal); m.SetFloat("_BumpScale", .6f);
                m.SetFloat("_UseEmission", 1); m.SetTexture("_EmissionMap", glow); m.SetColor("_EmissionColor", new Color(1, .5f, .25f, .4f)); m.SetFloat("_EmissionBlend", .5f);
                m.SetFloat("_EmissionMainStrength", 1);
                m.SetFloat("_UseShadow", 1); m.SetColor("_ShadowColor", new Color(.2f, .3f, .4f, 1)); m.SetFloat("_ShadowBorder", .7f);
                m.SetFloat("_UseMain2ndTex", 1); m.SetFloat("_Cull", 0);
                // lilToon's value under a name Poiyomi also uses, for something else.
                m.SetFloat("_ZWrite", 1);
            });
            var root = Avatar();
            Part(root, "Visor", visor);
            var entry = BrokenMaterials.Inspect(root).Single();
            Assert.AreEqual(MaterialShaders.PoiyomiToon, entry.Target.name);
            Assert.AreEqual(SurfaceMode.Transparent, entry.Surface.Mode);
            CollectionAssert.IsSupersetOf(entry.Kept, new[] { "texture", "normal map", "emission", "shadow colors" });
            CollectionAssert.Contains(entry.Lost, "2nd texture layer");

            Assert.AreEqual(1, BrokenMaterials.Fix(root, null, new[] { entry }));

            // Unity objects compare by identity (the same asset can come back through another managed wrapper).
            Assert.AreEqual(visor, root.GetComponentInChildren<Renderer>().sharedMaterial, "a project material is fixed where it is");
            Assert.AreEqual(MaterialShaders.PoiyomiToon, visor.shader.name);
            Assert.IsNull(BrokenMaterials.Problem(visor));
            Assert.AreEqual(albedo, visor.GetTexture("_MainTex"));
            Assert.AreEqual(new Vector2(2, 3), visor.GetTextureScale("_MainTex"));
            Assert.AreEqual(.75f, visor.GetColor("_Color").a, 1e-4f);
            Assert.AreEqual(normal, visor.GetTexture("_BumpMap"));
            Assert.AreEqual(.6f, visor.GetFloat("_BumpScale"), 1e-4f);
            Assert.AreEqual(1, visor.GetFloat("_EnableEmission"));
            Assert.AreEqual(glow, visor.GetTexture("_EmissionMap"));
            Assert.AreEqual(.2f, visor.GetFloat("_EmissionStrength"), 1e-4f, "lilToon's emission alpha × blend is Poiyomi's strength");
            Assert.AreEqual(.5f, visor.GetColor("_EmissionColor").g, 1e-4f);
            Assert.AreEqual(1, visor.GetFloat("_EmissionBaseColorAsMap"), "glowing in the base texture's colors (eyes), not the mask's white");
            Assert.True(visor.IsKeywordEnabled("_EMISSION"), "Poiyomi's drawers set its keywords");
            Assert.AreEqual(1, visor.GetFloat("_LightingMode"), "lilToon's shading is Poiyomi's Multilayer Math");
            Near(new Color(.2f, .3f, .4f, 1), visor.GetColor("_ShadowColor"), "shared shadow settings are kept");
            Assert.AreEqual(.7f, visor.GetFloat("_ShadowBorder"), 1e-4f);
            Assert.AreEqual(0, visor.GetFloat("_Cull"));
            Assert.AreEqual(3, visor.GetFloat("_Mode"), "Poiyomi's Transparent preset");
            Assert.AreEqual(0, visor.GetFloat("_ZWrite"), "the preset's depth writes, not lilToon's");
            Assert.AreEqual(3000, visor.renderQueue);
            Assert.AreEqual("Transparent", visor.GetTag("RenderType", false));
            string poiyomi = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(Shader.Find(MaterialShaders.PoiyomiToon)));
            Assert.AreEqual(poiyomi, SavedMaterial.FileShaderGuid(visor), "the fix is saved to the .mat file");

            UndoFix();
            Assert.AreEqual(MaterialShaders.ErrorShader, visor.shader.name, "one Undo step restores it");
            Assert.AreEqual(LilToonTransparent, SavedMaterial.Read(visor).ShaderGuid);
            Assert.AreEqual(albedo, visor.GetTexture("_MainTex"));
        }

        [Test]
        public void WithoutToonShadersOpaqueMovesToToonStandardAndTransparentToStandard()
        {
            Assume.That(Shader.Find(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader), Is.Not.Null, "the VRChat SDK is not installed");
            var albedo = Png("Skin", new Color32(90, 160, 220, 255));
            var skin = Broken("Skin", LilToon, m =>
            {
                m.SetFloat("_lilToonVersion", 36); m.SetTexture("_MainTex", albedo); m.SetColor("_Color", new Color(.9f, .8f, .7f, 1));
                m.SetFloat("_UseEmission", 1); m.SetColor("_EmissionColor", new Color(0, 1, 0, 1)); m.SetFloat("_Cull", 0);
            });
            var lens = Broken("Lens", LilToonTransparent, m =>
            {
                m.SetFloat("_lilToonVersion", 36); m.SetTexture("_MainTex", albedo); m.SetFloat("_Cull", 0);
            });
            var root = Avatar();
            Part(root, "Body", skin, lens);
            var catalog = new ShaderCatalog(name => name.StartsWith(".poiyomi/", StringComparison.Ordinal) || name.IndexOf("lilToon", StringComparison.Ordinal) >= 0);
            var broken = BrokenMaterials.Inspect(root, catalog).ToDictionary(b => b.Material.name);
            Assert.AreEqual(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader, broken["Skin"].Target.name);
            Assert.AreEqual("Standard", broken["Lens"].Target.name, "Toon Standard is opaque");
            CollectionAssert.Contains(broken["Lens"].Lost, "both sides", "Standard draws only the front");

            Assert.AreEqual(2, BrokenMaterials.Fix(root, null, broken.Values));

            Assert.AreEqual(albedo, skin.GetTexture("_MainTex"));
            Near(new Color(.9f, .8f, .7f, 1), skin.GetColor("_Color"));
            Assert.AreEqual(0, skin.GetFloat("_Culling"));
            Assert.AreEqual(1, skin.GetColor("_EmissionColor").g, 1e-4f);
            Assert.AreEqual(3, lens.GetFloat("_Mode"));
            Assert.AreEqual(3000, lens.renderQueue);
            Assert.True(lens.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"));
            Assert.AreEqual(albedo, lens.GetTexture("_MainTex"));
            UndoFix();
        }

        [Test]
        public void LockedPoiyomiMaterialIsUnlockedWithEverySetting()
        {
            var original = Shader.Find(MaterialShaders.PoiyomiToon);
            Assume.That(original, Is.Not.Null, "Poiyomi Toon is not installed");
            var glow = Png("Glow", new Color32(255, 255, 255, 255));
            string missing = Guid.NewGuid().ToString("N");
            var hair = Broken("Hair", missing, m =>
            {
                m.SetFloat("shader_master_label", 0); m.SetFloat("_ShaderOptimizerEnabled", 1); m.SetFloat("_LightingMode", 1);
                m.SetFloat("_EnableEmission", 1); m.SetFloat("_ShadowBorder", .33f); m.SetColor("_Color", new Color(.3f, .6f, .9f, 1));
                m.SetOverrideTag("OriginalShader", MaterialShaders.PoiyomiToon);
                m.SetOverrideTag("OriginalShaderGUID", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(original)));
                m.SetOverrideTag("OriginalKeywords", "_EMISSION _LIGHTINGMODE_MULTILAYER_MATH");
                m.SetOverrideTag("_stripped_tex__EmissionMap", AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(glow)));
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = 3000;
            });
            var root = Avatar();
            Part(root, "Hair", hair);
            var entry = BrokenMaterials.Inspect(root).Single();
            Assert.AreEqual(ShaderFamily.Poiyomi, entry.Family);
            Assert.True(entry.Locked);
            Assert.True(entry.Rebind);
            Assert.AreEqual(original, entry.Target);
            StringAssert.Contains("its generated shader is missing", entry.Change);

            BrokenMaterials.Fix(root, null, new[] { entry });

            Assert.AreEqual(original, hair.shader);
            Assert.AreEqual(0, hair.GetFloat("_ShaderOptimizerEnabled"), "unlocked");
            Near(new Color(.3f, .6f, .9f, 1), hair.GetColor("_Color"));
            Assert.AreEqual(.33f, hair.GetFloat("_ShadowBorder"), 1e-4f, "every saved setting is kept");
            Assert.AreEqual(1, hair.GetFloat("_LightingMode"));
            Assert.AreEqual(3000, hair.renderQueue);
            Assert.AreEqual("Transparent", hair.GetTag("RenderType", false));
            Assert.AreEqual(glow, hair.GetTexture("_EmissionMap"), "textures the lock kept aside come back");
            Assert.True(hair.IsKeywordEnabled("_EMISSION") && hair.IsKeywordEnabled("_LIGHTINGMODE_MULTILAYER_MATH"));
            Assert.AreEqual("", hair.GetTag("OriginalShader", false));
            UndoFix();
            Assert.AreEqual(MaterialShaders.ErrorShader, hair.shader.name);
        }

        [Test]
        public void ReadOnlyMaterialsAreFixedOnACopyForThisAvatarOnly()
        {
            var shared = Broken("Shared", Guid.NewGuid().ToString("N"), m => { m.SetColor("_Color", Color.red); m.SetOverrideTag("VRCFallback", "Toon"); });
            Assert.False(BrokenMaterials.ReadOnly(shared), "a .mat file in Assets is fixed where it is");
            var root = Avatar();
            var renderer = Part(root, "Body", shared).GetComponent<Renderer>();
            var other = Avatar("Other");
            var otherRenderer = Part(other, "Body", shared).GetComponent<Renderer>();
            var entry = BrokenMaterials.Inspect(root).Single();
            Assert.AreEqual(ShaderFamily.Unknown, entry.Family);
            Assert.NotNull(entry.Target);

            Assert.AreEqual(1, BrokenMaterials.Fix(root, null, new[] { entry }, copyAll: true));
            Assert.IsNull(entry.Backup, "a copy leaves the original as it is: no backup");

            var copy = renderer.sharedMaterial;
            assets.Add(AssetDatabase.GetAssetPath(copy));
            Assert.AreNotEqual(shared, copy);
            StringAssert.StartsWith(AvatarMaterials.Folder + "/", AssetDatabase.GetAssetPath(copy));
            Assert.IsNull(BrokenMaterials.Problem(copy));
            Near(Color.red, copy.GetColor(copy.HasProperty("_Color") ? "_Color" : "_BaseColor"));
            Assert.AreEqual(shared, otherRenderer.sharedMaterial, "another avatar keeps the original");
            Assert.NotNull(BrokenMaterials.Problem(shared), "the read-only original is untouched");
            UndoFix();
            Assert.AreEqual(shared, renderer.sharedMaterial);
        }

        [Test]
        public void TheOriginalIsKeptBesideAsOldAndNeverListedAsBroken()
        {
            var skin = Broken("Skin", LilToon, m => { m.SetFloat("_lilToonVersion", 36); m.SetColor("_Color", Color.green); });
            var root = Avatar();
            Part(root, "Body", skin);
            var entry = BrokenMaterials.Inspect(root).Single();

            BrokenMaterials.Fix(root, null, new[] { entry });

            Assert.AreEqual(folder + "/Skin (old).mat", entry.Backup);
            var old = AssetDatabase.LoadAssetAtPath<Material>(entry.Backup);
            Assert.NotNull(old);
            Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(skin)), AssetDatabase.AssetPathToGUID(entry.Backup), "a file of its own");
            Assert.True(BrokenMaterials.IsBackup(old));
            Assert.AreEqual("Skin (old)", old.name);
            Assert.AreEqual(LilToon, SavedMaterial.Read(old).ShaderGuid, "the backup still names the shader it was made for");
            Assert.AreEqual(Color.green, SavedMaterial.Read(old).Colors["_Color"]);
            Assert.IsNull(BrokenMaterials.Problem(skin), "the original asset itself is the one fixed");
            Part(root, "Old", old);
            Assert.IsEmpty(BrokenMaterials.Inspect(root), "a backup put back on a renderer is not listed");

            UndoFix();
            var again = BrokenMaterials.Inspect(root).Single();
            Assert.AreEqual(skin, again.Material, "after Undo only the original is listed, not its backup");
            BrokenMaterials.Fix(root, null, new[] { again });
            Assert.AreEqual(entry.Backup, again.Backup, "fixing again keeps the same backup");
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(folder + "/Skin (old) 1.mat"));
            UndoFix();
        }

        // The same name, another original (imported again since the first fix was undone): both are kept.
        [Test]
        public void AnotherOriginalGetsABackupOfItsOwn()
        {
            var skin = Broken("Skin", LilToon, m => { m.SetFloat("_lilToonVersion", 36); m.SetColor("_Color", Color.green); });
            var root = Avatar();
            Part(root, "Body", skin);
            var entry = BrokenMaterials.Inspect(root).Single();
            BrokenMaterials.Fix(root, null, new[] { entry });
            UndoFix();
            using (var values = new Saved(skin)) values.SetColor("_Color", Color.blue);
            EditorUtility.SetDirty(skin);

            var again = BrokenMaterials.Inspect(root).Single();
            BrokenMaterials.Fix(root, null, new[] { again });

            Assert.AreEqual(folder + "/Skin (old) 1.mat", again.Backup, "the other original gets the next name");
            Assert.AreEqual(Color.green, SavedMaterial.Read(AssetDatabase.LoadAssetAtPath<Material>(entry.Backup)).Colors["_Color"], "the first backup stays as it was");
            Assert.AreEqual(Color.blue, SavedMaterial.Read(AssetDatabase.LoadAssetAtPath<Material>(again.Backup)).Colors["_Color"]);
            UndoFix();
        }

        // A copy is never fixed again for another avatar: each fix makes its own (an unused one goes as a generated file;
        // this avatar is a test's, so its copies are not tracked with the user's).
        [Test]
        public void EachFixMakesItsOwnCopy()
        {
            var shared = Broken("Shared", Guid.NewGuid().ToString("N"), m => { m.SetColor("_Color", Color.red); m.SetOverrideTag("VRCFallback", "Toon"); });
            var root = Avatar();
            var renderer = Part(root, "Body", shared).GetComponent<Renderer>();
            BrokenMaterials.Fix(root, null, new[] { BrokenMaterials.Inspect(root).Single() }, copyAll: true);
            var copy = renderer.sharedMaterial;
            assets.Add(AssetDatabase.GetAssetPath(copy));
            Assert.False(Orbiters.Toolkit.Editor.Storage.GeneratedAssets.IsTracked(AssetDatabase.GetAssetPath(copy)), "a test's copy stays out of the user's list");
            UndoFix();
            Assert.AreEqual(shared, renderer.sharedMaterial);

            BrokenMaterials.Fix(root, null, new[] { BrokenMaterials.Inspect(root).Single() }, copyAll: true);

            var again = renderer.sharedMaterial;
            assets.Add(AssetDatabase.GetAssetPath(again));
            Assert.AreNotEqual(copy, again, "a new copy: the first may be someone else's now");
            Assert.IsNull(BrokenMaterials.Problem(again));
            UndoFix();
        }

        [Test]
        public void ParticleEffectsAreNotGuessed()
        {
            var smoke = Broken("Smoke", Guid.NewGuid().ToString("N"), m => m.SetColor("_TintColor", Color.gray));
            var root = Avatar();
            var part = new GameObject("Smoke"); part.transform.SetParent(root.transform, false);
            part.AddComponent<ParticleSystem>();
            part.GetComponent<ParticleSystemRenderer>().sharedMaterial = smoke;
            var entry = BrokenMaterials.Inspect(root).Single();
            Assert.IsNull(entry.Target);
            StringAssert.Contains("particle", entry.Reason);
            Assert.AreEqual(0, BrokenMaterials.Fix(root, null, new[] { entry }));
        }

        // ---- Fixtures --------------------------------------------------------------------------------------------

        private T Own<T>(T value) where T : UnityEngine.Object { owned.Add(value); return value; }

        private GameObject Avatar(string name = "Avatar")
        {
            var root = Own(new GameObject(name));
            SceneManager.MoveGameObjectToScene(root, scene);
            return root;
        }

        private GameObject Part(GameObject root, string name, params Material[] materials)
        {
            var part = new GameObject(name);
            part.transform.SetParent(root.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            owned.Add(renderer);
            return part;
        }

        // A material made for a shader the project doesn't have: its saved values, then a shader GUID nothing answers to.
        private Material Broken(string name, string guid, Action<Saved> setup)
        {
            var material = new Material(Shader.Find(MaterialShaders.ErrorShader)) { name = name };
            string path = folder + "/" + name + ".mat";
            AssetDatabase.CreateAsset(material, path);
            using (var values = new Saved(material)) setup(values);
            AssetDatabase.SaveAssets();
            string text = File.ReadAllText(path);
            text = Regex.Replace(text, @"m_Shader: \{fileID: -?\d+, guid: [0-9a-f]+, type: \d+\}", "m_Shader: {fileID: 4800000, guid: " + guid + ", type: 3}");
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var broken = AssetDatabase.LoadAssetAtPath<Material>(path);
            Assert.AreEqual(MaterialShaders.ErrorShader, broken.shader.name, name + " should not render");
            return broken;
        }

        // Values saved with a material whatever its shader has: what a material keeps from a shader that is gone.
        private sealed class Saved : IDisposable
        {
            private readonly SerializedObject serialized;
            internal Saved(Material material) { serialized = new SerializedObject(material); }
            public int renderQueue { set => serialized.FindProperty("m_CustomRenderQueue").intValue = value; }
            public void SetFloat(string name, float value) => Entry("m_SavedProperties.m_Floats", name).FindPropertyRelative("second").floatValue = value;
            public void SetColor(string name, Color value) => Entry("m_SavedProperties.m_Colors", name).FindPropertyRelative("second").colorValue = value;
            public void SetOverrideTag(string name, string value) => Entry("stringTagMap", name).FindPropertyRelative("second").stringValue = value;
            public void SetTexture(string name, Texture value)
            {
                var texture = Entry("m_SavedProperties.m_TexEnvs", name).FindPropertyRelative("second");
                texture.FindPropertyRelative("m_Texture").objectReferenceValue = value;
                texture.FindPropertyRelative("m_Scale").vector2Value = Vector2.one;
                texture.FindPropertyRelative("m_Offset").vector2Value = Vector2.zero;
            }
            public void SetTextureScale(string name, Vector2 value) =>
                Entry("m_SavedProperties.m_TexEnvs", name).FindPropertyRelative("second").FindPropertyRelative("m_Scale").vector2Value = value;

            private SerializedProperty Entry(string list, string name)
            {
                var array = serialized.FindProperty(list);
                for (int i = 0; i < array.arraySize; i++)
                    if (array.GetArrayElementAtIndex(i).FindPropertyRelative("first").stringValue == name) return array.GetArrayElementAtIndex(i);
                array.InsertArrayElementAtIndex(array.arraySize);
                var entry = array.GetArrayElementAtIndex(array.arraySize - 1);
                entry.FindPropertyRelative("first").stringValue = name;
                return entry;
            }

            public void Dispose() { serialized.ApplyModifiedPropertiesWithoutUndo(); serialized.Dispose(); }
        }

        private Texture2D Png(string name, Color32 color)
        {
            var image = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            image.SetPixels32(Enumerable.Repeat(color, 16).ToArray()); image.Apply();
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static SavedMaterial Values(params string[] floats)
        {
            var saved = new SavedMaterial();
            foreach (string name in floats) saved.Floats[name] = 0;
            return saved;
        }

        private static void Near(Color expected, Color actual, string message = null)
        {
            for (int i = 0; i < 4; i++) Assert.AreEqual(expected[i], actual[i], 1e-4f, (message ?? "") + " " + expected + " / " + actual);
        }

        // Undoes the fix, only when it is the last Undo step: never one of the user's.
        private static void UndoFix(string avatar = "Avatar")
        {
            Assert.AreEqual("Fix " + avatar + " materials", Undo.GetCurrentGroupName());
            Undo.PerformUndo();
        }
    }
}
