using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Expressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class MenuEditorTests
    {
        // Why Save may refuse: VRCFury finds items by name, can't move a menu into itself, and copies a menu two folders open
        // when it moves one that is not in the avatar's own menu assets.
        private static bool Refusal(string message) => message.Contains("same name") || message.Contains("itself") || message.Contains("same menu") || message.Contains("already there");

        private static void NeedVrcFury()
        {
            if (!VrcFury.Installed || VrcFury.Find("VF.Builder.VRCFuryBuilder") == null) Assert.Ignore("VRCFury is not installed.");
        }

        // ---- VRCFury's path and name rules ------------------------------------------------------------------------------------

        [Test]
        public void MenuPathsSplitOnSlashesExceptEscapedOnes()
        {
            CollectionAssert.AreEqual(new[] { "Clothes", "Hat" }, MenuRules.SplitPath("Clothes/Hat"));
            CollectionAssert.AreEqual(new[] { "On/Off", "Hat" }, MenuRules.SplitPath("On\\/Off/Hat"));
            CollectionAssert.IsEmpty(MenuRules.SplitPath("  "));
            Assert.AreEqual("On\\/Off/Hat", MenuRules.JoinPath(new[] { "On/Off", "Hat" }));
        }

        [Test]
        public void NamesAreFoundExactlyThenNormalizedThenByARichTextPiece()
        {
            var page = new MenuPage();
            page.Items.Add(new MenuNode { Name = "<b>Body</b>\n<size=20>Options" });
            page.Items.Add(new MenuNode { Name = "clothes  " });
            page.Items.Add(new MenuNode { Name = "Clothes", Type = ControlType.SubMenu });
            Assert.AreSame(page.Items[2], MenuRules.Find(page, "Clothes").Single(), "an exact name wins");
            Assert.AreEqual(2, MenuRules.Find(page, "CLOTHES").Count, "else names match without case and extra spaces");
            Assert.AreSame(page.Items[0], MenuRules.Find(page, "body").Single(), "else a rich text piece");
            Assert.AreSame(page.Items[2], MenuRules.Find(page, "clothes", MenuRules.IsFolder).Single(), "folders only");
            Assert.AreEqual("body options", MenuRules.Normalize(page.Items[0].Name));
        }

        [Test]
        public void MenusOfMoreThanEightItemsGetNextPages()
        {
            MenuPage Page(int count)
            {
                var page = new MenuPage();
                for (int i = 0; i < count; i++) page.Items.Add(new MenuNode { Id = "i" + i, Name = "Item " + i });
                return page;
            }
            Assert.AreEqual(8, MenuBuild.Paginate(Page(8), "Next", null).Items.Count, "8 items fit");
            var nine = MenuBuild.Paginate(Page(9), "More", null);
            Assert.AreEqual(8, nine.Items.Count);
            Assert.AreEqual("More", nine.Items[7].Name);
            Assert.AreEqual(ControlType.SubMenu, nine.Items[7].Type);
            CollectionAssert.AreEqual(new[] { "Item 7", "Item 8" }, nine.Items[7].Page.Items.Select(n => n.Name));
            var many = MenuBuild.Paginate(Page(20), "Next", null);
            var second = many.Items[7].Page;
            Assert.AreEqual(8, second.Items.Count, "page 2 holds 7 items and Next");
            Assert.AreEqual(6, second.Items[7].Page.Items.Count, "page 3 the rest");
        }

        [TestCase("ABCD", "BCDA", 1)]
        [TestCase("ABCD", "DABC", 1)]
        [TestCase("ABCDEF", "BADCFE", 3)]
        [TestCase("ABC", "ABC", 0)]
        public void ReordersTakeFewSteps(string from, string to, int steps)
        {
            var order = from.Select(c => c.ToString()).ToList();
            var wanted = to.Select(c => c.ToString()).ToList();
            var plan = MenuSave.Steps(order, wanted);
            Assert.AreEqual(steps, plan.Count);
            Assert.AreEqual(to, Apply(order, plan));
        }

        [Test]
        public void SameNamedItemsStayWhereTheOthersAreMovedAround()
        {
            // "x" and "y" share a name in the menu: only a, b and c can be named by Reorder Menu Item.
            var order = new List<string> { "x", "a", "y", "b", "c" };
            var wanted = new List<string> { "a", "x", "c", "y", "b" };
            var plan = MenuSave.Steps(order, wanted, id => id.Length == 1 && "abc".Contains(id));
            Assert.NotNull(plan);
            Assert.False(plan.Any(s => s.Item == "x" || s.Item == "y"));
            Assert.AreEqual(string.Concat(wanted), Apply(order, plan));
            Assert.Null(MenuSave.Steps(new[] { "x", "y" }, new[] { "y", "x" }, _ => false), "same-named items can't swap");
        }

        private static string Apply(List<string> order, List<(string Item, int Position)> plan)
        {
            var list = order.ToList();
            foreach (var (item, position) in plan)
            {
                list.Remove(item);
                list.Insert(Mathf.Clamp(position, 0, list.Count), item);
            }
            return string.Concat(list);
        }

        // ---- The preview: exactly what VRCFury builds ------------------------------------------------------------------------

        [Test]
        public void ThePreviewIsWhatVrcFuryBuilds()
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture())
                Assert.AreEqual(MenuFixture.Build(fixture.Root), MenuFixture.Preview(fixture.Root));
            using (var fixture = new MenuFixture(many: false))
                Assert.AreEqual(MenuFixture.Build(fixture.Root), MenuFixture.Preview(fixture.Root));
        }

        [Test]
        public void WithoutVrcFuryThePreviewIsTheDescriptorsMenu()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                Assert.False(built.VrcFury);
                Assert.AreEqual(2, built.Root.Items.Count(n => n.Name == "Effects"), "nothing merges same-named folders");
                Assert.AreEqual(MenuFixture.Dump(fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionsMenu), MenuFixture.Dump(built.Root));
            }
        }

        // The avatars of the active scene VRCFury builds (Novabeast and its MCB sliders in MCB Test), on hidden copies. An
        // avatar VRCFury itself can't build (a broken setup) is left out. It depends on the scene open: run it by hand.
        [Test, Explicit("Compares the avatars of the scene open now: run it by hand.")]
        public void ThePreviewOfTheOpenAvatarsIsWhatVrcFuryBuilds()
        {
            NeedVrcFury();
            int compared = 0;
            var skipped = new List<string>();
            foreach (var top in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (var descriptor in top.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                if (!MenuInputs.Read(descriptor.gameObject).UsesVrcFury) continue;
                string built;
                try { built = MenuFixture.Build(descriptor.gameObject); }
                catch (Exception ex) { skipped.Add(descriptor.name + " (" + ex.Message + ")"); continue; }
                Assert.AreEqual(built, MenuFixture.Preview(descriptor.gameObject), descriptor.name);
                compared++;
            }
            if (compared == 0) Assert.Ignore("No avatar VRCFury builds is open." + (skipped.Count > 0 ? " Not buildable: " + string.Join(", ", skipped) : ""));
        }

        [Test]
        public void ThePreviewIsInstant()
        {
            using (var fixture = new MenuFixture())
            {
                MenuFixture.Preview(fixture.Root);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 20; i++) MenuFixture.Preview(fixture.Root);
                Assert.Less(watch.Elapsed.TotalMilliseconds / 20, 10, "a rebuild takes a few milliseconds at most");
            }
        }

        // ---- Edits -------------------------------------------------------------------------------------------------------

        [Test]
        public void EditsRenameMoveAndChangeIconsOverTheBuiltMenu()
        {
            using (var fixture = new MenuFixture())
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var index = MenuTree.Index(built.Root);
                string Id(string name) => index.Values.First(e => e.Node.Name == name).Node.Id;
                var props = index.Values.First(e => e.Node.Name == "Props").Node;
                var edits = new List<MenuEdit>
                {
                    new MenuEdit { kind = MenuEditKind.Rename, id = Id("Hat"), text = "Cap" },
                    new MenuEdit { kind = MenuEditKind.Icon, id = Id("Hat"), icon = fixture.IconC },
                    new MenuEdit { kind = MenuEditKind.Move, id = Id("Hat"), parent = MenuTree.RootId, index = 0 },
                };
                // Everything out of a folder VRCFury makes for menu paths: it goes with its last item.
                foreach (var item in props.Page.Items) edits.Add(new MenuEdit { kind = MenuEditKind.Move, id = item.Id, parent = MenuTree.RootId, index = 1 });
                edits.Add(new MenuEdit { kind = MenuEditKind.Rename, id = "gone", text = "x" });
                var draft = MenuDraft.Apply(built, edits);
                Assert.AreEqual("Cap", draft.Root.Items[0].Name);
                Assert.AreSame(fixture.IconC, draft.Root.Items[0].Icon);
                Assert.False(draft.Root.Items.Any(n => n.Name == "Props"), "the emptied Props folder goes");
                Assert.True(draft.Dropped.Contains(props.Id));
                Assert.AreEqual(1, draft.Stale, "an edit of an item that is gone");
                Assert.AreEqual("Hat", MenuTree.Index(built.Root).Values.First(e => e.Node.Id == Id("Hat")).Node.Name, "the built menu is left as it was");
            }
        }

        [Test]
        public void EachChangeIsAnUndoStep()
        {
            var state = MenuEditState.Create();
            try
            {
                state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = "a", text = "One" }, "Rename A");
                Undo.IncrementCurrentGroup();
                state.Add(new MenuEdit { kind = MenuEditKind.Move, id = "a", parent = MenuTree.RootId, index = 1 }, "Move A");
                Undo.IncrementCurrentGroup();
                Assert.AreEqual(2, state.edits.Count);
                Undo.PerformUndo();
                Assert.AreEqual(1, state.edits.Count, "Ctrl+Z takes the last change back");
                Undo.PerformRedo();
                Assert.AreEqual(2, state.edits.Count, "Ctrl+Y brings it back");
                // Renaming the same item again is one change, and one Undo step (a slider dragged is one too).
                state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = "b", text = "B1" }, "Rename B");
                state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = "b", text = "B2" }, "Rename B");
                state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = "b", text = "B3" }, "Rename B");
                Assert.AreEqual(3, state.edits.Count);
                Assert.AreEqual("B3", state.edits[2].text);
                Undo.PerformUndo();
                Assert.AreEqual(2, state.edits.Count, "one Undo takes the whole rename back");
                Undo.PerformRedo();
                Assert.AreEqual("B3", state.edits[2].text);
                // After an Undo or a Redo, the same change again is a step of its own.
                state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = "b", text = "B4" }, "Rename B");
                Undo.PerformUndo();
                Assert.AreEqual("B3", state.edits[2].text, "Undo takes back only the rename made after the Redo");
            }
            finally
            {
                Undo.ClearUndo(state);
                Object.DestroyImmediate(state);
            }
        }

        // ---- Save: where each change is written, and that VRCFury then builds exactly the edited menu -------------------------

        [Test]
        public void SavedEditsGoWhereTheyBelongAndBuildTheEditedMenu()
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture())
            {
                var root = fixture.Root;
                var descriptor = root.GetComponent<VRCAvatarDescriptor>();
                var built = MenuBuild.Run(MenuInputs.Read(root));
                var index = MenuTree.Index(built.Root);
                MenuNode Item(string name) => index.Values.First(e => e.Node.Name == name).Node;
                string original = MenuFixture.Preview(root);
                var edits = new List<MenuEdit>
                {
                    // The avatar's own menu assets: renamed, given an icon, reordered, moved from one to another.
                    new MenuEdit { kind = MenuEditKind.Rename, id = Item("Glasses").Id, text = "Sunglasses" },
                    new MenuEdit { kind = MenuEditKind.Icon, id = Item("Lights").Id, icon = fixture.IconA },
                    new MenuEdit { kind = MenuEditKind.Move, id = Item("Lights").Id, parent = Item("Clothes").Id, index = 0 },
                    // The avatar's own VRCFury toggles: renamed, moved into a menu asset's folder, given an icon.
                    new MenuEdit { kind = MenuEditKind.Rename, id = Item("Sword").Id, text = "Saber" },
                    // A toggle a Reorder Menu Item names by its path: renamed with a Move Menu Item instead.
                    new MenuEdit { kind = MenuEditKind.Rename, id = Item("Shield").Id, text = "Buckler" },
                    new MenuEdit { kind = MenuEditKind.Move, id = Item("Glow").Id, parent = Item("Loop").Id, index = 0 },
                    new MenuEdit { kind = MenuEditKind.Icon, id = Item("Toggle 1").Id, icon = fixture.IconB },
                    // A folder VRCFury makes for menu paths, and a Full Controller's item: VRCFury features.
                    new MenuEdit { kind = MenuEditKind.Rename, id = Item("Props").Id, text = "Gear" },
                    new MenuEdit { kind = MenuEditKind.Move, id = Item("Scarf").Id, parent = MenuTree.RootId, index = 0 },
                    new MenuEdit { kind = MenuEditKind.NextText, text = "More" },
                };
                var draft = MenuSave.Draft(root, edits, null, out _);
                string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                Undo.IncrementCurrentGroup();
                var outcome = MenuSave.Save(root, edits);
                Assert.True(outcome.Saved, outcome.Message);
                Assert.AreEqual(expected, MenuFixture.Preview(root), "the editor shows the saved menu");
                Assert.AreEqual(expected, MenuFixture.Build(root), "VRCFury builds the edited menu");

                var menu = descriptor.expressionsMenu;
                var clothes = menu.controls.First(c => c.name == "Clothes").subMenu;
                Assert.True(clothes.controls.Any(c => c.name == "Sunglasses"), "renamed in its asset");
                Assert.AreEqual("Lights", clothes.controls[0].name, "moved between assets");
                Assert.AreSame(fixture.IconA, clothes.controls[0].icon);
                var toggles = VrcFury.Features(root).Where(f => f.kind == "Toggle").Select(f => VrcFury.Field(f.feature, "name") as string).ToList();
                Assert.Contains("Props/Saber", toggles, "the toggle's own menu path");
                Assert.Contains("Props/Shield", toggles, "a toggle another feature names by its path keeps it");
                Assert.True(VrcFury.Features(MenuSave.EditsObject(root)).Any(f => f.kind == "MoveMenuItem" && ((string)VrcFury.Field(f.feature, "toPath")).EndsWith("/Buckler")));
                Assert.Contains("Loop/Glow", toggles);
                var edited = MenuSave.EditsObject(root);
                Assert.NotNull(edited, "the rest is saved with VRCFury features");
                var kinds = VrcFury.Features(edited).Select(f => f.kind).ToList();
                Assert.Contains("MoveMenuItem", kinds);
                Assert.Contains("OverrideMenuSettings", kinds);
                Assert.AreEqual(root.transform.childCount - 1, edited.transform.GetSiblingIndex(), "after every other VRCFury component");

                // One Undo step puts everything back.
                Assert.AreEqual("Save menu", Undo.GetCurrentGroupName());
                Undo.PerformUndo();
                Assert.AreEqual(original, MenuFixture.Preview(root));
                Assert.Null(MenuSave.EditsObject(root));
            }
        }

        // Random sessions of renames, icons and moves over every kind of item: Save either makes VRCFury build exactly the
        // menu the editor showed, or refuses and changes nothing (VRCFury finds items by name, and can't move a menu that
        // opens itself). One VRCFury build per case.
        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        [TestCase(7)] [TestCase(8)] [TestCase(9)] [TestCase(10)] [TestCase(11)] [TestCase(12)]
        public void ARandomEditSessionBuildsWhatTheEditorShowed(int seed)
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture())
            {
                var edits = MenuFixture.RandomEdits(fixture.Root, new System.Random(seed), 7, new[] { fixture.IconA, fixture.IconB, fixture.IconC, null });
                var draft = MenuSave.Draft(fixture.Root, edits, null, out _);
                string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                string before = MenuFixture.Preview(fixture.Root);
                var outcome = MenuSave.Save(fixture.Root, edits);
                if (!outcome.Saved)
                {
                    Assert.True(Refusal(outcome.Message), outcome.Message);
                    Assert.AreEqual(before, MenuFixture.Preview(fixture.Root), "a refused save changes nothing");
                    return;
                }
                Assert.AreEqual(expected, MenuFixture.Preview(fixture.Root), "the preview after saving");
                Assert.AreEqual(expected, MenuFixture.Build(fixture.Root), "VRCFury's build");
            }
        }

        [Test]
        public void MostRandomEditSessionsSave()
        {
            NeedVrcFury();
            int saved = 0, sessions = 40;
            for (int seed = 100; seed < 100 + sessions; seed++)
                using (var fixture = new MenuFixture())
                {
                    var edits = MenuFixture.RandomEdits(fixture.Root, new System.Random(seed), 7, new[] { fixture.IconA, fixture.IconB, null });
                    var outcome = MenuSave.Save(fixture.Root, edits);
                    if (outcome.Saved) saved++;
                    else Assert.True(Refusal(outcome.Message), "seed " + seed + ": " + outcome.Message);
                }
            // The fixture has two folders named "Effects" and two items named "Spin" on purpose: some sessions must be refused.
            Assert.GreaterOrEqual(saved, sessions * 3 / 4, saved + " of " + sessions + " saved");
        }

        [Test]
        public void MenusUsedElsewhereAreCopiedBeforeTheyAreEdited()
        {
            NeedVrcFury();
            string name = "Menu Copy Test " + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (var temp = new TempAssets())
            {
                string folder = temp.Copies(name);
                using (var fixture = new MenuFixture(many: false))
                {
                    fixture.Root.name = name;
                    var descriptor = fixture.Root.GetComponent<VRCAvatarDescriptor>();
                    var clothes = descriptor.expressionsMenu.controls[0].subMenu;
                    // A Full Controller merging the same menu: editing it would change that too.
                    var full = MenuFixture.Feature(new GameObject("Shared") { transform = { parent = fixture.Root.transform } }, "FullController");
                    var entry = Activator.CreateInstance(VrcFury.Find("VF.Model.Feature.FullController+MenuEntry"), true);
                    MenuFixture.Set(entry, "menu", MenuFixture.Guid("GuidMenu", clothes)); MenuFixture.Set(entry, "prefix", "Shared");
                    ((System.Collections.IList)VrcFury.Field(full, "menus")).Add(entry);
                    var paramsEntry = Activator.CreateInstance(VrcFury.Find("VF.Model.Feature.FullController+ParamsEntry"), true);
                    MenuFixture.Set(paramsEntry, "parameters", MenuFixture.Guid("GuidParams", descriptor.expressionParameters));
                    ((System.Collections.IList)VrcFury.Field(full, "prms")).Add(paramsEntry);

                    var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                    var hat = MenuTree.Index(built.Root).Values.First(e => e.Node.Name == "Hat" && e.Node.Origin.Kind == MenuOriginKind.AvatarMenu).Node;
                    var edits = new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Rename, id = hat.Id, text = "Beanie" } };
                    var draft = MenuSave.Draft(fixture.Root, edits, null, out _);
                    string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                    var outcome = MenuSave.Save(fixture.Root, edits);
                    Assert.True(outcome.Saved, outcome.Message);
                    Assert.AreEqual("Hat", clothes.controls[0].name, "the shared menu is untouched");
                    var copy = descriptor.expressionsMenu.controls[0].subMenu;
                    Assert.AreNotSame(clothes, copy);
                    StringAssert.StartsWith(folder, AssetDatabase.GetAssetPath(copy));
                    Assert.AreEqual("Beanie", copy.controls[0].name);
                    Assert.AreEqual(expected, MenuFixture.Build(fixture.Root));
                }
            }
        }

        // ---- SPS: the folder and toggles VRCFury's sockets and plugs add ----------------------------------------------------

        [TestCase(true, true, null)]
        [TestCase(true, false, "Extras/Haptics")]
        [TestCase(false, true, null)]
        public void ThePreviewShowsWhatSpsAdds(bool sockets, bool plug, string menuPath)
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture(many: false))
            {
                fixture.AddSps(sockets, plug, menuPath);
                string preview = MenuFixture.Preview(fixture.Root);
                Assert.AreEqual(MenuFixture.Build(fixture.Root), preview);
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var sps = MenuTree.Walk(built.Root).Where(e => e.Node.Origin?.Kind == MenuOriginKind.Sps).Select(e => e.Node.Name).ToList();
                if (sockets)
                {
                    CollectionAssert.IsSubsetOf(new[] { "Mouth", "Mouth1", "Hips", "Blowjob" }, sps, "named by the socket, made unique, or after its object");
                    Assert.False(sps.Contains("Hidden"), "a socket without an item");
                    Assert.True(sps.Any(n => n.StartsWith("<b>Stealth Mode", StringComparison.Ordinal)));
                }
                if (plug) Assert.True(sps.Any(n => n.StartsWith("<b>DPS Tip Light", StringComparison.Ordinal)));
            }
        }

        [TestCase(21)] [TestCase(22)] [TestCase(23)] [TestCase(24)]
        public void ARandomEditSessionWithSpsBuildsWhatTheEditorShowed(int seed)
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture(many: false))
            {
                fixture.AddSps();
                var edits = MenuFixture.RandomEdits(fixture.Root, new System.Random(seed), 6, new[] { fixture.IconA, fixture.IconB, null });
                var draft = MenuSave.Draft(fixture.Root, edits, null, out _);
                string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                string before = MenuFixture.Preview(fixture.Root);
                var outcome = MenuSave.Save(fixture.Root, edits);
                if (!outcome.Saved)
                {
                    Assert.True(Refusal(outcome.Message), outcome.Message);
                    Assert.AreEqual(before, MenuFixture.Preview(fixture.Root), "a refused save changes nothing");
                    return;
                }
                Assert.AreEqual(expected, MenuFixture.Preview(fixture.Root), "the preview after saving");
                Assert.AreEqual(expected, MenuFixture.Build(fixture.Root), "VRCFury's build");
            }
        }

        // ---- A folder opening a menu above it moves like any other item -------------------------------------------------------

        [Test]
        public void AFolderOpeningTheMenuAboveItCanBeMovedAndKeepsOpeningIt()
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture(many: false))
            {
                var root = fixture.Root;
                var built = MenuBuild.Run(MenuInputs.Read(root));
                var index = MenuTree.Index(built.Root);
                var again = index.Values.First(e => e.Node.Name == "Again").Node;
                var loop = index.Values.First(e => e.Node.Name == "Loop" && e.Node.IsFolder).Node;
                Assert.True(MenuTree.OpenedElsewhere(built.Root, again), "“Again” opens Loop's own menu");
                var edits = new List<MenuEdit>
                {
                    // Within its menu, then up into the main menu.
                    new MenuEdit { kind = MenuEditKind.Move, id = again.Id, parent = loop.Id, index = 1 },
                    new MenuEdit { kind = MenuEditKind.Move, id = again.Id, parent = MenuTree.RootId, index = 0 },
                };
                var draft = MenuSave.Draft(root, edits, null, out _);
                Assert.AreEqual(0, draft.Stale);
                var moved = draft.Root.Items[0];
                Assert.AreEqual("Again", moved.Name);
                Assert.AreSame(MenuTree.Index(draft.Root)[loop.Id].Node.Page, moved.Page, "it still opens Loop's menu");
                string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                Undo.IncrementCurrentGroup();
                var outcome = MenuSave.Save(root, edits);
                Assert.True(outcome.Saved, outcome.Message);
                Assert.AreEqual(expected, MenuFixture.Preview(root));
                Assert.AreEqual(expected, MenuFixture.Build(root), "VRCFury builds it");
                var menu = root.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
                Assert.AreSame(menu.controls.First(c => c.name == "Loop").subMenu, menu.controls.First(c => c.name == "Again").subMenu, "written into the menu assets, still sharing the menu");
                Assert.Null(MenuSave.EditsObject(root), "no VRCFury feature needed");
            }
        }

        [Test]
        public void AFolderNeverGoesInsideItself()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var index = MenuTree.Index(built.Root);
                var clothes = index.Values.First(e => e.Node.Name == "Clothes").Node;
                var effects = index.Values.First(e => e.Node.Name == "Effects").Node;
                var draft = MenuDraft.Apply(built, new List<MenuEdit>
                {
                    new MenuEdit { kind = MenuEditKind.Move, id = effects.Id, parent = clothes.Id, index = 0 },
                    new MenuEdit { kind = MenuEditKind.Move, id = clothes.Id, parent = effects.Id, index = 0 },
                });
                Assert.AreEqual(1, draft.Stale, "Clothes can't go into Effects once Effects is inside Clothes");
                Assert.True(draft.Root.Items.Any(n => n.Id == clothes.Id));
            }
        }

        // ---- Disk: what Save writes is saved with the scene ----------------------------------------------------------------------

        [Test]
        public void TheAssetsSaveTouchesAreInTheCheckpoint()
        {
            var paths = TextureChanges.CheckpointPaths(default, null, new[] { "Assets/Some Menu.asset", "Packages/x/y.asset" });
            Assert.True(paths.Contains("Assets/Some Menu.asset") && paths.Contains("Assets/Some Menu.asset.meta"));
            Assert.False(paths.Contains("Packages/x/y.asset"), "only the project's own assets");
        }

        // ---- Modular Avatar: its menu rules, on plain data (Modular Avatar is not installed in this project) --------------------

        // The avatar's menu, then installers aimed at its asset, then the others; a menu asset with the installers aimed at it;
        // Menu Items named by label or object, submenus from children (shared, loops included), groups, install targets;
        // installers an install target names only there, disabled ones too; 7 items and "More" in a menu of more than 8.
        internal static MaSetup ModularAvatarSetup(MenuFixture fixture)
        {
            var ma = new MaSetup(fixture.Root);
            var rootMenu = fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
            var y = fixture.Menu("Extras Sub", MenuFixture.Control("C", ControlType.Toggle, ""));
            var x = fixture.Menu("Extras", MenuFixture.Control("A", ControlType.Toggle, ""), MenuFixture.Control("B", ControlType.SubMenu, "", menu: y));
            ma.Installer(ma.Object("Extras installer"), x);
            ma.Installer(ma.Object("Sub installer"), fixture.Menu("Z", MenuFixture.Control("Zed", ControlType.Button, "")), target: y);
            var outfit = ma.Object("Outfit");
            ma.Item(outfit, ControlType.SubMenu, children: true);
            ma.Installer(outfit);
            ma.Item(ma.Object("Hat2", outfit), ControlType.Toggle, label: "<b>Hat</b> 2");
            var elsewhere = ma.Object("Elsewhere");
            ma.Item(ma.Object("Sock1", elsewhere), ControlType.Toggle);
            ma.Item(ma.Object("Sock2", elsewhere), ControlType.RadialPuppet);
            ma.Group(ma.Object("Group", outfit), elsewhere);
            var placed = ma.Installer(ma.Object("Wing installer"), fixture.Menu("W", MenuFixture.Control("Wing", ControlType.Toggle, "")), target: rootMenu);
            ma.InstallTarget(ma.Object("Slot", outfit), placed);
            ma.Item(ma.Object("Back", outfit), ControlType.SubMenu, children: true, childrenOf: outfit);
            ma.Installer(ma.Object("Off installer"), fixture.Menu("V", MenuFixture.Control("Vee", ControlType.Toggle, "")), enabled: false);
            return ma;
        }

        private static List<string> Names(MenuPage page) => page.Items.Select(n => n.Name).ToList();

        [Test]
        public void ModularAvatarMenusFollowItsRules()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var ma = ModularAvatarSetup(fixture);
                var inputs = MenuInputs.Read(fixture.Root);
                inputs.ModularAvatar = ma.Menu;
                var built = MenuBuild.Run(inputs);
                Assert.True(built.ModularAvatar);
                var root = built.Root;
                CollectionAssert.AreEqual(new[] { "Clothes", "Effects", "Effects", "Dance", "<b>Wave</b>\n<size=20>hello", "Loop", "A", "More" }, Names(root));
                var more = root.Items[7];
                Assert.AreEqual(MenuOriginKind.MaMore, more.Origin.Kind);
                CollectionAssert.AreEqual(new[] { "B", "Outfit", "Vee" }, Names(more.Page), "installers aimed at no menu, the disabled one too");
                CollectionAssert.AreEqual(new[] { "C", "Zed" }, Names(more.Page.Items[0].Page), "a menu asset, then the installers aimed at it");
                var outfit = more.Page.Items[1];
                Assert.AreEqual(MenuOriginKind.MaItem, outfit.Origin.Kind);
                CollectionAssert.AreEqual(new[] { "<b>Hat</b> 2", "Sock1", "Sock2", "Wing", "Back" }, Names(outfit.Page), "children: items, a group's, an install target's installer");
                Assert.AreSame(outfit.Page, outfit.Page.Items[4].Page, "the same children make one menu: Back loops to it");
                Assert.AreEqual(ControlType.RadialPuppet, outfit.Page.Items[2].Type);
                Assert.AreEqual(MenuOriginKind.AvatarMenu, root.Items[0].Origin.Kind, "the avatar's own controls stay its own");
                Assert.AreEqual(MenuOriginKind.MaMenu, root.Items[6].Origin.Kind);

                // Without an enabled installer, Modular Avatar leaves the menu alone.
                foreach (var installer in ma.Menu.Installers) installer.Enabled = false;
                Assert.False(MenuBuild.Run(inputs).ModularAvatar);
            }
        }

        [Test]
        public void VrcFuryStartsFromModularAvatarsMenu()
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture(many: false))
            {
                var ma = ModularAvatarSetup(fixture);
                var inputs = MenuInputs.Read(fixture.Root);
                inputs.ModularAvatar = ma.Menu;
                var built = MenuBuild.Run(inputs);
                Assert.True(built.VrcFury && built.ModularAvatar);
                // Modular Avatar's menu first, as VRCFury's starting menu (its Move Menu Item then takes "Dance" into "Props").
                var first = built.Root.Items.TakeWhile(n => n.Sort == 0).Select(n => n.Name).ToList();
                CollectionAssert.AreEqual(new[] { "Clothes", "Effects", "Effects", "<b>Wave</b>\n<size=20>hello", "Loop", "A", "More" }, first);
                var more = built.Root.Items.First(n => n.Name == "More");
                CollectionAssert.AreEqual(new[] { "B", "Outfit", "Vee" }, Names(more.Page));
                Assert.True(built.Root.Items.Skip(first.Count).Any(n => n.Name == "Props"), "VRCFury's items come after");
            }
        }

        [Test]
        public void ModularAvatarMakesOneMenuPerAssetAndScope()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var ma = new MaSetup(fixture.Root);
                var clothes = fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionsMenu.controls.First(c => c.name == "Clothes").subMenu;
                ma.Installer(ma.Object("Enabled"), fixture.Menu("On menu", MenuFixture.Control("Clothes again", ControlType.SubMenu, "", menu: clothes)));
                ma.Installer(ma.Object("Disabled"), fixture.Menu("Off menu", MenuFixture.Control("Clothes shared", ControlType.SubMenu, "", menu: clothes)), enabled: false);
                var item = ma.Object("Item");
                ma.Item(item, ControlType.SubMenu, label: "Clothes item", subMenu: clothes);
                ma.Installer(item);
                var inputs = MenuInputs.Read(fixture.Root);
                inputs.ModularAvatar = ma.Menu;
                var root = MenuBuild.Run(inputs).Root;
                var all = MenuTree.Walk(root).Select(e => e.Node).ToList();
                MenuPage Page(string name) => all.First(n => n.Name == name).Page;
                Assert.AreNotSame(Page("Clothes"), Page("Clothes again"), "an enabled installer renames parameters: its own copy");
                Assert.AreSame(Page("Clothes"), Page("Clothes shared"), "a disabled one doesn't: the same menu");
                Assert.AreNotSame(Page("Clothes"), Page("Clothes item"), "a submenu Menu Item renames them too: its own copy");
                CollectionAssert.AreEqual(Names(Page("Clothes")), Names(Page("Clothes item")));
            }
        }

        // ---- Folders made in the editor ------------------------------------------------------------------------------------

        [Test]
        public void ANewFolderTakesTheFirstItemsPlaceAndHoldsTheItems()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var index = MenuTree.Index(built.Root);
                string Id(string name) => index.Values.First(e => e.Node.Name == name).Node.Id;
                var edits = new List<MenuEdit>
                {
                    new MenuEdit { kind = MenuEditKind.Folder, id = "new:a", text = "New folder", items = new List<string> { Id("Dance"), Id("Clothes") } },
                    new MenuEdit { kind = MenuEditKind.Rename, id = "new:a", text = "Fun" },
                    // Gone items and a folder going into itself are left out; no item at all makes no folder.
                    new MenuEdit { kind = MenuEditKind.Folder, id = "new:b", text = "New folder", items = new List<string> { "gone" } },
                };
                var draft = MenuDraft.Apply(built, edits);
                var root = draft.Root;
                Assert.AreEqual(1, draft.Stale);
                var fun = root.Items.First(n => n.Id == "new:a");
                Assert.AreEqual(2, root.Items.IndexOf(fun), "at Dance's place, after the two Effects folders");
                Assert.AreEqual(ControlType.SubMenu, fun.Type);
                Assert.AreEqual("Fun", fun.Name);
                CollectionAssert.AreEqual(new[] { "Dance", "Clothes" }, Names(fun.Page));
                Assert.False(root.Items.Any(n => n.Name == "Dance" || n.Name == "Clothes"));
                // Emptied by later moves, it goes like a folder made of menu paths.
                foreach (var item in fun.Page.Items.ToList()) edits.Add(new MenuEdit { kind = MenuEditKind.Move, id = item.Id, parent = MenuTree.RootId, index = 0 });
                Assert.False(MenuDraft.Apply(built, edits).Root.Items.Any(n => n.Id == "new:a"));
            }
        }

        // A folder made of the avatar's own menu items becomes a submenu of its menu asset; one made in a folder of VRCFury menu
        // paths is made by Move Menu Item; a mixed one gets both. VRCFury then builds the edited menu, and one Undo puts it back.
        [TestCase("Loop", "<b>Wave</b>\n<size=20>hello")]
        [TestCase("Sword", "Shield")]
        [TestCase("Loop", "Props")]
        [TestCase("Glasses", "Shirt")]
        public void NewFoldersSaveAndBuildTheEditedMenu(string first, string second)
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture(many: false))
            {
                var root = fixture.Root;
                var built = MenuBuild.Run(MenuInputs.Read(root));
                var index = MenuTree.Index(built.Root);
                string Id(string name) => index.Values.First(e => e.Node.Name == name).Node.Id;
                string original = MenuFixture.Preview(root);
                var edits = new List<MenuEdit>
                {
                    new MenuEdit { kind = MenuEditKind.Folder, id = "new:x", text = "New folder", items = new List<string> { Id(first), Id(second) } },
                    new MenuEdit { kind = MenuEditKind.Rename, id = "new:x", text = "Made here" },
                };
                var draft = MenuSave.Draft(root, edits, null, out _);
                string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                StringAssert.Contains("Made here [SubMenu]", expected);
                Undo.IncrementCurrentGroup();
                var outcome = MenuSave.Save(root, edits);
                Assert.True(outcome.Saved, outcome.Message);
                Assert.AreEqual(expected, MenuFixture.Preview(root), "the editor shows the saved menu");
                Assert.AreEqual(expected, MenuFixture.Build(root), "VRCFury builds it");
                Undo.PerformUndo();
                Assert.AreEqual(original, MenuFixture.Preview(root), "one Undo puts it back");
            }
        }

        [Test]
        public void ANewFolderOfTheAvatarsItemsIsANewSubmenuOfItsMenu()
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture(many: false))
            {
                var root = fixture.Root;
                var menu = root.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
                var index = MenuTree.Index(MenuBuild.Run(MenuInputs.Read(root)).Root);
                string Id(string name) => index.Values.First(e => e.Node.Name == name).Node.Id;
                var edits = new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Folder, id = "new:y", text = "Fun", items = new List<string> { Id("Effects"), Id("Loop") } } };
                var outcome = MenuSave.Save(root, edits);
                Assert.True(outcome.Saved, outcome.Message);
                var control = menu.controls.FirstOrDefault(c => c.name == "Fun");
                Assert.NotNull(control, "a submenu control in the avatar's menu");
                Assert.AreEqual(ControlType.SubMenu, control.type);
                CollectionAssert.AreEqual(new[] { "Effects", "Loop" }, control.subMenu.controls.Select(c => c.name));
                Assert.Null(MenuSave.EditsObject(root), "no VRCFury feature needed");
            }
        }

        [Test]
        public void ANewFolderOfAMenuAssetIsSavedNextToIt()
        {
            NeedVrcFury();
            string folder = "Assets/Menu Folder Test " + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (var fixture = new MenuFixture(many: false))
            {
                try
                {
                    AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
                    var menu = fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
                    AssetDatabase.CreateAsset(menu, folder + "/Root.asset");
                    var index = MenuTree.Index(MenuBuild.Run(MenuInputs.Read(fixture.Root)).Root);
                    string Id(string name) => index.Values.First(e => e.Node.Name == name).Node.Id;
                    var edits = new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Folder, id = "new:z", text = "Fun", items = new List<string> { Id("Loop"), Id("<b>Wave</b>\n<size=20>hello") } } };
                    var outcome = MenuSave.Save(fixture.Root, edits);
                    Assert.True(outcome.Saved, outcome.Message);
                    var control = menu.controls.First(c => c.name == "Fun");
                    Assert.AreEqual(folder + "/Fun.asset", AssetDatabase.GetAssetPath(control.subMenu), "a new menu next to the avatar's");
                    Assert.Contains(folder + "/Fun.asset", outcome.Touched.ToList(), "saved to disk with the scene");
                }
                finally
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
        }

        // A new folder in the avatar's menu holding only VRCFury toggles is still a menu of its own (VRCFury's Move Menu Item
        // fills it at upload): saved as an asset, or the avatar's menu would point at a menu gone after a restart.
        [Test]
        public void ANewFolderOfVrcFuryItemsIsSavedAsAMenuToo()
        {
            NeedVrcFury();
            string folder = "Assets/Menu Folder Test " + Guid.NewGuid().ToString("N").Substring(0, 8);
            using (var fixture = new MenuFixture())
            {
                try
                {
                    AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
                    var menu = fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
                    AssetDatabase.CreateAsset(menu, folder + "/Root.asset");
                    var index = MenuTree.Index(MenuBuild.Run(MenuInputs.Read(fixture.Root)).Root);
                    string Id(string name) => index.Values.First(e => e.Node.Name == name).Node.Id;
                    var edits = new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Folder, id = "new:v", text = "Toggles", items = new List<string> { Id("Toggle 1"), Id("Toggle 2") } } };
                    var draft = MenuSave.Draft(fixture.Root, edits, null, out _);
                    string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                    var outcome = MenuSave.Save(fixture.Root, edits);
                    Assert.True(outcome.Saved, outcome.Message);
                    var control = menu.controls.First(c => c.name == "Toggles");
                    Assert.AreEqual(folder + "/Toggles.asset", AssetDatabase.GetAssetPath(control.subMenu), "its menu is an asset, next to the avatar's");
                    Assert.AreEqual(0, control.subMenu.controls.Count, "VRCFury moves the toggles into it at upload");
                    Assert.AreEqual(expected, MenuFixture.Build(fixture.Root));
                }
                finally
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
        }

        // Random sessions with items left out and settings changed too: Save writes them where they live and keeps the left-out
        // items found where the menu puts them, or refuses and changes nothing.
        [TestCase(31)] [TestCase(32)] [TestCase(33)] [TestCase(34)] [TestCase(35)] [TestCase(36)]
        public void ARandomSessionWithSettingsAndLeftOutItemsSaves(int seed)
        {
            NeedVrcFury();
            using (var fixture = new MenuFixture())
            {
                var edits = MenuFixture.RandomEdits(fixture.Root, new System.Random(seed), 9, new[] { fixture.IconA, null }, settings: true);
                var draft = MenuSave.Draft(fixture.Root, edits, null, out _);
                string expected = MenuFixture.Dump(MenuBuild.Paginate(draft.Root, draft.NextText, draft.NextIcon));
                string before = MenuFixture.Preview(fixture.Root);
                var removals = MenuSave.Removals.Of(draft);
                var outcome = MenuSave.Save(fixture.Root, edits, null, removals);
                if (!outcome.Saved)
                {
                    Assert.True(Refusal(outcome.Message), outcome.Message);
                    Assert.AreEqual(before, MenuFixture.Preview(fixture.Root), "a refused save changes nothing");
                    return;
                }
                Assert.AreEqual(expected, MenuFixture.Preview(fixture.Root), "the preview after saving");
                Assert.AreEqual(expected, MenuFixture.Build(fixture.Root), "VRCFury's build");
                var left = MenuSave.SavedRemovals(fixture.Root, MenuBuild.Run(MenuInputs.Read(fixture.Root)), out _);
                Assert.AreEqual(removals.Items.Count, left.Count, "every item left out is found again, as itself");
            }
        }

        // An item's id doesn't follow its place in its menu: a control added before it elsewhere leaves pending edits on it.
        [Test]
        public void AControlAddedBeforeAnItemLeavesItsEditsOnIt()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var clothes = fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionsMenu.controls.First(c => c.name == "Clothes").subMenu;
                string glasses = MenuTree.Walk(MenuBuild.Run(MenuInputs.Read(fixture.Root)).Root).First(e => e.Node.Name == "Glasses").Node.Id;
                clothes.controls.Insert(0, MenuFixture.Control("Gloves", ControlType.Toggle, "Hat"));
                var draft = MenuDraft.Apply(MenuBuild.Run(MenuInputs.Read(fixture.Root)), new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Rename, id = glasses, text = "Shades" } });
                CollectionAssert.AreEqual(new[] { "Gloves", "Hat", "Shades" }, MenuTree.Walk(draft.Root).First(e => e.Node.Name == "Clothes").Node.Page.Items.Select(n => n.Name));
            }
        }

        [Test]
        public void PicturesBecomeMenuIconsVrChatAccepts()
        {
            string file = Path.Combine(Path.GetTempPath(), "orbiters-menu-icon-" + Guid.NewGuid().ToString("N") + ".png");
            var source = new Texture2D(600, 300, TextureFormat.RGBA32, false);
            // A picture of its own: an identical one imported before would be used again.
            source.SetPixel(3, 3, new Color((float)new System.Random().NextDouble(), .5f, .25f));
            source.Apply();
            string asset = null;
            var temp = new TempAssets();
            try
            {
                File.WriteAllBytes(file, source.EncodeToPNG());
                var edit = new MenuEdit { kind = MenuEditKind.Icon, id = "x", file = file };
                var preview = MenuIcons.Resolve(edit);
                Assert.NotNull(preview, "the picture shows before anything is imported");
                Assert.False(EditorUtility.IsPersistent(preview));
                var texture = MenuIcons.Import(file, out bool created);
                Assert.True(created);
                asset = AssetDatabase.GetAssetPath(texture);
                Assert.AreSame(texture, MenuIcons.Import(file, out bool again), "the same picture imported again is the same icon");
                Assert.False(again, "no “name 1.png”");
                StringAssert.StartsWith(MenuIcons.Folder + "/", asset);
                var importer = (TextureImporter)AssetImporter.GetAtPath(asset);
                Assert.AreEqual(256, importer.maxTextureSize);
                Assert.False(importer.mipmapEnabled);
                Assert.True(importer.alphaIsTransparency);
                Assert.AreNotEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
                Assert.LessOrEqual(Mathf.Max(texture.width, texture.height), 256);
            }
            finally
            {
                Object.DestroyImmediate(source);
                if (File.Exists(file)) File.Delete(file);
                temp.Add(asset);
                temp.Dispose();
            }
        }
    }
}
