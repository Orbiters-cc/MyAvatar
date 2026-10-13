using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Expressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class NovabeastOptionsTests
    {
        private readonly List<Object> owned = new List<Object>();
        private UnityEngine.SceneManagement.Scene scene;

        [SetUp]
        public void Open() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void Clean()
        {
            foreach (var o in owned) if (o != null) { Undo.ClearUndo(o); Object.DestroyImmediate(o); }
            owned.Clear();
            foreach (string folder in folders) AssetDatabase.DeleteAsset(folder);
            folders.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private readonly List<string> folders = new List<string>();

        // Novabeast's layout: "Hide…" toggles weighing clips in a direct blend tree, its breasts played by their slider, its
        // tail weighed by its slider; one body mesh.
        private sealed class Nova
        {
            public GameObject Root;
            public SkinnedMeshRenderer Body;
            public Transform Tail, Breast;
            public VRCExpressionParameters Parameters;
            public VRCExpressionsMenu Menu;
            public MyAvatar Settings;
        }

        private Nova Make(bool nested = false, string name = "Novabeast")
        {
            var nova = new Nova { Root = new GameObject(name) };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(nova.Root, scene);
            nova.Root.AddComponent<Animator>();
            nova.Settings = nova.Root.AddComponent<MyAvatar>();
            nova.Body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>(); nova.Body.transform.SetParent(nova.Root.transform, false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } }; owned.Add(mesh);
            foreach (var shape in new[] { "hideMane", "breasts" }) mesh.AddBlendShapeFrame(shape, 100, new Vector3[3], null, null);
            nova.Body.sharedMesh = mesh;
            var hips = new GameObject("hips").transform; hips.SetParent(new GameObject("skeleton").transform); hips.parent.SetParent(nova.Root.transform, false);
            nova.Tail = new GameObject("tailParent").transform; nova.Tail.SetParent(hips, false);
            nova.Breast = new GameObject("breast1_l").transform; nova.Breast.SetParent(hips, false);

            var hide = Clip(("Body", typeof(SkinnedMeshRenderer), "blendShape.hideMane", 100f, 100f));
            var tail = Clip(("skeleton/hips/tailParent", typeof(Transform), "m_LocalScale.x", .68f, .68f), ("skeleton/hips/tailParent", typeof(Transform), "m_LocalScale.y", .68f, .68f));
            var breasts = Clip(("skeleton/hips/breast1_l", typeof(Transform), "m_LocalScale.x", .75f, 1.25f), ("Body", typeof(SkinnedMeshRenderer), "blendShape.breasts", 100f, 100f));
            var fx = new AnimatorController(); owned.Add(fx);
            foreach (var p in new[] { "HideMane", "TailSize", "Breasts" }) fx.AddParameter(p, AnimatorControllerParameterType.Float);
            var control = Layer(fx, "Control Tree");
            var tree = new BlendTree { name = "Blend Tree", blendType = BlendTreeType.Direct }; owned.Add(tree);
            tree.children = new[] { new ChildMotion { motion = hide, directBlendParameter = "HideMane", timeScale = 1 }, new ChildMotion { motion = tail, directBlendParameter = "TailSize", timeScale = 1 } };
            control.AddState("Control Tree").motion = tree;
            var layer = Layer(fx, "Breasts");
            // Some avatars keep a layer's states in a sub-state machine.
            if (nested) layer = layer.AddStateMachine("Breasts");
            var off = layer.AddState("Breasts Off");
            var on = layer.AddState("Breasts On"); on.motion = breasts; on.timeParameterActive = true; on.timeParameter = "Breasts";
            off.AddTransition(on).AddCondition(AnimatorConditionMode.Greater, .01f, "Breasts");
            on.AddTransition(off).AddCondition(AnimatorConditionMode.Less, .01f, "Breasts");

            nova.Parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>(); owned.Add(nova.Parameters);
            nova.Parameters.parameters = new[]
            {
                new VRCExpressionParameters.Parameter { name = "HideMane", valueType = VRCExpressionParameters.ValueType.Bool, saved = true, networkSynced = true },
                new VRCExpressionParameters.Parameter { name = "Breasts", valueType = VRCExpressionParameters.ValueType.Float, saved = true, networkSynced = true },
                new VRCExpressionParameters.Parameter { name = "TailSize", valueType = VRCExpressionParameters.ValueType.Float, saved = true, networkSynced = true },
            };
            nova.Menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>(); owned.Add(nova.Menu);
            nova.Menu.controls = new List<VRCExpressionsMenu.Control>
            {
                MenuFixture.Control("Remove Mane", ControlType.Toggle, "HideMane"), MenuFixture.Control("Breasts", ControlType.RadialPuppet, null, sub: "Breasts"),
                MenuFixture.Control("Tail Size", ControlType.RadialPuppet, null, sub: "TailSize"),
            };
            var descriptor = nova.Root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customExpressions = true; descriptor.expressionsMenu = nova.Menu; descriptor.expressionParameters = nova.Parameters;
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
            return nova;
        }

        [Test]
        public void ItsPartsAreFoundByTheirHideToggleAndAHiddenOneStaysHiddenAtUpload()
        {
            var nova = Make();
            var options = NovabeastOptions.Find(nova.Root);
            CollectionAssert.AreEqual(new[] { "Mane" }, options.Parts.Select(p => p.Label));
            Assert.True(options.Breasts != null && options.Breasts.Timed);
            Assert.True(options.Tail != null && !options.Tail.Timed);
            var mane = options.Parts[0];
            Assert.True(NovabeastOptions.Shown(options, mane));

            NovabeastOptions.SetShown(options, mane, false);

            Assert.False(NovabeastOptions.Shown(options, mane));
            Assert.AreEqual(100f, nova.Body.GetBlendShapeWeight(0), "the scene shows it hidden");
            Assert.AreEqual(1, nova.Settings.lookRest.Count, "what its animation starts from is kept");

            MenuUploadBuild.Upload(nova.Root);

            Assert.False(nova.Menu.controls.Any(c => c.name == "Remove Mane"), "it stays hidden: its toggle is left out");
            Assert.False(nova.Parameters.parameters.Any(p => p.name == "HideMane"), "its synced bit is free");
        }

        [Test]
        public void ShowingAPartAgainForgetsWhatItsPreviewKept()
        {
            var nova = Make();
            var options = NovabeastOptions.Find(nova.Root);
            NovabeastOptions.SetShown(options, options.Parts[0], false);
            NovabeastOptions.SetShown(options, options.Parts[0], true);
            Assert.AreEqual(0f, nova.Body.GetBlendShapeWeight(0));
            Assert.AreEqual(0, nova.Settings.lookRest.Count);
        }

        [Test]
        public void SlidersShowInTheSceneAndTheUploadStartsFromTheAuthoredPose()
        {
            var nova = Make();
            var options = NovabeastOptions.Find(nova.Root);

            NovabeastOptions.SetValue(options, options.Tail, .5f);
            NovabeastOptions.SetValue(options, options.Breasts, .5f);

            Assert.AreEqual(.84f, nova.Tail.localScale.x, 1e-4f, "half the tail size: half way to the clip");
            Assert.AreEqual(1f, nova.Breast.localScale.x, 1e-4f, "breasts at half their slider: their clip half played");
            Assert.AreEqual(100f, nova.Body.GetBlendShapeWeight(1));
            Assert.AreEqual(.5f, nova.Parameters.parameters.Single(p => p.name == "TailSize").defaultValue, "VRChat starts the slider there");
            Assert.True(nova.Menu.controls.Any(c => c.name == "Tail Size"), "the slider stays in the menu");

            new LookPreviews.Restore().OnPreprocessAvatar(nova.Root);

            Assert.AreEqual(1f, nova.Tail.localScale.x, 1e-4f, "the upload starts from the authored pose, the slider's default does the rest");
            Assert.AreEqual(1f, nova.Breast.localScale.x, 1e-4f);
            Assert.AreEqual(0f, nova.Body.GetBlendShapeWeight(1));
        }

        [Test]
        public void StatesInASubStateMachineAreFound()
        {
            var nova = Make(nested: true);
            var options = NovabeastOptions.Find(nova.Root);
            Assert.True(options.Breasts != null && options.Breasts.Timed);
        }

        [Test]
        public void AGoneParameterReadsAsItsDefaultInsteadOfThrowing()
        {
            var nova = Make();
            var options = NovabeastOptions.Find(nova.Root);
            nova.Parameters.parameters = new VRCExpressionParameters.Parameter[0];
            Assert.True(NovabeastOptions.Shown(options, options.Parts[0]));
            Assert.AreEqual(0f, NovabeastOptions.Value(options, options.Tail));
            Assert.DoesNotThrow(() => NovabeastOptions.SetValue(options, options.Tail, .5f));
        }

        // The package's other prefab (another variant) uses the same parameters: this avatar gets its own copy, and the
        // variant keeps its starting values.
        [Test]
        public void ParametersSharedWithAnotherVariantAreCopiedForThisAvatarFirst()
        {
            string id = System.Guid.NewGuid().ToString("N");
            string package = "Assets/__NovaShareTest-" + id;
            AssetDatabase.CreateFolder("Assets", "__NovaShareTest-" + id); folders.Add(package);
            var nova = Make(name: "NovaShareTest-" + id);
            string copies = MenuSave.CopiesFolder + "/" + nova.Root.name;
            // The copy's folder goes, and every folder above it the test made (Assets/Orbiters... in a project without them).
            string made = copies;
            for (string parent = System.IO.Path.GetDirectoryName(made).Replace('\\', '/'); parent != "Assets" && !AssetDatabase.IsValidFolder(parent);
                 parent = System.IO.Path.GetDirectoryName(parent).Replace('\\', '/'))
                made = parent;
            folders.Add(made);
            AssetDatabase.CreateAsset(nova.Parameters, package + "/Parameters.asset");
            owned.Remove(nova.Parameters);
            var variant = new GameObject("Variant");
            variant.AddComponent<VRCAvatarDescriptor>().expressionParameters = nova.Parameters;
            PrefabUtility.SaveAsPrefabAsset(variant, package + "/Variant.prefab");
            Object.DestroyImmediate(variant);
            var options = NovabeastOptions.Find(nova.Root);
            var shared = nova.Parameters;

            NovabeastOptions.SetValue(options, options.Tail, .5f);

            var descriptor = nova.Root.GetComponent<VRCAvatarDescriptor>();
            Assert.AreNotEqual(shared, descriptor.expressionParameters, "the avatar uses its own copy");
            StringAssert.StartsWith(copies + "/", AssetDatabase.GetAssetPath(descriptor.expressionParameters), "with the menu editor's copies of this avatar");
            Assert.AreEqual(.5f, descriptor.expressionParameters.parameters.Single(p => p.name == "TailSize").defaultValue);
            Assert.AreEqual(0f, shared.parameters.Single(p => p.name == "TailSize").defaultValue, "the variant's starting value stays");

            NovabeastOptions.SetValue(options, options.Tail, .25f);
            Assert.AreEqual(.25f, descriptor.expressionParameters.parameters.Single(p => p.name == "TailSize").defaultValue, "its own copy changes in place now");
            Assert.AreEqual(1, AssetDatabase.FindAssets("t:VRCExpressionParameters", new[] { copies }).Length, "one copy");
        }

        private AnimatorStateMachine Layer(AnimatorController fx, string name)
        {
            var machine = new AnimatorStateMachine { name = name }; owned.Add(machine);
            fx.AddLayer(new AnimatorControllerLayer { name = name, stateMachine = machine, defaultWeight = 1 });
            return machine;
        }

        private AnimationClip Clip(params (string path, System.Type type, string property, float from, float to)[] curves)
        {
            var clip = new AnimationClip(); owned.Add(clip);
            foreach (var (path, type, property, from, to) in curves)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Linear(0, from, 1f / 6, to));
            return clip;
        }
    }
}
