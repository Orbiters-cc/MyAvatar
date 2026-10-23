using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The import settings VRChat's SDK panel asks for: found as the SDK finds them (EditorOnly objects left out), fixed in
    // the importers, and nothing left for the SDK's own scans afterwards. A real FBX (written by the Toolkit) and real PNGs.
    public sealed class VrchatImportSettingsTests
    {
        private static readonly PropertyInfo Legacy =
            typeof(ModelImporter).GetProperty("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes", BindingFlags.Instance | BindingFlags.NonPublic);
        private Scene scene;
        private string folder;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            folder = "Assets/Orbiters/MyAvatar/ImportTest-" + Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(folder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var value in owned) if (value) UnityEngine.Object.DestroyImmediate(value);
            owned.Clear();
            AssetDatabase.DeleteAsset(folder);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        [Test]
        public void WhatTheSdkAsksIsFoundAndFixed()
        {
            Assume.That(Legacy, Is.Not.Null, "this Unity has no legacy blendshape normals setting");
            string model = Model("Body");
            string skin = Texture("Skin", mips: true), flat = Texture("Flat", mips: false), preview = Texture("Preview", mips: true);
            var root = Own(new GameObject("Avatar"));
            SceneManager.MoveGameObjectToScene(root, scene);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            var skinned = body.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().Single();
            skinned.sharedMaterials = new[] { Material(skin), Material(flat) };
            var hidden = new GameObject("Preview") { tag = "EditorOnly" }; hidden.transform.SetParent(root.transform, false);
            hidden.AddComponent<MeshRenderer>().sharedMaterial = Material(preview);

            var plan = VrchatImportSettings.Inspect(root);
            Assert.That(plan.ReadWrite, Is.EqualTo(new[] { model }));
            Assert.That(plan.BlendShapeNormals, Is.EqualTo(new[] { model }));
            Assert.That(plan.StreamingMips, Is.EqualTo(new[] { skin }), "textures without mip maps and EditorOnly objects' textures are not asked for");
            Assert.That(plan.Count, Is.EqualTo(2));
            Assert.That(plan.Describe(), Is.EqualTo("Read/Write on 1 model, legacy blendshape normals on 1 model, streaming mip maps on 1 texture"));
            Assert.That(SdkScan(root, "ScanMeshesForDisabledMeshReadWriteSetting"), Is.EqualTo(1), "the SDK finds the same model");
            Assert.That(SdkScan(root, "ScanMeshesForIncorrectBlendShapeNormalsSetting"), Is.EqualTo(1));

            var done = VrchatImportSettings.Fix(plan);
            Assert.That(done.Describe(), Is.EqualTo(plan.Describe()));
            var importer = (ModelImporter)AssetImporter.GetAtPath(model);
            Assert.That(importer.isReadable, Is.True);
            Assert.That((bool)Legacy.GetValue(importer), Is.True);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(skin)).streamingMipmaps, Is.True);
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(preview)).streamingMipmaps, Is.False, "an EditorOnly object's texture is left as it is");
            Assert.That(VrchatImportSettings.Inspect(root).Empty, Is.True);
            Assert.That(SdkScan(root, "ScanMeshesForDisabledMeshReadWriteSetting"), Is.Zero, "the SDK has nothing to report");
            Assert.That(SdkScan(root, "ScanMeshesForIncorrectBlendShapeNormalsSetting"), Is.Zero);
            Assert.That(VrchatImportSettings.Fix(VrchatImportSettings.Inspect(root)).Empty, Is.True, "a second fix changes nothing");
        }

        // How many meshes under root one of the SDK's own scans finds (they are private: read through reflection).
        private static int SdkScan(GameObject root, string scan)
        {
            var builder = Type.GetType("VRC.SDK3A.Editor.VRCSdkControlPanelAvatarBuilder, VRC.SDK3A.Editor");
            Assume.That(builder, Is.Not.Null, "VRChat's avatar builder is not here");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var meshes = builder.GetMethod("GetAllMeshesInGameObjectHierarchy", flags).Invoke(null, new object[] { root });
            return ((HashSet<Mesh>)builder.GetMethod(scan, flags).Invoke(null, new[] { meshes })).Count;
        }

        // A model with a blendshape, imported as many are: Read/Write off, blendshape normals calculated the new way.
        private string Model(string name)
        {
            var mesh = Own(new Mesh { name = name });
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f) };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.SetTriangles(new[] { 0, 2, 1, 1, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.AddBlendShapeFrame("Smile", 100f, mesh.vertices.Select(v => new Vector3(0f, 0f, v.y * 0.2f)).ToArray(), null, null);
            var holder = Own(new GameObject(name));
            SceneManager.MoveGameObjectToScene(holder, scene);
            string path = folder + "/" + name + ".fbx";
            var written = FbxExport.Write(path, new[] { new FbxExportMesh { Transform = holder.transform, Mesh = mesh } });
            Assert.That(written.Error, Is.Null);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false;
            importer.importBlendShapes = true;
            importer.importBlendShapeNormals = ModelImporterNormals.Calculate;
            Legacy.SetValue(importer, false);
            importer.SaveAndReimport();
            return path;
        }

        private string Texture(string name, bool mips)
        {
            var texture = Own(new Texture2D(16, 16));
            string path = folder + "/" + name + ".png";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.mipmapEnabled = mips;
            importer.streamingMipmaps = false;
            importer.SaveAndReimport();
            return path;
        }

        private Material Material(string texture)
        {
            var material = Own(new Material(Shader.Find("Standard")));
            material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texture);
            return material;
        }

        private T Own<T>(T value) where T : UnityEngine.Object { owned.Add(value); return value; }
    }
}
