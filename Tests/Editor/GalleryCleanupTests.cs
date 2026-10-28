using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
using Orbiters.Toolkit.Editor;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Gallery cleanup: what stays (used from outside the files being freed, or by one that stays), a file changed since
    // the check stays, the quarantine keeps every file with its .meta and the .meta of the folders it emptied, Restore
    // puts them back with their GUIDs and never beside another .meta, and two cleanups never share a batch. Library is a
    // folder of the test's own.
    public sealed class GalleryCleanupTests
    {
        private string folder, library;

        [SetUp] public void SetUp()
        {
            library = Path.Combine(Path.GetTempPath(), "MyAvatarCleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(library);
            LibraryStore.Override = library;
            folder = "Assets/MyAvatarCleanupTest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            // Keeps the test's folder from being emptied (and removed) by a cleanup.
            Text("Keep.txt", "keep");
        }

        [TearDown] public void TearDown()
        {
            LibraryStore.Override = null;
            if (folder.StartsWith("Assets/MyAvatarCleanupTest-", StringComparison.Ordinal) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            if (Directory.Exists(library)) Directory.Delete(library, true);
        }

        private GalleryCleanup.Item Text(string relative, string content)
        {
            string path = folder + "/" + relative;
            string at = folder;
            foreach (string part in relative.Split('/').Reverse().Skip(1).Reverse())
            {
                if (!AssetDatabase.IsValidFolder(at + "/" + part)) AssetDatabase.CreateFolder(at, part);
                at += "/" + part;
            }
            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);
            return new GalleryCleanup.Item { guid = AssetDatabase.AssetPathToGUID(path), path = path, hash = UnityPackageFiles.FileHash(path), bytes = content.Length, from = "Test" };
        }

        private static GalleryCleanup.Report Reclaim(params GalleryCleanup.Item[] items)
        {
            var report = new GalleryCleanup.Report();
            report.Reclaim.AddRange(items);
            return report;
        }

        [Test] public void WhatStaysSpreadsToTheFilesItUses()
        {
            var items = new[] { "a", "b", "c", "d", "e" }.ToDictionary(n => "Assets/" + n, n => new GalleryCleanup.Item { path = "Assets/" + n }, StringComparer.OrdinalIgnoreCase);
            var users = new Dictionary<string, string[]>
            {
                // a is used by a scene outside the set; a uses b, b uses c; d is only used by e, which nothing uses.
                ["Assets/a"] = new[] { "Assets/Scene.unity" }, ["Assets/b"] = new[] { "Assets/a" }, ["Assets/c"] = new[] { "Assets/b" }, ["Assets/d"] = new[] { "Assets/e" },
            };
            var kept = GalleryCleanup.Retained(items, path => users.TryGetValue(path, out var u) ? u : Array.Empty<string>(), _ => false);
            Assert.That(kept.Select(k => k.item.path), Is.EquivalentTo(new[] { "Assets/a", "Assets/b", "Assets/c" }));
            Assert.That(kept.Single(k => k.item.path == "Assets/a").reason, Is.EqualTo("Used by Scene.unity."));
            Assert.That(kept.Single(k => k.item.path == "Assets/c").reason, Is.EqualTo("Used by b."));
            var inScene = GalleryCleanup.Retained(items, path => Array.Empty<string>(), path => path == "Assets/e");
            Assert.That(inScene.Single().reason, Is.EqualTo("It is used in an open scene."));
        }

        [Test] public void QuarantineAndRestoreKeepFilesAndFoldersAsTheyWere()
        {
            var a = Text("Sub/A.txt", "a");
            var b = Text("Sub/Deep/B.txt", "b");
            string sub = AssetDatabase.AssetPathToGUID(folder + "/Sub"), deep = AssetDatabase.AssetPathToGUID(folder + "/Sub/Deep");
            var batch = GalleryCleanup.Quarantine(Reclaim(a, b));
            Assert.That(batch.items.Count, Is.EqualTo(2));
            Assert.That(File.Exists(a.path) || File.Exists(b.path), Is.False);
            Assert.That(AssetDatabase.IsValidFolder(folder + "/Sub"), Is.False, "the emptied folders go too");
            Assert.That(batch.folders, Is.EquivalentTo(new[] { folder + "/Sub", folder + "/Sub/Deep" }));

            Assert.That(GalleryCleanup.Restore(batch), Is.Empty);
            Assert.That(AssetDatabase.AssetPathToGUID(a.path), Is.EqualTo(a.guid));
            Assert.That(AssetDatabase.AssetPathToGUID(b.path), Is.EqualTo(b.guid));
            Assert.That(AssetDatabase.AssetPathToGUID(folder + "/Sub"), Is.EqualTo(sub), "the folder's own GUID");
            Assert.That(AssetDatabase.AssetPathToGUID(folder + "/Sub/Deep"), Is.EqualTo(deep));
            Assert.That(Directory.Exists(batch.folder), Is.False, "an emptied batch goes");
        }

        [Test] public void AFileChangedSinceTheCheckStays()
        {
            var a = Text("A.txt", "as checked");
            File.WriteAllText(a.path, "edited since");
            var stayed = new List<GalleryCleanup.Item>();
            var batch = GalleryCleanup.Quarantine(Reclaim(a), stayed);
            Assert.That(File.Exists(a.path), Is.True);
            Assert.That(batch.items, Is.Empty);
            Assert.That(stayed.Single().reason, Is.EqualTo("It was changed since it was checked."));
        }

        [Test] public void RestoreNeverPutsAFileBesideAnotherMeta()
        {
            var a = Text("A.txt", "a");
            var batch = GalleryCleanup.Quarantine(Reclaim(a));
            // Another asset's .meta at its place (its file not imported yet).
            File.WriteAllText(a.path + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
            var blocked = GalleryCleanup.Restore(batch);
            Assert.That(blocked.Single(), Does.StartWith(a.path));
            Assert.That(File.Exists(Path.Combine(batch.folder, a.path)), Is.True, "kept in the quarantine");
            Assert.That(File.Exists(Path.Combine(batch.folder, a.path + ".meta")), Is.True, "with its own .meta");
            Assert.That(GalleryCleanup.Batches().Single().items.Single().path, Is.EqualTo(a.path));
        }

        [Test] public void TwoCleanupsInTheSameSecondHaveTheirOwnBatches()
        {
            var first = GalleryCleanup.Quarantine(Reclaim(Text("A.txt", "a")));
            var second = GalleryCleanup.Quarantine(Reclaim(Text("B.txt", "b")));
            Assert.That(second.folder, Is.Not.EqualTo(first.folder));
            var batches = GalleryCleanup.Batches();
            Assert.That(batches.Count, Is.EqualTo(2));
            Assert.That(batches.SelectMany(b => b.items).Select(i => Path.GetFileName(i.path)), Is.EquivalentTo(new[] { "A.txt", "B.txt" }));
        }
    }
}
