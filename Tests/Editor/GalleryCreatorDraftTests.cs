using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The creator's draft survives script reloads in SessionState; loading it must give back the same packages, not the
    // saved ones plus the defaults. What was sent to Orbiters belongs to one asset and version, a publish ends the draft,
    // and setups stay valid for the server. The session's own draft is kept aside while the test runs.
    public sealed class GalleryCreatorDraftTests
    {
        private const string Key = "Orbiters.MyAvatar.GalleryCreatorDraft";
        private string saved;

        [SetUp] public void KeepDraft() => saved = SessionState.GetString(Key, null);

        [TearDown]
        public void RestoreDraft()
        {
            if (saved == null) SessionState.EraseString(Key);
            else SessionState.SetString(Key, saved);
        }

        [Test]
        public void ReloadingKeepsThePackagesAsSaved()
        {
            var draft = new GalleryCreatorDraft { version = "2.0.0" };
            draft.variants[0].label = "PC";
            draft.variants[0].platforms = new List<string> { "pc" };
            draft.variants.Add(new GalleryVariantDraft { label = "Quest", platforms = new List<string> { "android" } });
            draft.Save();

            var loaded = GalleryCreatorDraft.Load();
            loaded.Save();
            loaded = GalleryCreatorDraft.Load();

            Assert.AreEqual("2.0.0", loaded.version);
            CollectionAssert.AreEqual(new[] { "PC", "Quest" }, loaded.variants.ConvertAll(v => v.label));
            CollectionAssert.AreEqual(new[] { "pc" }, loaded.variants[0].platforms);
            CollectionAssert.AreEqual(new[] { "android" }, loaded.variants[1].platforms);
        }

        [Test]
        public void APublishedDraftEndsAndNoCopyBringsItBack()
        {
            var draft = new GalleryCreatorDraft { name = "Neon Hat", version = "1.2.0" };
            draft.Target(41);
            draft.UseRelease(7);
            draft.variants[0].MarkUploaded(70, 7);
            draft.Save();
            // The window holds its own copy of the same draft.
            var window = GalleryCreatorDraft.Load();

            draft.End();
            window.version = "1.3.0";
            window.Save();
            draft.Save();

            var next = GalleryCreatorDraft.Load();
            Assert.That(window.Over, Is.True);
            Assert.That(next.Over, Is.False);
            Assert.That(next.draftId, Is.Not.EqualTo(draft.draftId));
            Assert.That(next.idempotencyKey, Is.Not.EqualTo(draft.idempotencyKey), "The next new asset is its own creation request.");
            Assert.That(next.assetId, Is.EqualTo(0));
            Assert.That(next.newAsset, Is.True);
            Assert.That(next.Release, Is.EqualTo(0));
            Assert.That(next.version, Is.EqualTo("1.0.0"));
            Assert.That(next.variants.Single().uploadedId, Is.EqualTo(0));
        }

        [Test]
        public void AnotherAssetDropsTheVersionAndUploadsOfThePreviousOne()
        {
            var draft = new GalleryCreatorDraft { picturesSent = true };
            draft.Target(41);
            Assert.That(draft.newAsset, Is.False);
            draft.UseRelease(7);
            draft.variants[0].MarkUploaded(70, 7);
            draft.picturesSent = true;

            draft.Target(41);
            Assert.That(draft.Release, Is.EqualTo(7), "The same asset keeps what was sent to it.");
            Assert.That(draft.variants[0].UploadedTo(7), Is.True);

            draft.Target(42);
            Assert.That(draft.Release, Is.EqualTo(0));
            Assert.That(draft.releaseId, Is.EqualTo(0));
            Assert.That(draft.variants[0].uploadedId, Is.EqualTo(0));
            Assert.That(draft.variants[0].UploadedTo(7), Is.False);
            Assert.That(draft.picturesSent, Is.False);

            draft.Target(0);
            Assert.That(draft.newAsset, Is.True);
            Assert.That(draft.assetId, Is.EqualTo(0));

            // A version recorded for another asset (an MCP edit of assetId alone) is never used.
            var stale = new GalleryCreatorDraft { assetId = 42, releaseId = 7, releaseAssetId = 41 };
            Assert.That(stale.Release, Is.EqualTo(0));
        }

        [Test]
        public void AnyChangeToAPackageSendsItAgain()
        {
            var variant = new GalleryVariantDraft { label = "PC", platforms = new List<string> { "pc" }, report = new GalleryPackager.Report { sha256 = "aa", parameterBits = 8 } };
            variant.manifest.setups.Add(new GallerySetup { key = "default", label = "Left", prefabs = { new GallerySetupPrefab { guid = "g1", name = "Glove" } } });
            variant.manifest.defaultSetup = "default";
            variant.MarkUploaded(70, 7);
            Assert.That(variant.UploadedTo(7), Is.True);
            Assert.That(variant.UploadedTo(8), Is.False, "Another version of the asset does not have it.");

            void Changes(System.Action change, System.Action back, string what)
            {
                change();
                Assert.That(variant.UploadedTo(7), Is.False, what);
                back();
                Assert.That(variant.UploadedTo(7), Is.True, what + " (undone)");
            }
            Changes(() => variant.label = "Windows", () => variant.label = "PC", "label");
            Changes(() => variant.platforms.Add("android"), () => variant.platforms.Remove("android"), "platforms");
            Changes(() => { variant.baseScope = "bases"; variant.baseIds.Add(3); }, () => { variant.baseScope = "any"; variant.baseIds.Clear(); }, "bases");
            Changes(() => variant.manifest.setups[0].label = "Right", () => variant.manifest.setups[0].label = "Left", "setup");
            Changes(() => variant.manifest.setups[0].prefabs[0].attach.mode = "parent", () => variant.manifest.setups[0].prefabs[0].attach.mode = "auto", "attachment");
            Changes(() => variant.report.sha256 = "bb", () => variant.report.sha256 = "aa", "package");
            Changes(() => variant.report.parameterBits = 9, () => variant.report.parameterBits = 8, "parameters");

            variant.ForgetUpload();
            Assert.That(variant.UploadedTo(7), Is.False);
        }

        [Test]
        public void PackagesNeedNamesOfTheirOwn()
        {
            var draft = new GalleryCreatorDraft { name = "Neon Hat" };
            draft.variants[0].label = "PC";
            draft.variants[0].report = new GalleryPackager.Report { bytes = 10 };
            draft.variants.Add(new GalleryVariantDraft { label = " pc ", report = new GalleryPackager.Report { bytes = 10 } });
            Assert.That(draft.DuplicateLabel(), Is.EqualTo("PC").IgnoreCase);
            Assert.That(GalleryPublisher.Missing(draft), Does.Contain("Two packages are named"));
            draft.variants[1].label = "Quest";
            Assert.That(draft.DuplicateLabel(), Is.Null);
            Assert.That(GalleryPublisher.Missing(draft), Is.Null);
            // An empty name is the server's "Default".
            draft.variants[1].label = "";
            draft.variants[0].label = "default";
            Assert.That(draft.DuplicateLabel(), Is.EqualTo("default").IgnoreCase);
        }

        [Test]
        public void RemovingTheDefaultSetupMovesTheDefaultAndKeysNeverRepeat()
        {
            var manifest = new GalleryManifest { defaultSetup = "setup-2" };
            manifest.setups.Add(new GallerySetup { key = "default" });
            manifest.setups.Add(new GallerySetup { key = manifest.NewKey() });
            Assert.That(manifest.setups[1].key, Is.EqualTo("setup-2"));
            manifest.setups.Add(new GallerySetup { key = manifest.NewKey() });
            Assert.That(manifest.setups[2].key, Is.EqualTo("setup-3"));

            manifest.Remove(manifest.setups[1]);
            Assert.That(manifest.defaultSetup, Is.EqualTo("default"));
            // Two setups left, "setup-3" among them: the next key is a new one.
            string next = manifest.NewKey();
            Assert.That(manifest.setups.Select(s => s.key), Has.No.Member(next));
            Assert.That(next, Is.EqualTo("setup-4"));

            manifest.defaultSetup = "setup-3";
            manifest.Remove(manifest.setups[0]);
            Assert.That(manifest.defaultSetup, Is.EqualTo("setup-3"), "Another setup's removal keeps the default.");
            manifest.Remove(manifest.setups[0]);
            Assert.That(manifest.defaultSetup, Is.Null);
        }

        [Test]
        public void PreReleasesComeBeforeTheirRelease()
        {
            Assert.That(GalleryUI.CompareVersions("1.0.0-beta", "1.0.0"), Is.LessThan(0));
            Assert.That(GalleryUI.CompareVersions("1.0.0", "1.0.0-beta"), Is.GreaterThan(0));
            Assert.That(GalleryUI.CompareVersions("1.0.0+build.2", "1.0.0"), Is.EqualTo(0));
            Assert.That(GalleryUI.CompareVersions("1.0.1-alpha", "1.0.0"), Is.GreaterThan(0));
        }

        [Test]
        public void PictureFilesAreEncodedAgainWithoutTheirExifAndUpright()
        {
            // A 4×2 photo stored sideways with EXIF orientation 6 (turn it a quarter clockwise to see it).
            var photo = new Texture2D(4, 2, TextureFormat.RGB24, false);
            photo.SetPixels32(Enumerable.Repeat(new Color32(200, 40, 40, 255), 8).ToArray());
            photo.Apply();
            var jpeg = photo.EncodeToJPG(95).ToList();
            Object.DestroyImmediate(photo);
            jpeg.InsertRange(2, new byte[] { 0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8,
                0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0 });
            Assert.That(GalleryPictures.ExifOrientation(jpeg.ToArray()), Is.EqualTo(6));
            string file = Path.Combine(Path.GetTempPath(), "MyAvatarGalleryPhoto-" + System.Guid.NewGuid().ToString("N") + ".jpg");
            File.WriteAllBytes(file, jpeg.ToArray());
            string picture = null;
            try
            {
                picture = GalleryPictures.Import(file, false);
                byte[] stored = File.ReadAllBytes(picture);
                Assert.That(GalleryPictures.Owned(picture), Is.True);
                Assert.That(Encoding.ASCII.GetString(stored).Contains("Exif"), Is.False);
                var texture = new Texture2D(2, 2);
                Assert.That(texture.LoadImage(stored), Is.True);
                Assert.That(new[] { texture.width, texture.height }, Is.EqualTo(new[] { 2, 4 }));
                Object.DestroyImmediate(texture);
            }
            finally
            {
                File.Delete(file);
                GalleryPictures.Delete(picture);
            }

            // Turning keeps each pixel where it is seen: the stored left one ends on top (rows count from the bottom).
            var pair = new Texture2D(2, 1, TextureFormat.RGBA32, false);
            pair.SetPixels32(new[] { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255) });
            pair.Apply();
            var upright = GalleryPictures.Upright(pair, 6);
            Assert.That(new[] { upright.width, upright.height }, Is.EqualTo(new[] { 1, 2 }));
            Assert.That((Color32)upright.GetPixel(0, 1), Is.EqualTo(new Color32(255, 0, 0, 255)));
            Assert.That((Color32)upright.GetPixel(0, 0), Is.EqualTo(new Color32(0, 255, 0, 255)));
            Object.DestroyImmediate(upright);
        }
    }
}
