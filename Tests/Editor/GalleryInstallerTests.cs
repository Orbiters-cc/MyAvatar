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
        private Func<GalleryJob, System.Threading.CancellationToken, Task<bool?>> trustedNow;
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
            trustedNow = GalleryInstaller.CreatorTrustedNow;
            GalleryInstaller.KnownDependencies = _ => Task.FromResult(new KnownCatalog());
            GalleryInstaller.CreatorTrustedNow = (job, cancellation) => Task.FromResult<bool?>(true);
        }

        [TearDown] public void TearDown()
        {
            GalleryInstaller.Download = download;
            GalleryInstaller.KnownDependencies = known;
            GalleryInstaller.CreatorTrustedNow = trustedNow;
            LibraryStore.Override = null;
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
            // Its creator says it is not worn on the body: the attachment never gets a fit question.
            asset.fit.variants[0].manifest.setups[0].prefabs[0].attach = new GalleryAttach { refit = false };
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, asset.fit.variants[0], null);
            yield return Until(() => Job(job.id).Terminal);
            Assert.That(Job(job.id).stage, Is.EqualTo(GalleryJob.Complete), Job(job.id).error);
            Assert.That(avatar.GetComponentInChildren<OrbitersAttachment>(true).neverRefit, Is.True, "Never refit, as its gallery setup says.");

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

        [Test] public void RetryImportsAgainUntilTheImportFinished()
        {
            // Jobs of an avatar that is not open: Retry picks the step, the run then waits for the avatar.
            GalleryJob Failed(int assetId, bool imported, int placed = 0) => new GalleryJob
            {
                id = Guid.NewGuid().ToString("N"), avatar = "not-an-avatar", avatarName = "Avatar", assetId = assetId, stage = GalleryJob.Failed,
                importGuids = new List<string> { "0123456789abcdef0123456789abcdef" }, imported = imported, placed = Enumerable.Repeat("placed", placed).ToList(),
            };
            var previewed = Failed(9201, imported: false);
            GalleryInstaller.Retry(previewed);
            Assert.That(previewed.stage, Is.EqualTo(GalleryJob.Download), "Known files to import are not an import.");
            var test = Failed(0, imported: false);
            GalleryInstaller.Retry(test);
            Assert.That(test.stage, Is.EqualTo(GalleryJob.Validate), "A creator's test starts again from its built package.");
            var imported = Failed(9202, imported: true);
            GalleryInstaller.Retry(imported);
            Assert.That(imported.stage, Is.EqualTo(GalleryJob.Attach));
            // Placed before: Attach finds what is on the avatar and places only what is missing.
            var placed = Failed(9203, imported: true, placed: 1);
            GalleryInstaller.Retry(placed);
            Assert.That(placed.stage, Is.EqualTo(GalleryJob.Attach));
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

        // An update's new copy takes the older copy's place: the bone it was moved to, where, hidden, its card's name;
        // copies are paired by their prefab, whatever their order.
        [Test] public void AnUpdateKeepsTheBoneThePlaceTheVisibilityAndTheName()
        {
            var avatar = Avatar();
            var head = new GameObject("Head").transform; head.SetParent(avatar.transform, false);
            var chest = new GameObject("Chest").transform; chest.SetParent(avatar.transform, false);
            OrbitersAttachment Copy(string name, string source, Transform bone)
            {
                var go = new GameObject(name); go.transform.SetParent(avatar.transform, false);
                var a = go.AddComponent<OrbitersAttachment>();
                a.mode = OrbitersAttachment.AttachMode.Parent; a.parent = bone; a.source = source;
                return a;
            }
            var oldHat = Copy("Hat", "aaa", head);
            var oldPin = Copy("Pin", "bbb", chest);
            oldHat.transform.localPosition = new Vector3(0.1f, 1.7f, 0.05f);
            oldHat.transform.localRotation = Quaternion.Euler(0, 30, 0);
            oldHat.transform.localScale = Vector3.one * 1.2f;
            oldHat.displayName = "Party hat"; oldHat.displayNameFor = "Hat";
            oldPin.gameObject.SetActive(false);
            // The update's copies come in another order, the hat back on the prefab's own bone.
            var newPin = Copy("Pin", "bbb", chest);
            var newHat = Copy("Hat", "aaa", chest);

            var pairs = GalleryInstaller.Pairs(new List<OrbitersAttachment> { oldHat, oldPin }, new List<OrbitersAttachment> { newPin, newHat });
            Assert.That(pairs.Select(p => (p.old, p.now)), Is.EqualTo(new[] { (oldHat, newHat), (oldPin, newPin) }));
            foreach (var (old, now) in pairs) GalleryInstaller.CarryPlacement(old, now);

            Assert.That(newHat.parent, Is.EqualTo(head), "the bone it was moved to");
            Assert.That(Vector3.Distance(newHat.transform.localPosition, oldHat.transform.localPosition), Is.LessThan(1e-5f), "where it was");
            Assert.That(Quaternion.Angle(newHat.transform.localRotation, oldHat.transform.localRotation), Is.LessThan(0.01f));
            Assert.That(newHat.transform.localScale.x, Is.EqualTo(1.2f).Within(1e-5f));
            Assert.That(newHat.DisplayName, Is.EqualTo("Party hat"));
            Assert.That(newPin.gameObject.activeSelf, Is.False, "hidden as it was");
            Assert.That(newHat.gameObject.activeSelf, Is.True);
        }

        // A jacket with item settings (a switch, a choice of objects, of materials and of a blendshape) in a folder of its
        // own, as each version is built, with a texture every version shares (one GUID); exported, then removed from the
        // project so the gallery imports it.
        private const string JacketSettings = @"{
    ""schema"": 1,
    ""settings"": [
        { ""label"": ""Hood"", ""type"": ""toggle"", ""on"": { ""show"": [""Hood""] } },
        { ""label"": ""Colour"", ""type"": ""choice"", ""choices"": [
            { ""label"": ""Red"", ""materials"": [{ ""renderer"": ""Body"", ""slot"": 0, ""material"": ""Red.mat"" }] },
            { ""label"": ""Blue"", ""materials"": [{ ""renderer"": ""Body"", ""slot"": 0, ""material"": ""Blue.mat"" }] }
        ] },
        { ""label"": ""Fit"", ""type"": ""choice"", ""choices"": [
            { ""label"": ""Regular"", ""blendshapes"": [{ ""renderer"": ""Skin"", ""name"": ""Slim"", ""weight"": 0 }] },
            { ""label"": ""Slim"", ""blendshapes"": [{ ""renderer"": ""Skin"", ""name"": ""Slim"", ""weight"": 100 }] }
        ] }
    ]
}";

        private (string package, string guid, string folder) Jacket(string version)
        {
            string shared = folder + "/Shared.png";
            if (!File.Exists(shared))
            {
                var texture = new Texture2D(4, 4);
                File.WriteAllBytes(shared, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(shared);
            }
            string dir = folder + "/Jacket " + version;
            AssetDatabase.CreateFolder(folder, "Jacket " + version);
            var shader = Shader.Find("Standard");
            var red = new Material(shader) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(shared) };
            var blue = new Material(shader) { mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(shared), color = Color.blue };
            AssetDatabase.CreateAsset(red, dir + "/Red.mat");
            AssetDatabase.CreateAsset(blue, dir + "/Blue.mat");
            var mesh = new Mesh { name = "Skin", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            mesh.AddBlendShapeFrame("Slim", 100f, new[] { Vector3.zero, Vector3.left * .1f, Vector3.zero }, null, null);
            AssetDatabase.CreateAsset(mesh, dir + "/Skin.asset");
            var root = new GameObject("Jacket");
            new GameObject("Hood").transform.SetParent(root.transform);
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            UnityEngine.Object.DestroyImmediate(body.GetComponent<Collider>());
            body.GetComponent<Renderer>().sharedMaterial = red;
            body.transform.SetParent(root.transform);
            var skin = new GameObject("Skin");
            skin.transform.SetParent(root.transform);
            skin.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
            string path = dir + "/Jacket.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            File.WriteAllText(Orbiters.Toolkit.Editor.VRChat.Attachments.ItemSettings.PathFor(path), JacketSettings);
            AssetDatabase.ImportAsset(Orbiters.Toolkit.Editor.VRChat.Attachments.ItemSettings.PathFor(path));
            string package = Path.Combine(temp, "Jacket " + version + ".unitypackage");
            AssetDatabase.ExportPackage(new[] { dir, shared }, package, ExportPackageOptions.Recurse);
            return (package, AssetDatabase.AssetPathToGUID(path), dir);
        }

        private static Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentOption Option(OrbitersAttachment item, string label) =>
            Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentHooks.Options(item.gameObject).Single(o => o.Label == label);

        // Enzo's ask: an item installed from the gallery keeps what was set on it when it is updated. Two versions built
        // into folders of their own (new GUIDs): the card's settings (a switch, a material, a blendshape), the name it was
        // given and whether it shows carry over to the new version's own files; the update owns the files both share.
        [UnityTest] public IEnumerator AnUpdateKeepsTheItemsSettingsNameAndVisibility()
        {
            var avatar = Avatar();
            var v1 = Jacket("1.0.0");
            var v2 = Jacket("1.1.0");
            string sharedGuid = AssetDatabase.AssetPathToGUID(folder + "/Shared.png");
            AssetDatabase.DeleteAsset(v1.folder);
            AssetDatabase.DeleteAsset(v2.folder);
            AssetDatabase.DeleteAsset(folder + "/Shared.png");

            var first = Asset(9301, v1.package, v1.guid);
            first.fit.variants[0].manifest.setups[0].prefabs[0].attach = new GalleryAttach { mode = "configured", refit = false };
            var install = GalleryInstaller.Start(avatar, first, first.fit.release, first.fit.variants[0], null);
            yield return Until(() => Job(install.id).Terminal);
            Assert.That(Job(install.id).stage, Is.EqualTo(GalleryJob.Complete), Job(install.id).error);
            var old = avatar.GetComponentsInChildren<OrbitersAttachment>(true).Single();
            Option(old, "Hood").Choose(0);
            Option(old, "Colour").Choose(1);
            Option(old, "Fit").Choose(1);
            old.displayName = "Rain jacket"; old.displayNameFor = old.name;
            old.gameObject.SetActive(false);

            var second = Asset(9301, v2.package, v2.guid);
            second.fit.release.version = "1.1.0";
            second.fit.variants[0].manifest.setups[0].prefabs[0].attach = new GalleryAttach { mode = "configured", refit = false };
            var update = GalleryInstaller.Update(avatar, second, old.gallery);
            yield return Until(() => Job(update.id).Terminal);
            Assert.That(Job(update.id).stage, Is.EqualTo(GalleryJob.Complete), Job(update.id).error);

            var now = avatar.GetComponentsInChildren<OrbitersAttachment>(true).Single();
            Assert.That(now.gallery.version, Is.EqualTo("1.1.0"));
            Assert.That(now.variant, Does.StartWith(v2.folder), "the new version's own prefab");
            Assert.That(Option(now, "Hood").Current, Is.EqualTo(0), "the switch");
            Assert.That(now.transform.Find("Body").GetComponent<Renderer>().sharedMaterial, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Material>(v2.folder + "/Blue.mat")), "the new version's own Blue");
            Assert.That(now.transform.Find("Skin").GetComponent<SkinnedMeshRenderer>().GetBlendShapeWeight(0), Is.EqualTo(100f), "the blendshape");
            Assert.That(now.DisplayName, Is.EqualTo("Rain jacket"));
            Assert.That(now.gameObject.activeSelf, Is.False, "hidden as it was");

            // The texture both versions bring stays the gallery's: the update owns it, it is not a file of the project's own.
            var entry = GalleryLedger.Entries.Single(e => e.installId == update.id);
            Assert.That(entry.files.Any(f => f.guid == sharedGuid), Is.True);
            Assert.That(entry.preexisting.Any(f => f.guid == sharedGuid), Is.False);
            Assert.That(GalleryLedger.Entries.Single(e => e.installId == install.id).removedAt, Is.GreaterThan(0));
        }

        // Code from a creator Orbiters trusted when the card was read: asked again before the package is checked (a job
        // resumed with its package already downloaded never asked), and an answer that cannot be had counts as untrusted.
        [UnityTest] public IEnumerator CodeAsksWhenTheCreatorsTrustCannotBeReadAgain()
        {
            var avatar = Avatar();
            var (package, guid, _) = Package("Widget", code: "mergeInto(LibraryManager.library, {});");
            var asset = Asset(9305, package, guid, trusted: true);
            int asked = 0;
            GalleryInstaller.CreatorTrustedNow = (j, c) => { asked++; return Task.FromResult<bool?>(null); };
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, asset.fit.variants[0], null);
            yield return Until(() => Job(job.id).Waiting || Job(job.id).Terminal);
            Assert.That(Job(job.id).waiting, Is.EqualTo("code"));
            Assert.That(asked, Is.EqualTo(1));
            GalleryInstaller.Answer(Job(job.id), false);
            Assert.That(avatar.GetComponentsInChildren<OrbitersAttachment>(true), Is.Empty);
        }

        // An import a reload interrupted is done only when the project's files are the package's: an update finds the
        // older version's file under the same GUID, and imports again.
        [UnityTest] public IEnumerator AnInterruptedImportOfOtherContentImportsAgain()
        {
            var avatar = Avatar();
            var (package, guid, path) = Package("Boot", keepInProject: true);
            var prefab = PrefabUtility.LoadPrefabContents(path);
            prefab.transform.localScale = Vector3.one * 3f;
            PrefabUtility.SaveAsPrefabAsset(prefab, path);
            PrefabUtility.UnloadPrefabContents(prefab);
            string expected = UnityPackageFiles.AssetHashes(package)[guid];
            Assert.That(UnityPackageFiles.FileHash(path), Is.Not.EqualTo(expected));
            var asset = Asset(9304, package, guid);
            string import = Path.Combine(temp, "Boot-import.unitypackage");
            File.Copy(package, import);
            var job = new GalleryJob
            {
                id = Guid.NewGuid().ToString("N"), avatar = GalleryInstaller.AvatarId(avatar), instance = avatar.GetInstanceID(), avatarName = "Avatar",
                assetId = 9304, assetName = "Boot", version = "1.0.0", manifest = asset.fit.variants[0].manifest, setupKey = "default",
                stage = GalleryJob.Import, importing = true, importPath = import, importGuids = new List<string> { guid },
                expected = new List<GalleryFile> { new GalleryFile { guid = guid, hash = expected } }, context = GalleryApi.Context,
            };
            var wrong = GalleryInstaller.Mismatched(job, System.Threading.CancellationToken.None);
            yield return Until(() => wrong.IsCompleted);
            Assert.That(wrong.Result, Is.EqualTo(new[] { path }));
            GalleryJournal.Save(job);
            _ = GalleryInstaller.RunAsync(job);
            yield return Until(() => Job(job.id).Terminal);
            Assert.That(Job(job.id).stage, Is.EqualTo(GalleryJob.Complete), Job(job.id).error);
            Assert.That(UnityPackageFiles.FileHash(path), Is.EqualTo(expected), "imported again");
        }

        // A prefab its creator put on a bone this avatar does not have: nothing is placed, and the files the import brought
        // are in the ledger for Cleanup all the same.
        [UnityTest] public IEnumerator AMissingBoneFailsAndCleanupStillKnowsTheFiles()
        {
            var avatar = Avatar();
            var (package, guid, _) = Package("Crown");
            var asset = Asset(9306, package, guid);
            asset.fit.variants[0].manifest.setups[0].prefabs[0].attach = new GalleryAttach { mode = "parent", bone = "Head" };
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, asset.fit.variants[0], null);
            yield return Until(() => Job(job.id).Terminal);
            Assert.That(Job(job.id).stage, Is.EqualTo(GalleryJob.Failed));
            Assert.That(Job(job.id).error, Does.Contain("Head"));
            Assert.That(avatar.GetComponentsInChildren<OrbitersAttachment>(true), Is.Empty);
            var entry = GalleryLedger.Entries.Single(e => e.installId == job.id);
            Assert.That(entry.files.Any(f => f.guid == guid), Is.True);
            Assert.That(entry.removedAt, Is.GreaterThan(0), "on no avatar: Cleanup may free them");
        }

        // Every declared prefab must be on the avatar (two copies of one count twice); an update's copies pair by prefab,
        // then by name (each version is built with new GUIDs).
        [Test] public void EveryDeclaredPrefabIsCheckedAndCopiesPairByName()
        {
            var avatar = Avatar();
            OrbitersAttachment Copy(string name, string source)
            {
                var go = new GameObject(name); go.transform.SetParent(avatar.transform, false);
                var a = go.AddComponent<OrbitersAttachment>(); a.source = source;
                return a;
            }
            var setup = new GallerySetup { key = "default", prefabs = { new GallerySetupPrefab { guid = "aa", name = "Glove" }, new GallerySetupPrefab { guid = "aa", name = "Glove" }, new GallerySetupPrefab { guid = "bb", name = "Belt" } } };
            var glove = Copy("Glove", "aa");
            var belt = Copy("Belt", "bb");
            Assert.That(GalleryInstaller.Unplaced(setup, new[] { glove, belt }).Select(p => p.name), Is.EqualTo(new[] { "Glove" }));
            Assert.That(GalleryInstaller.Unplaced(setup, new[] { glove, belt, Copy("Glove", "aa") }), Is.Empty);

            var oldHat = Copy("Hat", "v1-hat");
            var oldPin = Copy("Pin", "v1-pin");
            var newPin = Copy("Pin", "v2-pin");
            var newHat = Copy("Hat", "v2-hat");
            var pairs = GalleryInstaller.Pairs(new List<OrbitersAttachment> { oldHat, oldPin }, new List<OrbitersAttachment> { newPin, newHat });
            Assert.That(pairs.Select(p => (p.old, p.now)), Is.EquivalentTo(new[] { (oldHat, newHat), (oldPin, newPin) }));
        }

        // The journal is what a resumed installation trusts: when it cannot be saved the job stops; a cache never stops anything.
        [Test] public void AJournalThatCannotBeSavedSaysSo()
        {
            string blocker = Path.Combine(temp, "a file, not a folder");
            File.WriteAllText(blocker, "");
            LibraryStore.Override = blocker;
            Assert.Throws<IOException>(() => GalleryJournal.Save(new GalleryJob { id = Guid.NewGuid().ToString("N") }));
            Assert.DoesNotThrow(() => LibraryStore.Write("a-cache.json", new List<int> { 1 }));
        }
    }
}
