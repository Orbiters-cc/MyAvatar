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
        private OrbitersNoticeElement notice;
        private Label progress;
        private Button cancel, undo, save;
        private CancellationTokenSource operation, background;
        private Func<Task> pendingAi;
        private string backgroundStatus;
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
            var drop = new TextureDropElement(paths => _ = Run(() => Import(paths))); content.Add(drop);
            progress = new Label(); progress.AddToClassList("progress"); content.Add(progress);
            cancel = Button("Cancel", Cancel); cancel.style.display = DisplayStyle.None; content.Add(cancel);
            notice = new OrbitersNoticeElement("Original materials are preserved. Drop a texture set to begin.", HelpBoxMessageType.Info); notice.style.display = DisplayStyle.None; content.Add(notice);
            results = new VisualElement(); content.Add(results);
            var actions = new VisualElement(); actions.AddToClassList("actions");
            undo = Button("Undo last apply", () => _ = Run(() => { TextureChanges.UndoLast(avatar); RefreshResults(); return Task.CompletedTask; }));
            save = Button("Save · Unit Git", () => _ = Run(async () => { string saved = await TextureChanges.SaveAsync(avatar); Show(saved, HelpBoxMessageType.Info); }, false));
            save.AddToClassList("mcb-button--primary"); actions.Add(undo); actions.Add(save); content.Add(actions);
            var hint = new Label("Save records “texture change” with generated assets and this scene’s current changes. It does not push."); hint.AddToClassList("muted"); content.Add(hint);
            RefreshResults();
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar))
            { content.SetEnabled(false); root.Add(new OrbitersNoticeElement("Use My Avatar on a scene avatar outside Play Mode.", HelpBoxMessageType.Info)); }
            return root;
        }

        private async Task Import(string[] paths)
        {
            progress.text = "Finding textures…";
            var files = await Task.Run(() => TextureImport.Expand(paths), operation.Token);
            var slots = TextureMatching.Slots(avatar);
            if (slots.Count == 0) throw new InvalidOperationException("No editable texture slots were found below this avatar. Unlock baked shaders first if needed.");
            if (slots.Count > 512) throw new InvalidOperationException("This avatar has more than 512 texture slots. Place My Avatar on a smaller avatar root.");
            string folder = "Assets/Orbiters/MyAvatar/" + Guid.NewGuid().ToString("N");
            var entries = await TextureImport.ImportAsync(files, folder, message => progress.text = message, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!avatar) return;
            var stats = TextureAnalysis.Measure(entries.Select(e => e.texture));
            TextureMatching.Match(entries, slots, stats, TextureMemory.Load(avatar));
            string token = AuthenticationService.GetAuth()?.token;
            // Only unresolved textures are sent: measured on DeepSeek, adding resolved ones never produced a
            // useful correction and made fast (non-reasoning) answers less accurate.
            var unmatched = entries.Where(e => !e.material).ToList();
            int unresolved = unmatched.Count;
            // Slots filled by this drop's local matches are left out, so an answer cannot displace them.
            var open = slots.Where(s => !entries.Any(e => e.material == s.material && e.property == s.property)).ToList();
            bool ask = unresolved > 0 && open.Count > 0 && !string.IsNullOrEmpty(token);
            object payload = ask ? TextureAi.Payload(unmatched, open, stats) : null;
            Undo.RecordObject(avatar, "My Avatar: texture set");
            int count = TextureChanges.Apply(avatar, entries, folder);
            if (count == 0) { avatar.undoMaterials.Clear(); avatar.canRedo = false; }
            avatar.textures = entries; avatar.batchFolder = folder;
            TextureMemory.Record(avatar, entries);
            string status = ask ? "Orbiters AI is checking the remaining textures…" : unresolved == 0 ? "Local matching completed." :
                string.IsNullOrEmpty(token) ? "Offline matching · connect to Orbiters for AI assistance." : "Choose a slot for the remaining textures below.";
            avatar.notice = $"{count} of {entries.Count} textures applied. " + status;
            TextureChanges.Dirty(avatar); RefreshResults();
            if (ask) pendingAi = () => ResolveAsync(token, payload, entries, unmatched, open, folder);
        }

        // Runs after the local apply has released the UI; merges confident answers into the same batch.
        private async Task ResolveAsync(string token, object payload, List<TextureEntry> entries, List<TextureEntry> sent, List<TextureSlot> slots, string folder)
        {
            background?.Cancel();
            var source = background = new CancellationTokenSource();
            int started = revision;
            ShowBackground($"Orbiters AI is checking {sent.Count} unmatched texture{(sent.Count == 1 ? "" : "s")}…");
            string status;
            try
            {
                var result = await TextureAi.RequestAsync(token, payload, sent, slots, source.Token);
                while (busy && !source.IsCancellationRequested) await Task.Delay(50);
                source.Token.ThrowIfCancellationRequested();
                if (!avatar || started != revision) return;
                int accepted = TextureChanges.Revise(avatar, entries, folder, result.changes);
                TextureMemory.Record(avatar, entries);
                status = (accepted > 0 ? $"Orbiters AI matched {accepted} more texture{(accepted == 1 ? "" : "s")}." : "Orbiters AI found no further confident matches.") +
                    (result.warnings.Length > 0 ? " " + string.Join(" ", result.warnings) : "");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { status = "AI unavailable: " + ex.Message + " Offline matches are applied."; }
            finally
            {
                if (background == source) { background = null; ShowBackground(null); }
                source.Dispose();
            }
            if (!avatar || started != revision) return;
            avatar.notice = $"{avatar.textures.Count(t => t.applied)} of {avatar.textures.Count} textures applied. " + status;
            TextureChanges.Dirty(avatar); RefreshResults();
        }

        private void ShowBackground(string status)
        {
            backgroundStatus = status;
            if (root == null || busy) return;
            progress.text = status ?? "";
            cancel.style.display = status != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private async Task Run(Func<Task> work, bool edits = true)
        {
            if (!avatar || busy || !Running.Add(avatar.GetInstanceID())) return;
            if (edits) Edited();
            int id = avatar.GetInstanceID(); busy = true; operation = new CancellationTokenSource();
            content.Query<Button>().ForEach(b => b.SetEnabled(false));
            content.Query<TextureDropElement>().ForEach(d => d.SetEnabled(false));
            cancel.SetEnabled(true); cancel.style.display = DisplayStyle.Flex;
            progress.text = "Working…";
            try { await work(); }
            catch (OperationCanceledException) { Show("Cancelled. No pending texture assignments were applied. Imported files remain in Assets/Orbiters/MyAvatar.", HelpBoxMessageType.Info); }
            catch (Exception ex) { Show(ex.Message, HelpBoxMessageType.Warning); }
            finally
            {
                Running.Remove(id); busy = false; operation?.Dispose(); operation = null;
                if (root != null)
                {
                    content.Query<Button>().ForEach(b => b.SetEnabled(true)); content.Query<TextureDropElement>().ForEach(d => d.SetEnabled(true));
                    ShowBackground(backgroundStatus);
                    if (avatar) { undo.text = avatar.canRedo ? "Redo last apply" : "Undo last apply"; undo.SetEnabled(avatar.undoMaterials.Count > 0); save.SetEnabled(avatar.textures.Count > 0); }
                }
                var next = pendingAi; pendingAi = null;
                if (next != null && avatar) _ = next();
            }
        }

        private void RefreshResults()
        {
            if (!avatar || results == null) return;
            results.Clear();
            int remaining = avatar.textures.Count(t => !t.applied);
            Show(string.IsNullOrEmpty(avatar.notice) ? "Works offline. Original materials are preserved." : avatar.notice,
                remaining > 0 ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info);
            notice.style.display = string.IsNullOrEmpty(avatar.notice) ? DisplayStyle.None : DisplayStyle.Flex;
            MyAvatarResults.Populate(results, avatar, Edited, () => _ = Run(() => {
                int applied = TextureChanges.Apply(avatar, avatar.textures, avatar.batchFolder);
                avatar.notice = applied + " selected textures applied."; TextureMemory.Record(avatar, avatar.textures); TextureChanges.Dirty(avatar); RefreshResults(); return Task.CompletedTask;
            }));
            undo.text = avatar.canRedo ? "Redo last apply" : "Undo last apply";
            undo.SetEnabled(avatar.undoMaterials.Count > 0 && !busy); save.SetEnabled(avatar.textures.Count > 0 && !busy);
        }

        private void Show(string text, HelpBoxMessageType type) { if (notice != null) { notice.Set(text,type); notice.style.display = DisplayStyle.Flex; } }
        internal static Button Button(string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("mcb-button");
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("pressed"));
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("pressed"));
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
