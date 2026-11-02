using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Expressions;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Found by the compatibility project (MasculineCanine, 2026-11-02): an item My Avatar placed on a bone moves under that
    // bone just before VRCFury builds, so VRCFury meets its toggle where the bone is, not where the item sits in the scene.
    public sealed class MenuBuildOrderTests
    {
        [Test]
        public void AnItemsToggleSortsWhereTheBuildPutsTheItem()
        {
            if (!VrcFury.Installed || VrcFury.Find("VF.Builder.VRCFuryBuilder") == null || VrcFury.Writer == null) Assert.Ignore("VRCFury is not installed.");
            using (var fixture = new MenuFixture(many: false))
            {
                // A bone above the fixture's toggles, and an item placed on it that sits last in the scene.
                var head = new GameObject("Head");
                head.transform.SetParent(fixture.Root.transform, false);
                head.transform.SetSiblingIndex(0);
                var hat = GameObject.CreatePrimitive(PrimitiveType.Cube);
                hat.name = "Hat";
                hat.transform.SetParent(fixture.Root.transform, false);
                var attachment = hat.AddComponent<OrbitersAttachment>();
                attachment.mode = OrbitersAttachment.AttachMode.Parent;
                attachment.parent = head.transform;
                VrcFury.Writer.Toggle(hat, "Accessories/Hat", hat, true);
                CollectionAssert.IsNotEmpty(AttachmentBuild.Reparented(fixture.Root), "the build moves the hat onto its bone");

                Assert.AreEqual(MenuFixture.Build(fixture.Root), MenuFixture.Preview(fixture.Root));
            }
        }
    }
}
