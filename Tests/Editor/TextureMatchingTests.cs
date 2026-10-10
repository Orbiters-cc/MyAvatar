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
        private readonly List<string> folders = new List<string>();

        [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown] public void TearDown()
        {
            foreach (var value in owned) if (value) Object.DestroyImmediate(value);
            owned.Clear();
            foreach (var folder in folders) if (UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.DeleteAsset(folder);
            folders.Clear();
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

        // A Substance folder: three sets exported with one long prefix ("… hairs for substance_EyeMatt.001_BaseColor"), next
        // to files without it, so the prefix is on fewer than half of them. Its "hairs" must not pull the eyes to the feathers.
        [Test] public void AnExportPrefixSeveralSetsShareDoesNotNameAPart()
        {
            var avatar = Avatar(("BodyMatt", "Body_BaseMap"), ("EyesMap_Green", "Eyes_Green"), ("FeathersMatt", "Feathers_BaseMap"), ("MiscMatt", "Misc_BaseMap"), ("EyeLensMap", "T_MainTex_D"));
            const string prefix = "orbitmusclebodybuilderrexstarpose hoodies hairs for substance_";
            var eyes = TextureEntry(prefix + "EyeMatt.001_BaseColor.png", "color");
            var misc = TextureEntry(prefix + "MiscMatt.001_BaseColor.png", "color");
            // Made for the hair's "Material.002", which this avatar doesn't have: not guessed onto the feathers either.
            var hair = TextureEntry("hair_Material.002_Normal.png", "normal");
            var entries = new List<TextureEntry> { eyes, misc, hair, TextureEntry(prefix + "BodyMatt.001_Height.png", "height"), TextureEntry(prefix + "EyeMatt.001_Height.png", "height") };
            foreach (var name in new[] { "Height.png", "Height_4.png", "Metallic.png", "Metallic_2.png", "Normal.png", "Normal_3.png", "Roughness.png", "Roughness_1.png" })
                entries.Add(TextureEntry(name, "unknown"));
            TextureMatching.Match(entries, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreEqual("EyesMap_Green", eyes.material ? eyes.material.name : eyes.reason);
            Assert.AreEqual("MiscMatt", misc.material ? misc.material.name : misc.reason);
            Assert.IsNull(hair.material, "assigned to " + (hair.material ? hair.material.name : ""));
            StringAssert.Contains("Material.002", hair.reason);
        }

        // The same prefix with one set dropped alone: the other sets are already on the avatar (MiscMatt's and BodyMatt's
        // images), and their shared "Matt" and ".001" do not pull the eyes there either.
        [Test] public void OneSetOfAnExportFindsItsPartBesideTheSetsAlreadyThere()
        {
            const string prefix = "orbitmusclebodybuilderrexstarpose hoodies hairs for substance_";
            var avatar = Avatar(("BodyMatt", prefix + "BodyMatt.001_BaseColor"), ("EyesMap_Green", "Eyes_Green"), ("FeathersMatt", "Feathers_BaseMap"),
                ("MiscMatt Toon", prefix + "MiscMatt.001_BaseColor"), ("EyeLensMap", "T_MainTex_D"));
            // As on the Rexouium: the eyes glow through their own mask, MiscMatt through its exported one.
            var slots = TextureMatching.Slots(avatar);
            slots.First(s => s.materialName == "EyesMap_Green").material.SetTexture("_EmissionMap", Own(new Texture2D(4, 4) { name = "EyeEmissionMaskBlur" }));
            slots.First(s => s.materialName == "MiscMatt Toon").material.SetTexture("_EmissionMap", Own(new Texture2D(4, 4) { name = prefix + "MiscMatt.001_Emissive" }));
            var colour = TextureEntry(prefix + "EyeMatt.001_BaseColor.png", "color");
            var glow = TextureEntry(prefix + "EyeMatt.001_Emissive.png", "emission");
            TextureMatching.Match(new List<TextureEntry> { colour, glow }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreEqual("EyesMap_Green", colour.material ? colour.material.name : colour.reason);
            Assert.AreEqual("EyesMap_Green", glow.material ? glow.material.name : glow.reason);
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

        // A Rexouium's hair: Blender's "Material.002" under "Rexouium Spiky hair V2", with "canglha_Material.002_Normal". The
        // dropped "hair_Material.002_*" maps belong there, not on the body's MiscMatt whose texture export prefix happens to
        // say "hoodies hairs for substance".
        [Test] public void NumberedMaterialNameAndTheObjectAboveFindTheHair()
        {
            var (avatar, hair) = WithHair("canglha_Material.002_Normal");
            var normal = TextureEntry("hair_Material.002_Normal.png", "normal");
            var rough = TextureEntry("hair_Material.002_Roughness.png", "roughness");
            TextureMatching.Match(new List<TextureEntry> { normal, rough }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreSame(hair, normal.material, normal.reason);
            Assert.AreEqual("_BumpMap", normal.property);
            Assert.AreSame(hair, rough.material, rough.reason);
            Assert.AreEqual("_MetallicGlossMap", rough.property);
        }

        // Without a material name to go by, the object holding the hair names it; the avatar's own name ("Rexouium", in the
        // hair object's name too) names no part.
        [Test] public void TheObjectHoldingAPartNamesItButTheAvatarsNameDoesNot()
        {
            var (avatar, hair) = WithHair(null);
            var strands = TextureEntry("Hair_Normal.png", "normal");
            TextureMatching.Match(new List<TextureEntry> { strands }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreSame(hair, strands.material, strands.reason);
            var whole = TextureEntry("Rexouium_Normal.png", "normal");
            TextureMatching.Match(new List<TextureEntry> { whole }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreNotSame(hair, whole.material, whole.reason);
        }

        // The user's avatar: a drop made before the hair's names were understood left "hair_Material.002_Normal" on the body's
        // MiscMatt. Dropping the two files again (the import cache hands back that same copy) still finds the hair: where an
        // earlier drop put its own copy is no evidence, or the wrong guess would come back on every drop.
        [Test] public void AnEarlierWrongGuessDoesNotPullTheSameFilesBack()
        {
            var (avatar, hair) = WithHair(null);
            var earlier = Dropped("hair_Material.002_Normal");
            var misc = TextureMatching.Slots(avatar).First(s => s.materialName == "MiscMatt Toon").material;
            misc.SetTexture("_BumpMap", earlier);
            var normal = new TextureEntry { texture = earlier, fileName = "hair_Material.002_Normal.png", role = "normal" };
            var rough = TextureEntry("hair_Material.002_Roughness.png", "roughness");
            TextureMatching.Match(new List<TextureEntry> { normal, rough }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreSame(hair, normal.material, normal.reason);
            Assert.AreEqual("_BumpMap", normal.property);
            Assert.AreSame(hair, rough.material, rough.reason);
            Assert.AreEqual("_MetallicGlossMap", rough.property);
        }

        // The same with a project image an earlier drop used as it is, in the material copy it made: the EyeMatt colour map
        // left on the feathers' copy does not pull the EyeMatt set back to the feathers.
        [Test] public void AnEarlierGuessOfAProjectImageDoesNotPullItBack()
        {
            var avatar = Avatar(("BodyMatt", "Body_BaseMap"), ("EyesMap_Green", "Eyes_Green"), ("FeathersMatt", "Feathers_BaseMap"), ("MiscMatt", "Misc_BaseMap"));
            string batch = "Assets/Orbiters/MyAvatar/" + System.Guid.NewGuid().ToString("N");
            TextureImport.EnsureFolder(batch + "/Materials"); folders.Add(batch);
            string imagePath = batch + "/orbitmusclebodybuilderrexstarpose hoodies hairs for substance_EyeMatt.001_BaseColor.png";
            var image = new Texture2D(4, 4);
            System.IO.File.WriteAllBytes(imagePath, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            UnityEditor.AssetDatabase.ImportAsset(imagePath, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
            var eyeColour = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(imagePath);
            var renderer = avatar.GetComponentInChildren<MeshRenderer>();
            var materials = renderer.sharedMaterials;
            int feathers = System.Array.FindIndex(materials, m => m.name == "FeathersMatt");
            var copy = new Material(materials[feathers]) { name = "FeathersMatt" };
            copy.SetTexture("_MainTex", eyeColour);
            UnityEditor.AssetDatabase.CreateAsset(copy, batch + "/Materials/FeathersMatt.mat");
            materials[feathers] = copy; renderer.sharedMaterials = materials;
            var again = new TextureEntry { texture = eyeColour, fileName = System.IO.Path.GetFileName(imagePath), role = "color" };
            TextureMatching.Match(new List<TextureEntry> { again }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.AreEqual("EyesMap_Green", again.material ? again.material.name : again.reason);
        }

        // A slot you chose is remembered, so the image a drop put there still names it for the rest of that set.
        [Test] public void AChosenSlotStillNamesItsMaterialForTheRestOfTheSet()
        {
            var avatar = Avatar(("Material.001", "Material.001_Albedo"), ("Material.003", "Material.003_Albedo"), ("Material.004", "Material.004_Albedo"));
            var skin = TextureMatching.Slots(avatar).First(s => s.materialName == "Material.001").material;
            skin.SetTexture("_MainTex", Dropped("Skin_BaseColor"));
            var guessed = TextureEntry("Skin_Normal.png", "normal");
            TextureMatching.Match(new List<TextureEntry> { guessed }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), new Dictionary<string, List<TextureMemory.Slot>>());
            Assert.IsNull(guessed.material, "an earlier guess is no evidence");
            var memory = new Dictionary<string, List<TextureMemory.Slot>> { ["source:skin-basecolor"] = new List<TextureMemory.Slot> {
                new TextureMemory.Slot { material = "Material.001", shader = "Standard", property = "_MainTex" } } };
            var chosen = TextureEntry("Skin_Normal.png", "normal");
            TextureMatching.Match(new List<TextureEntry> { chosen }, TextureMatching.Slots(avatar), new Dictionary<Texture2D, TextureStats>(), memory);
            Assert.AreSame(skin, chosen.material, chosen.reason);
            Assert.AreEqual("_BumpMap", chosen.property);
        }

        // An image of an earlier drop: its copy in My Avatar's drop folder.
        private Texture2D Dropped(string name)
        {
            string folder = TextureImport.DropFolder + "MatchingTest-" + System.Guid.NewGuid().ToString("N"), path = folder + "/" + name + ".png";
            TextureImport.EnsureFolder(folder); folders.Add(folder);
            var image = new Texture2D(4, 4);
            System.IO.File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceSynchronousImport);
            return UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private (MyAvatar avatar, Material hair) WithHair(string normalMap)
        {
            var avatar = Avatar(("MiscMatt Toon", "orbitmusclebodybuilderrexstarpose hoodies hairs for substance_MiscMatt.001_BaseColor"),
                ("BodyMatt", "ultirexv5_spp_BodyMatt.001_BaseColor"), ("EyesMap_Green Toon", "Eyes_Green"));
            avatar.gameObject.name = "Rexouium1.6 Default Setup";
            var group = new GameObject("Rexouium Spiky hair V2 "); group.transform.SetParent(avatar.transform, false);
            var strands = new GameObject("NurbsPath.001"); strands.transform.SetParent(group.transform, false);
            var mesh = Own(new Mesh());
            mesh.SetVertices(new List<Vector3> { Vector3.zero, Vector3.up, Vector3.right });
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.up, Vector2.right });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            strands.AddComponent<MeshFilter>().sharedMesh = mesh;
            var hair = Own(new Material(Shader.Find("Standard")) { name = "Material.002" });
            if (normalMap != null) hair.SetTexture("_BumpMap", Own(new Texture2D(4, 4) { name = normalMap }));
            strands.AddComponent<MeshRenderer>().sharedMaterial = hair;
            return (avatar, hair);
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
