using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// The like (thumb up), edit and delete marks of reviews, drawn like MCB's custom base likes and comments. Toolkit's
    /// VectorIcon has none of them. The colour comes from the USS custom property <c>--icon-color</c>.
    /// </summary>
    internal sealed class GalleryReviewIcon : VisualElement
    {
        internal enum Kind { Like, Edit, Delete }

        private static readonly CustomStyleProperty<Color> ColorProperty = new CustomStyleProperty<Color>("--icon-color");
        private readonly Kind kind;
        private Color color = new Color(0.85f, 0.85f, 0.85f);

        private static readonly Vector2[][] LikeShapes =
        {
            new[] { new Vector2(2, 16), new Vector2(7, 16), new Vector2(7, 30), new Vector2(2, 30) },
            new[] { new Vector2(9, 30), new Vector2(23, 30), new Vector2(26.2f, 29.2f), new Vector2(28.7f, 26.7f), new Vector2(30, 23), new Vector2(30, 16),
                new Vector2(28.8f, 13.2f), new Vector2(26, 12), new Vector2(18, 12), new Vector2(18, 6), new Vector2(17.1f, 3.9f), new Vector2(15, 3),
                new Vector2(14.1f, 3.2f), new Vector2(12.9f, 4.7f), new Vector2(12, 10.6f), new Vector2(9, 15.2f) },
        };
        private static readonly Vector2[][] EditShapes =
        {
            new[] { new Vector2(25.25f, 4.16f), new Vector2(28.08f, 6.99f), new Vector2(29.12f, 9.31f), new Vector2(28.08f, 11.63f), new Vector2(25.68f, 15.05f),
                new Vector2(17.19f, 6.56f), new Vector2(19.6f, 4.16f), new Vector2(21.93f, 3.12f) },
            new[] { new Vector2(15.77f, 7.97f), new Vector2(4.95f, 18.81f), new Vector2(3.8f, 21.21f), new Vector2(3.27f, 26.04f), new Vector2(4.2f, 28.05f),
                new Vector2(6.21f, 28.97f), new Vector2(11.03f, 28.45f), new Vector2(13.43f, 27.31f), new Vector2(24.27f, 16.47f) },
        };
        private static readonly Vector2[][] DeleteShapes =
        {
            new[] { new Vector2(4, 5.33f), new Vector2(10.67f, 5.33f), new Vector2(10.67f, 4), new Vector2(12, 2.67f), new Vector2(20, 2.67f), new Vector2(21.33f, 4),
                new Vector2(21.33f, 5.33f), new Vector2(28, 5.33f), new Vector2(29.33f, 6.67f), new Vector2(28, 8), new Vector2(4, 8), new Vector2(2.67f, 6.67f) },
            new[] { new Vector2(5.33f, 10.67f), new Vector2(26.67f, 10.67f), new Vector2(24.33f, 27.04f), new Vector2(23.31f, 28.73f), new Vector2(21.69f, 29.33f),
                new Vector2(10.32f, 29.33f), new Vector2(8.69f, 28.73f), new Vector2(7.68f, 27.04f) },
        };

        internal GalleryReviewIcon(Kind kind)
        {
            this.kind = kind;
            pickingMode = PickingMode.Ignore;
            AddToClassList("review-icon");
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ =>
            {
                if (customStyle.TryGetValue(ColorProperty, out var resolved) && resolved != color) { color = resolved; MarkDirtyRepaint(); }
            });
        }

        // Shapes are drawn in a 32 x 32 box, scaled to the element.
        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            float scale = Mathf.Min(rect.width, rect.height) / 32f;
            if (scale <= 0f) return;
            var offset = new Vector2(rect.x + (rect.width - 32f * scale) * 0.5f, rect.y + (rect.height - 32f * scale) * 0.5f);
            var painter = context.painter2D;
            painter.fillColor = color;
            foreach (var shape in kind == Kind.Like ? LikeShapes : kind == Kind.Edit ? EditShapes : DeleteShapes)
            {
                painter.BeginPath();
                painter.MoveTo(offset + shape[0] * scale);
                for (int i = 1; i < shape.Length; i++) painter.LineTo(offset + shape[i] * scale);
                painter.ClosePath();
                painter.Fill(FillRule.NonZero);
            }
        }
    }
}
