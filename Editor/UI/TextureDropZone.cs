using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // The texture input: an empty drop field, a progress bar while a set is applied, then the result with Undo and Save.
    // The field morphs between states with a slightly bouncy height spring and crossfaded contents; it keeps accepting
    // drops in the empty and result states.
    internal sealed class TextureDropZone : VisualElement
    {
        private enum State { Idle, Working, Done }

        private static readonly Color Green = new Color(0f, .855f, .427f), Amber = new Color(1f, .69f, .13f), Dash = new Color(.48f, .48f, .48f);
        private readonly Action<string[]> dropped;
        private readonly VisualElement idle, working, done, fill, shimmer, statusDot, backgroundDot;
        private readonly Label progressLabel, statusLabel, backgroundLabel, idleMessage;
        private State state = State.Idle;
        private VisualElement outgoing;
        private readonly Action<bool> aiToggled;
        private readonly VisualElement aiButton;
        private readonly Orbiters.Toolkit.Editor.VectorIcon aiIcon;
        private bool aiConnected, aiEnabled;
        private float height = -1, velocity, target, fade = 1, displayedProgress, targetProgress, border = 1, shimmerPhase;
        private double lastTick;
        private IVisualElementScheduledItem ticker;

        internal TextureDropZone(Action<string[]> dropped, Button undo, Button save, Action<bool> aiToggled)
        {
            this.dropped = dropped;
            this.aiToggled = aiToggled;
            AddToClassList("drop-zone"); focusable = true;

            idle = Layer("drop-zone__idle");
            var icon = new Image { image = EditorGUIUtility.IconContent("TextAsset Icon").image, scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("drop-icon"); idle.Add(icon);
            idle.Add(new Label("Drop all your textures here !") { name = "drop-title" });
            idle.Add(new Label("PNG, JPG or TGA · multiple files or a folder") { name = "drop-hint" });
            idle.Add(Browse("Choose folder…", "mcb-button"));
            idleMessage = new Label(); idleMessage.AddToClassList("drop-zone__message"); idle.Add(idleMessage);

            working = Layer("drop-zone__working");
            var track = new VisualElement(); track.AddToClassList("drop-zone__track"); working.Add(track);
            fill = new VisualElement(); fill.AddToClassList("drop-zone__fill"); track.Add(fill);
            shimmer = new VisualElement(); shimmer.AddToClassList("drop-zone__shimmer"); track.Add(shimmer);
            progressLabel = new Label(); progressLabel.AddToClassList("drop-zone__progress-label"); track.Add(progressLabel);

            done = Layer("drop-zone__done");
            var status = new VisualElement(); status.AddToClassList("drop-zone__status"); done.Add(status);
            statusDot = new VisualElement(); statusDot.AddToClassList("drop-zone__dot"); status.Add(statusDot);
            statusLabel = new Label(); statusLabel.AddToClassList("drop-zone__status-label"); status.Add(statusLabel);
            var background = new VisualElement(); background.AddToClassList("drop-zone__background"); done.Add(background);
            backgroundDot = new VisualElement(); backgroundDot.AddToClassList("drop-zone__dot"); background.Add(backgroundDot);
            backgroundLabel = new Label(); backgroundLabel.AddToClassList("drop-zone__background-label"); background.Add(backgroundLabel);
            var actions = new VisualElement(); actions.AddToClassList("drop-zone__actions"); done.Add(actions);
            undo.AddToClassList("drop-zone__undo"); save.AddToClassList("drop-zone__save");
            actions.Add(undo); actions.Add(save);
            var again = new VisualElement(); again.AddToClassList("drop-zone__again"); done.Add(again);
            again.Add(new Label("Drop another set to replace it, or") { name = "drop-again" });
            again.Add(Browse("choose a folder", "drop-zone__link"));
            SetBackground(null);
            // AI switch in the corner: a crossed robot while AI matching is off or the account is not connected. Once
            // connected, a press flips it at once; the account preference is saved behind it.
            aiButton = new VisualElement { focusable = true };
            aiButton.AddToClassList("drop-zone__ai");
            aiIcon = new Orbiters.Toolkit.Editor.VectorIcon(Orbiters.Toolkit.Editor.IconGlyph.RobotOff);
            aiIcon.AddToClassList("drop-zone__ai-icon");
            aiButton.Add(aiIcon);
            aiButton.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) { ToggleAi(); e.StopPropagation(); } });
            aiButton.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Space || e.keyCode == KeyCode.Return) ToggleAi(); });
            Add(aiButton);
            SetAi(false, false);

            foreach (var layer in new[] { idle, working, done }) layer.RegisterCallback<GeometryChangedEvent>(_ => Retarget());
            generateVisualContent += DrawBorder;
            RegisterCallback<DragUpdatedEvent>(e => {
                DragAndDrop.visualMode = state == State.Working || !enabledInHierarchy ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.Copy;
                if (state != State.Working) AddToClassList("drag-over");
                MarkDirtyRepaint(); e.StopPropagation(); });
            RegisterCallback<DragLeaveEvent>(_ => Leave());
            RegisterCallback<DragExitedEvent>(_ => Leave());
            RegisterCallback<DragPerformEvent>(e => {
                if (state == State.Working || !enabledInHierarchy) return;
                var paths = DragAndDrop.paths.Concat(DragAndDrop.objectReferences.Select(AssetDatabase.GetAssetPath)).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToArray();
                DragAndDrop.AcceptDrag(); Leave(); dropped(paths); e.StopPropagation(); });
            RegisterCallback<AttachToPanelEvent>(_ => { lastTick = EditorApplication.timeSinceStartup; ticker = schedule.Execute(Tick).Every(16); });
            RegisterCallback<DetachFromPanelEvent>(_ => { ticker?.Pause(); ticker = null; });
            Show(idle, instant: true);
        }

        internal void ShowIdle(string message = null, bool warning = false)
        {
            idleMessage.text = message ?? ""; idleMessage.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            idleMessage.EnableInClassList("warning", warning);
            Enter(State.Idle, idle);
        }

        internal void ShowProgress(float value, string text)
        {
            if (state != State.Working) { displayedProgress = 0; targetProgress = 0; }
            targetProgress = Mathf.Max(targetProgress, Mathf.Clamp01(value));
            progressLabel.text = text;
            Enter(State.Working, working);
        }

        internal void ShowDone(string status, bool warning)
        {
            statusLabel.text = status;
            statusDot.style.backgroundColor = warning ? Amber : Green;
            Enter(State.Done, done);
        }

        // A quiet second line for background work, such as Orbiters AI resolving the remaining textures.
        internal void SetBackground(string text)
        {
            backgroundLabel.text = text ?? "";
            backgroundLabel.parent.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        internal void SetAi(bool connected, bool enabled)
        {
            aiConnected = connected;
            aiEnabled = connected && enabled;
            aiIcon.Glyph = aiEnabled ? Orbiters.Toolkit.Editor.IconGlyph.Robot : Orbiters.Toolkit.Editor.IconGlyph.RobotOff;
            aiButton.EnableInClassList("drop-zone__ai--on", aiEnabled);
            aiButton.EnableInClassList("drop-zone__ai--available", connected);
            aiButton.tooltip = !connected
                ? "AI help is off.\nWhen My Avatar can’t tell where a texture goes from its name, AI can place it for you. Log in at the top of this panel to use it."
                : aiEnabled
                    ? "AI help is on.\nTextures My Avatar can’t place from their names are sent (names, sizes and colour stats, never the images) to Orbiters’ AI to find their slot.\nClick to turn it off."
                    : "AI help is off.\nTextures My Avatar can’t place from their names are left for you to choose.\nClick to let AI place them.";
        }

        private void ToggleAi()
        {
            if (!aiConnected || !enabledInHierarchy) return;
            SetAi(true, !aiEnabled);
            aiToggled?.Invoke(aiEnabled);
        }

        private VisualElement Layer(string className)
        {
            var layer = new VisualElement(); layer.AddToClassList("drop-zone__layer"); layer.AddToClassList(className);
            layer.style.display = DisplayStyle.None; Add(layer); return layer;
        }

        private Button Browse(string text, string className)
        {
            var button = new Button(() => {
                string path = EditorUtility.OpenFolderPanel("Choose a texture folder", "", "");
                if (!string.IsNullOrEmpty(path)) dropped(new[] { path });
            }) { text = text };
            button.AddToClassList(className);
            return button;
        }

        private void Enter(State next, VisualElement layer)
        {
            if (state == next) { Retarget(); return; }
            state = next;
            Show(layer, instant: false);
        }

        private void Show(VisualElement layer, bool instant)
        {
            var current = new[] { idle, working, done }.FirstOrDefault(l => l != layer && l.style.display == DisplayStyle.Flex && l != outgoing);
            if (outgoing != null && outgoing != layer) outgoing.style.display = DisplayStyle.None;
            outgoing = instant ? null : current;
            foreach (var other in new[] { idle, working, done }) if (other != layer && other != outgoing) other.style.display = DisplayStyle.None;
            layer.style.display = DisplayStyle.Flex;
            layer.BringToFront();
            // The AI switch stays above every layer, or the shown layer would take its clicks.
            aiButton.BringToFront();
            fade = instant ? 1 : 0;
            ApplyFade();
            Retarget();
        }

        private void Retarget()
        {
            var layer = state == State.Idle ? idle : state == State.Working ? working : done;
            float measured = layer.resolvedStyle.height;
            if (float.IsNaN(measured) || measured <= 0) return;
            target = measured;
            if (height < 0) { height = target; style.height = height; }
        }

        private void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Clamp((float)(now - lastTick), 0f, .05f);
            lastTick = now;
            // Height: damped spring (ζ ≈ 0.75) — a restrained overshoot, settling in about a third of a second.
            if (height >= 0 && (Mathf.Abs(height - target) > .2f || Mathf.Abs(velocity) > .2f))
            {
                const float stiffness = 280f, damping = 25f;
                velocity += (-stiffness * (height - target) - damping * velocity) * dt;
                height += velocity * dt;
                style.height = height;
                MarkDirtyRepaint();
            }
            if (fade < 1)
            {
                fade = Mathf.Min(1, fade + dt / .22f);
                ApplyFade();
                if (fade >= 1 && outgoing != null) { outgoing.style.display = DisplayStyle.None; outgoing = null; }
            }
            float borderTarget = state == State.Working ? 0 : 1;
            if (!Mathf.Approximately(border, borderTarget)) { border = Mathf.MoveTowards(border, borderTarget, dt / .2f); MarkDirtyRepaint(); }
            if (state == State.Working)
            {
                displayedProgress = Mathf.Lerp(displayedProgress, targetProgress, 1f - Mathf.Exp(-dt * 8f));
                fill.style.width = Length.Percent(displayedProgress * 100f);
                shimmerPhase = (shimmerPhase + dt / 1.4f) % 1f;
                shimmer.style.left = Length.Percent(-30f + shimmerPhase * 130f);
            }
        }

        private void ApplyFade()
        {
            // Ease out: fast start, soft landing; the outgoing content stays visible until the incoming one has settled.
            float t = 1f - Mathf.Pow(1f - fade, 3f);
            var incoming = state == State.Idle ? idle : state == State.Working ? working : done;
            incoming.style.opacity = t;
            incoming.style.translate = new Translate(0, (1 - t) * 6f, 0);
            float scale = .98f + .02f * t;
            incoming.style.scale = new Scale(new Vector3(scale, scale, 1));
            if (outgoing != null) { outgoing.style.opacity = 1 - t; outgoing.style.translate = new Translate(0, -t * 6f, 0); }
        }

        private void Leave() { RemoveFromClassList("drag-over"); MarkDirtyRepaint(); }

        private void DrawBorder(MeshGenerationContext context)
        {
            if (border <= .01f) return;
            var p = context.painter2D;
            var color = ClassListContains("drag-over") ? Green : Dash;
            color.a *= border;
            p.strokeColor = color; p.lineWidth = 1.5f;
            var r = new Rect(3, 3, Mathf.Max(0, layout.width - 6), Mathf.Max(0, layout.height - 6));
            float radius = Mathf.Min(15, r.height / 2), left = r.xMin, top = r.yMin, right = r.xMax, bottom = r.yMax;
            void Line(Vector2 a, Vector2 b) { p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke(); }
            for (float x = left + radius; x < right - radius; x += 12) { Line(new Vector2(x, top), new Vector2(Mathf.Min(x + 6, right - radius), top)); Line(new Vector2(x, bottom), new Vector2(Mathf.Min(x + 6, right - radius), bottom)); }
            for (float y = top + radius; y < bottom - radius; y += 12) { Line(new Vector2(left, y), new Vector2(left, Mathf.Min(y + 6, bottom - radius))); Line(new Vector2(right, y), new Vector2(right, Mathf.Min(y + 6, bottom - radius))); }
            foreach (var corner in new[] { (new Vector2(left + radius, top + radius), 180f), (new Vector2(right - radius, top + radius), 270f), (new Vector2(right - radius, bottom - radius), 0f), (new Vector2(left + radius, bottom - radius), 90f) })
            { p.BeginPath(); p.Arc(corner.Item1, radius, new Angle(corner.Item2), new Angle(corner.Item2 + 90)); p.Stroke(); }
        }
    }
}
