using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // What a dropped package may do to the project: never write into Packages/, ask before replacing the project's files,
    // and import only the file the user was asked about.
    public sealed class AccessoryConsentTests
    {
        private Scene scene;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private string temp, staging, folder, savedJobs;
        private Func<UntrustedCodeDialog.Request, bool> confirmCode, confirmReplace;

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            temp = Path.Combine(Path.GetTempPath(), "MyAvatarConsent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            staging = Path.Combine(AccessoryImport.Staging, "consent-" + Guid.NewGuid().ToString("N"));
            folder = "Assets/MyAvatarConsent-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            confirmCode = AccessoryService.ConfirmCode;
            confirmReplace = AccessoryService.ConfirmReplace;
            savedJobs = SessionState.GetString(AccessoryService.JobKey, null);
        }

        [TearDown] public void TearDown()
        {
            AccessoryService.ConfirmCode = confirmCode;
            AccessoryService.ConfirmReplace = confirmReplace;
            if (string.IsNullOrEmpty(savedJobs)) SessionState.EraseString(AccessoryService.JobKey); else SessionState.SetString(AccessoryService.JobKey, savedJobs);
            foreach (var value in owned) if (value) { Undo.ClearUndo(value); UnityEngine.Object.DestroyImmediate(value); }
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            AccessoryImport.DeleteStaging(staging);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            // Only the folder this test created.
            if (folder.StartsWith("Assets/MyAvatarConsent-", StringComparison.Ordinal) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
        }

        private MyAvatar Avatar()
        {
            var root = new GameObject("Avatar"); owned.Add(root);
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<MyAvatar>();
        }

        private static IEnumerator Wait(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Task did not finish.");
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        // A .unitypackage: a gzipped tar of <guid>/pathname and <guid>/asset entries, with the GUIDs given.
        private string Package(string name, params (string guid, string path, string content)[] assets)
        {
            string file = Path.Combine(temp, name + ".unitypackage");
            using (var stream = new GZipStream(File.Create(file), CompressionMode.Compress))
            {
                foreach (var (guid, path, content) in assets)
                {
                    Entry(stream, guid + "/pathname", Encoding.UTF8.GetBytes(path));
                    Entry(stream, guid + "/asset", Encoding.UTF8.GetBytes(content));
                }
                stream.Write(new byte[1024], 0, 1024);
            }
            return file;
        }

        private static string NewGuid() => Guid.NewGuid().ToString("N");

        private static void Entry(Stream stream, string name, byte[] data)
        {
            var header = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
            Encoding.ASCII.GetBytes(Convert.ToString(data.Length, 8).PadLeft(11, '0')).CopyTo(header, 124);
            header[156] = (byte)'0';
            for (int i = 148; i < 156; i++) header[i] = 32;
            Encoding.ASCII.GetBytes(Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
            stream.Write(header, 0, 512);
            stream.Write(data, 0, data.Length);
            int pad = (512 - data.Length % 512) % 512;
            stream.Write(new byte[pad], 0, pad);
        }

        // Entries for Packages/ (a VPM manifest, a package's script) are left out of the copy imported, and not asked about.
        [UnityTest] public IEnumerator PackageEntriesForPackagesAreLeftOutOfTheImport()
        {
            string hat = NewGuid();
            var file = Package("Hat", (hat, folder + "/Hat.prefab", "prefab"), (NewGuid(), "Packages/vpm-manifest.json", "{}"),
                (NewGuid(), "Packages/com.example.hat/Runtime/Hat.cs", "class Hat {}"));
            var review = AccessoryImport.ReviewAsync(file, staging, CancellationToken.None);
            yield return Wait(review);
            var result = review.Result;
            Assert.That(result.refused, Is.Null);
            Assert.That(result.code, Is.Empty, "Nothing for Packages/ is imported, so nothing there is asked about.");
            StringAssert.Contains("2 files for Packages/ left out", result.note);
            Assert.That(result.guids, Is.EqualTo(new[] { hat }));
            Assert.That(AccessoryImport.ReadPackage(result.package.path).Paths, Is.EqualTo(new[] { folder + "/Hat.prefab" }));
            Assert.That(result.package.path, Does.StartWith(Path.GetFullPath(staging)), "Imported from the drop's own copy.");
        }

        // A package carrying a file the project has with other content replaces it everywhere: asked first, with the file
        // listed; Cancel imports nothing.
        [UnityTest] public IEnumerator ReplacingAProjectFileAsksFirst()
        {
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            string mine = folder + "/Notes.txt";
            File.WriteAllText(mine, "mine");
            AssetDatabase.ImportAsset(mine);
            string guid = AssetDatabase.AssetPathToGUID(mine);
            var file = Package("Notes", (guid, mine, "theirs"));
            UntrustedCodeDialog.Request asked = null;
            AccessoryService.ConfirmCode = _ => throw new AssertionException("Nothing with code here.");
            AccessoryService.ConfirmReplace = request => { asked = request; return false; };
            var avatar = Avatar();
            var drop = AccessoryService.DropAsync(avatar, new[] { file }, (_, __) => { }, CancellationToken.None);
            yield return Wait(drop);
            Assert.That(asked, Is.Not.Null);
            Assert.That(asked.Files, Is.EqualTo(new[] { mine }));
            Assert.That(drop.Result, Is.Empty);
            StringAssert.StartsWith("Cancelled", avatar.accessoryStatus);
            Assert.That(File.ReadAllText(mine), Is.EqualTo("mine"));
        }

        // Consent is for the file reviewed: a copy changed before its import (here after a reload) is reviewed and asked
        // about again, and declining leaves it out.
        [UnityTest] public IEnumerator PackageChangedAfterConsentIsAskedAgain()
        {
            string code = folder + "/Setup.cs";
            var review = AccessoryImport.ReviewAsync(Package("Tail", (NewGuid(), folder + "/Tail.prefab", "prefab")), Path.Combine(staging, "packages"), CancellationToken.None);
            yield return Wait(review);
            var package = review.Result.package;
            File.Copy(Package("Tail changed", (NewGuid(), code, "class Setup {}")), package.path, true);
            var unchanged = AccessoryImport.UnchangedAsync(package, CancellationToken.None);
            yield return Wait(unchanged);
            Assert.That(unchanged.Result, Is.False);

            var avatar = Avatar();
            UntrustedCodeDialog.Request asked = null;
            AccessoryService.ConfirmCode = request => { asked = request; return false; };
            var job = new AccessoryService.Job { instance = avatar.GetInstanceID(), batch = "consent-" + NewGuid(), drop = new AccessoryImport.Drop { staging = staging } };
            job.imports.Add(package);
            AccessoryService.Save(job);
            yield return Wait(AccessoryService.ResumeAsync(job));
            Assert.That(asked, Is.Not.Null, "Asked again about what the file holds now.");
            Assert.That(asked.Files, Is.EqualTo(new[] { code }));
            Assert.That(File.Exists(code), Is.False);
            Assert.That(avatar.accessoryNotes.Any(n => n.text.Contains("changed after you allowed it")), Is.True);
            Assert.That(AccessoryService.Saved().Any(j => j.batch == job.batch), Is.False);
        }
    }
}
