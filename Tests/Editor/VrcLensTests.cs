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
    // VRCLens's packages as My Avatar reads them before importing anything: which VRCLens it is, and whether it needs VRLabs'
    // Avatars 3.0 Manager first (1.10 and later), in which case a drop imports nothing and waits for its download. The
    // packages here only look like VRCLens's (its paths, a readme and a script that compiles): never imported.
    public sealed class VrcLensTests
    {
        private Scene scene;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private string temp, savedJobs;
        private Func<UntrustedCodeDialog.Request, bool> confirmCode, confirmReplace;

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            temp = Path.Combine(Path.GetTempPath(), "MyAvatarVrcLens-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            confirmCode = AccessoryService.ConfirmCode;
            confirmReplace = AccessoryService.ConfirmReplace;
            savedJobs = SessionState.GetString(AccessoryService.JobKey, null);
        }

        [TearDown] public void TearDown()
        {
            AccessoryService.ConfirmCode = confirmCode;
            AccessoryService.ConfirmReplace = confirmReplace;
            if (string.IsNullOrEmpty(savedJobs)) SessionState.EraseString(AccessoryService.JobKey); else SessionState.SetString(AccessoryService.JobKey, savedJobs);
            foreach (var value in owned.OfType<GameObject>()) if (value) VrcLens.Forget(value.GetComponent<MyAvatar>());
            foreach (var value in owned) if (value) { Undo.ClearUndo(value); UnityEngine.Object.DestroyImmediate(value); }
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        // A folder of its own, so a package that were imported by mistake would land nowhere the project uses.
        private static string Folder => "Assets/MyAvatarVrcLensTest-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "/Hirabiki/VRCLens";

        // Compiles if ever imported: the name of the manager's merging is in a comment.
        private const string NewScript = "// VRLabs.AV3Manager.AnimatorCloner.MergeControllers(user, instance);\nnamespace MyAvatarVrcLensTest { internal static class Setup110 { } }\n";
        private const string OldScript = "// AnimatorCloner.MergeControllers(user, instance);\nnamespace MyAvatarVrcLensTest { internal static class Setup192 { } }\n";

        private string Lens(string version, string script, bool bundlesCloner)
        {
            string folder = Folder;
            var assets = new List<(string, string, string)>
            {
                (VrcLens.SetupScriptGuid, folder + "/Resource/VRCLensSetup.cs", script),
                (NewGuid(), folder + "/readme_DoNotDelete.txt", "VRCLens version " + version + " - Copyright (c) 2020-2025 Hirabiki. All rights reserved.\n"),
                (VrcLens.PrefabGuid, folder + "/Prefabs/VRCLens.prefab", "prefab"),
            };
            if (bundlesCloner) assets.Add((NewGuid(), folder + "/Lib/AnimatorCloner/AnimatorCloner.cs", "namespace MyAvatarVrcLensTest { internal static class Cloner { } }\n"));
            return Package("VRCLens-v" + version, assets.ToArray());
        }

        [Test] public void ReadsItsVersionAndWhetherItNeedsAvatarsManager()
        {
            var lens110 = VrcLens.ReadPackage(Lens("1.10.0", NewScript, bundlesCloner: false));
            Assert.That(lens110, Is.Not.Null);
            Assert.That(lens110.version, Is.EqualTo("1.10.0"));
            Assert.That(lens110.needsManager, Is.True, "VRCLens 1.10 merges its animator with VRLabs' Avatars 3.0 Manager.");
            var lens192 = VrcLens.ReadPackage(Lens("1.9.2", OldScript, bundlesCloner: true));
            Assert.That(lens192, Is.Not.Null);
            Assert.That(lens192.version, Is.EqualTo("1.9.2"));
            Assert.That(lens192.needsManager, Is.False, "VRCLens 1.9 carries its own copy of the merging.");
            Assert.That(VrcLens.ReadPackage(Package("Hat", (NewGuid(), "Assets/Hat/Hat.prefab", "prefab"))), Is.Null);
            Assert.That(VrcLens.Version("VRCLens version 1.9.2 - Copyright"), Is.EqualTo("1.9.2"));
            Assert.That(VrcLens.Version("Something else"), Is.Null);
        }

        // Its scripts would not compile without Avatars 3.0 Manager: nothing is imported, nothing is asked about, and the
        // drop waits for the download (offered in the accessories field).
        [UnityTest] public IEnumerator ADropNeedingAvatarsManagerImportsNothingAndWaits()
        {
            if (VrcLens.ManagerInstalled) Assert.Ignore("Avatars 3.0 Manager is in this project: nothing to wait for.");
            AccessoryService.ConfirmCode = _ => throw new AssertionException("Nothing is imported, so nothing is asked.");
            AccessoryService.ConfirmReplace = _ => throw new AssertionException("Nothing is imported, so nothing is asked.");
            var root = new GameObject("Avatar"); owned.Add(root);
            SceneManager.MoveGameObjectToScene(root, scene);
            var avatar = root.AddComponent<MyAvatar>();
            string file = Lens("1.10.0", NewScript, bundlesCloner: false);
            var drop = AccessoryService.DropAsync(avatar, new[] { file }, (_, __) => { }, CancellationToken.None);
            yield return Wait(drop);
            Assert.That(drop.Result, Is.Empty);
            StringAssert.Contains("Avatars 3.0 Manager", avatar.accessoryStatus);
            Assert.That(VrcLens.AwaitsManager(avatar), Is.True);
            Assert.That(AssetDatabase.FindAssets("MyAvatarVrcLensTest"), Is.Empty, "Nothing of the package was imported.");
            VrcLens.Forget(avatar);
            Assert.That(VrcLens.AwaitsManager(avatar), Is.False);
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
    }
}
