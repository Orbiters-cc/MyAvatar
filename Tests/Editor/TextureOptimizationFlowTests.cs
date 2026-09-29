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
    // Optimizes a throwaway avatar in an additive scene: an unshared texture changes in place, a texture another avatar uses
    // is copied for this avatar only, and both Undo optimization and Unity's Undo restore everything.
    public sealed class TextureOptimizationFlowTests
    {
        private Scene scene;
        private bool ownScene;
        private GameObject mine, theirs;
        private string folder, generated;
        private readonly List<string> extraGenerated = new List<string>();
        private MyAvatar avatar;
        private Material own, other;
        private Texture2D opaque, cutout;
        private MeshRenderer renderer;

        [SetUp] public void SetUp()
        {
            // The test runner's own untitled scene when there is one: an additive scene cannot sit beside it.
            scene = SceneManager.GetActiveScene();
            ownScene = !string.IsNullOrEmpty(scene.path);
            if (ownScene) scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            folder = "Assets/Orbiters/MyAvatar/OptimizationTest-" + Guid.NewGuid().ToString("N"); generated = null;
            TextureImport.EnsureFolder(folder);
            opaque = Image("Body_BaseColor", 1024, false);
            cutout = Image("Lace", 1024, true);
            var descriptor = Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A");
            GameObject Avatar(string name)
            {
                var root = new GameObject(name); SceneManager.MoveGameObjectToScene(root, scene);
                if (descriptor != null) root.AddComponent(descriptor);
                return root;
            }
            mine = Avatar("Mine");
            avatar = mine.AddComponent<MyAvatar>();
            own = new Material(Shader.Find("Standard")) { name = "Own" };
            own.SetTexture("_MainTex", cutout); own.SetTexture("_EmissionMap", opaque);
            renderer = mine.AddComponent<MeshRenderer>(); renderer.sharedMaterial = own;
            // Marks the texture set as dropped and placed, which is what offers Quick optimization.
            avatar.textures.Add(new TextureEntry { texture = opaque, applied = true, material = own, property = "_EmissionMap" });
            theirs = Avatar("Theirs");
            other = new Material(Shader.Find("Standard")) { name = "Other" };
            other.SetTexture("_MainTex", cutout);
            theirs.AddComponent<MeshRenderer>().sharedMaterial = other;
        }

        [TearDown] public void TearDown()
        {
            if (avatar) Undo.ClearUndo(avatar);
            if (renderer) Undo.ClearUndo(renderer);
            if (mine) UnityEngine.Object.DestroyImmediate(mine);
            if (theirs) UnityEngine.Object.DestroyImmediate(theirs);
            if (ownScene) EditorSceneManager.CloseScene(scene, true);
            if (own) UnityEngine.Object.DestroyImmediate(own);
            if (other) UnityEngine.Object.DestroyImmediate(other);
            if (!string.IsNullOrEmpty(generated) && AssetDatabase.IsValidFolder(generated)) AssetDatabase.DeleteAsset(generated);
            foreach (var path in extraGenerated) if (AssetDatabase.IsValidFolder(path)) AssetDatabase.DeleteAsset(path);
            extraGenerated.Clear();
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
        }

        private Texture2D Image(string name, int size, bool alpha)
        {
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32((byte)(i % 251), (byte)(i / size % 253), 90, (byte)(alpha && i % size < size / 2 ? 0 : 255));
            image.SetPixels32(pixels); image.Apply();
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureCompression = TextureImporterCompression.Uncompressed; importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static IEnumerator Wait(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 60;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Task did not finish.");
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static TextureImporterPlatformSettings Standalone(Texture texture) =>
            ((TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture))).GetPlatformTextureSettings(TextureOptimization.Platform);

        [UnityTest] public IEnumerator OptimizesInPlaceCopiesSharedTexturesAndUndoes()
        {
            if (Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A") == null) Assert.Ignore("VRChat avatar SDK not installed.");
            var plan = TextureOptimization.Build(avatar);
            Assert.AreEqual(2, plan.changes.Count);
            Assert.AreEqual(TextureImporterFormat.DXT1, plan.changes.Single(c => c.texture == opaque).format);
            Assert.AreEqual(TextureImporterFormat.BC7, plan.changes.Single(c => c.texture == cutout).format);
            Assert.Less(plan.after, plan.before);

            yield return Wait(TextureOptimization.OptimizeAsync(avatar, (_, __) => { }, CancellationToken.None));
            generated = avatar.optimization.folder;
            var settings = Standalone(opaque);
            Assert.IsTrue(settings.overridden);
            Assert.AreEqual(TextureImporterFormat.DXT1, settings.format);
            Assert.AreEqual(512, settings.maxTextureSize);
            Assert.IsFalse(settings.crunchedCompression);
            Assert.AreEqual(512, AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(opaque)).width);
            // The shared texture stays as it is; this avatar alone uses an optimized copy through a material copy.
            Assert.IsFalse(Standalone(cutout).overridden);
            var copy = renderer.sharedMaterial;
            Assert.AreNotEqual(own, copy);
            var duplicate = copy.GetTexture("_MainTex");
            Assert.AreNotEqual(cutout, duplicate);
            Assert.AreEqual(TextureImporterFormat.BC7, Standalone(duplicate).format);
            Assert.AreEqual(opaque, copy.GetTexture("_EmissionMap"));
            Assert.AreEqual(cutout, other.GetTexture("_MainTex"));
            Assert.AreEqual(2, TextureOptimization.Changed(avatar.optimization));

            Assert.IsNull(TextureOptimization.Revert(avatar));
            Assert.AreEqual(own, renderer.sharedMaterial);
            Assert.IsFalse(Standalone(opaque).overridden);
            Assert.AreEqual(1024, AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(opaque)).width);
            Assert.IsFalse(TextureOptimization.Applied(avatar.optimization));

            // Unity's Undo of the undo re-applies it, importers included.
            Undo.PerformUndo();
            for (int i = 0; i < 5; i++) yield return null;
            Assert.AreEqual(copy, renderer.sharedMaterial);
            Assert.IsTrue(Standalone(opaque).overridden);
            Undo.PerformUndo();
            for (int i = 0; i < 5; i++) yield return null;
            Assert.AreEqual(own, renderer.sharedMaterial);
            Assert.IsFalse(Standalone(opaque).overridden);
        }

        [Test] public void EditedImportSettingsAreKept()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(opaque));
            var original = TextureOptimization.Read(importer);
            var entry = new OptimizedTexture { guid = AssetDatabase.AssetPathToGUID(importer.assetPath), applied = false, before = original,
                after = TextureOptimization.Target(TextureImporterFormat.DXT1, 512) };
            importer.maxTextureSize = 256; importer.mipmapEnabled = false; importer.SaveAndReimport();
            Assert.AreEqual(1, TextureOptimization.Reconcile(new[] { entry }));
            Assert.IsFalse(importer.mipmapEnabled);
        }

        [UnityTest] public IEnumerator TwoRealOptimizationsRestoreTheOriginalMaterialAndKeepOtherAvatar()
        {
            if (Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A") == null) Assert.Ignore("VRChat avatar SDK not installed.");
            yield return Wait(TextureOptimization.OptimizeAsync(avatar, (_, __) => { }, CancellationToken.None));
            generated = avatar.optimization.folder;
            var firstCopy = renderer.sharedMaterial;
            Assert.That(firstCopy, Is.Not.SameAs(own));

            // A new texture set brings another shared image to the material created by the previous optimization.
            var nextShared = Image("New_Emission", 1024, false);
            firstCopy.SetTexture("_EmissionMap", nextShared);
            other.SetTexture("_EmissionMap", nextShared);
            avatar.batchFolder = folder + "/SecondDrop";
            yield return Wait(TextureOptimization.OptimizeAsync(avatar, (_, __) => { }, CancellationToken.None));
            extraGenerated.Add(avatar.optimization.folder);
            Assert.That(renderer.sharedMaterial, Is.Not.SameAs(firstCopy));
            Assert.That(avatar.optimization.swaps.Count, Is.EqualTo(2));
            Assert.That(other.GetTexture("_EmissionMap"), Is.SameAs(nextShared));
            Assert.That(Standalone(nextShared).overridden, Is.False);

            Assert.That(TextureOptimization.Revert(avatar), Is.Null);
            Assert.That(renderer.sharedMaterial, Is.SameAs(own));
            Assert.That(other.GetTexture("_MainTex"), Is.SameAs(cutout));
            Assert.That(other.GetTexture("_EmissionMap"), Is.SameAs(nextShared));
            Assert.That(Standalone(cutout).overridden, Is.False);
            Assert.That(Standalone(nextShared).overridden, Is.False);
        }
    }
}
