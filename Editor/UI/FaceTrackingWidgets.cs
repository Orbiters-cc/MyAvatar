using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>A ring that fills to a fraction (how many of the face's tracking shapes are there), easing in when shown.</summary>
    internal sealed class FaceTrackingRing : VisualElement
    {
        private static readonly Color Track = new Color32(46, 49, 56, 255), Fill = new Color32(0, 218, 109, 255), Partial = new Color32(255, 176, 32, 255);
        private readonly Label value, caption;
        private float target, shown, started = -1f;

        public FaceTrackingRing()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.Center;
            value = new Label { pickingMode = PickingMode.Ignore };
            value.style.fontSize = 17; value.style.unityFontStyleAndWeight = FontStyle.Bold; value.style.color = Color.white;
            value.style.marginBottom = -2; value.style.paddingBottom = 0; value.style.paddingTop = 0;
            caption = new Label("shapes") { pickingMode = PickingMode.Ignore };
            caption.style.fontSize = 9; caption.style.color = new Color(.56f, .56f, .56f); caption.style.paddingTop = 0;
            Add(value); Add(caption);
        }

        /// <summary>Fills to <paramref name="fraction"/> over half a second; the label reads <paramref name="text"/>.</summary>
        public void Set(float fraction, string text, string under = "shapes")
        {
            target = Mathf.Clamp01(fraction);
            value.text = text;
            caption.text = under;
            // Without text the ring frames a logo.
            value.style.display = caption.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            started = (float)EditorApplication.timeSinceStartup;
            float from = shown;
            schedule.Execute(timer =>
            {
                float t = Mathf.Clamp01(((float)EditorApplication.timeSinceStartup - started) / .6f);
                shown = Mathf.Lerp(from, target, 1f - Mathf.Pow(1f - t, 3f));
                MarkDirtyRepaint();
            }).Every(16).Until(() => (float)EditorApplication.timeSinceStartup - started > .65f);
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            float radius = Mathf.Min(rect.width, rect.height) * .5f - 4f;
            if (radius <= 0f) return;
            var center = rect.center;
            var painter = context.painter2D;
            painter.lineWidth = 6f;
            painter.lineCap = LineCap.Round;
            painter.strokeColor = Track;
            painter.BeginPath(); painter.Arc(center, radius, 0f, 360f); painter.Stroke();
            if (shown <= .001f) return;
            painter.strokeColor = target >= .999f ? Fill : target >= .75f ? Fill : Partial;
            painter.BeginPath(); painter.Arc(center, radius, -90f, -90f + 360f * shown); painter.Stroke();
        }
    }

    /// <summary>A soft round glow (a radial gradient drawn as a fan of triangles).</summary>
    internal sealed class FaceTrackingGlow : VisualElement
    {
        private Color color;

        public FaceTrackingGlow(Color color)
        {
            this.color = color;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            float radius = Mathf.Min(rect.width, rect.height) * .5f;
            if (radius <= 0f) return;
            const int segments = 48;
            var mesh = context.Allocate(segments + 1, segments * 3);
            var center = rect.center;
            mesh.SetNextVertex(new Vertex { position = new Vector3(center.x, center.y, Vertex.nearZ), tint = color });
            var clear = new Color(color.r, color.g, color.b, 0f);
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                mesh.SetNextVertex(new Vertex { position = new Vector3(center.x + Mathf.Cos(a) * radius, center.y + Mathf.Sin(a) * radius, Vertex.nearZ), tint = clear });
            }
            for (int i = 0; i < segments; i++)
            {
                mesh.SetNextIndex(0);
                mesh.SetNextIndex((ushort)(1 + (i + 1) % segments));
                mesh.SetNextIndex((ushort)(1 + i));
            }
        }
    }

    /// <summary>Rings rippling out from the middle while the test waits for the phone.</summary>
    internal sealed class FaceTrackingPulse : VisualElement
    {
        private static readonly Color Color = new Color32(0, 218, 109, 255);
        private readonly IVisualElementScheduledItem ticking;

        public FaceTrackingPulse()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            ticking = schedule.Execute(MarkDirtyRepaint).Every(33);
            RegisterCallback<DetachFromPanelEvent>(_ => ticking.Pause());
            RegisterCallback<AttachToPanelEvent>(_ => ticking.Resume());
        }

        private void Draw(MeshGenerationContext context)
        {
            if (resolvedStyle.display == DisplayStyle.None) return;
            var rect = contentRect;
            float reach = Mathf.Min(rect.width, rect.height) * .48f;
            if (reach <= 0f) return;
            var painter = context.painter2D;
            painter.lineWidth = 2f;
            double time = EditorApplication.timeSinceStartup;
            for (int i = 0; i < 3; i++)
            {
                float t = (float)((time / 2.4 + i / 3.0) % 1.0);
                painter.strokeColor = new Color(Color.r, Color.g, Color.b, (1f - t) * .35f);
                painter.BeginPath(); painter.Arc(rect.center, Mathf.Lerp(reach * .18f, reach, t), 0f, 360f); painter.Stroke();
            }
        }
    }

    /// <summary>One value VRCFaceTracking sends, as a bar: from the left for 0 to 1, from the middle for -1 to 1.</summary>
    internal sealed class FaceTrackingMeter : VisualElement
    {
        public readonly string V2;
        public readonly bool Signed;
        private readonly VisualElement fill;
        private readonly Label number;
        private float shown = float.NaN;

        public FaceTrackingMeter(string label, string v2, bool signed)
        {
            V2 = v2; Signed = signed;
            AddToClassList("ft-meter");
            tooltip = "v2/" + v2;
            var name = new Label(label); name.AddToClassList("ft-meter__label"); Add(name);
            var track = new VisualElement(); track.AddToClassList("ft-meter__track"); Add(track);
            if (signed) { var zero = new VisualElement(); zero.AddToClassList("ft-meter__zero"); track.Add(zero); }
            fill = new VisualElement(); fill.AddToClassList("ft-meter__fill"); track.Add(fill);
            number = new Label(); number.AddToClassList("ft-meter__value"); Add(number);
        }

        public void Set(float value, bool on)
        {
            EnableInClassList("ft-meter--off", !on);
            value = Mathf.Clamp(value, Signed ? -1f : 0f, 1f);
            if (Mathf.Abs(value - shown) < .004f) return;
            shown = value;
            if (Signed)
            {
                fill.style.left = Length.Percent(50f + Mathf.Min(0f, value) * 50f);
                fill.style.width = Length.Percent(Mathf.Abs(value) * 50f);
            }
            else
            {
                fill.style.left = 0;
                fill.style.width = Length.Percent(value * 100f);
            }
            fill.EnableInClassList("ft-meter__fill--negative", value < 0f);
            number.text = value.ToString(Signed ? "+0.00;-0.00;0.00" : "0.00");
        }
    }


    /// <summary>
    /// A face tracker's mark in plain white: Meta's, Vive's and Apple's logos, or sliders for the custom choice. Dimmed
    /// until <see cref="On"/>.
    /// </summary>
    internal sealed class FaceTrackerLogo : VisualElement
    {
        private const string Meta = "<svg viewBox=\"0 0 32 32\" xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M5,19.5c0-4.6,2.3-9.4,5-9.4c1.5,0,2.7,0.9,4.6,3.6c-1.8,2.8-2.9,4.5-2.9,4.5c-2.4,3.8-3.2,4.6-4.5,4.6 C5.9,22.9,5,21.7,5,19.5 M20.7,17.8L19,15c-0.4-0.7-0.9-1.4-1.3-2c1.5-2.3,2.7-3.5,4.2-3.5c3,0,5.4,4.5,5.4,10.1 c0,2.1-0.7,3.3-2.1,3.3S23.3,22,20.7,17.8 M16.4,11c-2.2-2.9-4.1-4-6.3-4C5.5,7,2,13.1,2,19.5c0,4,1.9,6.5,5.1,6.5 c2.3,0,3.9-1.1,6.9-6.3c0,0,1.2-2.2,2.1-3.7c0.3,0.5,0.6,1,0.9,1.6l1.4,2.4c2.7,4.6,4.2,6.1,6.9,6.1c3.1,0,4.8-2.6,4.8-6.7 C30,12.6,26.4,7,22.1,7C19.8,7,18,8.8,16.4,11\"/></svg>";
        private const string Vive = "<svg viewBox=\"-2.25 0 804.75 705.8\" xmlns=\"http://www.w3.org/2000/svg\"><path d=\"m791.79 590.97-52.02 88.8c-9.18 16.84-26.01 26.03-44.37 26.03h-592.08c-18.36 0-35.19-9.19-44.37-26.03l-52.02-88.8c-9.18-15.31-9.18-35.21 0-52.05l298.34-512.89c9.18-16.84 26.01-26.03 44.37-26.03h102.5c18.36 0 35.19 9.19 44.37 26.03l296.81 512.89c9.18 15.31 9.18 35.21-1.53 52.05zm-175.94-125.54c-21.42-97.99-71.91-185.25-142.29-249.56-41.31-38.27-104.03-38.27-145.34 0-71.91 65.84-122.4 151.57-142.29 249.56-10.7 53.59 21.42 105.64 73.44 122.48 44.37 13.78 91.8 21.44 140.76 21.44 48.95 0 96.38-7.66 140.75-21.44 53.55-15.31 84.15-68.89 74.97-122.48z\"/></svg>";
        private const string Apple = "<svg viewBox=\"0 0 24 24\" xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M18.71 19.5C17.88 20.74 17 21.95 15.66 21.97C14.32 22 13.89 21.18 12.37 21.18C10.84 21.18 10.37 21.95 9.09997 22C7.78997 22.05 6.79997 20.68 5.95997 19.47C4.24997 17 2.93997 12.45 4.69997 9.39C5.56997 7.87 7.12997 6.91 8.81997 6.88C10.1 6.86 11.32 7.75 12.11 7.75C12.89 7.75 14.37 6.68 15.92 6.84C16.57 6.87 18.39 7.1 19.56 8.82C19.47 8.88 17.39 10.1 17.41 12.63C17.44 15.65 20.06 16.66 20.09 16.67C20.06 16.74 19.67 18.11 18.71 19.5ZM13 3.5C13.73 2.67 14.94 2.04 15.94 2C16.07 3.17 15.6 4.35 14.9 5.19C14.21 6.04 13.07 6.7 11.95 6.61C11.8 5.46 12.36 4.26 13 3.5Z\"/></svg>";

        public FaceTrackerLogo(FaceTrackingPreset preset)
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("ft-logo");
            string svg = preset == FaceTrackingPreset.MetaQuest ? Meta : preset == FaceTrackingPreset.Vive ? Vive : preset == FaceTrackingPreset.ARKit ? Apple : null;
            VisualElement mark = svg != null ? new OrbitersVectorLogo(svg) : (VisualElement)new Orbiters.Toolkit.Editor.VectorIcon(Orbiters.Toolkit.Editor.IconGlyph.Sliders);
            mark.AddToClassList("ft-logo__mark");
            Add(mark);
        }

        public bool On { set => EnableInClassList("ft-logo--on", value); }
    }
}
