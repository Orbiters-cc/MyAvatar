using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// The packages a gallery installation must change first, in a sheet over the gallery: each package with its versions
    /// (from → to, added, removed) and why, grouped as the VRChat SDK, what the asset needs, the project's other tools and
    /// what stays. Its primary button applies the whole plan and the installation then goes on by itself; Cancel stops the
    /// installation. It follows the job: progress while the plan is applied, the reload Unity does, a failure with Retry,
    /// a package that blocks with what to do. It closes once the project has the packages (Enter presses the primary
    /// button; Escape closes it, the card reopens it).
    /// </summary>
    internal sealed class GalleryPackagesDialog : VisualElement
    {
        private GalleryJob job;
        private readonly Action closed;
        private string shown;
        private VisualElement fill;
        private Label progressLabel;
        private Button primary;

        /// <summary>The job has a package plan to show: waiting for an answer, being applied, or Unity reloading after it.</summary>
        internal static bool Shows(GalleryJob job) => job != null && !job.Terminal && job.stage == GalleryJob.Dependencies && job.packages != null;

        internal GalleryPackagesDialog(GalleryJob job, Action closed)
        {
            this.job = job; this.closed = closed;
            AddToClassList("packages-dialog");
            focusable = true;
            VisualElement keys = null;
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                GalleryJournal.Changed += Changed;
                // Enter presses the primary button wherever the focus is in the sheet (the sheet itself has it when it opens).
                keys = (VisualElement)GetFirstAncestorOfType<GallerySheet>() ?? this;
                keys.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
                Render();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                GalleryJournal.Changed -= Changed;
                keys?.UnregisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            });
            Render();
        }

        private void OnKey(KeyDownEvent evt)
        {
            if (panel == null || (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)) return;
            if (primary != null && primary.enabledSelf) { Primary(); evt.StopPropagation(); }
        }

        private void Changed(GalleryJob changed)
        {
            if (changed == null || changed.id != job.id) return;
            // After a reload the journal holds another copy of the job.
            job = changed;
            if (!Shows(job)) { closed?.Invoke(); return; }
            if (Signature() != shown) Render();
            else UpdateProgress();
        }

        // What changes the dialog's layout (progress alone updates in place).
        private string Signature() => job.packages.state + "|" + job.waiting + "|" + job.packages.rows.Count + "|" + job.packages.error;

        private void Render()
        {
            Clear();
            fill = null; progressLabel = null; primary = null;
            var plan = job.packages;
            if (plan == null) return;
            shown = Signature();
            string asset = job.assetName;
            bool checking = !job.Waiting && !plan.Busy;
            var eyebrow = GalleryUI.Text(plan.Eyebrow, "gallery-sheet__eyebrow", this);
            eyebrow.EnableInClassList("packages-dialog__eyebrow--error", plan.state == GalleryPackagePlan.Failed || plan.state == GalleryPackagePlan.Blocked);
            GalleryUI.Text(plan.Title(asset), "gallery-sheet__title", this);
            GalleryUI.Text(plan.Text(asset), "gallery-sheet__text" + (plan.state == GalleryPackagePlan.Failed ? " packages-dialog__error" : ""), this);

            if (plan.Busy || checking)
            {
                var progress = GalleryUI.Box("packages-dialog__progress", this);
                var bar = GalleryUI.Box("packages-dialog__bar", progress);
                fill = GalleryUI.Box("packages-dialog__fill", bar);
                progressLabel = GalleryUI.Text("", "packages-dialog__progress-label", progress);
                UpdateProgress();
            }

            foreach (var group in plan.rows.GroupBy(r => r.Blocks ? "blocks" : r.group))
            {
                GalleryUI.Text(group.Key == "blocks" ? "What blocks it" : GalleryPackages.Heading(group.Key, asset), "packages-dialog__heading", this);
                var list = GalleryUI.Box("packages-dialog__list", this);
                foreach (var row in group) Row(row, list);
                list.Children().LastOrDefault()?.AddToClassList("packages-dialog__row--last");
            }

            foreach (var (note, warning) in plan.Notes())
                GalleryUI.Text(note, "packages-dialog__note" + (warning ? " packages-dialog__note--warning" : ""), this);

            var actions = GalleryUI.Box("gallery-sheet__actions", this);
            var cancel = GalleryUI.Button("Cancel", () => { if (!job.packages.Busy) { GalleryInstaller.Answer(job, false); closed?.Invoke(); } });
            cancel.tooltip = "Stops the installation. Packages already changed stay as they are.";
            cancel.SetEnabled(!plan.Busy);
            actions.Add(cancel);
            string label = plan.Primary;
            if (label != null)
            {
                primary = GalleryUI.Button(checking ? "Checking…" : label, Primary, "mcb-button--primary");
                primary.SetEnabled(job.Waiting);
                actions.Add(primary);
            }
            else
            {
                var working = GalleryUI.Button("Updating…", () => { }, "mcb-button--primary");
                working.SetEnabled(false);
                actions.Add(working);
            }
        }

        private void Primary()
        {
            if (primary == null || !job.Waiting) return;
            // Pressed at once; the journal's change redraws the dialog in its new state.
            primary.SetEnabled(false);
            primary.text = job.packages.state == GalleryPackagePlan.Blocked ? "Checking…" : "Updating…";
            GalleryInstaller.ApprovePackages(job);
        }

        private void UpdateProgress()
        {
            var plan = job.packages;
            if (fill == null || plan == null) return;
            bool checking = !job.Waiting && !plan.Busy;
            float value = plan.state == GalleryPackagePlan.Reloading ? 1f : checking ? 0.04f : Mathf.Clamp01(plan.progress);
            fill.style.width = Length.Percent(Mathf.Max(4f, value * 100f));
            progressLabel.text = plan.state == GalleryPackagePlan.Reloading ? "Unity is reloading its scripts…"
                : checking ? "Checking the packages again…"
                : (plan.progressText ?? "Starting…") + (plan.steps > 0 ? $" ({plan.step} of {plan.steps})" : "");
        }

        private static void Row(GalleryPackageRow row, VisualElement list)
        {
            var line = GalleryUI.Box("packages-dialog__row", list);
            line.EnableInClassList("packages-dialog__row--kept", row.kind == GalleryPackageRow.Keep);
            var texts = GalleryUI.Box("packages-dialog__texts", line);
            GalleryUI.Text(row.name, "packages-dialog__name", texts);
            if (!string.IsNullOrEmpty(row.why) && row.kind != GalleryPackageRow.Keep) GalleryUI.Text(row.why, "packages-dialog__why", texts);
            var side = GalleryUI.Box("packages-dialog__side", line);
            GalleryUI.Text(row.Tag, "packages-dialog__tag packages-dialog__tag--" + row.kind, side);
            if (!string.IsNullOrEmpty(row.Versions)) GalleryUI.Text(row.Versions, "packages-dialog__versions", side);
        }
    }
}
