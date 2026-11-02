using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // A published item asks for the oldest VRChat SDK its content works with, not the creator's own version: N4N0 1.0.19,
    // built with SDK 3.10.3, refused every project on 3.7.x although its local-only contact senders only need 3.7.6.
    public sealed class GalleryVrchatSdkFloorTests
    {
        [Test]
        public void TheNewestFeatureUsedSetsTheMinimum()
        {
            Assert.AreEqual(GalleryPackager.OldestVrchatSdk, GalleryPackager.SdkFloor(false, false, false));
            Assert.AreEqual("3.7.6", GalleryPackager.SdkFloor(localSender: true, contentTypes: false, preventRepeats: false));
            Assert.AreEqual("3.10.0", GalleryPackager.SdkFloor(localSender: true, contentTypes: true, preventRepeats: false));
            Assert.AreEqual("3.10.2", GalleryPackager.SdkFloor(localSender: true, contentTypes: true, preventRepeats: true));
        }

        [Test]
        public void OnlyTheVrchatSdkRangesAreLoweredAndNeverRaised()
        {
            var dependencies = new List<GalleryDependency>
            {
                new GalleryDependency { id = "com.vrchat.base", range = ">=3.10.3" },
                new GalleryDependency { id = "com.vrchat.avatars", range = ">=3.10.3" },
                new GalleryDependency { id = "com.vrcfury.vrcfury", range = ">=1.1434.0" },
            };
            GalleryPackager.LowerVrchatSdk(dependencies, "3.7.6");
            Assert.AreEqual(">=3.7.6", dependencies[0].range);
            Assert.AreEqual(">=3.7.6", dependencies[1].range);
            Assert.AreEqual(">=1.1434.0", dependencies[2].range, "other packages keep the creator's version");

            var older = new List<GalleryDependency> { new GalleryDependency { id = "com.vrchat.avatars", range = ">=3.7.2" } };
            GalleryPackager.LowerVrchatSdk(older, "3.10.2");
            Assert.AreEqual(">=3.7.2", older[0].range, "a creator on an older SDK than the floor keeps their range");
        }
    }
}
