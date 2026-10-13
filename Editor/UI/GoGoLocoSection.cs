using System.Collections.Generic;
using Orbiters.MyAvatar.Editor.Locomotion;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // GoGoLoco, Franada's locomotion system, in one click, framed like face tracking: offered on every humanoid avatar,
    // then what is on it (Select, Remove) with a big tile per version of GoGoLoco to switch between them, or the setup made
    // another way.
    internal sealed class GoGoLocoSection : AvatarSection
    {
        private const string LogoPath = "Packages/orbiters.myavatar/Editor/UI/GoGoLocoLogo.png";
        private static readonly Color Glow = new Color(.36f, .55f, 1f, .2f);

        private readonly MyAvatar avatar;
        private readonly VisualElement hero, plan;
        private readonly Label note;
        private IVisualElementScheduledItem pending;
        private string shown;
        private GameObject root;
        private GoGoLocoReport report;

        internal GoGoLocoSection(MyAvatar avatar) : base("Locomotion", card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            hero = new VisualElement(); Body.Add(hero);
            plan = new VisualElement(); Body.Add(plan);
            note = new Label(); note.AddToClassList("ft-note"); note.style.display = DisplayStyle.None; Body.Add(note);
            ToolHero.Credit(Body, "Locomotion by", "Franada’s GoGoLoco", GoGoLoco.Author,
                GoGoLoco.Author + "\nGoGoLoco is Franada’s: credit it on your store or product page when you sell an avatar that uses it.");
            RegisterCallback<AttachToPanelEvent>(_ => { Subscribe(); Schedule(); });
            RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            Refresh();
        }

        private void Subscribe()
        {
            Unsubscribe();
            EditorApplication.hierarchyChanged += Schedule; Undo.undoRedoPerformed += Schedule; PackageSetup.Finished += SetupFinished;
        }

        private void Unsubscribe()
        {
            EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule; PackageSetup.Finished -= SetupFinished;
        }

        private void SetupFinished(int id) { if (avatar && id == avatar.GetInstanceID()) Schedule(); }

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(400);
        }

        private void Refresh()
        {
            // What a download finished meanwhile did (or why it didn't), said once.
            string finished = GoGoLoco.Downloads.TakeNote(avatar);
            if (finished != null) SetNote(finished, !finished.StartsWith("GoGoLoco is on", System.StringComparison.Ordinal));
            root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            bool fits = root != null && GoGoLoco.Fits(root.transform);
            report = GoGoLoco.Inspect(fits ? root.transform : null);
            string state = !fits ? "" : report.Prefab != null ? "ours:" + report.Prefab.GetInstanceID() + ":" + GoGoLoco.Version
                : report.Existing != null ? "other:" + report.Existing : "ready:" + GoGoLoco.Installed + ":" + GoGoLoco.UsesModularAvatar + ":" + GoGoLoco.Downloads.Waiting(avatar);
            style.display = state.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (state == shown) return;
            shown = state;
            hero.Clear(); Actions.Clear();
            if (report.Prefab != null) ShowOurs();
            else if (report.Existing != null) ShowOther();
            else if (fits) ShowSuggestion();
        }

        private ToolHero Hero(string eyebrow, string eyebrowClass, string title, string subtitle, string version = null)
        {
            var frame = new ToolHero(hero, Glow, eyebrow, eyebrowClass, title, subtitle, version);
            var logo = AssetDatabase.LoadAssetAtPath<Texture2D>(LogoPath);
            if (logo != null)
            {
                var picture = new Image { image = logo, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                picture.AddToClassList("ft-hero__picture");
                frame.Badge.Add(picture);
            }
            return frame;
        }

        private void ShowSuggestion()
        {
            string with = GoGoLoco.UsesModularAvatar ? "Modular Avatar" : "VRCFury";
            bool waiting = GoGoLoco.Downloads.Waiting(avatar);
            var frame = Hero("READY FOR GOGOLOCO", null, "GoGoLoco",
                "Franada’s locomotion system: sit, lie down and pose anywhere, stay in place, and more from the Action Menu in VRChat. It goes on with " + with +
                ", as GoGoLoco All; once it is on, you can switch to another version." +
                (waiting ? " It goes on as soon as Unity has imported what was downloaded."
                    : GoGoLoco.Installed ? "" : " What it downloads first (Creator Companion) is shown before anything changes."));
            Button add = null;
            add = MyAvatarEditor.PressButton(waiting ? "Downloading…" : "Add", Press);
            add.AddToClassList("ft-primary");
            add.tooltip = "Add GoGoLoco to this avatar (one Undo step).";
            add.SetEnabled(!waiting);
            frame.Actions.Add(add);

            void Press()
            {
                add.SetEnabled(false);
                var missing = GoGoLoco.Missing();
                if (missing.Count > 0)
                {
                    // The plan of what would be downloaded comes first; nothing changes until it is accepted.
                    SetNote(null, false);
                    plan.Clear();
                    plan.Add(new PackageSetupPrompt(GoGoLoco.Downloads, avatar, missing, "GoGoLoco", (result, warning) =>
                    {
                        plan.Clear();
                        SetNote(result, warning);
                        add.SetEnabled(!GoGoLoco.Downloads.Waiting(avatar));
                        shown = null;
                        Refresh();
                    }));
                    return;
                }
                SetNote("Adding GoGoLoco…", false);
                // Paint the immediate response before the prefab and Undo work.
                schedule.Execute(() =>
                {
                    string result = root != null ? GoGoLoco.Start(avatar, root.transform) : "The avatar is no longer open.";
                    SetNote(result, result != null);
                    add.SetEnabled(true);
                    Refresh();
                });
            }
        }

        private void ShowOurs()
        {
            var prefab = report.Prefab;
            string version = GoGoLoco.Version;
            var frame = Hero("GOGOLOCO ON", null, report.Variant != null ? "GoGoLoco " + report.Variant.Name : "GoGoLoco",
                "“" + prefab.name + "” is on this avatar: its menu is in the Action Menu in VRChat.", version != null ? "v" + version : null);
            ShowVariants(frame.Frame, prefab);
            var select = MyAvatarEditor.Button("Select", () => { Selection.activeGameObject = prefab; EditorGUIUtility.PingObject(prefab); });
            select.AddToClassList("avatar-link");
            select.tooltip = "Select GoGoLoco on the avatar.";
            Actions.Add(select);
            if (!GoGoLoco.Removable(prefab)) return;
            var remove = MyAvatarEditor.PressButton("Remove", () =>
            {
                SetEnabled(false);
                schedule.Execute(() => { GoGoLoco.Remove(prefab); SetEnabled(true); SetNote(null, false); Refresh(); });
            });
            remove.AddToClassList("avatar-link");
            remove.tooltip = "Take GoGoLoco off this avatar. Undo puts it back; the downloaded package stays installed.";
            Actions.Add(remove);
        }

        // One big tile per version, like face tracking's trackers: the one on the avatar lit, the others a press away.
        private void ShowVariants(VisualElement frame, GameObject prefab)
        {
            var current = report.Variant;
            var variants = GoGoLoco.Variants(current != null ? current.ModularAvatar : GoGoLoco.UsesModularAvatar);
            if (variants.Count == 0 || (variants.Count == 1 && current != null && variants[0].Path == current.Path)) return;
            bool removable = GoGoLoco.Removable(prefab);
            var block = new VisualElement(); block.AddToClassList("gogo-variants"); frame.Add(block);
            var title = new Label("Version"); title.AddToClassList("ft-setting__title"); title.AddToClassList("gogo-variants__title"); block.Add(title);
            var tiles = new VisualElement(); tiles.AddToClassList("ft-trackers"); block.Add(tiles);
            var caption = new Label(); caption.AddToClassList("ft-trackers__caption"); block.Add(caption);
            var buttons = new List<(GoGoLocoVariant variant, Button tile)>();
            foreach (var variant in variants)
            {
                int bits = variant.BitsOn(root != null ? root.transform : null);
                var tile = MyAvatarEditor.PressButton("", () => Switch(prefab, variant, buttons, tiles, caption));
                tile.AddToClassList("ft-tracker"); tile.AddToClassList("gogo-variant");
                tile.EnableInClassList("ft-tracker--on", current != null && variant.Path == current.Path);
                var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(variant.IconPath);
                if (icon != null)
                {
                    var picture = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                    picture.AddToClassList("gogo-variant__icon"); tile.Add(picture);
                }
                var name = new Label(variant.Name); name.AddToClassList("ft-tracker__name"); name.pickingMode = PickingMode.Ignore; tile.Add(name);
                var sub = new Label(variant.Tagline); sub.AddToClassList("ft-tracker__sub"); sub.pickingMode = PickingMode.Ignore; tile.Add(sub);
                var cost = new Label(bits == variant.Bits ? bits + " bits" : "+" + bits + " bits");
                cost.AddToClassList("ft-bits"); cost.AddToClassList("gogo-variant__bits"); cost.pickingMode = PickingMode.Ignore; tile.Add(cost);
                tile.tooltip = variant.Description + " " + Cost(variant, bits) +
                               (removable ? "" : "\nGoGoLoco is part of this avatar's own prefab: change its version in that prefab.");
                tile.SetEnabled(removable);
                tiles.Add(tile);
                buttons.Add((variant, tile));
            }
            caption.text = current != null ? current.Description + " " + Cost(current, current.BitsOn(root != null ? root.transform : null))
                : "“" + prefab.name + "” isn't one of this GoGoLoco's versions: choose one to put it in its place.";
        }

        private static string Cost(GoGoLocoVariant variant, int bits) =>
            variant.Bits == 0 ? "" : bits == variant.Bits ? bits + " synced parameter bits."
                : bits + " synced parameter bits on this avatar (" + variant.Bits + " in all; the rest it shares with the avatar's own, like VRCEmote).";

        // The pressed tile lights at once and the others wait; the prefab is swapped on the next frame (one Undo step).
        private void Switch(GameObject prefab, GoGoLocoVariant target, List<(GoGoLocoVariant variant, Button tile)> buttons, VisualElement tiles, Label caption)
        {
            if (report.Variant != null && report.Variant.Path == target.Path) return;
            foreach (var (variant, tile) in buttons) tile.EnableInClassList("ft-tracker--on", variant == target);
            tiles.SetEnabled(false);
            caption.text = "Switching to GoGoLoco " + target.Name + "…";
            SetNote(null, false);
            schedule.Execute(() =>
            {
                bool done = GoGoLoco.Switch(prefab, target, out string problem);
                tiles.SetEnabled(true);
                SetNote(done ? problem ?? "GoGoLoco " + target.Name + " is on this avatar. Undo goes back." : problem, problem != null);
                shown = null;
                Refresh();
            });
        }

        private void ShowOther()
        {
            var frame = Hero("GOGOLOCO", "muted", "Already set up", "GoGoLoco is already set up: " + report.Existing + ".");
            if (report.ExistingObject == null) return;
            var target = report.ExistingObject;
            var select = MyAvatarEditor.Button("Select", () => { Selection.activeGameObject = target; EditorGUIUtility.PingObject(target); });
            select.AddToClassList("ft-ghost");
            frame.Actions.Add(select);
        }

        private void SetNote(string text, bool warning)
        {
            note.text = text;
            note.EnableInClassList("warning", warning);
            note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
