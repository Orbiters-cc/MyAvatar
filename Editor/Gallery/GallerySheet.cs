using System;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// A window inside the gallery page that grows out of the element it was opened from (a card, a button) to the middle
    /// of the visible part of the Inspector, over a dimmed page, and collapses back into it when closed (Escape, the close
    /// button or a click outside). Its content scrolls when taller than the Inspector.
    /// </summary>
    internal sealed class GallerySheet : VisualElement
    {
        private readonly VisualElement host, scrim, origin;
        private readonly Action closed;
        private bool closing;

        internal VisualElement Content { get; }

        private GallerySheet(VisualElement host, VisualElement origin, Action closed)
        {
            this.host = host; this.origin = origin; this.closed = closed;
            AddToClassList("gallery-sheet");
            scrim = new GalleryScrim();
            scrim.AddToClassList("gallery-scrim");
            scrim.RegisterCallback<PointerDownEvent>(evt => { if (evt.button == 0) Close(); });
            var scroll = new ScrollView(ScrollViewMode.Vertical); scroll.AddToClassList("gallery-sheet__scroll"); Add(scroll);
            Content = GalleryUI.Box("gallery-sheet__content");
            scroll.Add(Content);
            Add(GalleryUI.IconButton(IconGlyph.Close, "Close", Close, "gallery-sheet__close", "gallery-sheet__close-icon"));
            focusable = true;
            RegisterCallback<KeyDownEvent>(evt => { if (evt.keyCode == KeyCode.Escape) { Close(); evt.StopPropagation(); } });
        }

        /// <summary>Opens a sheet over <paramref name="host"/> from <paramref name="origin"/>; fill its <see cref="Content"/>.</summary>
        internal static GallerySheet Open(VisualElement host, VisualElement origin, float width, Action closed = null)
        {
            foreach (var open in host.Children().OfType<GallerySheet>().ToList()) open.Close();
            var sheet = new GallerySheet(host, origin, closed);
            host.Add(sheet.scrim);
            host.Add(sheet);
            host.pickingMode = PickingMode.Position;
            var start = sheet.Local(origin);
            sheet.Place(start);
            // Next frame: grow to the middle of what the Inspector shows.
            sheet.schedule.Execute(() =>
            {
                var view = sheet.Visible();
                float w = Mathf.Min(width, view.width - 24f);
                sheet.maxHeight = Mathf.Clamp(view.height - 40f, 220f, 680f);
                sheet.target = new Rect(view.x + (view.width - w) / 2f, view.y + 20f, w, sheet.Fitted());
                sheet.Place(sheet.target);
                sheet.AddToClassList("gallery-sheet--open");
                sheet.scrim.AddToClassList("gallery-scrim--shown");
                sheet.Focus();
            });
            // As tall as its content (up to the visible part of the Inspector), also when the content changes.
            sheet.Content.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (sheet.closing || sheet.maxHeight <= 0f) return;
                sheet.target.height = sheet.Fitted();
                sheet.Place(sheet.target);
            });
            return sheet;
        }

        internal void Close()
        {
            if (closing) return;
            closing = true;
            AddToClassList("gallery-sheet--closing");
            RemoveFromClassList("gallery-sheet--open");
            scrim.RemoveFromClassList("gallery-scrim--shown");
            Place(origin != null && origin.panel != null ? Local(origin) : new Rect(layout.center, Vector2.one * 40f));
            schedule.Execute(() =>
            {
                scrim.RemoveFromHierarchy();
                RemoveFromHierarchy();
                if (!host.Children().OfType<GallerySheet>().Any()) host.pickingMode = PickingMode.Ignore;
                closed?.Invoke();
            }).StartingIn(210);
        }

        private float maxHeight;
        private Rect target;

        private float Fitted() => Mathf.Min(maxHeight, Mathf.Max(120f, Content.layout.height + 2f));

        private void Place(Rect rect)
        {
            style.left = rect.x; style.top = rect.y; style.width = rect.width; style.height = rect.height;
        }

        private Rect Local(VisualElement element)
        {
            if (element == null || element.panel == null) return new Rect(host.layout.width / 2f - 20f, 40f, 40f, 40f);
            var world = element.worldBound;
            var min = host.WorldToLocal(world.min);
            return new Rect(min, world.size);
        }

        // The part of the page the Inspector shows right now, in the host's coordinates.
        private Rect Visible()
        {
            var bounds = host.layout;
            var scroll = host.GetFirstAncestorOfType<ScrollView>();
            if (scroll == null) return new Rect(0, 0, bounds.width, Mathf.Min(bounds.height, 720f));
            var viewport = scroll.contentViewport.worldBound;
            var min = host.WorldToLocal(viewport.min);
            var visible = Rect.MinMaxRect(Mathf.Max(0f, min.x), Mathf.Max(0f, min.y), Mathf.Min(bounds.width, min.x + viewport.width), Mathf.Min(bounds.height, min.y + viewport.height));
            return visible.height < 200f ? new Rect(0, 0, bounds.width, Mathf.Min(bounds.height, 720f)) : visible;
        }
    }

    /// <summary>
    /// The dimmed page behind a sheet: solid black at 60 %, fading in over its top <see cref="Fade"/> pixels so it melts
    /// into the tool's header instead of ending on a hard line. UI Toolkit has no gradients: two quads with vertex colours.
    /// </summary>
    internal sealed class GalleryScrim : VisualElement
    {
        internal const float Fade = 80f;
        private static readonly Color32 Solid = new Color32(0, 0, 0, 153), Clear = new Color32(0, 0, 0, 0);

        internal GalleryScrim() { generateVisualContent += Draw; }

        private void Draw(MeshGenerationContext context)
        {
            var r = contentRect;
            if (r.width <= 0f || r.height <= 0f) return;
            float edge = r.yMin + Mathf.Min(Fade, r.height);
            Vertex V(float x, float y, Color32 color) => new Vertex { position = new Vector3(x, y, Vertex.nearZ), tint = color };
            var mesh = context.Allocate(8, 12);
            mesh.SetAllVertices(new[]
            {
                V(r.xMin, r.yMin, Clear), V(r.xMax, r.yMin, Clear), V(r.xMax, edge, Solid), V(r.xMin, edge, Solid),
                V(r.xMin, edge, Solid), V(r.xMax, edge, Solid), V(r.xMax, r.yMax, Solid), V(r.xMin, r.yMax, Solid),
            });
            mesh.SetAllIndices(new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 });
        }
    }
}
