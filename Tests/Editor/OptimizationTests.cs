using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The Optimization card: the upload steps follow the chosen level on the build copy only, textures are sized from
    // their density on the meshes, and the estimate measures a hidden copy without touching the avatar.
    public sealed class OptimizationTests
    {
        private readonly List<Object> owned = new List<Object>();
        private readonly List<string> folders = new List<string>();
        private Scene scene;
        private GameObject root;
        private MyAvatar settings;
        private AnimatorController fx;
        private Transform[] bones;

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Avatar");
            SceneManager.MoveGameObjectToScene(root, scene);
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            fx = Own(new AnimatorController()); fx.AddLayer("Base");
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
            settings = root.AddComponent<MyAvatar>();
            bones = new Transform[3];
            for (int i = 0; i < 3; i++)
            {
                bones[i] = new GameObject("Bone" + i).transform;
                bones[i].SetParent(i == 0 ? root.transform : bones[i - 1], false);
                bones[i].localPosition = i == 0 ? Vector3.zero : Vector3.up;
            }
        }

        [TearDown] public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            foreach (var folder in folders) if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            folders.Clear();
            OptimizationEstimate.Clear();
            OptimizationSection.ShowQuest = null;
        }

        [Test] public void BigGivesQuestUploadsAstcAtHalfThePcSize()
        {
            string folder = "Assets/Orbiters/MyAvatar/OptimizationTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            var texture = Image(folder, "Board_BaseColor", 1024);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            Board(material);
            settings.uploadOptimization.level = OptimizationLevel.Small;
            Assert.That(TextureOptimization.Build(settings, true).For(texture), Is.Null, "Small keeps the Quest format: 1,024 px is what one meter needs.");

            settings.uploadOptimization.level = OptimizationLevel.Big;
            var pc = TextureOptimization.Build(settings, false).For(texture);
            var quest = TextureOptimization.Build(settings, true).For(texture);
            Assert.That(pc.width, Is.EqualTo(512), "Sharp from 3 m: a meter of surface needs about 300 texels, rounded up to a power of two.");
            Assert.That(quest.format, Is.EqualTo(TextureFormat.ASTC_6x6));
            Assert.That(quest.width, Is.EqualTo(pc.width / 2), "Half the PC size.");
        }

        [Test] public void TexturesAreOnlyEverMadeSmaller()
        {
            string folder = "Assets/Orbiters/MyAvatar/OptimizationTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            // 128 px on a meter: sharp from 8 m only, yet it keeps its size from closer.
            var texture = Image(folder, "Board_BaseColor", 128);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            Board(material);
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.ResizeTextures;
            settings.uploadOptimization.sharpFrom = .5f;
            var change = TextureOptimization.Build(settings, false).For(texture);
            Assert.That(change, Is.Not.Null, "Uncompressed, it still gets compressed.");
            Assert.That(change.width, Is.EqualTo(128));
            Assert.That(change.after, Is.LessThan(change.before));
        }

        // The upload gets copies at the plan's size and format on copies of the materials; the avatar's textures, materials
        // and import settings stay as they are, and the next upload reads the copies from Library.
        [Test] public void TheUploadGetsCopiesAndTheAvatarKeepsItsTextures()
        {
            string folder = "Assets/Orbiters/MyAvatar/OptimizationTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            var texture = Image(folder, "Board_BaseColor", 1024, noisy: true);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            Board(material);
            // A toggle swaps in another material (an asset, as on an avatar) showing the same texture.
            var swapped = new Material(material) { name = "Swapped" };
            AssetDatabase.CreateAsset(swapped, folder + "/Swapped.mat");
            var clip = Own(new AnimationClip { name = "Swap" });
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Board", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = swapped } });
            fx.layers[0].stateMachine.AddState("Swap").motion = clip;
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
            string imported = EditorJsonUtility.ToJson(importer);
            settings.uploadOptimization.level = OptimizationLevel.Big;
            foreach (bool quest in new[] { false, true })
            {
                var plan = TextureOptimization.Build(settings, quest);
                var change = plan.For(texture);
                var holder = new GameObject("Build");
                SceneManager.MoveGameObjectToScene(holder, scene);
                var copy = Object.Instantiate(root, holder.transform);
                copy.name = root.name;
                try
                {
                    Assert.That(UploadTextures.Apply(copy, plan), Is.EqualTo(1));
                    var shown = copy.GetComponentInChildren<MeshRenderer>().sharedMaterial;
                    var uploaded = (Texture2D)shown.GetTexture("_MainTex");
                    owned.Add(shown); owned.Add(uploaded);
                    Assert.That(shown, Is.Not.SameAs(material), "A copy of the material shows the copy.");
                    Assert.That(uploaded.width, Is.EqualTo(change.width), quest ? "Quest" : "PC");
                    Assert.That(uploaded.format, Is.EqualTo(change.format), quest ? "Quest" : "PC");
                    Assert.That(uploaded.mipmapCount, Is.GreaterThan(1), "With its mips.");
                    var built = AttachmentAnimationBuild.Prepare(copy).Clips.Select(c => c.Clip).Single();
                    var animated = (Material)AnimationUtility.GetObjectReferenceCurve(built, AnimationUtility.GetObjectReferenceCurveBindings(built).Single())[0].value;
                    owned.Add(animated);
                    Assert.That(animated, Is.Not.EqualTo(swapped), "The build's clip swaps in a copy too.");
                    Assert.That(animated.GetTexture("_MainTex"), Is.EqualTo(uploaded));
                    var again = UploadTextures.Copy(change); owned.Add(again);
                    Assert.That(again.GetRawTextureData(), Is.EqualTo(uploaded.GetRawTextureData()), "The next upload reads the same copy.");
                }
                finally { AttachmentAnimationBuild.Release(copy); Object.DestroyImmediate(holder); }
            }
            Assert.That(material.GetTexture("_MainTex"), Is.SameAs(texture));
            Assert.That(swapped.GetTexture("_MainTex"), Is.SameAs(texture));
            // Unity objects compare by instance: an asset read back from a curve can come with another C# wrapper.
            Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, AnimationUtility.GetObjectReferenceCurveBindings(clip).Single())[0].value, Is.EqualTo(swapped));
            Assert.That(EditorJsonUtility.ToJson(importer), Is.EqualTo(imported), "The import settings stay as they are.");
        }

        [Test] public void UploadRunsTheChosenLevelOnTheBuildCopyOnly()
        {
            var body = Skinned("Body");
            var hidden = new GameObject("Hidden"); hidden.transform.SetParent(root.transform, false); hidden.SetActive(false);
            var source = body.sharedMesh;

            Upload(root);
            Assert.That(root.transform.Find("Hidden"), Is.Not.Null, "Off: the avatar uploads as it is.");

            settings.uploadOptimization.level = OptimizationLevel.Small;
            // The SDK's build copy, made here beside the avatar.
            var holder = new GameObject("Build");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var copy = Object.Instantiate(root, holder.transform);
            Upload(copy);
            var built = copy.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single();
            Own(built.sharedMesh);
            Assert.That(copy.transform.Find("Hidden"), Is.Null, "Small removes what can never show.");
            Assert.That(built.sharedMesh.blendShapeCount, Is.EqualTo(0), "Nothing animates the shape: it leaves the mesh.");
            Assert.That(built.bones.Length, Is.EqualTo(2), "The bone no vertex follows leaves the list.");
            Assert.That(root.transform.Find("Hidden"), Is.Not.Null, "The scene avatar never changes.");
            Assert.That(body.sharedMesh, Is.SameAs(source));
            Assert.That(source.blendShapeCount, Is.EqualTo(1));
        }

        [Test] public void CustomRunsOnlyTheChangesTurnedOn()
        {
            var body = Skinned("Body");
            var hidden = new GameObject("Hidden"); hidden.transform.SetParent(root.transform, false); hidden.SetActive(false);
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.HiddenObjects;

            Upload(root);

            Assert.That(root.transform.Find("Hidden"), Is.Null);
            Assert.That(body.sharedMesh.blendShapeCount, Is.EqualTo(1), "Blendshapes are not turned on.");
            Assert.That(body.bones.Length, Is.EqualTo(3));
        }

        [Test] public void TexturesAreSizedFromWhatTheyCoverOnTheMesh()
        {
            // A one-meter square with the whole texture on it.
            var quad = new GameObject("Board");
            quad.transform.SetParent(root.transform, false);
            var mesh = Own(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one },
                triangles = new[] { 0, 2, 1, 1, 2, 3 },
            });
            quad.AddComponent<MeshFilter>().sharedMesh = mesh;
            var texture = Own(new Texture2D(2048, 2048));
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            quad.AddComponent<MeshRenderer>().sharedMaterial = material;

            var meters = TextureDensity.MetersPerTexture(settings, TextureMatching.Slots(settings));
            Assert.That(meters[texture], Is.EqualTo(1f).Within(1e-3f), "The texture's width covers a meter.");
            // A thousand screen pixels across a meter seen from a meter: 1,024 texels; twice as close, twice as many.
            Assert.That(TextureDensity.Cap(meters[texture], false, false, 1f), Is.EqualTo(1024));
            Assert.That(TextureDensity.Cap(meters[texture], false, false, .5f), Is.EqualTo(2048));
            Assert.That(TextureDensity.Cap(meters[texture], false, false, 2f), Is.EqualTo(512));
            Assert.That(TextureDensity.SharpFrom(1024, 1f), Is.EqualTo(.98f).Within(.03f));
            Assert.That(TextureDensity.Cap(.01f, false, false, 1f), Is.EqualTo(TextureDensity.MinSize), "A tiny part keeps a texture worth its detail.");

            material.SetTextureScale("_MainTex", new Vector2(2, 2));
            EditorUtility.SetDirty(material);
            var tiled = TextureDensity.MetersPerTexture(settings, TextureMatching.Slots(settings));
            Assert.That(tiled[texture], Is.EqualTo(.5f).Within(1e-3f), "Tiled twice, it repeats every half meter.");

            // Without a measure: the sizes My Avatar always used at one meter, scaled with the distance; matcaps by direction.
            Assert.That(TextureDensity.Cap(null, false, false, 1f), Is.EqualTo(TextureOptimization.MaxSize));
            Assert.That(TextureDensity.Cap(null, true, false, 1f), Is.EqualTo(TextureOptimization.BodyMaxSize));
            Assert.That(TextureDensity.Cap(null, false, false, .5f), Is.EqualTo(1024));
            Assert.That(TextureDensity.Cap(null, false, true, 1f), Is.EqualTo(512));
            Assert.That(TextureDensity.ViewMapped("_MatCapTex MatCap"), Is.True);
        }

        [Test] public void BigCrunchesColourTexturesAndHalvesMetallicMaps()
        {
            string folder = "Assets/Orbiters/MyAvatar/OptimizationTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            var color = Image(folder, "Board_BaseColor", 1024, noisy: true);
            var metallic = Image(folder, "Board_Metallic", 1024);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", color);
            material.SetTexture("_MetallicGlossMap", metallic);
            Board(material);
            // Big's distance without crunch and halving, then Big.
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.sharpFrom = UploadOptimization.BigSharpFrom;
            settings.uploadOptimization.custom = UploadOptimization.Big & ~(OptimizationPasses.CrunchTextures | OptimizationPasses.HalfSurfaceMaps);
            var plain = TextureOptimization.Build(settings, false);
            settings.uploadOptimization.level = OptimizationLevel.Big;
            var big = TextureOptimization.Build(settings, false);

            var colorPlain = plain.For(color);
            var colorBig = big.For(color);
            Assert.That(colorPlain.format, Is.EqualTo(TextureFormat.DXT1));
            Assert.That(colorBig.format, Is.EqualTo(TextureFormat.DXT1Crunched), "Big crunches colour.");
            Assert.That(colorBig.quality, Is.EqualTo(TextureOptimization.CrunchQuality));
            Assert.That(colorBig.after, Is.EqualTo(colorPlain.after), "Crunch shrinks the download, not memory.");
            Assert.That(big.For(metallic).width, Is.EqualTo(plain.For(metallic).width / 2), "A metallic map at half the size the others get.");
            long Download(TextureOptimization.TexturePlan c) => DownloadSize.TextureBytes(c.texture, false, c);
            Assert.That(Download(colorBig), Is.LessThan(Download(colorPlain)), "The download estimate sees the crunch.");
        }

        [Test] public void TheSameImageImportedTwiceUploadsOnce()
        {
            string folder = "Assets/Orbiters/MyAvatar/OptimizationTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            var first = Image(folder, "Shirt_BaseColor", 256);
            var second = Image(folder, "Shirt_BaseColor copy", 256);
            var other = Image(folder, "Pants_BaseColor", 256, noisy: true);
            var shirt = Own(new Material(Shader.Find("Standard"))); shirt.SetTexture("_MainTex", first);
            var copy = Own(new Material(Shader.Find("Standard"))); copy.SetTexture("_MainTex", second); copy.SetTexture("_DetailAlbedoMap", other);
            var board = new GameObject("Boards"); board.transform.SetParent(root.transform, false);
            board.AddComponent<MeshFilter>().sharedMesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2, 0, 2, 1 }, subMeshCount = 1 });
            var renderer = board.AddComponent<MeshRenderer>(); renderer.sharedMaterials = new[] { shirt, copy };
            var report = new UploadOptimizer.Report();

            UploadOptimizer.RunMaterials(root, UploadOptimization.Small, report);

            Assert.That(report.DuplicateTextures, Is.EqualTo(1));
            var after = renderer.sharedMaterials;
            owned.AddRange(after);
            Assert.That(after[0].GetTexture("_MainTex"), Is.SameAs(after[1].GetTexture("_MainTex")), "Both show one of the two.");
            Assert.That(after.Count(m => m != shirt && m != copy), Is.EqualTo(1), "Only the material pointing at the other one is copied.");
            Assert.That(after[1].GetTexture("_DetailAlbedoMap"), Is.SameAs(other), "Another image stays.");
            Assert.That(shirt.GetTexture("_MainTex"), Is.SameAs(first), "The material assets stay as they are.");
            Assert.That(copy.GetTexture("_MainTex"), Is.SameAs(second));
        }

        [Test] public void FewerPolygonsKeepTheSurfaceAndWhatExpressionsMove()
        {
            // A flat meter of 60 by 60 quads, skinned to the root, its corner moved by an expression.
            const int cells = 60;
            var go = new GameObject("Plane"); go.transform.SetParent(root.transform, false);
            var plane = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = Own(new Mesh { name = "Plane" });
            var vertices = new List<Vector3>(); var indices = new List<int>();
            for (int y = 0; y <= cells; y++) for (int x = 0; x <= cells; x++) vertices.Add(new Vector3(x / (float)cells, y / (float)cells, 0));
            for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                {
                    int i = y * (cells + 1) + x;
                    indices.AddRange(new[] { i, i + cells + 1, i + 1, i + 1, i + cells + 1, i + cells + 2 });
                }
            mesh.SetVertices(vertices); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals();
            mesh.boneWeights = vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };
            var smile = vertices.Select(v => v.x < .2f && v.y < .2f ? Vector3.forward * .01f : Vector3.zero).ToArray();
            mesh.AddBlendShapeFrame("Smile", 100, smile, null, null);
            plane.sharedMesh = mesh; plane.bones = new[] { root.transform }; plane.rootBone = root.transform;
            var clip = Own(new AnimationClip());
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Plane", typeof(SkinnedMeshRenderer), "blendShape.Smile"), AnimationCurve.Constant(0, 1, 100));
            fx.layers[0].stateMachine.AddState("Smile").motion = clip;
            var corner = new HashSet<Vector3>(Centers(mesh, v => v.x < .2f && v.y < .2f));

            var made = new List<Mesh>();
            int removed = PolygonReduction.Reduce(new Toolkit.Editor.VRChat.Optimization.AvatarUsage(root), 1f, made);
            owned.AddRange(made);

            var reduced = plane.sharedMesh;
            Assert.That(removed, Is.GreaterThan(cells * cells), "A flat surface needs a fraction of its triangles.");
            Assert.That(reduced.blendShapeCount, Is.EqualTo(1));
            Assert.That(Toolkit.Editor.VRChat.Optimization.MeshParts.Within(mesh.vertices, reduced.vertices, reduced.triangles, PolygonReduction.Budget(1f)),
                "Every point stays within half a millimetre of the surface.");
            Assert.That(Centers(reduced, _ => true), Is.SupersetOf(corner), "What the expression moves keeps its triangles.");
            var deltas = new Vector3[reduced.vertexCount];
            reduced.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
            Assert.That(deltas.Count(d => d != Vector3.zero), Is.EqualTo(smile.Count(d => d != Vector3.zero)), "The expression moves the same vertices.");
        }

        // The centers of a mesh's triangles whose three vertices pass the test, rounded to a tenth of a millimetre.
        private static IEnumerable<Vector3> Centers(Mesh mesh, Func<Vector3, bool> test)
        {
            var v = mesh.vertices; var t = mesh.triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
                if (test(v[t[i]]) && test(v[t[i + 1]]) && test(v[t[i + 2]]))
                {
                    var c = (v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f;
                    yield return new Vector3(Mathf.Round(c.x * 1e4f), Mathf.Round(c.y * 1e4f), Mathf.Round(c.z * 1e4f));
                }
        }

        [Test] public void TheEstimateMeasuresAHiddenCopyThroughTheChosenChanges()
        {
            Skinned("Body");
            var hidden = Skinned("Old shirt"); hidden.gameObject.SetActive(false);
            settings.uploadOptimization.level = OptimizationLevel.Small;
            int objects = root.GetComponentsInChildren<Transform>(true).Length;

            Assert.That(OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null), Is.Null, "Measured over editor frames.");
            OptimizationEstimate.Finish();
            var result = OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Now.Skinned, Is.EqualTo(2), "VRChat ranks the hidden mesh too.");
            Assert.That(result.Upload.Skinned, Is.EqualTo(1));
            Assert.That(result.Upload.Bones, Is.LessThan(result.Now.Bones));
            Assert.That(result.Report.HiddenObjects, Is.EqualTo(1));
            Assert.That(result.Report.BlendShapes, Is.EqualTo(1), "Blendshapes are counted, not removed.");
            Assert.That(result.Upload.BlendShapes, Is.EqualTo(0));
            Assert.That(result.Upload.Download, Is.LessThan(result.Now.Download), "The hidden mesh and the unused shape leave the download.");
            Assert.That(result.Now.Download, Is.GreaterThan(0));
            Assert.That(root.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(objects), "The avatar is never touched.");
            Assert.That(hidden.sharedMesh.blendShapeCount, Is.EqualTo(1));
        }

        [Test] public void TheEstimateShowsOnlyThePlatformTheProjectBuildsFor()
        {
            Skinned("Body");
            settings.uploadOptimization.level = OptimizationLevel.Small;
            OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            OptimizationEstimate.Finish();
            foreach (bool quest in new[] { false, true })
            {
                OptimizationSection.ShowQuest = quest;
                var section = new OptimizationSection(settings);
                var bars = section.Query<VrcLoadingBar>().ToList();
                Assert.That(bars, Has.Count.EqualTo(1), "One loading bar: the platform the project builds for.");
                var row = bars[0].parent;
                Assert.That(row.Q<Label>(className: "opt-rank__platform").text, Is.EqualTo(quest ? "Quest" : "PC"));
                var texts = section.Query<Label>().ToList().Select(l => l.text).ToList();
                Assert.That(texts, Has.Some.EndsWith(quest ? " ON QUEST" : " ON PC"), "The eyebrow ranks the platform the project builds for.");
                Assert.That(texts, Has.None.EndsWith(quest ? " ON PC" : " ON QUEST"), "Nothing about the other platform.");
            }
        }

        // A one-meter square under the avatar showing the material.
        private void Board(Material material)
        {
            var quad = new GameObject("Board");
            quad.transform.SetParent(root.transform, false);
            var mesh = Own(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one },
                triangles = new[] { 0, 2, 1, 1, 2, 3 },
            });
            quad.AddComponent<MeshFilter>().sharedMesh = mesh;
            quad.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        // An opaque, uncompressed PNG imported under the folder.
        private static Texture2D Image(string folder, string name, int size, bool noisy = false)
        {
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var random = new System.Random(1);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = noisy ? new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255) : new Color32((byte)(i % 251), (byte)(i / size % 253), 90, 255);
            image.SetPixels32(pixels); image.Apply();
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureCompression = TextureImporterCompression.Uncompressed; importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Both upload steps, as the SDK calls them.
        private static void Upload(GameObject avatar)
        {
            new UploadOptimizer.Structure().OnPreprocessAvatar(avatar);
            new UploadOptimizer.Materials().OnPreprocessAvatar(avatar);
        }

        // A quad on each of the first two bones (the third lists without weight) with one unused blendshape.
        private SkinnedMeshRenderer Skinned(string name)
        {
            var renderer = new GameObject(name).AddComponent<SkinnedMeshRenderer>();
            renderer.transform.SetParent(root.transform, false);
            var mesh = Own(new Mesh { name = name });
            var vertices = new List<Vector3>(); var weights = new List<BoneWeight>();
            for (int bone = 0; bone < 2; bone++)
                foreach (var corner in new[] { new Vector3(0, bone, 0), new Vector3(1, bone, 0), new Vector3(0, bone + .5f, 0), new Vector3(1, bone + .5f, 0) })
                {
                    vertices.Add(corner);
                    weights.Add(new BoneWeight { boneIndex0 = bone, weight0 = 1 });
                }
            mesh.SetVertices(vertices);
            mesh.boneWeights = weights.ToArray();
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3, 4, 6, 5, 5, 6, 7 };
            mesh.RecalculateNormals();
            mesh.bindposes = bones.Select(b => b.worldToLocalMatrix).ToArray();
            mesh.AddBlendShapeFrame("Unused", 100, vertices.Select(_ => Vector3.forward).ToArray(), null, null);
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[0];
            renderer.sharedMaterial = Own(new Material(Shader.Find("Standard")));
            return renderer;
        }

        private T Own<T>(T value) where T : Object { if (value != null) owned.Add(value); return value; }
    }
}
