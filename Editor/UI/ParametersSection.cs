using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // How much of VRChat's 256 bits of synced parameters the avatar will use once VRCFury has built it, and a switch for
    // VRCFury's Parameter Compressor.
    internal sealed class ParametersSection : AvatarSection
    {
        private static readonly Color Descriptor = new Color32(0xe0, 0xe0, 0xe0, 0xff), VrcFury = new Color32(0x8a, 0x7d, 0xff, 0xff), Free = new Color32(0x3a, 0x3a, 0x3a, 0xff);
        private readonly MyAvatar avatar;
        private readonly Label total, chip, compressionText;
        private readonly BudgetBar bar;
        private readonly ToggleSwitch compression;
        private IVisualElementScheduledItem pending;

        internal ParametersSection(MyAvatar avatar) : base("Parameters")
        {
            this.avatar = avatar;
            var refresh = new IconButton(IconGlyph.Refresh, "Refresh", "Count again", Refresh);
            refresh.AddToClassList("orb-icon-button--small");
            Actions.Add(refresh);

            var headline = new VisualElement(); headline.AddToClassList("parameters-headline"); Body.Add(headline);
            total = new Label(); total.AddToClassList("parameters-total"); headline.Add(total);
            var unit = new Label($"/ {ParameterBudget.MaxSyncedBits} bits after build"); unit.AddToClassList("parameters-unit"); headline.Add(unit);
            chip = new Label(); chip.AddToClassList("parameters-chip"); headline.Add(chip);
            bar = new BudgetBar(); bar.AddToClassList("orb-budget--inline"); Body.Add(bar);

            var row = new VisualElement(); row.AddToClassList("parameters-compression"); Body.Add(row);
            var texts = new VisualElement(); texts.AddToClassList("parameters-compression__texts"); row.Add(texts);
            var title = new Label("Compress parameters"); title.AddToClassList("parameters-compression__title"); texts.Add(title);
            compressionText = new Label(); compressionText.AddToClassList("parameters-compression__text"); texts.Add(compressionText);
            compression = new ToggleSwitch(false, SetCompression); row.Add(compression);

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

        private void SetCompression(bool on)
        {
            if (!avatar) return;
            // The switch has already moved; count again once the component is added or removed.
            compressionText.text = on ? "Adding VRCFury's Parameter Compressor…" : "Removing VRCFury's Parameter Compressor…";
            schedule.Execute(() =>
            {
                AvatarParameterBudget.SetCompression(avatar.gameObject, on, includeChildren: true);
                Refresh();
            });
        }

        private void Refresh()
        {
            if (!avatar) return;
            var budget = AvatarParameterBudget.Estimate(avatar.gameObject);
            int vrcfury = Mathf.Max(0, budget.ToggleBits + budget.FullControllerBits - budget.CompressionSavings);
            total.text = budget.TotalAfterBuild.ToString();
            total.EnableInClassList("warning", budget.OverBudget);
            chip.text = budget.OverBudget ? $"{budget.TotalAfterBuild - ParameterBudget.MaxSyncedBits} over" : $"{budget.Free} free";
            chip.EnableInClassList("warning", budget.OverBudget);
            chip.tooltip = budget.OverBudget ? "VRChat refuses uploads above 256 bits. Compress parameters, or remove a few toggles." : null;
            bar.SetSegments(new[]
            {
                new BudgetBar.Segment($"Avatar {budget.DescriptorBits}", budget.DescriptorBits, Descriptor),
                new BudgetBar.Segment($"VRCFury {vrcfury}", vrcfury, VrcFury),
                new BudgetBar.Segment($"Free {budget.Free}", budget.Free, Free),
            });
            bar.tooltip = "Avatar: the avatar's own expression parameters. VRCFury: what its toggles, sliders and full controllers add at build. Estimated without building.";

            compression.SetEnabled(budget.VrcFuryPresent && AvatarParameterBudget.CanCompress);
            compression.SetValueWithoutNotify(budget.CompressionEnabled);
            if (!budget.VrcFuryPresent) compressionText.text = "Install VRCFury to compress parameters.";
            else if (budget.CompressionEnabled) compressionText.text = $"VRCFury packs numbers into shared slots · saves {budget.CompressionSavings} bits";
            else
            {
                var packed = AvatarParameterBudget.Estimate(avatar.gameObject, new AvatarParameterBudget.Options { AssumeCompression = true });
                int savings = budget.TotalAfterBuild - packed.TotalAfterBuild;
                compressionText.text = savings > 0 ? $"VRCFury packs numbers into shared slots · would save {savings} bits" : "Nothing to pack yet: the avatar has no number parameters to share.";
            }
            compression.tooltip = budget.CompressionEnabled && !string.IsNullOrEmpty(budget.CompressionPath) ? "Compressor on " + budget.CompressionPath : "Adds VRCFury's Parameter Compressor to the avatar.";
        }
    }
}
