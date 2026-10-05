using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Versions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>What the gallery's sheets show: an asset's details, its purchase, and a job's question.</summary>
    internal static class GallerySheets
    {
        // ---- Question: code, files replaced, packages needed, parameter memory ----

        internal static void Question(VisualElement content, GalleryJob job, Action<bool> answer)
        {
            string eyebrow, confirm;
            switch (job.waiting)
            {
                case "code": eyebrow = "CODE FROM AN UNTRUSTED CREATOR"; confirm = "Install anyway"; break;
                case "conflicts": eyebrow = "FILES WILL BE REPLACED"; confirm = "Replace and continue"; break;
                case "dependencies": eyebrow = "PACKAGES NEEDED"; confirm = "Install packages and continue"; break;
                default: eyebrow = "PARAMETER MEMORY"; confirm = "Add anyway"; break;
            }
            GalleryUI.Text(eyebrow, "gallery-sheet__eyebrow", content);
            GalleryUI.Text(job.questionSummary, "gallery-sheet__title", content);
            string explanation = job.waiting == "code" ? "Choose Cancel unless you trust its author: nothing is imported, and the download is discarded."
                : job.waiting == "conflicts" ? "Unity's importer writes these files over your project's copies. Every avatar, prefab and scene using them changes too. Cancel keeps your files; nothing is imported."
                : job.waiting == "dependencies" ? "These are installed from the VPM repositories Orbiters knows. Nothing else in your project is updated."
                : "The avatar may not upload until some parameters are removed or compressed.";
            GalleryUI.Text(explanation, "gallery-sheet__text", content);
            if (job.questionLines.Count > 0)
            {
                var list = GalleryUI.Box("gallery-sheet__list", content);
                foreach (string line in job.questionLines.Take(120)) GalleryUI.Text(line, "gallery-sheet__line", list);
                if (job.questionLines.Count > 120) GalleryUI.Text($"…and {job.questionLines.Count - 120} more.", "gallery-sheet__line", list);
            }
            var actions = GalleryUI.Box("gallery-sheet__actions", content);
            actions.Add(GalleryUI.Button("Cancel", () => answer(false)));
            actions.Add(GalleryUI.Button(confirm, () => answer(true), job.waiting == "code" ? "gallery-btn--question" : "mcb-button--primary"));
        }

        // ---- Details ----

        internal static void Details(VisualElement content, GalleryAsset asset, GalleryCard card, MyAvatar avatar, Action<string> add, Action<GalleryCard> primary, Action withdrawn)
        {
            content.style.paddingTop = 0; content.style.paddingLeft = 0; content.style.paddingRight = 0;
            var hero = GalleryUI.Box("detail-hero", content);
            var picture = new Image { scaleMode = ScaleMode.ScaleAndCrop }; picture.AddToClassList("detail-hero__image"); hero.Add(picture);
            GalleryImages.Show(picture, asset.previews?.FirstOrDefault() ?? asset.thumbnail);
            var body = GalleryUI.Box(null, content);
            body.style.paddingLeft = 18; body.style.paddingRight = 18; body.style.paddingTop = 14;
            var titleRow = GalleryUI.Box("detail-title-row", body);
            GalleryUI.Text(asset.name, "detail-title", titleRow);
            var (chip, chipStyle) = GalleryUI.AccessChip(asset);
            var access = GalleryUI.Text(chip, "gallery-chip" + (chipStyle != null ? " " + chipStyle : ""), titleRow);
            access.tooltip = asset.access?.label;
            var creator = GalleryUI.Box("gallery-creator", body);
            var face = new Image { scaleMode = ScaleMode.ScaleAndCrop }; face.AddToClassList("gallery-creator__avatar"); creator.Add(face);
            if (!string.IsNullOrEmpty(asset.creator?.avatarUrl)) GalleryImages.Show(face, asset.creator.avatarUrl);
            GalleryUI.Text("by " + (asset.creator?.username ?? "unknown creator") + (asset.creator?.trusted == true ? " · trusted creator" : ""), "gallery-creator__name", creator);

            var fit = asset.fit;
            var variant = fit?.Compatible == true ? fit.variants[0] : null;
            string setupKey = variant?.manifest?.defaultSetup;
            var actions = GalleryUI.Box("detail-actions", body);
            if (variant != null && variant.manifest?.setups?.Count > 1 && card.State == GalleryUI.State.Add)
            {
                // Named setups the creator offers (left hand, right hand…): chosen here, never guessed.
                body.Insert(body.IndexOf(actions), GalleryUI.Text("Choose a setup", "detail-section"));
                var setups = new SegmentedControl(variant.manifest.setups.Select(s => s.label), index => setupKey = variant.manifest.setups[index].key);
                setups.AddToClassList("detail-setups");
                setups.SetIndex(Math.Max(0, variant.manifest.setups.FindIndex(s => s.key == setupKey)));
                body.Insert(body.IndexOf(actions), setups);
            }
            if (card.State == GalleryUI.State.Add) actions.Add(GalleryUI.Button("Add to " + (avatar ? avatar.name : "the avatar"), () => add(setupKey), "gallery-btn--add"));
            else if (card.State != GalleryUI.State.Unavailable)
            {
                string label = card.State == GalleryUI.State.Buy ? "Buy" : card.State == GalleryUI.State.Installed ? "Remove from avatar" : card.State == GalleryUI.State.Update ? "Update"
                    : card.State == GalleryUI.State.Question ? "Review" : card.State == GalleryUI.State.Failed ? "Retry" : "Cancel";
                actions.Add(GalleryUI.Button(label, () => primary(card), card.State == GalleryUI.State.Buy ? "gallery-btn--buy" : card.State == GalleryUI.State.Installed ? null : "gallery-btn--add"));
            }
            if (!string.IsNullOrWhiteSpace(asset.shortDescription)) GalleryUI.Text(asset.shortDescription, "gallery-sheet__text", body);

            var facts = GalleryUI.Box("detail-facts", body);
            void Fact(string label, string value, bool warning = false)
            {
                if (string.IsNullOrEmpty(value)) return;
                var fact = GalleryUI.Box("detail-fact", facts);
                GalleryUI.Text(label, "detail-fact__label", fact);
                GalleryUI.Text(value, "detail-fact__value" + (warning ? " warning" : ""), fact);
            }
            Fact("Version", fit?.release != null ? fit.release.version + (fit.release.scope != VersionScopes.Public ? " (" + fit.release.scope + ")" : "") : "No version for this avatar yet");
            Fact("For this avatar", fit?.Compatible == true ? "Fits " + (avatar ? avatar.name : "the avatar") : GalleryUI.FitNote(asset), fit?.Compatible != true);
            if (variant != null)
            {
                Fact("Download", GalleryUI.Size(variant.sizeBytes));
                Fact("Parameter memory", variant.parameterBits > 0 ? variant.parameterBits + " bits" : "None");
                Fact("Platforms", string.Join(", ", variant.platforms.Select(AvatarPlatform.Label)));
                Fact("Bases", variant.baseScope == "bases" ? string.Join(", ", variant.avatarBaseIds.Select(id => asset.bases.FirstOrDefault(b => b.id == id)?.name ?? "#" + id)) : "Any avatar");
                Fact("Code", variant.containsCode ? (asset.creator?.trusted == true ? "Contains code (trusted creator)" : "Contains code: you are asked before it is imported") : "No code", variant.containsCode && asset.creator?.trusted != true);
            }
            if (variant != null && variant.dependencies.Count > 0)
            {
                GalleryUI.Text("Needs", "detail-section", body);
                foreach (var dependency in variant.dependencies)
                {
                    var row = GalleryUI.Box("detail-dependency", body);
                    GalleryUI.Text(dependency.displayName ?? dependency.id, "detail-dependency__name", row);
                    bool present = AssetDatabase.IsValidFolder("Packages/" + dependency.id);
                    var state = GalleryUI.Text(present ? "Installed" : "Installed for you when you add it", "detail-dependency__state", row);
                    state.EnableInClassList("detail-dependency__state--ok", present);
                }
            }
            if (!string.IsNullOrWhiteSpace(asset.description) && asset.description != asset.shortDescription)
            {
                GalleryUI.Text("About", "detail-section", body);
                GalleryUI.Text(PlainText(asset.description), "detail-description", body);
            }
            if (asset.releases?.Count > 0)
            {
                GalleryUI.Text("Versions", "detail-section", body);
                body.Add(VersionTimeline.List(asset.releases.Cast<IVersionRecord>().ToList(), (record, header) =>
                {
                    if (card.Installed != null && record.Version == card.Installed.version) header.Add(VersionTimeline.Chip("Installed", VersionTimeline.Accent));
                    var release = (GalleryRelease)record;
                    if (release.status == "draft") header.Add(VersionTimeline.Chip("Draft", Color.gray));
                    if (release.status == "withdrawn") header.Add(VersionTimeline.Chip("Withdrawn", Color.gray));
                    if (release.status == "published" && asset.access?.state == "creator") header.Add(WithdrawLink(release, withdrawn));
                }, asset.releases.FindIndex(r => r.id == fit?.release?.id)));
            }
            GalleryUI.Box(null, body).style.height = 16;
        }

        // Takes the creator's published version out of the gallery: the first press asks, the second withdraws.
        // Copies already installed keep working.
        private static Button WithdrawLink(GalleryRelease release, Action withdrawn)
        {
            Button link = null;
            bool armed = false;
            link = GalleryUI.Button("Withdraw", async () =>
            {
                if (!armed)
                {
                    armed = true; link.text = $"Withdraw {release.version}?"; link.AddToClassList("gallery-link--danger");
                    link.tooltip = "It leaves the gallery. Copies already installed keep working.";
                    return;
                }
                link.SetEnabled(false); link.text = "Withdrawing…";
                try { await GalleryApi.WithdrawReleaseAsync(release.id); withdrawn(); }
                catch (Exception ex)
                {
                    armed = false; link.SetEnabled(true); link.text = "Withdraw"; link.RemoveFromClassList("gallery-link--danger");
                    link.tooltip = "Could not withdraw it: " + ex.Message;
                }
            }, "gallery-link");
            link.style.marginLeft = 4;
            return link;
        }

        // Markdown to readable text: headings, emphasis, links and images keep their words.
        internal static string PlainText(string markdown)
        {
            string text = Regex.Replace(markdown ?? "", @"!\[[^\]]*\]\([^)]*\)", "");
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
            text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"(\*\*|__|\*|_|`)", "");
            text = Regex.Replace(text, @"<[^>]+>", "");
            return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        }

        // ---- Purchase ----

        internal static void Buy(VisualElement content, GalleryAsset asset, Action redeemed)
        {
            GalleryUI.Text("BUY", "gallery-sheet__eyebrow", content).style.color = new Color(0.725f, 0.694f, 1f);
            GalleryUI.Text(asset.name + (asset.price != null ? " · " + asset.price.Label : ""), "gallery-sheet__title", content);
            GalleryUI.Text(asset.stores.Count == 0 ? "This asset is not sold in any store yet." : "Pick a store to buy it there. Once bought, it is added to your library and My Avatar can install it.",
                "gallery-sheet__text", content);
            var stage = GalleryUI.Box(null, content);
            void ShowStores()
            {
                stage.Clear();
                string preferredLabel = asset.stores.FirstOrDefault(s => s.preferred)?.label;
                foreach (var store in asset.stores)
                {
                    var row = GalleryUI.Box("buy-store" + (store.preferred ? " buy-store--preferred" : ""), stage);
                    var tile = GalleryUI.Text("", "buy-store__tile", row);
                    // The store's own mark when the package ships it, else its initial on its colour.
                    var mark = AssetDatabase.LoadAssetAtPath<Texture2D>($"Packages/orbiters.myavatar/Editor/Gallery/Icons/{store.provider.ToLowerInvariant()}.png");
                    if (mark != null) { tile.style.backgroundImage = mark; tile.style.backgroundColor = new Color(1f, 1f, 1f, 0.92f); }
                    else { tile.text = string.IsNullOrEmpty(store.label) ? "?" : store.label.Substring(0, 1).ToUpperInvariant(); tile.style.backgroundColor = StoreColor(store.provider); }
                    var texts = GalleryUI.Box("buy-store__texts", row);
                    GalleryUI.Text(store.label, "buy-store__name", texts);
                    if (store.preferred) GalleryUI.Text("Supports " + (asset.creator?.username ?? "the creator") + " best", "buy-store__note", texts);
                    var external = new VectorIcon(IconGlyph.External); external.AddToClassList("buy-store__open"); row.Add(external);
                    var chosen = store;
                    GalleryUI.Clickable(row, () => Purchase(chosen), "buy-store--pressed");
                }
                var key = GalleryUI.Button("I already have a license key", () =>
                {
                    stage.Clear();
                    stage.Add(new LicenseRedeemForm(redeemed, ShowStores, autoFocus: true) { style = { marginTop = 12 } });
                }, "gallery-link");
                key.style.alignSelf = Align.FlexStart; key.style.marginTop = 8;
                stage.Add(key);
            }
            async void Purchase(GalleryStore store)
            {
                stage.Clear();
                GalleryUI.Text("Opening " + store.label + "…", "gallery-sheet__text", stage);
                PurchaseStart start = null;
                try { start = await GalleryApi.StartPurchaseAsync(asset.id, store.provider); }
                catch (Exception ex) { Debug.Log("[My Avatar] Purchase tracking unavailable: " + ex.Message); }
                Application.OpenURL(start?.url ?? store.url);
                stage.Clear();
                GalleryUI.Text("Finish your purchase on " + store.label + ", then come back here.", "gallery-sheet__title", stage).style.fontSize = 13;
                if (start?.autoRedeem?.supported == true)
                {
                    var wait = GalleryUI.Box("buy-wait", stage);
                    wait.Add(GalleryUI.Spinner("buy-wait__spinner"));
                    var label = GalleryUI.Text(start.autoRedeem.reason + " You can also paste its license key.", "buy-wait__text", wait);
                    Poll(stage, asset, label, redeemed);
                }
                else GalleryUI.Text(start?.autoRedeem?.reason ?? "Then paste the license key from your receipt:", "gallery-sheet__text", stage);
                stage.Add(new LicenseRedeemForm(redeemed) { style = { marginTop = 12 } });
                var back = GalleryUI.Button("Choose another store", ShowStores, "gallery-link");
                back.style.alignSelf = Align.FlexStart;
                stage.Add(back);
            }
            ShowStores();
        }

        // Asks whether the purchase arrived while the sheet is open; the server checks the store each time.
        private static void Poll(VisualElement stage, GalleryAsset asset, Label label, Action redeemed)
        {
            bool asking = false;
            stage.schedule.Execute(async () =>
            {
                if (asking || stage.panel == null) return;
                asking = true;
                try
                {
                    var state = await GalleryApi.PurchaseStatusAsync(asset.id, CancellationToken.None);
                    if (state?.state == "redeemed") { label.text = "Added to your library!"; redeemed?.Invoke(); }
                }
                catch (Exception) { /* Next try; the license key stays available. */ }
                finally { asking = false; }
            }).Every(8000).Until(() => stage.panel == null || label.text.StartsWith("Added", StringComparison.Ordinal));
        }

        private static Color StoreColor(string provider)
        {
            switch (provider)
            {
                case "GUMROAD": return new Color32(255, 144, 232, 255);
                case "JINXXY": return new Color32(116, 92, 255, 255);
                case "PAYHIP": return new Color32(29, 177, 120, 255);
                case "LEMONSQUEEZY": return new Color32(255, 196, 0, 255);
                case "KOFI": return new Color32(41, 171, 224, 255);
                default: return new Color32(70, 70, 70, 255);
            }
        }
    }
}
