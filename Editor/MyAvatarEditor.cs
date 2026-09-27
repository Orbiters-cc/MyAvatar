using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    [CustomEditor(typeof(MyAvatar))]
    public sealed class MyAvatarEditor : UnityEditor.Editor
    {
        private static readonly HashSet<int> Running = new HashSet<int>();
        private MyAvatar avatar;
        private VisualElement root, content, results;
        private TextureDropZone zone;
        private Button undo, save;
        private CancellationTokenSource operation, background;
        private Func<Task> pendingAi;
        private string note, backgroundStatus;
        private bool noteWarning;
        private int revision;
        private bool busy;
        private void OnEnable() { avatar = (MyAvatar)target; Undo.undoRedoPerformed += Reload; AssemblyReloadEvents.beforeAssemblyReload += Cancel; }
        private void OnDisable() { Undo.undoRedoPerformed -= Reload; AssemblyReloadEvents.beforeAssemblyReload -= Cancel; Cancel(); }
        private void Cancel() { operation?.Cancel(); background?.Cancel(); }
        // Any edit, Undo or Redo makes a pending AI answer stale: it must never overwrite a newer state.
        private void Edited() { revision++; background?.Cancel(); }
        private void Reload() { Edited(); if (avatar && root != null) RefreshResults(); }

        public override VisualElement CreateInspectorGUI()
        {
            var shell = new OrbitersInspectorShell(); root = shell; root.AddToClassList("myavatar");
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.myavatar/Editor/UI/myavatar.uss"));
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/orbiters.myavatar/Editor/UI/MyAvatarLogo.svg.txt");
            if (asset) { var logo = new OrbitersVectorLogo(asset.text, new Vector2(309,258)); logo.AddToClassList("avatar-logo"); shell.Banner.Add(logo); }
            shell.Account.Add(new OrbitersAccountElement("myavatar/connection", null));
            content = new VisualElement(); content.AddToClassList("content"); root.Add(content);
            var section = new Label("Textures"); section.AddToClassList("section-title"); content.Add(section);
            content.Add(new Label("Drop a texture set to match it to this avatar’s materials.") { name = "intro" });
            undo = Button("Undo", () => _ = Run(() => { note = null; TextureChanges.UndoLast(avatar); RefreshResults(); return Task.CompletedTask; }));
            undo.tooltip = "Restore the materials from before this texture set. Click again to redo.";
            save = Button("Save", () => _ = Run(async () => {
                SetNote("Saving…", false);
                note = await TextureChanges.SaveAsync(avatar); noteWarning = false;
            }, false));
            save.tooltip = TextureChanges.Commits ? "Save the scene and generated assets, and record a “texture change” checkpoint in Unit Git. Nothing is pushed."
                : "Save the scene and the generated textures and materials. Install Unit Git to also record a local checkpoint.";
            save.AddToClassList("mcb-button--primary");
            zone = new TextureDropZone(paths => _ = Run(() => Import(paths)), undo, save); content.Add(zone);
            results = new VisualElement(); results.AddToClassList("results"); content.Add(results);
            RefreshResults();
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar))
            { content.SetEnabled(false); root.Add(new OrbitersNoticeElement("Use My Avatar on a scene avatar outside Play Mode.", HelpBoxMessageType.Info)); }
            return root;
        }

        private async Task Import(string[] paths)
        {
            note = null; results.Clear();
            zone.ShowProgress(.04f, "Finding textures…");
            var files = await Task.Run(() => TextureImport.Expand(paths), operation.Token);
            var slots = TextureMatching.Slots(avatar);
            if (slots.Count == 0) throw new InvalidOperationException("No editable texture slots were found below this avatar.");
            if (slots.Count > 512) throw new InvalidOperationException("This avatar has more than 512 texture slots. Place My Avatar on a smaller avatar root.");
            string folder = "Assets/Orbiters/MyAvatar/" + Guid.NewGuid().ToString("N");
            var entries = await TextureImport.ImportAsync(files, folder, (value, text) => zone.ShowProgress(value, text), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!avatar) return;
            zone.ShowProgress(.78f, "Matching " + entries.Count + " textures…");
            await Task.Yield();
            var stats = TextureAnalysis.Measure(entries.Select(e => e.texture));
            TextureMatching.Match(entries, slots, stats, TextureMemory.Load(avatar));
            string token = AuthenticationService.GetAuth()?.token;
            // Only unresolved textures are sent: measured on DeepSeek, adding resolved ones never produced a
            // useful correction and made fast (non-reasoning) answers less accurate.
            // A texture whose set already has a home that lacks the slot needs a shader change, not a guess elsewhere.
            var unmatched = entries.Where(e => !e.material && !(e.reason ?? "").StartsWith(TextureMatching.MissingSlotReason, StringComparison.Ordinal)).ToList();
            // Slots filled by this drop's local matches are left out, so an answer cannot displace them. Effect renderers
            // (particles, trails) and secondary layers (detail, matcap, rim...) stay available in the manual slot menu only.
            var open = slots.Where(s => !s.secondary && s.rendererKind != "effect" && unmatched.Any(e => TextureMatching.Compatible(e.role, s.role)) &&
                !entries.Any(e => e.material == s.material && e.property == s.property)).ToList();
            bool ask = unmatched.Count > 0 && open.Count > 0 && !string.IsNullOrEmpty(token);
            object payload = ask ? TextureAi.Payload(unmatched, open, stats) : null;
            zone.ShowProgress(.9f, "Applying to " + entries.Where(e => e.material).Select(e => e.material).Distinct().Count() + " materials…");
            await Task.Yield();
            Undo.RecordObject(avatar, "My Avatar: texture set");
            int count = TextureChanges.Apply(avatar, entries, folder);
            if (count == 0) { avatar.undoMaterials.Clear(); avatar.canRedo = false; }
            avatar.textures = entries; avatar.batchFolder = folder; avatar.notice = null;
            TextureMemory.Record(avatar, entries);
            TextureChanges.Dirty(avatar);
            zone.ShowProgress(1f, "Done");
            await Task.Delay(260);
            if (ask) pendingAi = () => ResolveAsync(token, payload, entries, unmatched, open, folder);
        }

        // Runs after the local apply has released the UI; merges confident answers into the same batch.
        private async Task ResolveAsync(string token, object payload, List<TextureEntry> entries, List<TextureEntry> sent, List<TextureSlot> slots, string folder)
        {
            background?.Cancel();
            var source = background = new CancellationTokenSource();
            int started = revision;
            SetBackground($"Orbiters AI is placing {sent.Count} more texture{(sent.Count == 1 ? "" : "s")}…");
            try
            {
                var result = await TextureAi.RequestAsync(token, payload, sent, slots, source.Token);
                while (busy && !source.IsCancellationRequested) await Task.Delay(50);
                source.Token.ThrowIfCancellationRequested();
                if (!avatar || started != revision) return;
                TextureChanges.Revise(avatar, entries, folder, result.changes);
                TextureMemory.Record(avatar, entries);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (avatar && started == revision) { note = null; SetNote(StatusText() + " · Orbiters AI unavailable: " + ex.Message, true); } return; }
            finally
            {
                if (background == source) { background = null; SetBackground(null); }
                source.Dispose();
            }
            if (!avatar || started != revision) return;
            TextureChanges.Dirty(avatar); RefreshResults();
        }

        private void SetBackground(string status)
        {
            backgroundStatus = status;
            zone?.SetBackground(status);
        }

        private void SetNote(string text, bool warning)
        {
            note = text; noteWarning = warning;
            RefreshResults();
        }

        private async Task Run(Func<Task> work, bool edits = true)
        {
            if (!avatar || busy || !Running.Add(avatar.GetInstanceID())) return;
            if (edits) Edited();
            int id = avatar.GetInstanceID(); busy = true; operation = new CancellationTokenSource();
            content.Query<Button>().ForEach(b => b.SetEnabled(false));
            try { await work(); }
            catch (OperationCanceledException) { note = "Cancelled. Nothing was applied."; noteWarning = false; }
            catch (Exception ex) { note = ex.Message; noteWarning = true; }
            finally
            {
                Running.Remove(id); busy = false; operation?.Dispose(); operation = null;
                if (root != null) { content.Query<Button>().ForEach(b => b.SetEnabled(true)); RefreshResults(); }
                var next = pendingAi; pendingAi = null;
                if (next != null && avatar) _ = next();
            }
        }

        // One line that summarises the current set: counts per dropped file, not per material it was applied to.
        private string StatusText()
        {
            var files = avatar.textures.GroupBy(t => t.fileName).ToList();
            int applied = files.Count(g => g.Any(t => t.applied)), pending = files.Count - applied;
            if (avatar.canRedo) return "Undone · original materials restored";
            return pending == 0 ? $"{applied} texture{(applied == 1 ? "" : "s")} applied" : $"{applied} applied · {pending} need{(pending == 1 ? "s" : "")} a slot";
        }

        private void RefreshResults()
        {
            if (!avatar || results == null) return;
            results.Clear();
            undo.text = avatar.canRedo ? "Redo" : "Undo";
            undo.SetEnabled(avatar.undoMaterials.Count > 0 && !busy); save.SetEnabled(avatar.textures.Count > 0 && !busy);
            if (busy && note == null) return;
            if (avatar.textures.Count == 0) { zone.ShowIdle(note, noteWarning); return; }
            bool pending = !avatar.canRedo && avatar.textures.GroupBy(t => t.fileName).Any(g => !g.Any(t => t.applied));
            zone.ShowDone(note ?? StatusText(), note != null ? noteWarning : pending);
            zone.SetBackground(backgroundStatus);
            if (!avatar.canRedo) MyAvatarResults.Populate(results, avatar, Edited, () => _ = Run(() => {
                note = null;
                TextureChanges.Apply(avatar, avatar.textures, avatar.batchFolder);
                TextureMemory.Record(avatar, avatar.textures); TextureChanges.Dirty(avatar); return Task.CompletedTask;
            }));
        }

        internal static Button Button(string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("mcb-button");
            // Immediate feedback on press; the click itself still follows Unity's normal release behaviour.
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("pressed"));
            return button;
        }
        [MenuItem("GameObject/Orbiters/My Avatar", false, 20)]
        private static void AddComponent()
        {
            var selected = Selection.activeGameObject;
            if (!selected || EditorUtility.IsPersistent(selected)) return;
            if (!selected.GetComponent<MyAvatar>()) Undo.AddComponent<MyAvatar>(selected);
        }
        [MenuItem("GameObject/Orbiters/My Avatar", true)]
        private static bool CanAdd() => Selection.activeGameObject && !EditorUtility.IsPersistent(Selection.activeGameObject);
    }
}
