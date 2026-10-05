using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The creator's draft survives script reloads in SessionState; loading it must give back the same packages, not the
    // saved ones plus the defaults. The session's own draft is kept aside while the test runs.
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
    }
}
