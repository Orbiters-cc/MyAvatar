using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // My Avatar's one change detector (architecture review MAC-2): a hierarchy change reaches the sections of the avatar it
    // happened on, not those of another avatar, and each watcher hears a burst once.
    public sealed class AvatarChangesTests
    {
        private Scene scene;
        private readonly List<System.IDisposable> watches = new List<System.IDisposable>();

        [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown] public void TearDown()
        {
            foreach (var watch in watches) watch.Dispose();
            watches.Clear();
            AvatarChanges.Clock = () => UnityEditor.EditorApplication.timeSinceStartup;
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private MyAvatar Avatar(string name)
        {
            var root = new GameObject(name);
            SceneManager.MoveGameObjectToScene(root, scene);
            var hair = new GameObject("Hair"); hair.transform.SetParent(root.transform, false);
            hair.AddComponent<MeshFilter>();
            hair.AddComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard"));
            return root.AddComponent<MyAvatar>();
        }

        [Test] public void TheSignatureFollowsWhatSectionsRead()
        {
            var avatar = Avatar("A");
            long signature = AvatarChanges.Signature(avatar);
            Assert.AreEqual(signature, AvatarChanges.Signature(avatar), "unchanged");
            var hair = avatar.transform.Find("Hair");
            hair.name = "Long hair";
            Assert.AreNotEqual(signature, signature = AvatarChanges.Signature(avatar), "renamed");
            hair.gameObject.SetActive(false);
            Assert.AreNotEqual(signature, signature = AvatarChanges.Signature(avatar), "hidden");
            hair.gameObject.AddComponent<BoxCollider>();
            Assert.AreNotEqual(signature, signature = AvatarChanges.Signature(avatar), "component added");
            hair.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard"));
            Assert.AreNotEqual(signature, signature = AvatarChanges.Signature(avatar), "material replaced");
            new GameObject("Hat").transform.SetParent(avatar.transform, false);
            Assert.AreNotEqual(signature, AvatarChanges.Signature(avatar), "object added");
        }

        [Test] public void AHierarchyChangeReachesOnlyItsAvatar()
        {
            MyAvatar a = Avatar("A"), b = Avatar("B");
            var heard = new List<(string, AvatarFacets)>();
            watches.Add(AvatarChanges.Watch(a, AvatarFacets.Hierarchy, f => heard.Add(("A", f))));
            watches.Add(AvatarChanges.Watch(a, AvatarFacets.Hierarchy, f => heard.Add(("A2", f))));
            watches.Add(AvatarChanges.Watch(b, AvatarFacets.Hierarchy | AvatarFacets.Undo, f => heard.Add(("B", f))));
            b.transform.Find("Hair").name = "Long hair";
            AvatarChanges.HierarchyChanged();
            AvatarChanges.HierarchyChanged();
            AvatarChanges.Flush(now: true);
            CollectionAssert.AreEqual(new[] { ("B", AvatarFacets.Hierarchy) }, heard);
            heard.Clear();
            AvatarChanges.HierarchyChanged();
            AvatarChanges.Flush(now: true);
            CollectionAssert.IsEmpty(heard, "nothing changed since");
            a.transform.Find("Hair").gameObject.SetActive(false);
            watches[1].Dispose();
            AvatarChanges.HierarchyChanged();
            AvatarChanges.Flush(now: true);
            CollectionAssert.AreEqual(new[] { ("A", AvatarFacets.Hierarchy) }, heard, "a disposed watch hears nothing");
        }

        // A script replacing a component without Undo (removed, then one of the same type added): same count, other component.
        [Test] public void ASignatureSeesAComponentReplaced()
        {
            var avatar = Avatar("A");
            var hair = avatar.transform.Find("Hair").gameObject;
            hair.AddComponent<BoxCollider>();
            long signature = AvatarChanges.Signature(avatar);
            Object.DestroyImmediate(hair.GetComponent<BoxCollider>());
            hair.AddComponent<BoxCollider>();
            Assert.AreNotEqual(signature, AvatarChanges.Signature(avatar));
        }

        // Without Flush(now): an avatar's change is told once its own burst settles, whatever changes elsewhere, and a burst
        // that goes on is told within MaxLatency.
        [Test] public void ChangesElsewhereDoNotHoldAnAvatarBack()
        {
            double time = 100;
            AvatarChanges.Clock = () => time;
            MyAvatar a = Avatar("A");
            var elsewhere = new GameObject("Light");
            SceneManager.MoveGameObjectToScene(elsewhere, scene);
            var heard = new List<(double, AvatarFacets)>();
            watches.Add(AvatarChanges.Watch(a, AvatarFacets.Hierarchy | AvatarFacets.Components, f => heard.Add((time, f))));
            AvatarChanges.Touched(a.transform.Find("Hair").gameObject.GetInstanceID(), AvatarFacets.Hierarchy);
            for (int step = 1; step <= 10 && heard.Count == 0; step++)
            {
                time = 100 + step * .1;
                AvatarChanges.Touched(elsewhere.GetInstanceID(), AvatarFacets.Components);
                AvatarChanges.Flush(now: false);
            }
            Assert.AreEqual(1, heard.Count, "told although another object kept changing");
            Assert.That(heard[0].Item1, Is.InRange(100.3, 100.41));
            Assert.AreEqual(AvatarFacets.Hierarchy, heard[0].Item2);

            heard.Clear();
            time = 200;
            for (int step = 0; step <= 30 && heard.Count == 0; step++)
            {
                time = 200 + step * .1;
                AvatarChanges.Touched(a.gameObject.GetInstanceID(), AvatarFacets.Components);
                AvatarChanges.Flush(now: false);
            }
            Assert.AreEqual(1, heard.Count, "a burst that goes on is told");
            Assert.That(heard[0].Item1 - 200, Is.InRange(AvatarChanges.MaxLatency - .01, AvatarChanges.MaxLatency + .11));
        }
        // A rename made without Undo is reported only as hierarchyChanged: the signature sees it, and the avatar's snapshot
        // (which follows reported changes only) gives the new path once the watchers hear.
        [Test] public void AChangeOnlyTheSignatureSeesRefreshesTheSnapshot()
        {
            var avatar = Avatar("A");
            var hair = avatar.transform.Find("Hair");
            var snapshot = AvatarSnapshot.Of(avatar.gameObject);
            Assert.AreEqual("Hair", snapshot.PathOf(hair));
            var heard = new List<AvatarFacets>();
            watches.Add(AvatarChanges.Watch(avatar, AvatarFacets.Hierarchy, heard.Add));
            hair.name = "Long hair";
            AvatarChanges.HierarchyChanged();
            AvatarChanges.Flush(now: true);
            CollectionAssert.AreEqual(new[] { AvatarFacets.Hierarchy }, heard);
            Assert.AreEqual("Long hair", AvatarSnapshot.Of(avatar.gameObject).PathOf(hair));
        }

        // Review of MAC-2: a component replaced without Undo at a constant object count while no section watched the avatar
        // (its Inspector closed) is seen once one watches again: what the sections read is what a fresh snapshot reads.
        [Test] public void AChangeWhileNothingWatchedIsSeenOnReopening()
        {
            var avatar = Avatar("A");
            var root = avatar.gameObject;
            var watch = AvatarChanges.Watch(avatar, AvatarFacets.Hierarchy, _ => { });
            CollectionAssert.AreEquivalent(AvatarSnapshot.Take(root).All<Renderer>(), AvatarMaterials.Renderers(root, fromSnapshot: true));
            watch.Dispose();
            var hair = avatar.transform.Find("Hair").gameObject;
            Object.DestroyImmediate(hair.GetComponent<MeshRenderer>());
            hair.AddComponent<SkinnedMeshRenderer>();
            watches.Add(AvatarChanges.Watch(avatar, AvatarFacets.Hierarchy, _ => { }));
            var read = AvatarMaterials.Renderers(root, fromSnapshot: true);
            CollectionAssert.AreEquivalent(AvatarSnapshot.Take(root).All<Renderer>(), read);
            Assert.That(read, Has.Some.InstanceOf<SkinnedMeshRenderer>());
        }

        // Changes that raise no event at all are caught by the periodic signature check.
        [Test] public void AChangeNothingReportsIsCaughtByReconciling()
        {
            var avatar = Avatar("A");
            var heard = new List<AvatarFacets>();
            watches.Add(AvatarChanges.Watch(avatar, AvatarFacets.Hierarchy, heard.Add));
            avatar.transform.Find("Hair").GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard"));
            AvatarChanges.Reconciled();
            AvatarChanges.Flush(now: true);
            CollectionAssert.AreEqual(new[] { AvatarFacets.Hierarchy }, heard);
        }

        // A hierarchy change somewhere does not move the time an avatar already waiting is told at.
        [Test] public void HierarchyChangesElsewhereDoNotHoldAnAvatarBack()
        {
            double time = 100;
            AvatarChanges.Clock = () => time;
            var avatar = Avatar("A");
            var heard = new List<double>();
            watches.Add(AvatarChanges.Watch(avatar, AvatarFacets.Hierarchy, _ => heard.Add(time)));
            AvatarChanges.Touched(avatar.transform.Find("Hair").gameObject.GetInstanceID(), AvatarFacets.Hierarchy);
            for (int step = 1; step <= 10 && heard.Count == 0; step++)
            {
                time = 100 + step * .1;
                AvatarChanges.HierarchyChanged();
                AvatarChanges.Flush(now: false);
            }
            Assert.AreEqual(1, heard.Count);
            Assert.That(heard[0], Is.InRange(100.3, 100.41));
        }

        // Through the editor itself: an edit with Undo reaches the change bus and the sections of its avatar only.
        [UnityTest] public IEnumerator AnEditWithUndoReachesItsAvatarThroughTheBus()
        {
            MyAvatar a = Avatar("A"), b = Avatar("B");
            var heard = new List<(string, AvatarFacets)>();
            watches.Add(AvatarChanges.Watch(a, AvatarFacets.Components | AvatarFacets.Renderers, f => heard.Add(("A", f))));
            watches.Add(AvatarChanges.Watch(b, AvatarFacets.Components | AvatarFacets.Renderers, f => heard.Add(("B", f))));
            var renderer = b.transform.Find("Hair").GetComponent<MeshRenderer>();
            Undo.RecordObject(renderer, "test");
            renderer.enabled = false;
            Undo.FlushUndoRecordObjects();
            double until = EditorApplication.timeSinceStartup + 5;
            while (heard.Count == 0 && EditorApplication.timeSinceStartup < until) yield return null;
            CollectionAssert.AreEqual(new[] { ("B", AvatarFacets.Renderers) }, heard);
        }
    }
}
