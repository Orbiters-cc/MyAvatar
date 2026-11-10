using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// Slots kept per avatar state and matching without re-tokenizing names (architecture review MAC-24, MAC-25) give what
    /// the code before them gave (<see cref="TextureMatchingReference"/>), on realistic avatars and file names.
    /// </summary>
    public sealed class TextureMatchingEquivalenceTests
    {
        private Scene scene;
        private readonly List<Object> owned = new List<Object>();

        [SetUp] public void SetUp() { scene = EditorSceneManager.NewPreviewScene(); TextureSlotCache.Clear(); }

        [TearDown] public void TearDown()
        {
            foreach (var value in owned) if (value) Object.DestroyImmediate(value);
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            TextureSlotCache.Clear();
        }

        private const string Prefix = "orbitmusclebodybuilderrexstarpose hoodies hairs for substance_";

        private static readonly string[][] Drops = {
            // A Substance folder: sets with one long prefix beside loose files.
            new[] { Prefix + "EyeMatt.001_BaseColor.png", Prefix + "MiscMatt.001_BaseColor.png", Prefix + "BodyMatt.001_Height.png", Prefix + "EyeMatt.001_Height.png",
                "hair_Material.002_Normal.png", "Height.png", "Height_4.png", "Metallic.png", "Metallic_2.png", "Normal.png", "Normal_3.png", "Roughness.png", "Roughness_1.png" },
            // One set of that export, the others already on the avatar.
            new[] { Prefix + "EyeMatt.001_BaseColor.png", Prefix + "EyeMatt.001_Emissive.png" },
            // A full clothing set, a variant of the body's texture, letter suffixes.
            new[] { "Shirt_Mat_BaseColor.png", "Shirt_Mat_Normal.png", "Shirt_Mat_Roughness.png", "Shirt_Mat_Metallic.png", "Shirt_Mat_AO.png", "Shirt_Mat_Height.png",
                "Shirt_Mat_Emissive.png", "Body_BaseMap_Green.png", "boots_D.png", "boots_N.png", "Pants_Alt_BaseColor.png" },
            // The hair's numbered material, one this avatar does not have, names in other scripts, an emission beside an albedo.
            new[] { "hair_Material.002_Normal.png", "hair_Material.002_Roughness.png", "Material.004_Normal.png", "体_BaseColor.png", "前髪_BaseColor.png",
                "Eyes_BaseColor.png", "Eyes_BaseColor3.png", "Sparkle_Glow.png", "Green_Body.png", "Unrelated_Normal.png", "Thing.png" },
        };

        [Test] public void SlotsAreTheSameAsBefore([Values(false, true)] bool includeAll)
        {
            var avatar = Avatar();
            AssertSameSlots(TextureMatchingReference.Slots(avatar, includeAll), TextureMatching.Slots(avatar, includeAll));
        }

        [Test] public void MatchesAreTheSameAsBefore([Values(0, 1, 2, 3, 4)] int drop)
        {
            var avatar = Avatar();
            var files = drop < Drops.Length ? Drops[drop] : Drops.SelectMany(d => d).Distinct().ToArray();
            var textures = files.Select(f => Own(new Texture2D(4, 4) { name = System.IO.Path.GetFileNameWithoutExtension(f) })).ToArray();
            var stats = new Dictionary<Texture2D, TextureStats>();
            for (int i = 0; i < files.Length; i++)
                stats[textures[i]] = files[i] == "Eyes_BaseColor3.png" ? new TextureStats { black = .8f, bright = .01f } : new TextureStats { black = .1f, bright = .5f };
            var memory = new Dictionary<string, List<TextureMemory.Slot>>();
            if (drop == 2) memory["texture:" + textures[0].GetInstanceID()] = new List<TextureMemory.Slot> {
                new TextureMemory.Slot { material = "Shirt_Mat", shader = "Standard (Specular setup)", property = "_MainTex" } };
            List<TextureEntry> Entries() => files.Select((f, i) => new TextureEntry { texture = textures[i], fileName = f, role = TextureMatching.FileRole(f) }).ToList();

            var before = Entries();
            TextureMatchingReference.Match(before, TextureMatchingReference.Slots(avatar), stats, memory);
            var now = Entries();
            TextureMatching.Match(now, TextureMatching.Slots(avatar), stats, memory);
            Assert.AreEqual(before.Count, now.Count, "entries, links included");
            for (int i = 0; i < before.Count; i++)
            {
                string what = before[i].fileName + " #" + i;
                Assert.AreSame(before[i].texture, now[i].texture, what);
                Assert.AreSame(before[i].material, now[i].material, what);
                Assert.AreEqual(before[i].property, now[i].property, what);
                Assert.AreEqual(before[i].confidence, now[i].confidence, what);
                Assert.AreEqual(before[i].reason, now[i].reason, what);
                Assert.AreEqual(before[i].suggestedMaterialName, now[i].suggestedMaterialName, what);
                Assert.AreEqual(before[i].suggestedProperty, now[i].suggestedProperty, what);
            }
            Assert.That(before.Count(e => e.material), Is.GreaterThan(0), "the fixture matches something");
        }

        // The cache follows what slots are made of; an unchanged avatar is not rebuilt.
        [Test] public void KeptSlotsFollowTheAvatar()
        {
            var avatar = Avatar();
            AssertSameSlots(TextureMatchingReference.Slots(avatar), TextureMatching.Slots(avatar));
            int builds = TextureSlotCache.Builds;
            TextureMatching.Slots(avatar);
            Assert.AreEqual(builds, TextureSlotCache.Builds, "unchanged avatar");

            void Changed(string what)
            {
                AssertSameSlots(TextureMatchingReference.Slots(avatar), TextureMatching.Slots(avatar));
                Assert.AreEqual(builds + 1, TextureSlotCache.Builds, what);
                builds = TextureSlotCache.Builds;
            }
            var body = avatar.transform.Find("Body").GetComponent<MeshRenderer>();
            body.sharedMaterials[0].SetTexture("_BumpMap", Own(new Texture2D(4, 4) { name = "Body_Normal" }));
            Changed("a texture set");
            avatar.transform.Find("Rexouium Spiky hair V2 ").name = "Long hair";
            Changed("an object above a renderer renamed");
            avatar.transform.Find("Outfit B").gameObject.SetActive(true);
            Changed("an outfit shown");
            var materials = body.sharedMaterials;
            materials[1] = Own(new Material(Shader.Find("Standard")) { name = "Swapped" });
            body.sharedMaterials = materials;
            Changed("a material replaced");
            body.sharedMaterials[0].name = "BodyMatt Renamed";
            Changed("a material renamed");
            body.sharedMaterials[0].shader = Shader.Find("Standard (Specular setup)");
            Changed("a shader changed");
            var extra = new GameObject("Hat"); extra.transform.SetParent(avatar.transform, false);
            extra.AddComponent<MeshFilter>().sharedMesh = Triangle();
            extra.AddComponent<MeshRenderer>().sharedMaterial = Own(new Material(Shader.Find("Standard")) { name = "Hat_Mat" });
            Changed("a renderer added");
            var copies = TextureMatching.Slots(avatar);
            copies[0].fillOnly = true;
            Assert.IsFalse(TextureMatching.Slots(avatar)[0].fillOnly, "each call has its own slots");
        }

        // What slots read beyond object identities (architecture review follow-up): a texture made in memory renamed or
        // resized, a material's original-shader GUID tag, and a locked material's stripped-texture tags.
        [Test] public void KeptSlotsFollowTagsAndTextureFacts()
        {
            var avatar = Avatar();
            var body = avatar.transform.Find("Body").GetComponent<MeshRenderer>();
            var main = body.sharedMaterials[0].GetTexture("_MainTex") as Texture2D;
            TextureMatching.Slots(avatar);
            int builds = TextureSlotCache.Builds;
            void Changed(string what)
            {
                AssertSameSlots(TextureMatchingReference.Slots(avatar), TextureMatching.Slots(avatar));
                Assert.AreEqual(builds + 1, TextureSlotCache.Builds, what);
                builds = TextureSlotCache.Builds;
                TextureMatching.Slots(avatar);
                Assert.AreEqual(builds, TextureSlotCache.Builds, what + ", then unchanged");
            }
            main.name = "Renamed in memory";
            Changed("a texture renamed");
            main.Reinitialize(8, 8);
            Changed("a texture resized");
            body.sharedMaterials[0].SetOverrideTag("OriginalShaderGUID", "0123456789abcdef0123456789abcdef");
            Changed("the original shader's GUID tag");

            // A locked material: its slots come from its original shader, and a slot its own shader lacks shows the texture
            // its stripped tag names.
            var images = UnityEditor.AssetDatabase.FindAssets("t:Texture2D").Take(2).ToArray();
            if (images.Length < 2) Assert.Ignore("needs two texture assets in the project");
            var locked = Own(UnityEditor.ShaderUtil.CreateShaderAsset("Shader \"Hidden/Locked/My Avatar Test\" { SubShader { Pass { } } }"));
            var material = Own(new Material(locked) { name = "Locked" });
            material.SetOverrideTag("OriginalShader", "Standard");
            material.SetOverrideTag("_stripped_tex__MainTex", images[0]);
            var materials = body.sharedMaterials; materials[1] = material; body.sharedMaterials = materials;
            Changed("a locked material");
            Assert.AreEqual(UnityEditor.AssetDatabase.GUIDToAssetPath(images[0]), TextureMatching.Slots(avatar).First(s => s.material == material && s.property == "_MainTex").existingPath);
            material.SetOverrideTag("_stripped_tex__MainTex", images[1]);
            Changed("a stripped texture's tag");
        }

        private static void AssertSameSlots(List<TextureSlot> before, List<TextureSlot> now)
        {
            Assert.AreEqual(before.Count, now.Count, "slots");
            for (int i = 0; i < before.Count; i++)
            {
                TextureSlot a = before[i], b = now[i];
                string what = a.Label;
                Assert.AreSame(a.material, b.material, what);
                Assert.AreEqual(a.materialName, b.materialName, what);
                Assert.AreEqual(a.shader, b.shader, what);
                Assert.AreEqual(a.property, b.property, what);
                Assert.AreEqual(a.description, b.description, what);
                Assert.AreEqual(a.role, b.role, what);
                Assert.AreEqual(a.secondary, b.secondary, what);
                Assert.AreSame(a.existing, b.existing, what);
                Assert.AreEqual(a.existingName, b.existingName, what);
                Assert.AreEqual(a.existingPath, b.existingPath, what);
                Assert.AreEqual((a.existingWidth, a.existingHeight), (b.existingWidth, b.existingHeight), what);
                Assert.AreEqual(a.rendererPath, b.rendererPath, what);
                Assert.AreEqual(a.rendererKind, b.rendererKind, what);
                Assert.AreEqual(a.active, b.active, what);
                Assert.AreEqual(a.fillOnly, b.fillOnly, what);
                CollectionAssert.AreEqual(a.renderers, b.renderers, what);
                CollectionAssert.AreEqual(a.parts, b.parts, what);
                CollectionAssert.AreEquivalent(a.ownerWords, b.ownerWords, what);
                CollectionAssert.AreEquivalent(a.namingWords, b.namingWords, what);
            }
        }

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        private Mesh Triangle()
        {
            var mesh = Own(new Mesh());
            mesh.SetVertices(new List<Vector3> { Vector3.zero, Vector3.up, Vector3.right });
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.up, Vector2.right });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
        }

        private Material Material(string name, string texture, string shader = "Standard")
        {
            var material = Own(new Material(Shader.Find(shader)) { name = name });
            if (texture != null) material.SetTexture("_MainTex", Own(new Texture2D(4, 4) { name = texture }));
            return material;
        }

        // A Rexouium-like avatar: a body split into material slots, hair under a named object, a skinned shirt, boots
        // sharing one material on two meshes, a particle effect, and a hidden outfit.
        private MyAvatar Avatar()
        {
            var root = Own(new GameObject("Rexouium1.6 Default Setup"));
            SceneManager.MoveGameObjectToScene(root, scene);
            var parts = new[] { ("BodyMatt", Prefix + "BodyMatt.001_BaseColor"), ("EyesMap_Green Toon", "Eyes_Green"), ("FeathersMatt", "Feathers_BaseMap"),
                ("MiscMatt Toon", Prefix + "MiscMatt.001_BaseColor"), ("EyeLensMap", "T_MainTex_D"), ("体", "Skin_A"), ("髪の毛", "Strands_B") };
            var body = new GameObject("Body"); body.transform.SetParent(root.transform, false);
            var mesh = Own(new Mesh { subMeshCount = parts.Length });
            var vertices = new List<Vector3>(); var uvs = new List<Vector2>();
            for (int i = 0; i < parts.Length; i++)
            {
                vertices.AddRange(new[] { new Vector3(i, 0, 0), new Vector3(i, 1, 0), new Vector3(i + 1, 0, 0) });
                uvs.AddRange(new[] { new Vector2(i * .1f, 0), new Vector2(i * .1f, .1f), new Vector2(i * .1f + .1f, 0) });
            }
            mesh.SetVertices(vertices); mesh.SetUVs(0, uvs);
            for (int i = 0; i < parts.Length; i++) mesh.SetTriangles(new[] { i * 3, i * 3 + 1, i * 3 + 2 }, i);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            body.AddComponent<MeshRenderer>().sharedMaterials = parts.Select(p => Material(p.Item1, p.Item2)).ToArray();

            var hairGroup = new GameObject("Rexouium Spiky hair V2 "); hairGroup.transform.SetParent(root.transform, false);
            var strands = new GameObject("NurbsPath.001"); strands.transform.SetParent(hairGroup.transform, false);
            strands.AddComponent<MeshFilter>().sharedMesh = Triangle();
            var hair = Material("Material.002", null);
            hair.SetTexture("_BumpMap", Own(new Texture2D(4, 4) { name = "canglha_Material.002_Normal" }));
            strands.AddComponent<MeshRenderer>().sharedMaterial = hair;

            var armature = new GameObject("Armature"); armature.transform.SetParent(root.transform, false);
            var hips = new GameObject("Hips"); hips.transform.SetParent(armature.transform, false);
            var chest = new GameObject("Chest"); chest.transform.SetParent(hips.transform, false);
            var shirt = new GameObject("Shirt"); shirt.transform.SetParent(root.transform, false);
            var skinned = shirt.AddComponent<SkinnedMeshRenderer>();
            skinned.sharedMesh = Triangle(); skinned.bones = new[] { hips.transform, chest.transform }; skinned.rootBone = hips.transform;
            skinned.sharedMaterial = Material("Shirt_Mat", "Shirt_Old_BaseColor", "Standard (Specular setup)");

            var boots = Material("Boots", "boots_old_D");
            foreach (var side in new[] { "Boot_L", "Boot_R" })
            {
                var boot = new GameObject(side); boot.transform.SetParent(root.transform, false);
                boot.AddComponent<MeshFilter>().sharedMesh = Triangle();
                boot.AddComponent<MeshRenderer>().sharedMaterial = boots;
            }

            var sparkles = new GameObject("Sparkles"); sparkles.transform.SetParent(root.transform, false);
            sparkles.AddComponent<ParticleSystem>();
            sparkles.GetComponent<ParticleSystemRenderer>().sharedMaterial = Material("Sparkle", "Sparkle_Tex", "Unlit/Texture");

            var outfit = new GameObject("Outfit B"); outfit.transform.SetParent(root.transform, false);
            var pants = new GameObject("Pants"); pants.transform.SetParent(outfit.transform, false);
            pants.AddComponent<MeshFilter>().sharedMesh = Triangle();
            pants.AddComponent<MeshRenderer>().sharedMaterial = Material("Pants_Alt", "Pants_Alt_Old");
            outfit.SetActive(false);
            return root.AddComponent<MyAvatar>();
        }
    }
}
