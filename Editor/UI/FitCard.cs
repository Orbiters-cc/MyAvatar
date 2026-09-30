using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // An accessory's fit on the avatar's custom base, as one card that changes with the answer: the question (two choice
    // tiles), lining it up with the original body, installing ReFit, the refit in progress and its result. It slides in when
    // its state is new, its buttons react on press, the progress bar shimmers and the result's badge pops.
    internal sealed class FitCard : VisualElement
    {
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/UI/fit-card.uss";
        internal static readonly Color Blue = new Color32(77, 163, 255, 255), Green = new Color32(52, 211, 140, 255),
            Amber = new Color32(255, 184, 64, 255), Red = new Color32(255, 107, 107, 255);

        internal sealed class Model
        {
            public string Fit, Item, Base, Version, Error;
            public int Shapes;
            public List<string> ShapeNames = new List<string>();
            public bool Rough, CanCommission, Animate;
            /// <summary>Work in progress on this accessory: shown instead of its answer.</summary>
            public bool Running;
            public float Progress;
            public string ProgressText;
        }

        internal sealed class Actions
        {
            public Action Fits, Refit, NotNow, Confirm, Cancel, Select, Restore, Commission, Retry, Dismiss;
            public Action<VisualElement> Install;
        }

        private VisualElement fill, shimmer;
        private Label progressLabel;
        private IVisualElementScheduledItem ticker;

        internal FitCard(Model model, Actions actions)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("fit-card");
            string baseName = string.IsNullOrEmpty(model.Base) ? "your custom base" : model.Base;
            if (model.Running) BuildProgress(model, baseName);
            else switch (model.Fit)
            {
                case AccessoryFit.Ask:
                    Header(FitIcon.Glyph.Sparkle, Blue, "Does it fit your body?", model, actions.NotNow);
                    Detail($"Your avatar uses {baseName}: {model.Shapes} of its blendshapes move the body right where {model.Item} sits.");
                    Chips(model);
                    var tiles = Row("fit-card__tiles");
                    tiles.Add(Tile(FitIcon.Glyph.Check, Green, "It fits", "Add " + Count(model.Shapes) + " so it flexes with you", actions.Fits, "fit-tile--yes"));
                    tiles.Add(Tile(FitIcon.Glyph.Refit, Blue, "It doesn't fit", "Refit it from the original base", actions.Refit, "fit-tile--refit"));
                    break;
                case AccessoryFit.AddShapes:
                    Header(FitIcon.Glyph.Sparkle, Blue, "Make it move with your body", model, actions.NotNow);
                    Detail($"{model.Item} lacks {Count(model.Shapes)} of {baseName}, like its flexing. Its own blendshapes stay as they are.");
                    Chips(model);
                    Row("fit-card__actions").Add(Button("Add " + Count(model.Shapes), "fit-btn--primary", actions.Fits));
                    break;
                case AccessoryFit.Refit:
                    Header(FitIcon.Glyph.Refit, Blue, "Made for the original base", model, actions.NotNow);
                    Detail($"{model.Item}'s creator made it for the original body. Refit it to {baseName} so it fits and flexes with you.");
                    Chips(model);
                    Row("fit-card__actions").Add(Button("Refit to " + baseName, "fit-btn--primary", actions.Refit));
                    break;
                case AccessoryFit.Place:
                    Header(FitIcon.Glyph.Target, Blue, "Line it up with the original body", model, actions.Cancel);
                    Step(1, "The original body is shown in blue over yours.");
                    var move = Step(2, $"If {model.Item} is off, move it onto the blue body.");
                    move.Add(Button("Select it", "fit-btn--ghost fit-btn--small", actions.Select));
                    var tightness = Step(3, "How close should it sit?");
                    tightness.parent.Add(TightnessControl());
                    var place = Row("fit-card__actions");
                    place.Add(Button("Refit now", "fit-btn--primary", actions.Confirm));
                    place.Add(Button("Cancel", "fit-btn--ghost", actions.Cancel));
                    break;
                case AccessoryFit.Install:
                    Header(FitIcon.Glyph.Download, Blue, "One click to go", model, actions.NotNow);
                    Detail($"ReFit fits clothing to {baseName}. It installs in a moment, then this continues on its own.");
                    actions.Install?.Invoke(this);
                    break;
                case AccessoryFit.Done:
                    Header(model.Rough ? FitIcon.Glyph.Alert : FitIcon.Glyph.Check, model.Rough ? Amber : Green,
                        model.Item + " moves with your body", model, actions.Dismiss, pop: model.Animate);
                    Detail($"{Count(model.Shapes)} now follow {baseName}, in the editor and in every animation once uploaded.");
                    if (model.Rough) Callout("A few spots could not be fitted exactly and may clip at full flex. A creator can refit it by hand.");
                    var done = Row("fit-card__actions");
                    if (model.Rough && model.CanCommission) done.Add(Button("Ask a creator", "fit-btn--primary", actions.Commission));
                    done.Add(Button("Restore original", "fit-btn--ghost", actions.Restore));
                    break;
                case AccessoryFit.Failed:
                    Header(FitIcon.Glyph.Alert, Red, "Couldn't refit " + model.Item, model, actions.NotNow);
                    Detail(string.IsNullOrEmpty(model.Error) ? "Something went wrong." : model.Error);
                    var failed = Row("fit-card__actions");
                    if (actions.Retry != null) failed.Add(Button("Try again", "fit-btn--primary", actions.Retry));
                    if (model.CanCommission) failed.Add(Button("Ask a creator", "fit-btn--ghost", actions.Commission));
                    break;
            }
            EnableInClassList("fit-card--warning", model.Fit == AccessoryFit.Done && model.Rough && !model.Running);
            EnableInClassList("fit-card--error", model.Fit == AccessoryFit.Failed && !model.Running);
            // New state: slide in. The class is removed once laid out, and USS transitions animate the rest.
            if (model.Animate)
            {
                AddToClassList("fit-card--enter");
                RegisterCallback<GeometryChangedEvent>(Enter);
            }
            RegisterCallback<DetachFromPanelEvent>(_ => ticker?.Pause());
        }

        private void Enter(GeometryChangedEvent evt)
        {
            UnregisterCallback<GeometryChangedEvent>(Enter);
            schedule.Execute(() =>
            {
                RemoveFromClassList("fit-card--enter");
                this.Query(className: "fit-card__badge--pop").ForEach(b => b.RemoveFromClassList("fit-card__badge--pop"));
            }).StartingIn(16);
        }

        /// <summary>Moves the bar of a running card (the refit reports its phases).</summary>
        internal void SetProgress(float value, string text)
        {
            if (fill == null) return;
            fill.style.width = Length.Percent(Mathf.Clamp01(value) * 100f);
            if (!string.IsNullOrEmpty(text)) progressLabel.text = text;
        }

        // ---- Parts ----------------------------------------------------------------------------------------------------

        private void BuildProgress(Model model, string baseName)
        {
            AddToClassList("fit-card--running");
            var header = Row("fit-card__header");
            var spinner = new FitIcon(FitIcon.Glyph.Spinner, Blue); spinner.AddToClassList("fit-card__spinner");
            var badge = new VisualElement(); badge.AddToClassList("fit-card__badge"); badge.Add(spinner); header.Add(badge);
            var title = new Label("Fitting " + model.Item + " to " + baseName); title.AddToClassList("fit-card__title"); header.Add(title);
            var track = new VisualElement(); track.AddToClassList("fit-card__track"); Add(track);
            fill = new VisualElement(); fill.AddToClassList("fit-card__fill"); track.Add(fill);
            shimmer = new VisualElement { pickingMode = PickingMode.Ignore }; shimmer.AddToClassList("fit-card__shimmer"); fill.Add(shimmer);
            progressLabel = new Label(); progressLabel.AddToClassList("fit-card__progress"); Add(progressLabel);
            SetProgress(model.Progress, string.IsNullOrEmpty(model.ProgressText) ? "Starting…" : model.ProgressText);
            // The spinner turns and a light sweeps along the bar while the refit works (it runs on a worker thread).
            double start = EditorApplication.timeSinceStartup;
            ticker = schedule.Execute(() =>
            {
                float t = (float)(EditorApplication.timeSinceStartup - start);
                spinner.style.rotate = new Rotate(new Angle(t * 360f % 360f, AngleUnit.Degree));
                float width = fill.resolvedStyle.width;
                if (width > 0) shimmer.style.left = (t * 160f) % (width + 60f) - 60f;
            }).Every(16);
        }

        private void Header(FitIcon.Glyph glyph, Color color, string text, Model model, Action close, bool pop = false)
        {
            var header = Row("fit-card__header");
            var badge = new VisualElement(); badge.AddToClassList("fit-card__badge");
            badge.style.backgroundColor = new Color(color.r, color.g, color.b, 0.16f);
            if (pop) badge.AddToClassList("fit-card__badge--pop");
            badge.Add(new FitIcon(glyph, color));
            header.Add(badge);
            var title = new Label(text); title.AddToClassList("fit-card__title"); header.Add(title);
            if (!string.IsNullOrEmpty(model.Version))
            {
                var version = new Label("v" + model.Version) { tooltip = "Custom base version" };
                version.AddToClassList("fit-card__version"); header.Add(version);
            }
            if (close != null)
            {
                var closeButton = new Button { tooltip = "Not now" };
                closeButton.AddToClassList("fit-card__close");
                closeButton.Add(new VectorIcon(IconGlyph.Close));
                Press(closeButton, close);
                header.Add(closeButton);
            }
        }

        private void Detail(string text)
        {
            var label = new Label(text); label.AddToClassList("fit-card__detail"); Add(label);
        }

        private void Callout(string text)
        {
            var row = Row("fit-card__callout");
            var label = new Label(text); label.AddToClassList("fit-card__callout-text"); row.Add(label);
        }

        // The blendshapes it would get, the first few by name.
        private void Chips(Model model)
        {
            if (model.ShapeNames == null || model.ShapeNames.Count == 0) return;
            var row = Row("fit-card__chips");
            foreach (string name in model.ShapeNames.Take(4))
            {
                var chip = new Label(name); chip.AddToClassList("fit-chip"); row.Add(chip);
            }
            if (model.ShapeNames.Count > 4)
            {
                var more = new Label("+" + (model.ShapeNames.Count - 4)) { tooltip = string.Join("\n", model.ShapeNames.Skip(4)) };
                more.AddToClassList("fit-chip"); more.AddToClassList("fit-chip--more"); row.Add(more);
            }
        }

        private VisualElement Step(int number, string text)
        {
            var step = Row("fit-step");
            var line = new VisualElement(); line.AddToClassList("fit-step__line"); step.Add(line);
            var dot = new Label(number.ToString()); dot.AddToClassList("fit-step__number"); line.Add(dot);
            var label = new Label(text); label.AddToClassList("fit-step__text"); line.Add(label);
            return line;
        }

        private static VisualElement TightnessControl()
        {
            float[] values = { 0f, 0.5f, 0.9f };
            var control = new SegmentedControl(new[]
            {
                new SegmentedControl.Option("Loose", null, "Keeps some room: best for accessories and loose clothing."),
                new SegmentedControl.Option("Balanced", null, "The default fit."),
                new SegmentedControl.Option("Tight", null, "Hugs the body: best for tight clothing."),
            }, index => Orbiters.Toolkit.Editor.Refit.RefitPreferences.Tightness = values[index]);
            float current = Orbiters.Toolkit.Editor.Refit.RefitPreferences.Tightness;
            control.SetIndex(Enumerable.Range(0, values.Length).OrderBy(i => Mathf.Abs(values[i] - current)).First());
            control.tooltip = "Shared with MCB's ReFit panel.";
            control.AddToClassList("fit-card__tightness");
            return control;
        }

        private VisualElement Row(string className)
        {
            var row = new VisualElement(); row.AddToClassList(className); Add(row);
            return row;
        }

        private static Button Tile(FitIcon.Glyph glyph, Color color, string title, string subtitle, Action action, string variant)
        {
            var tile = new Button { text = "" };
            tile.AddToClassList("fit-tile"); tile.AddToClassList(variant);
            var icon = new VisualElement(); icon.AddToClassList("fit-tile__icon");
            icon.style.backgroundColor = new Color(color.r, color.g, color.b, 0.16f);
            icon.Add(new FitIcon(glyph, color));
            tile.Add(icon);
            var texts = new VisualElement(); texts.AddToClassList("fit-tile__texts"); tile.Add(texts);
            var label = new Label(title); label.AddToClassList("fit-tile__title"); texts.Add(label);
            var sub = new Label(subtitle); sub.AddToClassList("fit-tile__subtitle"); texts.Add(sub);
            Press(tile, action);
            return tile;
        }

        private static Button Button(string text, string classes, Action action)
        {
            var button = new Button { text = text };
            button.AddToClassList("fit-btn");
            foreach (string c in classes.Split(' ')) button.AddToClassList(c);
            Press(button, action);
            return button;
        }

        // Visible feedback on press; the action runs on press too, with Unity's click as the fallback.
        private static void Press(Button button, Action action)
        {
            if (action == null) { button.SetEnabled(false); return; }
            button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("fit-pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("fit-pressed"), TrickleDown.TrickleDown);
            button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("fit-pressed"));
            ButtonInteraction.RegisterImmediateClick(button, action);
        }

        private static string Count(int shapes) => shapes + " blendshape" + (shapes == 1 ? "" : "s");
    }

    // Small glyphs of the fit card, drawn as vectors so they stay crisp at any scale.
    internal sealed class FitIcon : VisualElement
    {
        internal enum Glyph { Sparkle, Check, Refit, Target, Download, Alert, Spinner }

        private readonly Glyph glyph;
        private readonly Color color;

        internal FitIcon(Glyph glyph, Color color)
        {
            this.glyph = glyph;
            this.color = color;
            AddToClassList("fit-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            float s = Mathf.Min(rect.width, rect.height) / 24f;
            if (s <= 0f) return;
            var o = new Vector2(rect.x + (rect.width - 24f * s) * 0.5f, rect.y + (rect.height - 24f * s) * 0.5f);
            Vector2 P(float x, float y) => o + new Vector2(x, y) * s;
            var p = context.painter2D;
            p.strokeColor = color; p.fillColor = color;
            p.lineJoin = LineJoin.Round; p.lineCap = LineCap.Round; p.lineWidth = 2.2f * s;
            switch (glyph)
            {
                case Glyph.Sparkle:
                    Star(p, P(10f, 13f), 7f * s);
                    Star(p, P(18.5f, 5.5f), 3.2f * s);
                    break;
                case Glyph.Check:
                    p.BeginPath(); p.MoveTo(P(5f, 12.5f)); p.LineTo(P(10f, 17.5f)); p.LineTo(P(19f, 7f)); p.Stroke();
                    break;
                case Glyph.Refit:
                    // Two arrows turning around: the mesh is fitted again.
                    Arrow(p, P(12f, 12f), 7.5f * s, 200f, 330f, s);
                    Arrow(p, P(12f, 12f), 7.5f * s, 20f, 150f, s);
                    break;
                case Glyph.Target:
                    p.BeginPath(); p.Arc(P(12f, 12f), 7.5f * s, 0f, 360f); p.Stroke();
                    p.BeginPath(); p.Arc(P(12f, 12f), 2.6f * s, 0f, 360f); p.Fill();
                    p.BeginPath(); p.MoveTo(P(12f, 1.5f)); p.LineTo(P(12f, 4.5f)); p.MoveTo(P(12f, 19.5f)); p.LineTo(P(12f, 22.5f));
                    p.MoveTo(P(1.5f, 12f)); p.LineTo(P(4.5f, 12f)); p.MoveTo(P(19.5f, 12f)); p.LineTo(P(22.5f, 12f)); p.Stroke();
                    break;
                case Glyph.Download:
                    p.BeginPath(); p.MoveTo(P(12f, 4f)); p.LineTo(P(12f, 15f)); p.MoveTo(P(7.5f, 10.5f)); p.LineTo(P(12f, 15f)); p.LineTo(P(16.5f, 10.5f)); p.Stroke();
                    p.BeginPath(); p.MoveTo(P(5f, 19.5f)); p.LineTo(P(19f, 19.5f)); p.Stroke();
                    break;
                case Glyph.Alert:
                    p.BeginPath(); p.MoveTo(P(12f, 6f)); p.LineTo(P(12f, 13.5f)); p.Stroke();
                    p.BeginPath(); p.Arc(P(12f, 18f), 1.5f * s, 0f, 360f); p.Fill();
                    break;
                case Glyph.Spinner:
                    p.strokeColor = new Color(color.r, color.g, color.b, 0.25f);
                    p.BeginPath(); p.Arc(P(12f, 12f), 8f * s, 0f, 360f); p.Stroke();
                    p.strokeColor = color;
                    p.BeginPath(); p.Arc(P(12f, 12f), 8f * s, -90f, 20f); p.Stroke();
                    break;
            }
        }

        // An arc from one angle to another (degrees, clockwise on screen) ending in an arrow head.
        private static void Arrow(Painter2D p, Vector2 c, float r, float from, float to, float s)
        {
            p.BeginPath(); p.Arc(c, r, from, to); p.Stroke();
            float a = to * Mathf.Deg2Rad;
            var end = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
            var along = new Vector2(-Mathf.Sin(a), Mathf.Cos(a));
            var across = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            p.BeginPath();
            p.MoveTo(end - along * 3.2f * s + across * 3f * s);
            p.LineTo(end);
            p.LineTo(end - along * 3.2f * s - across * 3f * s);
            p.Stroke();
        }

        // A four-pointed star with curved sides.
        private static void Star(Painter2D p, Vector2 c, float r)
        {
            p.BeginPath();
            p.MoveTo(c + new Vector2(0, -r));
            p.QuadraticCurveTo(c, c + new Vector2(r, 0));
            p.QuadraticCurveTo(c, c + new Vector2(0, r));
            p.QuadraticCurveTo(c, c + new Vector2(-r, 0));
            p.QuadraticCurveTo(c, c + new Vector2(0, -r));
            p.ClosePath();
            p.Fill();
        }
    }
}
