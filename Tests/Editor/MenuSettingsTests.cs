using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Items' settings as Save writes them (in the order they were made, into copies of what the avatar shares), left-out items
    /// told from new items of the same name, and what makes the editor and the parameter budget work the menu out again.
    /// </summary>
    public sealed class MenuSettingsTests
    {
        private static MenuNode Node(GameObject root, string name) => MenuTree.Walk(MenuBuild.Run(MenuInputs.Read(root)).Root).First(e => e.Node.Name == name).Node;

        private static VRCExpressionParameters.Parameter Parameter(GameObject root, string name) =>
            root.GetComponent<VRCAvatarDescriptor>().expressionParameters.parameters.Single(p => p.name == name);

        // A default set after the item changed its parameter goes to the new parameter, not to the one it had.
        [Test]
        public void SettingsOfOneItemApplyInTheOrderTheyWereMade()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var hat = Node(fixture.Root, "Hat");
                var edits = new List<MenuEdit>
                {
                    new MenuEdit { kind = MenuEditKind.Parameter, id = hat.Id, text = "Glasses" },
                    new MenuEdit { kind = MenuEditKind.Default, id = hat.Id, number = 1 },
                    new MenuEdit { kind = MenuEditKind.Type, id = hat.Id, index = (int)ControlType.Button },
                };

                var outcome = MenuSave.Save(fixture.Root, edits);

                Assert.True(outcome.Saved, outcome.Message);
                Assert.AreEqual("Glasses", hat.Origin.Control.parameter.name);
                Assert.AreEqual(ControlType.Button, hat.Origin.Control.type);
                Assert.AreEqual(1f, Parameter(fixture.Root, "Glasses").defaultValue, "the default of the parameter it sets now");
                Assert.AreEqual(0f, Parameter(fixture.Root, "Hat").defaultValue, "the one it set before is left as it was");
            }
        }

        // A VRCFury toggle made a slider, then given a starting point: the slider's starting value is written.
        [Test]
        public void AToggleMadeASliderStartsWhereItsSliderSays()
        {
            if (!VrcFury.Installed) Assert.Ignore("VRCFury is not installed.");
            using (var fixture = new MenuFixture(many: false))
            {
                var sword = Node(fixture.Root, "Sword");
                var edits = new List<MenuEdit>
                {
                    new MenuEdit { kind = MenuEditKind.Type, id = sword.Id, index = (int)ControlType.RadialPuppet },
                    new MenuEdit { kind = MenuEditKind.Default, id = sword.Id, number = .6f },
                    new MenuEdit { kind = MenuEditKind.Saved, id = sword.Id, flag = true },
                };

                var outcome = MenuSave.Save(fixture.Root, edits);

                Assert.True(outcome.Saved, outcome.Message);
                var model = VrcFury.Content(sword.Origin.Component);
                Assert.AreEqual(true, VrcFury.Field(model, "slider"));
                Assert.AreEqual(.6f, (float)VrcFury.Field(model, "defaultSliderValue"), 1e-5f);
                Assert.AreEqual(false, VrcFury.Field(model, "defaultOn"), "not the toggle's on/off");
                Assert.AreEqual(true, VrcFury.Field(model, "saved"));
            }
        }

        // A Full Controller's items keep their settings: VRCFury gives its parameters their own names at upload, so the
        // avatar's parameter of the same name isn't theirs, and its menu stays as its creator made it.
        [Test]
        public void AFullControllersItemsKeepTheirSettings()
        {
            if (!VrcFury.Installed) Assert.Ignore("VRCFury is not installed.");
            using (var fixture = new MenuFixture(many: false))
            {
                var scarf = Node(fixture.Root, "Scarf");
                Assert.AreEqual(MenuOriginKind.MergedMenu, scarf.Origin.Kind);
                var info = MenuSave.Info(fixture.Root, scarf, null);
                CollectionAssert.IsEmpty(info.Types);
                Assert.Null(info.Parameters, "the avatar's parameters are not offered");
                Assert.False(info.DefaultEditable || info.SavedEditable || info.SyncedEditable || info.ValueEditable);
                StringAssert.Contains("Full Controller", info.Why);
                StringAssert.StartsWith("controller:", MenuDraft.SettingsKey(scarf));
            }
        }

        // The menus and parameters a prefab uses too are copied before an item's settings change: the prefab keeps its own.
        [Test]
        public void MenusAndParametersAPrefabUsesAreCopiedFirst()
        {
            string name = "Menu Settings Test " + Guid.NewGuid().ToString("N").Substring(0, 8);
            var temp = new TempAssets();
            string copies = temp.Copies(name);
            // The fixture's menus become assets of the test folder: it goes before the fixture does.
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                try
                {
                    fixture.Root.name = name;
                    string folder = temp.Folder(name);
                    var descriptor = fixture.Root.GetComponent<VRCAvatarDescriptor>();
                    var root = descriptor.expressionsMenu;
                    var parameters = descriptor.expressionParameters;
                    int n = 0;
                    foreach (var menu in MenuPaths.Menus(root)) AssetDatabase.CreateAsset(menu, folder + "/Menu " + n++ + ".asset");
                    AssetDatabase.CreateAsset(parameters, folder + "/Parameters.asset");
                    var shared = new GameObject("Shared avatar");
                    try
                    {
                        var other = shared.AddComponent<VRCAvatarDescriptor>();
                        other.customExpressions = true; other.expressionsMenu = root; other.expressionParameters = parameters;
                        PrefabUtility.SaveAsPrefabAsset(shared, folder + "/Shared avatar.prefab");
                    }
                    finally { Object.DestroyImmediate(shared); }
                    var hat = Node(fixture.Root, "Hat");
                    var clothes = hat.Origin.Asset;
                    var edits = new List<MenuEdit>
                    {
                        new MenuEdit { kind = MenuEditKind.Type, id = hat.Id, index = (int)ControlType.Button },
                        new MenuEdit { kind = MenuEditKind.Default, id = hat.Id, number = 1 },
                    };

                    var outcome = MenuSave.Save(fixture.Root, edits);

                    Assert.True(outcome.Saved, outcome.Message);
                    Assert.AreEqual(ControlType.Toggle, clothes.controls.Single(c => c.name == "Hat").type, "the prefab's menu is untouched");
                    Assert.AreEqual(0f, parameters.parameters.Single(p => p.name == "Hat").defaultValue, "the prefab's parameters are untouched");
                    Assert.AreNotSame(root, descriptor.expressionsMenu, "the avatar uses copies now");
                    Assert.AreNotSame(parameters, descriptor.expressionParameters);
                    StringAssert.StartsWith(copies + "/", AssetDatabase.GetAssetPath(descriptor.expressionParameters));
                    var copy = Node(fixture.Root, "Hat");
                    Assert.AreEqual(ControlType.Button, copy.Type);
                    StringAssert.StartsWith(copies + "/", AssetDatabase.GetAssetPath(copy.Origin.Asset));
                    Assert.AreEqual(1f, Parameter(fixture.Root, "Hat").defaultValue);
                }
                finally
                {
                    temp.Dispose();
                }
            }
        }

        // A left-out item is found again as itself: after a same-named item of another kind came before it, and not at all
        // once only a new item of that name is left.
        [Test]
        public void ALeftOutItemIsFoundAsItself()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var hat = MenuTree.Walk(built.Root).First(e => e.Node.Name == "Hat").Node;
                var removal = MenuSave.Removals.Of(MenuDraft.Apply(built, new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Delete, id = hat.Id } })).Items.Single();
                var clothes = hat.Origin.Asset;

                clothes.controls.Insert(0, MenuFixture.Control("Hat", ControlType.Button, "Dance"));
                var found = MenuPaths.Resolve(MenuBuild.Run(MenuInputs.Read(fixture.Root)).Root, removal);
                Assert.NotNull(found);
                Assert.AreEqual(ControlType.Toggle, found.Type, "the toggle, not the button now before it");

                clothes.controls.RemoveAll(c => c.name == "Hat" && c.type == ControlType.Toggle);
                Assert.Null(MenuPaths.Resolve(MenuBuild.Run(MenuInputs.Read(fixture.Root)).Root, removal), "a new item of the same name is not it");
            }
        }

        // Saving keeps the left-out items no item answers to now (an accessory left out of the build): they act again once
        // their item is back.
        [Test]
        public void SavingKeepsLeftOutItemsMissingNow()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var settings = fixture.Root.AddComponent<MyAvatar>();
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var hat = MenuTree.Walk(built.Root).First(e => e.Node.Name == "Hat").Node;
                settings.menuRemovals.AddRange(MenuSave.Removals.Of(MenuDraft.Apply(built, new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Delete, id = hat.Id } })).Items);
                // The hat's item is gone for now.
                var clothes = hat.Origin.Asset;
                var item = clothes.controls.Single(c => c.name == "Hat");
                clothes.controls.Remove(item);

                var now = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var dance = MenuTree.Walk(now.Root).First(e => e.Node.Name == "Dance").Node;
                var edits = new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Rename, id = dance.Id, text = "Groove" } };
                var outcome = MenuSave.Save(fixture.Root, edits, null, MenuSave.Removals.Of(MenuDraft.Apply(now, edits, MenuSave.SavedRemovals(fixture.Root, now, out _))));

                Assert.True(outcome.Saved, outcome.Message);
                Assert.AreEqual(1, settings.menuRemovals.Count, "the hat's removal is kept");
                clothes.controls.Insert(0, item);
                CollectionAssert.IsNotEmpty(MenuSave.SavedRemovals(fixture.Root, MenuBuild.Run(MenuInputs.Read(fixture.Root)), out _), "and acts again once the hat is back");
            }
        }

        // Only changes of the avatar or of what menus are made of make the editor and the budget work its menu out again.
        [Test]
        public void OnlyTheAvatarsChangesTouchItsMenu()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var elsewhere = new GameObject("Elsewhere");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(elsewhere, fixture.Scene);
                var material = new Material(Shader.Find("Standard"));
                try
                {
                    var child = new GameObject("Child");
                    child.transform.SetParent(fixture.Root.transform, false);
                    Assert.True(Touches(fixture.Root, (ref ObjectChangeEventStream.Builder b) => { var e = new ChangeGameObjectOrComponentPropertiesEventArgs(child.GetInstanceID(), fixture.Scene); b.PushChangeGameObjectOrComponentPropertiesEvent(ref e); }));
                    Assert.False(Touches(fixture.Root, (ref ObjectChangeEventStream.Builder b) => { var e = new ChangeGameObjectOrComponentPropertiesEventArgs(elsewhere.GetInstanceID(), fixture.Scene); b.PushChangeGameObjectOrComponentPropertiesEvent(ref e); }), "another object");
                    Assert.False(Touches(fixture.Root, (ref ObjectChangeEventStream.Builder b) => { var e = new ChangeAssetObjectPropertiesEventArgs(default, material.GetInstanceID(), default); b.PushChangeAssetObjectPropertiesEvent(ref e); }), "a material");
                    var menu = fixture.Root.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
                    Assert.True(Touches(fixture.Root, (ref ObjectChangeEventStream.Builder b) => { var e = new ChangeAssetObjectPropertiesEventArgs(default, menu.GetInstanceID(), default); b.PushChangeAssetObjectPropertiesEvent(ref e); }), "a menu");
                }
                finally
                {
                    Object.DestroyImmediate(elsewhere);
                    Object.DestroyImmediate(material);
                }
            }
        }

        private delegate void Push(ref ObjectChangeEventStream.Builder builder);

        private static bool Touches(GameObject root, Push push)
        {
            var builder = new ObjectChangeEventStream.Builder(Unity.Collections.Allocator.Temp);
            try
            {
                push(ref builder);
                var stream = builder.ToStream(Unity.Collections.Allocator.Temp);
                try { return MenuChanges.Read(ref stream).Touches(root); }
                finally { stream.Dispose(); }
            }
            finally { builder.Dispose(); }
        }
    }
}
