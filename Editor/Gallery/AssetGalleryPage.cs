using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// The asset gallery inside My Avatar's Inspector: the avatar's base, platform and parameter memory, the license key
    /// bar, search and filters, then the cards. Cards show each asset's state for this avatar (add, buy, installed,
    /// update, installing) and open their details, purchase and questions in sheets over the page.
    /// </summary>
    internal sealed class AssetGalleryPage : VisualElement
    {
        private static readonly string[] Filters = { "All", "Clothing", "Accessories", "On this avatar" };
        private readonly MyAvatar avatar;
        private readonly Action back, publish;
        private readonly VisualElement context, grid, footer, sheets;
        private readonly Label message;
        private readonly TextField search;
        private readonly Dictionary<int, GalleryCard> cards = new Dictionary<int, GalleryCard>();
        private List<GalleryAsset> assets = new List<GalleryAsset>();
        private List<GalleryBase> bases = new List<GalleryBase>();
        private GalleryAvatarContext avatarContext;
        private CancellationTokenSource loading;
        private int filter, total;
        private IVisualElementScheduledItem searchDelay;
        private bool creator;

        internal AssetGalleryPage(MyAvatar avatar, Action back, Action publish)
        {
            this.avatar = avatar; this.back = back; this.publish = publish;
            if (GalleryUI.Sheet) styleSheets.Add(GalleryUI.Sheet);
            var cardSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.toolkit/Editor/UI/gallery-card.uss");
            if (cardSheet) styleSheets.Add(cardSheet);
            AddToClassList("gallery-page");

            var header = GalleryUI.Box("gallery-page__header", this);
            header.Add(GalleryUI.IconButton(IconGlyph.Chevron, "Back to My Avatar", () => back(), "gallery-back", "gallery-back__icon"));
            GalleryUI.Text("Asset Gallery", "gallery-page__title", header);
            var refresh = new Orbiters.Toolkit.Editor.IconButton(IconGlyph.Refresh, "", "Reload the gallery", () => Reload());
            refresh.AddToClassList("orb-icon-button--small"); header.Add(refresh);
            var publishButton = GalleryUI.Button("Publish", () => publish(), "gallery-publish");
            publishButton.tooltip = "Publish your own clothes and accessories in the gallery.";
            publishButton.style.display = DisplayStyle.None;
            header.Add(publishButton);

            context = GalleryUI.Box("gallery-context", this);
            Add(new LicenseKeyButton(() => sheets, () => Reload()));
            var toolbar = GalleryUI.Box("gallery-toolbar", this);
            search = new TextField(); search.AddToClassList("gallery-search");
            var hint = GalleryUI.Text("Search the gallery", "gallery-search__hint", search); hint.pickingMode = PickingMode.Ignore;
            search.RegisterValueChangedCallback(evt =>
            {
                hint.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
                searchDelay?.Pause();
                searchDelay = schedule.Execute(() => Reload()).StartingIn(350);
            });
            toolbar.Add(search);
            var filters = new SegmentedControl(Filters, index => { filter = index; Reload(); });
            filters.AddToClassList("gallery-filters");
            Add(filters);
            message = GalleryUI.Text("", "gallery-sheet__text", this);
            message.style.display = DisplayStyle.None;
            grid = GalleryUI.Box("gallery-grid", this);
            CardGrid.Attach(grid, 150f, 12f, (card, width) =>
            {
                var media = card.Q(className: "orb-card__media");
                if (media != null) media.style.height = width;
            });
            footer = GalleryUI.Box("gallery-footer", this);
            sheets = GalleryUI.Box("gallery-sheet-host", this);
            sheets.pickingMode = PickingMode.Ignore;

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                GalleryJournal.Changed += JobChanged;
                AccessoryService.Changed += AvatarChanged;
                Undo.undoRedoPerformed += RefreshCards;
                AuthenticationService.Changed += Reload;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                GalleryJournal.Changed -= JobChanged;
                AccessoryService.Changed -= AvatarChanged;
                Undo.undoRedoPerformed -= RefreshCards;
                AuthenticationService.Changed -= Reload;
                loading?.Cancel();
            });
            // What the cache already knows shows at once (the entry card warmed it); the rest follows in the background.
            avatarContext = GalleryCache.Known(avatar);
            ShowContext(avatarContext);
            var cached = avatarContext != null ? GalleryCache.Page(GalleryCache.PageKey(avatar, avatarContext, null, null)) : null;
            if (cached != null) { assets = cached.assets ?? new List<GalleryAsset>(); total = cached.total; Build(); }
            else Skeleton();
            schedule.Execute(() => { _ = DetectAsync(publishButton); });
        }

        private async Task DetectAsync(VisualElement publishButton)
        {
            // Independent requests run together.
            var basesTask = GalleryCache.Bases();
            var creatorTask = GalleryCache.IsCreator();
            var contextTask = GalleryCache.Context(avatar);
            bases = await basesTask;
            var detected = await contextTask;
            if (panel == null) return;
            bool changed = avatarContext == null || avatarContext.BaseId != detected?.BaseId || avatarContext.Platform != detected?.Platform;
            avatarContext = detected;
            ShowContext(avatarContext);
            Reload(quiet: !changed && assets.Count > 0);
            creator = await creatorTask;
            if (panel == null) return;
            publishButton.style.display = creator ? DisplayStyle.Flex : DisplayStyle.None;
            if (creator && !grid.Children().Any(c => c.ClassListContains("gallery-create")) && filter != 3) Build();
        }

        // ---- Avatar context: base, platform, parameter memory ----

        private void ShowContext(GalleryAvatarContext detected)
        {
            context.Clear();
            if (detected == null) { Pill("Avatar base", "detecting…", null, false); return; }
            var basePill = Pill("Made for", detected.BaseLabel + " ▾", ChooseBase, !detected.BaseId.HasValue);
            basePill.tooltip = detected.BaseId.HasValue
                ? $"Recognised {(detected.BaseSource == "model" ? "from its body model" : detected.BaseSource == "tool" ? "from its custom base" : detected.BaseSource == "chosen" ? "by your choice" : "from the project")}. Click to choose another base."
                : "My Avatar could not recognise this avatar's base: choose it to see what fits.";
            Pill("Platform", AvatarPlatform.Label(detected.Platform), null, false).tooltip = "The platform the project builds for (File > Build Settings).";
            if (detected.HasDescriptor)
            {
                var budget = detected.Budget;
                Pill("Parameters", budget.Free + " bits free", null, budget.Free < 16).tooltip = $"{budget.TotalBeforeCompression} of {ParameterLimit} synced bits used before VRCFury compression.";
            }
        }

        private const int ParameterLimit = 256;

        private VisualElement Pill(string label, string value, Action action, bool warning)
        {
            var pill = GalleryUI.Box("gallery-pill", context);
            GalleryUI.Box("gallery-pill__dot", pill);
            GalleryUI.Text(label, "gallery-pill__label", pill);
            GalleryUI.Text(value, "gallery-pill__value", pill);
            pill.EnableInClassList("gallery-pill--warning", warning);
            if (action != null) { pill.AddToClassList("gallery-pill--action"); GalleryUI.Clickable(pill, action, "gallery-pill--pressed"); }
            return pill;
        }

        private void ChooseBase()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Recognise automatically"), EditorPrefs.GetInt(GalleryAvatarContext.Key(avatar), 0) == 0, () => { GalleryAvatarContext.Choose(avatar, 0); Redetect(); });
            menu.AddSeparator("");
            foreach (var item in bases.OrderBy(b => b.name))
            {
                var chosen = item;
                menu.AddItem(new GUIContent(item.name), avatarContext?.BaseId == item.id, () => { GalleryAvatarContext.Choose(avatar, chosen.id); Redetect(); });
            }
            menu.ShowAsContext();
        }

        private async void Redetect()
        {
            avatarContext = await GalleryCache.Context(avatar, refresh: true);
            ShowContext(avatarContext);
            Reload();
        }

        // ---- Cards ----

        internal void Reload() => Reload(false);

        /// <param name="quiet">The cards shown are current enough: refresh them without placeholders.</param>
        private void Reload(bool quiet)
        {
            if (avatarContext == null) return;
            loading?.Cancel();
            var source = loading = new CancellationTokenSource();
            _ = LoadAsync(source, quiet);
        }

        private string Type => filter == 1 ? "CLOTHING" : filter == 2 ? "ACCESSORY" : null;

        private async Task LoadAsync(CancellationTokenSource source, bool quiet)
        {
            string key = GalleryCache.PageKey(avatar, avatarContext, Type, search.value);
            // A filter or search seen a moment ago shows at once; the answer below refreshes it.
            var cached = GalleryCache.Page(key);
            if (cached != null && !quiet) { assets = cached.assets ?? new List<GalleryAsset>(); total = cached.total; Build(); quiet = true; }
            else if (!quiet && assets.Count == 0) Skeleton();
            try
            {
                var page = await GalleryApi.ListAsync(avatarContext.Platform, avatarContext.BaseId, Type, search.value, false, 0, source.Token);
                if (source.IsCancellationRequested) return;
                GalleryCache.Store(key, page);
                var fresh = page?.assets ?? new List<GalleryAsset>();
                total = page?.total ?? fresh.Count;
                ShowMessage(null);
                // Same cards: updated in place, without rebuilding the grid.
                if (quiet && fresh.Select(a => a.id).SequenceEqual(assets.Select(a => a.id)) && fresh.All(a => cards.ContainsKey(a.id)))
                {
                    assets = fresh;
                    foreach (var asset in fresh) { var card = cards[asset.id]; card.Bind(asset, card.Installed, card.Job); Bind(card); }
                    return;
                }
                assets = fresh;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (source.IsCancellationRequested) return;
                assets = new List<GalleryAsset>();
                ShowMessage(string.IsNullOrEmpty(AuthenticationService.GetAuth()?.token)
                    ? "Sign in to Orbiters at the top of My Avatar to see what your account can add." : "The gallery could not be loaded: " + ex.Message);
            }
            Build();
        }

        private void Build()
        {
            grid.Clear();
            cards.Clear();
            var installed = GalleryInstaller.Installed(avatar);
            IEnumerable<GalleryAsset> shown = assets;
            if (filter == 3) shown = shown.Where(a => installed.Any(i => i.receipt.assetId == a.id) || GalleryJournal.For(GalleryInstaller.AvatarId(avatar), a.id) != null);
            foreach (var asset in shown)
            {
                var card = new GalleryCard(asset, OpenDetails, Act);
                cards[asset.id] = card;
                Bind(card);
                grid.Add(card);
            }
            if (creator && filter != 3) grid.Add(CreateCard());
            CardGrid.Resize(grid, 150f, 12f, (card, width) => { var media = card.Q(className: "orb-card__media"); if (media != null) media.style.height = width; });
            Empty(shown.Any());
            footer.Clear();
            var cleanup = GalleryUI.Button("Cleanup unused gallery files…", GalleryCleanupWindow.Open, "gallery-link");
            footer.Add(cleanup);
            if (total > assets.Count) GalleryUI.Text($"Showing {assets.Count} of {total}: search to narrow it down.", "gallery-card__meta", footer);
        }

        private VisualElement CreateCard()
        {
            var card = GalleryUI.Box("orb-card gallery-card gallery-create");
            var media = GalleryUI.Box("orb-card__media orb-card__media--create", card);
            media.style.backgroundColor = new Color(0, 0, 0, 0);
            GalleryUI.Text("+", "gallery-create__plus", media);
            var body = GalleryUI.Box("gallery-card__body", card);
            GalleryUI.Text("Publish an asset", "gallery-create__label", body);
            GalleryUI.Text("Share your clothes and accessories here", "gallery-create__hint", body);
            GalleryUI.Clickable(card, () => publish(), "gallery-hero--pressed");
            return card;
        }

        private void Empty(bool any)
        {
            var old = this.Q(className: "gallery-empty");
            old?.RemoveFromHierarchy();
            if (any || message.style.display == DisplayStyle.Flex) return;
            var empty = GalleryUI.Box("gallery-empty");
            Insert(IndexOf(grid), empty);
            bool searching = !string.IsNullOrWhiteSpace(search.value);
            GalleryUI.Text(filter == 3 ? "Nothing from the gallery is on this avatar yet" : searching ? "Nothing matches your search" : "The gallery is empty for now", "gallery-empty__title", empty);
            GalleryUI.Text(filter == 3 ? "Add something from All to see it here." : searching ? "Try another word, or show every type." : "Creators publish their clothes and accessories here; check back soon.", "gallery-empty__text", empty);
            if (searching) empty.Add(GalleryUI.Button("Clear the search", () => search.value = ""));
        }

        // Placeholder cards while the gallery loads: the same shape as the real ones, a soft sweep over them.
        private void Skeleton()
        {
            grid.Clear();
            for (int i = 0; i < 3; i++)
            {
                var card = GalleryUI.Box("orb-card gallery-card gallery-card--skeleton", grid);
                var media = GalleryUI.Box("orb-card__media", card);
                GalleryUI.Shimmer(media);
                var body = GalleryUI.Box("gallery-card__body", card);
                GalleryUI.Box("gallery-skeleton__bar", body).style.width = Length.Percent(70);
                GalleryUI.Box("gallery-skeleton__bar gallery-skeleton__bar--small", body).style.width = Length.Percent(45);
                GalleryUI.Box("gallery-skeleton__button", body);
            }
            CardGrid.Resize(grid, 150f, 12f, (card, width) => { var media = card.Q(className: "orb-card__media"); if (media != null) media.style.height = width; });
        }

        private void ShowMessage(string text)
        {
            message.text = text ?? "";
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void Bind(GalleryCard card)
        {
            var receipt = GalleryInstaller.Installed(avatar).Select(i => i.receipt).FirstOrDefault(r => r.assetId == card.Asset.id);
            card.Bind(card.Asset, receipt, GalleryJournal.For(GalleryInstaller.AvatarId(avatar), card.Asset.id));
        }

        private void RefreshCards() { if (avatar) foreach (var card in cards.Values) Bind(card); }

        private void JobChanged(GalleryJob job)
        {
            if (!avatar || job.avatar != GalleryInstaller.AvatarId(avatar) || !cards.TryGetValue(job.assetId, out var card)) return;
            Bind(card);
            // A question opens on its own the first time it is asked.
            if (job.Waiting && !asked.Contains(job.id + job.waiting)) { asked.Add(job.id + job.waiting); OpenQuestion(card, job); }
            if (job.stage == GalleryJob.Complete) ShowContext(avatarContext = Refreshed(avatarContext));
        }
        private readonly HashSet<string> asked = new HashSet<string>();

        private GalleryAvatarContext Refreshed(GalleryAvatarContext detected)
        {
            if (detected != null && avatar) detected.Budget = Orbiters.Toolkit.Editor.VRChat.Parameters.AvatarParameterBudget.Estimate(avatar.gameObject);
            return detected;
        }

        private void AvatarChanged(MyAvatar changed) { if (changed == avatar) RefreshCards(); }

        // ---- Actions ----

        private void Act(GalleryCard card)
        {
            var asset = card.Asset;
            switch (card.State)
            {
                case GalleryUI.State.Add: Add(card, asset.fit.variants[0].manifest?.defaultSetup); break;
                case GalleryUI.State.Buy: OpenBuy(card); break;
                case GalleryUI.State.Update: Update(card); break;
                case GalleryUI.State.Installed:
                    GalleryInstaller.Remove(avatar, card.Installed.installId);
                    card.Bind(asset, null, null);
                    ShowMessage($"{asset.name} was taken off {avatar.name} (Undo brings it back). Its files stay in the project: Cleanup removes them once nothing uses them.");
                    break;
                case GalleryUI.State.Question: OpenQuestion(card, card.Job); break;
                case GalleryUI.State.Failed: GalleryInstaller.Retry(card.Job); break;
                case GalleryUI.State.Working: GalleryInstaller.Cancel(card.Job); break;
            }
        }

        private void Add(GalleryCard card, string setup)
        {
            var asset = card.Asset;
            if (!avatar || AccessoryService.Busy(avatar)) { ShowMessage("My Avatar is still busy with this avatar. Try again in a moment."); return; }
            var variant = asset.fit.variants[0];
            // Optimistic: the card shows its progress at once, before the job's first step reports.
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, variant, setup);
            card.Bind(asset, card.Installed, job);
        }

        private void Update(GalleryCard card)
        {
            var asset = card.Asset;
            var variant = asset.fit.variants.FirstOrDefault(v => v.manifest?.setups?.Any(s => s.key == card.Installed.setup) == true) ?? asset.fit.variants[0];
            var job = GalleryInstaller.Start(avatar, asset, asset.fit.release, variant, card.Installed.setup, card.Installed.installId);
            card.Bind(asset, card.Installed, job);
        }

        private void OpenQuestion(GalleryCard card, GalleryJob job)
        {
            if (job == null || !job.Waiting) return;
            GallerySheet sheet = null;
            sheet = GallerySheet.Open(sheets, card, 420f);
            GallerySheets.Question(sheet.Content, job, accept => { sheet.Close(); GalleryInstaller.Answer(job, accept); });
        }

        private void OpenBuy(GalleryCard card)
        {
            var sheet = GallerySheet.Open(sheets, card, 400f);
            GallerySheets.Buy(sheet.Content, card.Asset, () => { Reload(); });
        }

        private async void OpenDetails(GalleryCard card)
        {
            var sheet = GallerySheet.Open(sheets, card, 460f);
            var spinner = GalleryUI.Spinner("buy-wait__spinner");
            sheet.Content.Add(spinner);
            GalleryAsset detail;
            try { detail = await GalleryApi.DetailAsync(card.Asset.id, avatarContext?.Platform, avatarContext?.BaseId, CancellationToken.None) ?? card.Asset; }
            catch (Exception) { detail = card.Asset; }
            // Base names the detail did not bring come from the registry the page already has.
            if (detail.bases == null || detail.bases.Count == 0) detail.bases = bases;
            sheet.Content.Clear();
            GallerySheets.Details(sheet.Content, detail, card, avatar, setup => { sheet.Close(); Add(card, setup); }, c => { sheet.Close(); Act(c); },
                () => { sheet.Close(); Reload(true); });
        }
    }
}
