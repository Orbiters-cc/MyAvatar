using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class RexouiumOptionsTests
    {
        private readonly List<Object> owned = new List<Object>();
        private Scene scene;

        [SetUp]
        public void Open() => scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

        [TearDown]
        public void Clean()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
        }

        [Test]
        public void FeatherTogglesAreFoundByTheirSavedOffParameterAndShownByItsDefault()
        {
            var (root, parameters, _) = Avatar();

            var options = RexouiumOptions.Find(root);

            Assert.True(options.Any);
            CollectionAssert.AreEqual(new[] { "Head", "Wrist" }, options.Feathers.Select(f => f.Label));
            var head = options.Feathers[0];
            CollectionAssert.AreEqual(new[] { "Feathers/Head" }, head.Objects);
            CollectionAssert.AreEqual(new[] { ("Body", "HeadFeathersFlat") }, head.Shapes);
            Assert.True(RexouiumOptions.Shown(options, head));

            RexouiumOptions.SetShown(options, head, false);

            Assert.False(RexouiumOptions.Shown(options, head));
            Assert.AreEqual(1f, parameters.parameters.First(p => p.name == "FthrsHeadOff").defaultValue, "VRChat starts the avatar with them hidden");
            Assert.False(root.transform.Find("Feathers/Head").gameObject.activeSelf);
            var body = root.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            Assert.AreEqual(100f, body.GetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex("HeadFeathersFlat")));

            Undo.PerformUndo();

            Assert.True(RexouiumOptions.Shown(options, head));
            Assert.True(root.transform.Find("Feathers/Head").gameObject.activeSelf);
            Assert.AreEqual(0f, body.GetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex("HeadFeathersFlat")));
        }

        [Test]
        public void EarSizeGoesFromTheSmallShapeToTheBigOne()
        {
            var (root, _, body) = Avatar();
            body.SetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex("EarsSmall"), 52);
            var options = RexouiumOptions.Find(root);

            Assert.AreSame(body, options.Ears);
            Assert.AreEqual(-52f, RexouiumOptions.EarSize(options));

            RexouiumOptions.SetEarSize(options, 30);

            Assert.AreEqual(30f, body.GetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex("EarsBig")));
            Assert.AreEqual(0f, body.GetBlendShapeWeight(body.sharedMesh.GetBlendShapeIndex("EarsSmall")));
        }

        [Test]
        public void AnAvatarWithoutThemHasNoOptions()
        {
            var root = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<VRCAvatarDescriptor>();
            Assert.False(RexouiumOptions.Find(root).Any);
        }

        // Rexouium's layout: feather groups each with a saved "Fthrs…Off" toggle whose on state hides them, ear shapes on the body.
        private (GameObject root, VRCExpressionParameters parameters, SkinnedMeshRenderer body) Avatar()
        {
            var root = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            var body = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
            body.transform.SetParent(root.transform, false);
            body.sharedMesh = Mesh("EarsSmall", "EarsBig", "HeadFeathersFlat");
            var feathers = new GameObject("Feathers").transform;
            feathers.SetParent(root.transform, false);
            foreach (var name in new[] { "Head", "Wrist" }) new GameObject(name).transform.SetParent(feathers, false);

            var fx = new AnimatorController(); owned.Add(fx);
            Toggle(fx, "FthrsHeadOff", Clip(("Feathers/Head", typeof(GameObject), "m_IsActive", 0f), ("Body", typeof(SkinnedMeshRenderer), "blendShape.HeadFeathersFlat", 100f)));
            Toggle(fx, "FthrWristOff", Clip(("Feathers/Wrist", typeof(GameObject), "m_IsActive", 0f)));
            // A toggle that hides nothing is not a feather group, and other toggles are not feathers.
            Toggle(fx, "FthrsGhostOff", Clip());
            Toggle(fx, "HatOff", Clip(("Feathers/Head", typeof(GameObject), "m_IsActive", 0f)));

            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>(); owned.Add(parameters);
            parameters.parameters = new[] { "FthrsHeadOff", "FthrWristOff", "FthrsGhostOff", "HatOff" }
                .Select(n => new VRCExpressionParameters.Parameter { name = n, valueType = VRCExpressionParameters.ValueType.Bool, saved = true }).ToArray();

            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
            descriptor.expressionParameters = parameters;
            return (root, parameters, body);
        }

        private void Toggle(AnimatorController fx, string parameter, AnimationClip off)
        {
            fx.AddParameter(parameter, AnimatorControllerParameterType.Bool);
            var machine = new AnimatorStateMachine { name = parameter }; owned.Add(machine);
            fx.AddLayer(new AnimatorControllerLayer { name = parameter, stateMachine = machine, defaultWeight = 1 });
            var shown = machine.AddState("Shown");
            var hidden = machine.AddState("Hidden");
            hidden.motion = off;
            shown.AddTransition(hidden).AddCondition(AnimatorConditionMode.If, 0, parameter);
            hidden.AddTransition(shown).AddCondition(AnimatorConditionMode.IfNot, 0, parameter);
        }

        private AnimationClip Clip(params (string path, System.Type type, string property, float value)[] curves)
        {
            var clip = new AnimationClip(); owned.Add(clip);
            foreach (var (path, type, property, value) in curves)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, type, property), AnimationCurve.Constant(0, 1, value));
            return clip;
        }

        private Mesh Mesh(params string[] shapes)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.up, Vector3.right }, triangles = new[] { 0, 1, 2 } };
            owned.Add(mesh);
            var delta = new Vector3[3];
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, delta, null, null);
            return mesh;
        }
    }
}
