using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // What makes the optimization estimate measure again: a package or SDK update and component settings do; scripts, style
    // sheets, transforms and what editor tools show on the avatar don't.
    public sealed class OptimizationKeyTests
    {
        // MAF-22: texture plans kept across a script reload come back as they were, and not at all once a texture is gone.
        [Test] public void TexturePlansKeptAcrossReloadsComeBackWhole()
        {
            var texture = new Texture2D(8, 8);
            try
            {
                var plan = new TextureOptimization.Plan { before = 1000, after = 400, sharpFrom = 1.5f, mobile = true };
                plan.changes.Add(new TextureOptimization.TexturePlan { texture = texture, path = "Assets/A.png", format = TextureFormat.BC7, width = 4, height = 2, quality = 3, before = 900, after = 300, linear = true, normal = true });
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(Optimization.OptimizationEstimate.SavedPlan.Of(plan));
                var back = Newtonsoft.Json.JsonConvert.DeserializeObject<Optimization.OptimizationEstimate.SavedPlan>(json).Plan();
                Assert.That(Newtonsoft.Json.JsonConvert.SerializeObject(Optimization.OptimizationEstimate.SavedPlan.Of(back)), Is.EqualTo(json));
                Assert.That(back.changes[0].texture, Is.SameAs(texture));
                Object.DestroyImmediate(texture);
                Assert.That(Newtonsoft.Json.JsonConvert.DeserializeObject<Optimization.OptimizationEstimate.SavedPlan>(json).Plan(), Is.Null, "its texture is gone");
            }
            finally { if (texture != null) Object.DestroyImmediate(texture); }
        }

        [Test] public void PackageUpdatesCountScriptsDoNot()
        {
            Assert.That(TextureOptimization.ImportMatters("Packages/com.vrchat.avatars/package.json"), Is.True);
            Assert.That(TextureOptimization.ImportMatters("Packages/com.vrchat.avatars/Runtime/VRCSDK/Plugins/VRCSDK3A.dll"), Is.True);
            Assert.That(TextureOptimization.ImportMatters("Packages/orbiters.toolkit/Editor/Orbiters.Toolkit.Editor.asmdef"), Is.True);
            Assert.That(TextureOptimization.ImportMatters("Assets/Avatar/Body.png"), Is.True);
            Assert.That(TextureOptimization.ImportMatters("Assets/Scripts/Tool.cs"), Is.False);
            Assert.That(TextureOptimization.ImportMatters("Packages/orbiters.myavatar/Editor/UI/myavatar.uss"), Is.False);
            Assert.That(TextureOptimization.ImportMatters("Assets/Thry/settings.json"), Is.False);
        }

        [Test] public void OnlySettingsThatMayChangeTheUploadCount()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Avatar");
                SceneManager.MoveGameObjectToScene(root, scene);
                var collider = root.AddComponent<BoxCollider>();
                Assert.That(Optimization.OptimizationEstimate.SettingsMatter(root.transform), Is.False, "Moving objects isn't a setting.");
                Assert.That(Optimization.OptimizationEstimate.SettingsMatter(collider), Is.False, "The copies estimates and previews work on.");
                var helper = new GameObject("Bones") { hideFlags = HideFlags.HideAndDontSave };
                Assert.That(TextureOptimization.EditorHelper(helper.AddComponent<SkinnedMeshRenderer>()), Is.True, "What an editor tool shows on the avatar.");
                Assert.That(TextureOptimization.EditorHelper(collider), Is.False);
                Object.DestroyImmediate(helper);
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        // A setting edited counts for the avatars whose upload holds it only: a light or another avatar measures nothing again.
        [Test] public void SettingsCountForTheAvatarsHoldingThem()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Avatar");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.AddComponent<MyAvatar>();
                var hair = new GameObject("Hair");
                hair.transform.SetParent(root.transform, false);
                var light = new GameObject("Light");
                SceneManager.MoveGameObjectToScene(light, scene);
                Assert.That(Optimization.OptimizationEstimate.RootsOf(hair.transform), Is.EqualTo(new[] { root }));
                Assert.That(Optimization.OptimizationEstimate.RootsOf(root.transform), Is.EqualTo(new[] { root }));
                Assert.That(Optimization.OptimizationEstimate.RootsOf(light.transform), Is.Empty);
                var seen = new System.Collections.Generic.HashSet<Transform>();
                Assert.That(Optimization.OptimizationEstimate.RootsOf(hair.transform, seen), Is.EqualTo(new[] { root }));
                Assert.That(Optimization.OptimizationEstimate.RootsOf(root.transform, seen), Is.Empty, "Found once per change stream.");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
