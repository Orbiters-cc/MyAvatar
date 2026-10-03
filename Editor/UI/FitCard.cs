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
        // My Avatar's palette: its green accent, the light grey of secondary actions, amber and red.
        internal static readonly Color Green = new Color32(0, 218, 109, 255), Light = new Color32(217, 217, 217, 255),
            Amber = new Color32(255, 176, 32, 255), Red = new Color32(255, 107, 107, 255);

        internal sealed class Model
        {
            public string Fit, Item, Base, Version, Error;
            /// <summary>A picture of the custom base; null while it loads (asked again for a few seconds).</summary>
            public Func<Texture2D> Thumbnail;
            public int Shapes;
            public List<string> ShapeNames = new List<string>();
            public bool Rough, CanCommission, Animate;
            /// <summary>Work in progress on this accessory: shown instead of its answer.</summary>
            public bool Running;
            public float Progress;
            public string ProgressText;
            public double StartedAt;
        }

        internal sealed class Actions
        {
            public Action Fits, Refit, NotNow, Confirm, Cancel, Restore, Commission, Retry, Dismiss, Stop;
            public Action<VisualElement> Install;
        }

        private VisualElement fill, shimmer;
        private Label progressLabel;
        private string progressText;
        private double startedAt;
        private IVisualElementScheduledItem ticker;

        internal FitCard(Model model, Actions actions)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) styleSheets.Add(sheet);
            AddToClassList("fit-card");
            string baseName = string.IsNullOrEmpty(model.Base) ? "your custom base" : model.Base;
            if (model.Running) BuildProgress(model, actions);
            else switch (model.Fit)
            {
                case AccessoryFit.Ask:
                    Header(FitIcon.Glyph.Sparkle, Green, "Does it fit your body?", model, actions.NotNow);
                    Detail($"Your avatar uses {baseName}: {model.Shapes} of its blendshapes move the body right where {model.Item} sits.");
                    Chips(model);
                    var tiles = Row("fit-card__tiles");
                    tiles.Add(Tile(FitIcon.Glyph.Check, Green, "Already fits", "Add " + Count(model.Shapes) + " so it flexes with you", actions.Fits, "fit-tile--yes"));
                    tiles.Add(Tile(FitIcon.Glyph.Refit, Light, "ReFit", "Fit it to your body from the original base", actions.Refit, "fit-tile--refit"));
                    break;
                case AccessoryFit.AddShapes:
                    Header(FitIcon.Glyph.Sparkle, Green, "Make it move with your body", model, actions.NotNow);
                    Detail($"{model.Item} lacks {Count(model.Shapes)} of {baseName}, like its flexing. Its own blendshapes stay as they are.");
                    Chips(model);
                    Row("fit-card__actions").Add(Button("Add " + Count(model.Shapes), "fit-btn--primary", actions.Fits));
                    break;
                case AccessoryFit.Refit:
                    Header(FitIcon.Glyph.Refit, Green, "Made for the original base", model, actions.NotNow);
                    Detail($"{model.Item}'s creator made it for the original body. Refit it to {baseName} so it fits and flexes with you.");
                    Chips(model);
                    Row("fit-card__actions").Add(Button("ReFit to " + baseName, "fit-btn--primary", actions.Refit));
                    break;
                case AccessoryFit.Place:
                    Header(FitIcon.Glyph.Target, Green, "Line it up with the original body", model, actions.Cancel);
                    Step(1, "The original body is shown see-through, in blue, over yours in the Scene view.");
                    Step(2, $"If {model.Item} is off, drag its arrows in the Scene view onto the blue body.");
                    var tightness = Step(3, "How close should it sit?");
                    tightness.parent.Add(TightnessControl());
                    var place = Row("fit-card__actions");
                    place.Add(Button("ReFit now", "fit-btn--primary", actions.Confirm));
                    place.Add(Button("Cancel", "fit-btn--ghost", actions.Cancel));
                    break;
                case AccessoryFit.Install:
                    Header(FitIcon.Glyph.Download, Green, "One click to go", model, actions.NotNow);
                    Detail($"ReFit fits clothing to {baseName}. It installs in a moment, then this continues on its own.");
                    actions.Install?.Invoke(this);
                    break;
                case AccessoryFit.Done:
                    Header(model.Rough ? FitIcon.Glyph.Alert : FitIcon.Glyph.Check, model.Rough ? Amber : Green,
                        "Check your fit", model, null, pop: model.Animate);
                    Detail($"{model.Item} has been fitted to {baseName}. Look it over from every side and try your body sliders.");
                    if (model.Rough) Callout("A few spots could not be fitted exactly and may clip at full flex. A creator can refit it by hand.");
                    var done = Row("fit-card__actions");
                    if (model.CanCommission) done.Add(Button("Ask a creator", "fit-btn--primary", actions.Commission));
                    var restore = Button("Cancel refit", "fit-btn--ghost", actions.Restore);
                    restore.tooltip = "Restore the clothing's mesh and pose from before this refit.";
                    done.Add(restore);
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
            if (!string.IsNullOrEmpty(text)) progressText = text;
            UpdateElapsed();
        }

        private void UpdateElapsed()
        {
            int seconds = Math.Max(0, (int)(EditorApplication.timeSinceStartup - startedAt));
            progressLabel.text = progressText + (seconds > 0 ? $" · {seconds}s" : "");
        }

        // ---- Parts ----------------------------------------------------------------------------------------------------

        private void BuildProgress(Model model, Actions actions)
        {
            startedAt = model.StartedAt > 0 ? model.StartedAt : EditorApplication.timeSinceStartup;
            AddToClassList("fit-card--running");
            var header = Row("fit-card__header");
            var spinner = new FitIcon(FitIcon.Glyph.Spinner, Green); spinner.AddToClassList("fit-card__spinner");
            var badge = new VisualElement(); badge.AddToClassList("fit-card__badge"); badge.Add(spinner); header.Add(badge);
            var title = new Label("Fitting " + model.Item); title.AddToClassList("fit-card__title"); header.Add(title);
            header.Add(BasePill(model));
            if (actions.Stop != null)
            {
                var cancel = Button("Cancel", "fit-btn--ghost", actions.Stop);
                cancel.tooltip = "Stop fitting; keep completed meshes and leave the current mesh unchanged.";
                header.Add(cancel);
            }
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
                UpdateElapsed();
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
            header.Add(BasePill(model));
            if (close != null)
            {
                var closeButton = new Button { tooltip = "Not now" };
                closeButton.AddToClassList("fit-card__close");
                closeButton.Add(new VectorIcon(IconGlyph.Close));
                Press(closeButton, close);
                header.Add(closeButton);
            }
        }

        // The avatar's custom base: its picture, name and version.
        private VisualElement BasePill(Model model)
        {
            var pill = new VisualElement { tooltip = "Your avatar's custom base" };
            pill.AddToClassList("fit-base");
            // The picture may still be loading: it fades in when ready, never waited for.
            if (model.Thumbnail != null)
            {
                var picture = new AssetThumbnail(model.Thumbnail);
                picture.AddToClassList("fit-base__picture");
                pill.Add(picture);
            }
            if (!string.IsNullOrEmpty(model.Base)) { var name = new Label(model.Base); name.AddToClassList("fit-base__name"); pill.Add(name); }
            if (!string.IsNullOrEmpty(model.Version)) { var version = new Label("v" + model.Version); version.AddToClassList("fit-base__version"); pill.Add(version); }
            return pill;
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
                    // ReFit's logo: a thin bar, a thick bar and three short ones (its SVG, 39 x 35, fitted in the box).
                    Bar(p, P(2.5f, 4f), 19f, 1.9f, s);
                    Bar(p, P(2.5f, 10.2f), 19f, 4.8f, s);
                    Bar(p, P(2.5f, 18.6f), 4.5f, 1.5f, s);
                    Bar(p, P(9.8f, 18.6f), 4.5f, 1.5f, s);
                    Bar(p, P(17f, 18.6f), 4.5f, 1.5f, s);
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

        // A filled bar from its top-left corner, in box units.
        private static void Bar(Painter2D p, Vector2 topLeft, float width, float height, float s)
        {
            p.BeginPath();
            p.MoveTo(topLeft);
            p.LineTo(topLeft + new Vector2(width * s, 0));
            p.LineTo(topLeft + new Vector2(width * s, height * s));
            p.LineTo(topLeft + new Vector2(0, height * s));
            p.ClosePath();
            p.Fill();
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
