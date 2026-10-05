using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// "Add license key", as on the website's /assets page: the pill grows into a window (a sheet opening from the pill
    /// and collapsing back into it) holding the license key form.
    /// </summary>
    internal sealed class LicenseKeyButton : VisualElement
    {
        internal LicenseKeyButton(Func<VisualElement> sheetHost, Action redeemed)
        {
            if (GalleryUI.Sheet) styleSheets.Add(GalleryUI.Sheet);
            AddToClassList("license-bar");
            var open = new Button { tooltip = "Bought on Gumroad, Jinxxy or another creator store? Paste the key to add the asset to your library." };
            open.AddToClassList("license-bar__open");
            var icon = new VectorIcon(IconGlyph.Key); icon.AddToClassList("license-bar__icon"); open.Add(icon);
            GalleryUI.Text("Add license key", "license-bar__label", open);
            Add(open);
            ButtonInteraction.RegisterImmediateClick(open, () =>
            {
                var sheet = GallerySheet.Open(sheetHost(), open, 440f);
                GalleryUI.Text("Add a license key", "gallery-sheet__title", sheet.Content).style.marginTop = 0;
                GalleryUI.Text("Bought on Gumroad, Jinxxy or another creator store? Paste the key to add the asset to your library.", "gallery-sheet__text", sheet.Content);
                var form = new LicenseRedeemForm(() => { redeemed?.Invoke(); sheet.schedule.Execute(sheet.Close).StartingIn(900); }, sheet.Close, autoFocus: true);
                form.style.marginTop = 14;
                sheet.Content.Add(form);
            });
        }
    }

    /// <summary>
    /// The license key form of the website (LicenseRedeemIsland): the key and "Add asset", then "Checking creator stores…",
    /// and when Orbiters cannot tell the store, which creator sold it. Each phase slides in over the previous one.
    /// </summary>
    internal sealed class LicenseRedeemForm : VisualElement
    {
        private readonly Action redeemed, cancelled;
        private readonly bool autoFocus;
        private string key = "";
        private int? creatorId;
        private string feedback;
        private bool feedbackError, feedbackField;

        internal LicenseRedeemForm(Action redeemed, Action cancelled = null, bool autoFocus = false)
        {
            this.redeemed = redeemed; this.cancelled = cancelled; this.autoFocus = autoFocus;
            if (GalleryUI.Sheet) styleSheets.Add(GalleryUI.Sheet);
            AddToClassList("license-form");
            Input();
        }

        // Phases slide and fade in, like the website's SlidePanels.
        private VisualElement Phase(bool forward = true)
        {
            Clear();
            var phase = GalleryUI.Box("license-phase " + (forward ? "license-phase--from-right" : "license-phase--from-left"), this);
            phase.schedule.Execute(() => { phase.RemoveFromClassList("license-phase--from-right"); phase.RemoveFromClassList("license-phase--from-left"); });
            return phase;
        }

        private void Input(bool forward = true)
        {
            var phase = Phase(forward);
            GalleryUI.Text("License key", "license-form__label", phase);
            var row = GalleryUI.Box("license-form__row", phase);
            var field = new TextField { value = key };
            field.AddToClassList("license-form__field");
            field.EnableInClassList("license-form__field--invalid", feedbackField);
            var hint = GalleryUI.Text("Paste the key from your store receipt", "gallery-search__hint", field);
            hint.pickingMode = PickingMode.Ignore;
            hint.style.display = string.IsNullOrEmpty(key) ? DisplayStyle.Flex : DisplayStyle.None;
            row.Add(field);
            var submit = GalleryUI.Button("Add asset", () => Redeem(null), "license-form__submit mcb-button--primary");
            submit.SetEnabled(!string.IsNullOrWhiteSpace(key));
            row.Add(submit);
            field.RegisterValueChangedCallback(evt =>
            {
                key = evt.newValue ?? "";
                hint.style.display = string.IsNullOrEmpty(key) ? DisplayStyle.Flex : DisplayStyle.None;
                submit.SetEnabled(!string.IsNullOrWhiteSpace(key));
                if (feedbackField) { feedbackField = false; field.RemoveFromClassList("license-form__field--invalid"); }
            });
            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) { Redeem(null); evt.StopPropagation(); }
                if (evt.keyCode == KeyCode.Escape && cancelled != null) { cancelled(); evt.StopPropagation(); }
            }, TrickleDown.TrickleDown);
            if (!string.IsNullOrEmpty(feedback))
            {
                var note = GalleryUI.Text(feedback, "license-feedback" + (feedbackError ? " error" : ""), phase);
                note.style.marginTop = 8; note.style.marginLeft = 2;
            }
            if (cancelled != null)
            {
                var actions = GalleryUI.Box("license-form__actions", phase);
                actions.Add(GalleryUI.Button("Cancel", () => cancelled()));
            }
            if (autoFocus) field.schedule.Execute(() => field.Focus()).StartingIn(60);
        }

        private async void Redeem(int? creator)
        {
            if (string.IsNullOrWhiteSpace(key)) { Show("Paste the license key from your store receipt first.", true, field: true); return; }
            feedback = null;
            var phase = Phase();
            phase.AddToClassList("license-form__loading");
            phase.Add(GalleryUI.Spinner("buy-wait__spinner"));
            GalleryUI.Text("Checking creator stores…", "license-form__loading-text", phase);
            GalleryApi.RedeemResult result;
            try { result = await GalleryApi.RedeemAsync(key.Trim(), creator, CancellationToken.None); }
            catch (Exception ex) { Show(ex.Message, true); return; }
            if (panel == null) return;
            if (result.Creators != null && result.Creators.Count > 0) { PickCreator(result.Creators); return; }
            if (!result.Success) { Show(result.Message, true); return; }
            key = ""; creatorId = null;
            Show(result.Message, false);
            redeemed?.Invoke();
        }

        private void Show(string text, bool error, bool field = false)
        {
            feedback = text; feedbackError = error; feedbackField = field;
            Input(forward: false);
        }

        // Too many stores to try blindly: which creator sold it narrows the search.
        private void PickCreator(List<RedeemCreator> creators)
        {
            var phase = Phase();
            GalleryUI.Text("Which creator sold you this product?", "license-form__question", phase);
            GalleryUI.Text("We could not identify this key automatically. Picking the creator speeds up the search.", "gallery-sheet__text", phase);
            var actions = GalleryUI.Box("license-form__actions", phase);
            var retry = GalleryUI.Button("Try with this creator", () => Redeem(creatorId), "mcb-button--primary");
            retry.SetEnabled(false);
            var picker = new SearchableDropdownField("Creator", "Creators", creators.Select(c => new KeyValuePair<string, string>(c.id.ToString(), c.username)), null,
                value => { creatorId = int.TryParse(value, out int id) ? id : (int?)null; retry.SetEnabled(creatorId.HasValue); }, "Select the creator…");
            picker.style.marginTop = 10;
            phase.Insert(phase.IndexOf(actions), picker);
            actions.Add(GalleryUI.Button("Cancel", () => Show("Redemption cancelled.", true)));
            actions.Add(retry);
        }
    }
}
