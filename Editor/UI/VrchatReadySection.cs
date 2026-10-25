using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.VrchatReady;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Make it VRChat ready: on top of the page for a VRM avatar, or any avatar without a VRChat avatar descriptor, what one
    /// click will do (read from the model, nothing changed yet), then a short "ready" card once it's done. Hidden on avatars
    /// that have a descriptor. Framed like the other one-click tools (face-tracking.uss, checks.uss).
    /// </summary>
    internal sealed class VrchatReadySection : AvatarSection
    {
        private static readonly string[] StyleSheets =
        {
            "Packages/orbiters.myavatar/Editor/UI/optimization.uss", "Packages/orbiters.myavatar/Editor/UI/checks.uss", "Packages/orbiters.myavatar/Editor/UI/vrchat-ready.uss",
        };
        private static readonly Color Glow = new Color(0f, .85f, .43f, .18f);
        // What the last conversion of each avatar did, shown until dismissed (the Inspector rebuilds often).
        private static readonly Dictionary<int, string> Done = new Dictionary<int, string>();

        private readonly MyAvatar avatar;
        private readonly VisualElement hero;
        private IVisualElementScheduledItem pending;
        private string shown, failure;

        internal VrchatReadySection(MyAvatar avatar) : base(null, card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            foreach (var path in StyleSheets)
                if (AssetDatabase.LoadAssetAtPath<StyleSheet>(path) is StyleSheet sheet) styleSheets.Add(sheet);
            AddToClassList("chk");
            AddToClassList("vr-ready");
            hero = new VisualElement(); Body.Add(hero);
            RegisterCallback<AttachToPanelEvent>(_ => { Subscribe(); Schedule(); });
            RegisterCallback<DetachFromPanelEvent>(_ => Unsubscribe());
            Refresh();
        }

        private void Subscribe()
        {
            Unsubscribe();
            EditorApplication.hierarchyChanged += Schedule; Undo.undoRedoPerformed += Schedule;
        }

        private void Unsubscribe() { EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule; }

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(400);
        }

        private int Key => avatar ? avatar.GetInstanceID() : 0;

        private void Refresh()
        {
            if (!avatar) { style.display = DisplayStyle.None; return; }
            bool ready = ReadyPlan.HasDescriptor(avatar);
            // Undone: the card offers it again.
            if (!ready) Done.Remove(Key);
            Done.TryGetValue(Key, out string summary);
            if (ready && summary == null)
            {
                style.display = DisplayStyle.None;
                shown = "hidden";
                return;
            }
            style.display = DisplayStyle.Flex;
            if (summary != null)
            {
                if (shown == "done:" + summary) return;
                shown = "done:" + summary;
                ShowDone(summary);
                return;
            }
            var plan = ReadyPlan.Of(avatar);
            string state = Signature(plan) + ":" + failure;
            if (state == shown) return;
            shown = state;
            ShowPlan(plan);
        }

        private static string Signature(ReadyPlan plan) => string.Join("|", new object[]
        {
            plan.Root ? plan.Root.GetInstanceID() : 0, plan.Problem, plan.Vrm?.Format, plan.ViewFrom, plan.VisemesFrom, plan.VisemeCount, plan.Face ? plan.Face.GetInstanceID() : 0,
            plan.EyesFrom, plan.Blink, plan.PhysBones.Count, plan.Colliders.Count, plan.Toggles.Count, plan.Remove.Count, ReadyConversion.Toggles != null,
        });

        private ToolHero Hero(string eyebrow, string eyebrowClass, string title, string subtitle)
        {
            hero.Clear();
            var frame = new ToolHero(hero, Glow, eyebrow, eyebrowClass, title, subtitle);
            frame.Frame.name = "vrchat-ready-hero";
            var ring = new VisualElement(); ring.AddToClassList("opt-badge"); frame.Badge.Add(ring);
            var icon = new VectorIcon(eyebrowClass == "warning" ? IconGlyph.Person : IconGlyph.Sparkle); icon.AddToClassList("opt-badge__icon"); ring.Add(icon);
            ring.EnableInClassList("vr-badge--warning", eyebrowClass == "warning");
            return frame;
        }

        private void ShowPlan(ReadyPlan plan)
        {
            var vrm = plan.Vrm;
            if (!plan.CanConvert)
            {
                // What to fix comes first: no button that can't be pressed.
                var stuck = Hero(vrm != null ? vrm.Format.ToUpperInvariant() + " AVATAR · NOT HUMANOID" : "NOT READY FOR VRCHAT", "warning", "Make it VRChat ready", plan.Problem);
                if (plan.ModelFile != null)
                {
                    var file = plan.ModelFile;
                    var show = MyAvatarEditor.Button("Show the model file", () => EditorGUIUtility.PingObject(file));
                    show.AddToClassList("ft-ghost");
                    show.tooltip = AssetDatabase.GetAssetPath(file);
                    stuck.Actions.Add(show);
                }
                if (!string.IsNullOrEmpty(plan.Help)) { var help = new Label(plan.Help); help.AddToClassList("ft-hint"); stuck.Frame.Add(help); }
                return;
            }
            string subtitle = vrm != null
                ? "VRChat reads none of VRM's components: one click gives the avatar VRChat's own, filled from them (descriptor, PhysBones, expressions in the menu), " +
                  "and removes the VRM ones, which VRChat doesn't allow. One Undo step."
                : "VRChat needs an avatar descriptor: one click adds it, filled from the model (view point, lip sync, eye look and blink), ready to upload. One Undo step.";
            var frame = Hero(vrm != null ? vrm.Format.ToUpperInvariant() + " AVATAR" : "NO AVATAR DESCRIPTOR", null, "Make it VRChat ready", subtitle);
            Button make = null;
            make = MyAvatarEditor.PressButton("Make it VRChat ready", () =>
            {
                make.SetEnabled(false);
                make.text = "Making it ready…";
                // The press shows at once; the work runs on the next frame.
                schedule.Execute(() => Convert(plan));
            });
            make.AddToClassList("ft-primary");
            make.tooltip = "Adds VRChat's components to “" + plan.Root.name + "”" + (plan.Remove.Count > 0 ? " and removes the VRM ones" : "") + ", in one Undo step.";
            frame.Actions.Add(make);

            var panel = new VisualElement(); panel.AddToClassList("chk-group"); frame.Frame.Add(panel);
            var head = new VisualElement(); head.AddToClassList("chk-group__head"); panel.Add(head);
            var title = new Label("What it does"); title.AddToClassList("opt-estimate__title"); head.Add(title);
            var target = new Label("on “" + plan.Root.name + "”"); target.AddToClassList("chk-group__value"); target.AddToClassList("chk-group__value--note"); head.Add(target);

            float height = plan.ViewPosition.y * Mathf.Abs(plan.Root.transform.lossyScale.y);
            Row(panel, "ok", "View point", plan.ViewFrom + ", " + height.ToString("0.00") + " m up");
            if (plan.Face != null)
                Row(panel, "ok", "Lip sync", ReadyConversion.Count(plan.VisemeCount, "viseme") + " on “" + plan.Face.name + "” from " + Lower(plan.VisemesFrom) +
                                             (plan.VisemeCount < 15 ? " (silence and closed lips stay at rest)" : ""));
            else Row(panel, "warning", "Lip sync", plan.VisemesFrom + ". Set it in the descriptor if the face has mouth shapes under other names.");
            if (plan.LeftEye != null)
            {
                string aside = Mathf.Approximately(plan.LookInner, plan.LookOuter) ? Degrees(plan.LookInner) : Degrees(Mathf.Min(plan.LookInner, plan.LookOuter)) + "–" + Degrees(Mathf.Max(plan.LookInner, plan.LookOuter));
                Row(panel, "ok", "Eye look", plan.EyesFrom + " (“" + plan.LeftEye.name + "”, “" + plan.RightEye.name + "”): up " + Degrees(plan.LookUp) + ", down " + Degrees(plan.LookDown) + ", aside " + aside);
            }
            else Row(panel, "warning", "Eye look", plan.EyesFrom + ": the eyes stay still.");
            if (plan.BlinkMesh != null) Row(panel, "ok", "Blink", "“" + plan.Blink + "” on “" + plan.BlinkMesh.name + "”");
            else Row(panel, "note", "Blink", "No blink shape found: the eyes don't blink.");
            if (vrm != null)
            {
                if (plan.PhysBones.Count > 0)
                    Row(panel, "ok", "Spring bones → PhysBones", ReadyConversion.Count(vrm.Springs.Count, "spring") + " become " + ReadyConversion.Count(plan.PhysBones.Count, "PhysBone") +
                                                                (plan.Colliders.Count > 0 ? " with " + ReadyConversion.Count(plan.Colliders.Count, "collider") : "") +
                                                                ", under “" + ReadyConversion.PhysBonesName + "”: strands hanging from one bone share one");
                if (plan.Toggles.Count > 0)
                {
                    string names = string.Join(", ", plan.Toggles.Select(t => t.Name));
                    if (ReadyConversion.Toggles != null) Row(panel, "ok", "Expressions", names + ": toggles in the menu under “" + ReadyConversion.ExpressionsMenu + "”, one at a time (VRCFury)");
                    else Row(panel, "warning", "Expressions", names + ": install VRCFury to get them in the menu; they're left out without it.");
                }
            }
            if (plan.Remove.Count > 0)
            {
                var types = plan.Remove.Where(c => c != null).Select(c => c.GetType().Name).Distinct().ToList();
                Row(panel, "ok", "VRM components", ReadyConversion.Count(plan.Remove.Count, "component") + " removed (" + string.Join(", ", types.Take(6)) + (types.Count > 6 ? "…" : "") +
                                                   "): VRChat doesn't allow them" + (vrm != null && vrm.Constraints > 0 ? ". Its " + ReadyConversion.Count(vrm.Constraints, "constraint") + " aren't converted." : ""));
            }

            var notes = new List<string>();
            if (UsesMToon(plan.Root)) notes.Add("MToon works on PC; keep UniVRM in the project, its shaders draw them. Quest needs VRChat's mobile shaders instead.");
            else notes.Add("Quest versions need VRChat's mobile shaders.");
            var note = new Label(string.Join(" ", notes)); note.AddToClassList("ft-note"); frame.Frame.Add(note);
            if (vrm != null && (vrm.Restricted || !string.IsNullOrWhiteSpace(vrm.License)))
            {
                var license = new Label((string.IsNullOrWhiteSpace(vrm.Title) ? "The model" : "“" + vrm.Title + "”") + (string.IsNullOrWhiteSpace(vrm.Authors) ? "" : " by " + vrm.Authors) +
                                        (string.IsNullOrWhiteSpace(vrm.License) ? "" : ", license: " + vrm.License) +
                                        (vrm.Restricted ? ". Its VRM license lets only its author, or people they allow, use it as an avatar: check you may upload it." : "."));
                license.AddToClassList("ft-note");
                license.EnableInClassList("warning", vrm.Restricted);
                frame.Frame.Add(license);
            }
            if (failure != null) { var error = new Label(failure); error.AddToClassList("ft-note"); error.AddToClassList("warning"); frame.Frame.Add(error); }
        }

        private void ShowDone(string summary)
        {
            var frame = Hero("VRCHAT READY", null, "Ready for VRChat",
                "Done: " + summary + " Upload it from VRChat's SDK panel (VRChat SDK › Show Control Panel). Undo (Ctrl+Z) puts it back as it was.");
            var ok = MyAvatarEditor.Button("OK", () => { Done.Remove(Key); Refresh(); });
            ok.AddToClassList("ft-ghost");
            frame.Actions.Add(ok);
            var descriptor = avatar ? avatar.GetComponentInParent<VRC.SDKBase.VRC_AvatarDescriptor>(true) : null;
            if (descriptor != null)
            {
                var show = MyAvatarEditor.Button("Show the descriptor", () => EditorGUIUtility.PingObject(descriptor.gameObject));
                show.AddToClassList("ft-ghost");
                frame.Actions.Add(show);
            }
        }

        private void Convert(ReadyPlan plan)
        {
            failure = null;
            try
            {
                // Read again: the scene may have changed since the card was drawn.
                var fresh = ReadyPlan.Of(avatar);
                var result = ReadyConversion.Apply(fresh);
                Done[Key] = result.Summary;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                failure = "It couldn't be made ready: " + ex.Message;
            }
            shown = null;
            Refresh();
        }

        private static void Row(VisualElement parent, string level, string name, string detail)
        {
            var row = new VisualElement(); row.AddToClassList("chk-issue"); parent.Add(row);
            var dot = new VisualElement(); dot.AddToClassList("chk-issue__dot"); dot.AddToClassList("chk-issue__dot--" + level); row.Add(dot);
            var texts = new VisualElement(); texts.AddToClassList("chk-issue__texts"); row.Add(texts);
            var title = new Label(name); title.AddToClassList("chk-issue__title"); texts.Add(title);
            var text = new Label(detail); text.AddToClassList("chk-issue__detail"); texts.Add(text);
        }

        private static string Degrees(float value) => Mathf.RoundToInt(value) + "°";

        private static string Lower(string text) => string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);

        private static bool UsesMToon(GameObject root) =>
            root.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Any(m => m != null && m.shader != null && m.shader.name.IndexOf("MToon", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
