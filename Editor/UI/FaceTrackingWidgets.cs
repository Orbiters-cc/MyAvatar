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
}
