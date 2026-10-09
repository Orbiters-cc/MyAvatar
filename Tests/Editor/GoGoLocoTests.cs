using System;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Locomotion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
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
