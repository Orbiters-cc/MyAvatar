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

        private static void Remove(MyAvatar settings, params string[] path) =>
            settings.menuRemovals.Add(new MenuRemoval { path = path.ToList(), nth = path.Select(_ => 0).ToList() });

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
        public void ItemsAreFoundOverVrcFurysPages()
        {
            var second = Menu("Next", MenuFixture.Control("Nine", ControlType.Toggle, "A"), MenuFixture.Control("Ten", ControlType.Toggle, "A"));
            var controls = Enumerable.Range(1, 7).Select(i => MenuFixture.Control("Item " + i, ControlType.Toggle, "A")).ToList();
            controls.Add(MenuFixture.Control("Next", ControlType.SubMenu, null, menu: second));
            var root = Menu("Root", controls.ToArray());
            var names = new HashSet<string> { "Next" };

            var found = MenuPaths.Find(root, new[] { "Ten" }, new[] { 0 }, names);

            Assert.NotNull(found);
            Assert.AreSame(second, found.Value.Menu);
            Assert.AreEqual(9, MenuPaths.Items(root, names).Count(), "the Next item stands for its page");
            Assert.True(MenuPaths.IsNext(root, controls[7], names));
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
                settings.menuRemovals.Add(new MenuRemoval { path = names, nth = nth });

                var clone = Object.Instantiate(fixture.Root);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(clone, fixture.Scene);
                try
                {
                    Assert.True(new MenuUploadBuild.Capture().OnPreprocessAvatar(clone));
                    var builder = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("VF.Builder.VRCFuryBuilder")).First(t => t != null);
                    var wrapper = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("VF.Utils.VFGameObject")).First(t => t != null);
                    var convert = wrapper.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "op_Implicit" && m.ReturnType == wrapper && m.GetParameters()[0].ParameterType == typeof(GameObject));
                    builder.GetMethod("RunMain", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new[] { convert.Invoke(null, new object[] { clone }) });
                    Assert.True(new MenuUploadBuild.Apply().OnPreprocessAvatar(clone));

                    var menu = clone.GetComponent<VRCAvatarDescriptor>().expressionsMenu;
                    var all = MenuPaths.Menus(menu).SelectMany(m => m.controls).ToList();
                    Assert.False(all.Any(c => c.name == "Toggle 4"), MenuFixture.Dump(menu));
                    Assert.True(all.Any(c => c.name == "Toggle 5"), "the others stay");
                    var next = MenuPaths.Menus(menu).Select(m => m.controls.LastOrDefault(c => MenuPaths.IsNext(m, c, new[] { "Next" }))).FirstOrDefault(c => c != null);
                    if (next != null) Assert.AreSame(OrbitersMenuIcons.Next, next.icon, "VRCFury's Next page gets the arrow");
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
