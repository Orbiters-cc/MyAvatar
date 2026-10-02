using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.Vpm;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Clothes and accessories (alpha): a drop field like the texture one, the one question it may ask (which hand), what
    // needs a manual step, and the accessories on the avatar with their bone and a Remove button. On a custom base, each
    // accessory's fit question lives in its entry (AccessoriesSection.Fit.cs).
    internal sealed partial class AccessoriesSection : AvatarSection
    {
        internal const string Feature = "myavatar.accessories";
        internal static bool Enabled => OrbitersFeatures.IsEnabled(Feature);

        // What the section needs from the Inspector: its busy lock, the AI switch state and the texture pipeline.
        internal sealed class Host
        {
            public Func<Func<Task>, Task> Run;
            public Func<bool> AiOn;
            public Action<bool> SetAi;
            public Func<string[], Transform[], Task> ApplyTextures;
            public Action<string> Background;
            /// <summary>The Inspector's shared drop field.</summary>
            public DropZone Zone;
            /// <summary>True while the drop field shows the accessories' progress and status (the last drop was one).</summary>
            public Func<bool> OwnsZone = () => true;
            /// <summary>The field shows the accessories from now on: called before any accessory progress is shown in it.</summary>
            public Action ClaimZone = () => { };
        }

        private readonly MyAvatar avatar;
        private readonly Host host;
        private readonly DropZone zone;
        private readonly VisualElement choice, notes, list;
        private CancellationTokenSource ai;
        private int revision;
        // Accessories placed from their Bone menu: an AI answer still on its way never moves them again.
        private readonly HashSet<OrbitersAttachment> placedByHand = new HashSet<OrbitersAttachment>();
        internal Func<string, AccessoryAi.Request, CancellationToken, Task<AccessoryAi.Result>> RequestAi = AccessoryAi.RequestAsync;
        internal Func<string> Token = () => AuthenticationService.GetAuth()?.token;

        // No card around it: the drop field sits directly in the section, like the texture one.
        internal AccessoriesSection(MyAvatar avatar, Host host) : base("Clothes and accessories", card: false)
        {
            this.avatar = avatar;
            this.host = host;
            Actions.Add(new StageBadge(FeatureStage.Alpha));
            // The Inspector's one drop field ("Drop anything"): it sends accessory drops here.
            zone = host.Zone ?? new DropZone(DropZone.Anything, _ => { }, Array.Empty<VisualElement>(), host.SetAi);
            choice = new VisualElement(); choice.AddToClassList("accessory-choice"); Body.Add(choice);
            notes = new VisualElement(); notes.AddToClassList("accessory-notes"); Body.Add(notes);
            list = new VisualElement(); list.AddToClassList("accessory-list"); Body.Add(list);

            AccessoryService.Changed += Changed;
            EditorApplication.hierarchyChanged += Schedule;
            Undo.undoRedoPerformed += Schedule;
            OrbitersFeatures.Changed += FeatureChanged;
            CustomBaseDetection.Changed += BaseChanged;
            RefitRecords.Changed += RecordChanged;
            RefitEngine.Changed += Schedule;
            RegisterCallback<DetachFromPanelEvent>(_ => Detach());
            FeatureChanged(Feature);
        }

        internal void Detach()
        {
            AccessoryService.Changed -= Changed; EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule;
            OrbitersFeatures.Changed -= FeatureChanged; CancelAi();
            CustomBaseDetection.Changed -= BaseChanged; RefitRecords.Changed -= RecordChanged; RefitEngine.Changed -= Schedule;
            DetachFit();
        }

        internal DropZone Zone => zone;

        private void FeatureChanged(string key)
        {
            if (key != Feature) return;
            style.display = OrbitersFeatures.IsEnabled(Feature) ? DisplayStyle.Flex : DisplayStyle.None;
            Refresh();
        }

        private void Changed(MyAvatar changed) { if (changed == avatar) Refresh(); }

        private IVisualElementScheduledItem pending;
        private void Schedule() { pending?.Pause(); pending = schedule.Execute(Refresh).StartingIn(300); }

        // A new drop, the AI switch turned off or another account: answers still on their way are dropped.
        internal void CancelAi() { revision++; ai?.Cancel(); ai = null; host.Background(null); }

        internal async Task Drop(string[] paths)
        {
            host.ClaimZone();
            if (AccessoryService.Busy(avatar)) { zone.ShowDone("Still adding the previous accessory: drop again once it is on.", true); return; }
            CancelAi();
            zone.ShowProgress(.02f, "Opening…");
            await Report(async () => await FollowUp(await AccessoryService.DropAsync(avatar, paths, (value, text) => zone.ShowProgress(value, text), CancellationToken.None)));
        }

        private async Task Choose(string answer)
        {
            if (AccessoryService.Busy(avatar)) return;
            CancelAi();
            host.ClaimZone();
            zone.ShowProgress(.7f, "Attaching…");
            await Report(async () => await FollowUp(await AccessoryService.ChooseAsync(avatar, answer, (value, text) => zone.ShowProgress(value, text), CancellationToken.None)));
        }

        // Failures belong to this field, not to the texture field the Inspector reports to.
        private async Task Report(Func<Task> work)
        {
            try { await work(); }
            catch (OperationCanceledException) { AccessoryService.Status(avatar, "Cancelled.", false, avatar.accessoryNotes); }
            catch (Exception ex) { AccessoryService.Status(avatar, ex.Message, true, avatar.accessoryNotes); }
        }

        private async Task FollowUp(List<AccessoryService.Outcome> outcomes)
        {
            Refresh();
            // Images of the drop go to the accessories just added (or to the avatar when nothing was added).
            var images = outcomes.SelectMany(o => o.images ?? new List<string>()).Distinct().Where(File.Exists).ToArray();
            try
            {
                if (images.Length > 0)
                {
                    var scope = outcomes.Where(o => o.attachment != null).Select(o => o.attachment.transform).ToArray();
                    // Materials whose shader is missing have no slots: the accessory itself was still added.
                    if (scope.Length > 0 && TextureChanges.Scoped(TextureMatching.Slots(avatar), scope.ToList()).Count == 0)
                        AccessoryService.Status(avatar, ((avatar.accessoryStatus ?? "") + " Its textures were not applied: its materials have no texture slot My Avatar can edit (is their shader installed?).").Trim(),
                            avatar.accessoryWarning, avatar.accessoryNotes);
                    else await host.ApplyTextures(images, scope.Length > 0 ? scope : null);
                }
            }
            finally { foreach (var staging in outcomes.Select(o => o.staging).Distinct()) AccessoryService.Release(avatar, staging); }
            if (host.AiOn()) _ = AskAiAsync(outcomes.Where(o => o.attachment != null && o.plan != null).ToList(), revision);
            // Every drop asks again: on a custom base, does it fit?
            _ = CheckFitsAsync(outcomes.Where(o => o.attachment).Select(o => o.attachment).ToList(), dropped: true);
            _ = NameAsync(outcomes.Where(o => o.attachment).Select(o => o.attachment).ToList());
        }

        // A drop Unity finished after reloading its scripts: its images and AI help, as after any drop.
        private void ResumeFollowUp()
        {
            if (!AccessoryService.HasFollowUp(avatar)) return;
            _ = host.Run(async () =>
            {
                var outcomes = AccessoryService.TakeFollowUp(avatar);
                if (outcomes == null) return;
                CancelAi();
                await Report(() => FollowUp(outcomes));
                AccessoryFollowUps.Complete(avatar, outcomes);
            });
        }

        // Answers are for the drop, account and AI switch they were asked with.
        private bool Current(int started, string token) => started == revision && avatar && host.AiOn() && Token() == token;

        // Where an accessory is attached: an AI answer only applies to the placement it was asked about.
        private static (OrbitersAttachment.AttachMode mode, Transform parent, string links, Transform owner, Vector3 position, Quaternion rotation, Vector3 scale) Placement(OrbitersAttachment attachment) =>
            (attachment.mode, attachment.parent, string.Join(",", attachment.links.Select(l => (l.from ? l.from.GetInstanceID() : 0) + ">" + (l.to ? l.to.GetInstanceID() : 0))),
                attachment.transform.parent, attachment.transform.localPosition, attachment.transform.localRotation, attachment.transform.localScale);

        // After the local install: a guessed bone, unmatched clothing bones and manual steps, answered in the background.
        private async Task AskAiAsync(List<AccessoryService.Outcome> outcomes, int started)
        {
            string token = Token();
            if (string.IsNullOrEmpty(token)) return;
            var source = ai = new CancellationTokenSource();
            try
            {
                foreach (var outcome in outcomes)
                {
                    if (!Current(started, token)) return;
                    if (!outcome.attachment) continue;
                    var request = AccessoryAi.ForPlan(outcome.plan, outcome.candidate, outcome.docs);
                    if (!request.worthAsking) continue;
                    var placement = Placement(outcome.attachment);
                    host.Background($"Orbiters AI is checking {outcome.attachment.name}…");
                    var result = await RequestAi(token, request, source.Token);
                    source.Token.ThrowIfCancellationRequested();
                    if (!Current(started, token)) return;
                    // Placed by the user meanwhile (Bone menu, Inspector, Undo) or removed: their choice stands.
                    if (!outcome.attachment || placedByHand.Contains(outcome.attachment) || Placement(outcome.attachment) != placement) continue;
                    Apply(outcome, result);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (Current(started, token)) AddNotes(new MyAvatar.AccessoryNote { text = "Orbiters AI unavailable: " + ex.Message, warning = true }); }
            finally { if (ai == source) { ai = null; host.Background(null); } source.Dispose(); }
        }

        private void Apply(AccessoryService.Outcome outcome, AccessoryAi.Result result)
        {
            var attachment = outcome.attachment;
            if (result.target != null && attachment.mode == OrbitersAttachment.AttachMode.Parent && outcome.plan.ParentGuessed)
                AttachmentInstaller.Retarget(attachment, result.target, snap: false);
            if (result.links.Count > 0) AttachmentInstaller.Link(attachment, result.links);
            AddNotes(result.setup.Select(s => new MyAvatar.AccessoryNote { target = s.target, accessory = attachment, text = s.reason, warning = true })
                .Concat(result.warnings.Select(w => new MyAvatar.AccessoryNote { accessory = attachment, text = w })).ToArray());
            Refresh();
        }

        private void AddNotes(params MyAvatar.AccessoryNote[] added)
        {
            if (added.Length == 0) return;
            AccessoryService.Status(avatar, avatar.accessoryStatus, avatar.accessoryWarning || added.Any(n => n.warning), avatar.accessoryNotes.Concat(added).ToList());
        }

        private void Refresh()
        {
            if (!avatar || style.display == DisplayStyle.None) return;
            if (AccessoryService.Busy(avatar)) return;
            if (AccessoryService.HasFollowUp(avatar)) schedule.Execute(ResumeFollowUp);
            if (host.OwnsZone())
            {
                if (string.IsNullOrEmpty(avatar.accessoryStatus)) zone.ShowIdle();
                else zone.ShowDone(avatar.accessoryStatus, avatar.accessoryWarning);
            }
            RefreshChoice();
            RefreshNotes();
            RefreshList();
            RefreshFit();
            RefreshNames();
        }

        private void RefreshChoice()
        {
            choice.Clear();
            var options = AccessoryService.PendingOptions(avatar, out bool hands);
            choice.style.display = options.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (options.Count == 0) return;
            if (hands)
            {
                Title("This accessory comes for each hand. Which one should hold it?");
                var row = new VisualElement(); row.AddToClassList("accessory-choice__row"); choice.Add(row);
                foreach (var (label, value) in options)
                {
                    var answer = value;
                    var button = Small(label, () => { choice.style.display = DisplayStyle.None; _ = host.Run(() => Choose(answer)); });
                    button.RemoveFromClassList("accessory-button");
                    row.Add(button);
                }
                return;
            }
            // Each item with its picture and, with AI help on, a clean name; an add-on ("Extra Tentacles") is offered together
            // with the item it goes with.
            _ = NameOptionsAsync(options);
            var addons = options.Where(o => AccessoryCandidates.IsAddon(o.label)).ToList();
            bool together = addons.Count > 0 && addons.Count < options.Count;
            string Names(IEnumerable<(string label, string value)> items, string separator) => string.Join(separator, items.Select(o => OptionName(o.value, o.label)));
            Title(together ? $"{Names(addons, " and ")} look{(addons.Count == 1 ? "s" : "")} like an add-on of {Names(options.Except(addons), " or ")}: add them together, or pick."
                : $"This drop holds {options.Count} different items: choose what to add. The others stay here until you are done.");
            var cards = new VisualElement(); cards.AddToClassList("accessory-choice__cards"); choice.Add(cards);
            // As wide as the field allows, same margins left and right, like MCB's asset gallery; the picture stays square.
            CardGrid.Attach(cards, 112f, 8f, (card, width) =>
            {
                var picture = card.Q(className: "accessory-option__picture");
                if (picture != null) picture.style.height = width - 12f;
            });
            foreach (var (label, value) in options)
            {
                var answer = value;
                var card = new VisualElement { tooltip = label + "\n" + value }; card.AddToClassList("accessory-option"); cards.Add(card);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(value);
                string key = ModelThumbnails.KeyFor(value);
                var picture = new AssetThumbnail(() => ModelThumbnails.Get(asset, key), asset != null);
                picture.AddToClassList("accessory-option__picture"); card.Add(picture);
                var name = new Label(OptionName(value, label)); name.AddToClassList("accessory-option__name"); card.Add(name);
                var add = Small("Add", () => { card.style.display = DisplayStyle.None; _ = host.Run(() => Choose(answer)); });
                add.AddToClassList("accessory-option__button"); card.Add(add);
            }
            var actions = new VisualElement(); actions.AddToClassList("accessory-choice__actions"); choice.Add(actions);
            var all = Small(options.Count == 2 ? "Add both" : "Add all", () => { choice.style.display = DisplayStyle.None; _ = host.Run(() => Choose(AccessoryService.AllOptions)); });
            if (together) all.AddToClassList("mcb-button--primary");
            actions.Add(all);
            actions.Add(Small("Done", () => AccessoryService.DismissChoice(avatar)));
        }

        private void Title(string text)
        {
            var title = new Label(text); title.AddToClassList("accessory-choice__title"); choice.Add(title);
        }

        // Notes about an accessory on the avatar go in its entry of the list; the rest (refused files, errors) stay here.
        private void RefreshNotes()
        {
            notes.Clear();
            var installed = AttachmentInstaller.Installed(avatar.transform);
            // A fit question is about one accessory: once it is off the avatar (removed, undone), the question goes too.
            if (avatar.accessoryNotes.RemoveAll(n => !string.IsNullOrEmpty(n.fit) && !(n.accessory is OrbitersAttachment a && installed.Contains(a))) > 0)
                EditorUtility.SetDirty(avatar);
            foreach (var note in avatar.accessoryNotes.Where(n => !(n.accessory is OrbitersAttachment a && installed.Contains(a))))
                AddNote(notes, note);
        }

        private void AddNote(VisualElement parent, MyAvatar.AccessoryNote note)
        {
            if (!string.IsNullOrEmpty(note.fit)) { FitCard(parent, note); return; }
            var row = new VisualElement(); row.AddToClassList("accessory-note"); parent.Add(row);
            var dot = new VisualElement(); dot.AddToClassList("accessory-note__dot"); dot.EnableInClassList("warning", note.warning); row.Add(dot);
            var text = new Label(note.text); text.AddToClassList("accessory-note__text"); row.Add(text);
            if (note.modularAvatar) parent.Add(new DependencyPrompt("nadena.dev.modular-avatar", "This accessory is made for Modular Avatar.", "orbiters.myavatar"));
            if (note.vrcFury) parent.Add(new DependencyPrompt("com.vrcfury.vrcfury", "VRCFury adds a menu toggle and exact armature links.", "orbiters.myavatar"));
            if (!string.IsNullOrEmpty(note.duplicate))
            {
                var path = note.duplicate;
                var target = note.target as GameObject;
                row.Add(Small("Replace", () => _ = host.Run(() => Replace(note.accessory as OrbitersAttachment, target, path))));
                row.Add(Small("Add another", () => _ = host.Run(() => AddAnother(path))));
            }
            if (note.target != null)
            {
                var target = note.target;
                row.Add(Small("Select", () => Select(target)));
            }
            if (note.armatureFit && note.accessory is OrbitersAttachment fitted)
            {
                row.Add(Small("Cancel", () =>
                {
                    AttachmentFit.Cancel(fitted);
                    AccessoryService.Status(avatar, avatar.accessoryStatus, avatar.accessoryWarning, avatar.accessoryNotes.Where(n => n != note).ToList());
                }));
            }
        }

        private void RefreshList()
        {
            list.Clear();
            var installed = AttachmentInstaller.Installed(avatar.transform);
            if (installed.Count == 0) return;
            var title = new Label("On this avatar"); title.AddToClassList("accessory-list__title"); list.Add(title);
            foreach (var attachment in installed)
            {
                var entry = new VisualElement(); entry.AddToClassList("accessory-entry"); list.Add(entry);
                var row = new VisualElement(); row.AddToClassList("accessory-item"); entry.Add(row);
                // What it looks like as worn (its refit included), rendered in the background and kept.
                var worn = attachment.gameObject;
                string look = ModelThumbnails.KeyFor(attachment.variant) + "|" + attachment.name + "|" + string.Join(",",
                    attachment.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(r => r.sharedMesh ? r.sharedMesh.GetInstanceID() : 0));
                var picture = new AssetThumbnail(() => ModelThumbnails.Get(worn, look));
                picture.AddToClassList("accessory-item__picture"); row.Add(picture);
                // Beside the picture: its name, fit and buttons, then how it is attached on a second line.
                var body = new VisualElement(); body.AddToClassList("accessory-item__body"); row.Add(body);
                var top = new VisualElement(); top.AddToClassList("accessory-item__top"); body.Add(top);
                var name = new Label(attachment.DisplayName) { tooltip = attachment.DisplayName != attachment.name ? attachment.name : null };
                name.AddToClassList("accessory-item__name"); top.Add(name);
                var status = FitStatus(attachment); if (status != null) top.Add(status);
                var spacer = new VisualElement(); spacer.AddToClassList("accessory-item__spacer"); top.Add(spacer);
                string description = AccessoryService.Describe(attachment);
                var how = new Label(description) { tooltip = description }; how.AddToClassList("accessory-item__how"); body.Add(how);
                var item = attachment;
                if (attachment.mode == OrbitersAttachment.AttachMode.Parent)
                    top.Add(Small("Bone ▾", () => BoneMenu(item)));
                top.Add(Small("Select", () => Select(item.gameObject)));
                top.Add(Small("Remove", () =>
                {
                    entry.style.display = DisplayStyle.None;
                    AttachmentInstaller.Remove(item);
                    AccessoryService.Status(avatar, avatar.accessoryStatus, avatar.accessoryWarning, avatar.accessoryNotes.Where(n => n.accessory != item).ToList());
                }));
                TextureCard(entry, item);
                var own = avatar.accessoryNotes.Where(n => n.accessory == item).ToList();
                if (own.Count == 0) continue;
                var box = new VisualElement(); box.AddToClassList("accessory-entry__notes"); entry.Add(box);
                foreach (var note in own) AddNote(box, note);
            }
            var budget = Orbiters.Toolkit.Editor.VRChat.Parameters.AvatarParameterBudget.Estimate(avatar.gameObject);
            var cost = new Label(budget.VrcFuryPresent ? $"Menu toggles are VRCFury toggles under Accessories · {budget.TotalBeforeCompression} of 256 parameter bits used" : "Install VRCFury for menu toggles.");
            cost.AddToClassList("accessory-list__cost"); list.Add(cost);
        }

        private void BoneMenu(OrbitersAttachment attachment)
        {
            var menu = new GenericMenu();
            var index = Orbiters.Toolkit.Armature.AvatarBoneIndex.Build(avatar.transform, Orbiters.Toolkit.Editor.Posing.AvatarSkeleton.Bones(avatar.transform, AttachmentPlanner.Body(avatar.transform)));
            for (var id = HumanBodyBones.Hips; id < HumanBodyBones.LastBone; id++)
            {
                var bone = index.Humanoid(id);
                if (bone == null) continue;
                var target = bone;
                string group = id.ToString().Contains("Left") ? "Left/" : id.ToString().Contains("Right") ? "Right/" : "";
                menu.AddItem(new GUIContent(group + ObjectNames.NicifyVariableName(id.ToString())), attachment.parent == bone, () =>
                {
                    placedByHand.Add(attachment);
                    AttachmentInstaller.Retarget(attachment, target, snap: true);
                    Selection.activeGameObject = attachment.gameObject;
                    Refresh();
                });
            }
            menu.ShowAsContext();
        }

        // The copy on the avatar goes first: one My Avatar placed is removed as such, one put on by hand is deleted (Undo).
        private Task Replace(OrbitersAttachment existing, GameObject worn, string path)
        {
            if (existing != null) AttachmentInstaller.Remove(existing);
            else if (worn != null) Undo.DestroyObjectImmediate(worn);
            return AddAnother(path);
        }

        private async Task AddAnother(string path)
        {
            var outcomes = await AccessoryService.InstallPathAsync(avatar, path);
            await FollowUp(outcomes);
        }

        private static void Select(UnityEngine.Object target)
        {
            var go = target as GameObject ?? (target as Component)?.gameObject;
            if (go == null) return;
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        private static Button Small(string text, Action action)
        {
            var button = MyAvatarEditor.Button(text, () => { });
            button.AddToClassList("accessory-button");
            ButtonInteraction.RegisterImmediateClick(button, action);
            return button;
        }
    }

    [InitializeOnLoad]
    internal static class MyAvatarFeatures
    {
        static MyAvatarFeatures() => OrbitersFeatures.Register(new OrbitersFeature
        {
            Key = AccessoriesSection.Feature, Product = "My Avatar", Label = "Clothes and accessories",
            Description = "Drop clothing and accessory packages on the avatar: My Avatar places them and attaches them without changing the avatar.",
            Stage = FeatureStage.Alpha, Default = false,
        });
    }
}
