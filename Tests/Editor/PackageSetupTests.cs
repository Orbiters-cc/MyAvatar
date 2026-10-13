using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The one-click setups' downloads (GoGoLoco, face tracking) once accepted: every avatar asked for is set up when the
    // packages are in, also after a script reload; a download that never arrives is given up on and said to the section.
    // Nothing is downloaded: the test plays the packages arriving. Also: the scene objects MCP tools name, the drop's Undo
    // step, copies of textures, and why a ZIP was not opened.
    public sealed class PackageSetupTests
    {
        private Scene scene;
        private string key;
        private bool imported;
        private PackageSetup setup;
        private readonly List<string> finished = new List<string>();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

        [SetUp]
        public void Open()
        {
            scene = EditorSceneManager.NewPreviewScene();
            key = "Test" + Guid.NewGuid().ToString("N");
            imported = false;
            finished.Clear();
        }

        [TearDown]
        public void Close()
        {
            setup?.StopWaiting();
            setup = null;
            foreach (var value in owned) if (value) { Undo.ClearUndo(value); UnityEngine.Object.DestroyImmediate(value); }
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private MyAvatar Avatar(string name)
        {
            var root = new GameObject(name);
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<MyAvatar>();
        }

        private PackageSetup Setup() => new PackageSetup(key, "the test", "the test packages", () => imported, root => { finished.Add(root.name); return null; });

        [Test]
        public void EveryAvatarAskedForIsSetUpOnceThePackagesAreIn()
        {
            var a = Avatar("A"); var b = Avatar("B");
            setup = Setup();
            setup.Expect(a); setup.Expect(b);
            Assert.That(setup.Waiting(a) && setup.Waiting(b), Is.True, "a second avatar does not replace the first");

            setup.TryFinish(EditorApplication.timeSinceStartup);
            Assert.That(finished, Is.Empty, "not imported yet");
            imported = true;
            setup.TryFinish(EditorApplication.timeSinceStartup);

            CollectionAssert.AreEquivalent(new[] { "A", "B" }, finished);
            Assert.That(setup.Waiting(a), Is.False);
            StringAssert.Contains("is on A", setup.TakeNote(a));
            Assert.That(setup.TakeNote(a), Is.Null, "said once");
        }

        [Test]
        public void AfterAScriptReloadTheDownloadIsStillAwaited()
        {
            var a = Avatar("A");
            Setup().Expect(a);
            // A new instance, as after a reload: what was asked lives in the session.
            setup = Setup();
            Assert.That(setup.Waiting(a), Is.True);
            setup.Resume();
            imported = true;
            setup.TryFinish(EditorApplication.timeSinceStartup);
            CollectionAssert.AreEqual(new[] { "A" }, finished);
        }

        [Test]
        public void ADownloadThatNeverArrivesIsGivenUpOnAndSaid()
        {
            var a = Avatar("A");
            setup = Setup();
            setup.Expect(a);
            var told = new List<int>();
            void Told(int id) => told.Add(id);
            PackageSetup.Finished += Told;
            try { setup.TryFinish(EditorApplication.timeSinceStartup + PackageSetup.PendingTimeout + 5); }
            finally { PackageSetup.Finished -= Told; }

            Assert.That(setup.Waiting(a), Is.False);
            CollectionAssert.AreEqual(new[] { a.GetInstanceID() }, told, "the avatar's section hears of it");
            StringAssert.Contains("was not set up", setup.TakeNote(a));
            imported = true;
            setup.TryFinish(EditorApplication.timeSinceStartup);
            Assert.That(finished, Is.Empty, "a later install sets nothing up unasked");
        }

        [Test]
        public void ForgettingOneAvatarKeepsTheOthersWaiting()
        {
            var a = Avatar("A"); var b = Avatar("B");
            setup = Setup();
            setup.Expect(a); setup.Expect(b);
            setup.Forget(a);
            Assert.That(setup.Waiting(a), Is.False);
            Assert.That(setup.Waiting(b), Is.True);
        }

        // ---- MCP target names ----

        [Test]
        public void SceneObjectsAreNamedByIdOrPathWhateverTheirNames()
        {
            GameObject Make(string name, Transform parent = null)
            {
                var go = new GameObject(name);
                if (parent != null) go.transform.SetParent(parent, false);
                else SceneManager.MoveGameObjectToScene(go, scene);
                return go;
            }
            var scenes = new[] { scene };
            // A number no object answers to now, as a name.
            int free = 424242424;
            while (EditorUtility.InstanceIDToObject(free) != null) free++;
            string number = free.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var digits = Make(number);
            var colon = Make("Avatar:Quest");
            var twin = Make("Twin");
            Make("Child", twin.transform); Make("Child", twin.transform);

            Assert.That(SceneReference.Resolve(digits.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture), scenes), Is.EqualTo(digits));
            Assert.That(SceneReference.Resolve(" " + number + " ", scenes), Is.EqualTo(digits), "a number that is no object's ID is a name");
            Assert.That(SceneReference.Resolve("Avatar:Quest", scenes), Is.EqualTo(colon), "no scene is named Avatar: the colon is the name's");
            if (!string.IsNullOrEmpty(scene.name)) Assert.That(SceneReference.Resolve(scene.name + ":Avatar:Quest", scenes), Is.EqualTo(colon));
            Assert.Throws<ArgumentException>(() => SceneReference.Resolve("Twin/Child", scenes), "two objects answer: refused, not one picked");
            Assert.That(SceneReference.Resolve("Nothing", scenes), Is.Null);
        }

        // ---- The drop's Undo step ----

        private Material Owned(string name)
        {
            var material = new Material(Shader.Find("Standard")) { name = name };
            owned.Add(material);
            return material;
        }

        [Test]
        public void LaterWorkJoinsTheDropOnlyWhenNothingCameInBetween()
        {
            var drop = Owned("Drop"); var later = Owned("Later");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.RecordObject(drop, "Drop"); drop.color = Color.red;
            Undo.CollapseUndoOperations(group); Undo.IncrementCurrentGroup();
            var tail = UndoTail.After(group);

            tail.Join(() => { Undo.RecordObject(later, "Textures"); later.color = Color.green; });
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.That(drop.color, Is.EqualTo(Color.white), "one Undo takes the drop back");
            Assert.That(later.color, Is.EqualTo(Color.white), "with what joined it");
        }

        [Test]
        public void AnEditMadeMeanwhileNeverJoinsTheDrop()
        {
            var drop = Owned("Drop"); var user = Owned("User"); var later = Owned("Later");
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.RecordObject(drop, "Drop"); drop.color = Color.red;
            Undo.CollapseUndoOperations(group); Undo.IncrementCurrentGroup();
            var tail = UndoTail.After(group);
            // The user edits something while the textures are read (Unity starts a new group for it).
            Undo.IncrementCurrentGroup();
            Undo.RecordObject(user, "User"); user.color = Color.blue;

            tail.Join(() => { Undo.RecordObject(later, "Textures"); later.color = Color.green; });
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.That(later.color, Is.EqualTo(Color.white), "the textures are their own step");
            Assert.That(user.color, Is.EqualTo(Color.blue), "the user's edit is not taken back with them");
            Assert.That(drop.color, Is.EqualTo(Color.red));
            Undo.PerformUndo();
            Assert.That(user.color, Is.EqualTo(Color.white));
            Assert.That(drop.color, Is.EqualTo(Color.red), "nor folded into the drop");
        }

        // ---- Copies of textures, ZIPs ----

        [Test]
        public void ATextureCopyKeepsItsImportSettingsButHowItIsRead()
        {
            const string original = "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nTextureImporter:\n  serializedVersion: 12\n  mipmaps:\n" +
                                    "    mipMapMode: 0\n    sRGBTexture: 1\n  isReadable: 0\n  maxTextureSize: 512\n  textureSettings:\n    filterMode: 0\n    wrapU: 1\n" +
                                    "  alphaIsTransparency: 1\n  textureType: 0\n  platformSettings:\n  - serializedVersion: 3\n    buildTarget: Android\n    maxTextureSize: 256\n";
            string copy = TextureImport.CopyMeta(original, normal: true, srgb: false);
            StringAssert.DoesNotContain("0123456789abcdef0123456789abcdef", copy, "its own GUID");
            StringAssert.Contains("\n  textureType: 1\n", copy);
            StringAssert.Contains("\n    sRGBTexture: 0\n", copy);
            foreach (string kept in new[] { "maxTextureSize: 512", "filterMode: 0", "wrapU: 1", "alphaIsTransparency: 1", "buildTarget: Android", "maxTextureSize: 256" })
                StringAssert.Contains(kept, copy);
            Assert.That(TextureImport.CopyMeta("fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nIHVImageFormatImporter:\n", false, true), Is.Null, "another importer: the plain settings");
        }

        [Test]
        public void AZipTooDeepForWindowsSaysSoInsteadOfUnreadable()
        {
            StringAssert.Contains("folder path is too long", AccessoryImport.ZipProblem(new PathTooLongException("The project's folder path is too long for Windows")));
            StringAssert.Contains("could not be extracted", AccessoryImport.ZipProblem(new IOException("The disk is full")));
            Assert.That(AccessoryImport.ZipProblem(new NotSupportedException()), Is.EqualTo("It is not a readable ZIP."));
            StringAssert.Contains("folder path is too long", AccessoryImport.PackageProblem(new PathTooLongException("The project's folder path is too long for Windows")));
            Assert.That(AccessoryImport.PackageProblem(new EndOfStreamException()), Is.EqualTo("It is not a readable Unity package."));
        }
    }
}
