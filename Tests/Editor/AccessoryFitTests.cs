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
using UnityEngine.UIElements;

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

        [Test] public void CompletedFitAlwaysOffersCommissionAndCancelWithoutAWarning()
        {
            var card = new FitCard(new FitCard.Model { Fit = AccessoryFit.Done, Item = "Jacket", CanCommission = true }, new FitCard.Actions());
            var buttons = card.Query<Button>().ToList().Select(b => b.text).ToList();
            Assert.That(buttons, Does.Contain("Ask a creator").And.Contain("Cancel refit"));
            Assert.That(card.Query(className: "fit-card__close").ToList(), Is.Empty, "Fit actions must stay reachable.");
        }

        [Test] public void RunningFitShowsElapsedTimeAndCancelControl()
        {
            var card = new FitCard(new FitCard.Model { Running = true, Item = "Jacket", ProgressText = "Transferring 2/8",
                StartedAt = UnityEditor.EditorApplication.timeSinceStartup - 12 },
                new FitCard.Actions { Stop = () => { } });
            Assert.That(card.Query<Label>().ToList().Any(l => l.text.Contains("Transferring 2/8") && l.text.Contains("12s")), Is.True);
            var button = card.Query<Button>().ToList().Single(b => b.text == "Cancel");
            Assert.That(button.enabledInHierarchy, Is.True);
        }

        [Test] public void OriginalBodyPlacementDoesNotWaitForTheBlendshapeMap()
        {
            var (avatar, attachment) = Setup();
            var body = avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            provider = new Provider { Avatar = avatar.transform, Info = new CustomBaseInfo
            {
                Key = "test:placement", Body = body, Shapes = new List<string> { "Flex arms" },
                ResolveOriginal = () => new CustomBaseOriginal { Avatar = avatar.gameObject, Body = body }
            } };
            CustomBases.Register(provider);
            var stages = new List<string>();
            var task = AccessoryFit.PlaceAsync(avatar, attachment, (p, label) => stages.Add(label));
            try
            {
                Assert.That(task.IsCompleted && !task.IsFaulted, Is.True, "Provider placement must not await the shape map.");
                Assert.That(CustomBaseDetection.Current(avatar.transform), Is.Null);
                Assert.That(stages.Count, Is.EqualTo(3));
            }
            finally { if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); }
        }

        [Test] public void SavedRefitRestoresItsCardAndCommissionWithoutSessionResults()
        {
            var (avatar, attachment) = Setup();
            var renderer = attachment.GetComponentInChildren<SkinnedMeshRenderer>();
            var record = renderer.gameObject.AddComponent<OrbitersRefit>();
            record.mesh = renderer.sharedMesh;
            record.body = avatar.transform.Find("Body").GetComponent<SkinnedMeshRenderer>();
            record.baseName = "UltiPaw";
            record.shapes.Add(new RefitShape("Flex arms", "Flex arms"));
            var note = AccessoryFit.CompletedNote(attachment);
            Assert.That(note.fit, Is.EqualTo(AccessoryFit.Done));
            Assert.That(note.fitShapes, Is.EqualTo(1));
            var commission = AccessoryFit.CommissionItem(attachment);
            Assert.That(commission.Job.Renderer, Is.SameAs(renderer));
            Assert.That(commission.Job.Body, Is.SameAs(record.body));
            Assert.That(commission.Outcome.Success, Is.True);
            record.mesh = null;
            Assert.That(AccessoryFit.CompletedNote(attachment), Is.Null);
            Assert.That(AccessoryFit.CommissionItem(attachment), Is.Null);
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
