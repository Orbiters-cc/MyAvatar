using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Painter;
using Orbiters.Toolkit.Editor.LiveLink;
using Orbiters.Toolkit.Editor.Textures;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The Substance 3D Painter link: where a texture set's maps go on a material, which material a texture set paints, what
    // shows live and what saving in Painter applies, the projects linked and what they are sent. The link's folder is a
    // temporary one; no frame reaches the editor's own listener.
    public sealed class PainterLinkTests
    {
        private Scene scene;
        private readonly List<Object> owned = new List<Object>();
        private readonly List<string> folders = new List<string>();
        private string temp, linkFolder;

        // What a test's links send, instead of a listener.
        private sealed class Transport : IPainterLinkTransport
        {
            public readonly List<(int Connection, PainterLinkProtocol.FrameType Type, byte[] Payload)> Sent = new List<(int, PainterLinkProtocol.FrameType, byte[])>();
            public readonly List<int> Closed = new List<int>();
            public bool Send(int connection, byte type, byte[] payload) { Sent.Add((connection, (PainterLinkProtocol.FrameType)type, payload)); return true; }
            public void Disconnect(int connection) => Closed.Add(connection);

            public List<T> Of<T>(int connection, PainterLinkProtocol.FrameType type) =>
                Sent.Where(f => f.Connection == connection && f.Type == type).Select(f => PainterLinkProtocol.Read<T>(f.Payload)).ToList();
        }

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            temp = Path.Combine(Path.GetTempPath(), "OrbitersPainterTest-" + Guid.NewGuid().ToString("N"));
            linkFolder = Path.Combine(temp, "PainterLink");
            Directory.CreateDirectory(linkFolder);
            PainterLinkService.LinkFolderOverride = linkFolder;
        }

        [TearDown] public void TearDown()
        {
            PainterLinkService.LinkFolderOverride = null;
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

        // An image of a save, where Painter exports it (the link's commit folder).
        private string Png(string name, Color32 color, int size = 4)
        {
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
            image.SetPixels32(Enumerable.Repeat(color, size * size).ToArray());
            string folder = Path.Combine(linkFolder, "commit", "1234", "20261020-120000");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, name + ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            return path.Replace('\\', '/');
        }

        // A 1×1 TGA as Painter streams it (uncompressed, 24 bits).
        private static TgaImage Tga(byte r, byte g, byte b) =>
            TgaImage.Read(new byte[] { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 24, 0, b, g, r });

        // The first pixel of what a live image shows, read back from the GPU.
        private static Color Pixel(Texture shown)
        {
            var target = shown as RenderTexture;
            Assert.That(target, Is.Not.Null, "Live images are shown from the GPU.");
            var previous = RenderTexture.active;
            var read = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, !target.sRGB);
            try
            {
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                read.Apply(false);
                return read.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(read);
            }
        }

        private static Texture Shown(Renderer renderer, string property, int slot = 0)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block, slot);
            return block.GetTexture(property);
        }

        private static LiveLinkFrame Hello(int connection, string projectPath, string projectName = "Test.spp") => new LiveLinkFrame
        {
            Connection = connection, Type = (byte)PainterLinkProtocol.FrameType.Hello,
            Decoded = new PainterLinkProtocol.Hello { protocolVersion = 1, plugin = "1.4.0", avatarId = "test", projectName = projectName, projectPath = projectPath },
        };

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
            using (var preview = new PainterLivePreview())
            {
                preview.Show(avatar.transform, "Skin", "basecolor", 1, Tga(30, 20, 10), material, targets);
                var shown = (Color32)Pixel(Shown(renderer, "_MainTex"));
                Assert.That(Math.Abs(shown.r - 30) <= 1 && Math.Abs(shown.g - 20) <= 1 && Math.Abs(shown.b - 10) <= 1, Is.True, "Shown as streamed: " + shown);
                Assert.That(material.GetTexture("_MainTex"), Is.Null, "The material itself is untouched.");
                Assert.That(preview.Stale("Skin", "basecolor", 1), Is.True, "An image as old as the one shown is dropped.");
                Assert.That(preview.Live, Is.EqualTo(new[] { "Skin" }));
                Assert.That(preview.ShownOn("Skin"), Is.SameAs(material));
                preview.Clear("Skin");
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block, 0);
                Assert.That(block.isEmpty, Is.True);
            }
        }

        // Metallic and roughness are packed the way the material reads them (Standard: metallic in R, smoothness in A), the
        // channels Painter doesn't stream keeping the material's own values. 0.9.11 showed (0, 0, 0, 1) whatever was painted.
        [Test]
        public void LiveMetallicAndRoughnessArePackedAsTheMaterialReadsThem()
        {
            var avatar = Avatar();
            var material = Standard("Skin");
            var own = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false, true));
            own.SetPixels(Enumerable.Repeat(new Color(.1f, .2f, .3f, .4f), 4).ToArray());
            own.Apply();
            material.SetTexture("_MetallicGlossMap", own);
            var renderer = Part(avatar, "Body", material);
            var targets = PainterTargets.For(material, TextureMatching.Slots(avatar));
            using (var preview = new PainterLivePreview())
            {
                preview.Show(avatar.transform, "Skin", "metallic", 1, Tga(200, 200, 200), material, targets);
                var packed = Pixel(Shown(renderer, "_MetallicGlossMap"));
                Assert.That(packed.r, Is.EqualTo(200 / 255f).Within(.02f), "Metallic as painted.");
                Assert.That(packed.g, Is.EqualTo(.2f).Within(.02f), "The material's own green.");
                Assert.That(packed.b, Is.EqualTo(.3f).Within(.02f));
                Assert.That(packed.a, Is.EqualTo(.4f).Within(.02f), "Smoothness not streamed yet: the material's.");
                preview.Show(avatar.transform, "Skin", "roughness", 2, Tga(64, 64, 64), material, targets);
                packed = Pixel(Shown(renderer, "_MetallicGlossMap"));
                Assert.That(packed.r, Is.EqualTo(200 / 255f).Within(.02f));
                Assert.That(packed.a, Is.EqualTo(1f - 64 / 255f).Within(.02f), "Smoothness is one minus the roughness painted.");
            }
        }

        // A texture set taken off gives the slot back as other tools left it, with the images of the sets still shown on it.
        [Test]
        public void ClearingATextureSetKeepsWhatOthersPutInTheSlot()
        {
            var avatar = Avatar();
            var material = Standard("Skin");
            var renderer = Part(avatar, "Body", material);
            var block = new MaterialPropertyBlock();
            block.SetFloat("_Cutoff", .25f);
            renderer.SetPropertyBlock(block, 0);
            var targets = PainterTargets.For(material, TextureMatching.Slots(avatar));
            using (var preview = new PainterLivePreview())
            {
                preview.Show(avatar.transform, "Front", "basecolor", 1, Tga(255, 0, 0), material, targets);
                preview.Show(avatar.transform, "Back", "normal", 1, Tga(128, 128, 255), material, targets);
                var normal = Shown(renderer, "_BumpMap");
                preview.Clear("Front");
                renderer.GetPropertyBlock(block, 0);
                Assert.That(block.GetFloat("_Cutoff"), Is.EqualTo(.25f), "Another tool's value stays.");
                Assert.That(block.GetTexture("_MainTex"), Is.Null, "The set taken off is gone.");
                Assert.That(block.GetTexture("_BumpMap"), Is.SameAs(normal), "The other set still shows.");
                preview.Clear();
                renderer.GetPropertyBlock(block, 0);
                Assert.That(block.GetTexture("_BumpMap"), Is.Null);
                Assert.That(block.GetFloat("_Cutoff"), Is.EqualTo(.25f));
            }
        }

        // Mapped to another material, a texture set's live images leave the first one.
        [Test]
        public void ATextureSetPaintingAnotherMaterialLeavesTheFirstAsItWas()
        {
            var avatar = Avatar();
            var skin = Standard("Skin");
            var clothes = Standard("Clothes");
            var body = Part(avatar, "Body", skin);
            var outfit = Part(avatar, "Outfit", clothes);
            var slots = TextureMatching.Slots(avatar);
            using (var preview = new PainterLivePreview())
            {
                preview.Show(avatar.transform, "Set", "basecolor", 1, Tga(255, 0, 0), skin, PainterTargets.For(skin, slots));
                preview.Show(avatar.transform, "Set", "basecolor", 2, Tga(0, 255, 0), clothes, PainterTargets.For(clothes, slots));
                Assert.That(Shown(body, "_MainTex"), Is.Null);
                Assert.That(Shown(outfit, "_MainTex"), Is.Not.Null);
                Assert.That(preview.ShownOn("Set"), Is.SameAs(clothes));
            }
        }

        // Saving in Painter applies its maps as a texture drop does; the next save edits the same material copy, in new files:
        // Undo of a save shows the one before it.
        [Test]
        public void SavedMapsAreAppliedAndTheNextSaveKeepsTheOneBefore()
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
            var updated = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/Painter/Skin_BaseColor 2.png");
            Assert.That(updated, Is.Not.Null, "Each save in a file of its own.");
            Assert.That(renderer.sharedMaterial.GetTexture("_MainTex") == updated, Is.True);
            int Green(string file)
            {
                var pixels = new Texture2D(1, 1);
                try { pixels.LoadImage(File.ReadAllBytes(file)); return ((Color32)pixels.GetPixel(0, 0)).g; }
                finally { Object.DestroyImmediate(pixels); }
            }
            Assert.That(Green(folder + "/Painter/Skin_BaseColor 2.png"), Is.EqualTo(200), "The newest save.");
            Assert.That(Green(folder + "/Painter/Skin_BaseColor.png"), Is.EqualTo(10), "The save before it stays, for Undo.");
            Assert.That(Directory.Exists(Path.GetDirectoryName(first[0].path)), Is.True, "The export stays until the link discards it.");
            PainterCommit.Discard(second);
            Assert.That(Directory.Exists(Path.GetDirectoryName(second[0].path)), Is.False, "An export applied is deleted.");
        }

        // Only maps Painter exported in the link's folder are read; two texture sets on one material give it the first one's.
        [Test]
        public void SavedMapsAreReadFromTheExportFolderOnlyAndOncePerSlot()
        {
            var avatar = Avatar();
            var skin = Standard("Skin");
            var body = Part(avatar, "Body", skin);
            var choice = PainterMapping.Choices(avatar).First(c => c.Material == skin);
            PainterMapping.Set(avatar, "Front", choice);
            PainterMapping.Set(avatar, "Back", choice);
            (Material, PainterTargets) Resolve(string set)
            {
                var current = PainterMapping.Material(avatar, set);
                return (current, PainterTargets.For(current, TextureMatching.Slots(avatar)));
            }
            string outside = Path.Combine(temp, "elsewhere.png");
            File.Copy(Png("outside", new Color32(1, 2, 3, 255)), outside);
            Assert.That(PainterCommit.Apply(avatar, new List<PainterLinkProtocol.CommitMap>
            {
                new PainterLinkProtocol.CommitMap { textureSet = "Front", map = "basecolor", path = outside.Replace('\\', '/') },
            }, Resolve), Is.EqualTo(0), "A file outside the link's exports is never copied in.");
            int applied = -1;
            Assert.That(() => applied = PainterCommit.Apply(avatar, new List<PainterLinkProtocol.CommitMap>
            {
                new PainterLinkProtocol.CommitMap { textureSet = "Front", map = "basecolor", path = Png("front", new Color32(200, 0, 0, 255)) },
                new PainterLinkProtocol.CommitMap { textureSet = "Back", map = "basecolor", path = Png("back", new Color32(0, 0, 200, 255)) },
            }, Resolve), Throws.Nothing);
            if (!string.IsNullOrEmpty(avatar.painter.textureFolder)) folders.Add(avatar.painter.textureFolder);
            Assert.That(applied, Is.EqualTo(1));
            Assert.That(AssetDatabase.GetAssetPath(body.sharedMaterial.GetTexture("_MainTex")), Does.EndWith("/Front_BaseColor.png"));
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
            // Straight to the Painter link: the editor's other listeners (MCB's Blender link...) hear nothing of the test.
            PainterMeshUpdate.Applied(made.transform);
            PainterMeshUpdate.Applied(linked.transform);
            Assert.That(made.painter.meshOutdated, Is.True);
            Assert.That(linked.painter.meshOutdated, Is.False);
            Assert.That(PainterMeshUpdate.Busy(made), Is.False);
            // A project made before mesh updates: flagged for the card's note, never posed again.
            var older = Avatar();
            older.painter.projectPath = "C:/Painter/Older.spp";
            PainterMeshUpdate.Applied(older.transform);
            Assert.That(older.painter.meshOutdated, Is.True);
            Assert.That(PainterMeshUpdate.Busy(older), Is.False);
        }

        // A hand-made project linked to an avatar that has the project Open in Substance made: the avatar keeps that one, and
        // the hand-made one never gets the avatar's posed mesh (0.9.11 replaced its mesh). The made one, linked, does.
        [Test]
        public void OnlyTheProjectOpenInSubstanceMadeFollowsTheAvatarsMesh()
        {
            var avatar = Avatar();
            string made = Path.Combine(temp, "Made.spp");
            File.WriteAllText(made, "");
            avatar.painter.posed = true;
            avatar.painter.projectPath = PainterLinkService.Portable(made);
            avatar.painter.meshOutdated = true;
            var transport = new Transport();
            var links = new PainterLinkService.Links(transport, _ => avatar);

            links.OnFrame(Hello(1, Path.Combine(temp, "Hand made.spp").Replace('\\', '/'), "Hand made.spp"));
            var session = links.For(avatar);
            Assert.That(session, Is.Not.Null);
            Assert.That(session.MadeProject, Is.False);
            Assert.That(transport.Of<PainterLinkProtocol.Welcome>(1, PainterLinkProtocol.FrameType.Welcome).Count, Is.EqualTo(1));
            Assert.That(PainterLinkService.SamePath(avatar.painter.projectPath, made), Is.True, "The made project stays the avatar's.");
            Assert.That(avatar.painter.posed, Is.True);
            PainterMeshUpdate.Start(session);
            Assert.That(transport.Sent.Any(f => f.Type == PainterLinkProtocol.FrameType.Request), Is.False, "No mesh for a project Open in Substance didn't make.");
            Assert.That(PainterMeshUpdate.Busy(avatar), Is.False);

            // The made project links (its path as Painter writes it): it is the one that follows, and takes the avatar over.
            links.OnFrame(Hello(2, made.Replace('\\', '/'), "Made.spp"));
            Assert.That(links.For(avatar).Connection, Is.EqualTo(2));
            Assert.That(links.For(avatar).MadeProject, Is.True);
            var told = transport.Of<PainterLinkProtocol.Status>(1, PainterLinkProtocol.FrameType.Status).Single();
            Assert.That(told.closing && told.takenOver, Is.True, "The older project stops linking on its own (no ping-pong).");
            Assert.That(told.message, Does.StartWith("Made linked to"));
        }

        // A project linked from Painter to an avatar without one made by Open in Substance is the avatar's project (to open it
        // again from the card), kept from the home folder.
        [Test]
        public void AProjectLinkedFromPainterIsKeptWhenTheAvatarHasNone()
        {
            var avatar = Avatar();
            var links = new PainterLinkService.Links(new Transport(), _ => avatar);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(home, "Documents", "Orbiters", "Painter", "Test", "Hand made.spp").Replace('\\', '/');
            links.OnFrame(Hello(1, path));
            Assert.That(avatar.painter.projectPath, Is.EqualTo("~/Documents/Orbiters/Painter/Test/Hand made.spp"), "No user name in the scene.");
            Assert.That(PainterLinkService.SamePath(PainterLinkService.ProjectPath(avatar), path), Is.True);
            Assert.That(avatar.painter.posed, Is.False);
            Assert.That(PainterLinkService.Portable("D:/Elsewhere/A.spp"), Is.EqualTo("D:/Elsewhere/A.spp"));
        }

        // The avatar's scene closed (or opened again): the link ends, telling Painter why, so it links again once the avatar is
        // back instead of sending saves nobody applies. It gets a moment to close it itself.
        [Test]
        public void ALinkEndsWhenItsAvatarIsGone()
        {
            var avatar = Avatar();
            var transport = new Transport();
            var links = new PainterLinkService.Links(transport, _ => avatar);
            links.OnFrame(Hello(3, null));
            Assert.That(links.For(avatar), Is.Not.Null);
            Object.DestroyImmediate(avatar.gameObject);
            links.Update(10);
            Assert.That(links.All, Is.Empty);
            var told = transport.Of<PainterLinkProtocol.Status>(3, PainterLinkProtocol.FrameType.Status).Single();
            Assert.That(told.closing, Is.True);
            Assert.That(told.takenOver, Is.False, "It links again on its own.");
            Assert.That(transport.Closed, Is.Empty);
            links.OnFrame(new LiveLinkFrame { Connection = 3, Type = (byte)PainterLinkProtocol.FrameType.Committing });
            links.Update(10 + PainterLinkService.Links.ClosingSeconds + .1);
            Assert.That(transport.Closed, Is.EqualTo(new[] { 3 }));
            // A project of an avatar not open in Unity is told so the same way.
            links.OnFrame(Hello(4, null));
            Assert.That(transport.Of<PainterLinkProtocol.Status>(4, PainterLinkProtocol.FrameType.Status).Single().closing, Is.True);
        }

        // What Painter sends is checked on the connection's thread: JSON that doesn't hold together, or is null, closes it;
        // missing lists are empty.
        [Test]
        public void PayloadsThatDoNotHoldTogetherCloseTheConnection()
        {
            bool Decode(PainterLinkProtocol.FrameType type, string json, out object decoded)
            {
                var frame = new LiveLinkFrame { Type = (byte)type, Payload = json == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json) };
                bool accepted = PainterLinkService.Decode(frame);
                decoded = frame.Decoded;
                return accepted;
            }
            Assert.That(Decode(PainterLinkProtocol.FrameType.Hello, "null", out _), Is.False);
            Assert.That(Decode(PainterLinkProtocol.FrameType.Hello, "{", out _), Is.False);
            Assert.That(Decode(PainterLinkProtocol.FrameType.Commit, null, out _), Is.False, "An empty payload is no commit.");
            Assert.That(Decode(PainterLinkProtocol.FrameType.Texture, "{\"textureSet\":\"Skin\"}", out _), Is.False, "A texture without its channel and file.");
            Assert.That(Decode(PainterLinkProtocol.FrameType.Commit, "{\"maps\":null}", out var commit), Is.True);
            Assert.That(((PainterLinkProtocol.Commit)commit).maps, Is.Empty);
            Assert.That(Decode(PainterLinkProtocol.FrameType.Structure, "{\"textureSets\":[null,{\"name\":\"Skin\",\"channels\":null},{\"name\":\"\"}]}", out var structure), Is.True);
            var sets = ((PainterLinkProtocol.Structure)structure).textureSets;
            Assert.That(sets.Select(s => s.name), Is.EqualTo(new[] { "Skin" }));
            Assert.That(sets[0].channels, Is.Empty);
            Assert.That(Decode(PainterLinkProtocol.FrameType.Committing, null, out _), Is.True, "A frame without a payload.");
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
            var written = new List<string>
            {
                PainterProjectExport.WriteMeshUpdate(suit, Parts()),
                PainterProjectExport.WriteMeshUpdate(body, Parts()),
            };
            // A later second: another file name.
            System.Threading.Thread.Sleep(1100);
            written.Add(PainterProjectExport.WriteMeshUpdate(body, Parts()));
            Assert.That(written.All(w => PainterLinkService.Inside(w, linkFolder)), Is.True, "In the test's link folder.");
            Assert.That(File.Exists(written[0]), Is.True, "The other avatar's mesh stays.");
            Assert.That(File.Exists(written[1]), Is.False, "The avatar's earlier mesh goes.");
            Assert.That(File.Exists(written[2]), Is.True);
            Assert.That(File.ReadAllText(written[2]), Does.Contain("usemtl Skin"));
        }

        // A mesh made again for a project keeps the names its texture sets have: two materials of one name ("Body" and
        // "Body_2" in the project) keep theirs whatever order the meshes come in, and a new material takes a name no set has.
        [Test]
        public void MeshUpdatesKeepTheProjectsTextureSetNames()
        {
            var avatar = Avatar();
            var first = Standard("Body");
            var second = Standard("Body");
            var third = Standard("Body");
            var a = Part(avatar, "A", first);
            var b = Part(avatar, "B", second);
            var c = Part(avatar, "C", third);
            var mesh = Own(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx")));
            var parts = new[] { a, b, c }.Select(r => new PainterPose.Part { Name = r.name, Mesh = mesh, ToWorld = Matrix4x4.identity, Materials = new[] { r.sharedMaterial }, Source = r }).ToList();
            Assert.That(PainterProjectExport.Sets(avatar, parts).Values.Select(s => s.Name), Is.EqualTo(new[] { "Body", "Body_2", "Body_3" }), "A new project names them in order.");
            // The project made them the other way round, and doesn't know the third.
            var project = new List<PainterSet>
            {
                new PainterSet { textureSet = "Body", renderer = "B", slot = 0 },
                new PainterSet { textureSet = "Body_2", renderer = "A", slot = 0 },
            };
            var sets = PainterProjectExport.Sets(avatar, parts, project);
            Assert.That(sets[first].Name, Is.EqualTo("Body_2"));
            Assert.That(sets[second].Name, Is.EqualTo("Body"));
            Assert.That(sets[third].Name, Is.EqualTo("Body_3"));
            // The project's set painting the third material's slot (of its name) keeps its name there.
            project[0].renderer = "C";
            Assert.That(PainterProjectExport.Sets(avatar, parts, project)[third].Name, Is.EqualTo("Body"));
        }

        // What editor tools show on the avatar (the bones' X-ray, weight paint: hidden objects never saved) is never baked
        // for Painter.
        [Test]
        public void EditorHelpersAreNotBakedForPainter()
        {
            var root = Own(new GameObject("Copy"));
            SceneManager.MoveGameObjectToScene(root, scene);
            var cube = Own(Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx")));
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = cube;
            body.AddComponent<MeshRenderer>().sharedMaterial = Standard("Skin");
            var gizmo = new GameObject("Bones") { hideFlags = HideFlags.HideAndDontSave };
            gizmo.transform.SetParent(root.transform, false);
            gizmo.AddComponent<SkinnedMeshRenderer>().sharedMesh = cube;
            var parts = PainterPose.Bake(root, Matrix4x4.identity);
            try { Assert.That(parts.Select(p => p.Name), Is.EqualTo(new[] { "Body" })); }
            finally { PainterPose.Release(parts); }
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

        // The plugin is compared by the version it declares (line ends and copies aside): a newer one installed by another
        // project stays.
        [Test]
        public void PluginVersionsAreReadFromTheFiles()
        {
            string Plugin(string name, string text)
            {
                string path = Path.Combine(temp, name);
                File.WriteAllText(path, text);
                return path;
            }
            var cache = default((string, Version));
            Assert.That(PainterApp.VersionOf(Plugin("a.py", "import os\r\nPLUGIN_VERSION = \"1.4.0\"\r\n"), ref cache), Is.EqualTo(new Version(1, 4, 0)));
            cache = default;
            Assert.That(PainterApp.VersionOf(Plugin("b.py", "PLUGIN_VERSION = \"1.10.2\"\n"), ref cache), Is.GreaterThan(new Version(1, 4, 0)));
            cache = default;
            Assert.That(PainterApp.VersionOf(Plugin("c.py", "print('no version')\n"), ref cache), Is.Null);
            cache = default;
            Assert.That(PainterApp.VersionOf(Path.Combine(temp, "missing.py"), ref cache), Is.Null);
            Assert.That(PainterApp.BundledPlugin, Is.Not.Null, "The package's plugin declares its version.");
        }

        // The link's folder keeps what someone still needs: running Painters' live images, recent exports and project
        // sources, the editors' endpoint files.
        [Test]
        public void TheLinksFolderIsClearedOfWhatNoOneNeeds()
        {
            var now = DateTime.UtcNow;
            string Folder(string relative, double hoursOld)
            {
                string path = Path.Combine(linkFolder, relative);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "file"), "");
                Directory.SetLastWriteTimeUtc(path, now.AddHours(-hoursOld));
                return path;
            }
            string File_(string relative, double hoursOld)
            {
                string path = Path.Combine(linkFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "");
                File.SetLastWriteTimeUtc(path, now.AddHours(-hoursOld));
                return path;
            }
            var gone = new[]
            {
                Folder("commit/7/old", 2), Folder("commit/8/old", 2), Folder("live/99", 0), Folder("live/notapid", 0), Folder("projects/A-old", 24 * 8),
                File_("meshes/old.obj", 24 * 8), File_("jobs/x.json.taken", 2), File_("unity-99.json", 0), File_("painter-99.json", 0), File_("unity-1.json.tmp", 2),
            };
            var kept = new[]
            {
                Folder("commit/7/new", 0), Folder("live/42", 0), Folder("projects/B-new", 1), File_("meshes/new.obj", 1),
                File_("unity-1.json", 0), File_("unity-42.json", 0), File_("painter-42.json", 0),
            };
            Directory.SetLastWriteTimeUtc(Path.Combine(linkFolder, "commit", "7"), now);
            PainterLinkCleanup.Run(linkFolder, ownPid: 1, now, alive: pid => pid == 42);
            Assert.That(gone.Where(p => File.Exists(p) || Directory.Exists(p)), Is.Empty);
            Assert.That(kept.Where(p => !File.Exists(p) && !Directory.Exists(p)), Is.Empty);
            Assert.That(Directory.Exists(Path.Combine(linkFolder, "commit", "8")), Is.False, "An export folder left empty goes.");
        }
    }
}
