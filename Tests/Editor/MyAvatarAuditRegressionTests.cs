using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Photoshoot;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Orbiters.MyAvatar.Editor.Tests
{
    public sealed class MyAvatarAuditRegressionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private Scene scene;
        private GameObject owner;
        private MyAvatar avatar;
        private MyAvatarEditor editor;
        private string folder, thumbnails;
        private Material material;
        private Texture2D texture;

        [SetUp] public void SetUp()
        {
            scene = EditorSceneManager.NewPreviewScene();
            owner = new GameObject("MyAvatarRegression-" + Guid.NewGuid().ToString("N"));
            SceneManager.MoveGameObjectToScene(owner, scene);
            avatar = owner.AddComponent<MyAvatar>();
            editor = (MyAvatarEditor)UnityEditor.Editor.CreateEditor(avatar);
            folder = "Assets/Orbiters/MyAvatar/Regression-" + Guid.NewGuid().ToString("N");
            thumbnails = AvatarThumbnail.Folder(avatar);
            material = new Material(Shader.Find("Standard")) { name = "Body" };
            owner.AddComponent<MeshRenderer>().sharedMaterial = material;
            texture = new Texture2D(2, 2);
        }

        [TearDown] public void TearDown()
        {
            if (editor) UnityEngine.Object.DestroyImmediate(editor);
            Undo.ClearUndo(avatar); Undo.ClearUndo(owner.GetComponent<MeshRenderer>());
            if (owner) UnityEngine.Object.DestroyImmediate(owner);
            if (material) UnityEngine.Object.DestroyImmediate(material);
            if (texture && !EditorUtility.IsPersistent(texture)) UnityEngine.Object.DestroyImmediate(texture);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            if (AssetDatabase.IsValidFolder(thumbnails)) AssetDatabase.DeleteAsset(thumbnails);
            EditorSceneManager.ClosePreviewScene(scene);
        }

        private object Call(string name, params object[] args) => typeof(MyAvatarEditor).GetMethod(name, Private).Invoke(editor, args);
        private object Field(string name) => typeof(MyAvatarEditor).GetField(name, Private).GetValue(editor);
        private static IEnumerator Wait(Task task)
        {
            double deadline = EditorApplication.timeSinceStartup + 10;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Task did not finish.");
            if (task.IsFaulted) throw task.Exception.GetBaseException();
        }

        [Test]
        public void ThumbnailUndoAndRedoRestoreDifferentPixelFiles()
        {
            using var state = new PhotoshootState();
            var section = new ThumbnailSection(avatar, state);
            var assign = typeof(ThumbnailSection).GetMethod("Assign", Private);
            Texture2D Image(Color color)
            {
                var image = new Texture2D(2, 2); image.SetPixels(Enumerable.Repeat(color, 4).ToArray()); image.Apply(); return image;
            }
            Undo.IncrementCurrentGroup(); assign.Invoke(section, new object[] { Image(Color.red) }); Undo.FlushUndoRecordObjects();
            var first = avatar.thumbnail; string firstPath = AvatarThumbnail.FullPath(first); byte[] firstPixels = File.ReadAllBytes(firstPath);
            Undo.IncrementCurrentGroup(); assign.Invoke(section, new object[] { Image(Color.blue) }); Undo.FlushUndoRecordObjects();
            var second = avatar.thumbnail; byte[] secondPixels = File.ReadAllBytes(AvatarThumbnail.FullPath(second));
            Assert.That(second, Is.Not.SameAs(first)); Assert.That(secondPixels, Is.Not.EqualTo(firstPixels));
            Undo.PerformUndo(); Assert.That(avatar.thumbnail, Is.EqualTo(first));
            Assert.That(File.ReadAllBytes(AvatarThumbnail.FullPath(avatar.thumbnail)), Is.EqualTo(firstPixels));
            Undo.PerformRedo(); Assert.That(avatar.thumbnail, Is.EqualTo(second));
            Assert.That(File.ReadAllBytes(AvatarThumbnail.FullPath(avatar.thumbnail)), Is.EqualTo(secondPixels));
        }

        [Test]
        public void SameFilenameFromDifferentFoldersRemembersIndependentChoices()
        {
            var hair = new Material(material) { name = "Hair" };
            try
            {
                var bodyEntry = new TextureEntry { fileName = "Albedo.png", sourceKey = TextureMemory.SourceKey(Path.Combine(Path.GetTempPath(), "Body/Albedo.png")),
                    material = material, property = "_MainTex", applied = true, reason = TextureMatching.ChosenReason };
                var hairEntry = new TextureEntry { fileName = "Albedo.png", sourceKey = TextureMemory.SourceKey(Path.Combine(Path.GetTempPath(), "Hair/Albedo.png")),
                    material = hair, property = "_MainTex", applied = true, reason = TextureMatching.ChosenReason };
                var choices = new Dictionary<string, List<TextureMemory.Slot>>();
                Assert.That(TextureMemory.RecordChoices(choices, new[] { bodyEntry, hairEntry }), Is.True);
                Assert.That(choices.Count, Is.EqualTo(2));
                var slots = new[] { material, hair }.Select(m => new TextureSlot { material = m, materialName = m.name,
                    shader = TextureMatching.ShaderName(m), property = "_MainTex" }).ToList();
                Assert.That(TextureMemory.Find(choices, bodyEntry, slots).Single().material, Is.SameAs(material));
                Assert.That(TextureMemory.Find(choices, hairEntry, slots).Single().material, Is.SameAs(hair));
                bodyEntry.material = hair;
                TextureMemory.RecordChoices(choices, new[] { bodyEntry });
                Assert.That(TextureMemory.Find(choices, hairEntry, slots).Single().material, Is.SameAs(hair));
            }
            finally { UnityEngine.Object.DestroyImmediate(hair); }
        }

        [UnityTest] public IEnumerator AiOffRejectsLateResponse() { yield return LateResponse("off"); }
        [UnityTest] public IEnumerator AiOffThenOnStillRejectsOldResponse() { yield return LateResponse("off-on"); }
        [UnityTest] public IEnumerator ClosingInspectorRejectsLateResponse() { yield return LateResponse("close"); }
        [UnityTest] public IEnumerator CurrentAiResponseStillApplies() { yield return LateResponse("current"); }
        // A texture set by hand in the material's own Inspector while AI was answering stays.
        [UnityTest] public IEnumerator MaterialEditedMeanwhileRejectsLateResponse() { yield return LateResponse("edited"); }

        private IEnumerator LateResponse(string action)
        {
            if (action == "current")
            {
                TextureImport.EnsureFolder(folder);
                AssetDatabase.CreateAsset(texture, folder + "/fixture.asset");
            }
            Call("ApplyAiEnabled", true);
            var entry = new TextureEntry { texture = texture, fileName = "texture.png", sourceKey = "test:source", role = "color" };
            var entries = avatar.textures = new List<TextureEntry> { entry }; avatar.batchFolder = folder;
            var slot = new TextureSlot { material = material, materialName = "Body", property = "_MainTex", role = "color" };
            var response = new TaskCompletionSource<TextureAi.Result>(); CancellationToken requested = default;
            editor.RequestAi = (token, payload, sent, slots, cancellation) => { requested = cancellation; return response.Task; };
            var task = (Task)Call("ResolveAsync", "fixture", new object(), entries, entries, new List<TextureSlot> { slot }, folder);
            if (action.StartsWith("off")) Call("ApplyAiEnabled", false);
            if (action == "off-on") Call("ApplyAiEnabled", true);
            if (action == "close") UnityEngine.Object.DestroyImmediate(editor);
            var edited = action == "edited" ? new Texture2D(2, 2) { name = "edited" } : null;
            if (edited) material.SetTexture("_MainTex", edited);
            response.SetResult(new TextureAi.Result { changes = new List<TextureChanges.Change> {
                new TextureChanges.Change { entry = entry, slot = slot, confidence = 1, reason = "fixture" } } });
            yield return Wait(task);
            if (action == "current")
            {
                Assert.That(entry.applied, Is.True);
                Assert.That(AssetDatabase.GetAssetPath(owner.GetComponent<MeshRenderer>().sharedMaterial.GetTexture("_MainTex")), Is.EqualTo(folder + "/fixture.asset"));
            }
            else if (action == "edited")
            {
                Assert.That(entry.applied, Is.False); Assert.That(entry.material, Is.Null);
                StringAssert.Contains("changed meanwhile", entry.reason);
                Assert.That(owner.GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(material));
                Assert.That(material.GetTexture("_MainTex"), Is.SameAs(edited));
                UnityEngine.Object.DestroyImmediate(edited);
            }
            else
            {
                Assert.That(requested.IsCancellationRequested, Is.True);
                Assert.That(entry.material, Is.Null); Assert.That(entry.applied, Is.False);
                Assert.That(owner.GetComponent<MeshRenderer>().sharedMaterial, Is.SameAs(material));
                Assert.That(material.GetTexture("_MainTex"), Is.Null);
            }
        }

        [UnityTest]
        public IEnumerator ClosingDuringFinalDelayCannotQueueAi()
        {
            Call("ApplyAiEnabled", true);
            using var source = new CancellationTokenSource();
            int requests = 0;
            var finish = (Task)Call("FinishImportAsync", (Func<Task>)(() => { requests++; return Task.CompletedTask; }), source.Token, (int)Field("revision"));
            UnityEngine.Object.DestroyImmediate(editor);
            yield return Wait(finish);
            Assert.That(Field("pendingAi"), Is.Null); Assert.That(requests, Is.Zero);
        }

        [UnityTest]
        public IEnumerator FinalDelayHonorsCancellation()
        {
            Call("ApplyAiEnabled", true);
            using var source = new CancellationTokenSource();
            var finish = (Task)Call("FinishImportAsync", (Func<Task>)(() => Task.CompletedTask), source.Token, (int)Field("revision"));
            source.Cancel();
            yield return Wait(finish);
            Assert.That(finish.IsCanceled, Is.True); Assert.That(Field("pendingAi"), Is.Null);
        }

        [UnityTest]
        public IEnumerator RapidPreferenceWritesStayOrderedAndOnlyLatestResponsePublishes()
        {
            var sent = new List<bool>(); var shown = new List<bool>();
            var replies = new[] { new TaskCompletionSource<bool>(), new TaskCompletionSource<bool>(), new TaskCompletionSource<bool>() };
            var preferences = new TextureAiPreferences((token, enabled) => cancellation => { sent.Add(enabled); return replies[sent.Count - 1].Task; });
            preferences.Changed += (token, enabled) => shown.Add(enabled);
            var first = preferences.SetAsync("fixture", true);
            var second = preferences.SetAsync("fixture", false);
            var third = preferences.SetAsync("fixture", true);
            Assert.That(preferences.TryGetPendingChoice("fixture", out bool pending), Is.True); Assert.That(pending, Is.True);
            Assert.That(sent, Is.EqualTo(new[] { true })); Assert.That(shown, Is.EqualTo(new[] { true, false, true }));
            replies[0].SetException(new IOException("older request failed"));
            while (!first.IsCompleted || sent.Count < 2) yield return null;
            Assert.That(first.IsFaulted, Is.True); _ = first.Exception;
            Assert.That(shown.Last(), Is.True);
            replies[1].SetResult(false); yield return Wait(second);
            while (sent.Count < 3) yield return null;
            Assert.That(shown, Is.EqualTo(new[] { true, false, true }));
            replies[2].SetResult(true); yield return Wait(third);
            Assert.That(sent, Is.EqualTo(new[] { true, false, true }));
            Assert.That(shown, Is.EqualTo(new[] { true, false, true, true }));
        }

        [UnityTest]
        public IEnumerator FailedLatestPreferenceKeepsLocalAiOff()
        {
            var reply = new TaskCompletionSource<bool>(); bool shown = false;
            var preferences = new TextureAiPreferences((token, enabled) => cancellation => reply.Task);
            preferences.Changed += (token, enabled) => shown = enabled;
            var request = preferences.SetAsync("fixture", true); Assert.That(shown, Is.True);
            reply.SetException(new IOException("offline"));
            while (!request.IsCompleted) yield return null;
            Assert.That(request.IsFaulted, Is.True); _ = request.Exception; Assert.That(shown, Is.False);
        }

        [UnityTest]
        public IEnumerator AccountChangeSkipsQueuedOldAccountWrite()
        {
            int sends = 0; var firstReply = new TaskCompletionSource<bool>();
            var preferences = new TextureAiPreferences((token, enabled) => cancellation => { sends++; return firstReply.Task; });
            var first = preferences.SetAsync("old-account", true);
            var second = preferences.SetAsync("old-account", false);
            preferences.InvalidateContext();
            Assert.That(preferences.TryGetPendingChoice("old-account", out _), Is.False);
            firstReply.SetResult(true); yield return Wait(first);
            while (!second.IsCompleted) yield return null;
            Assert.That(second.IsFaulted, Is.True); _ = second.Exception;
            Assert.That(sends, Is.EqualTo(1));
        }
    }
}
