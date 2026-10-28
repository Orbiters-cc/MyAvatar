using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // What the gallery keeps in Library and reuses: a picture kept on disk loads again once its texture is gone, a kept
    // part variant is used only with the bytes Orbiters published (its size read first), the parts of one item apply one
    // after the other, and a publishing draft's ids stay with the server and account they came from. Library is a folder
    // of the test's own.
    public sealed class GalleryCacheTests
    {
        private string library;

        [SetUp] public void SetUp()
        {
            library = Path.Combine(Path.GetTempPath(), "MyAvatarCache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(library);
            LibraryStore.Override = library;
        }

        [TearDown] public void TearDown()
        {
            LibraryStore.Override = null;
            if (Directory.Exists(library)) Directory.Delete(library, true);
        }

        [Test] public void APictureKeptOnDiskLoadsAgainOnceItsTextureIsGone()
        {
            string url = "http://127.0.0.1:9/picture-" + Guid.NewGuid().ToString("N") + ".png";
            string name;
            using (var sha = SHA1.Create()) name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
            string folder = Path.Combine(library, "Gallery", "Images");
            Directory.CreateDirectory(folder);
            var source = new Texture2D(4, 4);
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), source.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(source);

            var first = GalleryImages.LoadAsync(url);
            Assert.That(first.IsCompleted, Is.True, "read from disk at once");
            Assert.That(first.Result != null, Is.True);
            UnityEngine.Object.DestroyImmediate(first.Result);
            var second = GalleryImages.LoadAsync(url);
            Assert.That(second.IsCompleted && second.Result != null, Is.True, "not the finished load of the texture destroyed since");
            UnityEngine.Object.DestroyImmediate(second.Result);
        }

        [Test] public void AKeptVariantIsUsedOnlyWithThePublishedBytes()
        {
            string file = Path.Combine(library, "variant.zip");
            var bytes = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(file, bytes);
            Assert.That(PartVariants.Kept(file, PartVariant.Hash(bytes)), Is.EqualTo(bytes));
            Assert.That(PartVariants.Kept(file, PartVariant.Hash(new byte[] { 9 })), Is.Null, "other bytes");
            Assert.That(File.Exists(file), Is.False, "and forgotten");
            using (var stream = File.Create(file)) stream.SetLength(ItemParts.MaxZipBytes + 1);
            Assert.That(PartVariants.Kept(file, PartVariant.Hash(bytes)), Is.Null, "larger than a variant may be, never read");
            Assert.That(File.Exists(file), Is.False);

            var unsigned = PartVariants.DownloadAsync(new VariantInfo { id = 5 }, null, System.Threading.CancellationToken.None);
            Assert.That(unsigned.IsFaulted, Is.True, "a variant without its checksum is never applied");
            Assert.That(unsigned.Exception.InnerException, Is.TypeOf<InvalidOperationException>());
        }

        [UnityTest] public IEnumerator TheAppliesOfOneItemRunOneAfterTheOther()
        {
            var order = new List<string>();
            var gate = new TaskCompletionSource<bool>();
            var first = PartVariants.AfterItemApplies(-4242, async () => { order.Add("first"); await gate.Task; order.Add("first done"); });
            var second = PartVariants.AfterItemApplies(-4242, () => { order.Add("second"); return Task.CompletedTask; });
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(order, Is.EqualTo(new[] { "first" }), "the second waits for the first");
            gate.SetResult(true);
            double until = EditorApplication.timeSinceStartup + 10;
            while (!second.IsCompleted && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.That(order, Is.EqualTo(new[] { "first", "first done", "second" }));
            Assert.That(first.IsCompleted, Is.True);
        }

        [Test] public void ADraftsIdsStayWithTheServerAndAccountTheyCameFrom()
        {
            var draft = new GalleryCreatorDraft { name = "Hat", version = "1.2.0" };
            draft.Target(41);
            draft.UseRelease(7);
            draft.variants[0].MarkUploaded(3, 7);
            string key = draft.idempotencyKey;
            Assert.That(draft.Bind(draft.context), Is.False, "the same server and account");
            Assert.That(draft.Release, Is.EqualTo(7));

            Assert.That(draft.Bind("https://elsewhere.example/|5"), Is.True);
            Assert.That(draft.assetId, Is.EqualTo(0));
            Assert.That(draft.Release, Is.EqualTo(0));
            Assert.That(draft.variants[0].uploadedId, Is.EqualTo(0));
            Assert.That(draft.idempotencyKey, Is.Not.EqualTo(key));
            Assert.That(draft.name, Is.EqualTo("Hat"), "what was written stays");
            Assert.That(draft.version, Is.EqualTo("1.2.0"));
            Assert.That(draft.messageError, Is.True);
            Assert.That(draft.Bind("https://elsewhere.example/|5"), Is.False, "bound to it now");
        }
    }
}
