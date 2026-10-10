using System;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Locomotion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class GoGoLocoTests
    {
        [TestCase("Packages/gogoloco/Runtime/GoGo/GoLoco/Prefabs/VRCFury/GogoLoco All (VRCFury).prefab", true)]
        [TestCase("Packages/gogoloco/Runtime/GoGo/GoLoco/Controllers/GoLocoBase.controller", true)]
        [TestCase("Assets/GoGo/GoLoco/Controllers/GoLocoBase.controller", true)]
        [TestCase("Packages/gogolocoextra/thing.asset", false)]
        [TestCase("Assets/MyGoGo/GoLocoBase.controller", false)]
        [TestCase("", false)]
        public void GoGoLocosFilesAreThoseOfItsPackageOrItsOlderUnitypackage(string path, bool expected) =>
            Assert.AreEqual(expected, GoGoLoco.IsGoGoLocoAsset(path));

        // A package's prefab on the avatar is found by the prefab it comes from, wherever it sits; a part of the avatar can be
        // left out (face tracking leaves out the template it set up itself).
        [Test]
        public void APackagesPrefabOnTheAvatarIsFound()
        {
            string folder = "Assets/__OrbitersPackageUseTest-" + Guid.NewGuid().ToString("N");
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
                var source = new GameObject("Locomotion");
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, folder + "/Locomotion.prefab");
                Object.DestroyImmediate(source);
                var avatar = new GameObject("Avatar");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar, scene);
                var armature = new GameObject("Armature").transform; armature.SetParent(avatar.transform, false);
                Assert.IsNull(PackageUse.Prefab(avatar.transform, p => p.StartsWith(folder + "/", StringComparison.Ordinal)));
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, armature);
                Assert.AreEqual(instance, PackageUse.Prefab(avatar.transform, p => p.StartsWith(folder + "/", StringComparison.Ordinal)));
                Assert.IsNull(PackageUse.Prefab(avatar.transform, p => p.StartsWith(folder + "/", StringComparison.Ordinal), skip: armature));
                Assert.IsNull(PackageUse.Prefab(avatar.transform, p => p.StartsWith("Packages/gogoloco/", StringComparison.Ordinal)), "Another package's prefab is not this one's");
                Assert.IsNull(PackageUse.Component(avatar.transform, p => p.StartsWith(folder + "/", StringComparison.Ordinal)), "No VRCFury or Modular Avatar component refers to it");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        // GoGoLoco 1.8.6 ships All (Base, Action and Sitting layers) and Beyond (with an FX layer and seats for flying), for
        // VRCFury and for Modular Avatar.
        [Test]
        public void TheInstalledVersionsAreReadFromTheirPrefabs()
        {
            if (!GoGoLoco.Installed) Assert.Ignore("GoGoLoco is not installed.");
            var variants = GoGoLoco.Variants(modularAvatar: false);
            Assert.GreaterOrEqual(variants.Count, 2);
            Assert.AreEqual("All", variants[0].Name);
            Assert.AreEqual("Beyond", variants[1].Name);
            var all = variants[0]; var beyond = variants[1];
            Assert.AreEqual(GoGoLoco.VrcFuryPrefab, all.Path);
            Assert.False(all.ModularAvatar);
            if (Orbiters.Toolkit.Editor.VRChat.VrcFury.Installed)
            {
                CollectionAssert.AreEquivalent(new[] { "Base", "Action", "Sitting" }, all.Layers);
                CollectionAssert.Contains(beyond.Layers, "FX", "Beyond adds an FX layer");
            }
            Assert.NotNull(all.Parameters);
            Assert.Greater(all.Bits, 0);
            Assert.Greater(beyond.Bits, all.Bits, "Beyond syncs its seat too");
            Assert.True(beyond.Parameters.parameters.Select(p => p.name).Contains("Go/Station/Chair"));
            Assert.That(beyond.Tagline, Does.Contain("flying"));

            var withModularAvatar = GoGoLoco.Variants(modularAvatar: true);
            CollectionAssert.AreEqual(new[] { "All", "Beyond" }, withModularAvatar.Take(2).Select(v => v.Name));
            Assert.True(withModularAvatar.All(v => v.ModularAvatar && v.Parameters != null), "Modular Avatar's prefabs share the menu's parameters");
            Assert.IsNull(GoGoLocoVariant.Read(GoGoLoco.PackageFolder + "/Runtime/GoGo/GoLoco/Prefabs/Beyond Prefab.prefab"), "a part of a version is not one");

            // Parameters the avatar already syncs are shared: most avatars sync VRCEmote.
            var scene = EditorSceneManager.NewPreviewScene();
            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            try
            {
                var avatar = new GameObject("Avatar");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar, scene);
                Assert.AreEqual(all.Bits, all.BitsOn(avatar.transform));
                parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "VRCEmote", valueType = VRCExpressionParameters.ValueType.Int, networkSynced = true } };
                avatar.AddComponent<VRCAvatarDescriptor>().expressionParameters = parameters;
                Assert.AreEqual(all.Bits - 8, all.BitsOn(avatar.transform));
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); Object.DestroyImmediate(parameters); }
        }

        [Test]
        public void SwitchingVersionsSwapsThePrefabInOneUndoStep()
        {
            if (!GoGoLoco.Installed) Assert.Ignore("GoGoLoco is not installed.");
            var variants = GoGoLoco.Variants(modularAvatar: false);
            var all = variants.Single(v => v.Name == "All"); var beyond = variants.Single(v => v.Name == "Beyond");
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var avatar = new GameObject("Avatar");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(avatar, scene);
                new GameObject("Armature").transform.SetParent(avatar.transform, false);
                var original = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(all.Path), avatar.transform);
                new GameObject("Hat").transform.SetParent(avatar.transform, false);
                original.name = "Locomotion";
                original.SetActive(false);
                Assert.AreEqual("All", GoGoLoco.Inspect(avatar.transform).Variant.Name);

                Assert.True(GoGoLoco.Switch(original, beyond, out string note), note);

                Assert.IsNull(note, "nothing was changed on the old prefab");
                Assert.True(original == null, "the old prefab goes");
                var report = GoGoLoco.Inspect(avatar.transform);
                Assert.AreEqual("Beyond", report.Variant.Name);
                var swapped = report.Prefab;
                Assert.AreEqual(1, swapped.transform.GetSiblingIndex(), "where the old one was");
                Assert.AreEqual("Locomotion", swapped.name, "a name you gave it stays");
                Assert.False(swapped.activeSelf);
                Assert.AreEqual(3, avatar.transform.childCount, "nothing left behind");
                Assert.AreEqual("Switch GoGoLoco to Beyond", Undo.GetCurrentGroupName());

                Undo.PerformUndo();
                report = GoGoLoco.Inspect(avatar.transform);
                Assert.AreEqual("All", report.Variant.Name, "one Undo step puts the old version back");
                Assert.AreEqual(3, avatar.transform.childCount);
                Assert.AreEqual("Locomotion", report.Prefab.name);

                Assert.True(GoGoLoco.Switch(report.Prefab, all, out note), "the version already on is kept");
                Assert.AreEqual("All", GoGoLoco.Inspect(avatar.transform).Variant.Name);
                foreach (var t in avatar.GetComponentsInChildren<Transform>(true)) Undo.ClearUndo(t.gameObject);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void GoGoLocoIsOfferedOnHumanoidAvatarsOnly()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var prop = new GameObject("Prop");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(prop, scene);
                Assert.IsFalse(GoGoLoco.Fits(prop.transform), "Not an avatar");
                prop.AddComponent<VRCAvatarDescriptor>();
                Assert.IsFalse(GoGoLoco.Fits(prop.transform), "No humanoid rig");
                Assert.IsFalse(GoGoLoco.Fits(null));
                Assert.IsFalse(GoGoLoco.Inspect(prop.transform).SetUp);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
