using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class TextureMatchingTests
    {
        private Scene scene;
        private readonly List<Object> owned = new List<Object>();

        [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown] public void TearDown()
        {
            foreach (var value in owned) if (value) Object.DestroyImmediate(value);
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        // Rexouium: "Body_BaseMap_Green" shares "Green" with the eye material as much as "Body" with the body material; being
        // a variant of the body's current "Body_BaseMap" decides it.
        [Test] public void ColourVariantOfTheTextureInASlotGoesToThatSlot()
        {
            var avatar = Avatar(("BodyMatt", "Body_BaseMap"), ("EyesMap_Green", "Eyes_Green"), ("MiscMatt", "Misc_BaseMap"));
            var entry = new TextureEntry { texture = Own(new Texture2D(4, 4) { name = "Body_BaseMap_Green" }), fileName = "Body_BaseMap_Green.png", role = "color" };
            var entries = new List<TextureEntry> { entry };
            TextureMatching.Match(entries, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.NotNull(entry.material, entry.reason);
            Assert.AreEqual("BodyMatt", entry.material.name);
            Assert.AreEqual("_MainTex", entry.property);
        }

        [Test] public void AnUndecidedTextureNamesTheRunnerUp()
        {
            var avatar = Avatar(("BodyMatt", "Body_BaseMap"), ("EyesMap_Green", "Eyes_Green"), ("MiscMatt", "Misc_BaseMap"));
            var entry = new TextureEntry { texture = Own(new Texture2D(4, 4) { name = "Green_Body" }), fileName = "Green_Body.png", role = "color" };
            TextureMatching.Match(new List<TextureEntry> { entry }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.Null(entry.material);
            StringAssert.Contains("about as likely", entry.reason);
        }

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        // One mesh with a submesh per material, like an avatar body split into material slots.
        private MyAvatar Avatar(params (string material, string texture)[] parts)
        {
            var root = Own(new GameObject("Matching avatar"));
            SceneManager.MoveGameObjectToScene(root, scene);
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            var mesh = Own(new Mesh { subMeshCount = parts.Length });
            var vertices = new List<Vector3>(); var uvs = new List<Vector2>();
            for (int i = 0; i < parts.Length; i++)
            {
                vertices.AddRange(new[] { new Vector3(i, 0, 0), new Vector3(i, 1, 0), new Vector3(i + 1, 0, 0) });
                uvs.AddRange(new[] { new Vector2(i * .3f, 0), new Vector2(i * .3f, .3f), new Vector2(i * .3f + .3f, 0) });
            }
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs);
            for (int i = 0; i < parts.Length; i++) mesh.SetTriangles(new[] { i * 3, i * 3 + 1, i * 3 + 2 }, i);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            var materials = new Material[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                materials[i] = Own(new Material(Shader.Find("Standard")) { name = parts[i].material });
                materials[i].SetTexture("_MainTex", Own(new Texture2D(4, 4) { name = parts[i].texture }));
            }
            body.AddComponent<MeshRenderer>().sharedMaterials = materials;
            return root.AddComponent<MyAvatar>();
        }
    }
}
