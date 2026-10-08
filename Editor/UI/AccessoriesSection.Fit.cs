using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.Editor.Vpm;
using Orbiters.Toolkit.VRChat;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // On an avatar with a known custom base, each accessory's fit question in its entry: does it fit (yes: add the custom
    // blendshapes it lacks; no: line it up with the original base and refit it), ReFit's one-click install when needed, and
    // the result with Restore and a way to ask a creator. Checks run in the background: a drop, the Inspector opening, a
    // new custom base version.
    internal sealed partial class AccessoriesSection
    {
        private CancellationTokenSource fitChecks;
        private CancellationTokenSource workCancellation;
        private double workStartedAt;
        private bool detecting, resuming, reconciling;
        // A failed detection (no connection, an unreadable body) is tried again after a while, not on every refresh.
        private double detectAgainAt;

        private void DetachFit()
        {
            fitChecks?.Cancel();
            fitChecks = null;
        }

        // Each refresh: detect the custom base once, drop a preview whose accessory is gone, continue what waited for ReFit.
        private void RefreshFit()
        {
            var active = AccessoryFit.Placement.Active;
            if (active != null && active.Attachment && active.Attachment.transform.IsChildOf(avatar.transform)
                && !avatar.accessoryNotes.Any(n => n.accessory == active.Attachment && n.fit == AccessoryFit.Place))
                active.Dispose();
            var current = CustomBaseDetection.Current(avatar.transform);
            if (!detecting && UnityEditor.EditorApplication.timeSinceStartup >= detectAgainAt && current == null)
                _ = DetectAsync();
            // Not while this refresh builds the list: dropping notes refreshes again.
            if (current != null && !reconciling && StaleFits(current).Count > 0)
            {
                reconciling = true;
                schedule.Execute(() => { reconciling = false; DropStaleFits(CustomBaseDetection.Current(avatar.transform)); });
            }
            if (resuming || AccessoryService.Busy(avatar)) return;
            // Lining up without the preview (it went away with a script reload or another line-up): ask again.
            var unplaced = avatar.accessoryNotes.FirstOrDefault(n => n.fit == AccessoryFit.Place && AccessoryFit.Placement.For(n.accessory as OrbitersAttachment) == null);
            if (unplaced != null)
            {
                resuming = true;
                schedule.Execute(() =>
                {
                    resuming = false;
                    UpdateFit(unplaced, n => n.fit = string.IsNullOrEmpty(n.fitNext) || n.fitNext == AccessoryFit.Place ? AccessoryFit.Refit : n.fitNext);
                });
                return;
            }
            if (!RefitEngine.Available) return;
            var pending = avatar.accessoryNotes.FirstOrDefault(n => n.fit == AccessoryFit.Install && n.accessory is OrbitersAttachment a && a);
            if (pending == null) return;
            resuming = true;
            schedule.Execute(() => { resuming = false; Act(pending, string.IsNullOrEmpty(pending.fitNext) ? AccessoryFit.AddShapes : pending.fitNext); });
        }

        // Once per avatar and custom base, the accessories already on it are checked (except those answered "Not now").
        private async Task DetectAsync()
        {
            detecting = true;
            try
            {
                var state = await CustomBaseDetection.DetectAsync(avatar.transform);
                if (!avatar) return;
                DropStaleFits(state);
                if (AccessoryFit.FirstLook(avatar, state))
                    await CheckFitsAsync(AttachmentInstaller.Installed(avatar.transform), dropped: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                detectAgainAt = UnityEditor.EditorApplication.timeSinceStartup + 60;
                Debug.LogWarning("[My Avatar] Could not recognise the avatar's custom base: " + ex.Message);
            }
            finally { detecting = false; }
        }

        // A custom base version was applied or reset on this avatar: detect it again and check its accessories (a line-up
        // with the previous original base ends by itself, see AccessoryFit.Placement).
        private void BaseChanged(Transform root)
        {
            if (!avatar || root == null || !(avatar.transform.IsChildOf(root) || root.IsChildOf(avatar.transform))) return;
            Schedule();
        }

        // A refit made elsewhere (MCB's panel, ReFit's window) can answer a question: check that accessory again.
        private void RecordChanged(SkinnedMeshRenderer renderer)
        {
            if (!avatar || renderer == null || !renderer.transform.IsChildOf(avatar.transform)) return;
            var attachment = renderer.GetComponentInParent<OrbitersAttachment>(true);
            // Our own refit records its meshes as it goes: its result, not a new check, answers it.
            if (attachment && attachment == working) return;
            var note = attachment ? avatar.accessoryNotes.FirstOrDefault(n => n.accessory == attachment && AccessoryFit.IsQuestion(n)) : null;
            if (note != null && !AccessoryService.Busy(avatar)) schedule.Execute(() => _ = RecheckAsync(attachment));
            Schedule();
        }

        private async Task RecheckAsync(OrbitersAttachment attachment)
        {
            try
            {
                var note = await AccessoryFit.CheckAsync(avatar, attachment, CancellationToken.None);
                if (!avatar || !attachment || attachment == working) return;
                if (note != null && !avatar.fitDismissed.Contains(AccessoryFit.DismissKey(attachment, note.fitKey))) SetFitNote(note);
                else RemoveFitNote(attachment);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { Debug.LogWarning("[My Avatar] " + ex.Message); }
        }

        // Fit notes answer the custom base they were asked for: after a version switch or reset, or once the refit a note
        // reports is gone, they go and their accessories are checked again. Never the one at work: its own answer ends it.
        private List<MyAvatar.AccessoryNote> StaleFits(CustomBaseState state)
        {
            string key = AccessoryFit.Key(state);
            return avatar.accessoryNotes.Where(n => n.accessory is OrbitersAttachment a && a && a != working && AccessoryFit.Stale(n, key)).ToList();
        }

        private void DropStaleFits(CustomBaseState state)
        {
            if (!avatar || state == null) return;
            var stale = StaleFits(state);
            if (stale.Count == 0) return;
            foreach (var note in stale) AccessoryFit.Placement.For(note.accessory as OrbitersAttachment)?.Dispose();
            AccessoryService.Status(avatar, avatar.accessoryStatus, avatar.accessoryWarning, avatar.accessoryNotes.Except(stale).ToList());
            if (AccessoryFit.Key(state) == null) return;
            foreach (var attachment in stale.Select(n => (OrbitersAttachment)n.accessory).Distinct()) _ = RecheckAsync(attachment);
        }

        // An answer the custom base outdated (a version switched meanwhile): asked again instead of failing.
        private bool AnsweredStale(MyAvatar.AccessoryNote note)
        {
            var current = CustomBaseDetection.Current(avatar.transform);
            if (current == null || !AccessoryFit.Stale(FitNote(note.accessory) ?? note, AccessoryFit.Key(current))) return false;
            DropStaleFits(current);
            return true;
        }

        private void AskAgain(OrbitersAttachment attachment)
        {
            RemoveFitNote(attachment);
            if (attachment) _ = RecheckAsync(attachment);
        }

        private async Task CheckFitsAsync(List<OrbitersAttachment> attachments, bool dropped)
        {
            if (attachments.Count == 0) return;
            fitChecks?.Cancel();
            var source = fitChecks = new CancellationTokenSource();
            try
            {
                foreach (var attachment in attachments)
                {
                    if (source.IsCancellationRequested || !avatar) return;
                    if (!attachment || avatar.accessoryNotes.Any(n => n.accessory == attachment && !string.IsNullOrEmpty(n.fit))) continue;
                    if (!dropped && AccessoryFit.Refitted(attachment)) continue;
                    var note = await AccessoryFit.CheckAsync(avatar, attachment, source.Token);
                    if (note == null || !avatar || !attachment || source.IsCancellationRequested) continue;
                    if (!dropped && avatar.fitDismissed.Contains(AccessoryFit.DismissKey(attachment, note.fitKey))) continue;
                    SetFitNote(note);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.LogWarning("[My Avatar] Could not check the accessories against the custom base: " + ex.Message); }
            finally
            {
                if (fitChecks == source) fitChecks = null;
                source.Dispose();
            }
        }

        // ---- Notes ----------------------------------------------------------------------------------------------------

        private MyAvatar.AccessoryNote FitNote(UnityEngine.Object attachment) =>
            avatar.accessoryNotes.FirstOrDefault(n => n.accessory == attachment && !string.IsNullOrEmpty(n.fit));

        private void SetFitNote(MyAvatar.AccessoryNote note)
        {
            var notes = avatar.accessoryNotes.Where(n => !(n.accessory == note.accessory && !string.IsNullOrEmpty(n.fit))).ToList();
            notes.Add(note);
            AccessoryService.Status(avatar, avatar.accessoryStatus, avatar.accessoryWarning, notes);
        }

        private void RemoveFitNote(UnityEngine.Object attachment) =>
            AccessoryService.Status(avatar, avatar.accessoryStatus, avatar.accessoryWarning,
                avatar.accessoryNotes.Where(n => !(n.accessory == attachment && !string.IsNullOrEmpty(n.fit))).ToList());

        // Changes the accessory's fit note (the stored one: the card's copy can be stale after a reload) and shows it.
        private void UpdateFit(MyAvatar.AccessoryNote card, Action<MyAvatar.AccessoryNote> change)
        {
            var note = FitNote(card.accessory) ?? card;
            change(note);
            SetFitNote(note);
        }

        // ---- The card -------------------------------------------------------------------------------------------------

        // Each accessory's last shown card state: a card only slides in when its state is new, not on every refresh.
        private readonly Dictionary<int, string> shownFit = new Dictionary<int, string>();
        // The accessory being worked on (refit, preparing the original body) and its live card.
        private OrbitersAttachment working;
        private float workProgress;
        private string workText;
        private FitCard workCard;

        private void FitCard(VisualElement parent, MyAvatar.AccessoryNote note)
        {
            var attachment = note.accessory as OrbitersAttachment;
            bool running = attachment && attachment == working;
            string state = running ? "running" : note.fit;
            int id = attachment ? attachment.GetInstanceID() : 0;
            bool animate = !shownFit.TryGetValue(id, out var shown) || shown != state;
            shownFit[id] = state;
            var model = new FitCard.Model
            {
                Fit = note.fit, Item = attachment ? attachment.DisplayName : "This accessory", Base = note.fitBase, Version = note.fitVersion,
                Thumbnail = CustomBaseDetection.Current(avatar.transform)?.Info?.Thumbnail,
                Error = note.text, Shapes = note.fitShapes, ShapeNames = note.fitShapeNames ?? new List<string>(), Rough = note.fitRough,
                CanCommission = attachment && AccessoryFit.CanCommission(attachment), Animate = animate,
                Running = running, Progress = workProgress, ProgressText = workText,
                StartedAt = workStartedAt,
            };
            var actions = attachment ? new FitCard.Actions
            {
                Fits = () => Act(note, AccessoryFit.AddShapes),
                Refit = () => Act(note, AccessoryFit.Place),
                NotNow = () => Dismiss(note),
                Confirm = () => Confirm(note),
                Cancel = () => CancelPlacement(note),
                Restore = () => RestoreFit(note),
                Commission = () => AccessoryFit.Commission(attachment),
                Retry = string.IsNullOrEmpty(note.fitNext) ? null : (Action)(() => Act(note, note.fitNext)),
                Stop = () => { ReportWork(workProgress, "Cancelling…"); workCancellation?.Cancel(); },
                Dismiss = () => RemoveFitNote(attachment),
                Install = card =>
                {
                    var prompt = new DependencyPrompt(RefitEngine.PackageId, "ReFit is not installed yet.", "orbiters.myavatar");
                    prompt.AddToClassList("fit-card__install");
                    // Unity reloads its scripts after the install: ReFit registers, and the answer continues on its own.
                    prompt.Installed += Schedule;
                    card.Add(prompt);
                },
            } : new FitCard.Actions();
            var fitCard = new FitCard(model, actions);
            if (running) workCard = fitCard;
            parent.Add(fitCard);
        }

        // The accessory's fit in its row of the list: fitted, a question waiting, or at work.
        private VisualElement FitStatus(OrbitersAttachment attachment)
        {
            string text = null, variant = null;
            if (attachment == working) { text = "Fitting…"; variant = "working"; }
            else if (avatar.accessoryNotes.Any(n => n.accessory == attachment && AccessoryFit.IsQuestion(n))) { text = "Fit?"; variant = "question"; }
            else if (AccessoryFit.Refitted(attachment)) { text = "Fitted"; variant = "fitted"; }
            if (text == null) return null;
            var chip = new Label(text) { tooltip = variant == "fitted" ? "Refitted to the custom base: it moves with the body." : null };
            chip.styleSheets.Add(UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.myavatar/Editor/UI/fit-card.uss"));
            chip.AddToClassList("fit-status"); chip.AddToClassList("fit-status--" + variant);
            return chip;
        }

        // Work on an accessory shows in its card (live bar), not in the drop field.
        private void BeginWork(OrbitersAttachment attachment, string text)
        {
            working = attachment; workProgress = 0.02f; workText = text; workCard = null;
            workCancellation = new CancellationTokenSource();
            workStartedAt = UnityEditor.EditorApplication.timeSinceStartup;
            Refresh();
        }

        private void ReportWork(float value, string text)
        {
            workProgress = value; workText = text;
            workCard?.SetProgress(value, text);
        }

        private void EndWork()
        {
            working = null; workCard = null;
            workCancellation?.Dispose(); workCancellation = null;
        }

        // ---- Answers --------------------------------------------------------------------------------------------------

        private void Act(MyAvatar.AccessoryNote note, string action)
        {
            var attachment = note.accessory as OrbitersAttachment;
            if (!attachment || AccessoryService.Busy(avatar) || AnsweredStale(note)) return;
            // ReFit is only needed now: install it, then the answer continues after Unity reloads.
            if (!RefitEngine.Available)
            {
                UpdateFit(note, n => { n.fitNext = action; n.fit = AccessoryFit.Install; });
                return;
            }
            if (action == AccessoryFit.Place) _ = host.Run(() => StartPlacement(note, attachment));
            else _ = host.Run(() => RunFit(note, attachment, RefitMode.Shapes, null));
        }

        private async Task StartPlacement(MyAvatar.AccessoryNote note, OrbitersAttachment attachment)
        {
            string question = AccessoryFit.IsQuestion(note) ? note.fit : AccessoryFit.Refit;
            AccessoryFit.Placement.Active?.Dispose();
            BeginWork(attachment, "Preparing the original body…");
            await Task.Yield();
            try
            {
                var placement = await AccessoryFit.PlaceAsync(avatar, attachment, ReportWork, workCancellation.Token, (FitNote(attachment) ?? note).fitKey);
                EndWork();
                if (placement == null) { AskAgain(attachment); return; }
                UpdateFit(note, n => { n.fitNext = question; n.fit = AccessoryFit.Place; });
            }
            catch (OperationCanceledException)
            {
                EndWork();
                UpdateFit(note, n => n.fit = question);
            }
            catch (Exception ex)
            {
                EndWork();
                UpdateFit(note, n => { n.fitNext = AccessoryFit.Place; n.fit = AccessoryFit.Failed; n.text = ex.Message; n.warning = true; });
            }
        }

        private void Confirm(MyAvatar.AccessoryNote note)
        {
            var attachment = note.accessory as OrbitersAttachment;
            if (!attachment || AnsweredStale(note)) return;
            // The preview is gone (a script reload): show it again first.
            var placement = AccessoryFit.Placement.For(attachment);
            if (placement == null) { Act(note, AccessoryFit.Place); return; }
            var original = placement.Take();
            _ = host.Run(() => RunFit(note, attachment, RefitMode.Fit, original));
        }

        private void CancelPlacement(MyAvatar.AccessoryNote note)
        {
            AccessoryFit.Placement.For(note.accessory as OrbitersAttachment)?.Dispose();
            UpdateFit(note, n => n.fit = string.IsNullOrEmpty(n.fitNext) || n.fitNext == AccessoryFit.Place ? AccessoryFit.Ask : n.fitNext);
        }

        private async Task RunFit(MyAvatar.AccessoryNote note, OrbitersAttachment attachment, RefitMode mode, CustomBaseOriginal original)
        {
            string action = mode == RefitMode.Fit ? AccessoryFit.Place : AccessoryFit.AddShapes;
            BeginWork(attachment, mode == RefitMode.Fit ? "Fitting to the custom body…" : "Adding the blendshapes…");
            await Task.Yield();
            try
            {
                var asked = FitNote(attachment) ?? note;
                var result = await AccessoryFit.RunAsync(avatar, attachment, mode, original, asked.fitShapeNames, asked.fitKey, ReportWork, workCancellation.Token);
                ReportWork(1f, "Done");
                EndWork();
                // The custom base changed before or during the refit: the question is asked again for the new one.
                if (result == null)
                {
                    AskAgain(attachment);
                    return;
                }
                var failed = result.Failed;
                if (result.Cancelled)
                {
                    CancelledFit(note, attachment);
                    return;
                }
                // Nothing was refitted and nothing failed: it already had every shape. No question left, no false "done".
                if (result.Refitted == 0 && failed.Count == 0)
                {
                    RemoveFitNote(attachment);
                    return;
                }
                UpdateFit(note, n =>
                {
                    if (result.Refitted == 0 && failed.Count > 0)
                    {
                        n.fit = AccessoryFit.Failed; n.fitNext = action; n.warning = true;
                        n.text = failed[0].Outcome.Error;
                        return;
                    }
                    n.fit = AccessoryFit.Done; n.fitShapes = result.Shapes; n.fitRough = result.Rough || failed.Count > 0; n.warning = n.fitRough;
                });
            }
            catch (OperationCanceledException)
            {
                EndWork();
                CancelledFit(note, attachment);
            }
            catch (Exception ex)
            {
                EndWork();
                UpdateFit(note, n => { n.fit = AccessoryFit.Failed; n.fitNext = action; n.text = ex.Message; n.warning = true; });
            }
            finally { original?.Dispose(); }
        }

        private void CancelledFit(MyAvatar.AccessoryNote note, OrbitersAttachment attachment)
        {
            var completed = AccessoryFit.CompletedNote(attachment);
            UpdateFit(note, n => { n.fit = completed != null ? AccessoryFit.Done : AccessoryFit.Ask;
                n.fitShapes = completed != null ? completed.fitShapes : 0; });
        }

        private void Dismiss(MyAvatar.AccessoryNote note)
        {
            var attachment = note.accessory as OrbitersAttachment;
            AccessoryFit.Placement.For(attachment)?.Dispose();
            if (attachment && !string.IsNullOrEmpty(note.fitKey))
            {
                string key = AccessoryFit.DismissKey(attachment, note.fitKey);
                if (!avatar.fitDismissed.Contains(key)) avatar.fitDismissed.Add(key);
            }
            RemoveFitNote(note.accessory);
        }

        // Back as it was before the refit, and asked again.
        private void RestoreFit(MyAvatar.AccessoryNote note)
        {
            var attachment = note.accessory as OrbitersAttachment;
            AccessoryFit.Restore(attachment);
            RemoveFitNote(attachment);
            if (attachment) _ = CheckFitsAsync(new List<OrbitersAttachment> { attachment }, dropped: true);
        }
    }
}
