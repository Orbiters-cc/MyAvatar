using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.FaceTracking;
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

        [Test] public void PerfectSyncGetsEveryARKitClipAndARKitNamesOnTheMesh()
        {
            // 40 of ARKit's 52 shapes, one under Unified Expressions' name.
            var arKit = FaceTrackingStandard.ARKit.Core;
            face.sharedMesh = FaceMesh(arKit.Take(40).Select(n => n == "eyeWideRight" ? "EyeWideRight" : n).ToArray());

            var model = VrmModel.From(root, new VrmExportSettings(), "Test", null);

            var custom = model.Expressions.Where(e => e.Preset == null).ToList();
            Assert.That(custom.Select(e => e.Name), Is.EquivalentTo(arKit.Select(n => char.ToUpperInvariant(n[0]) + n.Substring(1))),
                "VNyan and VSeeFace use perfect sync only with all 52 clips: the missing ones are there, empty.");
            Assert.That(custom.Count(e => e.Shapes.Count == 0), Is.EqualTo(12));
            var mesh = face.sharedMesh;
            Assert.That(mesh.GetBlendShapeIndex("eyeWideRight"), Is.GreaterThanOrEqualTo(0), "Warudo finds the mesh's shapes by ARKit's exact names.");
            Assert.That(mesh.GetBlendShapeIndex("EyeWideRight"), Is.EqualTo(-1));
            Assert.That(custom.Single(e => e.Name == "EyeWideRight").Shapes.Single().Shape, Is.EqualTo("eyeWideRight"));
        }

        [Test] public void AFaceWithFewARKitShapesGetsNoEmptyClips()
        {
            var model = VrmModel.From(root, new VrmExportSettings(), "Test", null);
            Assert.That(model.Expressions.Where(e => e.Preset == null).All(e => e.Shapes.Count > 0), Is.True,
                "Without the eyes', the jaw's and half of the others, perfect sync would take over blinking and talking with nothing.");
        }

        [Test] public void KeepEveryBlendshapeKeepsTheOnesNoExpressionSets()
        {
            VrmModel.From(root, new VrmExportSettings { keepAllShapes = true }, "Test", null);
            Assert.That(face.sharedMesh.GetBlendShapeIndex("Unused"), Is.GreaterThanOrEqualTo(0));
        }

        // UniVRM 0.x moves every bone under a spring's root: a fork, a PhysBone inside another's and the humanoid bones
        // under a PhysBone on the hips each move once, or not at all.
        [Test] public void Vrm0SpringRootsMoveEachBoneOnceAndNeverTheHumanoid()
        {
            var writer = VrmExport.Writer(0);
            if (writer == null) Assert.Ignore("UniVRM 0.x isn't installed.");
            var tail = Chain("Tail", bones["Hips"], 3);
            var fork = new GameObject("Fork").transform; fork.SetParent(tail.GetChild(0), false); fork.localPosition = new Vector3(.1f, 0, -.1f);
            tail.gameObject.AddComponent<VRCPhysBone>();
            Chain("Skirt", bones["Hips"], 2);
            // A skirt's PhysBone on the hips, as many are made.
            bones["Hips"].gameObject.AddComponent<VRCPhysBone>();
            var copy = Object.Instantiate(root);
            SceneManager.MoveGameObjectToScene(copy, scene);

            var json = Json(writer.Write(VrmModel.From(copy, new VrmExportSettings { authors = "Test", licenseAllows = true }, "Test", null)));

            var names = NodeNames(json);
            var secondary = json.Substring(json.IndexOf("\"secondaryAnimation\"", System.StringComparison.Ordinal));
            var roots = Regex.Matches(secondary.Substring(0, secondary.IndexOf("\"specVersion\"", System.StringComparison.Ordinal)), "\"bones\":\\[([0-9,]*)\\]")
                .Cast<Match>().SelectMany(m => m.Groups[1].Value.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)).Select(i => names[int.Parse(i)]).ToList();
            Assert.That(roots, Is.EquivalentTo(new[] { "Tail0", "Skirt0" }), secondary.Substring(0, System.Math.Min(secondary.Length, 1500)));
        }

        // lilToon: its shadow's colour, border and blur, its rim light, an added matcap and its outline (an outline shader).
        [Test] public void ALilToonMaterialKeepsItsShadingRimMatcapAndOutline()
        {
            var matcap = Own(new Texture2D(4, 4)); var bump = Own(new Texture2D(4, 4)); var mask = Own(new Texture2D(4, 4));
            var material = Toon("Hidden/Orbiters Test/lilToonOutline",
                new[] { ("_lilToonVersion", 44f), ("_UseShadow", 1f), ("_ShadowBorder", .6f), ("_ShadowBlur", .05f), ("_ShadowStrength", 1f), ("_UseRim", 1f),
                    ("_RimFresnelPower", 2f), ("_RimEnableLighting", 1f), ("_UseMatCap", 1f), ("_MatCapBlendMode", 1f), ("_MatCapBlend", 1f), ("_OutlineWidth", .1f),
                    ("_UseBumpMap", 0f), ("_BumpScale", 1f), ("_Cull", 2f) },
                new[] { ("_Color", Color.white), ("_ShadowColor", new Color(.5f, .5f, .5f, 1)), ("_RimColor", new Color(1, 0, 0, .5f)), ("_MatCapColor", Color.white),
                    ("_OutlineColor", new Color(.1f, .2f, .3f, 1)) },
                new[] { ("_MainTex", (Texture)null), ("_MatCapTex", matcap), ("_BumpMap", bump), ("_OutlineWidthMask", mask) });

            var read = VrmModel.Read(material);

            Assert.That(read.ShadeColor.r, Is.EqualTo(.5f).Within(1e-4f), "The shadow's colour over the base");
            Assert.That(read.ShadeCenter, Is.EqualTo(.2f).Within(1e-4f), "Border 0.6 of half Lambert is 0.2 of N·L");
            Assert.That(read.ShadeWidth, Is.EqualTo(.1f).Within(1e-4f));
            Assert.That((read.RimColor.r, read.RimColor.g), Is.EqualTo((.5f, 0f)), "The rim's colour as bright as its alpha");
            Assert.That(read.RimFresnelPower, Is.EqualTo(2f));
            Assert.That(read.MatcapTexture, Is.SameAs(matcap), "An added matcap");
            Assert.That(read.OutlineWidth, Is.EqualTo(.1f).Within(1e-5f), "Centimetres, as lilToon gives them");
            Assert.That(read.OutlineWidthTexture, Is.SameAs(mask));
            Assert.That(read.NormalTexture, Is.Null, "lilToon's normal map is off");

            face.sharedMaterial = material;
            foreach (int version in new[] { 0, 1 })
            {
                var writer = VrmExport.Writer(version);
                if (writer == null) continue;
                var copy = Object.Instantiate(root);
                SceneManager.MoveGameObjectToScene(copy, scene);
                var json = Json(writer.Write(Model(copy)));
                if (version == 0)
                    Assert.That(json, Does.Contain("\"_OutlineWidthMode\":1").And.Contain("\"_SphereAdd\"").And.Contain("\"_RimColor\""), "MToon 0.x");
                else
                    Assert.That(json, Does.Contain("\"outlineWidthMode\":\"worldCoordinates\"").And.Contain("\"matcapTexture\"").And.Contain("\"parametricRimColorFactor\""), "MToon 1.0");
            }
        }

        // Poiyomi: flat lighting has no shade; its outline, the added part of its matcap and its emission's strength.
        [Test] public void APoiyomiMaterialKeepsItsFlatShadingOutlineMatcapAndEmission()
        {
            var matcap = Own(new Texture2D(4, 4));
            var material = Toon(".poiyomi/Orbiters Test Toon",
                new[] { ("_LightingMode", 5f), ("_EnableOutlines", 1f), ("_LineWidth", 2f), ("_MatcapEnable", 1f), ("_MatcapAdd", .5f), ("_MatcapIntensity", 1f),
                    ("_EnableEmission", 1f), ("_EmissionStrength", 2f), ("_Cull", 0f) },
                new[] { ("_Color", new Color(1, .5f, .5f, 1)), ("_LineColor", Color.blue), ("_MatcapColor", Color.white), ("_EmissionColor", Color.yellow) },
                new[] { ("_MainTex", (Texture)null), ("_Matcap", matcap), ("_OutlineMask", (Texture)null), ("_EmissionMap", (Texture)null) });

            var read = VrmModel.Read(material);

            Assert.That((read.ShadeColor.r, read.ShadeColor.g), Is.EqualTo((1f, .5f)), "Flat lighting: the shade is the lit colour");
            Assert.That(read.OutlineWidth, Is.EqualTo(2f));
            Assert.That(read.OutlineColor, Is.EqualTo(Color.blue));
            Assert.That(read.MatcapTexture, Is.SameAs(matcap));
            Assert.That(read.MatcapColor.r, Is.EqualTo(.5f).Within(1e-4f), "Only the matcap's added part");
            Assert.That((read.Emission, read.EmissionStrength), Is.EqualTo((Color.yellow, 2f)));
            Assert.That(read.DoubleSided, Is.True);
        }

        // Eyes only the VRChat descriptor names: the file's humanoid has them, so apps turning eye bones find them.
        [Test] public void TheDescriptorsEyesBecomeTheHumanoidsEyeBones()
        {
            var left = new GameObject("EyeL").transform; left.SetParent(bones["Head"], false); left.localPosition = new Vector3(-.03f, .05f, .07f);
            var right = new GameObject("EyeR").transform; right.SetParent(bones["Head"], false); right.localPosition = new Vector3(.03f, .05f, .07f);
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            descriptor.enableEyeLook = true;
            var eyes = descriptor.customEyeLookSettings;
            eyes.leftEye = left; eyes.rightEye = right;
            descriptor.customEyeLookSettings = eyes;
            Assert.That(root.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftEye), Is.Null, "The rig has no eyes");

            foreach (int version in new[] { 0, 1 })
            {
                var copy = Object.Instantiate(root);
                SceneManager.MoveGameObjectToScene(copy, scene);
                var model = Model(copy);
                var animator = copy.GetComponent<Animator>();
                Assert.That(animator.GetBoneTransform(HumanBodyBones.LeftEye), Is.SameAs(copy.transform.Find("Hips/Spine/Chest/Neck/Head/EyeL")));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.RightEye), Is.SameAs(copy.transform.Find("Hips/Spine/Chest/Neck/Head/EyeR")));
                Assert.That(animator.GetBoneTransform(HumanBodyBones.Head), Is.SameAs(copy.transform.Find("Hips/Spine/Chest/Neck/Head")), "Every other bone as before");
                var writer = VrmExport.Writer(version);
                if (writer == null) continue;
                Assert.That(Json(writer.Write(model)), Does.Contain("\"leftEye\"").And.Contain("\"rightEye\""), "VRM " + version);
            }
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

        // A model of the copy, whose own objects (a humanoid with eyes) the test frees.
        private VrmModel Model(GameObject copy)
        {
            var model = VrmModel.From(copy, new VrmExportSettings { authors = "Test", licenseAllows = true }, "Test", null);
            owned.AddRange(model.Made);
            return model;
        }

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        // A material on a stand-in shader of that name declaring these properties (an undeclared property is not kept).
        private Material Toon(string shaderName, (string name, float value)[] floats, (string name, Color value)[] colors, (string name, Texture value)[] textures)
        {
            var source = new StringBuilder("Shader \"" + shaderName + "\" { Properties {\n");
            foreach (var (name, _) in floats) source.Append(name).Append(" (\"x\", Float) = 0\n");
            foreach (var (name, _) in colors) source.Append(name).Append(" (\"x\", Color) = (1,1,1,1)\n");
            foreach (var (name, _) in textures) source.Append(name).Append(" (\"x\", 2D) = \"white\" {}\n");
            source.Append("} SubShader { Pass { } } }");
            var shader = Own(ShaderUtil.CreateShaderAsset(source.ToString()));
            var material = Own(new Material(shader));
            foreach (var (name, value) in floats) material.SetFloat(name, value);
            foreach (var (name, value) in colors) material.SetColor(name, value);
            foreach (var (name, value) in textures) material.SetTexture(name, value);
            return material;
        }

        // The glTF nodes' names, by index.
        private static List<string> NodeNames(string json)
        {
            int start = json.IndexOf("\"nodes\":[", System.StringComparison.Ordinal) + 9;
            var names = new List<string>();
            int depth = 0, from = -1;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') { i = json.IndexOf('"', i + 1); while (json[i - 1] == '\\') i = json.IndexOf('"', i + 1); continue; }
                if (c == '{' || c == '[') { if (depth++ == 0) from = i; }
                else if (c == '}' || c == ']')
                {
                    if (depth == 0) break;
                    if (--depth == 0) names.Add(Regex.Match(json.Substring(from, i - from + 1), "\"name\":\"([^\"]*)\"").Groups[1].Value);
                }
            }
            return names;
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
            renderer.sharedMesh = FaceMesh(shapes);
            renderer.bones = new[] { bones["Head"] };
            var material = new Material(Shader.Find("Standard")); owned.Add(material);
            renderer.sharedMaterial = material;
            return renderer;
        }

        private Mesh FaceMesh(string[] shapes)
        {
            var mesh = new Mesh { name = "Body", vertices = new[] { new Vector3(0, 1.5f, 0), new Vector3(.1f, 1.5f, 0), new Vector3(0, 1.6f, 0) }, triangles = new[] { 0, 1, 2 } };
            owned.Add(mesh);
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray();
            mesh.bindposes = new[] { bones["Head"].worldToLocalMatrix };
            var delta = new[] { Vector3.forward * .01f, Vector3.zero, Vector3.zero };
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100, delta, null, null);
            return mesh;
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
