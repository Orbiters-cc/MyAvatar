using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Expressions;
using VRC.SDK3.Avatars.Components;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>The menu editor's items left out of the upload, the default menu, and items' settings, as saved.</summary>
    public sealed class MenuRemovalTests
    {
        private static MenuNode Node(MenuBuildResult built, string name) => MenuTree.Walk(built.Root).First(e => e.Node.Name == name).Node;

        [Test]
        public void ADeletedItemWaitsOutOfTheMenuAndIsFoundAgainWhereItWas()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var hat = Node(built, "Hat");
                var delete = new MenuEdit { kind = MenuEditKind.Delete, id = hat.Id };

                var draft = MenuDraft.Apply(built, new List<MenuEdit> { delete });

                Assert.False(MenuTree.Walk(draft.Visible()).Any(e => e.Node.Id == hat.Id), "the editor and the upload show it no more");
                Assert.True(MenuTree.Walk(draft.Root).Any(e => e.Node.Id == hat.Id), "until the upload it stays the avatar's");
                CollectionAssert.AreEqual(new[] { hat.Id }, draft.Left().Select(e => e.Node.Id));
                var removal = MenuSave.Removals.Of(draft).Items.Single();
                CollectionAssert.AreEqual(new[] { "Clothes", "Hat" }, removal.path);
                Assert.AreEqual(hat.Id, MenuPaths.Find(built.Root, removal.path, removal.nth).Id);

                var back = MenuDraft.Apply(built, new List<MenuEdit> { delete, new MenuEdit { kind = MenuEditKind.Restore, id = hat.Id } });
                Assert.AreEqual(0, back.Left().Count, "a click puts it back");
            }
        }

        [Test]
        public void TheDefaultMenuLeavesTheEditorAsOneGroup()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var draft = MenuDraft.Apply(built, new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.DefaultMenu, flag = true } });
                Assert.True(draft.DefaultRemoved);
                Assert.AreEqual(0, draft.Visible().Items.Count, "this avatar's menu is all its own");
                Assert.AreEqual(built.Root.Items.Count, draft.Left().Count);
                Assert.True(MenuSave.Removals.Of(draft).DefaultMenu);
            }
        }

        [Test]
        public void SavingKeepsWhatTheUploadLeavesOutOnTheAvatar()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var settings = fixture.Root.AddComponent<MyAvatar>();
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var hat = Node(built, "Hat");
                var edits = new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Delete, id = hat.Id } };

                var outcome = MenuSave.Save(fixture.Root, edits, null, MenuSave.Removals.Of(MenuDraft.Apply(built, edits)));

                Assert.True(outcome.Saved, outcome.Message);
                Assert.AreEqual(1, settings.menuRemovals.Count);
                var again = MenuSave.SavedRemovals(fixture.Root, MenuBuild.Run(MenuInputs.Read(fixture.Root)), out bool defaultMenu);
                CollectionAssert.AreEqual(new[] { hat.Id }, again, "the editor finds it left out next time");
                Assert.False(defaultMenu);
            }
        }

        [Test]
        public void AnItemsSettingsAreSavedWhereTheyLive()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var hat = Node(built, "Hat");
                var info = MenuSave.Info(fixture.Root, hat, null);
                CollectionAssert.AreEqual(new[] { ControlType.Toggle, ControlType.Button }, info.Types);
                Assert.True(info.DefaultEditable && info.SavedEditable && info.SyncedEditable);
                Assert.AreEqual("Hat", info.Parameter);
                var edits = new List<MenuEdit>
                {
                    new MenuEdit { kind = MenuEditKind.Type, id = hat.Id, index = (int)ControlType.Button },
                    new MenuEdit { kind = MenuEditKind.Default, id = hat.Id, number = 1 },
                    new MenuEdit { kind = MenuEditKind.Saved, id = hat.Id, flag = false },
                    new MenuEdit { kind = MenuEditKind.Synced, id = hat.Id, flag = false },
                };
                var draft = MenuDraft.Apply(built, edits);
                var shown = MenuSave.Info(fixture.Root, MenuTree.Index(draft.Root)[hat.Id].Node, draft);
                Assert.AreEqual(1f, shown.Default, "the editor shows the change before it is saved");

                var outcome = MenuSave.Save(fixture.Root, edits);

                Assert.True(outcome.Saved, outcome.Message);
                Assert.AreEqual(ControlType.Button, hat.Origin.Control.type);
                var parameter = fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionParameters.parameters.Single(p => p.name == "Hat");
                Assert.AreEqual(1f, parameter.defaultValue);
                Assert.False(parameter.saved);
                Assert.False(parameter.networkSynced);
            }
        }
    }
}
