using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Build;
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
        private TestCaches caches;
        private IDisposable polygons, estimates;

        [SetUp] public void SetUp()
        {
            caches = new TestCaches();
            // A cache and estimates of the tests' own: the user's (an open Inspector's) stay as they are.
            polygons = PolygonReduction.Isolated();
            estimates = OptimizationEstimate.Isolated();
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
            OptimizationEstimate.FailWith = null;
            OptimizationSection.ShowQuest = null;
            TextureOptimization.TargetOverride = null;
            UploadTextures.MakeCopy = UploadTextures.Copy;
            estimates.Dispose();
            polygons.Dispose();
            caches.Dispose();
        }

        [Test] public void BigGivesQuestUploadsAstcAtHalfThePcSize()
        {
            string folder = Folder();
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
            string folder = Folder();
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

        // Under Custom with only changes to objects and meshes, every texture uploads as it is: format included.
        [Test] public void CustomWithoutTexturePassesKeepsEveryTextureAsItIs()
        {
            string folder = Folder();
            var texture = Image(folder, "Board_BaseColor", 512);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            var board = Board(material);
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.HiddenObjects | OptimizationPasses.MaterialSlots | OptimizationPasses.DuplicateTextures;

            Assert.That(TextureOptimization.Build(settings, false).changes, Is.Empty, "Uncompressed, yet no texture pass is on.");
            Assert.That(TextureOptimization.Build(settings, true).changes, Is.Empty);
            Upload(root);
            Assert.That(board.sharedMaterial.GetTexture("_MainTex"), Is.SameAs(texture));
        }

        // The upload gets copies at the plan's size and format on copies of the materials; the avatar's textures, materials
        // and import settings stay as they are, and the next upload reads the copies from Library.
        [Test] public void TheUploadGetsCopiesAndTheAvatarKeepsItsTextures()
        {
            string folder = Folder();
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
                    Assert.That(uploaded.isReadable, Is.False, "Not readable: no second copy in memory in game.");
                    var built = AttachmentAnimationBuild.Prepare(copy).Clips.Select(c => c.Clip).Single();
                    var animated = (Material)AnimationUtility.GetObjectReferenceCurve(built, AnimationUtility.GetObjectReferenceCurveBindings(built).Single())[0].value;
                    owned.Add(animated);
                    Assert.That(animated, Is.Not.EqualTo(swapped), "The build's clip swaps in a copy too.");
                    Assert.That(animated.GetTexture("_MainTex"), Is.EqualTo(uploaded));
                    string file = UploadTextures.CacheFile(change);
                    Assert.That(file, Does.StartWith(LibraryStore.Folder), "Kept in the (test's) Library cache.");
                    var data = File.ReadAllBytes(file);
                    var again = Own(UploadTextures.Copy(change));
                    Assert.That(again.format, Is.EqualTo(change.format));
                    Assert.That(UploadTextures.Data(change), Is.EqualTo(data), "The next upload reads the same copy.");
                }
                finally { AttachmentAnimationBuild.Release(copy); Object.DestroyImmediate(holder); }
            }
            Assert.That(material.GetTexture("_MainTex"), Is.SameAs(texture));
            Assert.That(swapped.GetTexture("_MainTex"), Is.SameAs(texture));
            // Unity objects compare by instance: an asset read back from a curve can come with another C# wrapper.
            Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, AnimationUtility.GetObjectReferenceCurveBindings(clip).Single())[0].value, Is.EqualTo(swapped));
            Assert.That(EditorJsonUtility.ToJson(importer), Is.EqualTo(imported), "The import settings stay as they are.");
        }

        // A copy isn't readable (no second copy in memory in game), yet saved as an asset, as the build saves it, it keeps every
        // pixel: read back from the file alone, it shows what it showed.
        [Test] public void UploadCopiesKeepTheirPixelsOnceSaved()
        {
            string folder = Folder();
            var texture = Image(folder, "Board_BaseColor", 256, noisy: true);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            Board(material);
            settings.uploadOptimization.level = OptimizationLevel.Big;
            var change = TextureOptimization.Build(settings, false).For(texture);
            Assert.That(change, Is.Not.Null, "Uncompressed: Big compresses it.");
            var copy = UploadTextures.Copy(change);
            Assert.That(copy.isReadable, Is.False);
            var shown = Pixels(copy);
            Assert.That(shown.Any(p => p.r + p.g + p.b > 0), Is.True, "Not an empty image.");
            AssetDatabase.CreateAsset(copy, folder + "/Copy.asset");
            AssetDatabase.SaveAssets();
            // A second file of the same bytes, imported fresh: what it holds comes from the disk alone.
            File.Copy(folder + "/Copy.asset", folder + "/Saved.asset");
            AssetDatabase.ImportAsset(folder + "/Saved.asset", ImportAssetOptions.ForceSynchronousImport);
            var saved = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Saved.asset");
            Assert.That(saved, Is.Not.Null);
            Assert.That(saved, Is.Not.SameAs(copy));
            Assert.That(saved.isReadable, Is.False, "Saved not readable.");
            Assert.That(saved.format, Is.EqualTo(change.format));
            Assert.That(saved.mipmapCount, Is.EqualTo(copy.mipmapCount));
            Assert.That(Pixels(saved), Is.EqualTo(shown), "Every pixel saved.");
        }

        // A texture an animation sets on a renderer's material ("material._MainTex") is swapped for its copy in the build's clip.
        [Test] public void TexturesAnimationsSetGetTheCopies()
        {
            string folder = Folder();
            var texture = Image(folder, "Board_BaseColor", 1024, noisy: true);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            Board(material);
            var clip = Own(new AnimationClip { name = "Set" });
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Board", typeof(MeshRenderer), "material._MainTex"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = texture } });
            fx.layers[0].stateMachine.AddState("Set").motion = clip;
            settings.uploadOptimization.level = OptimizationLevel.Big;
            var plan = TextureOptimization.Build(settings, false);
            var holder = new GameObject("Build");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var copy = Object.Instantiate(root, holder.transform);
            copy.name = root.name;
            try
            {
                Assert.That(UploadTextures.Apply(copy, plan), Is.EqualTo(1));
                var uploaded = copy.GetComponentInChildren<MeshRenderer>().sharedMaterial.GetTexture("_MainTex");
                owned.Add(copy.GetComponentInChildren<MeshRenderer>().sharedMaterial); owned.Add(uploaded);
                var built = AttachmentAnimationBuild.Prepare(copy).Clips.Select(c => c.Clip).Single();
                var set = AnimationUtility.GetObjectReferenceCurve(built, AnimationUtility.GetObjectReferenceCurveBindings(built).Single())[0].value;
                Assert.That(set, Is.EqualTo(uploaded), "Only the copy ships, not the original beside it.");
            }
            finally { AttachmentAnimationBuild.Release(copy); Object.DestroyImmediate(holder); }
            Assert.That(AnimationUtility.GetObjectReferenceCurve(clip, AnimationUtility.GetObjectReferenceCurveBindings(clip).Single())[0].value, Is.EqualTo(texture));
        }

        // Every copy is made before anything changes: a copy that fails leaves the build copy as it was, and the copies made
        // before it are destroyed.
        [Test] public void AFailedCopyLeavesNothingHalfApplied()
        {
            string folder = Folder();
            var color = Image(folder, "Board_BaseColor", 512);
            var emission = Image(folder, "Board_Emission", 512, noisy: true);
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", color);
            material.SetTexture("_EmissionMap", emission);
            Board(material);
            settings.uploadOptimization.level = OptimizationLevel.Small;
            var plan = TextureOptimization.Build(settings, false);
            Assert.That(plan.changes, Has.Count.EqualTo(2), "Both are uncompressed: both change.");
            var made = new List<Texture2D>();
            UploadTextures.MakeCopy = change =>
            {
                if (made.Count == 1) throw new IOException("Fixture failure");
                var made1 = UploadTextures.Copy(change);
                made.Add(made1);
                return made1;
            };
            var holder = new GameObject("Build");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var copy = Object.Instantiate(root, holder.transform);
            try
            {
                Assert.Throws<IOException>(() => UploadTextures.Apply(copy, plan));
                var shown = copy.GetComponentInChildren<MeshRenderer>().sharedMaterial;
                Assert.That(shown, Is.SameAs(material), "The build copy keeps its materials.");
                Assert.That(shown.GetTexture("_MainTex"), Is.SameAs(color));
                Assert.That(made, Has.Count.EqualTo(1));
                Assert.That(made[0] == null, Is.True, "The copy made before the failure is destroyed.");
            }
            finally { Object.DestroyImmediate(holder); }
        }

        // A crunched texture stays crunched with its quality; a normal map keeps how its channels are laid out (DXT5nm, crunched
        // DXT5nm, BC5), and one already at its size in BC5 isn't encoded again.
        [Test] public void CrunchedTexturesAndNormalMapsKeepTheirFormats()
        {
            string folder = Folder();
            var crunched = Image(folder, "Crunched_BaseColor", 1024, compression: TextureImporterCompression.Compressed, crunch: true, quality: 60);
            var dxt5nm = Image(folder, "Plain_Normal", 1024, type: TextureImporterType.NormalMap, compression: TextureImporterCompression.Compressed);
            var crunchedNormal = Image(folder, "Crunched_Normal", 1024, type: TextureImporterType.NormalMap, compression: TextureImporterCompression.Compressed, crunch: true, quality: 60);
            var hq = Image(folder, "Fine_Normal", 1024, type: TextureImporterType.NormalMap, compression: TextureImporterCompression.CompressedHQ);
            foreach (var map in new[] { dxt5nm, crunchedNormal, hq })
            {
                var material = Own(new Material(Shader.Find("Standard")));
                material.SetTexture("_MainTex", crunched);
                material.SetTexture("_BumpMap", map);
                Board(material);
            }
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.ResizeTextures;
            settings.uploadOptimization.sharpFrom = 3f;
            var plan = TextureOptimization.Build(settings, false);

            var colour = plan.For(crunched);
            Assert.That(colour.format, Is.EqualTo(TextureFormat.DXT1Crunched), "Crunched, it stays crunched.");
            Assert.That(colour.quality, Is.EqualTo(60), "With its own quality.");
            Assert.That(colour.width, Is.EqualTo(512));
            Assert.That(colour.after, Is.LessThan(colour.before));
            Assert.That(plan.For(dxt5nm).format, Is.EqualTo(TextureFormat.DXT5), "DXT5nm keeps X in alpha: a BC5 copy of it would lose it.");
            Assert.That(plan.For(crunchedNormal).format, Is.EqualTo(TextureFormat.DXT5Crunched), "Crunched DXT5nm stays crunched.");
            Assert.That(plan.For(crunchedNormal).quality, Is.EqualTo(60));
            Assert.That(plan.For(hq).format, Is.EqualTo(TextureFormat.BC5));
            Assert.That(plan.For(hq).width, Is.EqualTo(512));

            settings.uploadOptimization.sharpFrom = 1f;
            Assert.That(TextureOptimization.Build(settings, false).For(hq), Is.Null, "At its size already in BC5: nothing to encode again.");
            var copy = Own(UploadTextures.Copy(plan.For(crunchedNormal)));
            Assert.That(copy.format, Is.EqualTo(TextureFormat.DXT5Crunched));
            Assert.That(copy.width, Is.EqualTo(512));
        }

        // BC5 holds X and Y in red and green: a copy of a normal map holding X in alpha (DXT5nm) moves it, as shaders read it.
        [Test] public void NormalMapCopiesInBc5KeepX()
        {
            string folder = Folder();
            var normal = Image(folder, "Tilted_Normal", 256, type: TextureImporterType.NormalMap, pixel: _ => new Color32(191, 140, 230, 255));
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_BumpMap", normal);
            Board(material);
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.ResizeTextures;
            settings.uploadOptimization.sharpFrom = 1f;
            var change = TextureOptimization.Build(settings, false).For(normal);
            Assert.That(change.format, Is.EqualTo(TextureFormat.BC5));
            Assert.That(change.normal, Is.True);
            var copy = Own(UploadTextures.Copy(change));
            var source = Read(normal);
            var result = Read(copy);
            // What a shader reads as X: red × alpha (DXT5nm keeps red white, BC5 alpha white).
            Assert.That(result.r * result.a / 255f, Is.EqualTo(source.r * source.a / 255f).Within(6f), "X");
            Assert.That((float)result.g, Is.EqualTo(source.g).Within(6f), "Y");
        }

        // iOS uploads read the iPhone import settings, Quest uploads the Android ones; crunch on mobile holds ETC blocks.
        [Test] public void MobileUploadsReadTheirPlatformsImportSettings()
        {
            string folder = Folder();
            var texture = Image(folder, "Board_BaseColor", 1024);
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = TextureOptimization.Android, overridden = true, maxTextureSize = 1024, format = TextureImporterFormat.ASTC_4x4 });
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = TextureOptimization.IPhone, overridden = true, maxTextureSize = 256, format = TextureImporterFormat.ASTC_8x8 });
            importer.SaveAndReimport();

            TextureOptimization.TargetOverride = BuildTarget.iOS;
            Assert.That(TextureOptimization.Mobile, Is.True);
            Assert.That(TextureOptimization.BuildSize(texture, importer, true), Is.EqualTo(new Vector2Int(256, 256)));
            Assert.That(TextureOptimization.BuildFormat(texture, true).format, Is.EqualTo(TextureFormat.ASTC_8x8));
            TextureOptimization.TargetOverride = BuildTarget.Android;
            Assert.That(TextureOptimization.BuildSize(texture, importer, true), Is.EqualTo(new Vector2Int(1024, 1024)));
            Assert.That(TextureOptimization.BuildFormat(texture, true).format, Is.EqualTo(TextureFormat.ASTC_4x4));

            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = TextureOptimization.Android, overridden = true, maxTextureSize = 1024,
                format = TextureImporterFormat.Automatic, textureCompression = TextureImporterCompression.Compressed, crunchedCompression = true, compressionQuality = 50 });
            importer.SaveAndReimport();
            var (format, crunched, quality) = TextureOptimization.BuildFormat(texture, true);
            Assert.That(crunched, Is.True);
            Assert.That(format == TextureFormat.ETC_RGB4Crunched || format == TextureFormat.ETC2_RGBA8Crunched, Is.True, "Crunch on Android is ETC, not ASTC: " + format);
            Assert.That(quality, Is.EqualTo(50));
        }

        // The same image imported twice uploads once: whichever is kept gets what the most demanding of them needs (the face's
        // size, not a small prop's).
        [Test] public void DuplicatesGetWhatTheMostDemandingOfThemNeeds()
        {
            string folder = Folder();
            var face = Image(folder, "Face_BaseColor", 1024);
            var prop = Image(folder, "Prop_BaseColor", 1024);
            var faceMaterial = Own(new Material(Shader.Find("Standard"))); faceMaterial.SetTexture("_MainTex", face);
            var propMaterial = Own(new Material(Shader.Find("Standard"))); propMaterial.SetTexture("_MainTex", prop);
            Board(faceMaterial);
            Board(propMaterial).transform.localScale = Vector3.one * .1f;
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.ResizeTextures;
            settings.uploadOptimization.sharpFrom = 1f;
            Assert.That(TextureOptimization.Build(settings, false).For(prop).width, Is.EqualTo(128), "Alone, ten centimetres need 128 px.");

            settings.uploadOptimization.custom |= OptimizationPasses.DuplicateTextures;
            var plan = TextureOptimization.Build(settings, false);
            Assert.That(plan.For(face).width, Is.EqualTo(1024));
            Assert.That(plan.For(prop).width, Is.EqualTo(1024), "Merged with the face's image, it gets the face's size.");
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

            // Scaled up, it covers more: measured again.
            quad.transform.localScale = Vector3.one * 2f;
            Assert.That(TextureDensity.MetersPerTexture(settings, TextureMatching.Slots(settings))[texture], Is.EqualTo(1f).Within(1e-3f));

            // Without a measure: the sizes My Avatar always used at one meter, scaled with the distance; matcaps by direction.
            Assert.That(TextureDensity.Cap(null, false, false, 1f), Is.EqualTo(TextureOptimization.MaxSize));
            Assert.That(TextureDensity.Cap(null, true, false, 1f), Is.EqualTo(TextureOptimization.BodyMaxSize));
            Assert.That(TextureDensity.Cap(null, false, false, .5f), Is.EqualTo(1024));
            Assert.That(TextureDensity.Cap(null, false, true, 1f), Is.EqualTo(512));
            Assert.That(TextureDensity.ViewMapped("_MatCapTex MatCap"), Is.True);
        }

        // Blender exports at scale 100: an object scaled 100 whose vertices are in hundredths, skinned through bind poses that
        // carry the scale. Baked, the skin leaves the renderer's scale out: its surface is a meter, not ten thousand square
        // meters.
        [Test] public void SkinnedMeshesImportedAtAnotherScaleMeasureTheSurfaceTheyShow()
        {
            var go = new GameObject("Skin");
            go.transform.SetParent(root.transform, false);
            go.transform.localScale = Vector3.one * 100f;
            var skin = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = Own(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) }.Select(v => v * .01f).ToArray(),
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one },
                triangles = new[] { 0, 2, 1, 1, 2, 3 },
            });
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            mesh.bindposes = new[] { bones[0].worldToLocalMatrix * go.transform.localToWorldMatrix };
            skin.sharedMesh = mesh; skin.bones = new[] { bones[0] }; skin.rootBone = bones[0];
            var texture = Own(new Texture2D(1024, 1024));
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            skin.sharedMaterial = material;

            var meters = TextureDensity.MetersPerTexture(settings, TextureMatching.Slots(settings));
            Assert.That(meters[texture], Is.EqualTo(1f).Within(.01f), "A meter of surface.");
            Assert.That(TextureDensity.Cap(meters[texture], false, false, 1f), Is.EqualTo(1024), "Not 8,192.");
        }

        [Test] public void BigCrunchesColourTexturesAndHalvesMetallicMaps()
        {
            string folder = Folder();
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

            // Halving alone halves the size the texture has, not a cap above it.
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.HalfSurfaceMaps;
            var halved = TextureOptimization.Build(settings, false);
            Assert.That(halved.For(metallic).width, Is.EqualTo(512));
            Assert.That(halved.For(color), Is.Null, "Not a surface map: as it is.");
        }

        [Test] public void TheSameImageImportedTwiceUploadsOnce()
        {
            string folder = Folder();
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
            var plane = Plane("Plane", cells);
            var mesh = plane.sharedMesh;
            var vertices = mesh.vertices;
            var smile = vertices.Select(v => v.x < .2f && v.y < .2f ? Vector3.forward * .01f : Vector3.zero).ToArray();
            mesh.AddBlendShapeFrame("Smile", 100, smile, null, null);
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

        // MAF-24: the switch between the simplifiers. Both keep the surface within the budget and the expression's triangles;
        // the run does it from one simplification, the old one (the default for now) from up to five.
        [Test] public void BothSimplifiersKeepTheSurfaceAndWhatExpressionsMove([Values(false, true)] bool meshia)
        {
            Assert.That(PolygonReduction.UseOldMeshiaSimplifier, Is.True, "the old simplifier stays the default until the two are compared in Unity");
            const int cells = 40;
            var plane = Plane("Plane", cells);
            var mesh = plane.sharedMesh;
            var smile = mesh.vertices.Select(v => v.x < .2f && v.y < .2f ? Vector3.forward * .01f : Vector3.zero).ToArray();
            mesh.AddBlendShapeFrame("Smile", 100, smile, null, null);
            var clip = Own(new AnimationClip());
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Plane", typeof(SkinnedMeshRenderer), "blendShape.Smile"), AnimationCurve.Constant(0, 1, 100));
            fx.layers[0].stateMachine.AddState("Smile").motion = clip;
            var corner = new HashSet<Vector3>(Centers(mesh, v => v.x < .2f && v.y < .2f));
            var made = new List<Mesh>();
            PolygonReduction.UseOldMeshiaSimplifier = meshia;
            try
            {
                int removed = PolygonReduction.Reduce(new Toolkit.Editor.VRChat.Optimization.AvatarUsage(root), 1f, made);
                owned.AddRange(made);
                Debug.Log("[My Avatar] " + (meshia ? "Meshia" : "SimplificationRun") + ": " + removed + " of " + 2 * cells * cells + " triangles removed.");
                var reduced = plane.sharedMesh;
                Assert.That(removed, Is.GreaterThan(cells * cells));
                Assert.That(Toolkit.Editor.VRChat.Optimization.MeshParts.Within(mesh.vertices, reduced.vertices, reduced.triangles, PolygonReduction.Budget(1f)));
                Assert.That(Centers(reduced, _ => true), Is.SupersetOf(corner));
                var deltas = new Vector3[reduced.vertexCount];
                reduced.GetBlendShapeFrameVertices(0, 0, deltas, null, null);
                Assert.That(deltas.Count(d => d != Vector3.zero), Is.EqualTo(smile.Count(d => d != Vector3.zero)));
            }
            finally { PolygonReduction.UseOldMeshiaSimplifier = true; }
        }

        // The budget is measured on the avatar through the bones' bind poses: a mesh switched off at rest, or with a culling
        // box a tool enlarged (Modular Avatar's Mesh Settings), loses the same polygons.
        [Test] public void FewerPolygonsDontDependOnBoundsOrBeingShown()
        {
            var plane = Plane("Plane", 60);
            var source = plane.sharedMesh;
            var made = new List<Mesh>();
            int removed = PolygonReduction.Reduce(new Toolkit.Editor.VRChat.Optimization.AvatarUsage(root), 1f, made);
            owned.AddRange(made);
            Assert.That(removed, Is.GreaterThan(3600));

            PolygonReduction.Release();
            plane.sharedMesh = source;
            plane.localBounds = new Bounds(Vector3.zero, Vector3.one * 50f);
            plane.gameObject.SetActive(false);
            made.Clear();
            int again = PolygonReduction.Reduce(new Toolkit.Editor.VRChat.Optimization.AvatarUsage(root), 1f, made);
            owned.AddRange(made);
            Assert.That(again, Is.EqualTo(removed));
        }

        // The cache knows a mesh by everything simplifying keeps: a mesh re-weighted or re-UV'd in Blender and uploaded again
        // in the same session isn't the old one.
        [Test] public void PolygonCacheKeysFollowWhatSimplifyingKeeps()
        {
            var plane = Plane("Plane", 4);
            var mesh = plane.sharedMesh;
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            var smile = mesh.vertices.Select(v => v.x < .5f ? Vector3.forward * .01f : Vector3.zero).ToArray();
            mesh.AddBlendShapeFrame("Smile", 100, smile, null, null);
            string key = PolygonReduction.MeshKey(mesh);
            Assert.That(PolygonReduction.MeshKey(Own(Object.Instantiate(mesh))), Is.EqualTo(key), "The same content, the same key.");
            void Changed(string what, Action<Mesh> edit)
            {
                var copy = Own(Object.Instantiate(mesh));
                edit(copy);
                Assert.That(PolygonReduction.MeshKey(copy), Is.Not.EqualTo(key), what);
            }
            Changed("UVs", m => m.uv = m.uv.Select(u => u * .5f).ToArray());
            Changed("normals", m => m.normals = m.normals.Select(n => -n).ToArray());
            Changed("weights", m => m.boneWeights = m.boneWeights.Select(_ => new BoneWeight { boneIndex0 = 1, weight0 = 1 }).ToArray());
            Changed("bind poses", m => m.bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.up) });
            Changed("blendshape offsets", m => { m.ClearBlendShapes(); m.AddBlendShapeFrame("Smile", 100, smile.Select(d => d * 2f).ToArray(), null, null); });
        }

        // More large meshes than the cache's usual size: once each is simplified, the next estimate finds them all.
        [UnityTest] public IEnumerator EstimatesWithManyLargeMeshesSettle()
        {
            for (int i = 0; i < 18; i++) Plane("Plane " + i, 32, i * .01f);
            bool ready = false;
            void OnReady() => ready = true;
            PolygonReduction.Ready += OnReady;
            try
            {
                var made = new List<Mesh>();
                Assert.That(PolygonReduction.Reduce(new Toolkit.Editor.VRChat.Optimization.AvatarUsage(root), 1f, made, background: true), Is.Zero, "In the background.");
                Assert.That(PolygonReduction.Measuring, Is.True);
                double deadline = EditorApplication.timeSinceStartup + 120;
                while (!ready && EditorApplication.timeSinceStartup < deadline) yield return null;
                Assert.That(ready, Is.True, "Every mesh is simplified.");
                PolygonReduction.Reduce(new Toolkit.Editor.VRChat.Optimization.AvatarUsage(root), 1f, made, background: true);
                owned.AddRange(made);
                Assert.That(PolygonReduction.Measuring, Is.False, "Nothing to simplify again.");
            }
            finally { PolygonReduction.Ready -= OnReady; }
        }

        // Before a script reload the background runs stop and what they and the cache hold is destroyed: nothing is left
        // behind, and a stopped run never reports.
        [UnityTest] public IEnumerator BackgroundRunsStopAndFreeTheirMeshesWhenReleased()
        {
            Plane("Plane", 60);
            bool ready = false;
            void OnReady() => ready = true;
            PolygonReduction.Ready += OnReady;
            try
            {
                PolygonReduction.Reduce(new Toolkit.Editor.VRChat.Optimization.AvatarUsage(root), 1f, new List<Mesh>(), background: true);
                Assert.That(PolygonReduction.Measuring, Is.True);
                for (int i = 0; i < 3; i++) yield return null;
                PolygonReduction.Release();
                Assert.That(PolygonReduction.Measuring, Is.False);
                Assert.That(PolygonReduction.HeldCount, Is.Zero);
                for (int i = 0; i < 30; i++) yield return null;
                Assert.That(ready, Is.False, "A stopped run never reports.");
                Assert.That(PolygonReduction.HeldCount, Is.Zero);
            }
            finally { PolygonReduction.Ready -= OnReady; }
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
            FinishKeepingTheKey();
            var result = OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Now.Skinned, Is.EqualTo(2), "VRChat ranks the hidden mesh too.");
            Assert.That(result.Upload.Skinned, Is.EqualTo(1));
            Assert.That(result.Upload.Bones, Is.LessThan(result.Now.Bones));
            Assert.That(result.Report.HiddenObjects, Is.EqualTo(1));
            Assert.That(result.Report.BlendShapes, Is.EqualTo(1), "Blendshapes are counted, not removed.");
            Assert.That(result.Upload.BlendShapes, Is.EqualTo(0));
            Assert.That(result.Upload.Download, Is.LessThan(result.Now.Download), "The hidden mesh and the unused shape leave the download.");
            Assert.That(result.Now.Download, Is.GreaterThan(0));
            Assert.That(result.Now.Bundle, Is.GreaterThan(result.Now.Download), "VRChat's limits apply to the bundle, a little larger than the size it shows.");
            Assert.That(result.Now.Unpacked, Is.GreaterThan(result.Now.Bundle));
            Assert.That(root.GetComponentsInChildren<Transform>(true).Length, Is.EqualTo(objects), "The avatar is never touched.");
            Assert.That(hidden.sharedMesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(OptimizationEstimate.Latest(settings), Is.SameAs(result));
        }

        // The estimate is kept in parts, each redone only when what it depends on changes: the texture slider plans the
        // textures again and measures the same hidden copy with them, without copying the avatar; any other setting copies it
        // again. Either way the figures are those of an estimate measured from nothing.
        [Test] public void ATextureSettingRedoesOnlyTheTexturesOfTheEstimate()
        {
            string folder = Folder();
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", Image(folder, "Board_BaseColor", 1024));
            Board(material);
            Skinned("Body");
            var hidden = Skinned("Old shirt"); hidden.gameObject.SetActive(false);
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.ResizeTextures | OptimizationPasses.HiddenObjects;
            settings.uploadOptimization.sharpFrom = .5f;
            OptimizationEstimate.Result Measure()
            {
                OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
                OptimizationEstimate.Finish();
                var result = OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
                Assert.That(result?.Error, Is.Null);
                return result;
            }
            string Text(OptimizationEstimate.Result result) =>
                Newtonsoft.Json.JsonConvert.SerializeObject(new object[] { result.Now, result.Upload, result.Report.ToString() });

            var near = Measure();
            int copies = OptimizationEstimate.CopiesMade;
            Assert.That(copies, Is.EqualTo(1));
            settings.uploadOptimization.sharpFrom = 8f;
            var far = Measure();
            Assert.That(OptimizationEstimate.CopiesMade, Is.EqualTo(copies), "The texture slider measures the same copy with the textures' new plans.");
            Assert.That(far.Upload.TextureMegabytes, Is.LessThan(near.Upload.TextureMegabytes), "Sharp from farther: smaller copies.");
            Assert.That(far.Report.HiddenObjects, Is.EqualTo(1));

            OptimizationEstimate.Clear();
            var fresh = Measure();
            Assert.That(OptimizationEstimate.CopiesMade, Is.EqualTo(copies + 1), "Nothing kept: copied again.");
            Assert.That(Text(fresh), Is.EqualTo(Text(far)), "The parts kept give what a whole new estimate measures.");

            settings.uploadOptimization.custom &= ~OptimizationPasses.HiddenObjects;
            var kept = Measure();
            Assert.That(OptimizationEstimate.CopiesMade, Is.EqualTo(copies + 2), "Another change to the objects: copied again.");
            Assert.That(kept.Upload.Skinned, Is.EqualTo(2), "The hidden mesh uploads now.");
        }

        // Textures' measures compress within one set of limits whichever estimate starts them: one more than the limit
        // waits, the same measure is never compressed twice, and a finished one is reused (also by an estimate replacing
        // the one that started it).
        [Test] public void TextureMeasuresShareOneBoundedSchedule()
        {
            DownloadSize.FinishCompressing();
            string folder = Folder();
            var textures = Enumerable.Range(0, DownloadSize.MaxCompressing + 1)
                .Select(n => Image(folder, "Measure_" + n, 64, pixel: i => new Color32((byte)(i * 7 + n * 31), (byte)(n * 13), (byte)i, 255))).ToList();
            var keys = new List<string>();
            foreach (var texture in textures.Take(DownloadSize.MaxCompressing))
            {
                Assert.That(DownloadSize.Measure(texture, false, null, out var key), Is.EqualTo(DownloadSize.Measuring.Compressing));
                keys.Add(key);
            }
            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Count), "One key per image.");
            Assert.That(DownloadSize.Measure(textures.Last(), false, null, out _), Is.EqualTo(DownloadSize.Measuring.Full), "The limit holds.");
            Assert.That(DownloadSize.Measure(textures[0], false, null, out var again), Is.EqualTo(DownloadSize.Measuring.Compressing), "Already under way: not started twice.");
            Assert.That(again, Is.EqualTo(keys[0]));
            DownloadSize.FinishCompressing();
            Assert.That(DownloadSize.AnyCompressing, Is.False);
            Assert.That(DownloadSize.Measure(textures[0], false, null, out _), Is.EqualTo(DownloadSize.Measuring.Done), "Measured once, reused.");
            Assert.That(DownloadSize.Measure(textures.Last(), false, null, out _), Is.EqualTo(DownloadSize.Measuring.Compressing), "Room again.");
            DownloadSize.FinishCompressing();
        }

        // A mesh asset edited in place (not imported again) is measured again: the textures' density on it and the
        // estimate's plans, and its blendshapes' download when their offsets change but not their count.
        [Test] public void AMeshEditedInPlaceIsMeasuredAgain()
        {
            string folder = Folder();
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", Image(folder, "Board_BaseColor", 1024));
            var filter = Board(material).GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh);
            AssetDatabase.CreateAsset(mesh, folder + "/Board.asset");
            filter.sharedMesh = mesh;
            settings.uploadOptimization.level = OptimizationLevel.Custom;
            settings.uploadOptimization.custom = OptimizationPasses.ResizeTextures;
            settings.uploadOptimization.sharpFrom = 8f;
            float Megabytes()
            {
                OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
                OptimizationEstimate.Finish();
                var result = OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
                Assert.That(result?.Error, Is.Null);
                return result.Upload.TextureMegabytes;
            }

            float small = Megabytes();
            // Eight times as wide and tall: the texture covers more surface, so it keeps more texels from 8 m.
            mesh.vertices = mesh.vertices.Select(v => v * 8f).ToArray();
            EditorUtility.SetDirty(mesh);
            Assert.That(Megabytes(), Is.GreaterThan(small), "The density is measured on the mesh as it is now.");

            var shapes = Object.Instantiate(Skinned("Body").sharedMesh);
            AssetDatabase.CreateAsset(shapes, folder + "/Shapes.asset");
            var deltas = new Vector3[shapes.vertexCount];
            deltas[0] = Vector3.up;
            shapes.ClearBlendShapes();
            shapes.AddBlendShapeFrame("Moving", 100, deltas, null, null);
            long one = DownloadSize.ShapeBytes(shapes)["Moving"];
            for (int i = 0; i < deltas.Length; i++) deltas[i] = Vector3.up;
            shapes.ClearBlendShapes();
            shapes.AddBlendShapeFrame("Moving", 100, deltas, null, null);
            EditorUtility.SetDirty(shapes);
            Assert.That(DownloadSize.ShapeBytes(shapes)["Moving"], Is.GreaterThan(one), "The same shape, moving more vertices.");
        }

        // A mesh two skinned renderers show uploads with the blendshapes either of them keeps.
        [Test] public void AMeshTwoRenderersShowKeepsTheShapesEitherKeeps()
        {
            var first = Skinned("Body");
            var second = Skinned("Body copy");
            second.sharedMesh = first.sharedMesh;
            var mesh = first.sharedMesh;
            Assert.That(mesh.blendShapeCount, Is.GreaterThan(0), "The fixture's mesh has a blendshape.");
            string shape = mesh.GetBlendShapeName(0);
            long Shapes(Func<SkinnedMeshRenderer, ICollection<string>> kept) => DownloadSize.Of(root, kept: kept).BlendShapes;

            long all = Shapes(null);
            Assert.That(all, Is.GreaterThan(0));
            Assert.That(Shapes(r => r == first ? new HashSet<string> { shape } : new HashSet<string>()), Is.EqualTo(all), "The first renderer keeps it.");
            Assert.That(Shapes(r => r == second ? new HashSet<string> { shape } : new HashSet<string>()), Is.EqualTo(all), "The second renderer keeps it.");
            Assert.That(Shapes(r => new HashSet<string>()), Is.EqualTo(0), "Neither does.");
        }

        // A tool editing one avatar's meshes live (Mixer sculpting) holds back that avatar's estimate only, even when it was
        // asked for first.
        [Test] public void AnAvatarEditedLiveHoldsBackOnlyItsOwnEstimate()
        {
            Skinned("Body");
            settings.uploadOptimization.level = OptimizationLevel.Small;
            var otherRoot = new GameObject("Other avatar");
            SceneManager.MoveGameObjectToScene(otherRoot, scene);
            otherRoot.AddComponent<VRCAvatarDescriptor>();
            var other = otherRoot.AddComponent<MyAvatar>();
            Func<Transform, bool> editing = avatarRoot => avatarRoot == root.transform;
            Orbiters.Toolkit.Editor.Meshes.AvatarMeshEdits.LiveEditing += editing;
            try
            {
                Assert.That(OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null), Is.Null);
                Assert.That(OptimizationEstimate.Get(other, other.uploadOptimization.Passes, null), Is.Null);
                OptimizationEstimate.Finish(waitForLiveEdits: true);
                Assert.That(OptimizationEstimate.Get(other, other.uploadOptimization.Passes, null), Is.Not.Null, "The other avatar is measured meanwhile.");
                Assert.That(OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null), Is.Null, "The avatar edited live waits.");
            }
            finally { Orbiters.Toolkit.Editor.Meshes.AvatarMeshEdits.LiveEditing -= editing; }
            FinishKeepingTheKey();
            var result = OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            Assert.That(result, Is.Not.Null, "Measured once the live edits are over.");
            Assert.That(result.Error, Is.Null);
        }

        // A failing estimate is kept and shown with its reason: no new attempt (and no new error in the console) every few
        // seconds, until something changes or the card tries again.
        [Test] public void AFailingEstimateIsShownUntilTriedAgain()
        {
            Skinned("Body");
            settings.uploadOptimization.level = OptimizationLevel.Small;
            var passes = settings.uploadOptimization.Passes;
            OptimizationEstimate.FailWith = "Fixture failure";
            LogAssert.Expect(LogType.Exception, new Regex("Fixture failure"));
            Assert.That(OptimizationEstimate.Get(settings, passes, null), Is.Null);
            OptimizationEstimate.Finish();
            var failed = OptimizationEstimate.Get(settings, passes, null);
            Assert.That(failed, Is.Not.Null);
            Assert.That(failed.Error, Is.EqualTo("Fixture failure"));
            Assert.That(OptimizationEstimate.Get(settings, passes, null), Is.SameAs(failed), "Kept: not measured again at every poll.");

            var section = new OptimizationSection(settings);
            var texts = section.Query<Label>().ToList().Select(l => l.text).ToList();
            Assert.That(texts, Has.Some.EqualTo("COULDN'T MEASURE"));
            Assert.That(texts, Has.Some.StartsWith("The estimate failed: Fixture failure"));
            Assert.That(section.Query<Button>().ToList().Select(b => b.text), Has.Some.EqualTo("Try again"));

            OptimizationEstimate.FailWith = null;
            OptimizationEstimate.Retry(settings);
            Assert.That(OptimizationEstimate.Get(settings, passes, null), Is.Null, "Measured again.");
            OptimizationEstimate.Finish();
            Assert.That(OptimizationEstimate.Get(settings, passes, null).Error, Is.Null);
        }

        [Test] public void TheEstimateShowsOnlyThePlatformTheProjectBuildsFor()
        {
            Skinned("Body");
            settings.uploadOptimization.level = OptimizationLevel.Small;
            OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            FinishKeepingTheKey();
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

        // Sizes are written as VRChat writes them, whatever the system's language.
        [Test] public void SizesAreWrittenTheSameInEveryLanguage()
        {
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                Assert.That(VrcLoadingBar.Format(18559795), Is.EqualTo("17.7 MB"));
                Assert.That(DownloadSize.Megabytes(5452595), Is.EqualTo("5.2 MB"));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
        }

        // A one-meter square under the avatar showing the material.
        private MeshRenderer Board(Material material)
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
            var renderer = quad.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        // A flat meter of cells by cells quads, skinned to the root, at depth z.
        private SkinnedMeshRenderer Plane(string name, int cells, float z = 0f)
        {
            var go = new GameObject(name); go.transform.SetParent(root.transform, false);
            var plane = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = Own(new Mesh { name = name });
            var vertices = new List<Vector3>(); var indices = new List<int>(); var uvs = new List<Vector2>();
            for (int y = 0; y <= cells; y++)
                for (int x = 0; x <= cells; x++)
                {
                    vertices.Add(new Vector3(x / (float)cells, y / (float)cells, z));
                    uvs.Add(new Vector2(x / (float)cells, y / (float)cells));
                }
            for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                {
                    int i = y * (cells + 1) + x;
                    indices.AddRange(new[] { i, i + cells + 1, i + 1, i + 1, i + cells + 1, i + cells + 2 });
                }
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs); mesh.SetTriangles(indices, 0); mesh.RecalculateNormals();
            mesh.boneWeights = vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            mesh.bindposes = new[] { Matrix4x4.identity };
            plane.sharedMesh = mesh; plane.bones = new[] { root.transform }; plane.rootBone = root.transform;
            return plane;
        }

        private string Folder()
        {
            string folder = "Assets/Orbiters/MyAvatar/OptimizationTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            return folder;
        }

        // An opaque PNG imported under the folder, uncompressed unless said otherwise.
        private static Texture2D Image(string folder, string name, int size, bool noisy = false, TextureImporterType type = TextureImporterType.Default,
            TextureImporterCompression compression = TextureImporterCompression.Uncompressed, bool crunch = false, int quality = 50, Func<int, Color32> pixel = null)
        {
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            var random = new System.Random(1);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = pixel != null ? pixel(i) : noisy ? new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255) : new Color32((byte)(i % 251), (byte)(i / size % 253), 90, 255);
            image.SetPixels32(pixels); image.Apply();
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = type;
            importer.textureCompression = compression;
            importer.crunchedCompression = crunch;
            importer.compressionQuality = quality;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Every texel of the texture's full size as the GPU reads it (raw channels, linear).
        private static Color32[] Pixels(Texture texture)
        {
            var target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply();
                return image.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
            }
        }

        // A texel of the texture as the GPU reads it (raw channels, linear).
        private static Color32 Read(Texture texture)
        {
            var target = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var image = new Texture2D(8, 8, TextureFormat.RGBA32, false, true);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                image.Apply();
                return image.GetPixels32()[27];
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(image);
            }
        }

        // Both upload steps, as the build runs them, on one build's context.
        private static void Upload(GameObject avatar)
        {
            var context = BuildContext.For(avatar);
            new UploadOptimizer.Structure().Run(context);
            new UploadOptimizer.Materials().Run(context);
        }

        // MAF-1: the optimizer's steps run where its callbacks ran: structure in the Structure phase (after MCB's -8800,
        // before NDMF's optimizing -1025), materials after Poiyomi's locks (100) and before the build data is written.
        [Test] public void TheOptimizerStepsRunWhereItsCallbacksRan()
        {
            var steps = AvatarBuildPipeline.Steps.ToList();
            int structure = steps.FindIndex(s => s is UploadOptimizer.Structure), materials = steps.FindIndex(s => s is UploadOptimizer.Materials);
            int save = steps.FindIndex(s => s.GetType().FullName == "Orbiters.Toolkit.Editor.VRChat.Attachments.BuildCopyAssetsHook");
            Assert.That(steps[structure].Phase, Is.EqualTo(BuildPhase.Structure));
            Assert.That(BuildPhases.Order(BuildPhase.Structure), Is.GreaterThan(-8800).And.LessThan(-1025));
            Assert.That(steps[materials].Phase, Is.EqualTo(BuildPhase.SaveBuildData));
            Assert.That(BuildPhases.Order(BuildPhase.SaveBuildData), Is.GreaterThan(100));
            Assert.That(save, Is.GreaterThan(materials), "the material copies are made before the build data is written");
            Assert.That(steps.Where(s => s.Phase == BuildPhase.Structure).Select(s => s.GetType()), Is.EqualTo(new[] { typeof(UploadOptimizer.Structure) }),
                "the optimizer's phase is its own");
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

        // An estimate measures a hidden copy: at every level, nothing the avatar references is changed or marked changed, its
        // meshes as assets and in memory alike.
        [Test] public void MeasuringLeavesEverythingTheAvatarShowsUntouched([Values(OptimizationLevel.Small, OptimizationLevel.Big)] OptimizationLevel level)
        {
            string folder = Folder();
            var material = Own(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", Image(folder, "Board_BaseColor", 256));
            Board(material);
            Skinned("Body");
            var asset = Skinned("Shirt");
            var mesh = Object.Instantiate(asset.sharedMesh);
            AssetDatabase.CreateAsset(mesh, folder + "/Shirt.asset");
            asset.sharedMesh = mesh;
            settings.uploadOptimization.level = level;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var referenced = renderers.Select(Toolkit.Editor.VRChat.Optimization.MeshOptimization.MeshOf).Cast<Object>()
                .Concat(renderers.SelectMany(r => r.sharedMaterials))
                .Concat(renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).SelectMany(m => m.GetTexturePropertyNameIDs().Select(m.GetTexture)))
                .Where(o => o != null).Distinct().ToList();
            string State() => string.Join("\n", referenced.Select(o => o.name + " " + EditorUtility.GetDirtyCount(o) + " " + EditorUtility.IsDirty(o) + " " +
                (o is Mesh m ? string.Join(",", m.vertices.Select(v => v.ToString("R"))) + "|" + string.Join(",", m.triangles) : EditorJsonUtility.ToJson(o)))
                .Concat(renderers.Select(r => EditorJsonUtility.ToJson(r))));
            string before = State();

            OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            FinishKeepingTheKey();

            Assert.That(OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null)?.Error, Is.Null);
            Assert.That(State(), Is.EqualTo(before), "the avatar's meshes, materials, textures and renderers as they were, none marked changed");
        }

        // A tool that edits the avatar's meshes in place (Mixer's working mesh: the same object, the same counts) says so, and the
        // estimate is measured again; another avatar's edits leave it as it is.
        [Test] public void AnnouncedMeshEditsMeasureTheEstimateAgain()
        {
            Skinned("Body");
            settings.uploadOptimization.level = OptimizationLevel.Small;
            var otherRoot = new GameObject("Other avatar");
            SceneManager.MoveGameObjectToScene(otherRoot, scene);
            otherRoot.AddComponent<VRCAvatarDescriptor>();
            OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            FinishKeepingTheKey();
            var measured = OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null);
            Assert.That(measured, Is.Not.Null);
            string key = OptimizationEstimate.KeyOf(settings);

            Toolkit.Editor.Meshes.AvatarMeshEdits.NotifyApplied(otherRoot.transform);
            Assert.That(OptimizationEstimate.KeyOf(settings), Is.EqualTo(key), "another avatar's meshes");
            Assert.That(OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null), Is.SameAs(measured));

            Toolkit.Editor.Meshes.AvatarMeshEdits.NotifyPreviewed(root.transform);
            Assert.That(OptimizationEstimate.KeyOf(settings), Is.Not.EqualTo(key), "its own meshes, edited in place");
            Assert.That(OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null), Is.Null, "measured again");
            FinishKeepingTheKey();
            Assert.That(OptimizationEstimate.Get(settings, settings.uploadOptimization.Passes, null)?.Error, Is.Null);
        }

        // Finishes the estimates, and fails saying what changed when measuring changed what the avatar's estimate is keyed by
        // (Get would then never find the result: it measures the avatar again).
        private void FinishKeepingTheKey()
        {
            string before = OptimizationEstimate.KeyOf(settings);
            OptimizationEstimate.Finish();
            string after = OptimizationEstimate.KeyOf(settings);
            if (before == after) return;
            var was = before.Split('|', ','); var now = after.Split('|', ',');
            var differ = Enumerable.Range(0, Math.Max(was.Length, now.Length))
                .Where(i => i >= was.Length || i >= now.Length || was[i] != now[i])
                .Select(i => "[" + i + "] " + (i < was.Length ? was[i] : "-") + " -> " + (i < now.Length ? now[i] : "-")).Take(8);
            Assert.Fail("Measuring changed the avatar's estimate key: " + string.Join("; ", differ));
        }

        private T Own<T>(T value) where T : Object { if (value != null) owned.Add(value); return value; }
    }

    /// <summary>
    /// Points My Avatar's Library caches (texture copies, alpha and download measures, imports) at a folder of the test's
    /// own under the project's Temp, deleted afterwards: tests never write the user's caches. The folder is the one every
    /// cache uses while a test runs: a gallery download or texture match the user started before it reads and writes there
    /// meanwhile too (it finds nothing cached, and what it caches is dropped). Measures not yet written are written to the
    /// user's caches first.
    /// </summary>
    internal sealed class TestCaches : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Temp", "OrbitersMyAvatarTests", Guid.NewGuid().ToString("N"));

        internal TestCaches()
        {
            DownloadSize.ForgetCaches();
            LibraryStore.Override = folder;
            TextureOptimization.ForgetCaches();
            DownloadSize.ForgetCaches();
        }

        public void Dispose()
        {
            DownloadSize.ForgetCaches();
            LibraryStore.Override = null;
            TextureOptimization.ForgetCaches();
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
