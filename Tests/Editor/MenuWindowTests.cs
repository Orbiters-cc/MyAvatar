using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Expressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// The menu editor's window, driven with real pointer events in a hidden copy (never shown on a display, never focused) on
    /// a test avatar in a preview scene: dragging, holding an item on another to make a folder, ticking items.
    /// </summary>
    public sealed class MenuWindowTests
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private sealed class Driver : IDisposable
        {
            public readonly MenuEditorWindow Window;
            private readonly IPanel panel;

            /// <param name="template">A window this one copies, as Orbiters Toolkit's capture copies a window to show it off-screen.</param>
            /// <param name="before">Runs on the window before it is shown.</param>
            public Driver(GameObject root, MenuEditorWindow template = null, Action<MenuEditorWindow> before = null)
            {
                Window = ScriptableObject.CreateInstance<MenuEditorWindow>();
                if (template != null) EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(template), Window);
                before?.Invoke(Window);
                typeof(MenuEditorWindow).GetField("root", Members).SetValue(Window, root);
                Window.position = new Rect(-32000, -32000, 1220, 860);
                var showMode = typeof(EditorWindow).Assembly.GetType("UnityEditor.ShowMode");
                typeof(EditorWindow).GetMethod("ShowPopupWithMode", Members, null, new[] { showMode, typeof(bool) }, null)
                    .Invoke(Window, new[] { Enum.Parse(showMode, "PopupMenu"), (object)false });
                panel = Window.rootVisualElement.panel;
                Layout();
            }

            public void Layout() => typeof(EditorWindow).GetMethod("RepaintImmediately", Members).Invoke(Window, null);

            public int Edits => Window.State.edits.Count;

            public void Down(Vector2 at, int clicks = 1) => Send(EventType.MouseDown, at, clicks);
            public void Move(Vector2 at) => Send(EventType.MouseDrag, at);
            public void Up(Vector2 at, int clicks = 1) => Send(EventType.MouseUp, at, clicks);

            /// <summary>A double click: a click, then a press and a release counting two clicks.</summary>
            public void DoubleClick(Vector2 at)
            {
                Down(at); Up(at); Layout();
                Down(at, 2); Up(at, 2); Layout();
            }

            /// <summary>A press, a drag through <paramref name="via"/> and a release at <paramref name="to"/>.</summary>
            public void Drag(Vector2 from, Vector2 to, params Vector2[] via)
            {
                Down(from);
                Move(from + new Vector2(12, 3));
                foreach (var point in via) Move(point);
                Move(to);
                Up(to);
                Layout();
            }

            // Time passes for the panel's scheduled actions (holding on an item, opening a folder).
            public void Wait(int milliseconds)
            {
                System.Threading.Thread.Sleep(milliseconds);
                var scheduler = panel.GetType().GetProperty("scheduler", Members).GetValue(panel, null);
                scheduler.GetType().GetMethod("UpdateScheduledEvents", Members).Invoke(scheduler, null);
            }

            private void Send(EventType type, Vector2 at, int clicks = 1)
            {
                var e = new Event { type = type, mousePosition = at, button = 0, clickCount = clicks };
                EventBase evt = type == EventType.MouseDown ? PointerDownEvent.GetPooled(e) : type == EventType.MouseDrag ? (EventBase)PointerMoveEvent.GetPooled(e) : PointerUpEvent.GetPooled(e);
                using (evt) panel.visualTree.SendEvent(evt);
                typeof(PointerCaptureHelper).GetMethod("ProcessPointerCapture", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                    .Invoke(null, new object[] { panel, PointerId.mousePointerId });
            }

            public VisualElement Tile(string id) => Window.Quick.TileOf(id) ?? throw new AssertionException("No tile for " + id);

            public Vector2 Center(string id) => Tile(id).worldBound.center;

            public void Dispose()
            {
                if (Window != null) Window.CloseWithoutSaving();
            }
        }

        private static string Id(GameObject root, string name) =>
            MenuTree.Index(MenuBuild.Run(MenuInputs.Read(root)).Root).Values.First(e => e.Node.Name == name).Node.Id;

        private static List<VisualElement> Tiles(MenuEditorWindow window) => window.Quick.Query<VisualElement>(className: "qm-tile").ToList();

        private static void AssertUnchanged(Driver driver, string dragged, Rect where, string how)
        {
            Assert.AreEqual(0, driver.Edits, how + ": no change, no Undo step");
            Assert.False(Tiles(driver.Window).Any(t => t.ClassListContains("qm-tile--lifted")), how + ": every tile shows");
            Assert.AreEqual(where.position, driver.Tile(dragged).worldBound.position, how + ": the item is back in its place");
        }

        [Test]
        public void ADragThatChangesNothingLeavesTheMenuAsItWas()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                // Clothes holds 8 items: without VRCFury nothing more fits in it.
                var clothesMenu = fixture.Root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>().expressionsMenu.controls.First(c => c.name == "Clothes").subMenu;
                for (int i = 0; i < 6; i++) clothesMenu.controls.Add(MenuFixture.Control("Extra " + i, VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle, "Hat"));
                using (var driver = new Driver(fixture.Root))
                    DragsThatChangeNothing(fixture, driver);
            }
        }

        private static void DragsThatChangeNothing(MenuFixture fixture, Driver driver)
        {
            {
                string dance = Id(fixture.Root, "Dance");
                var place = driver.Tile(dance).worldBound;
                // Picked up and released where it was.
                driver.Drag(place.center, place.center + new Vector2(2, 1));
                AssertUnchanged(driver, dance, place, "dropped in place");
                // Released outside the menu.
                driver.Drag(place.center, new Vector2(900, 800), new Vector2(600, 500));
                AssertUnchanged(driver, dance, place, "dropped outside the menu");
                // Dropped into a full folder: refused.
                driver.Drag(place.center, driver.Center(Id(fixture.Root, "Clothes")));
                AssertUnchanged(driver, dance, place, "dropped into a full folder");
                // The Action Menu: dropped outside its circle.
                var icon = driver.Window.Radial.IconOf(dance).Value;
                driver.Drag(icon, new Vector2(1100, 800));
                Assert.AreEqual(0, driver.Edits, "the Action Menu: nothing changes");
                Assert.False(driver.Window.Radial.Faded(dance), "the Action Menu: the item shows again");
                // A real move still is one.
                driver.Drag(place.center, driver.Center(Id(fixture.Root, "Clothes")) + new Vector2(-40, 0));
                Assert.AreEqual(1, driver.Edits, "a real move is one change");
            }
        }

        [Test]
        public void TheRedCrossLeavesAnItemOutAtOnceAndItsChipPutsItBack()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(fixture.Root))
            {
                string dance = Id(fixture.Root, "Dance");
                var cross = driver.Tile(dance).Q<VisualElement>(className: MenuDelete.Class);
                Assert.NotNull(cross, "an item has its red cross");
                driver.Down(cross.worldBound.center);
                Assert.AreEqual(1, driver.Edits, "it answers on pointer down: one change");
                driver.Up(cross.worldBound.center);
                driver.Layout();
                Assert.IsNull(driver.Window.Quick.TileOf(dance), "it leaves the menu");
                var chip = driver.Window.rootVisualElement.Query<VisualElement>(className: "me-chip").ToList().Single(c => c.Q<Label>().text == "Dance");
                driver.Down(chip.worldBound.center);
                driver.Up(chip.worldBound.center);
                driver.Layout();
                Assert.AreEqual(2, driver.Edits, "putting it back is one change");
                Assert.NotNull(driver.Window.Quick.TileOf(dance), "a click on it puts it back where it was");
            }
        }

        // Without VRCFury nothing splits a full menu into pages: putting an item back into one makes the "Next" folder.
        [Test]
        public void PuttingAnItemBackIntoAFullMenuMakesANextPage()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var menu = fixture.Root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>().expressionsMenu;
                menu.controls.Add(MenuFixture.Control("Extra 1", VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle, "Hat"));
                menu.controls.Add(MenuFixture.Control("Extra 2", VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle, "Glasses"));
                using (var driver = new Driver(fixture.Root))
                {
                    string dance = Id(fixture.Root, "Dance"), hat = Id(fixture.Root, "Hat");
                    var cross = driver.Tile(dance).Q<VisualElement>(className: MenuDelete.Class);
                    driver.Down(cross.worldBound.center); driver.Up(cross.worldBound.center); driver.Layout();
                    // The place fills up again: the hat moves to the main menu.
                    driver.Window.State.Add(new MenuEdit { kind = MenuEditKind.Move, id = hat, parent = MenuTree.RootId, index = 7 }, "Move the hat");
                    typeof(MenuEditorWindow).GetMethod("Refresh", Members).Invoke(driver.Window, null);
                    driver.Layout();
                    var chip = driver.Window.rootVisualElement.Query<VisualElement>(className: "me-chip").ToList().Single(c => c.Q<Label>().text == "Dance");
                    driver.Down(chip.worldBound.center); driver.Up(chip.worldBound.center); driver.Layout();

                    Assert.AreEqual(5, driver.Edits, "putting it back, the new page and its arrow");
                    var visible = (MenuPage)typeof(MenuEditorWindow).GetField("visible", Members).GetValue(driver.Window);
                    Assert.AreEqual(MenuBuild.MaxControls, visible.Items.Count, "the main menu keeps 8 items");
                    var next = visible.Items.Last();
                    Assert.AreEqual("Next", next.Name);
                    Assert.True(next.IsFolder);
                    CollectionAssert.AreEqual(new[] { "Extra 2", "Dance" }, next.Page.Items.Select(n => n.Name), "the place's last item and the one put back go on the next page");
                    Assert.NotNull(next.Icon, "with the arrow");
                }
            }
        }

        [Test]
        public void HoldingAnItemOnAnotherMakesAFolderOfTheTwo()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(fixture.Root))
            {
                string dance = Id(fixture.Root, "Dance"), wave = Id(fixture.Root, "<b>Wave</b>\n<size=20>hello");
                var target = driver.Center(wave);
                // Passing over an item makes nothing: only holding does.
                driver.Drag(driver.Center(dance), driver.Center(dance) + new Vector2(2, 0), target);
                Assert.AreEqual(0, driver.Edits, "passing over an item");

                driver.Down(driver.Center(dance));
                driver.Move(driver.Center(dance) + new Vector2(12, 3));
                driver.Move(target);
                driver.Wait(520);
                Assert.True(driver.Tile(wave).ClassListContains("qm-tile--fold"), "the target shows the folder forming");
                driver.Up(target);
                driver.Layout();
                Assert.AreEqual(1, driver.Edits, "one change, one Undo step");
                var edit = driver.Window.State.edits[0];
                Assert.AreEqual(MenuEditKind.Folder, edit.kind);
                CollectionAssert.AreEqual(new[] { wave, dance }, edit.items, "the target first, at its place");
                Assert.AreEqual(edit.id, driver.Window.Quick.Renaming, "the new folder is named in place right away");
                Assert.AreEqual("New folder", driver.Tile(edit.id).Q<TextField>().value);
            }
        }

        [Test]
        public void HoldingOnAnItemInTheActionMenuMakesAFolder()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(fixture.Root))
            {
                string dance = Id(fixture.Root, "Dance"), wave = Id(fixture.Root, "<b>Wave</b>\n<size=20>hello");
                var radial = driver.Window.Radial;
                var from = radial.IconOf(dance).Value;
                var onto = radial.IconOf(wave).Value;
                driver.Down(from);
                driver.Move(from + new Vector2(12, 3));
                driver.Move(onto);
                driver.Wait(520);
                driver.Up(onto);
                driver.Layout();
                Assert.AreEqual(1, driver.Edits);
                Assert.AreEqual(MenuEditKind.Folder, driver.Window.State.edits[0].kind);
                Assert.AreEqual(driver.Window.State.edits[0].id, radial.Renaming, "named in place in the Action Menu");
            }
        }

        [Test]
        public void TickingItemsSelectsThemWithoutOpeningFoldersAndMakesAFolderOfThem()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(fixture.Root))
            {
                string clothes = Id(fixture.Root, "Clothes"), dance = Id(fixture.Root, "Dance");
                var box = driver.Tile(clothes).Q<VisualElement>(className: MenuCheck.Class);
                driver.Down(box.worldBound.center);
                Assert.True(box.ClassListContains(MenuCheck.On), "ticked on the press");
                driver.Up(box.worldBound.center);
                driver.Layout();
                Assert.AreEqual(0, driver.Window.Path.Count, "a ticked folder is not opened");
                CollectionAssert.AreEqual(new[] { clothes }, driver.Window.Ticked);
                Assert.AreEqual("Clothes", FindName(driver.Window), "its name and icon beside the menu");

                // A second one, in the Action Menu: both menus show both ticks.
                var second = driver.Window.Radial.CheckOf(dance);
                driver.Down(second.worldBound.center);
                driver.Up(second.worldBound.center);
                driver.Layout();
                CollectionAssert.AreEqual(new[] { clothes, dance }, driver.Window.Ticked);
                Assert.True(driver.Tile(dance).Q<VisualElement>(className: MenuCheck.Class).ClassListContains(MenuCheck.On));
                Assert.AreEqual("2 items selected", driver.Window.SideTitle);
                Assert.AreEqual(0, driver.Edits, "ticking changes nothing");

                var button = driver.Window.SideFolderButton;
                Assert.NotNull(button, "Create folder (submenu)");
                driver.Down(button.worldBound.center);
                driver.Up(button.worldBound.center);
                driver.Layout();
                Assert.AreEqual(1, driver.Edits);
                var edit = driver.Window.State.edits[0];
                CollectionAssert.AreEqual(new[] { clothes, dance }, edit.items);
                Assert.AreEqual(edit.id, driver.Window.Quick.Renaming);
                CollectionAssert.IsEmpty(driver.Window.Ticked);
                // Clicking a folder still opens it.
                driver.Down(driver.Center(edit.id));
                driver.Up(driver.Center(edit.id));
                Assert.AreEqual(1, driver.Window.Path.Count, "a click opens a folder");
            }
        }

        // A double click on a folder renames it: its first click opens it, and the second press, landing on the page it
        // opened, goes back and names the folder in place instead of renaming what is inside.
        [Test]
        public void DoubleClickingAFolderRenamesTheFolder()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(FullerClothes(fixture)))
            {
                string clothes = Id(fixture.Root, "Clothes");
                driver.DoubleClick(driver.Center(clothes));
                Assert.AreEqual(0, driver.Window.Path.Count, "back on the main menu");
                Assert.AreEqual(clothes, driver.Window.Quick.Renaming, "the folder is renamed, not the item inside it");
                Assert.AreEqual(0, driver.Edits);

                // A double click on an item still renames it.
                string dance = Id(fixture.Root, "Dance");
                driver.DoubleClick(driver.Center(dance));
                Assert.AreEqual(dance, driver.Window.Quick.Renaming);
            }
        }

        [Test]
        public void DoubleClickingAFolderInTheActionMenuRenamesTheFolder()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(FullerClothes(fixture)))
            {
                string clothes = Id(fixture.Root, "Clothes");
                driver.DoubleClick(driver.Window.Radial.IconOf(clothes).Value);
                Assert.AreEqual(0, driver.Window.Path.Count, "back on the main menu");
                Assert.AreEqual(clothes, driver.Window.Radial.Renaming, "the folder is renamed, not the item inside it");
            }
        }

        // Closing with changes and choosing Save: when the save is refused, the changes stay and so does the window.
        [Test]
        public void ARefusedSaveKeepsTheChangesUnsaved()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(fixture.Root))
            {
                // A default for a parameter the avatar doesn't have: Save refuses it.
                string hat = Id(fixture.Root, "Hat");
                driver.Window.State.Add(new MenuEdit { kind = MenuEditKind.Parameter, id = hat, text = "Nope" }, "Parameter of Hat");
                driver.Window.State.Add(new MenuEdit { kind = MenuEditKind.Default, id = hat, number = 1 }, "Default of Hat");
                typeof(MenuEditorWindow).GetMethod("Refresh", Members).Invoke(driver.Window, null);
                Assert.True(driver.Window.hasUnsavedChanges);

                driver.Window.SaveChanges();

                Assert.True(driver.Window.hasUnsavedChanges, "the window stays, saying why");
                Assert.AreEqual(2, driver.Edits, "the changes are still there");
            }
        }

        // The changes not saved go with the window (Undo can't bring back what nothing shows any more).
        [Test]
        public void TheChangesGoWithTheWindow()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                MenuEditState state;
                using (var driver = new Driver(fixture.Root))
                {
                    state = driver.Window.State;
                    state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = Id(fixture.Root, "Dance"), text = "Groove" }, "Rename Dance");
                }
                Assert.True(state == null, "destroyed with the window");
            }
        }

        // Orbiters Toolkit captures a window off-screen in a copy made from its serialized state: the copy shows the same
        // changes without sharing them, and closing it leaves the window's changes (and their Undo) as they were.
        [Test]
        public void ACopyOfTheWindowLeavesItsChangesAlone()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(fixture.Root))
            {
                // The window as the user opened it from My Avatar (the copy's serialized state says so too).
                typeof(MenuEditorWindow).GetField("opened", Members).SetValue(driver.Window, true);
                var state = driver.Window.State;
                state.Add(new MenuEdit { kind = MenuEditKind.Rename, id = Id(fixture.Root, "Dance"), text = "Groove" }, "Rename Dance");
                typeof(MenuEditorWindow).GetMethod("Refresh", Members).Invoke(driver.Window, null);
                // A copy never shown, then one shown.
                var unshown = ScriptableObject.CreateInstance<MenuEditorWindow>();
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(driver.Window), unshown);
                Object.DestroyImmediate(unshown);
                Assert.True(state != null && driver.Window.State == state && state.edits.Count == 1, "an unshown copy closed");
                using (var copy = new Driver(fixture.Root, driver.Window))
                {
                    Assert.AreNotSame(state, copy.Window.State, "the copy works on its own clone");
                    Assert.AreEqual(1, copy.Edits, "with the same changes");
                }
                Assert.True(state != null && state.edits.Count == 1, "closing the copy leaves the window's changes");
                // A copy whose serialized state brought the window's own changes along (the reference itself).
                using (var copy = new Driver(fixture.Root, driver.Window, w => typeof(MenuEditorWindow).GetField("state", Members).SetValue(w, state)))
                {
                    Assert.AreNotSame(state, copy.Window.State, "it works on a clone too");
                    Assert.AreEqual(1, copy.Edits);
                }
                Assert.True(state != null && state.edits.Count == 1, "closing that copy leaves them too");
                Assert.True(driver.Window.hasUnsavedChanges);
            }
        }

        // Changes elsewhere (another object, a material) don't build the menu again; the avatar's do; an Undo of a change made
        // here only shows the edits again.
        [Test]
        public void OnlyTheAvatarsChangesBuildItsMenuAgain()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            using (var driver = new Driver(fixture.Root))
            {
                var elsewhere = new GameObject("Elsewhere");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(elsewhere, fixture.Scene);
                var dirty = typeof(MenuEditorWindow).GetField("dirty", Members);
                var changes = typeof(MenuEditorWindow).GetMethod("OnObjectChanges", Members);
                void Changed(GameObject target)
                {
                    var builder = new ObjectChangeEventStream.Builder(Unity.Collections.Allocator.Temp);
                    var e = new ChangeGameObjectOrComponentPropertiesEventArgs(target.GetInstanceID(), fixture.Scene);
                    builder.PushChangeGameObjectOrComponentPropertiesEvent(ref e);
                    var stream = builder.ToStream(Unity.Collections.Allocator.Temp);
                    try { changes.Invoke(driver.Window, new object[] { stream }); }
                    finally { stream.Dispose(); builder.Dispose(); }
                }
                try
                {
                    dirty.SetValue(driver.Window, false);
                    Changed(elsewhere);
                    Assert.False((bool)dirty.GetValue(driver.Window), "another object");
                    Changed(fixture.Root);
                    Assert.True((bool)dirty.GetValue(driver.Window), "the avatar");

                    typeof(MenuEditorWindow).GetMethod("Refresh", Members).Invoke(driver.Window, null);
                    Assert.False((bool)dirty.GetValue(driver.Window));
                    driver.Window.State.Add(new MenuEdit { kind = MenuEditKind.Rename, id = Id(fixture.Root, "Dance"), text = "Groove" }, "Rename “Dance”");
                    Undo.PerformUndo();
                    Assert.False((bool)dirty.GetValue(driver.Window), "an Undo of a change made here");
                    Assert.AreEqual(0, driver.Edits);
                }
                finally { Object.DestroyImmediate(elsewhere); }
            }
        }

        // Clothes with six items: the second press of a double click on it lands on one of them once it is open.
        private static GameObject FullerClothes(MenuFixture fixture)
        {
            var clothes = fixture.Root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>().expressionsMenu.controls.First(c => c.name == "Clothes").subMenu;
            for (int i = 0; i < 4; i++) clothes.controls.Add(MenuFixture.Control("Extra " + i, VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType.Toggle, "Hat"));
            return fixture.Root;
        }

        private static string FindName(MenuEditorWindow window) => window.rootVisualElement.Q<TextField>(className: "me-name")?.value;
    }
}
