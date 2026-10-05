using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Net;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Gallery installations end to end, from a package the test exports (the download is replaced by a copy): the declared
    // prefab is imported and attached, recorded on its attachment and in the ledger, never placed twice, removed with
    // its files left for Cleanup; code and replaced files wait for the user's answer. The journal and ledger files of
    // the project are kept aside while the tests run.
    public sealed class GalleryInstallerTests
    {
        private Scene scene;
        private string folder, temp;
        private readonly List<GameObject> owned = new List<GameObject>();
        private readonly Dictionary<string, byte[]> saved = new Dictionary<string, byte[]>();
        private Func<GalleryJob, string, OrbitersTransfer.Progress, System.Threading.CancellationToken, Task<OrbitersTransfer.Result>> download;
        private Func<System.Threading.CancellationToken, Task<KnownCatalog>> known;
        private static readonly string[] Stores = { "gallery-jobs.json", "gallery-ledger.json" };

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            folder = "Assets/MyAvatarGalleryTest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            temp = Path.Combine(Path.GetTempPath(), "MyAvatarGallery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            foreach (string name in Stores)
            {
                string path = Path.Combine(LibraryStore.Folder, name);
                if (File.Exists(path)) { saved[name] = File.ReadAllBytes(path); File.Delete(path); }
            }
            GalleryJournal.ResetForTests();
            download = GalleryInstaller.Download;
            known = GalleryInstaller.KnownDependencies;
            GalleryInstaller.KnownDependencies = _ => Task.FromResult(new KnownCatalog());
        }

        [TearDown] public void TearDown()
        {
            GalleryInstaller.Download = download;
            GalleryInstaller.KnownDependencies = known;
            foreach (var go in owned) if (go) { Undo.ClearUndo(go); UnityEngine.Object.DestroyImmediate(go); }
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            if (folder.StartsWith("Assets/MyAvatarGalleryTest-", StringComparison.Ordinal) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            foreach (string name in Stores)
            {
                string path = Path.Combine(LibraryStore.Folder, name);
                if (saved.TryGetValue(name, out var bytes)) File.WriteAllBytes(path, bytes); else if (File.Exists(path)) File.Delete(path);
            }
            saved.Clear();
            GalleryJournal.ResetForTests();
            typeof(GalleryJournal).GetField("jobs", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, null);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        private MyAvatar Avatar()
        {
            var root = new GameObject("Avatar"); owned.Add(root);
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<MyAvatar>();
        }

        // A prefab with a visible mesh, exported to a package, then removed from the project so the gallery imports it.
        private (string package, string guid, string path) Package(string name, bool keepInProject = false, string code = null)
        {
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = name;
            UnityEngine.Object.DestroyImmediate(source.GetComponent<Collider>());
            string path = folder + "/" + name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(source, path);
            UnityEngine.Object.DestroyImmediate(source);
            var paths = new List<string> { path };
            if (code != null)
            {
                // Code Unity does not compile in the editor (a WebGL plugin): a .cs file would start a compilation the
                // installer must wait for, which never ends while tests run.
                string script = folder + "/" + name + "Setup.jslib";
                File.WriteAllText(script, code);
                AssetDatabase.ImportAsset(script);
                paths.Add(script);
            }
            string guid = AssetDatabase.AssetPathToGUID(path);
            string package = Path.Combine(temp, name + ".unitypackage");
            AssetDatabase.ExportPackage(paths.ToArray(), package, ExportPackageOptions.Default);
            if (!keepInProject) foreach (string file in paths) AssetDatabase.DeleteAsset(file);
            return (package, guid, path);
        }

        private GalleryAsset Asset(int id, string package, string guid, bool trusted = true)
        {
            string sha = UnityPackageFiles.FileHash(package);
            var variant = new GalleryVariant
            {
                id = id * 10, label = "Default", platforms = new List<string> { AvatarPlatform.Current }, sha256 = sha, sizeBytes = new FileInfo(package).Length,
                manifest = new GalleryManifest { defaultSetup = "default", setups = { new GallerySetup { key = "default", label = "Default", prefabs = { new GallerySetupPrefab { guid = guid, name = "Item" } } } } },
            };
            GalleryInstaller.Download = (job, path, progress, cancellation) => { File.Copy(package, path, true); return Task.FromResult(new OrbitersTransfer.Result { Success = true }); };
            return new GalleryAsset
            {
                id = id, name = "Gallery Item " + id, creator = new GalleryCreator { id = 99999, username = "Creator", trusted = trusted },
                access = new GalleryAccess { state = "free" }, fit = new GalleryFit { state = "compatible", release = new GalleryRelease { id = id, version = "1.0.0", scope = "public" }, variants = { variant } },
            };
        }

        private static IEnumerator Until(Func<bool> done, double seconds = 60)
        {
            double deadline = EditorApplication.timeSinceStartup + seconds;
            while (!done() && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(done(), Is.True, "Timed out.");
        }

        private static GalleryJob Job(string id) => GalleryJournal.All.First(j => j.id == id);

        [UnityTest] public IEnumerator InstallsTheDeclaredPrefabOnceAndRemovesIt()
        {
            var avatar = Avatar();
            var (package, guid, path) = Package("Hat");
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.Empty.Or.Not.EqualTo(path).Or.EqualTo(path));
            var asset = Asset(9101, package, guid);
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, asset.fit.variants[0], null);
            yield return Until(() => Job(job.id).Terminal);
            Assert.That(Job(job.id).stage, Is.EqualTo(GalleryJob.Complete), Job(job.id).error);

            var installed = GalleryInstaller.Installed(avatar);
            Assert.That(installed.Count, Is.EqualTo(1));
            Assert.That(installed[0].receipt.assetId, Is.EqualTo(9101));
            Assert.That(installed[0].receipt.installId, Is.EqualTo(job.id));
            Assert.That(installed[0].receipt.version, Is.EqualTo("1.0.0"));
            var entry = GalleryLedger.Entries.Single(e => e.installId == job.id);
            Assert.That(entry.files.Any(f => f.guid == guid), Is.True, "The imported prefab is the gallery's file.");
            Assert.That(entry.preexisting, Is.Empty);
            Assert.That(File.Exists(Path.Combine(GalleryApi.StagingFolder, job.id + ".unitypackage")), Is.False, "The staged download is removed once installed.");

            // Running the attach step again (a retry after a reload) finds what it placed: no second copy.
            var again = Job(job.id);
            again.stage = GalleryJob.Attach;
            yield return Until(() => { _ = GalleryInstaller.RunAsync(again); return true; });
            yield return Until(() => Job(job.id).Terminal);
            Assert.That(avatar.GetComponentsInChildren<OrbitersAttachment>(true).Length, Is.EqualTo(1));

            GalleryInstaller.Remove(avatar, job.id);
            Assert.That(GalleryInstaller.Installed(avatar), Is.Empty);
            Assert.That(GalleryLedger.Entries.Single(e => e.installId == job.id).removedAt, Is.GreaterThan(0));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)), Is.Not.Null, "Removal keeps the project's files.");
        }

        [UnityTest] public IEnumerator CodeFromAnUntrustedCreatorWaitsForTheUser()
        {
            var avatar = Avatar();
            var (package, guid, path) = Package("Gadget", code: "mergeInto(LibraryManager.library, {});");
            var asset = Asset(9102, package, guid, trusted: false);
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, asset.fit.variants[0], null);
            yield return Until(() => Job(job.id).Waiting || Job(job.id).Terminal);
            Assert.That(Job(job.id).waiting, Is.EqualTo("code"));
            Assert.That(Job(job.id).questionLines.Any(l => l.EndsWith("GadgetSetup.jslib", StringComparison.Ordinal)), Is.True);
            GalleryInstaller.Answer(Job(job.id), false);
            Assert.That(Job(job.id).stage, Is.EqualTo(GalleryJob.Cancelled));
            Assert.That(File.Exists(path), Is.False, "Nothing was imported.");
            Assert.That(avatar.GetComponentsInChildren<OrbitersAttachment>(true), Is.Empty);
        }

        [UnityTest] public IEnumerator ReplacedProjectFilesAreListedBeforeImporting()
        {
            var avatar = Avatar();
            var (package, guid, path) = Package("Scarf", keepInProject: true);
            // The project's copy changes after the export: importing would overwrite it.
            var prefab = PrefabUtility.LoadPrefabContents(path);
            prefab.transform.localScale = Vector3.one * 2f;
            PrefabUtility.SaveAsPrefabAsset(prefab, path);
            PrefabUtility.UnloadPrefabContents(prefab);
            string before = UnityPackageFiles.FileHash(path);
            var asset = Asset(9103, package, guid);
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, asset.fit.variants[0], null);
            yield return Until(() => Job(job.id).Waiting || Job(job.id).Terminal);
            Assert.That(Job(job.id).waiting, Is.EqualTo("conflicts"));
            Assert.That(Job(job.id).questionLines.Single(), Does.StartWith(path));
            GalleryInstaller.Answer(Job(job.id), false);
            Assert.That(UnityPackageFiles.FileHash(path), Is.EqualTo(before), "Cancel keeps the project's file.");
            Assert.That(avatar.GetComponentsInChildren<OrbitersAttachment>(true), Is.Empty);
        }

        [Test] public void CardStatesFollowTheJobThenTheAvatarThenAccess()
        {
            var asset = new GalleryAsset { access = new GalleryAccess { state = "purchase" }, fit = new GalleryFit { state = "compatible", release = new GalleryRelease { version = "1.2.0" }, variants = { new GalleryVariant() } } };
            Assert.That(GalleryUI.StateOf(asset, null, null, out _), Is.EqualTo(GalleryUI.State.Buy));
            asset.access.state = "included";
            Assert.That(GalleryUI.StateOf(asset, null, null, out _), Is.EqualTo(GalleryUI.State.Add));
            var receipt = new OrbitersAttachment.GalleryReceipt { assetId = 1, version = "1.1.0" };
            Assert.That(GalleryUI.StateOf(asset, receipt, null, out string note), Is.EqualTo(GalleryUI.State.Update));
            Assert.That(note, Is.EqualTo("1.1.0 → 1.2.0"));
            receipt.version = "1.2.0";
            Assert.That(GalleryUI.StateOf(asset, receipt, null, out _), Is.EqualTo(GalleryUI.State.Installed));
            Assert.That(GalleryUI.StateOf(asset, receipt, new GalleryJob { stage = GalleryJob.Import }, out _), Is.EqualTo(GalleryUI.State.Working));
            Assert.That(GalleryUI.StateOf(asset, null, new GalleryJob { stage = GalleryJob.Validate, waiting = "code" }, out _), Is.EqualTo(GalleryUI.State.Question));
            asset.fit = new GalleryFit { state = "platform", platforms = { "android" } };
            Assert.That(GalleryUI.StateOf(asset, null, null, out string why), Is.EqualTo(GalleryUI.State.Unavailable));
            Assert.That(why, Is.EqualTo("Quest only"));
            Assert.That(GalleryUI.CompareVersions("1.10.0", "1.9.2"), Is.GreaterThan(0));
            Assert.That(GalleryCreatorPage.NextVersion("1.4.9"), Is.EqualTo("1.4.10"));
        }
    }
}
