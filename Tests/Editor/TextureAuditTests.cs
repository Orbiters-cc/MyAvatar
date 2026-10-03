using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Texture sets and Quick optimization: import settings of reused textures, alpha, repeated optimizations, checkpoints,
    // packed slots, UV layout groups, accessory scope and the manual slot menu.
    public sealed class TextureAuditTests
    {
        private Scene scene;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly List<string> folders = new List<string>();

        [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown] public void TearDown()
        {
            foreach (var value in owned) if (value) { Undo.ClearUndo(value); UnityEngine.Object.DestroyImmediate(value); }
            owned.Clear();
            foreach (var folder in folders) if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            folders.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private T Own<T>(T value) where T : UnityEngine.Object { owned.Add(value); return value; }

        private string Folder()
        {
            string folder = "Assets/Orbiters/MyAvatar/AuditTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            return folder;
        }

        private static Texture2D Png(string folder, string name, int size, Func<int, int, Color32> pixel, bool uncompressed = true)
        {
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) pixels[y * size + x] = pixel(x, y);
            image.SetPixels32(pixels); image.Apply();
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (uncompressed) { var importer = (TextureImporter)AssetImporter.GetAtPath(path); importer.textureCompression = TextureImporterCompression.Uncompressed; importer.SaveAndReimport(); }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private MyAvatar Avatar()
        {
            var root = Own(new GameObject("Audit avatar"));
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<MyAvatar>();
        }

        [TestCase("Standard", "_Glossiness")]
        [TestCase("VRChat/Mobile/Toon Standard", "_GlossStrength")]
        public void ColorOnlyDropDefaultsToMatteWithoutChangingSourceAndUndoRestoresIt(string shader, string scalar)
        {
            var avatar = Avatar();
            var material = Own(new Material(Shader.Find(shader)) { name = "Cloth" });
            material.SetFloat(scalar, 1);
            var renderer = avatar.gameObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            var folder = Folder();
            var color = Png(folder, "Cloth_Color", 2, (x, y) => new Color32(128, 0, 0, 255));
            var entry = new TextureEntry { texture = color, fileName = "Cloth_Color.png", role = "color", material = material, property = "_MainTex" };
            TextureChanges.Apply(avatar, new List<TextureEntry> { entry }, folder + "/Batch");
            Assert.AreEqual(0, renderer.sharedMaterial.GetFloat(scalar));
            Assert.AreEqual(1, material.GetFloat(scalar));
            TextureChanges.UndoLast(avatar); Assert.AreSame(material, renderer.sharedMaterial);
            TextureChanges.UndoLast(avatar); Assert.AreEqual(0, renderer.sharedMaterial.GetFloat(scalar));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SeparateMetallicAndRoughnessPackCorrectlyInEitherOrder(bool reverse)
        {
            var avatar = Avatar(); var source = Standard("Body");
            var renderer = avatar.gameObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = source;
            var folder = Folder();
            var rough = Png(folder, "Body_Roughness", 2, (x, y) => new Color32(64, 64, 64, 255));
            var metal = Png(folder, "Body_Metallic", 2, (x, y) => new Color32(153, 153, 153, 255));
            var entries = new List<TextureEntry> {
                new TextureEntry { texture = rough, role = "roughness", fileName = "Body_Roughness.png" },
                new TextureEntry { texture = metal, role = "metallic", fileName = "Body_Metallic.png" }
            };
            if (reverse) entries.Reverse();
            Match(entries, TextureMatching.Slots(avatar));
            Assert.That(entries.All(e => e.property == "_MetallicGlossMap"), Is.True);
            Assert.AreEqual(2, TextureChanges.Apply(avatar, entries, folder + "/Batch"));
            var map = renderer.sharedMaterial.GetTexture("_MetallicGlossMap");
            var pixel = SurfacePixel(map);
            Assert.That(pixel.r, Is.EqualTo(153 / 255f).Within(.015f));
            Assert.That(pixel.a, Is.EqualTo(1 - 64 / 255f).Within(.015f));
            Assert.AreEqual(1, renderer.sharedMaterial.GetFloat("_GlossMapScale"));
            Assert.IsFalse(((TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(map))).sRGBTexture);
            Assert.IsTrue(entries.All(e => e.applied && e.appliedTexture == map));
            Assert.IsNull(source.GetTexture("_MetallicGlossMap"));
            TextureChanges.UndoLast(avatar); TextureChanges.UndoLast(avatar);
            Assert.That(SurfacePixel(renderer.sharedMaterial.GetTexture("_MetallicGlossMap")).a, Is.EqualTo(pixel.a).Within(.001f));
        }

        [TestCase("roughness", .75f)]
        [TestCase("smoothness", .25f)]
        public void ToonUsesGrayscaleDataInsteadOfOpaqueAlpha(string role, float expected)
        {
            var folder = Folder();
            var map = Png(folder, "Cloth_" + role, 2, (x, y) => new Color32(64, 64, 64, 255));
            var material = Own(new Material(Shader.Find(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader)));
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.Assign(material, "_GlossMap", map, role, map.name, folder);
            Assert.AreEqual(0, material.GetFloat("_GlossMapChannel"));
            Assert.AreEqual(1, material.GetFloat("_GlossStrength"));
            Assert.That(SurfacePixel(material.GetTexture("_GlossMap")).r, Is.EqualTo(expected).Within(.015f));
        }

        [Test] public void AuthoredSurfaceMapAndScaleSurviveMatteDefault()
        {
            var source = Standard("Body"); var folder = Folder();
            var map = Png(folder, "Body_MetallicSmoothness", 2, (x, y) => new Color32(64, 0, 0, 192));
            source.SetTexture("_MetallicGlossMap", map); source.SetFloat("_GlossMapScale", .4f);
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.DefaultRough(source);
            Assert.AreSame(map, source.GetTexture("_MetallicGlossMap")); Assert.AreEqual(.4f, source.GetFloat("_GlossMapScale"));
            Assert.AreEqual("metallic", TextureMatching.FileRole("Body_MetallicSmoothness.png"));
            var toon = Own(Orbiters.Toolkit.Editor.MaterialRepair.CreateToon(source));
            Assert.AreEqual(3, toon.GetFloat("_GlossMapChannel")); Assert.AreEqual(.4f, toon.GetFloat("_GlossStrength"));
        }

        [Test] public void AlbedoAlphaSmoothnessSurvivesMaterialRepair()
        {
            var source = Standard("Cloth"); var folder = Folder();
            var albedo = Png(folder, "Cloth_Color", 2, (x, y) => new Color32(128, 64, 32, 192));
            source.SetTexture("_MainTex", albedo); source.SetFloat("_SmoothnessTextureChannel", 1); source.SetFloat("_GlossMapScale", .4f);
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.DefaultRough(source);
            Assert.AreEqual(.4f, source.GetFloat("_GlossMapScale"));
            var copy = Own(Orbiters.Toolkit.Editor.MaterialRepair.CreateToon(source));
            Assert.AreSame(albedo, copy.GetTexture("_GlossMap")); Assert.AreEqual(3, copy.GetFloat("_GlossMapChannel"));
            Assert.AreEqual(.4f, copy.GetFloat("_GlossStrength"));
        }

        private Color SurfacePixel(Texture texture)
        {
            var read = typeof(Orbiters.Toolkit.Editor.MaterialSurfaceMaps).GetMethod("Read", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return ((Color[])read.Invoke(null, new object[] { texture, texture.width, texture.height }))[0];
        }

        [Test] public void ClothingWithoutBundledMapsDefaultsToMatteAndStaysScoped()
        {
            var avatar = Avatar(); var clothing = Child(avatar, "Clothing");
            var item = clothing.AddComponent<Orbiters.Toolkit.VRChat.OrbitersAttachment>();
            var worn = clothing.AddComponent<MeshRenderer>(); var other = Child(avatar, "Other").AddComponent<MeshRenderer>();
            var source = Standard("Cloth"); source.SetFloat("_Glossiness", 1); worn.sharedMaterial = other.sharedMaterial = source;
            Assert.AreEqual(1, AccessoryMaterials.Repair(avatar, item, Folder()));
            Assert.AreEqual(0, worn.sharedMaterial.GetFloat("_Glossiness"));
            Assert.AreSame(source, other.sharedMaterial); Assert.AreEqual(1, source.GetFloat("_Glossiness"));
            Assert.AreEqual(0, AccessoryMaterials.Repair(avatar, item, Folder()));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo(); Assert.AreSame(source, worn.sharedMaterial);
        }

        [Test] public void RemovingLastSurfaceMapRestoresMatteDefault()
        {
            var avatar = Avatar(); var clothing = Child(avatar, "Clothing"); var renderer = clothing.AddComponent<MeshRenderer>();
            var material = Standard("Cloth"); renderer.sharedMaterial = material;
            var folder = Folder(); var map = Png(folder, "Cloth_MetallicSmoothness", 2, (x, y) => new Color32(128, 0, 0, 192));
            material.SetTexture("_MetallicGlossMap", map); material.SetFloat("_Glossiness", 1); material.SetFloat("_GlossMapScale", 1);
            TextureChanges.RemoveSlot(avatar, clothing.transform, material, "_MetallicGlossMap", folder);
            Assert.IsNull(renderer.sharedMaterial.GetTexture("_MetallicGlossMap")); Assert.AreEqual(0, renderer.sharedMaterial.GetFloat("_Glossiness"));
            Assert.AreSame(map, material.GetTexture("_MetallicGlossMap"));
        }

        [Test] public void UnlockingPoiyomiCopyRestoresEnabledDecals()
        {
            var shader = Shader.Find(".poiyomi/Poiyomi Toon");
            if (!shader) Assert.Ignore("Poiyomi is not installed.");
            var folder = Folder();
            string path = folder + "/Locked.shader";
            File.WriteAllText(path, "Shader \"Hidden/Locked/SurfaceRegression\" { Properties { _DecalEnabled (\"Enable decal\", Float) = 1 _MochieRoughnessMultiplier (\"Smoothness\", Float) = 1 } SubShader { Pass {} } }");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var original = Own(new Material(AssetDatabase.LoadAssetAtPath<Shader>(path)));
            original.SetOverrideTag("OriginalShader", shader.name);
            var copy = Own(new Material(original));
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.MakeEditable(copy);
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.DefaultRough(copy);
            Assert.IsTrue(copy.IsKeywordEnabled("GEOM_TYPE_BRANCH"), "The decal carrying the garment color must stay enabled.");
            Assert.AreEqual(0, copy.GetFloat("_MochieRoughnessMultiplier"));
            StringAssert.StartsWith("Hidden/Locked/", original.shader.name);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BundledPackedMapPreservesAuthoredPoiyomiLayout(bool locked)
        {
            var shader = Shader.Find(".poiyomi/Poiyomi Toon"); if (!shader) Assert.Ignore("Poiyomi is not installed.");
            var folder = Folder();
            if (locked)
            {
                string path = folder + "/LockedPacked.shader";
                File.WriteAllText(path, "Shader \"Hidden/Locked/PackedMapRegression\" { Properties { _MochieMetallicMaps (\"Packed Maps\", 2D) = \"white\" {} _MochieRoughnessMapInvert (\"Invert\", Float) = 1 _MochieRoughnessMultiplier (\"Smoothness\", Float) = 1 _MochieMetallicMapsRoughnessChannel (\"Channel\", Float) = 1 _MochieMetallicMultiplier (\"Metallic\", Float) = 0.3 } SubShader { Pass {} } }");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            }
            var material = Own(new Material(shader) { name = "Fishing cloth" });
            // The fishing outfit uses G with inversion despite this filename; opaque A is NOT smoothness.
            var packed = Png(folder, "Shirt_Pants_MetallicSmoothnessMaps", 2, (x, y) => new Color32(51, 230, 153, 255));
            material.SetTexture("_MochieMetallicMaps", packed);
            material.SetFloat("_MochieRoughnessMapInvert", 1);
            material.SetFloat("_MochieRoughnessMultiplier", .8f);
            material.SetFloat("_MochieMetallicMapsRoughnessChannel", 1);
            material.SetFloat("_MochieMetallicMultiplier", .3f);
            material.SetTextureScale("_MochieMetallicMaps", new Vector2(2, 3));
            var avatar = Avatar();
            var renderer = avatar.gameObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            var entry = new TextureEntry { texture = packed, role = "metallic", fileName = packed.name + ".png", material = material, property = "_MochieMetallicMaps" };
            TextureChanges.Apply(avatar, new List<TextureEntry> { entry }, folder + "/Batch");
            var result = renderer.sharedMaterial;
            Assert.AreSame(packed, result.GetTexture("_MochieMetallicMaps"));
            Assert.AreSame(shader, result.shader);
            Assert.AreEqual(1, result.GetFloat("_MochieRoughnessMapInvert"));
            Assert.AreEqual(1, result.GetFloat("_MochieMetallicMapsRoughnessChannel"));
            Assert.AreEqual(.8f, result.GetFloat("_MochieRoughnessMultiplier"));
            Assert.AreEqual(.3f, result.GetFloat("_MochieMetallicMultiplier"));
            Assert.AreEqual(new Vector2(2, 3), result.GetTextureScale("_MochieMetallicMaps"));
            Assert.IsFalse(Directory.Exists(folder + "/Batch/SurfaceMaps"), "No replacement packing should be generated.");
            TextureChanges.UndoLast(avatar); Assert.AreSame(material, renderer.sharedMaterial);
            TextureChanges.UndoLast(avatar); Assert.AreSame(packed, renderer.sharedMaterial.GetTexture("_MochieMetallicMaps"));
        }

        [Test] public void RoughnessPackingPreservesPoiyomiCustomMetallicAndMaskChannels()
        {
            var shader = Shader.Find(".poiyomi/Poiyomi Toon"); if (!shader) Assert.Ignore("Poiyomi is not installed.");
            var material = Own(new Material(shader)); var folder = Folder();
            var packed = Png(folder, "AuthoredPacked", 2, (x, y) => new Color32(51, 102, 153, 204));
            var rough = Png(folder, "Cloth_Roughness", 2, (x, y) => new Color32(64, 64, 64, 255));
            material.SetTexture("_MochieMetallicMaps", packed); material.SetFloat("_MochieMetallicMapsMetallicChannel", 2);
            material.SetFloat("_MochieMetallicMapsRoughnessChannel", 0); material.SetFloat("_MochieMetallicMapsReflectionMaskChannel", 1);
            material.SetFloat("_MochieMetallicMapsSpecularMaskChannel", 3); material.SetFloat("_MochieMetallicMultiplier", .3f);
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.Assign(material, "_MochieMetallicMaps", rough, "roughness", rough.name, folder);
            var p = SurfacePixel(material.GetTexture("_MochieMetallicMaps"));
            Assert.That(p.r, Is.EqualTo(.6f).Within(.015f)); Assert.That(p.g, Is.EqualTo(.75f).Within(.015f));
            Assert.That(p.b, Is.EqualTo(.4f).Within(.015f)); Assert.That(p.a, Is.EqualTo(.8f).Within(.015f));
            Assert.AreEqual(.3f, material.GetFloat("_MochieMetallicMultiplier"));
            Assert.AreEqual(0, material.GetFloat("_MochieMetallicMapsMetallicChannel"));
            Assert.IsTrue(material.IsKeywordEnabled("MOCHIE_PBR"));
        }

        [TestCase("orm", 204, 64, 153)]
        [TestCase("rma", 64, 153, 204)]
        [TestCase("mra", 153, 64, 204)]
        public void PackedRoughnessLayoutsUseTheCorrectChannels(string layout, int r, int g, int b)
        {
            var folder = Folder();
            var input = Png(folder, "Body_" + layout, 2, (x, y) => new Color32((byte)r, (byte)g, (byte)b, 255));
            var material = Standard("Body");
            Assert.AreEqual("metallic", TextureMatching.FileRole("Body_" + layout + ".png"));
            Orbiters.Toolkit.Editor.MaterialSurfaceMaps.Assign(material, "_MetallicGlossMap", input, "metallic", input.name + ".png", folder);
            var packed = SurfacePixel(material.GetTexture("_MetallicGlossMap"));
            Assert.That(packed.r, Is.EqualTo(.6f).Within(.015f)); Assert.That(packed.a, Is.EqualTo(.75f).Within(.015f));
            Assert.That(SurfacePixel(material.GetTexture("_OcclusionMap")).g, Is.EqualTo(.8f).Within(.015f));
        }

        [Test] public void RemovingEmissionIsScopedAndUndoRestoresTheOriginalMaterial()
        {
            var avatar = Avatar();
            var hoodie = Own(new GameObject("Hoodie")); hoodie.transform.SetParent(avatar.transform);
            var other = Own(new GameObject("Other garment")); other.transform.SetParent(avatar.transform);
            var worn = hoodie.AddComponent<MeshRenderer>();
            var neighbour = other.AddComponent<MeshRenderer>();
            var material = Own(new Material(Shader.Find("Standard")));
            var folder = Folder();
            var texture = Png(folder, "emission", 2, (x, y) => new Color32(255, 255, 255, 255));
            material.SetTexture("_EmissionMap", texture); material.SetColor("_EmissionColor", Color.white); material.EnableKeyword("_EMISSION");
            material.SetTexture("_BumpMap", texture);
            worn.sharedMaterial = neighbour.sharedMaterial = material;
            TextureChanges.RemoveSlot(avatar, hoodie.transform, material, "_EmissionMap", folder);
            Assert.AreSame(material, neighbour.sharedMaterial);
            Assert.AreSame(texture, material.GetTexture("_EmissionMap"));
            Assert.AreNotSame(material, worn.sharedMaterial);
            Assert.IsNull(worn.sharedMaterial.GetTexture("_EmissionMap"));
            Assert.False(worn.sharedMaterial.IsKeywordEnabled("_EMISSION"));
            Assert.AreEqual(Color.black, worn.sharedMaterial.GetColor("_EmissionColor"));
            Assert.AreSame(texture, worn.sharedMaterial.GetTexture("_BumpMap"));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.AreSame(material, worn.sharedMaterial);
            Undo.PerformRedo();
            Assert.IsNull(worn.sharedMaterial.GetTexture("_EmissionMap"));
            Undo.ClearUndo(worn);
        }

        private static GameObject Child(Component parent, string name) { var child = new GameObject(name); child.transform.SetParent(parent.transform, false); return child; }

        private Material Standard(string name) => Own(new Material(Shader.Find("Standard")) { name = name });

        [Test] public void BrokenShaderRepairPreservesMapsAndUvAndOnlyChangesTheSelectedItem()
        {
            var avatar = Avatar();
            var jacket = Child(avatar, "Material repair jacket").AddComponent<Orbiters.Toolkit.VRChat.OrbitersAttachment>();
            var worn = jacket.gameObject.AddComponent<MeshRenderer>();
            var other = Child(avatar, "Shared source neighbour").AddComponent<MeshRenderer>();
            var source = Standard("Broken import");
            string folder = Folder();
            var texture = Png(folder, "albedo", 2, (x, y) => new Color32(20, 40, 200, 255));
            source.SetTexture("_MainTex", texture); source.SetTexture("_BumpMap", texture);
            source.SetTextureScale("_MainTex", new Vector2(2, 3)); source.SetTextureOffset("_MainTex", new Vector2(.1f, .2f));
            source.shader = Shader.Find("Hidden/InternalErrorShader");
            worn.sharedMaterial = other.sharedMaterial = source;
            Assert.That(Orbiters.Toolkit.Editor.MaterialRepair.Reason(source), Is.Not.Null);
            Assert.That(AccessoryMaterials.Repair(avatar, jacket, folder), Is.EqualTo(1));
            var repaired = worn.sharedMaterial;
            Assert.That(repaired.shader.name, Is.EqualTo(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader));
            Assert.That(repaired.GetTexture("_MainTex"), Is.SameAs(texture));
            Assert.That(repaired.GetTexture("_BumpMap"), Is.SameAs(texture));
            Assert.That(repaired.GetTextureScale("_MainTex"), Is.EqualTo(new Vector2(2, 3)));
            Assert.That(repaired.GetTextureOffset("_MainTex"), Is.EqualTo(new Vector2(.1f, .2f)));
            Assert.That(repaired.IsKeywordEnabled("USE_NORMAL_MAPS"), Is.True);
            Assert.That(other.sharedMaterial, Is.SameAs(source));
            Assert.That(source.shader.name, Is.EqualTo("Hidden/InternalErrorShader"));
            Assert.That(AccessoryMaterials.Repair(avatar, jacket, folder), Is.Zero, "Repair is idempotent.");
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(worn.sharedMaterial, Is.SameAs(source));
            Undo.ClearUndo(worn); Undo.ClearUndo(other);
        }

        [Test] public void AuthoredWhiteEmissionIsNotMistakenForABrokenImport()
        {
            var source = Standard("Intentional glow");
            source.SetColor("_EmissionColor", Color.white); source.EnableKeyword("_EMISSION");
            Assert.That(Orbiters.Toolkit.Editor.MaterialRepair.Reason(source), Is.Null);
        }

        [Test] public void EmbeddedWhiteEmissionIsRebuiltWithVisibleAlbedoAndNoGlow()
        {
            string folder = Folder(), path = folder + "/Import.obj";
            File.WriteAllText(folder + "/Import.mtl", "newmtl Default\nKd 0.1 0.1 0.1\n");
            File.WriteAllText(path, "mtllib Import.mtl\no Import\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Default\nf 1 2 3\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();
            var source = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().First();
            source.shader = Shader.Find("Standard");
            source.SetColor("_Color", Color.gray * .2f);
            source.SetColor("_EmissionColor", Color.white); source.EnableKeyword("_EMISSION");
            var texture = Own(new Texture2D(2, 2)); source.SetTexture("_MainTex", texture);
            Assert.That(Orbiters.Toolkit.Editor.MaterialRepair.Reason(source), Is.Not.Null);
            var copy = Own(Orbiters.Toolkit.Editor.MaterialRepair.CreateToon(source));
            Assert.That(copy.GetTexture("_MainTex"), Is.SameAs(texture));
            Assert.That(copy.GetColor("_Color"), Is.EqualTo(Color.white));
            Assert.That(copy.GetColor("_EmissionColor"), Is.EqualTo(Color.black));
            Assert.That(source.GetColor("_EmissionColor"), Is.EqualTo(Color.white));
            source.SetColor("_EmissionColor", Color.red);
            Assert.That(Orbiters.Toolkit.Editor.MaterialRepair.Reason(source), Is.Null, "Colored glow is not the white import failure.");
        }

        [Test] public void RemovingToonNormalMapDisablesItsShaderFeature()
        {
            var avatar = Avatar();
            var jacket = Child(avatar, "Toon jacket");
            var worn = jacket.AddComponent<MeshRenderer>();
            var material = Own(new Material(Shader.Find(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader)));
            material.SetTexture("_BumpMap", Own(new Texture2D(2, 2)));
            Orbiters.Toolkit.Editor.MaterialRepair.ConfigureTextureFeatures(material);
            worn.sharedMaterial = material;
            TextureChanges.RemoveSlot(avatar, jacket.transform, material, "_BumpMap", Folder());
            Assert.That(worn.sharedMaterial.IsKeywordEnabled("USE_NORMAL_MAPS"), Is.False);
            Assert.That(material.IsKeywordEnabled("USE_NORMAL_MAPS"), Is.True);
            Undo.ClearUndo(worn);
        }

        private static IEnumerator Wait(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Task did not finish.");
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static void Match(List<TextureEntry> entries, List<TextureSlot> slots) =>
            TextureMatching.Match(entries, slots, new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());

        private TextureEntry Entry(string fileName) =>
            new TextureEntry { texture = Own(new Texture2D(4, 4) { name = Path.GetFileNameWithoutExtension(fileName) }), fileName = fileName, role = TextureMatching.FileRole(fileName) };

        // MA-7: data images already in the project keep their colour import settings; the drop uses a linear copy.
        [UnityTest] public IEnumerator ProjectDataTextureImportedAsColourIsCopiedAsLinearData()
        {
            string folder = Folder();
            var rough = Png(folder, "Body_Roughness", 4, (x, y) => new Color32(128, 128, 128, 255), uncompressed: false);
            var color = Png(folder, "Body_BaseColor", 4, (x, y) => new Color32(200, 90, 40, 255), uncompressed: false);
            string roughPath = AssetDatabase.GetAssetPath(rough);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(roughPath)).sRGBTexture, Is.True);
            string batch = "AuditTest-" + Guid.NewGuid().ToString("N");
            folders.Add("Assets/Orbiters/MyAvatar/Textures/" + batch);
            string root = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/') + "/";
            var import = TextureImport.ImportAsync(new[] { root + roughPath, root + AssetDatabase.GetAssetPath(color) }, "Assets/Orbiters/MyAvatar/" + batch, (_, __) => { }, CancellationToken.None);
            yield return Wait(import);
            string copied = AssetDatabase.GetAssetPath(import.Result[0].texture);
            Assert.That(copied, Is.Not.EqualTo(roughPath));
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(copied)).sRGBTexture, Is.False);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(roughPath)).sRGBTexture, Is.True);
            Assert.That(import.Result[1].texture, Is.SameAs(color));
        }

        // MA-8: near-opaque alpha and a cut-out smaller than a sample cell are real alpha; an opaque alpha channel is not.
        [UnityTest] public IEnumerator ProjectTexturesWithWrongTypesOrColourSpaceGetOwnedCopies()
        {
            string folder = Folder();
            foreach (var settings in new[] {
                (name: "Body_BaseColor", type: TextureImporterType.NormalMap, srgb: false, expectedSrgb: true),
                (name: "Hair_BaseColor", type: TextureImporterType.Default, srgb: false, expectedSrgb: true),
                (name: "Body_Roughness", type: TextureImporterType.NormalMap, srgb: false, expectedSrgb: false),
            })
            {
                var source = Png(folder, settings.name, 4, (x, y) => new Color32(128, 128, 255, 255));
                string path = AssetDatabase.GetAssetPath(source);
                var original = (TextureImporter)AssetImporter.GetAtPath(path);
                original.textureType = settings.type; original.sRGBTexture = settings.srgb; original.SaveAndReimport();
                string batch = "AuditTest-" + Guid.NewGuid().ToString("N");
                folders.Add("Assets/Orbiters/MyAvatar/Textures/" + batch);
                var import = TextureImport.ImportAsync(new[] { Path.GetFullPath(path) }, folder + "/" + batch, (_, __) => { }, CancellationToken.None);
                yield return Wait(import);
                string copyPath = AssetDatabase.GetAssetPath(import.Result.Single().texture);
                Assert.That(copyPath, Is.Not.EqualTo(path));
                var copied = (TextureImporter)AssetImporter.GetAtPath(copyPath);
                Assert.That(copied.textureType, Is.EqualTo(TextureImporterType.Default));
                Assert.That(copied.sRGBTexture, Is.EqualTo(settings.expectedSrgb));
                Assert.That(original.textureType, Is.EqualTo(settings.type));
                Assert.That(original.sRGBTexture, Is.EqualTo(settings.srgb));
            }
        }

        [Test] public void AlphaIsMeasuredOnEveryTexel()
        {
            string folder = Folder();
            var near = Png(folder, "Near", 1024, (x, y) => new Color32(200, 100, 50, 252));
            var cutout = Png(folder, "Cutout", 1024, (x, y) => new Color32(200, 100, 50, (byte)(x >= 500 && x < 502 && y >= 700 && y < 702 ? 0 : 255)));
            var opaque = Png(folder, "Opaque", 1024, (x, y) => new Color32(200, 100, 50, 255));
            var alpha = TextureOptimization.AlphaUsed(new[] { near, cutout, opaque }.Select(t => (t, (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)))).ToList());
            Assert.That(alpha[near], Is.True);
            Assert.That(alpha[cutout], Is.True);
            Assert.That(alpha[opaque], Is.False);
        }

        // MA-9: A → B, then B → C; Undo optimization brings back A without reporting a slot kept by hand.
        [Test] public void UndoAfterTwoOptimizationsRestoresTheFirstMaterial()
        {
            var avatar = Avatar();
            var renderer = avatar.gameObject.AddComponent<MeshRenderer>();
            Material a = Standard("A"), b = Standard("B"), c = Standard("C");
            renderer.sharedMaterial = c;
            avatar.optimization.swaps.Add(new MaterialSwap { renderer = renderer, index = 0, before = a, after = b });
            avatar.optimization.swaps.Add(new MaterialSwap { renderer = renderer, index = 0, before = b, after = c });
            Assert.That(TextureOptimization.Revert(avatar), Is.Null);
            Assert.That(renderer.sharedMaterial, Is.SameAs(a));
            Undo.ClearUndo(renderer);
        }

        // MA-10: a texture optimized in place outside the generated folders is part of the Save checkpoint.
        [Test] public void CheckpointIncludesImportSettingsChangedInPlace()
        {
            var texture = Png(Folder(), "Outside", 4, (x, y) => new Color32(1, 2, 3, 255));
            var avatar = Avatar();
            string path = AssetDatabase.GetAssetPath(texture);
            avatar.optimization.textures.Add(new OptimizedTexture { guid = AssetDatabase.AssetPathToGUID(path), applied = true });
            Assert.That(TextureChanges.CheckpointPaths(avatar), Does.Contain(path + ".meta"));
        }

        // MA-11: separate roughness is converted into packed smoothness, never assigned raw as metallic.
        [Test] public void RoughnessCanUseAConvertedPackedMetallicSlot()
        {
            var avatar = Avatar();
            avatar.gameObject.AddComponent<MeshRenderer>().sharedMaterial = Standard("Body");
            var slots = TextureMatching.Slots(avatar);
            var rough = Entry("Body_Roughness.png");
            Match(new List<TextureEntry> { rough }, slots);
            Assert.That(rough.property, Is.EqualTo("_MetallicGlossMap"));
            var packed = MyAvatarResults.MenuItems(rough, slots).Single(i => i.slot.property == "_MetallicGlossMap");
            Assert.IsFalse(packed.path.StartsWith("Packed metallic slots"));

            var metal = Entry("Body_Metallic.png");
            Match(new List<TextureEntry> { metal }, slots);
            Assert.That(metal.property, Is.EqualTo("_MetallicGlossMap"));
        }

        // MA-12: complementary halves of one atlas are not one layout, so an ambiguous image asks instead of going to both.
        [Test] public void ComplementaryAtlasHalvesAreNotOneLayout()
        {
            var slots = UvAvatar(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) }, new[] { new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 1) });
            var entries = new List<TextureEntry> { Entry("Character_BaseColor.png") };
            Match(entries, slots);
            Assert.That(entries.Count(e => e.material), Is.Zero);
            StringAssert.Contains("about as likely", entries[0].reason);
        }

        [Test] public void SubmeshesSharingTheirUvLayoutAreOneTarget()
        {
            var triangle = new[] { new Vector2(.1f, .1f), new Vector2(.9f, .1f), new Vector2(.1f, .9f) };
            var slots = UvAvatar(triangle, triangle);
            var entries = new List<TextureEntry> { Entry("Character_BaseColor.png") };
            Match(entries, slots);
            Assert.That(entries.Where(e => e.material).Select(e => e.material.name), Is.EquivalentTo(new[] { "Character_Face", "Character_Clothes" }));
        }

        // One renderer, two materials with their own atlas, one submesh each.
        private List<TextureSlot> UvAvatar(Vector2[] face, Vector2[] clothes)
        {
            var avatar = Avatar();
            var character = Child(avatar, "Character");
            var mesh = Own(new Mesh());
            mesh.SetVertices(face.Concat(clothes).Select(uv => (Vector3)uv).ToList());
            mesh.SetUVs(0, face.Concat(clothes).ToList());
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0); mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            character.AddComponent<MeshFilter>().sharedMesh = mesh;
            Material Atlas(string material, string texture) { var m = Standard(material); m.SetTexture("_MainTex", Own(new Texture2D(4, 4) { name = texture })); return m; }
            character.AddComponent<MeshRenderer>().sharedMaterials = new[] { Atlas("Character_Face", "FaceAtlas"), Atlas("Character_Clothes", "ClothesAtlas") };
            return TextureMatching.Slots(avatar);
        }

        // MA-13: the images of an accessory drop change the accessory's material copy, not other objects sharing the material.
        [Test] public void AccessoryTexturesLeaveOtherObjectsSharingTheirMaterial()
        {
            string folder = Folder();
            var texture = Png(folder, "Hat_BaseColor", 4, (x, y) => new Color32(10, 200, 30, 255));
            var avatar = Avatar();
            var shared = Standard("Shared");
            var old = Child(avatar, "OldHat").AddComponent<MeshRenderer>(); old.sharedMaterial = shared;
            var added = Child(avatar, "NewHat").AddComponent<MeshRenderer>(); added.sharedMaterial = shared;
            var entry = new TextureEntry { texture = texture, fileName = "Hat_BaseColor.png", role = "color", material = shared, property = "_MainTex" };
            Assert.That(TextureChanges.Apply(avatar, new List<TextureEntry> { entry }, folder + "/Batch", new List<Transform> { added.transform }), Is.EqualTo(1));
            Assert.That(old.sharedMaterial, Is.SameAs(shared));
            Assert.That(shared.GetTexture("_MainTex"), Is.Null);
            Assert.That(added.sharedMaterial, Is.Not.SameAs(shared));
            Assert.That(added.sharedMaterial.GetTexture("_MainTex"), Is.SameAs(texture));
            Assert.That(avatar.batchScope, Is.EqualTo(new[] { added.transform }));
            Assert.That(TextureChanges.Slots(avatar).Any(s => s.material == shared), Is.False);
            Undo.ClearUndo(old); Undo.ClearUndo(added); Undo.ClearUndo(avatar);
        }

        // MA-15: two materials named Body get two menu entries, told apart by their object.
        [Test] public void ScopedBatchSplitsAGeneratedMaterialSharedAfterItsFirstApply()
        {
            string folder = Folder();
            var baseMap = Png(folder, "Hat_BaseColor", 4, (x, y) => new Color32(10, 200, 30, 255));
            var emission = Png(folder, "Hat_Emission", 4, (x, y) => new Color32(200, 10, 30, 255));
            var avatar = Avatar();
            var original = Standard("Hat");
            var added = Child(avatar, "NewHat").AddComponent<MeshRenderer>(); added.sharedMaterial = original;
            var entries = new List<TextureEntry> {
                new TextureEntry { texture = baseMap, fileName = "Hat_BaseColor.png", role = "color", material = original, property = "_MainTex" },
                new TextureEntry { texture = emission, fileName = "Hat_Emission.png", role = "emission" },
            };
            string batch = folder + "/Batch";
            TextureChanges.Apply(avatar, entries, batch, new List<Transform> { added.transform });
            var sharedCopy = added.sharedMaterial;
            var other = Child(avatar, "AnotherHat").AddComponent<MeshRenderer>(); other.sharedMaterial = sharedCopy;
            entries[1].material = sharedCopy; entries[1].property = "_EmissionMap";
            Assert.That(TextureChanges.Apply(avatar, entries, batch), Is.EqualTo(1));
            Assert.That(other.sharedMaterial, Is.SameAs(sharedCopy));
            Assert.That(sharedCopy.GetTexture("_EmissionMap"), Is.Null);
            Assert.That(added.sharedMaterial, Is.Not.SameAs(sharedCopy));
            Assert.That(added.sharedMaterial.GetTexture("_MainTex"), Is.SameAs(baseMap));
            Assert.That(added.sharedMaterial.GetTexture("_EmissionMap"), Is.SameAs(emission));
            TextureChanges.UndoLast(avatar);
            Assert.That(added.sharedMaterial, Is.SameAs(original));
            Assert.That(other.sharedMaterial, Is.SameAs(sharedCopy));
            Undo.ClearUndo(added); Undo.ClearUndo(other);
        }

        [Test] public void AccessoryBundleKeepsExistingMapsButStillFillsMissingMaps()
        {
            var avatar = Avatar();
            var material = Standard("Jacket");
            avatar.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            var original = Entry("Jacket_BaseColor.png");
            material.mainTexture = original.texture;
            var replacement = Entry("Jacket_BaseColor_Blue.png");
            var normal = Entry("Jacket_Normal.png");
            var slots = TextureMatching.Slots(avatar);
            foreach (var slot in slots) slot.fillOnly = true;
            Match(new List<TextureEntry> { replacement, normal }, slots);
            Assert.IsNull(replacement.material, "bundled variants cannot replace a creator's existing map");
            Assert.AreSame(material, normal.material);
            Assert.AreEqual("_BumpMap", normal.property);
            Assert.AreSame(original.texture, material.mainTexture);
            Assert.False(TextureMatching.CanAutoAssign(replacement, slots.Single(s => s.property == "_EmissionMap")),
                "a bundled colour decal must not turn into emission instead");
            // A deliberate texture-only drop still replaces an existing colour map.
            Match(new List<TextureEntry> { replacement }, TextureMatching.Slots(avatar));
            Assert.AreSame(material, replacement.material);
            Assert.AreEqual("_MainTex", replacement.property);
        }

        [Test] public void LateAiCannotOverwriteRepairedAccessoryAlbedoButManualChoiceStillWorks()
        {
            string folder = Folder();
            var avatar = Avatar();
            var item = Child(avatar, "Jacket").AddComponent<Orbiters.Toolkit.VRChat.OrbitersAttachment>();
            var renderer = item.gameObject.AddComponent<MeshRenderer>();
            var original = Standard("Default");
            var albedo = Entry("Default_Base_color.png");
            var pattern = Entry("Pattern_Back_Base_Color.png");
            albedo.texture = Png(folder, "Default_Base_color", 4, (x, y) => new Color32(20, 40, 200, 255));
            pattern.texture = Png(folder, "Pattern_Back_Base_Color", 4, (x, y) => new Color32(255, 255, 255, 255));
            original.mainTexture = albedo.texture;
            original.shader = Shader.Find("Hidden/InternalErrorShader");
            renderer.sharedMaterial = original;
            AccessoryMaterials.Repair(avatar, item, folder);
            var repaired = renderer.sharedMaterial;
            Assert.AreSame(albedo.texture, repaired.mainTexture, "the repaired material starts with the creator's albedo");
            var slots = TextureMatching.Slots(avatar);
            foreach (var slot in slots) slot.fillOnly = true;
            var main = slots.Single(s => s.property == "_MainTex");
            var normal = Entry("Default_Normal.png");
            normal.texture = Png(folder, "Default_Normal", 4, (x, y) => new Color32(128, 128, 255, 255));
            normal.material = repaired; normal.property = "_BumpMap";
            var entries = new List<TextureEntry> { normal, pattern };
            string batch = folder + "/Batch";
            TextureChanges.Apply(avatar, entries, batch, new List<Transform> { item.transform });
            var before = TextureChanges.Capture(avatar, slots);
            var changes = new List<TextureChanges.Change> { new TextureChanges.Change { entry = pattern, slot = main, confidence = .99f, reason = "colour role" } };
            Assert.AreEqual(0, TextureChanges.Revise(avatar, entries, batch, changes, before));
            Assert.AreSame(albedo.texture, renderer.sharedMaterial.mainTexture);
            Assert.AreEqual(Orbiters.Toolkit.Editor.MaterialRepair.ToonShader, renderer.sharedMaterial.shader.name);
            Assert.AreSame(normal.texture, renderer.sharedMaterial.GetTexture("_BumpMap"));
            Assert.False(pattern.applied);
            pattern.material = renderer.sharedMaterial; pattern.property = "_MainTex"; pattern.reason = TextureMatching.ChosenReason;
            Assert.AreEqual(1, TextureChanges.Apply(avatar, entries, batch));
            Assert.AreSame(pattern.texture, renderer.sharedMaterial.mainTexture, "manual selection remains available");
        }

        [Test] public void BlankModelMaterialUsesItsUniqueAuthoredSetupWithoutLosingTransparency()
        {
            string folder = Folder(), path = folder + "/Import.obj";
            File.WriteAllText(folder + "/Import.mtl", "newmtl Decal\nKd 0.8 0.8 0.8\n");
            File.WriteAllText(path, "mtllib Import.mtl\no Import\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Decal\nf 1 2 3\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            importer.SaveAndReimport();
            var embedded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().Single();
            var authored = new Material(Shader.Find("Standard")) { name = "Decal" };
            authored.SetFloat("_Mode", 1); authored.EnableKeyword("_ALPHATEST_ON");
            authored.mainTexture = Png(folder, "DecalColor", 2, (x, y) => new Color32(100, 140, 200, 0));
            AssetDatabase.CreateAsset(authored, folder + "/Decal.mat");
            var avatar = Avatar();
            var item = Child(avatar, "Jacket").AddComponent<Orbiters.Toolkit.VRChat.OrbitersAttachment>();
            var renderer = item.gameObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = embedded;
            Assert.AreEqual(1, AccessoryMaterials.Repair(avatar, item, folder + "/Repaired"));
            Assert.AreNotSame(authored, renderer.sharedMaterial);
            Assert.AreEqual(1, renderer.sharedMaterial.GetFloat("_Mode"));
            Assert.True(renderer.sharedMaterial.IsKeywordEnabled("_ALPHATEST_ON"));
            Assert.AreSame(authored.mainTexture, renderer.sharedMaterial.mainTexture);
            Assert.IsNull(embedded.mainTexture);
            var ambiguous = new Material(authored) { name = "Decal" };
            AssetDatabase.CreateFolder(folder, "Alternative");
            AssetDatabase.CreateAsset(ambiguous, folder + "/Alternative/Decal.mat");
            Assert.IsNull(Orbiters.Toolkit.Editor.MaterialRepair.AuthoredReplacement(embedded), "ambiguous variants are not guessed");
        }

        [Test] public void TextureAiKeepsManualEditsOnTheGeneratedMaterial()
        {
            string folder = Folder();
            var texture = Png(folder, "Body_BaseColor", 4, (x, y) => new Color32(10, 200, 30, 255));
            var manual = Png(folder, "Manual_Emission", 4, (x, y) => new Color32(200, 10, 30, 255));
            var avatar = Avatar();
            var original = Standard("Body");
            var renderer = avatar.gameObject.AddComponent<MeshRenderer>(); renderer.sharedMaterial = original;
            var emissionSlot = TextureMatching.Slots(avatar).Single(s => s.property == "_EmissionMap");
            var pending = new TextureEntry { texture = texture, fileName = "Unresolved_Emission.png", role = "emission" };
            var entries = new List<TextureEntry> {
                new TextureEntry { texture = texture, fileName = "Body_BaseColor.png", role = "color", material = original, property = "_MainTex" }, pending,
            };
            string batch = folder + "/Batch";
            TextureChanges.Apply(avatar, entries, batch);
            var before = TextureChanges.Capture(avatar, new[] { emissionSlot });
            renderer.sharedMaterial.SetTexture("_EmissionMap", manual);
            int accepted = TextureChanges.Revise(avatar, entries, batch, new List<TextureChanges.Change> {
                new TextureChanges.Change { entry = pending, slot = emissionSlot, confidence = 1, reason = "fixture" },
            }, before);
            Assert.That(accepted, Is.Zero);
            Assert.That(renderer.sharedMaterial.GetTexture("_EmissionMap"), Is.SameAs(manual));
            Assert.That(pending.applied, Is.False);
            StringAssert.Contains("changed meanwhile", pending.reason);
            Undo.ClearUndo(renderer);
        }

        [Test] public void SameNamedMaterialsGetDistinctMenuEntries()
        {
            var avatar = Avatar();
            Child(avatar, "Top").AddComponent<MeshRenderer>().sharedMaterial = Standard("Body");
            Child(avatar, "Bottom").AddComponent<MeshRenderer>().sharedMaterial = Standard("Body");
            var items = MyAvatarResults.MenuItems(Entry("Body_BaseColor.png"), TextureMatching.Slots(avatar));
            var main = items.Where(i => i.slot.property == "_MainTex").ToList();
            Assert.That(main.Count, Is.EqualTo(2));
            Assert.That(main.Any(i => i.path.Contains("Top")) && main.Any(i => i.path.Contains("Bottom")), Is.True);
            Assert.That(items.Select(i => i.path).Distinct().Count(), Is.EqualTo(items.Count));
        }
    }
}
