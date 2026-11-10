using NUnit.Framework;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // The one registry of work on an avatar (architecture review MAC-17), with the rules the three lock sets had: one
    // Inspector operation and one accessory drop at a time per avatar, installations side by side, other avatars free.
    public sealed class AvatarWorkTests
    {
        private GameObject a, b;

        [SetUp] public void SetUp() { a = new GameObject("A"); b = new GameObject("B"); }
        [TearDown] public void TearDown() { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }

        [Test] public void EachKindHoldsAsTheOldLocksDid()
        {
            MyAvatar first = a.AddComponent<MyAvatar>(), second = b.AddComponent<MyAvatar>();
            Assert.That(AvatarWork.Busy(first), Is.False);
            var inspector = AvatarWork.TryEnter(first, AvatarWork.Kind.Inspector);
            Assert.That(inspector, Is.Not.Null);
            Assert.That(AvatarWork.TryEnter(first, AvatarWork.Kind.Inspector), Is.Null, "one Inspector operation at a time");
            Assert.That(AvatarWork.Busy(first), Is.True);
            Assert.That(AvatarWork.Installing(first), Is.False, "an Inspector operation alone installs nothing");
            Assert.That(AvatarWork.Busy(second), Is.False, "other avatars stay free");

            // An accessory drop runs inside the Inspector's operation.
            var accessory = AvatarWork.TryEnter(first, AvatarWork.Kind.Accessory);
            Assert.That(accessory, Is.Not.Null);
            Assert.That(AvatarWork.TryEnter(first, AvatarWork.Kind.Accessory), Is.Null);
            Assert.That(AccessoryService.Busy(first), Is.True);

            // Installations overlap.
            var one = AvatarWork.TryEnter(first, AvatarWork.Kind.Gallery);
            var two = AvatarWork.TryEnter(first, AvatarWork.Kind.Gallery);
            Assert.That(one, Is.Not.Null); Assert.That(two, Is.Not.Null);
            one.Dispose();
            Assert.That(Gallery.GalleryInstaller.Busy(first), Is.True, "the other installation still runs");
            two.Dispose(); two.Dispose();
            Assert.That(Gallery.GalleryInstaller.Busy(first), Is.False, "a hold released twice counts once");

            accessory.Dispose();
            Assert.That(AccessoryService.Busy(first), Is.False);
            Assert.That(AvatarWork.Busy(first), Is.True);
            inspector.Dispose();
            Assert.That(AvatarWork.Busy(first), Is.False);
            Assert.That(AvatarWork.TryEnter(null, AvatarWork.Kind.Inspector), Is.Null);
        }
    }
}
