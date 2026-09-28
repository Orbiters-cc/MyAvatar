using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Parameter estimates before build-time compression.
    internal sealed class ParametersSection : AvatarSection
    {
        private static readonly Color Descriptor = new Color32(0xe0, 0xe0, 0xe0, 0xff), VrcFury = new Color32(0x8a, 0x7d, 0xff, 0xff), Free = new Color32(0x3a, 0x3a, 0x3a, 0xff);
        private readonly MyAvatar avatar;
        private readonly Label total, chip, compressionText;
        private readonly BudgetBar bar;
        private IVisualElementScheduledItem pending;

        internal ParametersSection(MyAvatar avatar) : base("Parameters")
        {
            this.avatar = avatar;
            var refresh = new IconButton(IconGlyph.Refresh, "Refresh", "Count again", Refresh);
            refresh.AddToClassList("orb-icon-button--small");
            Actions.Add(refresh);

            var headline = new VisualElement(); headline.AddToClassList("parameters-headline"); Body.Add(headline);
            total = new Label(); total.AddToClassList("parameters-total"); headline.Add(total);
            var unit = new Label($"/ {ParameterBudget.MaxSyncedBits} bits before compression"); unit.AddToClassList("parameters-unit"); headline.Add(unit);
            chip = new Label(); chip.AddToClassList("parameters-chip"); headline.Add(chip);
            bar = new BudgetBar(); bar.AddToClassList("orb-budget--inline"); Body.Add(bar);

            var row = new VisualElement(); row.AddToClassList("parameters-compression"); Body.Add(row);
            var texts = new VisualElement(); texts.AddToClassList("parameters-compression__texts"); row.Add(texts);
            var title = new Label("Parameter compression"); title.AddToClassList("parameters-compression__title"); texts.Add(title);
            compressionText = new Label(); compressionText.AddToClassList("parameters-compression__text"); texts.Add(compressionText);

            // Toggles and controllers change as the hierarchy is edited; recount shortly after, not on every change.
            EditorApplication.hierarchyChanged += Schedule;
            Undo.undoRedoPerformed += Schedule;
            RegisterCallback<DetachFromPanelEvent>(_ => { EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule; });
            Refresh();
        }

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(400);
        }

        private void Refresh()
        {
            if (!avatar) return;
            var budget = AvatarParameterBudget.Estimate(avatar.gameObject);
            int vrcfury = Mathf.Max(0, budget.ToggleBits + budget.FullControllerBits);
            total.text = budget.TotalBeforeCompression.ToString();
            total.EnableInClassList("warning", budget.OverBudget);
            chip.text = budget.OverBudget ? $"{budget.TotalBeforeCompression - ParameterBudget.MaxSyncedBits} over" : $"{budget.Free} free";
            chip.EnableInClassList("warning", budget.OverBudget);
            chip.tooltip = budget.OverBudget ? "This estimate exceeds 256 bits before compression. Check the final build result in VRCFury." : null;
            bar.SetSegments(new[]
            {
                new BudgetBar.Segment($"Avatar {budget.DescriptorBits}", budget.DescriptorBits, Descriptor),
                new BudgetBar.Segment($"VRCFury {vrcfury}", vrcfury, VrcFury),
                new BudgetBar.Segment($"Free {budget.Free}", budget.Free, Free),
            });
            bar.tooltip = "Avatar: the avatar's own expression parameters. VRCFury: what its toggles, sliders and full controllers add at build. Estimated without building.";

            compressionText.text = budget.VrcFuryPresent ? budget.CompressionStatus : "VRCFury is not installed.";
            compressionText.tooltip = "Estimates are before compression. Change compression behavior from VRCFury's global settings.";
        }
    }
}
