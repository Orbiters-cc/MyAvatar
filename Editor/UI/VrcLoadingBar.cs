using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// VRChat's avatar loading bar as players see it the moment an avatar starts downloading: a navy pill with the cyan
    /// progress pill at its start, its darker stripe drifting left while it waits, and "0% / 17.7 MB". Sizes and colours were
    /// measured on a screenshot of VRChat's bar (191 × 25 px). Styles: optimization.uss.
    /// </summary>
    internal sealed class VrcLoadingBar : VisualElement
    {
        internal const float Width = 191f, Height = 25f;
        // The progress pill sits 3 px inside the bar; at 0% it is 40 px long, with one stripe 16 px wide leaning 3.5 px, 11 px
        // in at mid-height. The text starts 56 px from the bar's left end.
        private const float Inset = 3f, FillWidth = 40f, StripeStart = 11f, StripeWidth = 16f, StripeLean = 3.5f, TextLeft = 56f;
        // The stripe drifts left by one pill length every two seconds and comes back in from the right, a pixel per tick.
        private const float Drift = FillWidth / 2f;
        private const long Tick = (long)(1000f / Drift);
        private static readonly Color Back = Hex(0x172C4B), Edge = Hex(0x1E4271), Rim = Hex(0x164676), Shade = Hex(0x3F8FC3), Fill = Hex(0x48ADD8), Stripe = Hex(0x3692C7);

        private readonly Label text;
        private readonly VisualElement stripes;
        private IVisualElementScheduledItem animation;
        private float offset;

        public VrcLoadingBar()
        {
            AddToClassList("vrc-loading");
            style.width = Width; style.height = Height; style.flexShrink = 0;
            generateVisualContent += DrawBar;
            // The progress pill clips what drifts inside it to its round ends.
            var pill = new VisualElement { pickingMode = PickingMode.Ignore }; pill.AddToClassList("vrc-loading__pill"); Add(pill);
            var face = new VisualElement { pickingMode = PickingMode.Ignore }; face.AddToClassList("vrc-loading__face"); pill.Add(face);
            stripes = new VisualElement { pickingMode = PickingMode.Ignore }; stripes.AddToClassList("vrc-loading__stripes"); pill.Add(stripes);
            stripes.generateVisualContent += DrawStripes;
            pill.style.left = Inset; pill.style.top = Inset; pill.style.width = FillWidth; pill.style.height = Height - 2f * Inset;
            pill.style.backgroundColor = Shade; face.style.backgroundColor = Fill;
            text = new Label(); text.AddToClassList("vrc-loading__text"); text.style.left = TextLeft; Add(text);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                animation?.Pause();
                double start = Time.realtimeSinceStartupAsDouble;
                animation = schedule.Execute(() => { offset = (float)((Time.realtimeSinceStartupAsDouble - start) * Drift % FillWidth); stripes.MarkDirtyRepaint(); }).Every(Tick);
            });
            RegisterCallback<DetachFromPanelEvent>(_ => animation?.Pause());
        }

        /// <summary>Shows the download's size, as VRChat writes it.</summary>
        public void SetSize(long bytes) => text.text = "0% / " + Format(bytes);

        internal static string Format(long bytes)
        {
            double megabytes = bytes / 1048576.0;
            return (megabytes < 100 ? megabytes.ToString("0.#") : megabytes.ToString("0")) + " MB";
        }

        // The bar, its edge and the darker rim around the progress pill.
        private static void DrawBar(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            Pill(painter, new Rect(.5f, .5f, Width - 1f, Height - 1f));
            painter.fillColor = Back; painter.Fill();
            painter.strokeColor = Edge; painter.lineWidth = 1f; painter.Stroke();
            Pill(painter, new Rect(Inset - .5f, Inset - .5f, FillWidth + 1f, Height - 2f * Inset + 1f));
            painter.fillColor = Rim; painter.Fill();
        }

        // One stripe per pill length, drifted left; the one leaving on the left comes back in on the right.
        private void DrawStripes(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            float top = 1f, bottom = Height - 2f * Inset - 1.5f, half = StripeLean / 2f;
            for (int k = -1; k <= 1; k++)
            {
                float x = StripeStart - offset + k * FillWidth;
                painter.BeginPath();
                painter.MoveTo(new Vector2(x + half, top));
                painter.LineTo(new Vector2(x + StripeWidth + half, top));
                painter.LineTo(new Vector2(x + StripeWidth - half, bottom));
                painter.LineTo(new Vector2(x - half, bottom));
                painter.ClosePath();
                painter.fillColor = Stripe; painter.Fill();
            }
        }

        // A rectangle with fully round ends.
        private static void Pill(Painter2D painter, Rect rect)
        {
            float r = rect.height / 2f;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin + r, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax - r, rect.yMin));
            painter.Arc(new Vector2(rect.xMax - r, rect.yMin + r), r, -90f, 90f);
            painter.LineTo(new Vector2(rect.xMin + r, rect.yMax));
            painter.Arc(new Vector2(rect.xMin + r, rect.yMin + r), r, 90f, 270f);
            painter.ClosePath();
        }

        private static Color Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8 & 255), (byte)(rgb & 255), 255);
    }
}
