using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Photoshoot;
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
        private DropZone zone;
        private AccessoriesSection accessories;
        private Button undo, save;
        private CancellationTokenSource operation, background;
        private readonly PhotoshootState photoshoot = new PhotoshootState();
        private Func<Task> pendingAi;
        // Transient UI state: Unity would otherwise serialize these across script reloads (turning null strings into "").
        [NonSerialized] private string note, backgroundStatus;
        [NonSerialized] private bool noteWarning;
        [NonSerialized] private int revision;
        [NonSerialized] private bool busy, aiConnected, aiEnabled, active, localAiChoice;
        internal Func<string, object, List<TextureEntry>, List<TextureSlot>, CancellationToken, Task<TextureAi.Result>> RequestAi = TextureAi.RequestAsync;
        private void OnEnable()
        {
            avatar = (MyAvatar)target; active = true;
            Undo.undoRedoPerformed += Reload; AssemblyReloadEvents.beforeAssemblyReload += Cancel;
            TextureAi.Preferences.Changed += PreferenceChanged;
            AuthenticationService.Changed += AccountChanged; OrbitersEnvironment.Changed += AccountChanged;
        }
        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Reload; AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            TextureAi.Preferences.Changed -= PreferenceChanged;
            AuthenticationService.Changed -= AccountChanged; OrbitersEnvironment.Changed -= AccountChanged;
            Cancel(); photoshoot.Dispose();
        }
        private void Cancel() { active = false; Edited(); operation?.Cancel(); }
        // Any edit, Undo or Redo makes a pending AI answer stale: it must never overwrite a newer state.
        private void Edited() { revision++; pendingAi = null; background?.Cancel(); if (active) SetBackground(null); }
        private void ApplyAiEnabled(bool enabled)
        {
            if (aiEnabled != enabled || !enabled) Edited();
            if (!enabled) accessories?.CancelAi();
            aiEnabled = enabled;
            zone?.SetAi(aiConnected, aiEnabled);
            accessories?.Zone.SetAi(aiConnected, aiEnabled);
        }
        private void AccountChanged() { localAiChoice = false; ApplyAiEnabled(false); }
        private void PreferenceChanged(string token, bool enabled)
        {
            if (!active || token != AuthenticationService.GetAuth()?.token) return;
            localAiChoice = true; ApplyAiEnabled(enabled);
        }
        private void Reload() { Edited(); if (avatar && root != null) RefreshResults(); }

        public override VisualElement CreateInspectorGUI()
        {
            if (TextureAi.Preferences.TryGetPendingChoice(AuthenticationService.GetAuth()?.token, out bool choice))
            { localAiChoice = true; ApplyAiEnabled(choice); }
            var shell = new OrbitersInspectorShell(); root = shell; root.AddToClassList("myavatar");
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.myavatar/Editor/UI/myavatar.uss"));
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/orbiters.myavatar/Editor/UI/MyAvatarLogo.svg.txt");
            if (asset) { var logo = new OrbitersVectorLogo(asset.text, new Vector2(309,258)); logo.AddToClassList("avatar-logo"); shell.Banner.Add(logo); }
            shell.Header.Add(new Orbiters.Toolkit.Editor.InspectorLockButton(
                "Lock this Inspector on the avatar: pick textures in the Project window, then drag them onto the drop zone.",
                "Locked on this avatar. Click to follow the selection again."));
            // The account only matters for AI texture matching; the corner robot on the drop zone shows whether it is on.
            shell.Account.Add(new OrbitersAccountElement("myavatar/connection", ai =>
                {
                    aiConnected = !string.IsNullOrEmpty(AuthenticationService.GetAuth()?.token);
                    if (!active) return;
                    if (!aiConnected || !localAiChoice) ApplyAiEnabled(aiConnected && ai);
                },
                "Allow My Avatar to use AI when it can’t find where to put a texture (you can turn it off easily here)."));
            content = new VisualElement(); content.AddToClassList("content"); root.Add(content);
            var section = new Label("Textures"); section.AddToClassList("section-title"); content.Add(section);
            undo = Button("Undo", () => _ = Run(() => { note = null; TextureChanges.UndoLast(avatar); RefreshResults(); return Task.CompletedTask; }));
            undo.tooltip = "Restore the materials from before this texture set. Click again to redo.";
            save = Button("Save", () => _ = Run(async () => {
                SetNote("Saving…", false);
                note = await TextureChanges.SaveAsync(avatar); noteWarning = false;
            }, false));
            save.tooltip = TextureChanges.Commits ? "Save the scene and generated assets, and record a “texture change” checkpoint in Unit Git. Nothing is pushed."
                : "Save the scene and the generated textures and materials. Install Unit Git to also record a local checkpoint.";
            save.AddToClassList("mcb-button--primary");
            undo.AddToClassList("drop-zone__undo"); save.AddToClassList("drop-zone__save");
            zone = new DropZone(DropZone.Textures, paths => _ = Run(() => Import(paths)), new VisualElement[] { undo, save }, enabled => _ = SetAiAsync(enabled));
            zone.SetAi(aiConnected, aiEnabled); content.Add(zone);
            results = new VisualElement(); results.AddToClassList("results"); content.Add(results);
            accessories = new AccessoriesSection(avatar, new AccessoriesSection.Host
            {
                Run = work => Run(work),
                AiOn = () => aiConnected && aiEnabled,
                SetAi = enabled => _ = SetAiAsync(enabled),
                ApplyTextures = (images, scope) => Import(images, scope),
                Background = status => accessories.Zone.SetBackground(status),
            });
            accessories.Zone.SetAi(aiConnected, aiEnabled); content.Add(accessories);
            content.Add(new ThumbnailSection(avatar, photoshoot));
            content.Add(new PosingSection(avatar));
            content.Add(new PhysicsSection(avatar));
            content.Add(new ParametersSection(avatar));
#if MYAVATAR_UNITGIT
            content.Add(new VersioningSection());
#endif
            RefreshResults();
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.IsPersistent(avatar))
            { content.SetEnabled(false); root.Add(new OrbitersNoticeElement("Use My Avatar on a scene avatar outside Play Mode.", HelpBoxMessageType.Info)); }
            var credit = new Orbiters.Toolkit.Editor.SupportCredit(); credit.AddToClassList("myavatar-credit"); root.Add(credit);
            var toolbar = new IMGUIContainer(DrawToolbar); toolbar.AddToClassList("myavatar-toolbar"); root.Add(toolbar);
            return root;
        }

        // Scope: only the materials of these objects (the accessories an accessory drop just added).
        private async Task Import(string[] paths, Transform[] scope = null)
        {
            var cancellation = operation.Token;
            int started = revision;
            note = null; results.Clear();
            zone.ShowProgress(.04f, "Finding textures…");
            var files = await Task.Run(() => TextureImport.Expand(paths), cancellation);
            cancellation.ThrowIfCancellationRequested();
            var scoped = scope?.Where(t => t).ToList() ?? new List<Transform>();
            var slots = TextureChanges.Scoped(TextureMatching.Slots(avatar), scoped);
            if (slots.Count == 0) throw new InvalidOperationException("No editable texture slots were found below this avatar.");
            if (slots.Count > 512) throw new InvalidOperationException("This avatar has more than 512 texture slots. Place My Avatar on a smaller avatar root.");
            string folder = "Assets/Orbiters/MyAvatar/" + Guid.NewGuid().ToString("N");
            var entries = await TextureImport.ImportAsync(files, folder, (value, text) => { if (active) zone.ShowProgress(value, text); }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!avatar) return;
            zone.ShowProgress(.78f, "Matching " + entries.Count + " textures…");
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
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
            bool ask = unmatched.Count > 0 && open.Count > 0 && !string.IsNullOrEmpty(token) && aiEnabled;
            object payload = ask ? TextureAi.Payload(unmatched, open, stats) : null;
            zone.ShowProgress(.9f, "Applying to " + entries.Where(e => e.material).Select(e => e.material).Distinct().Count() + " materials…");
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
            Undo.RecordObject(avatar, "My Avatar: texture set");
            int count = TextureChanges.Apply(avatar, entries, folder, scoped);
            if (count == 0) { avatar.undoMaterials.Clear(); avatar.canRedo = false; }
            avatar.textures = entries; avatar.batchFolder = folder; avatar.batchScope = scoped; avatar.notice = null;
            TextureMemory.Record(avatar, entries);
            TextureChanges.Dirty(avatar);
            zone.ShowProgress(1f, "Done");
            await FinishImportAsync(ask ? () => ResolveAsync(token, payload, entries, unmatched, open, folder) : (Func<Task>)null,
                cancellation, started);
        }

        private async Task FinishImportAsync(Func<Task> next, CancellationToken cancellation, int started)
        {
            await Task.Delay(260, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (active && avatar && aiEnabled && started == revision) pendingAi = next;
        }

        // Runs after the local apply has released the UI; merges confident answers into the same batch.
        private async Task ResolveAsync(string token, object payload, List<TextureEntry> entries, List<TextureEntry> sent, List<TextureSlot> slots, string folder)
        {
            if (!active || !avatar || !aiEnabled) return;
            background?.Cancel();
            var source = background = new CancellationTokenSource();
            int started = revision;
            var before = TextureChanges.Capture(avatar, slots);
            SetBackground($"Orbiters AI is placing {sent.Count} more texture{(sent.Count == 1 ? "" : "s")}…");
            try
            {
                var result = await RequestAi(token, payload, sent, slots, source.Token);
                while (busy && !source.IsCancellationRequested) await Task.Delay(50, source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (!active || !avatar || !aiEnabled || started != revision) return;
                TextureChanges.Revise(avatar, entries, folder, result.changes, before);
                TextureMemory.Record(avatar, entries);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { if (active && avatar && aiEnabled && started == revision) { note = null; SetNote(StatusText() + " · Orbiters AI unavailable: " + ex.Message, true); } return; }
            finally
            {
                if (background == source) { background = null; if (active) SetBackground(null); }
                source.Dispose();
            }
            if (!active || !avatar || !aiEnabled || started != revision) return;
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
            if (!active || !avatar || busy || !Running.Add(avatar.GetInstanceID())) return;
            if (edits) Edited();
            int id = avatar.GetInstanceID(); busy = true; operation = new CancellationTokenSource();
            content.Query<Button>().ForEach(b => b.SetEnabled(false));
            try { await work(); }
            catch (OperationCanceledException) { note = "Cancelled."; noteWarning = false; }
            catch (Exception ex) { note = ex.Message; noteWarning = true; }
            finally
            {
                Running.Remove(id); busy = false; operation?.Dispose(); operation = null;
                if (active && root != null) { content.Query<Button>().ForEach(b => b.SetEnabled(true)); RefreshResults(); }
                var next = pendingAi; pendingAi = null;
                if (next != null && active && aiEnabled && avatar) _ = next();
            }
        }

        // One line that summarises the current set: counts per dropped file, not per material it was applied to.
        private string StatusText()
        {
            var files = avatar.textures.GroupBy(TextureMemory.Identity).ToList();
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
            if (busy && string.IsNullOrEmpty(note)) return;
            if (avatar.textures.Count == 0) { zone.ShowIdle(string.IsNullOrEmpty(note) ? null : note, noteWarning); return; }
            bool pending = !avatar.canRedo && avatar.textures.GroupBy(TextureMemory.Identity).Any(g => !g.Any(t => t.applied));
            bool hasNote = !string.IsNullOrEmpty(note);
            zone.ShowDone(hasNote ? note : StatusText(), hasNote ? noteWarning : pending);
            zone.SetBackground(backgroundStatus);
            if (!avatar.canRedo) MyAvatarResults.Populate(results, avatar, Edited, () => _ = Run(() => {
                note = null;
                TextureChanges.Apply(avatar, avatar.textures, avatar.batchFolder);
                TextureMemory.Record(avatar, avatar.textures); TextureChanges.Dirty(avatar); return Task.CompletedTask;
            }));
            OptimizationCard.Populate(results, avatar, () => _ = Run(Optimize), () => _ = Run(() => {
                note = TextureOptimization.Revert(avatar); noteWarning = note != null; return Task.CompletedTask;
            }), RefreshResults);
        }

        // Import settings change on the main thread; the drop zone shows the progress first, like a texture drop.
        private async Task Optimize()
        {
            var cancellation = operation.Token;
            note = null; results.Clear();
            await TextureOptimization.OptimizeAsync(avatar, (value, text) => { if (active) zone.ShowProgress(value, text); }, cancellation);
            await Task.Delay(260, cancellation);
        }

        // Blendshape Links still ships with MCB; the button opens it when MCB is installed.
        private const string BlendShapeLinksMenu = "Tools/My Custom Base (MCB)/Blendshape Links";
        private static readonly bool BlendShapeLinksInstalled = Type.GetType("BlendShapeLinksDebugWindow, mcb.Editor") != null;

        // Bottom toolbar in Unity's own style, like MCB's.
        private static void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(new GUIContent("Settings", "Orbiters settings: production (default) or development server."), EditorStyles.toolbarButton, GUILayout.Width(126f)))
                Orbiters.Toolkit.Editor.OrbitersSettingsWindow.Open();
            using (new EditorGUI.DisabledScope(!BlendShapeLinksInstalled))
            {
                var links = new GUIContent("Blendshape Links", BlendShapeLinksInstalled
                    ? "Open the Blendshape Links tool."
                    : "Blendshape Links comes with MCB for now: install My Custom Base to use it.");
                if (GUILayout.Button(links, EditorStyles.toolbarButton, GUILayout.Width(126f))) EditorApplication.ExecuteMenuItem(BlendShapeLinksMenu);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // The shared writer publishes optimistic state and serializes account writes across inspectors.
        private async Task SetAiAsync(bool enabled)
        {
            if (!active) return;
            localAiChoice = true;
            ApplyAiEnabled(enabled);
            string token = AuthenticationService.GetAuth()?.token;
            Task<bool> request = TextureAi.SetEnabledAsync(token, enabled);
            long choice = TextureAi.Preferences.Revision;
            try { await request; }
            catch (Exception)
            {
                if (active && choice == TextureAi.Preferences.Revision && token == AuthenticationService.GetAuth()?.token)
                    SetNote("AI is paused locally. Could not save the account setting; check your connection and try again.", true);
            }
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
