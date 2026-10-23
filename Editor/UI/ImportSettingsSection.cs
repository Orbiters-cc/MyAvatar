using System.Collections.Generic;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // The import settings VRChat's SDK panel asks of the avatar's files (VrchatImportSettings), folded under Materials.
    // They are fixed by themselves unless the user asked to be asked first (Orbiters settings); the card then says what
    // changed. Hidden when there is nothing to say.
    internal sealed class ImportSettingsSection : VisualElement
    {
        // Per avatar: whether the user opened the card (it stays closed after a fix by itself: the chip says it).
        private static readonly Dictionary<int, bool> Folds = new Dictionary<int, bool>();

        private readonly MyAvatar avatar;
        private readonly VisualElement body;
        private readonly Label chip, note, detail;
        private readonly Button askFirst, fix;
        private IVisualElementScheduledItem pending;
        private VrchatImportSettings.Plan plan = new VrchatImportSettings.Plan();

        internal ImportSettingsSection(MyAvatar avatar)
        {
            this.avatar = avatar;
            AddToClassList("optimize-card"); AddToClassList("materials-card");
            var header = new VisualElement(); header.AddToClassList("optimize-card__header"); Add(header);
            var chevron = new VectorIcon(IconGlyph.Chevron); chevron.AddToClassList("optimize-card__chevron"); header.Add(chevron);
            var title = new Label("VRChat import settings"); title.AddToClassList("optimize-card__title"); header.Add(title);
            chip = new Label(); chip.AddToClassList("optimize-card__chip"); header.Add(chip);
            body = new VisualElement(); body.AddToClassList("optimize-card__body"); Add(body);
            var noteRow = new VisualElement(); noteRow.AddToClassList("materials__note-row"); body.Add(noteRow);
            note = new Label(); note.AddToClassList("materials__note"); noteRow.Add(note);
            askFirst = MyAvatarEditor.Button("Ask me first", () => OrbitersFeatures.SetEnabled(VrchatImportSettings.Feature, false));
            askFirst.tooltip = "From now on, list these here and change them only when you click. Orbiters settings has the same switch.";
            askFirst.AddToClassList("avatar-link"); noteRow.Add(askFirst);
            detail = new Label(); detail.AddToClassList("materials__detail"); body.Add(detail);
            var actions = new VisualElement(); actions.AddToClassList("materials__actions"); body.Add(actions);
            fix = MyAvatarEditor.PressButton("Fix", Fix);
            fix.AddToClassList("mcb-button--primary");
            fix.tooltip = "Change these import settings as VRChat's SDK panel asks (each file is imported again once).";
            actions.Add(fix);
            header.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) Show(!Open); });
            Show(Open);
            RegisterCallback<AttachToPanelEvent>(_ => { Subscribe(); Schedule(0); });
            RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            style.display = DisplayStyle.None;
        }

        private bool Open => avatar && Folds.TryGetValue(avatar.GetInstanceID(), out bool open) && open;

        private void Show(bool open)
        {
            if (avatar) Folds[avatar.GetInstanceID()] = open;
            EnableInClassList("optimize-card--open", open);
            body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void Subscribe()
        {
            Unsubscribe();
            EditorApplication.hierarchyChanged += Changed;
            EditorApplication.projectChanged += ProjectChanged;
            OrbitersFeatures.Changed += FeatureChanged;
            VrchatImportSettings.Fixed += FixedElsewhere;
        }

        private void Unsubscribe()
        {
            EditorApplication.hierarchyChanged -= Changed; EditorApplication.projectChanged -= ProjectChanged;
            OrbitersFeatures.Changed -= FeatureChanged; VrchatImportSettings.Fixed -= FixedElsewhere;
        }

        private void Changed() => Schedule(400);
        // Imports and saves come in bursts: looked at once they settle.
        private void ProjectChanged() => Schedule(1500);
        private void FeatureChanged(string key) { if (key == VrchatImportSettings.Feature) Schedule(0); }
        private void FixedElsewhere(int id) { if (avatar && id == avatar.GetInstanceID()) Schedule(0); }

        private void Schedule(long delay)
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(delay);
        }

        private void Refresh()
        {
            var root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            if (root == null) { style.display = DisplayStyle.None; return; }
            VrchatImportSettings.AutoFix(avatar, root);
            plan = VrchatImportSettings.Inspect(root);
            VrchatImportSettings.FixedNotes.TryGetValue(avatar.GetInstanceID(), out string fixedNote);
            bool auto = OrbitersFeatures.IsEnabled(VrchatImportSettings.Feature);
            bool todo = !plan.Empty;
            style.display = todo || fixedNote != null || plan.Elsewhere.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            chip.text = todo ? plan.Count + " to fix" : fixedNote != null ? "Fixed" : plan.Elsewhere.Count > 0 ? plan.Elsewhere.Count + " in packages" : "";
            chip.style.display = chip.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            chip.EnableInClassList("materials-card__chip--todo", todo);
            note.text = todo ? "VRChat's SDK panel asks for " + plan.Describe() + " before an upload." : fixedNote ?? "";
            note.style.display = note.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            askFirst.style.display = !todo && fixedNote != null && auto ? DisplayStyle.Flex : DisplayStyle.None;
            fix.parent.style.display = todo ? DisplayStyle.Flex : DisplayStyle.None;
            detail.text = plan.Elsewhere.Count > 0
                ? plan.Elsewhere.Count + (plan.Elsewhere.Count == 1 ? " file comes" : " files come") + " from a package and stays as it is: VRChat's panel can still list it."
                : "";
            detail.style.display = detail.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Reimports take a moment: the card says so first, then the files are imported once it has drawn.
        private void Fix()
        {
            chip.text = "Fixing…";
            chip.EnableInClassList("materials-card__chip--todo", false);
            fix.SetEnabled(false);
            pending?.Pause();
            pending = schedule.Execute(() =>
            {
                var done = VrchatImportSettings.Fix(plan);
                if (!done.Empty && avatar) VrchatImportSettings.FixedNotes[avatar.GetInstanceID()] = "Fixed: " + done.Describe() + ".";
                fix.SetEnabled(true);
                Refresh();
            }).StartingIn(30);
        }
    }
}
