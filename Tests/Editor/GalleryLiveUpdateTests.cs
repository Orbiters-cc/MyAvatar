using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
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
    // A real update from the gallery: a hidden copy (in a preview scene) of an open avatar wearing an older N4N0 from the
    // development gallery gets other card choices, other customizable parts, a name and is hidden, then is updated to the
    // gallery's newest N4N0 the way its Update button does it; the new copy must keep all of it. Run by hand (the
    // development server on 127.0.0.1:4100, My Avatar's Development environment on): it imports the newest package and
    // leaves the user's avatar, scene, journal and ledger alone (Library is a folder of the test's own; what the probe's
    // choices wrote under Assets is removed afterwards).
    [Explicit, Category("Live")]
    public sealed class GalleryLiveUpdateTests
    {
        private const int N4N0 = 30;
        private const string Probe = "MyAvatar update probe";
        private Scene scene;
        private string library;
        private HashSet<string> before;

        private static HashSet<string> Files() => new HashSet<string>(
            new[] { "Assets/Orbiters/N4N0" }.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories)).Select(p => p.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        [SetUp] public void SetUp()
        {
            library = Path.Combine(Path.GetTempPath(), "MyAvatarLive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(library);
            LibraryStore.Override = library;
            GalleryJournal.ResetForTests();
            before = Files();
            scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown] public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(scene);
            LibraryStore.Override = null;
            typeof(GalleryJournal).GetField("jobs", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, null);
            // What the probe's parts wrote goes; N4N0's option files are shared by every N4N0 with those options and stay.
            string variants = ItemParts.VariantsFolder + "/" + Probe;
            if (AssetDatabase.IsValidFolder(variants)) AssetDatabase.DeleteAsset(variants);
            var added = Files().Where(f => !before.Contains(f) && !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToList();
            if (added.Count > 0) Debug.Log("[My Avatar live] N4N0 option files written meanwhile: " + string.Join(", ", added));
            if (Directory.Exists(library)) Directory.Delete(library, true);
        }

        private static IEnumerator Wait(Func<bool> done, double seconds, string what)
        {
            double until = EditorApplication.timeSinceStartup + seconds;
            while (!done() && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.That(done(), Is.True, "Timed out: " + what);
        }

        private static Dictionary<string, string> Choices(GameObject item) =>
            AttachmentHooks.Options(item).GroupBy(o => o.Label).ToDictionary(g => g.Key, g => { var o = g.First(); return o.Current >= 0 && o.Current < o.Choices.Count ? o.Choices[o.Current] : "(none)"; });

        private static Dictionary<string, string> Parts(GameObject item) =>
            ItemParts.Of(item).ToDictionary(p => p.Part.key, p => p.Current == null ? "(none)" : p.Current.IsBase ? "base " + p.Current.baseName : "variant " + p.Current.id + " " + p.Current.sha256);

        [UnityTest] public IEnumerator AnN4N0UpdateFromTheGalleryKeepsWhatWasSetOnIt()
        {
            if (!OrbitersEnvironment.IsDevelopment) Assert.Ignore("Turn My Avatar's Development environment on.");
            var source = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Where(s => s.isLoaded).SelectMany(s => s.GetRootGameObjects())
                .Select(r => r.GetComponent<MyAvatar>()).FirstOrDefault(a => a && a.GetComponentsInChildren<OrbitersAttachment>(true).Any(x => x.gallery != null && x.gallery.assetId == N4N0));
            if (!source) Assert.Ignore("No open avatar wears N4N0 from the gallery.");

            var read = GalleryApi.DetailAsync(N4N0, AvatarPlatform.Current, null, CancellationToken.None);
            yield return Wait(() => read.IsCompleted, 30, "the gallery");
            if (read.IsFaulted || read.Result?.fit?.release == null) Assert.Ignore("The development gallery could not be read: " + read.Exception?.GetBaseException().Message);
            var asset = read.Result;

            var holder = new GameObject("Probe holder");
            SceneManager.MoveGameObjectToScene(holder, scene);
            var copy = UnityEngine.Object.Instantiate(source.gameObject, holder.transform);
            copy.name = Probe;
            var avatar = copy.GetComponent<MyAvatar>();
            var old = copy.GetComponentsInChildren<OrbitersAttachment>(true).First(x => x.gallery != null && x.gallery.assetId == N4N0);
            Assume.That(GalleryUI.CompareVersions(asset.fit.release.version, old.gallery.version), Is.GreaterThan(0), "The gallery has no newer N4N0 than " + old.gallery.version + ".");

            // Other choices than those it has: every switch flipped, every choice moved on (each read again, since one can change another).
            foreach (string label in AttachmentHooks.Options(old.gameObject).Select(o => o.Label).Distinct().ToList())
            {
                var option = AttachmentHooks.Options(old.gameObject).First(o => o.Label == label);
                if (option.Choices.Count > 1) option.Choose((Math.Max(0, option.Current) + 1) % option.Choices.Count);
            }
            // Other base variants of its parts drawn by a renderer (theme, language).
            foreach (var placed in ItemParts.Of(old.gameObject).Where(p => !string.IsNullOrEmpty(p.Part.renderer)).ToList())
            {
                var other = ItemParts.Bases(placed.PartsPath, placed.Part).FirstOrDefault(b => placed.Current == null || b.BaseName != placed.Current.baseName);
                if (other == null) continue;
                var apply = ItemParts.ApplyAsync(ItemParts.Of(old.gameObject).First(p => p.Part.key == placed.Part.key), other);
                yield return Wait(() => apply.IsCompleted, 180, "applying " + placed.Part.key);
                Assert.That(apply.Result.Applied, Is.True, placed.Part.key + ": " + string.Join(" ", apply.Result.Problems));
            }
            old.displayName = "Probe orb"; old.displayNameFor = old.name;
            old.gameObject.SetActive(false);
            var choices = Choices(old.gameObject);
            var parts = Parts(old.gameObject);
            Debug.Log("[My Avatar live] " + old.gallery.version + " set to: " + string.Join(", ", choices.Select(c => c.Key + " = " + c.Value)) + " · " + string.Join(", ", parts.Select(c => c.Key + " = " + c.Value)));

            var job = GalleryInstaller.Update(avatar, asset, old.gallery);
            Assert.That(job, Is.Not.Null, "the gallery offers an update for this avatar");
            yield return Wait(() => { var j = GalleryJournal.All.First(x => x.id == job.id); return j.Terminal || j.Waiting; }, 600, "the update");
            var done = GalleryJournal.All.First(x => x.id == job.id);
            Assert.That(done.Waiting, Is.False, "it asked: " + done.questionSummary + " " + string.Join("; ", done.questionLines.Take(8)));
            Assert.That(done.stage, Is.EqualTo(GalleryJob.Complete), done.error);

            var now = copy.GetComponentsInChildren<OrbitersAttachment>(true).Where(x => x.gallery != null && x.gallery.assetId == N4N0).ToList();
            Assert.That(now.Count, Is.EqualTo(1), "the older copy is gone");
            Assert.That(now[0].gallery.version, Is.EqualTo(asset.fit.release.version));
            // Its parts take their variants again in the background (a renderer draws them).
            var problems = new List<string>();
            for (double until = EditorApplication.timeSinceStartup + 240; EditorApplication.timeSinceStartup < until && parts.Any(p => !Parts(now[0].gameObject).TryGetValue(p.Key, out var v) || v != p.Value);)
                yield return null;
            foreach (var pair in choices)
                if (!Choices(now[0].gameObject).TryGetValue(pair.Key, out var value) || value != pair.Value) problems.Add(pair.Key + ": " + pair.Value + " became " + (value ?? "(gone)"));
            foreach (var pair in parts)
                if (!Parts(now[0].gameObject).TryGetValue(pair.Key, out var value) || value != pair.Value) problems.Add(pair.Key + ": " + pair.Value + " became " + (value ?? "(gone)"));
            if (now[0].DisplayName != "Probe orb") problems.Add("name: " + now[0].DisplayName);
            if (now[0].gameObject.activeSelf) problems.Add("shown again");
            Debug.Log("[My Avatar live] " + now[0].gallery.version + " has: " + string.Join(", ", Choices(now[0].gameObject).Select(c => c.Key + " = " + c.Value)) + " · " + string.Join(", ", Parts(now[0].gameObject).Select(c => c.Key + " = " + c.Value)));
            Assert.That(problems, Is.Empty);
        }
    }
}
