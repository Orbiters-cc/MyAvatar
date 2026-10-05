using System;
using System.Linq;
using System.Threading;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Cleanup of gallery files: what can be freed and how much space it takes, what stays and why, and earlier cleanups
    /// that can still be restored. Nothing is deleted outright: files go to a quarantine in Library first.
    /// </summary>
    internal sealed class GalleryCleanupWindow : EditorWindow
    {
        private VisualElement body;
        private GalleryCleanup.Report report;
        private CancellationTokenSource scanning;

        internal static void Open()
        {
            var window = GetWindow<GalleryCleanupWindow>(true, "Gallery cleanup");
            window.minSize = new Vector2(420f, 360f);
            window.Show();
        }

        private void CreateGUI()
        {
            foreach (string path in new[] { "Packages/orbiters.toolkit/Editor/UI/settings-window.uss", GalleryUI.StyleSheetPath, "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss" })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet) rootVisualElement.styleSheets.Add(sheet);
            }
            rootVisualElement.AddToClassList("orb-settings");
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.AddToClassList("orb-settings__scroll"); rootVisualElement.Add(scroll);
            body = GalleryUI.Box("orb-settings__content gallery-cleanup", scroll);
            rootVisualElement.RegisterCallback<DetachFromPanelEvent>(_ => scanning?.Cancel());
            Scan();
        }

        private async void Scan()
        {
            scanning?.Cancel();
            var source = scanning = new CancellationTokenSource();
            body.Clear();
            var card = Card("Checking gallery files");
            var label = GalleryUI.Text("Reading what the gallery installed…", "orb-settings__caption", card);
            var track = GalleryUI.Box("gallery-progress", card); track.style.marginTop = 10; track.style.height = 8;
            var fill = GalleryUI.Box("gallery-progress__fill", track);
            try
            {
                report = await GalleryCleanup.ScanAsync((value, text) => { label.text = text; fill.style.width = Length.Percent(value * 100f); }, source.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { label.text = "The check stopped: " + ex.Message; return; }
            ShowReport();
        }

        private VisualElement Card(string title)
        {
            var card = GalleryUI.Box("orb-settings__card", body);
            GalleryUI.Text(title, "orb-settings__title", card);
            return card;
        }

        private void ShowReport()
        {
            body.Clear();
            var summary = Card(report.Reclaim.Count == 0 ? "Nothing to clean up" : $"{GalleryUI.Size(report.ReclaimBytes)} can be freed");
            GalleryUI.Text(report.Reclaim.Count == 0
                ? "Every file the gallery imported is still in use, or was not the gallery's to remove."
                : $"{report.Reclaim.Count} file{(report.Reclaim.Count == 1 ? "" : "s")} of removed gallery assets that nothing in the project or the open scenes uses. They move to a quarantine first: you can restore them below.",
                "orb-settings__caption", summary);
            if (report.Reclaim.Count > 0)
            {
                var list = GalleryUI.Box("gallery-sheet__list", summary);
                foreach (var group in report.Reclaim.GroupBy(i => i.from ?? "Gallery asset"))
                {
                    GalleryUI.Text($"{group.Key} · {GalleryUI.Size(group.Sum(i => i.bytes))}", "gallery-sheet__line", list).style.unityFontStyleAndWeight = FontStyle.Bold;
                    foreach (var item in group.Take(40)) GalleryUI.Text("   " + item.path, "gallery-sheet__line", list);
                    if (group.Count() > 40) GalleryUI.Text($"   …and {group.Count() - 40} more", "gallery-sheet__line", list);
                }
                var actions = GalleryUI.Box("gallery-sheet__actions", summary);
                actions.Add(GalleryUI.Button("Check again", Scan));
                actions.Add(GalleryUI.Button("Clean up " + GalleryUI.Size(report.ReclaimBytes), CleanUp, "mcb-button--primary"));
            }
            else
            {
                var actions = GalleryUI.Box("gallery-sheet__actions", summary);
                actions.Add(GalleryUI.Button("Check again", Scan));
            }

            if (report.Kept.Count > 0)
            {
                var kept = Card($"Kept · {report.Kept.Count} file{(report.Kept.Count == 1 ? "" : "s")}");
                GalleryUI.Text("Files of removed gallery assets that stay, with the reason. When usage is uncertain, a file always stays.", "orb-settings__caption", kept);
                var list = GalleryUI.Box("gallery-sheet__list", kept);
                foreach (var group in report.Kept.GroupBy(i => i.reason))
                {
                    GalleryUI.Text(group.Key, "gallery-sheet__line", list).style.unityFontStyleAndWeight = FontStyle.Bold;
                    foreach (var item in group.Take(12)) GalleryUI.Text("   " + item.path, "gallery-sheet__line", list);
                    if (group.Count() > 12) GalleryUI.Text($"   …and {group.Count() - 12} more", "gallery-sheet__line", list);
                }
            }

            var batches = GalleryCleanup.Batches();
            if (batches.Count > 0)
            {
                var quarantine = Card("Earlier cleanups");
                GalleryUI.Text("Kept in the project's Library folder. Restore puts the files back where they were.", "orb-settings__caption", quarantine);
                foreach (var batch in batches)
                {
                    var row = GalleryUI.Box("detail-dependency", quarantine);
                    row.style.minHeight = 34; row.style.marginTop = 6;
                    GalleryUI.Text($"{new DateTime(batch.createdAt, DateTimeKind.Utc).ToLocalTime():g} · {batch.items.Count} files · {GalleryUI.Size(batch.Bytes)}", "detail-dependency__name", row);
                    var chosen = batch;
                    row.Add(GalleryUI.Button("Restore", () => Restore(chosen), "gallery-link"));
                    row.Add(GalleryUI.Button("Delete for good", () => Delete(chosen), "gallery-link"));
                }
            }
        }

        private void CleanUp()
        {
            if (report == null || report.Reclaim.Count == 0) return;
            var batch = GalleryCleanup.Quarantine(report);
            Debug.Log($"[My Avatar] Moved {batch?.items.Count ?? 0} unused gallery files ({GalleryUI.Size(batch?.Bytes ?? 0)}) to {batch?.folder}.");
            Scan();
        }

        private void Restore(GalleryCleanup.Batch batch)
        {
            var blocked = GalleryCleanup.Restore(batch);
            if (blocked.Count > 0) Debug.LogWarning("[My Avatar] Not restored, a file is at its place now: " + string.Join(", ", blocked));
            Scan();
        }

        // Deleting a quarantined batch is the user's explicit choice; it stays one step away from the restore button.
        private void Delete(GalleryCleanup.Batch batch)
        {
            if (!EditorUtility.DisplayDialog("Delete these files for good?", $"{batch.items.Count} quarantined files ({GalleryUI.Size(batch.Bytes)}) are deleted and cannot be restored.", "Delete", "Cancel")) return;
            GalleryCleanup.Delete(batch);
            Scan();
        }
    }
}
