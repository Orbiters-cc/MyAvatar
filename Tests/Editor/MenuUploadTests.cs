using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Expressions;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VrcFury = Orbiters.Toolkit.Editor.VRChat.VrcFury;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>What an upload leaves out: deleted menu items, the default menu, hidden looks, and what only they drive.</summary>
    public sealed class MenuUploadTests
    {
        private readonly List<Object> owned = new List<Object>();
        private UnityEngine.SceneManagement.Scene scene;

        [SetUp]
        public void Open() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown]
        public void Clean()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        // An avatar of the kinds a creator's menu holds: a hat toggle, material buttons sharing an int, a pat reaction its
        // menu switches off (mixed with a contact), all in the descriptor's own menu.
        private sealed class Avatar
        {
            public GameObject Root, Hat, Halo;
            public MeshRenderer Body;
            public Material Red, Blue;
            public AnimatorController Fx;
            public VRCExpressionParameters Parameters;
            public VRCExpressionsMenu Menu;
            public MyAvatar Settings;
        }

        private Avatar Make()
        {
            var a = new Avatar { Root = new GameObject("Avatar") };
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(a.Root, scene);
            a.Root.AddComponent<Animator>();
            a.Settings = a.Root.AddComponent<MyAvatar>();
            a.Hat = Child(a.Root, "Hat"); a.Hat.SetActive(false);
            a.Hat.AddComponent<MeshFilter>(); a.Hat.AddComponent<MeshRenderer>();
            a.Halo = Child(a.Root, "Halo"); a.Halo.SetActive(false);
            a.Red = Own(new Material(Shader.Find("Standard")) { name = "Red" }); a.Blue = Own(new Material(Shader.Find("Standard")) { name = "Blue" });
            a.Body = Child(a.Root, "Body").AddComponent<MeshRenderer>();
            a.Body.sharedMaterial = a.Red;

            var fx = a.Fx = Own(new AnimatorController { name = "FX" });
            foreach (var (name, type) in new[] { ("Hat", AnimatorControllerParameterType.Bool), ("MaterialInt", AnimatorControllerParameterType.Int), ("NoPat", AnimatorControllerParameterType.Bool), ("PatContact", AnimatorControllerParameterType.Float) })
                fx.AddParameter(name, type);
            Layer(fx, "Base").AddState("Idle");
            var hat = Layer(fx, "Hat");
            var off = hat.AddState("Off"); off.motion = Active("Hat", false);
            var on = hat.AddState("On"); on.motion = Active("Hat", true);
            off.AddTransition(on).AddCondition(AnimatorConditionMode.If, 0, "Hat");
            on.AddTransition(off).AddCondition(AnimatorConditionMode.IfNot, 0, "Hat");
            var materials = Layer(fx, "Materials");
            var red = materials.AddState("Red"); red.motion = Swap(a.Red);
            var blue = materials.AddState("Blue"); blue.motion = Swap(a.Blue);
            materials.AddAnyStateTransition(red).AddCondition(AnimatorConditionMode.Equals, 0, "MaterialInt");
            materials.AddAnyStateTransition(blue).AddCondition(AnimatorConditionMode.Equals, 1, "MaterialInt");
            var pat = Layer(fx, "Pat");
            var idle = pat.AddState("Idle");
            var patted = pat.AddState("Patted"); patted.motion = Active("Halo", true);
            var toPatted = idle.AddTransition(patted);
            toPatted.AddCondition(AnimatorConditionMode.Greater, .5f, "PatContact"); toPatted.AddCondition(AnimatorConditionMode.IfNot, 0, "NoPat");
            idle.AddTransition(patted).AddCondition(AnimatorConditionMode.If, 0, "NoPat");
            patted.AddTransition(idle).AddCondition(AnimatorConditionMode.Less, .5f, "PatContact");

            a.Parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            a.Parameters.parameters = new[]
            {
                new VRCExpressionParameters.Parameter { name = "Hat", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true },
                new VRCExpressionParameters.Parameter { name = "MaterialInt", valueType = VRCExpressionParameters.ValueType.Int, networkSynced = true },
                new VRCExpressionParameters.Parameter { name = "NoPat", valueType = VRCExpressionParameters.ValueType.Bool, networkSynced = true },
            };
            var swaps = Menu("Materials", MenuFixture.Control("Red", ControlType.Button, "MaterialInt"), MenuFixture.Control("Blue", ControlType.Button, "MaterialInt"));
            swaps.controls[0].value = 0;
            a.Menu = Menu("Root", MenuFixture.Control("Hat", ControlType.Toggle, "Hat"), MenuFixture.Control("Materials", ControlType.SubMenu, null, menu: swaps),
                MenuFixture.Control("No pat", ControlType.Toggle, "NoPat"));
            var descriptor = a.Root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customExpressions = true;
            descriptor.expressionsMenu = a.Menu;
            descriptor.expressionParameters = a.Parameters;
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
            return a;
        }

        // Leaves the item at a path out, as the menu editor saves it (what it is, to tell it from a new item of that name).
        private static void Remove(MyAvatar settings, params string[] path)
        {
            var nth = path.Select(_ => 0).ToList();
            var node = MenuPaths.Find(MenuBuild.Run(MenuInputs.Read(settings.gameObject)).Root, path, nth) ?? throw new AssertionException("No item at " + string.Join("/", path));
            settings.menuRemovals.Add(new MenuRemoval { path = path.ToList(), nth = nth, type = (int)node.Type, parameter = MenuSave.ItemParameter(node), source = node.Origin?.Component, fromComponent = node.Origin?.Component != null });
        }

        [Test]
        public void DeletedItemsTakeWhatOnlyTheyDriveWithThem()
        {
            var a = Make();
            Remove(a.Settings, "Hat"); Remove(a.Settings, "Materials"); Remove(a.Settings, "No pat");

            string log = MenuUploadBuild.Upload(a.Root);

            Assert.AreEqual(0, a.Menu.controls.Count, "the items are out of the menu: " + log);
            Assert.AreEqual(0, a.Parameters.parameters.Length, "their parameters free their synced bits");
            CollectionAssert.AreEqual(new[] { "Base", "Pat" }, a.Fx.layers.Select(l => l.name), "the hat and the material switch are gone");
            Assert.True(a.Hat == null, "the hat can't show any more: its object is deleted");
            Assert.AreSame(a.Red, a.Body.sharedMaterial, "the material it starts with is the avatar's");
            Assert.False(a.Fx.animationClips.SelectMany(c => AnimationUtility.GetObjectReferenceCurveBindings(c).SelectMany(b => AnimationUtility.GetObjectReferenceCurve(c, b)))
                .Any(k => k.value == a.Blue), "nothing refers to the other material any more: it stays out of the upload");
            var pat = a.Fx.layers.Single(l => l.name == "Pat").stateMachine;
            var toPatted = pat.states.Single(s => s.state.name == "Idle").state.transitions;
            Assert.AreEqual(1, toPatted.Length, "what only happens with petting switched off can't happen any more");
            CollectionAssert.AreEqual(new[] { "PatContact" }, toPatted[0].conditions.Select(c => c.parameter), "petting itself keeps working");
            Assert.True(a.Halo != null, "the halo petting shows stays");
        }

        [Test]
        public void ParametersOtherControlsStillSetOnlyLoseTheValuesThatWent()
        {
            var a = Make();
            Remove(a.Settings, "Materials", "Blue");

            MenuUploadBuild.Upload(a.Root);

            Assert.True(a.Parameters.parameters.Any(p => p.name == "MaterialInt"), "the red button still sets it");
            Assert.False(a.Fx.layers.Any(l => l.name == "Materials"), "only red can show: the layer is written into the avatar");
            Assert.AreSame(a.Red, a.Body.sharedMaterial);
            Assert.True(a.Hat != null && a.Fx.layers.Any(l => l.name == "Hat"), "the hat keeps its toggle");
        }

        [Test]
        public void TheParameterBudgetCountsWhatTheUploadFrees()
        {
            var a = Make();
            int before = Orbiters.Toolkit.Editor.VRChat.Parameters.AvatarParameterBudget.Estimate(a.Root).DescriptorBits;
            Remove(a.Settings, "Hat"); Remove(a.Settings, "Materials", "Blue");

            var freed = MenuUploadEstimate.Of(a.Root);

            CollectionAssert.AreEquivalent(new[] { "Hat" }, freed.Descriptor, "the red button still sets the material int");
            Assert.AreEqual(before - 1, Orbiters.Toolkit.Editor.VRChat.Parameters.AvatarParameterBudget.Estimate(a.Root).DescriptorBits, "the hat's bit is not counted");
            MenuUploadBuild.Upload(a.Root);
            CollectionAssert.AreEquivalent(new[] { "MaterialInt", "NoPat" }, a.Parameters.parameters.Select(p => p.name), "the upload frees the same");
        }

        [Test]
        public void TheDefaultMenuGoesButWhatOthersAddToItsFoldersStays()
        {
            if (!VrcFury.Installed) Assert.Ignore("VRCFury is not installed.");
            using (var fixture = new MenuFixture(many: false))
            {
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var items = MenuUploadBuild.DefaultItems(built.Root);
                Assert.True(items.All(n => n.Origin?.Kind == MenuOriginKind.AvatarMenu));
                var names = items.Select(n => n.Name).ToList();
                CollectionAssert.IsSubsetOf(new[] { "Hat", "Glasses", "Lights", "Loop" }, names);
                Assert.False(names.Contains("Clothes"), "VRCFury's Shirt is in the avatar's Clothes folder: the folder stays");
                Assert.False(names.Contains("Shirt"));
            }
        }

        [Test]
        public void HiddenRexouiumFeathersAreLeftOutWithTheirToggle()
        {
            var root = new GameObject("Avatar"); UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<Animator>();
            var feathers = Child(root, "Feathers");
            var head = Child(feathers, "Head");
            head.AddComponent<MeshFilter>(); head.AddComponent<MeshRenderer>();
            var fx = Own(new AnimatorController { name = "FX" });
            fx.AddParameter("FthrsHeadOff", AnimatorControllerParameterType.Bool);
            Layer(fx, "Base").AddState("Idle");
            var toggle = Layer(fx, "Fthrs Head Toggle");
            var shown = toggle.AddState("Shown");
            var hidden = toggle.AddState("Hidden"); hidden.motion = Active("Feathers/Head", false);
            shown.AddTransition(hidden).AddCondition(AnimatorConditionMode.If, 0, "FthrsHeadOff");
            hidden.AddTransition(shown).AddCondition(AnimatorConditionMode.IfNot, 0, "FthrsHeadOff");
            var parameters = Own(ScriptableObject.CreateInstance<VRCExpressionParameters>());
            parameters.parameters = new[] { new VRCExpressionParameters.Parameter { name = "FthrsHeadOff", valueType = VRCExpressionParameters.ValueType.Bool, saved = true, networkSynced = true, defaultValue = 1 } };
            var menu = Menu("Root", MenuFixture.Control("Head feathers off", ControlType.Toggle, "FthrsHeadOff"));
            var descriptor = root.AddComponent<VRCAvatarDescriptor>();
            descriptor.customExpressions = true; descriptor.expressionsMenu = menu; descriptor.expressionParameters = parameters;
            descriptor.customizeAnimationLayers = true;
            descriptor.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, animatorController = fx } };
            head.SetActive(false);

            MenuUploadBuild.Upload(root);

            Assert.True(head == null, "hidden feathers are not uploaded: no mesh, material or texture of theirs");
            Assert.AreEqual(0, menu.controls.Count, "their toggle goes with them");
            Assert.AreEqual(0, parameters.parameters.Length);
            CollectionAssert.AreEqual(new[] { "Base" }, fx.layers.Select(l => l.name));
        }

        [Test]
        public void PathsFollowVrcFurysPagesAndNothingElse()
        {
            var second = Menu("Next", MenuFixture.Control("Nine", ControlType.Toggle, "A"), MenuFixture.Control("Ten", ControlType.Toggle, "A"));
            var controls = Enumerable.Range(1, 7).Select(i => MenuFixture.Control("Item " + i, ControlType.Toggle, "A")).ToList();
            controls.Add(MenuFixture.Control("Next", ControlType.SubMenu, null, menu: second));
            var root = Menu("Root", controls.ToArray());

            var paged = MenuPaths.Find(root, new[] { new MenuPaths.Step("Next", 0, true), new MenuPaths.Step("Ten", 0, false) });
            Assert.NotNull(paged, "VRCFury's Next item: the last of a full page");
            Assert.AreSame(second, paged.Value.Menu);
            Assert.Null(MenuPaths.Find(root, new[] { new MenuPaths.Step("Ten", 0, false) }), "an item of the next page is not on this one");
            // The same menu made by a creator: their folder called "Next" is a folder like any other.
            var folder = MenuPaths.Find(root, new[] { new MenuPaths.Step("Next", 0, false), new MenuPaths.Step("Nine", 0, false) });
            Assert.NotNull(folder);
            Assert.AreSame(second, folder.Value.Menu);
            Assert.Null(MenuPaths.Find(Menu("Short", MenuFixture.Control("Next", ControlType.SubMenu, null, menu: second)), new[] { new MenuPaths.Step("Next", 0, true) }),
                "a page that isn't full has no Next item");
        }

        // A creator's own folder named "Next" in the eighth place (or the one the editor makes to put an item back) keeps its
        // items: only the pages VRCFury makes are pages.
        [Test]
        public void ACreatorsFolderCalledNextIsNotAPage()
        {
            var a = Make();
            var inside = Menu("Next", MenuFixture.Control("Inner", ControlType.Toggle, "Hat"), MenuFixture.Control("Kept", ControlType.Toggle, "NoPat"));
            for (int i = 0; i < 4; i++) a.Menu.controls.Add(MenuFixture.Control("Extra " + i, ControlType.Button, "MaterialInt"));
            a.Menu.controls.Add(MenuFixture.Control("Next", ControlType.SubMenu, null, menu: inside));
            Assert.AreEqual(MenuBuild.MaxControls, a.Menu.controls.Count);
            Remove(a.Settings, "Next", "Inner");

            string log = MenuUploadBuild.Upload(a.Root);

            CollectionAssert.AreEqual(new[] { "Kept" }, inside.controls.Select(c => c.name), log);
            Assert.True(a.Menu.controls.Any(c => c.name == "Next"), "the folder stays, with what is left in it");
            Assert.True(a.Menu.controls.Any(c => c.name == "Hat"), "the main menu is not taken for a page and closed up");
        }

        // Modular Avatar's items in the descriptor's menu (merged before VRCFury) are not the avatar's own: "Remove default menu"
        // leaves them, and items on Modular Avatar's "More" pages are found. The plan is made before Modular Avatar builds, the
        // menu it built is changed after.
        [Test]
        public void TheDefaultMenuGoesAndModularAvatarsItemsStay()
        {
            using (var fixture = new MenuFixture(vrcFury: false))
            {
                var ma = MenuEditorTests.ModularAvatarSetup(fixture);
                var settings = fixture.Root.AddComponent<MyAvatar>();
                settings.removeDefaultMenu = true;
                var built = MenuBuild.Run(ma.Inputs());
                var outfit = MenuTree.Walk(built.Root).First(e => e.Node.Name == "Outfit").Node;
                var sock = MenuTree.Walk(built.Root).First(e => e.Node.Name == "Sock1").Node;
                var names = new List<string>(); var nth = new List<int>();
                Assert.True(MenuPaths.Of(built.Root, sock, names, nth));
                CollectionAssert.AreEqual(new[] { "More", "Outfit", "Sock1" }, names, "Sock1 is on Modular Avatar's More page");
                settings.menuRemovals.Add(new MenuRemoval { path = names, nth = nth, type = (int)sock.Type, parameter = MenuSave.ItemParameter(sock), source = sock.Origin.Component, fromComponent = true });
                CollectionAssert.DoesNotContain(MenuUploadBuild.DefaultItems(built.Root).Select(n => n.Name), "Outfit");

                var plan = MenuUploadBuild.PlanFor(fixture.Root, ma.Inputs());
                var descriptor = fixture.Root.GetComponent<VRCAvatarDescriptor>();
                // What NDMF leaves on the descriptor: Modular Avatar's menu, as plain menus.
                descriptor.expressionsMenu = ma.Built(fixture);
                string log = MenuUploadBuild.Run(fixture.Root, plan);

                var left = MenuPaths.Menus(descriptor.expressionsMenu).SelectMany(m => m.controls).Select(c => c.name).ToList();
                CollectionAssert.IsSubsetOf(new[] { "A", "More", "B", "Outfit", "Vee", "C", "Zed", "<b>Hat</b> 2", "Sock2", "Wing" }, left, log);
                CollectionAssert.DoesNotContain(left, "Sock1", "an item on Modular Avatar's More page is found and left out");
                foreach (var own in new[] { "Clothes", "Effects", "Dance", "Loop", "Hat", "Glasses" }) CollectionAssert.DoesNotContain(left, own, "the avatar's own menu goes");
            }
        }

        // An item of the same name put where a left-out item was is not left out in its place.
        [Test]
        public void ANewItemOfTheSameNameIsNotLeftOut()
        {
            var a = Make();
            var built = MenuBuild.Run(MenuInputs.Read(a.Root));
            var hat = MenuTree.Walk(built.Root).First(e => e.Node.Name == "Hat").Node;
            var draft = MenuDraft.Apply(built, new List<MenuEdit> { new MenuEdit { kind = MenuEditKind.Delete, id = hat.Id } });
            a.Settings.menuRemovals.AddRange(MenuSave.Removals.Of(draft).Items);
            // The hat toggle replaced by a hat button switching the material.
            a.Menu.controls[0] = MenuFixture.Control("Hat", ControlType.Button, "MaterialInt");

            MenuUploadBuild.Upload(a.Root);

            Assert.True(a.Menu.controls.Any(c => c.name == "Hat"), "the new Hat stays");
            Assert.True(a.Parameters.parameters.Any(p => p.name == "MaterialInt"));
        }

        // A folder left out whose menu another folder still opens: what is inside stays in the upload, so the budget doesn't
        // count its bits as freed.
        [Test]
        public void ASharedSubmenuKeepsWhatItSetsInTheBudget()
        {
            var a = Make();
            var swaps = a.Menu.controls.First(c => c.name == "Materials").subMenu;
            a.Menu.controls.Add(MenuFixture.Control("Materials again", ControlType.SubMenu, null, menu: swaps));
            Remove(a.Settings, "Materials");

            CollectionAssert.DoesNotContain(MenuUploadEstimate.Of(a.Root).Descriptor, "MaterialInt", "the other folder still opens the buttons");
            MenuUploadBuild.Upload(a.Root);
            Assert.True(a.Parameters.parameters.Any(p => p.name == "MaterialInt"), "the upload keeps it too");
        }

        // A parameter apps set over OSC (a heart rate display) stays when the menu item that also set it goes.
        [Test]
        public void ParametersAppsSetOverOscStay()
        {
            var a = Make();
            a.Parameters.parameters = a.Parameters.parameters.Append(new VRCExpressionParameters.Parameter { name = "HR", valueType = VRCExpressionParameters.ValueType.Int, networkSynced = true }).ToArray();
            a.Fx.AddParameter("HR", AnimatorControllerParameterType.Int);
            a.Menu.controls.Add(MenuFixture.Control("Heart rate test", ControlType.RadialPuppet, null, sub: "HR"));
            Remove(a.Settings, "Heart rate test");

            CollectionAssert.DoesNotContain(MenuUploadEstimate.Of(a.Root).Descriptor, "HR");
            string log = MenuUploadBuild.Upload(a.Root);
            Assert.True(a.Parameters.parameters.Any(p => p.name == "HR"), log);
            Assert.False(a.Menu.controls.Any(c => c.name == "Heart rate test"), "its menu item goes");
        }

        [Test]
        public void TheUploadLeavesDeletedItemsOutOfWhatVrcFuryBuildsAndGivesItsPagesTheArrow()
        {
            if (!VrcFury.Installed || VrcFury.Find("VF.Builder.VRCFuryBuilder") == null) Assert.Ignore("VRCFury is not installed.");
            using (var fixture = new MenuFixture())
            {
                var settings = fixture.Root.AddComponent<MyAvatar>();
                var built = MenuBuild.Run(MenuInputs.Read(fixture.Root));
                var names = new List<string>(); var nth = new List<int>();
                var target = MenuTree.Walk(built.Root).First(e => e.Node.Name == "Toggle 4").Node;
                Assert.True(MenuPaths.Of(built.Root, target, names, nth));
                settings.menuRemovals.Add(new MenuRemoval { path = names, nth = nth, type = (int)target.Type, parameter = MenuSave.ItemParameter(target), source = target.Origin.Component, fromComponent = target.Origin.Component != null });

                var clone = Object.Instantiate(fixture.Root);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, fixture.Scene);
                try
                {
                    Assert.True(new MenuUploadBuild.Capture().OnPreprocessAvatar(clone));
                    VrcFury.RunMain(clone);
                    Assert.True(new MenuUploadBuild.Apply().OnPreprocessAvatar(clone));

                    var menu = clone.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
                    var all = MenuPaths.Menus(menu).SelectMany(m => m.controls).ToList();
                    Assert.False(all.Any(c => c.name == "Toggle 4"), MenuFixture.Dump(menu));
                    Assert.True(all.Any(c => c.name == "Toggle 5"), "the others stay");
                    // The fixture's main menu holds more than 8 items: VRCFury splits it, and its Next item gets the arrow.
                    var pages = MenuPaths.Menus(menu).Where(m => m.controls.Count == MenuBuild.MaxControls && m.controls[MenuBuild.MaxControls - 1].type == ControlType.SubMenu &&
                                                                 m.controls[MenuBuild.MaxControls - 1].name == "Next").ToList();
                    Assert.IsNotEmpty(pages, MenuFixture.Dump(menu));
                    foreach (var page in pages) Assert.AreSame(OrbitersMenuIcons.Next, page.controls[MenuBuild.MaxControls - 1].icon, "VRCFury's Next page gets the arrow");
                }
                finally
                {
                    Object.DestroyImmediate(clone);
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        // ---- Building blocks ----

        private T Own<T>(T o) where T : Object { owned.Add(o); return o; }

        private GameObject Child(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private AnimatorStateMachine Layer(AnimatorController fx, string name)
        {
            var machine = Own(new AnimatorStateMachine { name = name });
            fx.AddLayer(new AnimatorControllerLayer { name = name, stateMachine = machine, defaultWeight = 1 });
            return machine;
        }

        private AnimationClip Active(string path, bool on)
        {
            var clip = Own(new AnimationClip { name = path + (on ? " on" : " off") });
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0, 1, on ? 1 : 0));
            return clip;
        }

        private AnimationClip Swap(Material material)
        {
            var clip = Own(new AnimationClip { name = material.name });
            AnimationUtility.SetObjectReferenceCurve(clip, EditorCurveBinding.PPtrCurve("Body", typeof(MeshRenderer), "m_Materials.Array.data[0]"),
                new[] { new ObjectReferenceKeyframe { time = 0, value = material } });
            return clip;
        }

        private VRCExpressionsMenu Menu(string name, params VRCExpressionsMenu.Control[] controls)
        {
            var menu = Own(ScriptableObject.CreateInstance<VRCExpressionsMenu>());
            menu.name = name;
            menu.controls = controls.ToList();
            return menu;
        }
    }
}
