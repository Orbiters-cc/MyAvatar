using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // What a drop puts on: different items are offered (not picked silently), an add-on goes with its item, and an item
    // the avatar already wears is taken over instead of copied. Prefabs live in a temporary folder removed afterwards.
    public sealed class AccessoryChoiceTests
    {
        private Scene scene;
        private string folder;
        private readonly List<GameObject> owned = new List<GameObject>();

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            folder = "Assets/MyAvatarChoiceTest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
        }

        [TearDown] public void TearDown()
        {
            foreach (var go in owned) if (go) { Undo.ClearUndo(go); UnityEngine.Object.DestroyImmediate(go); }
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
            // Only the folder this test created.
            if (folder.StartsWith("Assets/MyAvatarChoiceTest-", StringComparison.Ordinal) && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
        }

        private MyAvatar Avatar()
        {
            var root = new GameObject("Avatar"); owned.Add(root);
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<MyAvatar>();
        }

        // A small prefab with a visible mesh.
        private string Prefab(string name, GameObject basedOn = null)
        {
            var source = basedOn != null ? (GameObject)PrefabUtility.InstantiatePrefab(basedOn) : GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = name;
            UnityEngine.Object.DestroyImmediate(source.GetComponent<Collider>());
            string path = folder + "/" + name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(source, path);
            UnityEngine.Object.DestroyImmediate(source);
            return path;
        }

        private static IEnumerator Wait(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 20;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Task did not finish.");
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        private static List<OrbitersAttachment> Installed(MyAvatar avatar) => avatar.GetComponentsInChildren<OrbitersAttachment>(true).ToList();

        [Test] public void AddonNamesAreRecognised()
        {
            Assert.That(AccessoryCandidates.IsAddon("Extra Tentacles"), Is.True);
            Assert.That(AccessoryCandidates.IsAddon("Hoodie Add-On"), Is.True);
            Assert.That(AccessoryCandidates.IsAddon("Hoodie_AddOn"), Is.True);
            Assert.That(AccessoryCandidates.IsAddon("Body Tentacles"), Is.False);
            Assert.That(AccessoryCandidates.IsAddon("Paddington Hat"), Is.False);
        }

        // Two different items of equal standing: nothing is put on before the user answers; "Add both" puts on both.
        [UnityTest] public IEnumerator TwoDifferentItemsAreOfferedThenAddedTogether()
        {
            var avatar = Avatar();
            Prefab("Body Tentacles"); Prefab("Extra Tentacles");
            var drop = AccessoryService.DropAsync(avatar, new[] { folder }, (_, __) => { }, CancellationToken.None);
            yield return Wait(drop);
            Assert.That(Installed(avatar), Is.Empty, "Nothing is chosen for the user.");
            var options = AccessoryService.PendingOptions(avatar, out bool hands);
            Assert.That(hands, Is.False);
            Assert.That(options.Select(o => o.label), Is.EquivalentTo(new[] { "Body Tentacles", "Extra Tentacles" }));

            var both = AccessoryService.ChooseAsync(avatar, AccessoryService.AllOptions, (_, __) => { }, CancellationToken.None);
            yield return Wait(both);
            Assert.That(Installed(avatar).Select(a => a.name), Is.EquivalentTo(new[] { "Body Tentacles", "Extra Tentacles" }));
            Assert.That(AccessoryService.PendingOptions(avatar, out _), Is.Empty);
            Assert.That(avatar.accessoryStatus, Does.Not.Contain("left out"));
        }

        // A waiting choice keeps only what it needs in the scene. "Add both" is one Undo step; an item that fails while being
        // put on is taken back alone (its copy destroyed, said in a note) and the other stays.
        [UnityTest] public IEnumerator ADropIsOneUndoStepAndAFailingItemIsTakenBackAlone()
        {
            var avatar = Avatar();
            Prefab("Body Tentacles"); Prefab("Extra Tentacles");
            yield return Wait(AccessoryService.DropAsync(avatar, new[] { folder }, (_, __) => { }, CancellationToken.None));
            var pending = JsonUtility.FromJson<AccessoryService.Job>(avatar.accessoryChoice);
            Assert.That(pending.guids, Is.Empty, "No GUID list in the scene.");
            Assert.That(pending.imports, Is.Empty);
            Assert.That(pending.drop.assets.Count, Is.EqualTo(2));

            var hooks = (List<Action<GameObject, Transform>>)typeof(AttachmentHooks).GetField("Hooks", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Action<GameObject, Transform> fail = (placed, _) => { if (placed.name == "Extra Tentacles") throw new InvalidOperationException("Fixture failure."); };
            AttachmentHooks.Register(fail);
            try
            {
                LogAssert.Expect(LogType.Exception, new Regex("Fixture failure"));
                yield return Wait(AccessoryService.ChooseAsync(avatar, AccessoryService.AllOptions, (_, __) => { }, CancellationToken.None));
            }
            finally { hooks.Remove(fail); }
            Assert.That(Installed(avatar).Select(a => a.name), Is.EqualTo(new[] { "Body Tentacles" }));
            Assert.That(avatar.transform.childCount, Is.EqualTo(1), "The failed copy was taken back.");
            Assert.That(avatar.accessoryNotes.Any(n => n.warning && n.text.StartsWith("Extra Tentacles could not be put on", StringComparison.Ordinal)), Is.True);
            Undo.PerformUndo();
            Assert.That(avatar.transform.childCount, Is.Zero, "One Undo takes the drop back.");
        }

        // Notes about an accessory (or an object of it) go once it is gone, removed or undone; notes about nothing on the
        // avatar stay.
        [Test] public void NotesOfAGoneAccessoryLeaveWithIt()
        {
            var avatar = Avatar();
            var hat = new GameObject("Hat"); hat.transform.SetParent(avatar.transform, false);
            var attachment = hat.AddComponent<OrbitersAttachment>();
            var brim = new GameObject("Brim"); brim.transform.SetParent(hat.transform, false);
            avatar.accessoryNotes.Add(new MyAvatar.AccessoryNote { accessory = attachment, text = "Install VRCFury.", vrcFury = true });
            avatar.accessoryNotes.Add(new MyAvatar.AccessoryNote { target = brim, text = "Set up the brim by hand.", warning = true });
            avatar.accessoryNotes.Add(new MyAvatar.AccessoryNote { text = "A refused file" });
            UnityEngine.Object.DestroyImmediate(hat);
            var section = new AccessoriesSection(avatar, new AccessoriesSection.Host
            {
                Run = work => work(), AiOn = () => false, SetAi = _ => { }, ApplyTextures = (_, __, ___) => Task.CompletedTask, Background = _ => { },
            });
            try
            {
                typeof(AccessoriesSection).GetMethod("RefreshNotes", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(section, null);
                Assert.That(avatar.accessoryNotes.Select(n => n.text), Is.EqualTo(new[] { "A refused file" }));
            }
            finally { section.Detach(); }
        }

        // The prefab put on by hand earlier is taken over: one copy, managed (Remove can take it off), not a second one.
        [UnityTest] public IEnumerator SamePrefabWornByHandIsTakenOverNotCopied()
        {
            var avatar = Avatar();
            string hat = Prefab("Hat");
            var worn = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(hat), avatar.transform);
            var drop = AccessoryService.DropAsync(avatar, new[] { hat }, (_, __) => { }, CancellationToken.None);
            yield return Wait(drop);
            var outcomes = ((Task<List<AccessoryService.Outcome>>)drop).Result;
            Assert.That(avatar.transform.childCount, Is.EqualTo(1), "No second copy.");
            Assert.That(outcomes.Single().adopted, Is.True);
            var attachment = worn.GetComponent<OrbitersAttachment>();
            Assert.That(attachment, Is.Not.Null);
            Assert.That(attachment.created, Is.True, "Remove takes it off like one My Avatar placed.");
            Assert.That(avatar.accessoryStatus, Does.Contain("already on the avatar"));
        }

        // A fit question whose accessory is no longer on the avatar is dropped, so no "Does it fit?" card stays alone.
        [Test] public void FitQuestionLeavesWithItsAccessory()
        {
            var avatar = Avatar();
            var hat = new GameObject("Hat"); hat.transform.SetParent(avatar.transform, false);
            var attachment = hat.AddComponent<OrbitersAttachment>();
            avatar.accessoryNotes.Add(new MyAvatar.AccessoryNote { accessory = attachment, fit = "ask" });
            avatar.accessoryNotes.Add(new MyAvatar.AccessoryNote { accessory = null, fit = "ask" });
            avatar.accessoryNotes.Add(new MyAvatar.AccessoryNote { text = "A refused file" });
            var section = new AccessoriesSection(avatar, new AccessoriesSection.Host
            {
                Run = work => work(), AiOn = () => false, SetAi = _ => { }, ApplyTextures = (_, __, ___) => Task.CompletedTask, Background = _ => { },
            });
            try
            {
                typeof(AccessoriesSection).GetMethod("RefreshNotes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(section, null);
                Assert.That(avatar.accessoryNotes.Count(n => n.fit == "ask"), Is.EqualTo(1));
                Assert.That(avatar.accessoryNotes.Single(n => n.fit == "ask").accessory, Is.SameAs(attachment));
                Assert.That(avatar.accessoryNotes.Any(n => n.text == "A refused file"), Is.True, "Other notes stay.");
            }
            finally { section.Detach(); }
        }

        // A variant of an item the avatar wears (same base prefab or model) is the same item: asked about, not added.
        [UnityTest] public IEnumerator VariantOfAWornItemIsReportedNotAdded()
        {
            var avatar = Avatar();
            string hat = Prefab("Hat");
            string variant = Prefab("Hat Variant", AssetDatabase.LoadAssetAtPath<GameObject>(hat));
            yield return Wait(AccessoryService.DropAsync(avatar, new[] { hat }, (_, __) => { }, CancellationToken.None));
            Assert.That(Installed(avatar).Count, Is.EqualTo(1));
            yield return Wait(AccessoryService.DropAsync(avatar, new[] { variant }, (_, __) => { }, CancellationToken.None));
            Assert.That(Installed(avatar).Count, Is.EqualTo(1), "The variant is not put on as a second item.");
            Assert.That(avatar.accessoryNotes.Any(n => n.duplicate == variant), Is.True, "Replace or Add another is offered.");
        }
    }
}
