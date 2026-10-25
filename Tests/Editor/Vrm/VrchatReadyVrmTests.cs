using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Vrm;
using Orbiters.MyAvatar.Editor.VrchatReady;
using UniGLTF.Extensions.VRMC_vrm;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniVRM10;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using VRM;
using HumanBone = UnityEngine.HumanBone;
using LookAtType = UniGLTF.Extensions.VRMC_vrm.LookAtType;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests.Vrm
{
    // Make it VRChat ready on models carrying UniVRM's own components, as UniVRM's importers make them: VRM 0.x (meta,
    // first person, bone look-at, blend shape clips, spring bones with sphere colliders) and VRM 1.0 (an instance with
    // its VRM object, joints, a capsule collider, a constraint). Nothing of VRM may stay, and one Undo brings it all back.
    public sealed class VrchatReadyVrmTests
    {
        private readonly List<Object> owned = new List<Object>();
        private Scene scene;
        private GameObject root;
        private Dictionary<string, Transform> bones;
        private SkinnedMeshRenderer face;

        private static readonly string[] Shapes = { "Fcl_ALL_Neutral", "Fcl_ALL_Joy", "Fcl_ALL_Surprised", "Fcl_EYE_Close", "Fcl_MTH_A", "Fcl_MTH_I", "Fcl_MTH_U", "Fcl_MTH_E", "Fcl_MTH_O", "jawOpen" };

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("VRoid");
            SceneManager.MoveGameObjectToScene(root, scene);
        }

        [TearDown] public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
        }

        [Test] public void Vrm0ComponentsBecomeVrchatOnesAndGo()
        {
            bones = Humanoid(eyesInRig: false);
            face = Face();
            var meta = Own(ScriptableObject.CreateInstance<VRMMetaObject>());
            meta.Title = "Sample"; meta.Author = "VRoid"; meta.LicenseType = LicenseType.CC_BY; meta.AllowedUser = AllowedUser.Everyone;
            root.AddComponent<VRMMeta>().Meta = meta;
            var firstPerson = root.AddComponent<VRMFirstPerson>();
            firstPerson.FirstPersonBone = bones["Head"];
            firstPerson.FirstPersonOffset = new Vector3(0, .06f, 0);
            root.AddComponent<VRMLookAtHead>().Head = bones["Head"];
            var lookAt = root.AddComponent<VRMLookAtBoneApplyer>();
            lookAt.LeftEye = OffsetOnTransform.Create(bones["EyeL"]);
            lookAt.RightEye = OffsetOnTransform.Create(bones["EyeR"]);
            lookAt.VerticalUp = new VRM.CurveMapper(90, 8); lookAt.VerticalDown = new VRM.CurveMapper(90, 9);
            lookAt.HorizontalInner = new VRM.CurveMapper(90, 7); lookAt.HorizontalOuter = new VRM.CurveMapper(90, 12);
            var clips = Own(ScriptableObject.CreateInstance<BlendShapeAvatar>());
            clips.Clips.Add(Clip(BlendShapePreset.A, "A", "Fcl_MTH_A"));
            clips.Clips.Add(Clip(BlendShapePreset.O, "O", "Fcl_MTH_O"));
            clips.Clips.Add(Clip(BlendShapePreset.Blink, "Blink", "Fcl_EYE_Close"));
            clips.Clips.Add(Clip(BlendShapePreset.Joy, "Joy", "Fcl_ALL_Joy"));
            clips.Clips.Add(Clip(BlendShapePreset.Unknown, "Surprised", "Fcl_ALL_Surprised"));
            clips.Clips.Add(Clip(BlendShapePreset.Neutral, "Neutral", "Fcl_ALL_Neutral"));
            root.AddComponent<VRMBlendShapeProxy>().BlendShapeAvatar = clips;
            var strands = Enumerable.Range(1, 2).Select(i => Chain("J_Sec_Hair" + i, bones["Head"], 3, new Vector3((i - 1.5f) * .04f, .06f, -.03f))).ToList();
            var group = bones["Head"].gameObject.AddComponent<VRMSpringBoneColliderGroup>();
            group.Colliders = new[] { new VRMSpringBoneColliderGroup.SphereCollider { Offset = new Vector3(0, .07f, 0), Radius = .09f },
                new VRMSpringBoneColliderGroup.SphereCollider { Offset = new Vector3(0, .03f, .04f), Radius = .05f } };
            var secondary = new GameObject("secondary");
            secondary.transform.SetParent(root.transform, false);
            var spring = secondary.AddComponent<VRMSpringBone>();
            spring.m_comment = "Hair";
            spring.m_stiffnessForce = 1f; spring.m_dragForce = .4f; spring.m_hitRadius = .02f;
            spring.RootBones = strands;
            spring.ColliderGroups = new[] { group };

            var plan = ReadyPlan.Of(root);

            Assert.That(plan.CanConvert, Is.True, plan.Problem);
            Assert.That(plan.Vrm?.Format, Is.EqualTo("VRM 0.x"));
            Assert.That(plan.Vrm.Title, Is.EqualTo("Sample"));
            Assert.That((plan.LeftEye, plan.RightEye), Is.EqualTo((bones["EyeL"], bones["EyeR"])), "The look-at's eye bones");
            Assert.That((plan.LookUp, plan.LookDown, plan.LookInner, plan.LookOuter), Is.EqualTo((8f, 9f, 7f, 12f)));
            Assert.That(plan.Vrm.Expressions.Select(e => e.Name), Is.EquivalentTo(new[] { "A", "O", "Blink", "Joy", "Surprised" }), "Neutral never");
            Assert.That(plan.Visemes[10], Is.EqualTo("Fcl_MTH_A"));
            Assert.That(plan.Visemes[13], Is.EqualTo("Fcl_MTH_O"));
            Assert.That(plan.Visemes[12], Is.EqualTo("Fcl_MTH_I"), "I, which the VRM has no clip for, by name");
            Assert.That(plan.Blink, Is.EqualTo("Fcl_EYE_Close"));
            Assert.That(plan.Toggles.Select(t => t.Name), Is.EqualTo(new[] { "Joy", "Surprised" }));
            Assert.That(plan.PhysBones, Has.Count.EqualTo(1));
            Assert.That(plan.PhysBones[0].Root, Is.SameAs(bones["Head"]), "Two strands on the head: one PhysBone there");
            Assert.That(plan.Colliders, Has.Count.EqualTo(2));

            var result = ReadyConversion.Apply(plan);

            Assert.That(root.GetComponentsInChildren<Component>(true).Where(c => c != null && ReadyPlan.IsVrm(c.GetType())), Is.Empty, "Nothing of VRM stays");
            Assert.That(VRC.SDK3.Validation.AvatarValidation.FindIllegalComponents(root).Select(c => c.GetType().Name), Is.Empty, "The SDK finds nothing it would strip");
            Assert.That(root.transform.Find("secondary"), Is.Null);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            Assert.That(descriptor.VisemeSkinnedMesh, Is.SameAs(face));
            Assert.That(descriptor.customEyeLookSettings.eyelidsBlendshapes[0], Is.EqualTo(face.sharedMesh.GetBlendShapeIndex("Fcl_EYE_Close")));
            var physBone = root.GetComponentsInChildren<VRCPhysBone>(true).Single();
            Assert.That(physBone.rootTransform, Is.SameAs(bones["Head"]));
            Assert.That(physBone.ignoreTransforms, Is.EquivalentTo(new[] { bones["EyeL"], bones["EyeR"] }));
            Assert.That(physBone.colliders, Has.Count.EqualTo(2));
            Assert.That(bones["Head"].GetComponents<VRCPhysBoneCollider>().Select(c => c.radius), Is.EquivalentTo(new[] { .09f, .05f }));
            if (ReadyConversion.Toggles != null)
            {
                var toggles = root.transform.Find(ReadyConversion.ExpressionsMenu);
                Assert.That(toggles, Is.Not.Null, "VRCFury's toggles for Joy and Surprised");
                Assert.That(toggles.GetComponents<Component>().Count(c => c != null && c.GetType().Name == "VRCFury"), Is.EqualTo(2));
                Assert.That(result.Toggles, Is.EqualTo(2));
            }

            Undo.PerformUndo();
            Assert.That(root.GetComponent<VRCAvatarDescriptor>() == null, Is.True);
            Assert.That(root.GetComponent<VRMBlendShapeProxy>(), Is.Not.Null);
            Assert.That(root.transform.Find("secondary").GetComponent<VRMSpringBone>().RootBones, Has.Count.EqualTo(2));
            Assert.That(bones["Head"].GetComponent<VRMSpringBoneColliderGroup>(), Is.Not.Null);
            Assert.That(bones["Head"].GetComponents<VRCPhysBoneCollider>(), Is.Empty);
            Assert.That(root.transform.Find(ReadyConversion.ExpressionsMenu), Is.Null);
        }

        [Test] public void Vrm1ComponentsBecomeVrchatOnesAndGo()
        {
            bones = Humanoid(eyesInRig: true);
            face = Face();
            var instance = root.AddComponent<Vrm10Instance>();
            Assert.That(root.GetComponent<UniHumanoid.Humanoid>(), Is.Not.Null, "Required by the instance");
            var vrm = Own(ScriptableObject.CreateInstance<VRM10Object>());
            vrm.Meta.Name = "Sample"; vrm.Meta.Authors = new List<string> { "VRoid" }; vrm.Meta.AvatarPermission = AvatarPermissionType.onlyAuthor;
            vrm.LookAt.LookAtType = LookAtType.bone;
            vrm.LookAt.VerticalUp = new UniVRM10.CurveMapper(90, 12);
            vrm.Expression.Aa = Expression("aa", "Fcl_MTH_A");
            vrm.Expression.Blink = Expression("blink", "Fcl_EYE_Close");
            vrm.Expression.Happy = Expression("happy", "Fcl_ALL_Joy");
            vrm.Expression.CustomClips.Add(Expression("JawOpen", "jawOpen"));
            instance.Vrm = vrm;
            var chain = Chain("J_Sec_Tail", bones["Hips"], 3, new Vector3(0, 0, -.1f));
            var joints = chain.GetComponentsInChildren<Transform>().Select(t => t.gameObject.AddComponent<VRM10SpringBoneJoint>()).ToList();
            foreach (var joint in joints) { joint.m_stiffnessForce = 2f; joint.m_dragForce = .5f; joint.m_jointRadius = .03f; }
            var capsule = bones["Hips"].gameObject.AddComponent<VRM10SpringBoneCollider>();
            capsule.ColliderType = VRM10SpringBoneColliderTypes.Capsule; capsule.Offset = new Vector3(0, -.05f, 0); capsule.Tail = new Vector3(0, .1f, 0); capsule.Radius = .08f;
            var secondary = new GameObject("secondary");
            secondary.transform.SetParent(root.transform, false);
            var group = secondary.AddComponent<VRM10SpringBoneColliderGroup>();
            group.Colliders.Add(capsule);
            instance.SpringBone.ColliderGroups.Add(group);
            var spring = new Vrm10InstanceSpringBone.Spring("Tail");
            spring.Joints.AddRange(joints);
            spring.ColliderGroups.Add(group);
            instance.SpringBone.Springs.Add(spring);
            bones["Chest"].gameObject.AddComponent<Vrm10RotationConstraint>().Source = bones["Spine"];

            var plan = ReadyPlan.Of(root);

            Assert.That(plan.CanConvert, Is.True, plan.Problem);
            Assert.That(plan.Vrm?.Format, Is.EqualTo("VRM 1.0"));
            Assert.That(plan.Vrm.Restricted, Is.True, "Only its author may use it");
            Assert.That(plan.Vrm.Constraints, Is.EqualTo(1));
            Assert.That((plan.LeftEye, plan.RightEye), Is.EqualTo((bones["EyeL"], bones["EyeR"])));
            Assert.That(plan.LookUp, Is.EqualTo(12f));
            Assert.That(plan.Toggles.Select(t => t.Name), Is.EqualTo(new[] { "Happy" }), "Perfect sync's JawOpen isn't a toggle");
            var tail = plan.PhysBones.Single();
            Assert.That(tail.Root, Is.SameAs(chain));
            Assert.That(tail.StillRoot, Is.False);
            Assert.That(tail.Endpoint, Is.EqualTo(Vector3.zero), "A VRM 1.0 chain ends at its last joint");
            Assert.That(tail.Pull, Is.EqualTo(2f / 6f).Within(1e-5f));
            Assert.That(tail.Spring, Is.EqualTo(.5f / .9f).Within(1e-5f));

            ReadyConversion.Apply(plan);

            Assert.That(root.GetComponentsInChildren<Component>(true).Where(c => c != null && ReadyPlan.IsVrm(c.GetType())), Is.Empty, "The instance went before the humanoid it needs");
            Assert.That(VRC.SDK3.Validation.AvatarValidation.FindIllegalComponents(root).Select(c => c.GetType().Name), Is.Empty);
            Assert.That(root.transform.Find("secondary"), Is.Null);
            var collider = bones["Hips"].GetComponent<VRCPhysBoneCollider>();
            Assert.That(collider.shapeType, Is.EqualTo(VRCPhysBoneColliderBase.ShapeType.Capsule));
            Assert.That(collider.height, Is.EqualTo(.31f).Within(1e-5f));
            var physBone = root.GetComponentsInChildren<VRCPhysBone>(true).Single();
            Assert.That(physBone.rootTransform, Is.SameAs(chain));
            Assert.That(physBone.colliders.Single(), Is.SameAs(collider));

            Undo.PerformUndo();
            Assert.That(root.GetComponent<Vrm10Instance>(), Is.Not.Null);
            Assert.That(root.GetComponent<UniHumanoid.Humanoid>(), Is.Not.Null);
            Assert.That(bones["Chest"].GetComponent<Vrm10RotationConstraint>(), Is.Not.Null);
            Assert.That(root.GetComponent<VRCAvatarDescriptor>() == null, Is.True);
        }

        // ---- Fixtures ----

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        private BlendShapeClip Clip(BlendShapePreset preset, string name, string shape)
        {
            var clip = Own(ScriptableObject.CreateInstance<BlendShapeClip>());
            clip.Preset = preset;
            clip.BlendShapeName = name;
            clip.Values = new[] { new BlendShapeBinding { RelativePath = "Face", Index = face.sharedMesh.GetBlendShapeIndex(shape), Weight = 100 } };
            return clip;
        }

        private VRM10Expression Expression(string name, string shape)
        {
            var clip = Own(ScriptableObject.CreateInstance<VRM10Expression>());
            clip.name = name;
            clip.MorphTargetBindings = new[] { new MorphTargetBinding("Face", face.sharedMesh.GetBlendShapeIndex(shape), 1f) };
            return clip;
        }

        private static Transform Chain(string name, Transform parent, int length, Vector3 first)
        {
            Transform start = null, at = parent;
            for (int i = 0; i < length; i++)
            {
                var bone = new GameObject(name + "_" + i).transform;
                bone.SetParent(at, false);
                bone.localPosition = i == 0 ? first : Vector3.down * .05f;
                if (start == null) start = bone;
                at = bone;
            }
            return start;
        }

        private SkinnedMeshRenderer Face()
        {
            var renderer = new GameObject("Face").AddComponent<SkinnedMeshRenderer>();
            renderer.transform.SetParent(root.transform, false);
            var mesh = Own(new Mesh { name = "Face", vertices = new[] { new Vector3(0, 1.5f, 0), new Vector3(.1f, 1.5f, 0), new Vector3(0, 1.6f, 0) }, triangles = new[] { 0, 1, 2 } });
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            mesh.bindposes = new[] { bones["Head"].worldToLocalMatrix };
            var delta = new[] { Vector3.forward * .01f, Vector3.zero, Vector3.zero };
            foreach (var shape in Shapes) mesh.AddBlendShapeFrame(shape, 100, delta, null, null);
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { bones["Head"] };
            return renderer;
        }

        // A humanoid skeleton (hips 1 m up) with eye bones under the head, mapped in the rig or not.
        private Dictionary<string, Transform> Humanoid(bool eyesInRig)
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
            Add("EyeL", "Head", new Vector3(-.03f, .06f, .07f)); Add("EyeR", "Head", new Vector3(.03f, .06f, .07f));
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
            if (eyesInRig) { map["LeftEye"] = "EyeL"; map["RightEye"] = "EyeR"; }
            var description = new HumanDescription
            {
                human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
            };
            var avatar = Own(AvatarBuilder.BuildHumanAvatar(root, description));
            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            Assert.That(animator.isHuman, Is.True);
            return human;
        }
    }
}
