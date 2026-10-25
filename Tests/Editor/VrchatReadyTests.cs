using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Vrm;
using Orbiters.MyAvatar.Editor.VrchatReady;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRC.SDKBase;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Make it VRChat ready on a plain humanoid (names only) and on a VRM model as a reader describes it (the readers'
    // UniVRM components are covered by Orbiters.MyAvatar.Editor.Tests.Vrm): the descriptor, PhysBones, toggles and
    // what goes, all in one Undo step. Everything lives in a preview scene.
    public sealed class VrchatReadyTests
    {
        private readonly List<Object> owned = new List<Object>();
        private Scene scene;
        private GameObject root;
        private Dictionary<string, Transform> bones;
        private SkinnedMeshRenderer face;
        private FakeReader reader;
        private FakeToggles toggles;
        private IExpressionToggles realToggles;

        private static readonly string[] VroidFace =
        {
            "Face.M_F00_000_00_Fcl_ALL_Neutral", "Face.M_F00_000_00_Fcl_ALL_Joy", "Face.M_F00_000_00_Fcl_ALL_Angry", "Face.M_F00_000_00_Fcl_EYE_Close",
            "Face.M_F00_000_00_Fcl_EYE_Close_R", "Face.M_F00_000_00_Fcl_EYE_Close_L", "Face.M_F00_000_00_Fcl_MTH_Angry", "Face.M_F00_000_00_Fcl_MTH_A",
            "Face.M_F00_000_00_Fcl_MTH_I", "Face.M_F00_000_00_Fcl_MTH_U", "Face.M_F00_000_00_Fcl_MTH_E", "Face.M_F00_000_00_Fcl_MTH_O", "Face.M_F00_000_00_Fcl_MTH_Up",
        };

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Model");
            SceneManager.MoveGameObjectToScene(root, scene);
            bones = Humanoid();
            face = Face("Face", VroidFace);
            reader = new FakeReader();
            ReadyPlan.Readers.Insert(0, reader);
            realToggles = ReadyConversion.Toggles;
            ReadyConversion.Toggles = toggles = new FakeToggles();
        }

        [TearDown] public void TearDown()
        {
            ReadyPlan.Readers.Remove(reader);
            ReadyConversion.Toggles = realToggles;
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
        }

        [Test] public void FaceShapesFindVroidMmdAndVrchatNames()
        {
            Assert.That(FaceShapes.Vowels5(VroidFace), Is.EqualTo(new[] { "Face.M_F00_000_00_Fcl_MTH_A", "Face.M_F00_000_00_Fcl_MTH_I", "Face.M_F00_000_00_Fcl_MTH_U",
                "Face.M_F00_000_00_Fcl_MTH_E", "Face.M_F00_000_00_Fcl_MTH_O" }));
            Assert.That(FaceShapes.Blink(VroidFace), Is.EqualTo("Face.M_F00_000_00_Fcl_EYE_Close"), "Both eyes, not one");
            Assert.That(FaceShapes.Vowels5(new[] { "まばたき", "あ", "い", "う", "え", "お", "にやり" }), Is.EqualTo(new[] { "あ", "い", "う", "え", "お" }));
            Assert.That(FaceShapes.Blink(new[] { "あ", "まばたき" }), Is.EqualTo("まばたき"));

            var vrchat = FaceShapes.Visemes.Select(v => "vrc.v_" + v.ToLowerInvariant()).Concat(new[] { "vrc.blink" }).ToList();
            var visemes = FaceShapes.Fill(vrchat, FaceShapes.Vowels5(vrchat));
            Assert.That(visemes, Is.EqualTo(FaceShapes.Visemes.Select(v => "vrc.v_" + v.ToLowerInvariant())), "VRChat's own visemes, all 15, as they are");
            Assert.That(FaceShapes.Blink(vrchat), Is.EqualTo("vrc.blink"));
        }

        [Test] public void FiveVowelsMakeTheVisemesAndAMissingVowelBorrowsTheClosest()
        {
            var visemes = FaceShapes.FromVowels(new[] { "A", "I", "U", "E", "O" });
            Assert.That(visemes, Is.EqualTo(new[] { null, null, "I", "E", "E", "A", "U", "I", "E", "O", "A", "E", "I", "O", "U" }),
                "sil PP FF TH DD kk CH SS nn RR aa E ih oh ou");
            var noO = FaceShapes.FromVowels(new[] { "A", "I", "U", "E", null });
            Assert.That(noO[13], Is.EqualTo("U"), "oh borrows U");
            Assert.That(FaceShapes.FromVowels(new string[5]).All(v => v == null), Is.True);
        }

        [Test] public void APlainHumanoidGetsADescriptorFromItsNames()
        {
            var (left, right) = Eyes("J_Adj_L_FaceEye", "J_Adj_R_FaceEye");

            var plan = ReadyPlan.Of(root);

            Assert.That(plan.CanConvert, Is.True, plan.Problem);
            Assert.That(plan.Vrm, Is.Null);
            Assert.That((plan.LeftEye, plan.RightEye), Is.EqualTo((left, right)), "VRoid's eye bones by name: the rig doesn't map them");
            Assert.That(plan.Face, Is.SameAs(face));
            Assert.That(plan.Remove, Is.Empty);

            var result = ReadyConversion.Apply(plan);

            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            Assert.That(descriptor, Is.Not.Null.And.SameAs(result.Descriptor));
            Assert.That(Pipeline(), Is.Not.Null, "Its pipeline manager");
            Assert.That(descriptor.lipSync, Is.EqualTo(VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape));
            Assert.That(descriptor.VisemeSkinnedMesh, Is.SameAs(face));
            Assert.That(descriptor.VisemeBlendShapes, Has.Length.EqualTo(15));
            Assert.That(descriptor.VisemeBlendShapes[10], Is.EqualTo("Face.M_F00_000_00_Fcl_MTH_A"), "aa");
            Assert.That(descriptor.VisemeBlendShapes[14], Is.EqualTo("Face.M_F00_000_00_Fcl_MTH_U"), "ou");
            Assert.That(descriptor.VisemeBlendShapes[0], Is.Empty, "Silence stays at rest");
            Assert.That(descriptor.enableEyeLook, Is.True);
            var eyes = descriptor.customEyeLookSettings;
            Assert.That((eyes.leftEye, eyes.rightEye), Is.EqualTo((left, right)));
            Assert.That(eyes.eyelidType, Is.EqualTo(VRCAvatarDescriptor.EyelidType.Blendshapes));
            Assert.That(eyes.eyelidsSkinnedMesh, Is.SameAs(face));
            Assert.That(eyes.eyelidsBlendshapes[0], Is.EqualTo(face.sharedMesh.GetBlendShapeIndex("Face.M_F00_000_00_Fcl_EYE_Close")));
            // Looking up turns the eye's forward upward by the default angle.
            var straight = eyes.eyesLookingStraight.left;
            var up = eyes.eyesLookingUp.left;
            var parent = left.parent.rotation;
            var forwardUp = parent * up * Vector3.forward;
            Assert.That(Vector3.Angle(parent * straight * Vector3.forward, forwardUp), Is.EqualTo(ReadyPlan.DefaultLook).Within(.01f));
            Assert.That(forwardUp.y, Is.GreaterThan(0f), "Up is up");
            var lookingLeft = parent * eyes.eyesLookingLeft.left * Vector3.forward;
            Assert.That(lookingLeft.x, Is.LessThan(0f), "The avatar's left is -X");
            // Between the eyes, a little in front of their centres.
            var view = root.transform.TransformPoint(descriptor.ViewPosition);
            Assert.That(view.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(view.y, Is.EqualTo(left.position.y).Within(1e-4f));
            Assert.That(view.z, Is.EqualTo(left.position.z + .06f * ReadyPlan.ViewForward).Within(1e-4f));
            Assert.That(descriptor.baseAnimationLayers.Select(l => l.type), Is.EqualTo(new[] { VRCAvatarDescriptor.AnimLayerType.Base, VRCAvatarDescriptor.AnimLayerType.Additive,
                VRCAvatarDescriptor.AnimLayerType.Gesture, VRCAvatarDescriptor.AnimLayerType.Action, VRCAvatarDescriptor.AnimLayerType.FX }));
            Assert.That(descriptor.baseAnimationLayers.All(l => l.isDefault), Is.True);
            Assert.That(descriptor.collider_head.radius, Is.GreaterThan(0f), "The automatic colliders are measured");
            Assert.That(result.Summary, Does.Contain("13 visemes").And.Contain("eye look and blink"));
        }

        [Test] public void ARigThatIsntHumanoidIsExplainedNotConverted()
        {
            root.GetComponent<Animator>().avatar = null;
            var plan = ReadyPlan.Of(root);
            Assert.That(plan.CanConvert, Is.False);
            Assert.That(plan.Problem, Does.Contain("humanoid"));
            Assert.That(plan.Help, Does.Contain("Rig").And.Contain("Humanoid"));
            Assert.Throws<System.InvalidOperationException>(() => ReadyConversion.Apply(plan));
            Assert.That(root.GetComponent<VRCAvatarDescriptor>(), Is.Null);
        }

        [Test] public void AVrmBecomesPhysBonesTogglesAndADescriptorInOneUndoStep()
        {
            var (left, right) = Eyes("EyeL", "EyeR");
            // Three hair strands under the head, beside the eyes; a tail of three bones on the hips.
            var strands = Enumerable.Range(1, 3).Select(i => Chain("Hair" + i, bones["Head"], 3, new Vector3((i - 2) * .03f, .05f, -.02f), Vector3.down * .05f)).ToList();
            var tail = Chain("Tail", bones["Hips"], 3, new Vector3(0, 0, -.1f), new Vector3(0, -.03f, -.08f));
            var secondary = new GameObject("secondary");
            secondary.transform.SetParent(root.transform, false);
            var vrmOnly = secondary.AddComponent<BoxCollider>();
            var headSphere = new VrmModel.Collider { Transform = bones["Head"], Offset = new Vector3(0, .08f, 0), Radius = .1f };
            var hips = new VrmModel.Collider { Transform = bones["Hips"], Offset = new Vector3(0, -.05f, 0), Tail = new Vector3(0, .1f, 0), Radius = .08f, Capsule = true };
            var source = reader.Source = new VrmSource { Format = "VRM 0.x", Head = bones["Head"], ViewOffset = new Vector3(0, .06f, 0), EyeBones = true, LeftEye = left, RightEye = right,
                LookUp = 8f, LookDown = 9f, LookInner = 7f, LookOuter = 12f, Title = "Test", Authors = "Someone", License = "CC BY" };
            var hair = new VrmSource.Spring { Name = "Hair", Stiffness = 1f, Drag = .4f, Radius = .02f };
            hair.Roots.AddRange(strands);
            hair.Colliders.Add(headSphere);
            var tailSpring = new VrmSource.Spring { Name = "Tail", Stiffness = 3f, Drag = .7f, GravityPower = .5f, Radius = .03f };
            tailSpring.Roots.Add(tail);
            tailSpring.Colliders.Add(headSphere);
            tailSpring.Colliders.Add(hips);
            source.Springs.Add(hair);
            source.Springs.Add(tailSpring);
            Add(source, VrmPreset.Aa, "A", "Face.M_F00_000_00_Fcl_MTH_A");
            Add(source, VrmPreset.Ih, "I", "Face.M_F00_000_00_Fcl_MTH_I");
            Add(source, VrmPreset.Blink, "Blink", "Face.M_F00_000_00_Fcl_EYE_Close");
            Add(source, VrmPreset.Happy, "Joy", "Face.M_F00_000_00_Fcl_ALL_Joy");
            Add(source, VrmPreset.Angry, "Angry", "Face.M_F00_000_00_Fcl_ALL_Angry", "Face.M_F00_000_00_Fcl_MTH_Angry");
            Add(source, null, "BlendShape.JawOpen", "Face.M_F00_000_00_Fcl_MTH_A");
            source.Components.Add(vrmOnly);

            var plan = ReadyPlan.Of(root);

            Assert.That(plan.CanConvert, Is.True, plan.Problem);
            Assert.That(plan.Vrm, Is.SameAs(source));
            Assert.That(plan.VisemesFrom, Does.Contain("A, I"));
            Assert.That(plan.Visemes[11], Is.EqualTo("Face.M_F00_000_00_Fcl_MTH_E"), "A vowel the VRM lacks, by name");
            Assert.That(plan.Toggles.Select(t => t.Name), Is.EqualTo(new[] { "Joy", "Angry" }), "Emotions, never perfect sync's ARKit clips");
            Assert.That(plan.PhysBones, Has.Count.EqualTo(2));
            var hairGroup = plan.PhysBones.Single(g => g.Root == bones["Head"]);
            Assert.That(hairGroup.StillRoot, Is.True, "The strands share the head, which stays still");
            Assert.That(hairGroup.Ignored, Is.EquivalentTo(new[] { left, right }), "The head's other children stay out");
            Assert.That(hairGroup.Transforms, Is.EqualTo(9));
            Assert.That(hairGroup.Endpoint.y, Is.LessThan(-.06f), "The VRM tip: 7 cm on along the strand (down)");
            Assert.That((hairGroup.Pull, hairGroup.Spring, hairGroup.Stiffness), Is.EqualTo((1f / 6f, 2f / 3f, 1f / 6f)).Using(new FloatTupleComparer()));
            var tailGroup = plan.PhysBones.Single(g => g.Root == tail);
            Assert.That(tailGroup.StillRoot, Is.False, "A lone root swings, as in VRM");
            Assert.That(tailGroup.Gravity, Is.EqualTo(.5f).Within(1e-5f));
            Assert.That(plan.Colliders, Has.Count.EqualTo(2), "One collider per VRM collider, shared by the springs");

            int undoBefore = Undo.GetCurrentGroup();
            var result = ReadyConversion.Apply(plan);

            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            Assert.That(descriptor, Is.Not.Null);
            Assert.That(descriptor.customEyeLookSettings.eyesLookingLeft.linked, Is.False, "One eye turns 12° away from the nose, the other 7° toward it");
            var parent = left.parent.rotation;
            Assert.That(Vector3.Angle(parent * descriptor.customEyeLookSettings.eyesLookingLeft.left * Vector3.forward, Vector3.forward), Is.EqualTo(12f).Within(.01f));
            Assert.That(Vector3.Angle(parent * descriptor.customEyeLookSettings.eyesLookingLeft.right * Vector3.forward, Vector3.forward), Is.EqualTo(7f).Within(.01f));
            var physBones = root.GetComponentsInChildren<VRCPhysBone>(true);
            Assert.That(physBones, Has.Length.EqualTo(2));
            Assert.That(physBones.All(p => p.transform.parent.name == ReadyConversion.PhysBonesName), Is.True);
            var hairBone = physBones.Single(p => p.rootTransform == bones["Head"]);
            Assert.That(hairBone.multiChildType, Is.EqualTo(VRCPhysBoneBase.MultiChildType.Ignore));
            Assert.That(hairBone.ignoreTransforms, Is.EquivalentTo(new[] { left, right }));
            Assert.That(hairBone.version, Is.EqualTo(VRCPhysBoneBase.Version.Version_1_1));
            var tailBone = physBones.Single(p => p.rootTransform == tail);
            Assert.That(tailBone.multiChildType, Is.EqualTo(VRCPhysBoneBase.MultiChildType.First));
            var colliders = root.GetComponentsInChildren<VRCPhysBoneCollider>(true);
            Assert.That(colliders, Has.Length.EqualTo(2));
            var capsule = colliders.Single(c => c.shapeType == VRCPhysBoneColliderBase.ShapeType.Capsule);
            Assert.That(capsule.transform, Is.SameAs(bones["Hips"]));
            Assert.That(capsule.height, Is.EqualTo(.15f + .16f).Within(1e-5f), "The segment and its round ends");
            Assert.That(capsule.position, Is.EqualTo(new Vector3(0, .025f, 0)).Using(new Vector3Comparer()));
            Assert.That(tailBone.colliders, Has.Count.EqualTo(2));
            Assert.That(hairBone.colliders.Single(), Is.SameAs(tailBone.colliders.Single(c => c.shapeType == VRCPhysBoneColliderBase.ShapeType.Sphere)));
            Assert.That(toggles.Written.Select(e => e.Name), Is.EqualTo(new[] { "Joy", "Angry" }));
            Assert.That(vrmOnly == null, Is.True, "VRM's components go");
            Assert.That(secondary == null, Is.True, "And the object that only held them");
            Assert.That(result.Removed, Is.EqualTo(1));

            // One Undo step puts everything back.
            Assert.That(Undo.GetCurrentGroup(), Is.GreaterThan(undoBefore));
            Undo.PerformUndo();
            Assert.That(root.GetComponent<VRCAvatarDescriptor>() == null, Is.True);
            Assert.That(Pipeline(), Is.Null);
            Assert.That(root.GetComponentsInChildren<VRCPhysBone>(true), Is.Empty);
            Assert.That(root.GetComponentsInChildren<VRCPhysBoneCollider>(true), Is.Empty);
            Assert.That(root.transform.Find("secondary"), Is.Not.Null);
            Assert.That(root.transform.Find("secondary").GetComponent<BoxCollider>(), Is.Not.Null);
            Assert.That(root.transform.Find(ReadyConversion.PhysBonesName), Is.Null);
        }

        [Test] public void SpringsOfOneFeelShareTheirParentAcrossVrm1ChainsUnlessSomethingMovesIt()
        {
            var a = Chain("A", bones["Head"], 3, new Vector3(-.03f, .05f, 0), Vector3.down * .05f);
            var b = Chain("B", bones["Head"], 3, new Vector3(.03f, .05f, 0), Vector3.down * .05f);
            var source = new VrmSource { Format = "VRM 1.0" };
            foreach (var start in new[] { a, b })
            {
                var spring = new VrmSource.Spring { Name = start.name, Chain = start.GetComponentsInChildren<Transform>().ToList() };
                spring.Roots.Add(start);
                source.Springs.Add(spring);
            }
            // A third chain whose own settings differ swings by itself.
            var c = Chain("C", bones["Head"], 2, new Vector3(0, .05f, -.03f), Vector3.down * .05f);
            var other = new VrmSource.Spring { Name = "C", Chain = c.GetComponentsInChildren<Transform>().ToList(), Stiffness = 2f };
            other.Roots.Add(c);
            source.Springs.Add(other);

            var groups = SpringMapping.Groups(source, new HashSet<Transform>(bones.Values));

            Assert.That(groups, Has.Count.EqualTo(2));
            var shared = groups.Single(g => g.StillRoot);
            Assert.That(shared.Root, Is.SameAs(bones["Head"]));
            Assert.That(shared.Ignored, Does.Contain(c), "The other chain stays out of the shared one");
            Assert.That(shared.Endpoint, Is.EqualTo(Vector3.zero), "A VRM 1.0 chain ends at its own tip");
            Assert.That(groups.Single(g => !g.StillRoot).Root, Is.SameAs(c));
        }

        [Test] public void AnAvatarWithADescriptorIsLeftAlone()
        {
            Undo.AddComponent<VRCAvatarDescriptor>(root);
            var avatar = root.AddComponent<MyAvatar>();
            Assert.That(ReadyPlan.HasDescriptor(avatar), Is.True);
            Assert.Throws<System.InvalidOperationException>(() => ReadyConversion.Apply(ReadyPlan.Of(root)));
        }

        // ---- Fixtures ----

        // VRChat's pipeline manager (VRC.Core, an assembly the tests don't reference).
        private Component Pipeline() => root.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "PipelineManager");

        private sealed class FakeReader : IVrmReader
        {
            public VrmSource Source;
            public VrmSource Read(GameObject model) => Source;
        }

        private sealed class FakeToggles : IExpressionToggles
        {
            public readonly List<VrmModel.Expression> Written = new List<VrmModel.Expression>();
            public GameObject Write(GameObject model, string menuFolder, IReadOnlyList<VrmModel.Expression> expressions)
            {
                Written.AddRange(expressions);
                var holder = new GameObject(menuFolder);
                holder.transform.SetParent(model.transform, false);
                Undo.RegisterCreatedObjectUndo(holder, ReadyConversion.UndoName);
                return holder;
            }
        }

        private sealed class FloatTupleComparer : IEqualityComparer<(float, float, float)>
        {
            public bool Equals((float, float, float) a, (float, float, float) b) =>
                Mathf.Abs(a.Item1 - b.Item1) < 1e-4f && Mathf.Abs(a.Item2 - b.Item2) < 1e-4f && Mathf.Abs(a.Item3 - b.Item3) < 1e-4f;
            public int GetHashCode((float, float, float) v) => 0;
        }

        private sealed class Vector3Comparer : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-8f;
            public int GetHashCode(Vector3 v) => 0;
        }

        private void Add(VrmSource source, VrmPreset? preset, string name, params string[] shapes)
        {
            var expression = new VrmModel.Expression { Preset = preset, Name = name };
            foreach (string shape in shapes) expression.Shapes.Add((face, shape, 1f));
            source.Expressions.Add(expression);
        }

        private (Transform, Transform) Eyes(string left, string right)
        {
            var l = new GameObject(left).transform; l.SetParent(bones["Head"], false); l.localPosition = new Vector3(-.03f, .06f, .07f);
            var r = new GameObject(right).transform; r.SetParent(bones["Head"], false); r.localPosition = new Vector3(.03f, .06f, .07f);
            return (l, r);
        }

        private static Transform Chain(string name, Transform parent, int length, Vector3 first, Vector3 step)
        {
            Transform start = null, at = parent;
            for (int i = 0; i < length; i++)
            {
                var bone = new GameObject(name + "_" + i).transform;
                bone.SetParent(at, false);
                bone.localPosition = i == 0 ? first : step;
                if (start == null) start = bone;
                at = bone;
            }
            start.name = name;
            return start;
        }

        // A triangle skinned to the head with the shapes.
        private SkinnedMeshRenderer Face(string name, string[] shapes)
        {
            var renderer = new GameObject(name).AddComponent<SkinnedMeshRenderer>();
            renderer.transform.SetParent(root.transform, false);
            var mesh = new Mesh { name = name, vertices = new[] { new Vector3(0, 1.5f, 0), new Vector3(.1f, 1.5f, 0), new Vector3(0, 1.6f, 0) }, triangles = new[] { 0, 1, 2 } };
            owned.Add(mesh);
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            mesh.bindposes = new[] { bones["Head"].worldToLocalMatrix };
            var delta = new[] { Vector3.forward * .01f, Vector3.zero, Vector3.zero };
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, delta, null, null);
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bones["Head"] };
            return renderer;
        }

        // A humanoid skeleton under the model (hips 1 m up) and its Animator, without eye bones.
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
