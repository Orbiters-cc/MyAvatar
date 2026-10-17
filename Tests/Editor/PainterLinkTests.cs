using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Painter;
using Orbiters.Toolkit.Editor.Meshes;
using Orbiters.Toolkit.Editor.Textures;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The Substance 3D Painter link: where a texture set's maps go on a material, which material a texture set paints, what
    // shows live and what saving in Painter applies.
    public sealed class PainterLinkTests
    {
        private Scene scene;
        private readonly List<Object> owned = new List<Object>();
        private readonly List<string> folders = new List<string>();
        private string temp;

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            temp = Path.Combine(Path.GetTempPath(), "OrbitersPainterTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
        }

        [TearDown] public void TearDown()
        {
            foreach (var value in owned) if (value) { Undo.ClearUndo(value); Object.DestroyImmediate(value); }
            owned.Clear();
            foreach (var folder in folders) if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            folders.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        private T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        private MyAvatar Avatar()
        {
            var root = Own(new GameObject("Painter avatar"));
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<MyAvatar>();
        }

        private MeshRenderer Part(MyAvatar avatar, string name, Material material)
        {
            var part = new GameObject(name);
            part.transform.SetParent(avatar.transform, false);
            part.AddComponent<MeshFilter>();
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return renderer;
        }

        private Material Standard(string name) => Own(new Material(Shader.Find("Standard")) { name = name });

        private string Png(string name, Color32 color, int size = 4)
        {
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            image.SetPixels32(Enumerable.Repeat(color, size * size).ToArray());
            string path = Path.Combine(temp, name + ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            return path.Replace('\\', '/');
        }

        [Test]
        public void StandardMaterialsTakeMetallicInRedAndSmoothnessInAlpha()
        {
            var avatar = Avatar();
            var material = Standard("Skin");
            Part(avatar, "Body", material);
            var targets = PainterTargets.For(material, TextureMatching.Slots(avatar));
            Assert.That(targets.Color, Is.EqualTo("_MainTex"));
            Assert.That(targets.Normal, Is.EqualTo("_BumpMap"));
            Assert.That(targets.Emission, Is.EqualTo("_EmissionMap"));
            Assert.That((targets.Metallic.Property, targets.Metallic.Index), Is.EqualTo(("_MetallicGlossMap", 0)));
            Assert.That((targets.Smoothness.Property, targets.Smoothness.Index, targets.Smoothness.Inverted), Is.EqualTo(("_MetallicGlossMap", 3, false)));
            Assert.That(targets.Maps, Is.EquivalentTo(new[] { "basecolor", "normal", "roughness", "metallic", "emissive", "ambientocclusion" }));
            // Empty slots may be off in the shader: only the base colour shows live until the first save fills them.
            Assert.That(targets.LiveChannels(material), Is.EqualTo(new[] { "basecolor" }));
            Assert.That(targets.Slot("roughness"), Is.EqualTo(("roughness", "_MetallicGlossMap")));
            Assert.That(PainterTargets.For(Standard("Elsewhere"), TextureMatching.Slots(avatar)), Is.Null, "A material the avatar doesn't show.");
        }

        [Test]
        public void TextureSetsAreMatchedByNameAndFollowTheirRendererSlot()
        {
            var avatar = Avatar();
            var skin = Standard("Skin");
            var clothes = Standard("Clothes");
            Part(avatar, "Body", skin);
            var outfit = Part(avatar, "Outfit", clothes);
            PainterMapping.AutoMap(avatar, new[] { "Skin", "Clothes_Jacket", "Unrelated" });
            Assert.That(PainterMapping.Material(avatar, "Skin"), Is.SameAs(skin));
            Assert.That(PainterMapping.Material(avatar, "Clothes_Jacket"), Is.SameAs(clothes), "Half its words match, and no other material does.");
            Assert.That(PainterMapping.Material(avatar, "Unrelated"), Is.Null);
            Assert.That(PainterMapping.Key(avatar, "Unrelated"), Is.Null);
            // A copy of the material (as My Avatar makes when applying textures) is still what the texture set paints.
            var copy = Own(new Material(clothes) { name = "Clothes" });
            outfit.sharedMaterial = copy;
            Assert.That(PainterMapping.Material(avatar, "Clothes_Jacket"), Is.SameAs(copy));
            // Mapped by hand: kept, never matched again.
            PainterMapping.Set(avatar, "Unrelated", PainterMapping.Choices(avatar).First(c => c.Material == skin));
            PainterMapping.AutoMap(avatar, new[] { "Unrelated" });
            Assert.That(PainterMapping.Material(avatar, "Unrelated"), Is.SameAs(skin));
        }

        // Live images go into the renderers' property blocks: the material, its assets and the scene stay as they are.
        [Test]
        public void LiveImagesShowThroughPropertyBlocksOnly()
        {
            var avatar = Avatar();
            var material = Standard("Skin");
            var renderer = Part(avatar, "Body", material);
            var targets = PainterTargets.For(material, TextureMatching.Slots(avatar));
            var tga = new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 24, 0, 10, 20, 30 };
            using (var preview = new PainterLivePreview())
            {
                preview.Show(avatar.transform, "Skin", "basecolor", 1, TgaImage.Read(tga), material, targets);
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block, 0);
                var shown = block.GetTexture("_MainTex") as Texture2D;
                Assert.That(shown, Is.Not.Null);
                Assert.That((Color32)shown.GetPixel(0, 0), Is.EqualTo(new Color32(30, 20, 10, 255)));
                Assert.That(material.GetTexture("_MainTex"), Is.Null, "The material itself is untouched.");
                Assert.That(preview.Stale("Skin", "basecolor", 1), Is.True, "An image as old as the one shown is dropped.");
                Assert.That(preview.Live, Is.EqualTo(new[] { "Skin" }));
                preview.Clear("Skin");
                renderer.GetPropertyBlock(block, 0);
                Assert.That(block.isEmpty, Is.True);
            }
        }

        // Saving in Painter applies its maps as a texture drop does; the next save edits the same material copy and files.
        [Test]
        public void SavedMapsAreAppliedAndTheNextSaveUpdatesThem()
        {
            var avatar = Avatar();
            var material = Standard("Skin");
            var renderer = Part(avatar, "Body", material);
            PainterMapping.AutoMap(avatar, new[] { "Skin" });
            (Material, PainterTargets) Resolve(string set)
            {
                var current = PainterMapping.Material(avatar, set);
                return (current, PainterTargets.For(current, TextureMatching.Slots(avatar)));
            }
            var first = new List<PainterLinkProtocol.CommitMap>
            {
                new PainterLinkProtocol.CommitMap { textureSet = "Skin", map = "basecolor", path = Png("color", new Color32(200, 10, 10, 255)) },
                new PainterLinkProtocol.CommitMap { textureSet = "Skin", map = "roughness", path = Png("rough", new Color32(64, 64, 64, 255)) },
                new PainterLinkProtocol.CommitMap { textureSet = "Skin", map = "metallic", path = Png("metal", new Color32(255, 255, 255, 255)) },
            };
            Assert.That(PainterCommit.Apply(avatar, first, Resolve), Is.EqualTo(3));
            string folder = avatar.painter.textureFolder;
            folders.Add(folder);
            var applied = renderer.sharedMaterial;
            Assert.That(applied, Is.Not.SameAs(material), "Applied on a copy, as drops are.");
            var color = applied.GetTexture("_MainTex");
            Assert.That(AssetDatabase.GetAssetPath(color), Is.EqualTo(folder + "/Painter/Skin_BaseColor.png"));
            Assert.That(((TextureImporter)AssetImporter.GetAtPath(folder + "/Painter/Skin_Roughness.png")).sRGBTexture, Is.False);
            Assert.That(applied.GetTexture("_MetallicGlossMap"), Is.Not.Null, "Roughness and metallic packed into Standard's map.");

            var second = new List<PainterLinkProtocol.CommitMap>
            {
                new PainterLinkProtocol.CommitMap { textureSet = "Skin", map = "basecolor", path = Png("color2", new Color32(10, 200, 10, 255)) },
            };
            Assert.That(PainterCommit.Apply(avatar, second, Resolve), Is.GreaterThanOrEqualTo(0));
            Assert.That(avatar.painter.textureFolder, Is.EqualTo(folder), "The same batch goes on.");
            // The asset may come back as another C# object after the reimport: the same material is the same instance.
            Assert.That(renderer.sharedMaterial.GetInstanceID(), Is.EqualTo(applied.GetInstanceID()), "The same material copy is edited again.");
            var updated = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Painter/Skin_BaseColor.png");
            Assert.That(renderer.sharedMaterial.GetTexture("_MainTex") == updated, Is.True);
            var pixels = new Texture2D(1, 1);
            try
            {
                pixels.LoadImage(File.ReadAllBytes(folder + "/Painter/Skin_BaseColor.png"));
                Assert.That(((Color32)pixels.GetPixel(0, 0)).g, Is.EqualTo(200), "The file holds the newest save.");
            }
            finally { Object.DestroyImmediate(pixels); }
        }

        // Undo in the drop field, then a save painting one map: only that map is applied; the undone ones stay undone.
        [Test]
        public void ASaveAfterUndoAppliesOnlyWhatItBrings()
        {
            var avatar = Avatar();
            var skin = Standard("Skin");
            var clothes = Standard("Clothes");
            var body = Part(avatar, "Body", skin);
            var outfit = Part(avatar, "Outfit", clothes);
            PainterMapping.AutoMap(avatar, new[] { "Skin", "Clothes" });
            (Material, PainterTargets) Resolve(string set)
            {
                var current = PainterMapping.Material(avatar, set);
                return (current, PainterTargets.For(current, TextureMatching.Slots(avatar)));
            }
            var everything = new List<PainterLinkProtocol.CommitMap>
            {
                new PainterLinkProtocol.CommitMap { textureSet = "Skin", map = "basecolor", path = Png("skin", new Color32(200, 10, 10, 255)) },
                new PainterLinkProtocol.CommitMap { textureSet = "Clothes", map = "basecolor", path = Png("clothes", new Color32(10, 10, 200, 255)) },
            };
            Assert.That(PainterCommit.Apply(avatar, everything, Resolve), Is.EqualTo(2));
            folders.Add(avatar.painter.textureFolder);
            TextureChanges.UndoLast(avatar);
            Assert.That(avatar.canRedo, Is.True);
            Assert.That(outfit.sharedMaterial.GetInstanceID(), Is.EqualTo(clothes.GetInstanceID()), "Undo shows the clothes' own material again.");

            var one = new List<PainterLinkProtocol.CommitMap>
            {
                new PainterLinkProtocol.CommitMap { textureSet = "Skin", map = "basecolor", path = Png("skin2", new Color32(10, 200, 10, 255)) },
            };
            Assert.That(PainterCommit.Apply(avatar, one, Resolve), Is.EqualTo(1));
            folders.Add(avatar.painter.textureFolder);
            Assert.That(outfit.sharedMaterial.GetInstanceID(), Is.EqualTo(clothes.GetInstanceID()), "The undone clothes texture stays undone.");
            Assert.That(body.sharedMaterial.GetInstanceID(), Is.Not.EqualTo(skin.GetInstanceID()), "The skin gets the saved map.");
            Assert.That(avatar.textures.Count, Is.EqualTo(1));
        }

        // A change of the avatar's meshes (MCB applying a Blender export) outdates the mesh of a project made from the avatar;
        // a project linked from Painter keeps its own mesh. Without the project open, nothing is built until it links.
        [Test]
        public void AMeshChangeOutdatesOnlyAProjectMadeFromTheAvatar()
        {
            var made = Avatar();
            made.painter.posed = true;
            var linked = Avatar();
            AvatarMeshEdits.NotifyApplied(made.transform);
            AvatarMeshEdits.NotifyApplied(linked.transform);
            Assert.That(made.painter.meshOutdated, Is.True);
            Assert.That(linked.painter.meshOutdated, Is.False);
            Assert.That(PainterMeshUpdate.Busy(made), Is.False);
        }

        // The mesh sent again is written as the project's was (a texture set per material), and only the avatar's own earlier
        // ones are cleared: another avatar's name may start the same.
        [Test]
        public void MeshUpdatesReplaceOnlyTheAvatarsEarlierOnes()
        {
            var cube = Own(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx")));
            var skin = Standard("Skin");
            var body = Avatar(); body.gameObject.name = "OrbitersPainterTestBody";
            var suit = Avatar(); suit.gameObject.name = "OrbitersPainterTestBody-Suit";
            List<PainterPose.Part> Parts() => new List<PainterPose.Part>
                { new PainterPose.Part { Name = "Body", Mesh = cube, ToWorld = Matrix4x4.identity, Materials = new[] { skin } } };
            var written = new List<string>();
            try
            {
                written.Add(PainterProjectExport.WriteMeshUpdate(suit, Parts()));
                written.Add(PainterProjectExport.WriteMeshUpdate(body, Parts()));
                // A later second: another file name.
                System.Threading.Thread.Sleep(1100);
                written.Add(PainterProjectExport.WriteMeshUpdate(body, Parts()));
                Assert.That(File.Exists(written[0]), Is.True, "The other avatar's mesh stays.");
                Assert.That(File.Exists(written[1]), Is.False, "The avatar's earlier mesh goes.");
                Assert.That(File.Exists(written[2]), Is.True);
                Assert.That(File.ReadAllText(written[2]), Does.Contain("usemtl Skin"));
            }
            finally
            {
                foreach (string path in written)
                    foreach (string file in new[] { path, Path.ChangeExtension(path, ".mtl") })
                        if (File.Exists(file)) File.Delete(file);
            }
        }

        // A built copy shows its own copies of the materials (VRCFury names them "VRCFury M_Skin (Body)"): the texture set is
        // the avatar's material, found through the renderer and slot that show it, and named after it; copies of one
        // material paint one texture set.
        [Test]
        public void BuiltMaterialCopiesPaintTheAvatarsMaterials()
        {
            var avatar = Avatar();
            var skin = Standard("M_Skin");
            Part(avatar, "Body", skin);
            var copy = Avatar();
            var clone = Standard("VRCFury M_Skin (Body)");
            var other = Standard("VRCFury M_Skin (Body) 2");
            var copied = Part(copy, "Body", clone);
            var mesh = Own(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx")));
            var parts = new List<PainterPose.Part>
            {
                new PainterPose.Part { Name = "Body", Mesh = mesh, ToWorld = Matrix4x4.identity, Materials = new[] { clone }, Source = copied },
                new PainterPose.Part { Name = "Body", Mesh = mesh, ToWorld = Matrix4x4.identity, Materials = new[] { other }, Source = copied },
            };
            var sets = PainterProjectExport.Sets(avatar, parts);
            Assert.That(sets[clone].Name, Is.EqualTo("M_Skin"));
            Assert.That(sets[clone].Choice?.Material, Is.SameAs(skin));
            Assert.That(sets[other], Is.SameAs(sets[clone]), "Both copies paint the avatar's one material.");
        }

        // The window edits its own copy of the choices; the avatar keeps the ones its project was made with.
        [Test]
        public void PoseChoicesAreCopiedNotShared()
        {
            var options = new PainterPoseOptions { placement = 2 };
            options.shapes.Add(PainterPose.FromKey(PainterPose.Key("Body", "vrc.v_aa")));
            options.copies.Add(PainterPose.Relaxed);
            var copy = PainterPose.Copy(options);
            copy.shapes[0].weight = 40f;
            copy.copies.Clear();
            Assert.That(options.shapes[0].weight, Is.EqualTo(100f));
            Assert.That(options.copies, Is.EqualTo(new[] { PainterPose.Relaxed }));
            Assert.That(copy.placement, Is.EqualTo(2));
            Assert.That(copy.shapes[0].renderer, Is.EqualTo("Body"));
            Assert.That(copy.shapes[0].name, Is.EqualTo("vrc.v_aa"));
        }
    }
}
