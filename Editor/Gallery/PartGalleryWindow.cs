using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// A placed item's part and its variants in a window of their own (like the publishing window): the part's gallery
    /// (the creator's variants first, marked "By the creator", then everyone's, with search, a language filter for texts,
    /// popular or new first), a click applies one to the item; <b>+</b> opens the form to make one (<see cref="PartVariantForm"/>).
    /// </summary>
    internal sealed class PartGalleryWindow : EditorWindow
    {
        [SerializeField] private MyAvatar avatar;
        [SerializeField] private Transform item;
        [SerializeField] private string part;
        [SerializeField] private bool form;

        // The part last asked for: a window made without one (Unity restoring it, a hidden copy for a capture) shows it.
        private static (MyAvatar avatar, Transform item, string part, bool form) last;

        /// <summary>The part a window made without one shows (its gallery, or its + form).</summary>
        internal static void Remember(MyAvatar avatar, Transform item, string part, bool form) => last = (avatar, item, part, form);

        internal static PartGalleryWindow Open(MyAvatar avatar, Transform item, string part)
        {
            last = (avatar, item, part, false);
            var window = Resources.FindObjectsOfTypeAll<PartGalleryWindow>().FirstOrDefault();
            if (window == null)
            {
                window = CreateInstance<PartGalleryWindow>();
                window.minSize = new Vector2(420, 460);
                window.position = new Rect(180, 120, 620, 780);
            }
            window.avatar = avatar; window.item = item; window.part = part; window.form = false;
            window.Rebuild();
            window.Show();
            window.Focus();
            return window;
        }

        private void CreateGUI() => Rebuild();

        internal void ShowForm(bool show) { form = show; last.form = show; Rebuild(); }

        private void Rebuild()
        {
            if (rootVisualElement == null) return;
            rootVisualElement.Clear();
            if (item == null && last.item != null) { avatar = last.avatar; item = last.item; part = last.part; form = last.form; }
            foreach (string path in new[] { "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss", "Packages/orbiters.myavatar/Editor/UI/myavatar.uss",
                         "Packages/orbiters.toolkit/Editor/UI/gallery-card.uss", GalleryUI.StyleSheetPath, PartGalleryPage.StyleSheetPath })
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet && !rootVisualElement.styleSheets.Contains(sheet)) rootVisualElement.styleSheets.Add(sheet);
            }
            rootVisualElement.AddToClassList("gallery-creator-window");
            var placed = item != null ? ItemParts.Of(item.gameObject).FirstOrDefault(p => p.Item == item && p.Part.key == part) : null;
            titleContent = new GUIContent("Customize" + (placed != null ? " · " + placed.Part.Label : ""));
            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList("gallery-creator-window__scroll");
            rootVisualElement.Add(scroll);
            if (placed == null)
            {
                var gone = new VisualElement(); gone.AddToClassList("gallery-page"); scroll.Add(gone);
                GalleryUI.Text("This item is not on the avatar any more, or it has no such part.", "creator-message", gone);
                return;
            }
            scroll.Add(form ? (VisualElement)new PartVariantForm(placed, () => ShowForm(false)) : new PartGalleryPage(placed, () => ShowForm(true)));
        }
    }

    /// <summary>A part's gallery of variants: every card applies its variant to the item on a click.</summary>
    internal sealed class PartGalleryPage : VisualElement
    {
        internal const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/Gallery/part-variants.uss";
        private readonly ItemParts.Placed placed;
        private readonly VisualElement grid, footer;
        private readonly Label message;
        private readonly TextField search;
        private readonly List<PartVariant> bases;
        private readonly List<VariantInfo> shown = new List<VariantInfo>();
        private readonly List<PartVariantCard> cards = new List<PartVariantCard>();
        private CancellationTokenSource loading;
        private IVisualElementScheduledItem searchDelay;
        private string language, cursor;
        private bool newest;

        internal PartGalleryPage(ItemParts.Placed placed, Action make)
        {
            this.placed = placed;
            AddToClassList("gallery-page");
            AddToClassList("parts-gallery");
            bases = ItemParts.Bases(placed.PartsPath, placed.Part);
            var header = GalleryUI.Box("gallery-page__header", this);
            var titles = GalleryUI.Box("parts-gallery__titles", header);
            GalleryUI.Text(placed.Part.Label, "gallery-page__title", titles);
            GalleryUI.Text(placed.Item.name, "parts-gallery__item", titles);
            var plus = GalleryUI.Button("+  Make your own", make, "parts-gallery__make");
            plus.tooltip = "Make a variant of " + placed.Part.Label.ToLowerInvariant() + " (only the kinds of files the creator allows), see it on the item, then post it.";
            header.Add(plus);
            if (!string.IsNullOrWhiteSpace(placed.Part.description)) GalleryUI.Text(placed.Part.description, "creator-intro", this);

            var toolbar = GalleryUI.Box("gallery-toolbar", this);
            search = new TextField(); search.AddToClassList("gallery-search");
            var hint = GalleryUI.Text("Search variants or their authors", "gallery-search__hint", search); hint.pickingMode = PickingMode.Ignore;
            search.RegisterValueChangedCallback(evt =>
            {
                hint.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
                searchDelay?.Pause();
                searchDelay = schedule.Execute(() => Reload()).StartingIn(350);
            });
            toolbar.Add(search);
            // Under the search: popular or new first, and the language of texts.
            var filters = GalleryUI.Box("parts-gallery__filters", this);
            var sort = new SegmentedControl(new[] { "Popular", "New" }, index => { newest = index == 1; Reload(); });
            sort.AddToClassList("parts-gallery__sort");
            sort.SetIndex(0);
            filters.Add(sort);
            if (placed.Part.kind == ItemParts.Strings)
            {
                var languages = PartVariants.Languages(placed);
                var choices = new List<string> { "All languages" }.Concat(languages.Select(l => PartVariants.LanguageName(l) + " (" + l + ")")).ToList();
                var filter = new DropdownField(choices, 0); filter.AddToClassList("parts-gallery__language");
                // The pill of the gallery's search (the popup's own style would win over a class).
                var input = filter.Q(className: "unity-base-field__input");
                if (input != null)
                {
                    input.style.backgroundColor = new Color(31 / 255f, 31 / 255f, 31 / 255f);
                    input.style.borderTopWidth = input.style.borderBottomWidth = input.style.borderLeftWidth = input.style.borderRightWidth = 0;
                    input.style.borderTopLeftRadius = input.style.borderTopRightRadius = input.style.borderBottomLeftRadius = input.style.borderBottomRightRadius = 14;
                    input.style.minHeight = 28; input.style.paddingLeft = 12; input.style.paddingRight = 10;
                }
                filter.RegisterValueChangedCallback(evt => { int index = choices.IndexOf(evt.newValue); language = index > 0 ? languages[index - 1] : null; Reload(); });
                filters.Add(filter);
            }
            message = GalleryUI.Text("", "creator-hint parts-gallery__message", this);
            message.style.display = DisplayStyle.None;
            grid = GalleryUI.Box("gallery-grid", this);
            CardGrid.Attach(grid, 150f, 12f, (card, width) =>
            {
                var media = card.Q(className: "orb-card__media");
                if (media != null) media.style.height = width;
            });
            footer = GalleryUI.Box("gallery-footer", this);
            RegisterCallback<AttachToPanelEvent>(_ => { PartVariants.Changed += JobChanged; Undo.undoRedoPerformed += Refresh; ItemParts.Changed += ItemChanged; });
            RegisterCallback<DetachFromPanelEvent>(_ => { PartVariants.Changed -= JobChanged; Undo.undoRedoPerformed -= Refresh; ItemParts.Changed -= ItemChanged; loading?.Cancel(); });
            Build();
            schedule.Execute(() => Reload());
        }

        private void JobChanged(PartVariants.Job job)
        {
            if (placed.Item == null || job.Item != placed.Item.GetInstanceID() || job.Part != placed.Part.key) return;
            if (!job.Running) placed.Current = ItemParts.Current(placed.Item, placed.Definition, placed.Part);
            foreach (var card in cards) card.Refresh(placed.Current, job);
        }

        private void ItemChanged(Transform changed) { if (changed == placed.Item) Refresh(); }

        private void Refresh()
        {
            if (placed.Item == null) return;
            placed.Current = ItemParts.Current(placed.Item, placed.Definition, placed.Part);
            var job = PartVariants.JobOf(placed.Item, placed.Part.key);
            foreach (var card in cards) card.Refresh(placed.Current, job);
        }

        // The creator's variants in the asset (filtered here), then the gallery's (filtered by Orbiters).
        private void Build()
        {
            grid.Clear(); cards.Clear();
            string query = search.value?.Trim() ?? "";
            var job = PartVariants.JobOf(placed.Item, placed.Part.key);
            foreach (var variant in bases)
            {
                if (query.Length > 0 && variant.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (language != null && !LanguageMatches(variant.language)) continue;
                // Its post as the creator's official variant has the picture Orbiters shows.
                var posted = shown.FirstOrDefault(v => v.official && string.Equals(v.name, variant.name, StringComparison.OrdinalIgnoreCase));
                var card = new PartVariantCard(placed, variant, null, () => PartVariants.Apply(placed, variant), posted?.thumbnail);
                cards.Add(card); grid.Add(card);
                card.Refresh(placed.Current, job);
            }
            // The creator's own posts of the variants above are the same files: shown once.
            foreach (var info in shown.Where(v => !(v.official && bases.Any(b => string.Equals(b.name, v.name, StringComparison.OrdinalIgnoreCase)))))
            {
                var chosen = info;
                var card = new PartVariantCard(placed, null, info, () => PartVariants.Apply(placed, chosen));
                cards.Add(card); grid.Add(card);
                card.Refresh(placed.Current, job);
            }
            CardGrid.Resize(grid, 150f, 12f, (card, width) =>
            {
                var media = card.Q(className: "orb-card__media");
                if (media != null) media.style.height = width;
            });
        }

        private bool LanguageMatches(string code) => code != null && (code == language || code.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));

        private void Reload(bool more = false)
        {
            loading?.Cancel();
            if (!more) { cursor = null; shown.Clear(); }
            footer.Clear();
            if (placed.AssetId <= 0)
            {
                Note("Only items from the asset gallery share variants with everyone: these are the creator's. You can still make your own and see it here.");
                Build();
                return;
            }
            if (string.IsNullOrEmpty(GalleryApi.Token))
            {
                Note("Sign in to Orbiters (My Avatar's account button) to see everyone's variants.");
                Build();
                return;
            }
            var cancel = loading = new CancellationTokenSource();
            footer.Add(GalleryUI.Spinner("buy-wait__spinner"));
            _ = LoadAsync(cancel);
        }

        private async Task LoadAsync(CancellationTokenSource cancel)
        {
            try
            {
                var page = await PartVariantApi.VariantsAsync(placed.AssetId, placed.Part.key, search.value, language, newest, cursor, cancel.Token);
                if (cancel.IsCancellationRequested || panel == null) return;
                shown.AddRange(page?.variants ?? new List<VariantInfo>());
                cursor = page?.nextCursor;
                Note(shown.Count == 0 && bases.Count == 0 ? "No variants yet: make the first one with +." : null);
                Build();
                footer.Clear();
                if (!string.IsNullOrEmpty(cursor)) footer.Add(GalleryUI.Button("Load more", () => Reload(more: true), "gallery-more"));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (cancel.IsCancellationRequested || panel == null) return;
                footer.Clear();
                bool none = e.Message.Contains("has no part");
                Note(none ? "This version of the asset on Orbiters has no gallery for " + placed.Part.Label.ToLowerInvariant() + " yet: the creator's variants are below."
                    : "Everyone's variants could not be loaded: " + e.Message);
                Build();
                if (!none) footer.Add(GalleryUI.Button("Try again", () => Reload(), "gallery-more"));
            }
        }

        private void Note(string text)
        {
            message.text = text ?? "";
            message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }

    /// <summary>One variant of a part: its picture, "By the creator" or its author, its language; a click applies it.</summary>
    internal sealed class PartVariantCard : VisualElement
    {
        private readonly ItemParts.Placed placed;
        private readonly PartVariant local;
        private readonly VariantInfo info;
        private readonly Label inUse, meta, status;
        private readonly VisualElement progress, fill;

        internal PartVariantCard(ItemParts.Placed placed, PartVariant local, VariantInfo info, Action apply, string postedPicture = null)
        {
            this.placed = placed; this.local = local; this.info = info;
            AddToClassList("orb-card");
            AddToClassList("gallery-card");
            AddToClassList("parts-card");
            var media = GalleryUI.Box("orb-card__media", this);
            var image = new Image { scaleMode = ScaleMode.ScaleAndCrop, pickingMode = PickingMode.Ignore };
            image.AddToClassList("gallery-card__image");
            media.Add(image);
            var chips = GalleryUI.Box("gallery-card__chips", media);
            bool official = local != null || info.official;
            var creator = GalleryUI.Text(official ? "By the creator" : "", "gallery-chip gallery-chip--owned", chips);
            creator.style.visibility = official ? Visibility.Visible : Visibility.Hidden;
            inUse = GalleryUI.Text("In use", "gallery-chip gallery-chip--muted", chips);
            var body = GalleryUI.Box("gallery-card__body", this);
            GalleryUI.Text(local != null ? local.name : info.name, "gallery-card__title", body);
            var author = GalleryUI.Box("gallery-creator", body);
            var face = new Image { scaleMode = ScaleMode.ScaleAndCrop }; face.AddToClassList("gallery-creator__avatar"); author.Add(face);
            GalleryUI.Text(local != null ? "In the asset" : info.author?.username ?? "Unknown author", "gallery-creator__name", author);
            if (local != null) face.style.display = DisplayStyle.None;
            else if (!string.IsNullOrEmpty(info.author?.avatarUrl)) GalleryImages.ShowAvatar(face, info.author.avatarUrl);
            string language = local != null ? local.language : info.language;
            meta = GalleryUI.Text(string.Join(" · ", new[]
            {
                string.IsNullOrEmpty(language) ? null : PartVariants.LanguageName(language),
                info != null ? info.downloads + (info.downloads == 1 ? " download" : " downloads") : null,
                info != null && info.sizeBytes > 0 ? GalleryUI.Size(info.sizeBytes) : null,
            }.WithoutNulls()), "gallery-card__meta", body);
            progress = GalleryUI.Box("gallery-progress parts-card__progress", body);
            fill = GalleryUI.Box("gallery-progress__fill", progress);
            status = GalleryUI.Text("", "gallery-card__status", body);
            tooltip = (local != null ? local.description : info.description) is string description && description.Length > 0 ? description : null;

            if (local != null && local.Thumbnail == null && !string.IsNullOrEmpty(postedPicture)) GalleryImages.Show(image, postedPicture);
            else if (local != null)
            {
                void Picture() { image.image = PartVariants.BaseThumbnail(placed, local, () => schedule.Execute(Picture)); image.EnableInClassList(GalleryImages.ShownClass, image.image != null); }
                Picture();
            }
            else if (!string.IsNullOrEmpty(info.thumbnail)) GalleryImages.Show(image, info.thumbnail);
            // Pressed at once; applied on release over it.
            GalleryUI.Clickable(this, () =>
            {
                progress.style.display = DisplayStyle.Flex;
                fill.style.width = Length.Percent(5);
                status.text = "";
                apply();
            }, "parts-card--pressed");
            if (info != null && info.canRemove)
                this.AddManipulator(new ContextualMenuManipulator(evt => evt.menu.AppendAction(info.mine ? "Withdraw it from the gallery" : "Remove it from the gallery", action => { var removing = RemoveAsync(); })));
        }

        private bool IsCurrent(ItemParts.Applied current) => current != null &&
            (local != null ? current.IsBase && current.baseName == local.BaseName : !current.IsBase && current.id == info.id);

        private bool IsJob(PartVariants.Job job) => job != null && (local != null ? job.BaseName == local.BaseName && job.VariantId == 0 : job.VariantId == info.id);

        internal void Refresh(ItemParts.Applied current, PartVariants.Job job)
        {
            bool mine = IsJob(job);
            bool working = mine && job.Running;
            inUse.style.visibility = IsCurrent(current) && !working ? Visibility.Visible : Visibility.Hidden;
            EnableInClassList("parts-card--current", IsCurrent(current));
            progress.style.display = working ? DisplayStyle.Flex : DisplayStyle.None;
            if (working) fill.style.width = Length.Percent(Mathf.Clamp01(job.Progress) * 100);
            status.text = mine ? (working ? job.Status : job.Failed ? job.Error : job.Status) : "";
            status.style.display = string.IsNullOrEmpty(status.text) ? DisplayStyle.None : DisplayStyle.Flex;
            status.EnableInClassList("error", mine && job.Failed);
        }

        private async Task RemoveAsync()
        {
            try
            {
                await PartVariantApi.DeleteAsync(info.id);
                RemoveFromHierarchy();
            }
            catch (Exception e)
            {
                status.text = "It could not be taken down: " + e.Message;
                status.style.display = DisplayStyle.Flex;
                status.AddToClassList("error");
            }
        }
    }
}
