using System.Collections.Generic;
using System.Linq;
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

        // Names in scripts without spaces: one ideograph is a word, and a name containing it or sharing it still matches.
        [Test] public void NonLatinNamesMatchTheirMaterial()
        {
            var avatar = Avatar(("体", "Skin_A"), ("髪の毛", "Strands_B"), ("目", "Iris_C"));
            var body = new TextureEntry { texture = Own(new Texture2D(4, 4) { name = "体_BaseColor" }), fileName = "体_BaseColor.png", role = "color" };
            var bangs = new TextureEntry { texture = Own(new Texture2D(4, 4) { name = "前髪_BaseColor" }), fileName = "前髪_BaseColor.png", role = "color" };
            TextureMatching.Match(new List<TextureEntry> { body, bangs }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreEqual("体", body.material ? body.material.name : null, body.reason);
            Assert.AreEqual("髪の毛", bangs.material ? bangs.material.name : null, bangs.reason);
            CollectionAssert.AreEqual(new[] { "髪", "base", "color" }, TextureMatching.Tokens("髪_BaseColor", stem: false));
            CollectionAssert.AreEqual(new[] { "cheveux", "été" }, TextureMatching.Tokens("Cheveux_été", stem: false));
        }

        // Poiyomi declares over a hundred texture slots per material: a large avatar still matches locally, and the AI request
        // carries the slots of the materials the drop is about first, at most the server's limit.
        [Test] public void LargeAvatarsMatchLocallyAndSendTheMostRelevantSlots()
        {
            var parts = Enumerable.Range(0, 100).Select(i => (Code(i), Code(i) + "_Albedo")).ToArray();
            var avatar = Avatar(parts);
            var slots = TextureMatching.Slots(avatar);
            var target = TextureEntry(Code(95) + "_BaseColor.png", "color");
            var unmatched = TextureEntry("Unrelated_Normal.png", "normal");
            var entries = new List<TextureEntry> { target, unmatched };
            TextureMatching.Match(entries, slots, new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreEqual(Code(95), target.material ? target.material.name : null, target.reason);
            var open = slots.Where(s => !s.secondary && !entries.Any(e => e.material == s.material && e.property == s.property)).ToList();
            Assert.Greater(open.Count, TextureAi.MaxSlots);
            var sent = TextureAi.Relevant(open, entries);
            Assert.AreEqual(TextureAi.MaxSlots, sent.Count);
            Assert.That(open.Where(s => s.material == target.material), Is.SubsetOf(sent));
            CollectionAssert.AreEqual(open.Where(sent.Contains), sent, "slots keep the avatar's order");
        }

        // IDs are positions in the request, so an accessory's slots far down a large avatar are "s0", "s1"... and answers map back.
        [Test] public void AiRequestNumbersSlotsWithinItself()
        {
            var parts = Enumerable.Range(0, 100).Select(i => (Code(i), Code(i) + "_Albedo")).ToArray();
            var slots = TextureMatching.Slots(Avatar(parts));
            var sent = slots.Where(s => s.materialName == Code(99)).ToList();
            var entry = TextureEntry("Thing.png", "unknown");
            var local = sent.First(s => s.property == "_MainTex");
            var placed = TextureEntry("Placed.png", "color"); placed.material = local.material; placed.property = local.property;
            object payload = TextureAi.Payload(new List<TextureEntry> { entry, placed }, sent, new Dictionary<Texture2D, TextureStats>());
            var ids = ((System.Collections.IEnumerable)Get(payload, "slots")).Cast<object>().Select(s => (string)Get(s, "id")).ToList();
            CollectionAssert.AreEqual(Enumerable.Range(0, sent.Count).Select(i => "s" + i), ids);
            var textures = ((System.Collections.IEnumerable)Get(payload, "textures")).Cast<object>().ToList();
            Assert.AreEqual("s" + sent.IndexOf(local), Get(textures[1], "localSlotId"));
            Assert.IsNull(Get(textures[0], "localSlotId"));

            int occlusion = sent.FindIndex(s => s.property == "_OcclusionMap");
            var plan = new TextureAi.Plan { assignments = new[] {
                new TextureAi.Assignment { textureId = "t0", slotId = "s" + occlusion, confidence = .95f, reason = "fixture" },
                new TextureAi.Assignment { textureId = "t0", slotId = "s" + sent.Count, confidence = .99f, reason = "outside the request" },
                new TextureAi.Assignment { textureId = "t0", slotId = "x1", confidence = .99f, reason = "malformed" },
            } };
            var change = TextureAi.Accept(plan, new List<TextureEntry> { entry }, sent).changes.Single();
            Assert.AreSame(sent[occlusion], change.slot);
        }

        private static object Get(object value, string name) => value.GetType().GetProperty(name).GetValue(value);

        // Distinct three-letter names ("baa", "bab"...): no name is a prefix of another.
        private static string Code(int i) => "B" + (char)('a' + i / 26) + (char)('a' + i % 26);

        private TextureEntry TextureEntry(string fileName, string role) =>
            new TextureEntry { texture = Own(new Texture2D(4, 4) { name = System.IO.Path.GetFileNameWithoutExtension(fileName) }), fileName = fileName, role = role };

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
