using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // Accessories on a custom base: the question each one gets, and none without a known custom base.
    public sealed class AccessoryFitTests
    {
        private sealed class Provider : ICustomBaseProvider
        {
            public Transform Avatar;
            public CustomBaseInfo Info;
            public CustomBaseInfo Describe(Transform avatarRoot) => avatarRoot == Avatar ? Info : null;
        }

        private Scene scene;
        private readonly List<Object> owned = new List<Object>();
        private Provider provider;

        [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();

        [TearDown] public void TearDown()
        {
            if (provider != null) CustomBases.Unregister(provider);
            provider = null;
            foreach (var value in owned) if (value) Object.DestroyImmediate(value);
            owned.Clear();
            EditorSceneManager.ClosePreviewScene(scene);
        }

        [Test] public void NotesReadAsTheirQuestion()
        {
            var note = new MyAvatar.AccessoryNote { fit = AccessoryFit.Ask, fitBase = "Muscle Orbit 0.5.0", fitShapes = 1 };
            Assert.That(AccessoryFit.Text(note, "Hoodie"), Is.EqualTo("Your avatar uses Muscle Orbit 0.5.0. Does Hoodie fit your body?"));
            Assert.That(AccessoryFit.IsQuestion(note), Is.True);
            note.fit = AccessoryFit.AddShapes;
            Assert.That(AccessoryFit.Text(note, "Hoodie"), Does.StartWith("Hoodie lacks 1 blendshape of Muscle Orbit 0.5.0"));
            note.fit = AccessoryFit.Done; note.fitShapes = 3; note.fitRough = true;
            Assert.That(AccessoryFit.Text(note, "Hoodie"), Does.Contain("3 blendshapes move with your body").And.Contain("a creator can refit it"));
            Assert.That(AccessoryFit.IsQuestion(note), Is.False);
            note.fit = AccessoryFit.Failed; note.text = "No body.";
            Assert.That(AccessoryFit.Text(note, "Hoodie"), Is.EqualTo("No body."));
        }

        [UnityTest] public IEnumerator AnAvatarWithoutAKnownCustomBaseAsksNothing()
        {
            var (avatar, attachment) = Setup();
            var check = AccessoryFit.CheckAsync(avatar, attachment, CancellationToken.None);
            yield return Wait(check);
            Assert.That(check.Result, Is.Null);
        }

        [UnityTest] public IEnumerator ClothingOnAKnownCustomBaseIsAskedWhetherItFits()
        {
            var (avatar, attachment) = Setup();
            provider = new Provider
            {
                Avatar = avatar.transform,
                Info = new CustomBaseInfo
                {
                    Key = "test:14:1.0", Name = "Muscle Orbit 1.0", AssetId = 14, Source = "Test",
                    Body = avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>(),
                    Shapes = new List<string> { "Flex arms" }, ResolveOriginal = () => null,
                },
            };
            CustomBases.Register(provider);
            CustomBaseDetection.Invalidate(avatar.transform);
            var check = AccessoryFit.CheckAsync(avatar, attachment, CancellationToken.None);
            yield return Wait(check);
            var note = check.Result;
            Assert.That(note, Is.Not.Null);
            Assert.That(note.fit, Is.EqualTo(AccessoryFit.Ask));
            Assert.That(note.accessory, Is.SameAs(attachment));
            Assert.That(note.fitBase, Is.EqualTo("Muscle Orbit 1.0"));
            Assert.That(note.fitKey, Is.EqualTo("test:14:1.0"));
            Assert.That(note.fitShapes, Is.EqualTo(1));
        }

        // A body whose arm skin "Flex arms" moves, and a sleeve 1 cm from it.
        private (MyAvatar avatar, OrbitersAttachment attachment) Setup()
        {
            var root = Own(new GameObject("Fit avatar"));
            SceneManager.MoveGameObjectToScene(root, scene);
            var avatar = root.AddComponent<MyAvatar>();
            var arm = Points(new Vector3(1, 1, 0));
            var bodyMesh = Own(new Mesh { name = "Body", vertices = arm, triangles = Enumerable.Range(0, arm.Length).ToArray() });
            bodyMesh.AddBlendShapeFrame("Flex arms", 100, arm.Select(_ => new Vector3(0.01f, 0, 0)).ToArray(), null, null);
            Skin("Body", root.transform, bodyMesh);
            var item = new GameObject("Jacket");
            item.transform.SetParent(root.transform, false);
            var sleeve = Points(new Vector3(1.01f, 1, 0));
            Skin("Jacket mesh", item.transform, Own(new Mesh { name = "Jacket", vertices = sleeve, triangles = Enumerable.Range(0, sleeve.Length).ToArray() }));
            return (avatar, item.AddComponent<OrbitersAttachment>());
        }

        private static Vector3[] Points(Vector3 center) =>
            Enumerable.Range(0, 12).Select(i => center + new Vector3((i % 3) * 0.004f, (i / 3 % 2) * 0.004f, (i / 6) * 0.004f)).ToArray();

        private static void Skin(string name, Transform parent, Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        }

        private T Own<T>(T value) where T : Object
        {
            owned.Add(value);
            return value;
        }

        private static IEnumerator Wait(Task task)
        {
            for (int frames = 0; !task.IsCompleted && frames < 600; frames++) yield return null;
            Assert.That(task.IsCompleted, Is.True, "The check did not finish.");
            if (task.IsFaulted) throw task.Exception.InnerException;
        }
    }
}
