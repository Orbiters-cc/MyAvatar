using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // An asset's likes and reviews against a fake Orbiters: what shows at once, what a failure puts back (only what it
    // changed), likes sent again after a reload, the typed text kept when an edit fails, the two-press delete, and the
    // gallery's text, versions, pictures and packages rules. Nothing reaches a server.
    public sealed class GalleryReviewTests
    {
        private sealed class FakeApi : GalleryReviewState.IApi
        {
            public AssetInteractions Loaded;
            public Func<bool, Task<LikeState>> Like;
            public Func<int, bool, Task<LikeState>> ReviewLike;
            public Func<int, Task<ReviewCount>> Deleted;
            public Func<string, Task<GalleryReview>> Edited;
            public readonly List<string> Sent = new List<string>();

            public Task<AssetInteractions> Load(int assetId, int offset) { Sent.Add("load"); return Task.FromResult(Copy(Loaded)); }
            public Task<LikeState> SetLike(int assetId, bool liked) { Sent.Add("like " + liked); return Like(liked); }
            public Task<LikeState> SetReviewLike(int assetId, int reviewId, bool liked) { Sent.Add("review " + reviewId + " " + liked); return ReviewLike(reviewId, liked); }
            public Task<GalleryReview> Post(int assetId, string content) => Task.FromResult(new GalleryReview { id = 99, content = content, mine = true });
            public Task<GalleryReview> Edit(int assetId, int reviewId, string content) { Sent.Add("edit " + reviewId); return Edited(content); }
            public Task<ReviewCount> Delete(int assetId, int reviewId) { Sent.Add("delete " + reviewId); return Deleted(reviewId); }

            // Each load answers new objects, as Orbiters does.
            private static AssetInteractions Copy(AssetInteractions data) => new AssetInteractions
            {
                assetId = data.assetId, likeCount = data.likeCount, reviewCount = data.reviewCount, likedByCurrentUser = data.likedByCurrentUser, hasMore = data.hasMore,
                viewer = new ReviewViewer { signedIn = data.viewer.signedIn, canReview = data.viewer.canReview, canLike = data.viewer.canLike },
                reviews = data.reviews.Select(r => new GalleryReview
                {
                    id = r.id, content = r.content, likeCount = r.likeCount, likedByCurrentUser = r.likedByCurrentUser, mine = r.mine,
                    author = new ReviewAuthor { username = r.author?.username, avatarUrl = r.author?.avatarUrl },
                }).ToList(),
            };
        }

        private GalleryReviewState.IApi saved;
        private FakeApi api;
        // The caches and the project's pictures live in a folder of the test's own: the user's Library and Assets stay as they are.
        private string scratch, project;

        [SetUp]
        public void UseFake()
        {
            saved = GalleryReviewState.Api;
            GalleryReviewState.Api = api = new FakeApi { Loaded = Interactions() };
            scratch = Path.Combine(Path.GetTempPath(), "orbiters-review-" + Guid.NewGuid().ToString("N"));
            project = Path.Combine(scratch, "Assets");
            Directory.CreateDirectory(project);
            LibraryStore.Override = Path.Combine(scratch, "Library");
            GalleryPictures.ProjectPictures = project;
        }

        [TearDown]
        public void Restore()
        {
            GalleryReviewState.Api = saved;
            LibraryStore.Override = null;
            GalleryPictures.ProjectPictures = "Assets";
            GalleryPictures.UnloadAll();
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }

        private static AssetInteractions Interactions() => new AssetInteractions
        {
            assetId = 1, likeCount = 3, reviewCount = 2, viewer = new ReviewViewer { signedIn = true, canReview = true },
            reviews = new List<GalleryReview>
            {
                new GalleryReview { id = 7, content = "Lovely", likeCount = 0, author = new ReviewAuthor { username = "a" } },
                new GalleryReview { id = 8, content = "Fits well", likeCount = 2, author = new ReviewAuthor { username = "b" } },
            },
        };

        private static GalleryReviewState Loaded()
        {
            var state = new GalleryReviewState(1);
            Assert.That(state.LoadAsync(0).IsCompleted, Is.True, "the fake answers at once");
            return state;
        }

        private static IEnumerator Until(Func<bool> done, double seconds = 10)
        {
            double deadline = EditorApplication.timeSinceStartup + seconds;
            while (!done() && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(done(), Is.True, "Timed out.");
        }

        [Test]
        public void ALikeIsSentAgainAfterAReloadShowedItUnliked()
        {
            api.ReviewLike = (id, liked) => Task.FromResult(new LikeState { liked = liked, likeCount = liked ? 1 : 0 });
            var state = Loaded();
            state.ToggleReviewLike(7);
            Assert.That(state.Find(7).likedByCurrentUser, Is.True);
            // Orbiters lost it (or it was taken back on the website): the reload shows it unliked.
            Assert.That(state.LoadAsync(0).IsCompleted, Is.True);
            Assert.That(state.Find(7).likedByCurrentUser, Is.False);

            state.ToggleReviewLike(7);

            CollectionAssert.AreEqual(new[] { "load", "review 7 True", "load", "review 7 True" }, api.Sent, "the like goes out again, not taken as already sent");
            Assert.That(state.Find(7).likedByCurrentUser, Is.True);
            Assert.That(state.Find(7).likeCount, Is.EqualTo(1));
        }

        [Test]
        public void AFailedLikePutsBackTheLikeAlone()
        {
            api.Like = liked => Task.FromException<LikeState>(new InvalidOperationException("offline"));
            var state = Loaded();
            state.Find(8).content = "Edited meanwhile";

            state.ToggleAssetLike();

            Assert.That(state.Data.likedByCurrentUser, Is.False);
            Assert.That(state.Data.likeCount, Is.EqualTo(3));
            Assert.That(state.Find(8).content, Is.EqualTo("Edited meanwhile"), "nothing else goes back");
            StringAssert.Contains("offline", state.Message);
        }

        [UnityTest]
        public IEnumerator QuickPressesSendOnlyTheLatestWish()
        {
            var answers = new List<TaskCompletionSource<LikeState>>();
            api.Like = liked => { var answer = new TaskCompletionSource<LikeState>(); answers.Add(answer); return answer.Task; };
            var state = Loaded();
            state.ToggleAssetLike();
            state.ToggleAssetLike();
            state.ToggleAssetLike();
            Assert.That(state.Data.likedByCurrentUser, Is.True, "shown at once");
            Assert.That(answers.Count, Is.EqualTo(1), "one send at a time");

            answers[0].SetResult(new LikeState { liked = true, likeCount = 10 });
            yield return Until(() => state.Data.likeCount == 10);

            Assert.That(answers.Count, Is.EqualTo(1), "Orbiters holds the latest wish: nothing more to send");
            Assert.That(state.Data.likedByCurrentUser, Is.True);
        }

        [UnityTest]
        public IEnumerator AFailedDeleteBringsBackTheReviewAloneAtItsPlace()
        {
            var answer = new TaskCompletionSource<ReviewCount>();
            api.Deleted = id => answer.Task;
            api.ReviewLike = (id, liked) => Task.FromResult(new LikeState { liked = liked, likeCount = 5 });
            var state = Loaded();
            var first = state.Find(7);

            var deleting = state.DeleteAsync(first);
            Assert.That(state.Find(7), Is.Null, "gone at once");
            Assert.That(state.Data.reviewCount, Is.EqualTo(1));
            state.ToggleReviewLike(8);
            answer.SetException(new InvalidOperationException("refused"));
            yield return Until(() => deleting.IsCompleted);

            Assert.That(deleting.Result, Is.False);
            CollectionAssert.AreEqual(new[] { 7, 8 }, state.Data.reviews.Select(r => r.id), "back where it was");
            Assert.That(state.Data.reviewCount, Is.EqualTo(2));
            Assert.That(state.Find(8).likedByCurrentUser, Is.True, "the like given meanwhile stays");
            Assert.That(state.Find(8).likeCount, Is.EqualTo(5));
        }

        [Test]
        public void AFailedEditKeepsWhatWasTypedAndTheReviewItsSavedText()
        {
            api.Loaded.reviews[0].mine = true;
            api.Edited = content => Task.FromException<GalleryReview>(new InvalidOperationException("too long"));
            var reviews = new GalleryReviews(new GalleryAsset { id = 1, likeCount = 3 }, null);
            Assert.That(reviews.State.LoadAsync(0).IsCompleted, Is.True);
            var review = reviews.State.Find(7);

            reviews.Edit(review);
            var field = reviews.Query<TextField>().ToList().Single(f => f.value == "Lovely");
            field.SetValueWithoutNotify("Lovely, and the fit is great");
            reviews.Typed(field.value);
            // The list shows again while editing (a reload): the field stays as it is (its text, cursor and input method).
            Assert.That(reviews.State.LoadAsync(0).IsCompleted, Is.True);
            Assert.That(reviews.Query<TextField>().ToList(), Does.Contain(field), "the same field, kept");
            reviews.Save(review, field.value);
            CollectionAssert.Contains(api.Sent, "edit 7");

            Assert.That(reviews.State.Find(7).content, Is.EqualTo("Lovely"), "not saved: the review shows its saved text");
            Assert.That(Fields(reviews), Does.Contain("Lovely, and the fit is great"), "the editor opens again with what was typed");
            StringAssert.Contains("too long", reviews.State.Message);
        }

        private static List<string> Fields(VisualElement root) => root.Query<TextField>().ToList().Select(f => f.value).ToList();

        [Test]
        public void ReviewTextAndNamesAreShownAsWritten()
        {
            api.Loaded.reviews[0].content = "<color=red>snake_case</color> <3 **as is**";
            api.Loaded.reviews[0].author.username = "<b>Name</b>";
            var reviews = new GalleryReviews(new GalleryAsset { id = 1 }, null);
            Assert.That(reviews.State.LoadAsync(0).IsCompleted, Is.True);
            var labels = reviews.Query<Label>().ToList();
            var text = labels.Single(l => l.ClassListContains("review__text") && l.text.Contains("snake_case"));
            Assert.That(text.text, Is.EqualTo("<color=red>snake_case</color> <3 **as is**"));
            Assert.That(text.enableRichText, Is.False);
            Assert.That(labels.Single(l => l.ClassListContains("review__author") && l.text == "<b>Name</b>").enableRichText, Is.False);
        }

        [Test]
        public void ASecondPressConfirmsOnlyAfterADelayAndTheArmedStateEnds()
        {
            double now = 0;
            int confirmed = 0;
            var shown = new List<bool>();
            var press = new GalleryUI.TwoPress(null, shown.Add, () => confirmed++, () => now);

            press.Press();
            now = .03; press.Release(); press.Press();
            Assert.That(confirmed, Is.EqualTo(0), "the second click of a double-click doesn't confirm");
            now = .55; press.Press();
            Assert.That(confirmed, Is.EqualTo(0), "longer than Windows' double-click");
            now = 1; press.Press();
            Assert.That(confirmed, Is.EqualTo(1));
            Assert.That(press.Armed, Is.False);

            now = 5; press.Press();
            now = 6; press.Press();
            Assert.That(confirmed, Is.EqualTo(1), "a held key repeats without a release: no confirm");
            now = 5 + GalleryUI.TwoPress.ArmedFor + 1;
            Assert.That(press.Armed, Is.False, "armed for a few seconds only");
            press.Press();
            Assert.That(confirmed, Is.EqualTo(1), "a press after that arms again");
            CollectionAssert.AreEqual(new[] { true, false, true }, shown, "arming again while shown armed shows nothing twice");
        }

        [Test]
        public void MarkdownLosesItsMarkupOnly()
        {
            Assert.That(GallerySheets.PlainText("# Title\n**Bold** and *soft* and `code`, <b>tagged</b>"), Is.EqualTo("Title\nBold and soft and code, tagged"));
            Assert.That(GallerySheets.PlainText("snake_case_name stays, 2 * 3 * 4, #1 fan <3, a < b > c"), Is.EqualTo("snake_case_name stays, 2 * 3 * 4, #1 fan <3, a < b > c"));
            Assert.That(GallerySheets.PlainText("[the site](https://orbiters.cc) ![shot](x.png)_done_"), Is.EqualTo("the site done"));
        }

        [Test]
        public void PreReleasesCompareLikeSemver()
        {
            Assert.That(GalleryUI.CompareVersions("1.0.0-beta.10", "1.0.0-beta.2"), Is.GreaterThan(0));
            Assert.That(GalleryUI.CompareVersions("1.0.0-alpha", "1.0.0-alpha.1"), Is.LessThan(0));
            Assert.That(GalleryUI.CompareVersions("1.0.0-alpha.1", "1.0.0-alpha.beta"), Is.LessThan(0), "numbers before words");
            Assert.That(GalleryUI.CompareVersions("1.0.0-beta", "1.0.0-rc.1"), Is.LessThan(0));
            Assert.That(GalleryUI.CompareVersions("1.0.0-rc.1", "1.0.0"), Is.LessThan(0));
            Assert.That(GalleryUI.CompareVersions("1.0.0-rc.1+build.5", "1.0.0-rc.1"), Is.EqualTo(0));
        }

        [Test]
        public void MembersFacesComeOnlyFromOrbitersAndItsSignInServices()
        {
            Assert.That(GalleryImages.Allowed("https://cdn.discordapp.com/avatars/1/a.png?size=128", face: true), Is.True);
            Assert.That(GalleryImages.Allowed("https://avatars.githubusercontent.com/u/1", face: true), Is.True);
            Assert.That(GalleryImages.Allowed(OrbitersEnvironment.ApiUrl("files/serve/3"), face: true), Is.True);
            Assert.That(GalleryImages.Allowed("https://tracker.example.com/pixel.png", face: true), Is.False);
            Assert.That(GalleryImages.Allowed("https://cdn.discordapp.com.example.com/a.png", face: true), Is.False);
            Assert.That(GalleryImages.Allowed("http://example.com/a.png", face: false), Is.False);
            Assert.That(GalleryImages.Allowed("https://example.com/a.png", face: false), Is.True, "gallery pictures: any https host");
        }

        [Test]
        public void PicturesForAListingAreItsOwnOrProjectPicturesTakenIn()
        {
            string outside = Path.Combine(scratch, "home");
            Directory.CreateDirectory(outside);
            string secret = Path.Combine(outside, "id_rsa");
            File.WriteAllText(secret, "-----BEGIN OPENSSH PRIVATE KEY-----");
            Assert.Throws<ArgumentException>(() => GalleryPictures.Adopt(secret), "not a picture");
            string disguised = Path.Combine(outside, "id_rsa.png");
            File.Copy(secret, disguised);
            Assert.Throws<ArgumentException>(() => GalleryPictures.Adopt(disguised), "outside the project: refused, never read");

            var image = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            image.SetPixels32(Enumerable.Repeat(new Color32(200, 40, 40, 255), 16).ToArray()); image.Apply();
            string own = GalleryPictures.Save(image, false);
            Assert.That(GalleryPictures.Adopt(own), Is.EqualTo(Path.GetFullPath(own)), "one of the window's pictures stays as it is");

            File.WriteAllBytes(Path.Combine(project, "shot.png"), image.EncodeToPNG());
            File.WriteAllText(Path.Combine(project, "fake.png"), "not a picture");
            UnityEngine.Object.DestroyImmediate(image);
            string taken = GalleryPictures.Adopt(Path.Combine(project, "shot.png"));
            Assert.That(GalleryPictures.Owned(taken), Is.True, "a project picture is taken in as a new one");
            Assert.Throws<InvalidOperationException>(() => GalleryPictures.Adopt(Path.Combine(project, "fake.png")), "named .png, not a picture");
        }

        // MCP's configure: keys in any case reach the same fields, so the protected ones never get in; a refused picture
        // changes nothing.
        [Test]
        public void DraftChangesInAnyCaseLeaveProtectedFieldsAlone()
        {
            const string Key = "Orbiters.MyAvatar.GalleryCreatorDraft";
            string kept = SessionState.GetString(Key, null);
            try
            {
                new GalleryCreatorDraft { name = "Hat", assetId = 4 }.Save();
                var draft = GalleryDraftChanges.ApplyJson("{\"Name\":\"Cap\",\"AssetId\":99,\"Rights\":true,\"PicturesSent\":true,\"IdempotencyKey\":\"x\",\"VARIANTS\":[{\"Label\":\"PC\",\"UploadedId\":7}]}");
                Assert.That(draft.name, Is.EqualTo("Cap"));
                Assert.That(draft.assetId, Is.EqualTo(4), "the asset is chosen with existingAssetId only");
                Assert.That(draft.rights, Is.False);
                Assert.That(draft.idempotencyKey, Is.Not.EqualTo("x"));
                Assert.That(draft.variants.Single().label, Is.EqualTo("PC"));
                Assert.That(draft.variants.Single().uploadedId, Is.EqualTo(0), "what Orbiters answered stays its own");

                string secret = Path.Combine(scratch, "id_rsa.png");
                File.WriteAllText(secret, "key");
                Assert.Throws<ArgumentException>(() => GalleryDraftChanges.ApplyJson("{\"reset\":true,\"ThumbnailPath\":" + Json(secret) + "}"));
                Assert.That(GalleryCreatorDraft.Load().name, Is.EqualTo("Cap"), "refused before anything changed, reset included");
                Assert.Throws<ArgumentException>(() => GalleryDraftChanges.ApplyJson("{\"name\":\"A\",\"Name\":\"B\"}"), "the same field twice");
            }
            finally
            {
                if (kept == null) SessionState.EraseString(Key); else SessionState.SetString(Key, kept);
            }
        }

        // A JSON string of a path (backslashes escaped).
        private static string Json(string text) => "\"" + text.Replace("\\", "\\\\") + "\"";

        [Test]
        public void APictureIsMeasuredFromItsHeaderBeforeItIsDecoded()
        {
            var image = new Texture2D(5, 3, TextureFormat.RGBA32, false);
            byte[] png = image.EncodeToPNG(), jpeg = image.EncodeToJPG();
            UnityEngine.Object.DestroyImmediate(image);
            Assert.That(GalleryImages.Dimensions(png, out int w, out int h) && w == 5 && h == 3, Is.True);
            Assert.That(GalleryImages.Dimensions(jpeg, out w, out h) && w == 5 && h == 3, Is.True);
            // A PNG claiming to be 20000 px wide: refused before anything is decoded.
            png[16] = 0; png[17] = 0; png[18] = 0x4E; png[19] = 0x20;
            Assert.That(GalleryImages.Dimensions(png, out w, out _) && w == 20000, Is.True);
            Assert.That(GalleryImages.Dimensions(System.Text.Encoding.ASCII.GetBytes("GIF89a, not one of ours....."), out _, out _), Is.False);
        }

        [Test]
        public void APsdThePackageUsesGoesInAndOthersStayOut()
        {
            var wanted = new HashSet<string> { "aaaa0000aaaa0000aaaa0000aaaa0000", "bbbb0000bbbb0000bbbb0000bbbb0000" };
            // A big material read in small pieces: the reference that straddles two of them is still found.
            string material = new string(' ', 100) + "m_Texture: {fileID: 2800000, guid: aaaa0000aaaa0000aaaa0000aaaa0000, type: 3}\n";
            using (var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(material)))
                CollectionAssert.AreEquivalent(new[] { "aaaa0000aaaa0000aaaa0000aaaa0000" }, GalleryPackager.GuidsIn(stream, wanted, chunk: 128));
            Assert.That(GalleryPackager.Excluded("Assets/Hat/Hat.psd", used: true), Is.Null);
            StringAssert.Contains("nothing in the package uses", GalleryPackager.Excluded("Assets/Hat/Unused.psd", used: false));
            StringAssert.Contains("2D PSD Importer", GalleryPackager.Excluded("Assets/Hat/Big.psb", used: true));
            Assert.That(GalleryPackager.Excluded("Assets/Hat/Hat.blend", used: true), Is.EqualTo("source file"));
        }
    }
}
