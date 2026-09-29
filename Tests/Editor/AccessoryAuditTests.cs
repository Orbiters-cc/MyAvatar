using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Accessory drops: what a package or archive may do before anything is imported, jobs across domain reloads, and AI
    // answers that arrive after the user placed the accessory.
    public sealed class AccessoryAuditTests
    {
        private Scene scene;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private string temp, savedJobs, savedFollowUps;
        private Func<UntrustedCodeDialog.Request, bool> confirm;

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            temp = Path.Combine(Path.GetTempPath(), "MyAvatarAudit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            confirm = AccessoryService.ConfirmCode;
            savedJobs = SessionState.GetString(AccessoryService.JobKey, null);
            savedFollowUps = SessionState.GetString(AccessoryFollowUps.Key, null);
        }

        [TearDown] public void TearDown()
        {
            AccessoryService.ConfirmCode = confirm;
            if (string.IsNullOrEmpty(savedJobs)) SessionState.EraseString(AccessoryService.JobKey); else SessionState.SetString(AccessoryService.JobKey, savedJobs);
            if (string.IsNullOrEmpty(savedFollowUps)) SessionState.EraseString(AccessoryFollowUps.Key); else SessionState.SetString(AccessoryFollowUps.Key, savedFollowUps);
            foreach (var value in owned) if (value) { Undo.ClearUndo(value); UnityEngine.Object.DestroyImmediate(value); }
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        private MyAvatar Avatar(string name = "Avatar")
        {
            var root = new GameObject(name); owned.Add(root);
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<MyAvatar>();
        }

        private static IEnumerator Wait(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 20;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Task did not finish.");
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        // A .unitypackage: a gzipped tar of <guid>/pathname and <guid>/asset entries.
        private string Package(string name, params (string path, string content)[] assets)
        {
            string file = Path.Combine(temp, name + ".unitypackage");
            using (var stream = new GZipStream(File.Create(file), CompressionMode.Compress))
            {
                foreach (var (path, content) in assets)
                {
                    string guid = Guid.NewGuid().ToString("N");
                    Entry(stream, guid + "/pathname", Encoding.UTF8.GetBytes(path));
                    Entry(stream, guid + "/asset", Encoding.UTF8.GetBytes(content));
                }
                stream.Write(new byte[1024], 0, 1024);
            }
            return file;
        }

        private static void Entry(Stream stream, string name, byte[] data, long? declared = null)
        {
            var header = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
            Encoding.ASCII.GetBytes(Convert.ToString(declared ?? data.Length, 8).PadLeft(11, '0')).CopyTo(header, 124);
            header[156] = (byte)'0';
            for (int i = 148; i < 156; i++) header[i] = 32;
            Encoding.ASCII.GetBytes(Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
            stream.Write(header, 0, 512);
            stream.Write(data, 0, data.Length);
            int pad = (512 - data.Length % 512) % 512;
            stream.Write(new byte[pad], 0, pad);
        }

        [Test] public void PackageIndexKeepsReadmesAndListsCode()
        {
            var file = Package("Hat", ("Assets/Hat/Readme.txt", "Put the hat on the head bone."), ("Assets/Hat/Editor/Setup.cs", "class Setup {}"), ("Assets/Hat/Hat.prefab", "prefab"));
            var index = AccessoryImport.ReadPackage(file);
            Assert.That(index.Entries.Count, Is.EqualTo(3));
            Assert.That(index.CodeFiles, Is.EqualTo(new[] { "Assets/Hat/Editor/Setup.cs" }));
            var docs = AccessoryImport.PackageDocs(index);
            Assert.That(docs.Single().name, Is.EqualTo("Readme.txt"));
            StringAssert.Contains("head bone", docs[0].text);
        }

        // A header may declare a gigabyte for a pathname in a file of a few bytes: refused before anything is allocated.
        [Test] public void HugeDeclaredEntryIsRefusedBeforeAllocating()
        {
            string file = Path.Combine(temp, "Huge.unitypackage");
            using (var stream = new GZipStream(File.Create(file), CompressionMode.Compress))
            {
                Entry(stream, Guid.NewGuid().ToString("N") + "/pathname", Array.Empty<byte>(), declared: 1073741823);
                stream.Write(new byte[1024], 0, 1024);
            }
            Assert.Throws<InvalidDataException>(() => AccessoryImport.ReadPackage(file));
        }

        [Test] public void ZipWritingOutsideItsFolderIsRefused()
        {
            string zip = Path.Combine(temp, "Evil.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var name in new[] { "Hat.fbx", "../escaped.txt" })
                    using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("x");
            string staging = Path.Combine(AccessoryImport.Staging, "audit-" + Guid.NewGuid().ToString("N"));
            try
            {
                var drop = AccessoryImport.Expand(new[] { zip }, AccessoryImport.ProjectRoot, staging);
                StringAssert.Contains("outside its folder", drop.refused.Single());
                Assert.That(drop.models, Is.Empty);
                Assert.That(File.Exists(Path.Combine(staging, "escaped.txt")), Is.False);
            }
            finally { AccessoryImport.DeleteStaging(staging); }
            Assert.That(Directory.Exists(staging), Is.False);
        }

        [Test] public void ZipWithTooManyFilesIsRefusedBeforeExtracting()
        {
            string zip = Path.Combine(temp, "Many.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                for (int i = 0; i <= AccessoryImport.MaxArchiveEntries; i++) archive.CreateEntry("f" + i + ".txt");
            string staging = Path.Combine(AccessoryImport.Staging, "audit-" + Guid.NewGuid().ToString("N"));
            try
            {
                var drop = AccessoryImport.Expand(new[] { zip }, AccessoryImport.ProjectRoot, staging);
                StringAssert.Contains("too many files", drop.refused.Single());
                Assert.That(Directory.Exists(Path.Combine(staging, "1")), Is.False);
            }
            finally { AccessoryImport.DeleteStaging(staging); }
        }

        [Test] public void StagingCleanupNeverDeletesOutsideTheStagingFolder()
        {
            // A drop's staging path comes from data saved in the scene: anything outside the staging folder is left alone.
            AccessoryImport.DeleteStaging(temp);
            Assert.That(Directory.Exists(temp), Is.True);
        }

        [UnityTest] public IEnumerator PackageWithCodeAsksFirstAndCancelStopsTheDrop()
        {
            var avatar = Avatar();
            string code = "Assets/MyAvatarAudit-" + Guid.NewGuid().ToString("N") + "/Setup.cs";
            var file = Package("Tail", (code, "class Setup {}"), ("Assets/Tail/Readme.md", "A tail."));
            UntrustedCodeDialog.Request asked = null;
            AccessoryService.ConfirmCode = request => { asked = request; return false; };
            var drop = AccessoryService.DropAsync(avatar, new[] { file }, null, (_, __) => { }, CancellationToken.None);
            yield return Wait(drop);
            Assert.That(asked, Is.Not.Null);
            Assert.That(asked.Files, Is.EqualTo(new[] { code }));
            Assert.That(asked.Message, Is.EqualTo(AccessoryService.CodeMessage));
            Assert.That(drop.Result, Is.Empty);
            StringAssert.StartsWith("Cancelled", avatar.accessoryStatus);
            Assert.That(File.Exists(code), Is.False);
            Assert.That(AccessoryService.Busy(avatar), Is.False);
            Assert.That(AccessoryService.Saved(), Is.Empty);
        }

        [UnityTest] public IEnumerator PackageWritingOutsideTheProjectIsRefused()
        {
            var avatar = Avatar();
            var file = Package("Evil", ("Assets/../../evil.txt", "x"));
            AccessoryService.ConfirmCode = _ => throw new AssertionException("Nothing with code here.");
            var drop = AccessoryService.DropAsync(avatar, new[] { file }, null, (_, __) => { }, CancellationToken.None);
            yield return Wait(drop);
            Assert.That(avatar.accessoryNotes.Any(n => n.text.Contains("outside Assets and Packages")), Is.True);
            Assert.That(drop.Result, Is.Empty);
        }

        [UnityTest] public IEnumerator NestedZipCodeConsentPrecedesEveryProjectWrite()
        {
            var avatar = Avatar();
            string token = Guid.NewGuid().ToString("N"), code = "Assets/MyAvatarAudit-" + token + "/Setup.cs";
            string model = "AuditModel-" + token + ".fbx";
            string package = Package("Nested", (code, "class Setup {}"));
            string inner = Path.Combine(temp, "Inner.zip"), outer = Path.Combine(temp, "Outer.zip");
            using (var zip = ZipFile.Open(inner, ZipArchiveMode.Create)) zip.CreateEntryFromFile(package, "Nested.unitypackage");
            using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(inner, "Inner.zip");
                using (var writer = new StreamWriter(zip.CreateEntry(model).Open())) writer.Write("Never import this fixture.");
            }
            bool asked = false;
            bool ModelCopied() => Directory.Exists(AccessoryImport.Folder) && Directory.GetFiles(AccessoryImport.Folder, model, SearchOption.AllDirectories).Length > 0;
            AccessoryService.ConfirmCode = request =>
            {
                asked = true;
                Assert.That(request.Files, Does.Contain(code));
                Assert.That(File.Exists(code), Is.False);
                Assert.That(ModelCopied(), Is.False, "Even a loose model must wait for the nested package's consent.");
                return false;
            };
            var task = AccessoryService.DropAsync(avatar, new[] { outer }, null, (_, __) => { }, CancellationToken.None);
            yield return Wait(task);
            Assert.That(asked, Is.True);
            Assert.That(task.Result, Is.Empty);
            Assert.That(File.Exists(code), Is.False);
            Assert.That(ModelCopied(), Is.False);
            Assert.That(AccessoryService.Busy(avatar), Is.False);
        }

        // Objects of a never-saved scene have no resolvable global ID; the instance ID finds them after a domain reload.
        [Test] public void JobFindsAnAvatarOfANeverSavedScene()
        {
            var avatar = Avatar();
            var job = new AccessoryService.Job { avatar = GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString(), instance = avatar.GetInstanceID(), batch = "audit" };
            Assert.That(AccessoryService.Find(job), Is.SameAs(avatar));
        }

        [Test] public void JobsOfTwoAvatarsAreKeptApart()
        {
            var first = new AccessoryService.Job { batch = "audit-" + Guid.NewGuid().ToString("N"), instance = 1 };
            var second = new AccessoryService.Job { batch = "audit-" + Guid.NewGuid().ToString("N"), instance = 2 };
            AccessoryService.Save(first); AccessoryService.Save(second);
            Assert.That(AccessoryService.Saved().Select(j => j.batch), Is.SupersetOf(new[] { first.batch, second.batch }));
            AccessoryService.Forget(first);
            var saved = AccessoryService.Saved().Select(j => j.batch).ToList();
            Assert.That(saved, Does.Not.Contain(first.batch)); Assert.That(saved, Does.Contain(second.batch));
        }

        [UnityTest] public IEnumerator OneJobPerAvatarWhileOtherAvatarsRun()
        {
            var first = Avatar("First"); var second = Avatar("Second");
            var hold = new TaskCompletionSource<List<AccessoryService.Outcome>>();
            var running = AccessoryService.Locked(first, () => hold.Task);
            Assert.That(AccessoryService.Busy(first), Is.True); Assert.That(AccessoryService.Busy(second), Is.False);
            yield return Wait(AccessoryService.Locked(second, () => Task.FromResult(new List<AccessoryService.Outcome>())));
            var again = AccessoryService.Locked(first, () => Task.FromResult(new List<AccessoryService.Outcome>()));
            while (!again.IsCompleted) yield return null;
            Assert.That(again.IsFaulted, Is.True); Assert.That(again.Exception.GetBaseException(), Is.TypeOf<InvalidOperationException>());
            Assert.That(AccessoryService.Busy(first), Is.True);
            hold.SetResult(new List<AccessoryService.Outcome>());
            yield return Wait(running);
            Assert.That(AccessoryService.Busy(first), Is.False);
        }

        // A drop finished after a domain reload keeps its loose images for the Inspector, like an uninterrupted drop.
        [UnityTest] public IEnumerator ResumedDropKeepsItsImagesForTheFollowUp()
        {
            var avatar = Avatar();
            string image = Path.Combine(temp, "Hat_BaseColor.png");
            var pixels = new Texture2D(2, 2); File.WriteAllBytes(image, pixels.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(pixels);
            var job = new AccessoryService.Job { instance = avatar.GetInstanceID(), batch = "audit-" + Guid.NewGuid().ToString("N"), drop = new AccessoryImport.Drop { images = { image } } };
            AccessoryService.Save(job);
            yield return Wait(AccessoryService.ResumeAsync(job));
            Assert.That(AccessoryService.Saved().Any(j => j.batch == job.batch), Is.False);
            Assert.That(AccessoryService.HasFollowUp(avatar), Is.True);
            var outcomes = AccessoryService.TakeFollowUp(avatar);
            Assert.That(outcomes.SelectMany(o => o.images), Is.EqualTo(new[] { image }));
            Assert.That(AccessoryService.HasFollowUp(avatar), Is.False);
            AccessoryFollowUps.Complete(avatar, outcomes);
        }

        [Test] public void FollowUpSurvivesAnotherReloadUntilItsInspectorAcknowledgesIt()
        {
            var avatar = Avatar();
            var hat = new GameObject("Hat"); hat.transform.SetParent(avatar.transform, false);
            var attachment = hat.AddComponent<OrbitersAttachment>(); attachment.mode = OrbitersAttachment.AttachMode.Parent;
            var unresolved = new GameObject("Unmatched bone").transform; unresolved.SetParent(hat.transform, false);
            var plan = new AttachmentPlan { Root = hat, Avatar = avatar.transform, Kind = AttachmentKind.Rigid, ParentGuessed = true };
            plan.Unmatched.Add(unresolved);
            AccessoryFollowUps.Save(avatar, new List<AccessoryService.Outcome> {
                new AccessoryService.Outcome { attachment = attachment, plan = plan, images = new List<string> { "saved-image.png" },
                    docs = new List<AccessoryImport.Doc> { new AccessoryImport.Doc { name = "README.txt", text = "Fixture" } } },
            });
            var first = AccessoryService.TakeFollowUp(avatar);
            Assert.That(AccessoryService.HasFollowUp(avatar), Is.False, "An in-flight Inspector owns the record.");
            AccessoryFollowUps.ReleaseClaim(avatar); // domain reload loses in-memory claims, not SessionState
            Assert.That(AccessoryService.HasFollowUp(avatar), Is.True);
            var replay = AccessoryService.TakeFollowUp(avatar);
            Assert.That(replay.Single().images, Is.EqualTo(new[] { "saved-image.png" }));
            Assert.That(replay.Single().attachment, Is.SameAs(attachment));
            Assert.That(replay.Single().plan.ParentGuessed, Is.True);
            Assert.That(replay.Single().plan.Unmatched, Is.EqualTo(new[] { unresolved }));
            Assert.That(replay.Single().docs.Single().text, Is.EqualTo("Fixture"));
            AccessoryFollowUps.Complete(avatar, replay);
            AccessoryFollowUps.ReleaseClaim(avatar);
            Assert.That(AccessoryService.HasFollowUp(avatar), Is.False);
        }

        [Test] public void DeletedAccessoryFollowUpCannotRecolorTheWholeAvatar()
        {
            var avatar = Avatar();
            var hat = new GameObject("Hat"); hat.transform.SetParent(avatar.transform, false);
            var attachment = hat.AddComponent<OrbitersAttachment>();
            AccessoryFollowUps.Save(avatar, new List<AccessoryService.Outcome> {
                new AccessoryService.Outcome { attachment = attachment, images = new List<string> { "hat.png" } },
            });
            UnityEngine.Object.DestroyImmediate(hat);
            var outcomes = AccessoryService.TakeFollowUp(avatar);
            Assert.That(outcomes.Any(o => o.images != null && o.images.Count > 0), Is.False);
            AccessoryFollowUps.Complete(avatar, outcomes);
            Assert.That(AccessoryService.HasFollowUp(avatar), Is.False);
        }

        [UnityTest] public IEnumerator CurrentAccessoryAnswerApplies() { yield return LateAccessoryAnswer("current"); }
        [UnityTest] public IEnumerator AccessoryPlacedMeanwhileKeepsItsBone() { yield return LateAccessoryAnswer("moved"); }
        [UnityTest] public IEnumerator AiTurnedOffRejectsLateAccessoryAnswer() { yield return LateAccessoryAnswer("ai-off"); }
        [UnityTest] public IEnumerator OtherAccountRejectsLateAccessoryAnswer() { yield return LateAccessoryAnswer("account"); }
        [UnityTest] public IEnumerator ManualTransformRejectsLateAccessoryAnswer() { yield return LateAccessoryAnswer("transform"); }
        [UnityTest] public IEnumerator DetachedInspectorRejectsLateAccessoryAnswer() { yield return LateAccessoryAnswer("detach"); }

        private IEnumerator LateAccessoryAnswer(string action)
        {
            var avatar = Avatar();
            Transform Bone(string name) { var bone = new GameObject(name).transform; bone.SetParent(avatar.transform, false); return bone; }
            Transform head = Bone("Head"), hand = Bone("Hand"), chest = Bone("Chest");
            var hat = new GameObject("Hat"); hat.transform.SetParent(avatar.transform, false);
            var attachment = hat.AddComponent<OrbitersAttachment>(); attachment.mode = OrbitersAttachment.AttachMode.Parent; attachment.parent = head;
            var plan = new AttachmentPlan { Root = hat, Avatar = avatar.transform, Kind = AttachmentKind.Rigid, Mode = OrbitersAttachment.AttachMode.Parent, Parent = head, ParentGuessed = true };
            bool ai = true; string token = "fixture";
            var section = new AccessoriesSection(avatar, new AccessoriesSection.Host
            {
                Run = work => work(), AiOn = () => ai, SetAi = _ => { }, ApplyTextures = (_, __) => Task.CompletedTask, Background = _ => { },
            });
            var reply = new TaskCompletionSource<AccessoryAi.Result>();
            section.RequestAi = (_, __, ___) => reply.Task;
            section.Token = () => token;
            try
            {
                const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
                int revision = (int)typeof(AccessoriesSection).GetField("revision", Private).GetValue(section);
                var outcome = new AccessoryService.Outcome { attachment = attachment, plan = plan, docs = new List<AccessoryImport.Doc>() };
                var ask = (Task)typeof(AccessoriesSection).GetMethod("AskAiAsync", Private).Invoke(section, new object[] { new List<AccessoryService.Outcome> { outcome }, revision });
                if (action == "moved") attachment.parent = chest;
                if (action == "ai-off") ai = false;
                if (action == "account") token = "another account";
                if (action == "transform") attachment.transform.localPosition = new Vector3(1, 2, 3);
                if (action == "detach") section.Detach();
                reply.SetResult(new AccessoryAi.Result { target = hand });
                yield return Wait(ask);
                Assert.That(attachment.parent, Is.SameAs(action == "current" ? hand : action == "moved" ? chest : head));
                if (action == "transform") Assert.That(attachment.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            }
            finally { section.Detach(); Undo.ClearUndo(attachment); }
        }
    }
}
