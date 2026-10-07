using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Versions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// Publishing to the gallery: the asset (new, or one of the creator's own, linked to its Gumroad or Jinxxy product),
    /// the version (number, changelog, scope and testers, as MCB's versions), its packages per platform and base (built
    /// with the packaging rules and tried on this avatar first), then the creator's rights confirmation and Publish.
    /// </summary>
    internal sealed partial class GalleryCreatorPage : VisualElement
    {
        private static readonly string[] Currencies = { "USD", "EUR", "GBP", "JPY", "CAD", "AUD" };
        private static readonly string[] Stores = { null, "GUMROAD", "JINXXY", "PAYHIP", "LEMONSQUEEZY" };
        private readonly MyAvatar avatar;
        private readonly Action back;
        private GalleryCreatorDraft draft = GalleryCreatorDraft.Load();
        private readonly VisualElement steps;
        private CreatorAssets mine;
        private List<GalleryBase> bases = new List<GalleryBase>();
        private bool publishing;

        /// <param name="back">Leaves the page: closes the creator window.</param>
        internal GalleryCreatorPage(MyAvatar avatar, Action back)
        {
            this.avatar = avatar; this.back = back;
            if (GalleryUI.Sheet) styleSheets.Add(GalleryUI.Sheet);
            AddToClassList("gallery-page");
            var header = GalleryUI.Box("gallery-page__header", this);
            GalleryUI.Text("Publish to the gallery", "gallery-page__title", header);
            GalleryUI.Text("Buyers add your clothes and accessories to their avatar in one click. Each version holds one package per platform and avatar base; buyers get the one that fits.",
                "creator-intro", this);
            steps = GalleryUI.Box(null, this);
            schedule.Execute(() => _ = LoadAsync());
            // The MCP tool edits the same draft: show its version.
            void Replaced() { if (publishing) return; draft = GalleryCreatorDraft.Load(); if (mine != null) Build(); }
            RegisterCallback<AttachToPanelEvent>(_ => GalleryPublisher.DraftReplaced += Replaced);
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                GalleryPublisher.DraftReplaced -= Replaced;
                CloseStudio();
                photoshoot?.Dispose();
                photoshoot = null;
                GalleryPictures.UnloadAll();
            });
        }

        private async Task LoadAsync()
        {
            steps.Clear();
            steps.Add(GalleryUI.Spinner("buy-wait__spinner"));
            try
            {
                mine = await GalleryApi.CreatorAssetsAsync(CancellationToken.None);
                bases = (await GalleryApi.BasesAsync(CancellationToken.None))?.bases ?? new List<GalleryBase>();
            }
            catch (Exception ex)
            {
                steps.Clear();
                GalleryUI.Text(ex.Message.Contains("creator") ? ex.Message + " Turn them on in your Orbiters account (Creator tools), then come back." : "Could not load your assets: " + ex.Message, "creator-message error", steps);
                steps.Add(GalleryUI.Button("Try again", () => _ = LoadAsync()));
                return;
            }
            if (string.IsNullOrEmpty(draft.preferredStore)) draft.preferredStore = mine?.preferredStore;
            Build();
        }

        private void Build()
        {
            draft.Save();
            steps.Clear();
            AssetStep(Step(1, "Asset", AssetSummary(), draft.assetId > 0 || (draft.newAsset && !string.IsNullOrWhiteSpace(draft.name))));
            VersionStep(Step(2, "Version", draft.version + (draft.scope != VersionScopes.Public ? " · " + draft.scope : ""), !string.IsNullOrWhiteSpace(draft.version)));
            VariantsStep(Step(3, "Packages", draft.variants.Count(v => v.report != null && v.report.Publishable) + " of " + draft.variants.Count + " ready",
                draft.variants.Count > 0 && draft.variants.All(v => v.report != null && v.report.Publishable)));
            PicturesStep(Step(4, "Pictures", PicturesSummary(), HasCardPicture()));
            PublishStep(Step(5, "Publish", draft.rights ? "Rights confirmed" : "", false));
        }

        private VisualElement Step(int number, string title, string summary, bool done)
        {
            var step = GalleryUI.Box("creator-step", steps);
            step.EnableInClassList("creator-step--done", done);
            var header = GalleryUI.Box("creator-step__header", step);
            GalleryUI.Text(done ? "✓" : number.ToString(), "creator-step__number", header);
            GalleryUI.Text(title, "creator-step__title", header);
            GalleryUI.Text(summary, "creator-step__summary", header);
            return step;
        }

        // ---- 1. Asset ----

        private string AssetSummary() => draft.newAsset ? (string.IsNullOrWhiteSpace(draft.name) ? "New asset" : draft.name)
            : mine?.assets.FirstOrDefault(a => a.id == draft.assetId)?.name ?? "Choose one";

        private void AssetStep(VisualElement step)
        {
            var linkable = mine?.assets ?? new List<CreatorAsset>();
            foreach (var asset in linkable.Take(30))
            {
                var row = GalleryUI.Box("creator-asset", step);
                row.EnableInClassList("creator-asset--selected", !draft.newAsset && draft.assetId == asset.id);
                GalleryUI.Text(asset.name, "creator-asset__name", row);
                GalleryUI.Text(asset.inGallery ? (asset.type == "CLOTHING" ? "Clothing" : "Accessory") + " · " + asset.releases.Count + " version" + (asset.releases.Count == 1 ? "" : "s")
                    : "Add to the gallery", "creator-asset__meta", row);
                var chosen = asset;
                GalleryUI.Clickable(row, () => ChooseAsset(chosen), "buy-store--pressed");
            }
            var create = GalleryUI.Box("creator-asset", step);
            create.EnableInClassList("creator-asset--selected", draft.newAsset);
            GalleryUI.Text("+  New asset", "creator-asset__name", create);
            GalleryUI.Clickable(create, () => { draft.newAsset = true; draft.assetId = 0; draft.releaseId = 0; Build(); }, "buy-store--pressed");
            if (draft.newAsset) NewAssetForm(step);
            else ExistingAssetOptions(step, linkable.FirstOrDefault(a => a.id == draft.assetId));
            Label("Preferred store", step);
            var stores = GalleryUI.Box("creator-row", step);
            foreach (string store in Stores)
            {
                string id = store;
                var chip = GalleryUI.Button(store == null ? "No preference" : StoreLabel(store), () => { draft.preferredStore = id; _ = SavePreferredStore(id); Build(); }, "creator-chip");
                chip.EnableInClassList("creator-chip--on", draft.preferredStore == store);
                stores.Add(chip);
            }
            Hint("Buyers see this store first, marked as the one that supports you best (the one leaving you the best split). It is your default for every asset.", step);
        }

        private async Task SavePreferredStore(string id)
        {
            try { await GalleryApi.SetPreferredStoreAsync(id); }
            catch (Exception ex) { Message("Could not save your preferred store: " + ex.Message, true); }
        }

        private void ChooseAsset(CreatorAsset asset)
        {
            draft.newAsset = false;
            draft.assetId = asset.id;
            draft.name = asset.name;
            draft.type = asset.inGallery ? asset.type : draft.type;
            draft.listed = asset.listed;
            draft.releaseId = asset.releases.FirstOrDefault(r => r.status == "draft")?.id ?? 0;
            var latest = asset.releases.Where(r => r.status != "draft").Select(r => r.version).OrderByDescending(v => v, Comparer<string>.Create(GalleryUI.CompareVersions)).FirstOrDefault();
            draft.version = asset.releases.FirstOrDefault(r => r.status == "draft")?.version ?? NextVersion(latest);
            Build();
        }

        internal static string NextVersion(string latest)
        {
            if (string.IsNullOrEmpty(latest)) return "1.0.0";
            var parts = latest.Split('-')[0].Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToList();
            while (parts.Count < 3) parts.Add(0);
            parts[parts.Count - 1]++;
            return string.Join(".", parts);
        }

        private void NewAssetForm(VisualElement step)
        {
            Label("Name", step);
            step.Add(Field(draft.name, value => draft.name = value));
            Label("Type", step);
            var type = new SegmentedControl(new[] { "Accessory", "Clothing" }, index => { draft.type = index == 1 ? "CLOTHING" : "ACCESSORY"; draft.Save(); });
            type.SetIndex(draft.type == "CLOTHING" ? 1 : 0);
            step.Add(type);
            Label("Short description", step);
            step.Add(Field(draft.shortDescription, value => draft.shortDescription = value));
            Label("Description", step);
            step.Add(Field(draft.description, value => draft.description = value, multiline: true));
            Label("Price", step);
            var price = GalleryUI.Box("creator-row", step);
            var free = GalleryUI.Button("Free", () => { draft.free = !draft.free; Build(); }, "creator-chip");
            free.EnableInClassList("creator-chip--on", draft.free);
            price.Add(free);
            if (!draft.free)
            {
                var amount = Field(draft.priceCents > 0 ? (draft.priceCents / 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "",
                    value => draft.priceCents = decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)Math.Round(d * 100) : 0);
                amount.tooltip = "Price shown in the gallery; buyers pay in your store.";
                price.Add(amount);
                var currency = new PopupField<string>(Currencies.ToList(), Math.Max(0, Array.IndexOf(Currencies, draft.currency)));
                currency.RegisterValueChangedCallback(evt => { draft.currency = evt.newValue; draft.Save(); });
                currency.AddToClassList("creator-field"); currency.style.width = 70;
                price.Add(currency);
            }
            Label("Store pages", step);
            step.Add(Field(draft.gumroad, value => draft.gumroad = value, placeholder: "https://you.gumroad.com/l/…"));
            var jinxxy = Field(draft.jinxxy, value => draft.jinxxy = value, placeholder: "https://jinxxy.com/you/…");
            jinxxy.style.marginTop = 4;
            step.Add(jinxxy);
            Hint("Connect your stores on Orbiters (Creator > Integrations) to redeem license keys and add purchases automatically.", step);
        }

        private void ExistingAssetOptions(VisualElement step, CreatorAsset asset)
        {
            if (asset == null) return;
            if (!asset.inGallery)
            {
                Label("Add it to the gallery as", step);
                var type = new SegmentedControl(new[] { "Accessory", "Clothing" }, index => { draft.type = index == 1 ? "CLOTHING" : "ACCESSORY"; draft.Save(); });
                type.SetIndex(draft.type == "CLOTHING" ? 1 : 0);
                step.Add(type);
            }
            Label("Store products", step);
            if (asset.stores.Count > 0) foreach (var store in asset.stores) Hint(StoreLabel(store.provider) + " · " + store.url, step);
            else Hint("No store product is linked yet: link one so its license keys and purchases add the asset.", step);
            var link = GalleryUI.Box("creator-actions", step);
            link.Add(GalleryUI.Button("Link a Gumroad product…", () => _ = LinkProduct(asset, "GUMROAD", step)));
            link.Add(GalleryUI.Button("Link a Jinxxy product…", () => _ = LinkProduct(asset, "JINXXY", step)));
        }

        private async Task LinkProduct(CreatorAsset asset, string provider, VisualElement step)
        {
            StoreProducts products;
            try { products = await GalleryApi.StoreProductsAsync(provider, "", CancellationToken.None); }
            catch (Exception ex) { Message(ex.Message, true); return; }
            if (products == null || !products.connected) { Message($"Connect {StoreLabel(provider)} on Orbiters first (Creator > Integrations).", true); return; }
            if (products.items.Count == 0) { Message($"Every {StoreLabel(provider)} product is already on Orbiters.", false); return; }
            var menu = new GenericMenu();
            foreach (var product in products.items)
            {
                var chosen = product;
                menu.AddItem(new GUIContent(product.name), false, async () =>
                {
                    try { await GalleryApi.LinkStoreProductAsync(asset.id, chosen); Message($"Linked {chosen.name}.", false); await LoadAsync(); }
                    catch (Exception ex) { Message(ex.Message, true); }
                });
            }
            menu.ShowAsContext();
        }

        // ---- 2. Version ----

        private void VersionStep(VisualElement step)
        {
            var row = GalleryUI.Box("creator-row creator-row--fields", step);
            var version = Field(draft.version, value => draft.version = value);
            version.AddToClassList("creator-field--version");
            row.Add(version);
            var title = Field(draft.title, value => draft.title = value, placeholder: "Title (optional)");
            title.style.marginRight = 0;
            row.Add(title);
            Label("What changed", step);
            step.Add(Field(draft.changelog, value => draft.changelog = value, multiline: true));
            Label("Who gets it", step);
            var scopes = GalleryUI.Box("creator-row", step);
            foreach (string scope in VersionScopes.All)
            {
                string chosen = scope;
                var chip = GalleryUI.Button(VersionScopes.Label(scope), () => { draft.scope = chosen; Build(); }, "creator-chip creator-chip--" + scope);
                chip.EnableInClassList("creator-chip--on", draft.scope == scope);
                scopes.Add(chip);
            }
            Hint(VersionScopes.Audience(draft.scope), step);
            if (draft.scope != VersionScopes.Public && draft.assetId > 0) _ = Testers(step);
            else if (draft.scope != VersionScopes.Public) Hint("Add testers once the asset exists (after publishing a first version, or by choosing it above).", step);
        }

        private async Task Testers(VisualElement step)
        {
            var box = GalleryUI.Box(null, step);
            Label("Testers", box);
            Dictionary<string, List<ScopeUser>> testers;
            try { testers = await GalleryApi.TestersAsync(draft.assetId, CancellationToken.None); }
            catch (Exception ex) { Hint("Could not load testers: " + ex.Message, box); return; }
            List<ScopeUser> Scope(string key) => testers.TryGetValue(key, out var users) && users != null ? users : new List<ScopeUser>();
            // Alpha testers also get beta versions.
            var list = (draft.scope == VersionScopes.Beta ? Scope("beta") : new List<ScopeUser>()).Concat(Scope("alpha")).GroupBy(u => u.id).Select(g => g.First()).ToList();
            foreach (var tester in list)
            {
                var row = GalleryUI.Box("creator-tester", box);
                GalleryUI.Text(tester.username, "creator-tester__name", row);
                var user = tester;
                row.Add(GalleryUI.Button("Remove", async () =>
                {
                    try { await GalleryApi.RemoveTesterAsync(draft.assetId, draft.scope, user.id); Build(); } catch (Exception ex) { Message(ex.Message, true); }
                }, "gallery-link"));
            }
            var search = Field("", null, placeholder: "Add a tester by username");
            box.Add(search);
            search.RegisterCallback<KeyDownEvent>(async evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                var users = await GalleryApi.SearchUsersAsync(search.value, CancellationToken.None);
                var menu = new GenericMenu();
                foreach (var found in users.Take(20))
                {
                    var user = found;
                    menu.AddItem(new GUIContent(user.username), false, async () =>
                    {
                        try { await GalleryApi.AddTesterAsync(draft.assetId, draft.scope, user.id); Build(); } catch (Exception ex) { Message(ex.Message, true); }
                    });
                }
                if (users.Count == 0) menu.AddDisabledItem(new GUIContent("Nobody found"));
                menu.ShowAsContext();
            }, TrickleDown.TrickleDown);
        }

        // ---- helpers ----

        private static string StoreLabel(string provider) =>
            provider == "GUMROAD" ? "Gumroad" : provider == "JINXXY" ? "Jinxxy" : provider == "PAYHIP" ? "Payhip" : provider == "LEMONSQUEEZY" ? "Lemon Squeezy" : provider;

        private static void Label(string text, VisualElement parent) => GalleryUI.Text(text, "creator-label", parent);
        private static void Hint(string text, VisualElement parent) => GalleryUI.Text(text, "creator-hint", parent);

        private TextField Field(string value, Action<string> changed, bool multiline = false, string placeholder = null)
        {
            var field = new TextField { value = value ?? "", multiline = multiline };
            field.AddToClassList("creator-field");
            if (multiline) field.AddToClassList("creator-field--multiline");
            if (!string.IsNullOrEmpty(placeholder))
            {
                var hint = GalleryUI.Text(placeholder, "gallery-search__hint", field);
                hint.style.top = 6; hint.pickingMode = PickingMode.Ignore;
                hint.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.Flex : DisplayStyle.None;
                field.RegisterValueChangedCallback(evt => hint.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None);
            }
            if (changed != null) field.RegisterValueChangedCallback(evt => { changed(evt.newValue); draft.Save(); });
            return field;
        }

        private void Message(string text, bool error)
        {
            draft.message = text; draft.messageError = error;
            var label = this.Q<Label>(className: "creator-message");
            if (label == null) return;
            label.text = text; label.EnableInClassList("error", error); label.style.display = DisplayStyle.Flex;
        }
    }
}
