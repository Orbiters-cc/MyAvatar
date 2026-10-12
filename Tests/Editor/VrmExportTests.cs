using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Vrm;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Export to VRM: what a built avatar becomes (expressions, springs, colliders, materials, shapes kept), and both
    // writers' files when UniVRM is installed.
    public sealed class VrmExportTests
    {
        private readonly List<Object> owned = new List<Object>();
        private Scene scene;
        private GameObject root;
        private Dictionary<string, Transform> bones;
        private SkinnedMeshRenderer face;

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            bones = Humanoid();
            face = Face(new[] { "vrc.v_aa", "vrc.v_ih", "vrc.v_ou", "vrc.v_e", "vrc.v_oh", "Blink", "jawOpen", "eyeBlinkLeft", "EyeWideRight", "Happy", "Unused" });
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.lipSync = VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape;
            descriptor.VisemeSkinnedMesh = face;
            descriptor.VisemeBlendShapes = Enumerable.Range(0, 15).Select(i => i == 10 ? "vrc.v_aa" : i == 11 ? "vrc.v_e" : i == 12 ? "vrc.v_ih" : i == 13 ? "vrc.v_oh" : i == 14 ? "vrc.v_ou" : "").ToArray();
            descriptor.ViewPosition = new Vector3(0, 1.6f, .08f);
        }

        [TearDown] public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            BlendShapePruning.Keep.Clear();
        }

        [Test] public void TheModelHasVisemesBlinkEmotionsAndPerfectSync()
        {
            var model = VrmModel.From(root, new VrmExportSettings(), "Test", null);

            VrmModel.Expression Preset(VrmPreset preset) => model.Expressions.Single(e => e.Preset == preset);
            Assert.That(Preset(VrmPreset.Aa).Shapes.Single().Shape, Is.EqualTo("vrc.v_aa"));
            Assert.That(Preset(VrmPreset.Ee).Shapes.Single().Shape, Is.EqualTo("vrc.v_e"));
            Assert.That(Preset(VrmPreset.Blink).Shapes.Single().Shape, Is.EqualTo("Blink"), "The face's blink, when the descriptor has no eyelids.");
            Assert.That(Preset(VrmPreset.Happy).Shapes.Single().Shape, Is.EqualTo("Happy"));
            Assert.That(model.Expressions.Where(e => e.Preset == null).Select(e => e.Name), Is.SupersetOf(new[] { "JawOpen", "EyeBlinkLeft", "EyeWideRight" }),
                "Perfect sync: ARKit names, the face's shape under its own name or another standard's.");
            var mesh = face.sharedMesh;
            Assert.That(mesh.GetBlendShapeIndex("Unused"), Is.EqualTo(-1), "A shape no expression sets leaves the mesh.");
            Assert.That(mesh.GetBlendShapeIndex("jawOpen"), Is.GreaterThanOrEqualTo(0));
            Assert.That(model.ViewOffset.y, Is.EqualTo(1.6f - bones["Head"].position.y).Within(1e-4f));
            Assert.That(model.Head, Is.SameAs(bones["Head"]));
        }

        [Test] public void TheBuildKeepsTheShapesTheVrmNeeds()
        {
            var shapes = VrmModel.Shapes(root);
            Assert.That(shapes, Does.Contain("jawOpen").And.Contain("vrc.v_aa").And.Not.Contain("Unused"));
            BlendShapePruning.Keep[root] = shapes;
            var used = BlendShapePruning.Used(root, face);
            Assert.That(used, Does.Contain("jawOpen"), "Nothing animates it, but the VRM keeps it.");
            Assert.That(used, Does.Not.Contain("Unused"));
        }

        [Test] public void APhysBoneBecomesOneSpringPerChain()
        {
            // A tail of three bones whose second one forks.
            var tail = Chain("Tail", bones["Hips"], 3);
            var fork = new GameObject("Fork").transform; fork.SetParent(tail.GetChild(0), false); fork.localPosition = new Vector3(.1f, 0, -.1f);
            var physBone = tail.gameObject.AddComponent<VRCPhysBone>();
            physBone.pull = .2f; physBone.spring = .2f; physBone.stiffness = .2f; physBone.gravity = .1f; physBone.radius = .03f;
            var hipCollider = bones["Hips"].gameObject.AddComponent<VRCPhysBoneCollider>();
            hipCollider.shapeType = VRCPhysBoneColliderBase.ShapeType.Capsule; hipCollider.radius = .05f; hipCollider.height = .3f;
            physBone.colliders = new List<VRCPhysBoneColliderBase> { hipCollider };

            var model = VrmModel.From(root, new VrmExportSettings(), "Test", null);

            Assert.That(model.Springs, Has.Count.EqualTo(2), "The fork starts a chain of its own.");
            Assert.That(model.Springs[0].Joints.Select(j => j.name), Is.EqualTo(new[] { "Tail0", "Tail1", "Tail2" }));
            Assert.That(model.Springs[1].Joints.Select(j => j.name), Is.EqualTo(new[] { "Fork" }));
            Assert.That(model.Springs[0].Stiffness, Is.EqualTo(1.2f).Within(1e-4f), "Pull and stiffness: VRM's usual 1 for PhysBone's usual 0.2.");
            Assert.That(model.Springs[0].Drag, Is.EqualTo(.82f).Within(1e-4f));
            Assert.That(model.Springs[0].End, Is.EqualTo(new Vector3(0, .1f, 0)).Using(new Vector3EqualityComparer()), "On along the last bone by its length.");
            var collider = model.Springs[0].Colliders.Single();
            Assert.That(collider.Capsule, Is.True);
            Assert.That((collider.Tail - collider.Offset).magnitude, Is.EqualTo(.2f).Within(1e-4f), "The capsule's segment: its height less its round ends.");
        }

        [Test] public void BothWritersWriteAVrmWithItsHumanoidAndExpressions()
        {
            foreach (int version in new[] { 0, 1 })
            {
                var writer = VrmExport.Writer(version);
                if (writer == null) continue;
                var copy = Object.Instantiate(root);
                SceneManager.MoveGameObjectToScene(copy, scene);
                var model = VrmModel.From(copy, new VrmExportSettings { authors = "Test", licenseAllows = true }, "Test", null);
                var json = Json(writer.Write(model));
                Assert.That(json, Does.Contain(version == 0 ? "\"VRM\"" : "\"VRMC_vrm\""), "VRM " + version);
                Assert.That(json, Does.Contain("\"hips\""), "The humanoid's bones.");
                Assert.That(json, Does.Contain(version == 0 ? "\"JawOpen\"" : "JawOpen"), "Perfect sync's custom expressions.");
            }
            if (VrmExport.Writers.Count == 0) Assert.Ignore("UniVRM isn't installed.");
        }

        // The JSON chunk of a GLB file.
        private static string Json(byte[] glb)
        {
            Assert.That(Encoding.ASCII.GetString(glb, 0, 4), Is.EqualTo("glTF"));
            int length = System.BitConverter.ToInt32(glb, 12);
            return Encoding.UTF8.GetString(glb, 20, length);
        }

        private sealed class Vector3EqualityComparer : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-8f;
            public int GetHashCode(Vector3 v) => 0;
        }

        private Transform Chain(string name, Transform parent, int length)
        {
            Transform first = null, at = parent;
            for (int i = 0; i < length; i++)
            {
                var bone = new GameObject(name + i).transform;
                bone.SetParent(at, false);
                bone.localPosition = i == 0 ? new Vector3(0, 0, -.1f) : new Vector3(0, .1f, 0);
                if (first == null) first = bone;
                at = bone;
            }
            return first;
        }

        // A triangle skinned to the head with the shapes.
        private SkinnedMeshRenderer Face(string[] shapes)
        {
            var renderer = new GameObject("Body").AddComponent<SkinnedMeshRenderer>();
            renderer.transform.SetParent(root.transform, false);
            var mesh = new Mesh { name = "Body", vertices = new[] { new Vector3(0, 1.5f, 0), new Vector3(.1f, 1.5f, 0), new Vector3(0, 1.6f, 0) }, triangles = new[] { 0, 1, 2 } };
            owned.Add(mesh);
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            mesh.bindposes = new[] { bones["Head"].worldToLocalMatrix };
            var delta = new[] { Vector3.forward * .01f, Vector3.zero, Vector3.zero };
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, delta, null, null);
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bones["Head"] };
            var material = new Material(Shader.Find("Standard")); owned.Add(material);
            renderer.sharedMaterial = material;
            return renderer;
        }

        // A humanoid skeleton under the avatar (hips 1 m up) and its Animator.
        private Dictionary<string, Transform> Humanoid()
        {
            var human = new Dictionary<string, Transform>();
            void Add(string name, string parent, Vector3 position)
            {
                var bone = new GameObject(name).transform;
                bone.SetParent(parent == null ? root.transform : human[parent], false);
                bone.localPosition = position;
                human[name] = bone;
            }
            Add("Hips", null, new Vector3(0, 1, 0)); Add("Spine", "Hips", new Vector3(0, .1f, 0)); Add("Chest", "Spine", new Vector3(0, .15f, 0));
            Add("Neck", "Chest", new Vector3(0, .24f, 0)); Add("Head", "Neck", new Vector3(0, .08f, 0));
            foreach (var side in new[] { "L", "R" })
            {
                float x = side == "L" ? -1 : 1;
                Add("UpperArm" + side, "Chest", new Vector3(.15f * x, .15f, 0)); Add("LowerArm" + side, "UpperArm" + side, new Vector3(.25f * x, 0, 0));
                Add("Hand" + side, "LowerArm" + side, new Vector3(.25f * x, 0, 0));
                Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
                Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
            }
            var map = new Dictionary<string, string>
            {
                ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
                ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
                ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
                ["LeftUpperLeg"] = "UpperLegL", ["LeftLowerLeg"] = "LowerLegL", ["LeftFoot"] = "FootL",
                ["RightUpperLeg"] = "UpperLegR", ["RightLowerLeg"] = "LowerLegR", ["RightFoot"] = "FootR",
            };
            var description = new HumanDescription
            {
                human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
            };
            var avatar = AvatarBuilder.BuildHumanAvatar(root, description); owned.Add(avatar);
            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            Assert.That(animator.isHuman, Is.True);
            return human;
        }
    }
}
