using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
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
        private sealed class Provider : ICustomBaseProvider, ICustomBaseFits
        {
            public Transform Avatar;
            public CustomBaseInfo Info;
            public readonly List<SkinnedMeshRenderer> Forgotten = new List<SkinnedMeshRenderer>();
            public CustomBaseInfo Describe(Transform avatarRoot) => avatarRoot == Avatar ? Info : null;
            public void Forget(Transform avatarRoot, SkinnedMeshRenderer renderer) { if (avatarRoot == Avatar) Forgotten.Add(renderer); }
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

        // Restore in My Avatar takes the refit back for good: the custom base's tool (MCB) must not put its saved fit back.
        [Test] public void RestoreMakesTheCustomBaseForgetItsSavedFit()
        {
            var (avatar, attachment) = Setup();
            provider = new Provider { Avatar = avatar.transform };
            CustomBases.Register(provider);
            var renderer = attachment.GetComponentInChildren<SkinnedMeshRenderer>();
            var record = renderer.gameObject.AddComponent<OrbitersRefit>();
            record.mesh = renderer.sharedMesh;
            AccessoryFit.Restore(attachment);
            Assert.That(provider.Forgotten, Is.EqualTo(new[] { renderer }));
            Assert.That(renderer.GetComponent<OrbitersRefit>(), Is.Null);
        }

        // Notes answer the custom base they were asked for: another version, the original base or a refit gone outdates them.
        [Test] public void NotesForAnotherCustomBaseOrAGoneRefitAreStale()
        {
            var (_, attachment) = Setup();
            var question = new MyAvatar.AccessoryNote { accessory = attachment, fit = AccessoryFit.Ask, fitKey = "mcb:1:1.0" };
            Assert.That(AccessoryFit.Stale(question, "mcb:1:1.0"), Is.False);
            Assert.That(AccessoryFit.Stale(question, "mcb:1:2.0"), Is.True, "A version switched since.");
            Assert.That(AccessoryFit.Stale(question, null), Is.True, "Reset to the original base.");
            var done = new MyAvatar.AccessoryNote { accessory = attachment, fit = AccessoryFit.Done, fitKey = "mcb:1:1.0" };
            Assert.That(AccessoryFit.Stale(done, "mcb:1:1.0"), Is.True, "The refit it reports was restored elsewhere (MCB's panel).");
            var record = attachment.GetComponentInChildren<SkinnedMeshRenderer>().gameObject.AddComponent<OrbitersRefit>();
            record.mesh = record.GetComponent<SkinnedMeshRenderer>().sharedMesh;
            Assert.That(AccessoryFit.Stale(done, "mcb:1:1.0"), Is.False);
            Assert.That(AccessoryFit.Stale(new MyAvatar.AccessoryNote { accessory = attachment, text = "Prepared 2 materials." }, "mcb:1:2.0"), Is.False,
                "Other notes are not about the custom base.");
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
            KnownBase(avatar);
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

        // An accessory not worn on the body (a floating bot, a held prop: its creator's gallery setup or the user said so) is
        // never asked about its fit, and a question asked before goes, in My Avatar and in Mixer's Hierarchy (no ReFit there).
        [UnityTest] public IEnumerator AnAccessoryNotWornOnTheBodyIsNeverAskedAboutItsFit()
        {
            var (avatar, attachment) = Setup();
            KnownBase(avatar);
            attachment.neverRefit = true;
            var check = AccessoryFit.CheckAsync(avatar, attachment, CancellationToken.None);
            yield return Wait(check);
            Assert.That(check.Result, Is.Null, "Lying on the arm skin, it is still not asked about.");

            var question = new MyAvatar.AccessoryNote { accessory = attachment, fit = AccessoryFit.Ask, fitKey = "test:14:1.0" };
            avatar.accessoryNotes.Add(question);
            Assert.That(AccessoryFit.Stale(question, "test:14:1.0"), Is.True, "A question asked before goes.");
            Assert.That(Orbiters.Toolkit.Editor.AvatarObjectTools.RefitQuestion(attachment.gameObject), Is.Null, "No ReFit in Mixer's Hierarchy.");
            attachment.neverRefit = false;
            Assert.That(AccessoryFit.Stale(question, "test:14:1.0"), Is.False);
            Assert.That(Orbiters.Toolkit.Editor.AvatarObjectTools.RefitQuestion(attachment.gameObject), Is.Not.Null, "Worn on the body, its question shows.");
        }

        private void KnownBase(MyAvatar avatar)
        {
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
        }

        // Clothing made for the original base whose hips stand 2 cm above the original's: within the armature fit's
        // thresholds, so the fit leaves it, and lining up still puts its hips on the original's (0.9.10 left them off).
        [Test] public void LiningUpPutsHipsALittleOffOnTheOriginals()
        {
            var root = Own(new GameObject("Humanoid avatar"));
            SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<MyAvatar>();
            Humanoid(root.transform);
            // The original base: the same skeleton, its body skinned to it.
            var original = Own(new GameObject("Original base"));
            SceneManager.MoveGameObjectToScene(original, scene);
            var bones = Humanoid(original.transform);
            Skinned("Body", original.transform, bones.Values.ToArray());
            // A jacket rigged on the original's hips, spine and chest, 2 cm high.
            var jacket = new GameObject("Jacket");
            jacket.transform.SetParent(root.transform, false);
            var jacketHips = new GameObject("Hips").transform;
            jacketHips.SetParent(jacket.transform, false);
            jacketHips.localPosition = new Vector3(0, 1.02f, 0);
            var spine = new GameObject("Spine").transform;
            spine.SetParent(jacketHips, false);
            spine.localPosition = new Vector3(0, .1f, 0);
            var chest = new GameObject("Chest").transform;
            chest.SetParent(spine, false);
            chest.localPosition = new Vector3(0, .15f, 0);
            Skinned("Jacket mesh", jacket.transform, new[] { jacketHips, spine, chest });
            var attachment = jacket.AddComponent<OrbitersAttachment>();
            var measure = Orbiters.Toolkit.Editor.VRChat.Attachments.AttachmentFit.Compare(jacket, original.transform);
            Assert.That(measure, Is.Not.Null, "Precondition: the jacket's bones are the original's.");
            Assert.That(measure.NeedsFit, Is.False, "Precondition: 2 cm is within the armature fit's thresholds.");
            AccessoryFit.LineUp(attachment, original.transform, bones["Hips"]);
            Assert.That(Vector3.Distance(jacketHips.position, bones["Hips"].position), Is.LessThan(.001f));
            Undo.ClearUndo(jacket.transform);
        }

        // A skinned mesh of three vertices on these bones.
        private void Skinned(string name, Transform parent, Transform[] bones)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mesh = Own(new Mesh
            {
                name = name, vertices = new[] { Vector3.up, Vector3.up + Vector3.right * .1f, Vector3.up + Vector3.forward * .1f }, triangles = new[] { 0, 1, 2 },
                boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 3).ToArray(),
                bindposes = bones.Select(b => b.worldToLocalMatrix * go.transform.localToWorldMatrix).ToArray(),
            });
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            renderer.bones = bones;
            renderer.rootBone = bones[0];
        }

        // Cancelling a line-up puts the accessory back as it was before it (its bones and what the fit recorded).
        [Test] public void CancellingALineUpPutsTheAccessoryBack()
        {
            var (_, attachment) = Setup();
            var bone = attachment.transform.GetChild(0);
            var position = attachment.transform.position;
            var rotation = bone.localRotation;
            var before = new AccessoryFit.Snapshot(attachment);
            attachment.transform.position += Vector3.up * .08f;
            bone.localRotation = Quaternion.Euler(0, 30, 0);
            attachment.fitted.Add(new OrbitersAttachment.FittedPose { transform = attachment.transform });
            before.Restore();
            Assert.That(Vector3.Distance(attachment.transform.position, position), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(bone.localRotation, rotation), Is.LessThan(.01f));
            Assert.That(attachment.fitted, Is.Empty);
            Undo.ClearUndo(attachment);
            Undo.ClearUndo(attachment.transform);
            Undo.ClearUndo(bone);
        }

        // A humanoid skeleton (hips 1 m up) and its Animator.
        private Dictionary<string, Transform> Humanoid(Transform root)
        {
            var human = new Dictionary<string, Transform>();
            void Add(string name, string parent, Vector3 position)
            {
                var bone = new GameObject(name).transform;
                bone.SetParent(parent == null ? root : human[parent], false);
                bone.localPosition = position;
                human[name] = bone;
            }
            Add("Hips", null, new Vector3(0, 1, 0)); Add("Spine", "Hips", new Vector3(0, .1f, 0)); Add("Chest", "Spine", new Vector3(0, .15f, 0));
            Add("Neck", "Chest", new Vector3(0, .24f, 0)); Add("Head", "Neck", new Vector3(0, .08f, 0));
            foreach (var side in new[] { "L", "R" })
            {
                float x = side == "L" ? -1 : 1;
                Add("UpperArm" + side, "Chest", new Vector3(.15f * x, .15f, 0)); Add("LowerArm" + side, "UpperArm" + side, new Vector3(.25f * x, 0, 0));
                Add("Hand" + side, "LowerArm" + side, new Vector3(.25f * x, 0, 0));
                Add("UpperLeg" + side, "Hips", new Vector3(.1f * x, -.05f, 0)); Add("LowerLeg" + side, "UpperLeg" + side, new Vector3(0, -.45f, 0));
                Add("Foot" + side, "LowerLeg" + side, new Vector3(0, -.45f, 0));
            }
            var map = new Dictionary<string, string>
            {
                ["Hips"] = "Hips", ["Spine"] = "Spine", ["Chest"] = "Chest", ["Neck"] = "Neck", ["Head"] = "Head",
                ["LeftUpperArm"] = "UpperArmL", ["LeftLowerArm"] = "LowerArmL", ["LeftHand"] = "HandL",
                ["RightUpperArm"] = "UpperArmR", ["RightLowerArm"] = "LowerArmR", ["RightHand"] = "HandR",
                ["LeftUpperLeg"] = "UpperLegL", ["LeftLowerLeg"] = "LowerLegL", ["LeftFoot"] = "FootL",
                ["RightUpperLeg"] = "UpperLegR", ["RightLowerLeg"] = "LowerLegR", ["RightFoot"] = "FootR",
            };
            var description = new HumanDescription
            {
                human = map.Select(pair => new HumanBone { humanName = pair.Key, boneName = pair.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
                skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
                upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f, armStretch = .05f, legStretch = .05f,
            };
            var avatar = Own(AvatarBuilder.BuildHumanAvatar(root.gameObject, description));
            root.gameObject.AddComponent<Animator>().avatar = avatar;
            Assert.That(root.GetComponent<Animator>().isHuman, Is.True);
            return human;
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
