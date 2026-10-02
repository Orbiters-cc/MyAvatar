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

        // MA-11: a roughness image is not a Standard metallic map; the menu offers that slot apart, as a packed slot.
        [Test] public void RoughnessIsNotPutInAPackedMetallicSlot()
        {
            var avatar = Avatar();
            avatar.gameObject.AddComponent<MeshRenderer>().sharedMaterial = Standard("Body");
            var slots = TextureMatching.Slots(avatar);
            var rough = Entry("Body_Roughness.png");
            Match(new List<TextureEntry> { rough }, slots);
            Assert.That(rough.material, Is.Null);
            StringAssert.Contains("No roughness slot", rough.reason);
            var packed = MyAvatarResults.MenuItems(rough, slots).Single(i => i.slot.property == "_MetallicGlossMap");
            StringAssert.StartsWith("Packed metallic slots", packed.path);

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
