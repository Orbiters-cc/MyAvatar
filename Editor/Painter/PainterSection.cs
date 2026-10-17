using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// Substance 3D Painter in the My Avatar Inspector (shown when Painter is installed): Open in Substance (a project made
    /// from the avatar, posed for painting), the state of the link with the Painter project, and which material each of its
    /// texture sets paints.
    /// </summary>
    internal sealed class PainterSection : AvatarSection
    {
        internal const string Feature = "myavatar.painter";
        private static readonly string[] StyleSheets =
            { "Packages/orbiters.myavatar/Editor/UI/optimization.uss", "Packages/orbiters.myavatar/Editor/UI/vrm.uss", "Packages/orbiters.myavatar/Editor/Painter/painter.uss" };
        private static readonly Color Glow = new Color(.93f, .30f, .20f, .2f);
        private static readonly (int Size, string Label, string Tooltip)[] Sizes =
        {
            (512, "512", "Lightest: strokes show fastest, blurry up close."),
            (1024, "1K", "Strokes show within a second, sharp enough for most of the avatar."),
            (2048, "2K", "Sharp up close; each image takes Painter a little longer."),
        };

        [InitializeOnLoadMethod]
        private static void Register() => OrbitersFeatures.Register(new OrbitersFeature
        {
            Key = Feature, Product = "My Avatar", Label = "Substance 3D Painter link",
            Description = "Open the avatar in Substance 3D Painter, see what you paint on it live, and get the textures applied when you save in Painter.",
            Stage = FeatureStage.Beta, Default = true,
        });

        private readonly MyAvatar avatar;
        private readonly VisualElement hero;
        private bool queued;
        // What the last Open the project did (until Painter links the project).
        private string note;
        private bool noteWarning;

        internal PainterSection(MyAvatar avatar) : base("Substance Painter", card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            foreach (var path in StyleSheets)
                if (AssetDatabase.LoadAssetAtPath<StyleSheet>(path) is StyleSheet sheet) styleSheets.Add(sheet);
            AddToClassList("opt");
            AddToClassList("painter");
            var badge = new StageBadge(FeatureStage.Beta); badge.AddToClassList("painter__stage"); Actions.Add(badge);
            hero = new VisualElement(); Body.Add(hero);
            RegisterCallback<AttachToPanelEvent>(_ => { PainterLinkService.Changed += Queue; OrbitersFeatures.Changed += FeatureChanged; });
            RegisterCallback<DetachFromPanelEvent>(_ => { PainterLinkService.Changed -= Queue; OrbitersFeatures.Changed -= FeatureChanged; });
            Show();
        }

        private void FeatureChanged(string key) { if (key == Feature) Show(); }

        // Link events come in bursts (one per live image): one redraw per frame at most.
        private void Queue()
        {
            if (queued) return;
            queued = true;
            schedule.Execute(() => { queued = false; Show(); });
        }

        private void Show()
        {
            hero.Clear();
            bool shown = avatar && PainterApp.Installed && OrbitersFeatures.IsEnabled(Feature) && !EditorUtility.IsPersistent(avatar);
            style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (!shown) return;
            PainterLinkService.EnsureRunning();
            var session = PainterLinkService.For(avatar);
            bool plugin = PainterApp.PluginCurrent;
            string eyebrow = session != null ? "LINKED · " + session.ProjectName.ToUpperInvariant() : plugin ? "NOT LINKED" : "PAINTER PLUGIN NOT INSTALLED";
            var frame = new ToolHero(hero, Glow, eyebrow, session != null ? "painter-linked" : plugin ? "muted" : "warning",
                "Paint in Substance 3D Painter",
                "Strokes show on the avatar here as you paint, and saving in Painter applies the textures, with Undo. Open in Substance makes a project " +
                "from the avatar, posed for painting, with its textures.");
            frame.Frame.name = "painter-hero";
            var ring = new VisualElement(); ring.AddToClassList("opt-badge"); frame.Badge.Add(ring);
            var icon = new VectorIcon(IconGlyph.Brush); icon.AddToClassList("opt-badge__icon"); ring.Add(icon);

            // The avatar's project first when it has one (and isn't linked now); a new one otherwise.
            bool project = session == null && !string.IsNullOrEmpty(avatar.painter.projectPath) && File.Exists(avatar.painter.projectPath);
            if (project)
            {
                var reopen = MyAvatarEditor.PressButton("Open the project", () =>
                {
                    if (!PainterApp.PluginCurrent) PainterApp.InstallPlugin();
                    PainterLinkService.EnsureRunning();
                    (bool opening, string message) = PainterApp.OpenProject(avatar);
                    note = message;
                    noteWarning = !opening;
                    Show();
                });
                reopen.AddToClassList("ft-primary");
                reopen.tooltip = "Opens " + Path.GetFileName(avatar.painter.projectPath) + " in Painter: it links to this avatar on its own.";
                frame.Actions.Add(reopen);
            }
            var open = MyAvatarEditor.PressButton(project ? "New project…" : "Open in Substance", () => PainterProjectWindow.Open(avatar));
            if (!project) open.AddToClassList("ft-primary");
            open.tooltip = "Choose a pose for painting, then Painter opens a new project made from the avatar and its textures.";
            frame.Actions.Add(open);
            if (!plugin)
            {
                var install = MyAvatarEditor.PressButton(File.Exists(PainterApp.PluginPath) ? "Update the Painter plugin" : "Install the Painter plugin", InstallPlugin);
                install.tooltip = "Copies My Avatar's plugin into Painter's startup folder (" + PainterApp.PluginPath + "). Painter loads it when it starts.";
                frame.Actions.Add(install);
            }

            var sets = TextureSets(session);
            if (sets.Count > 0) Mapping(frame.Frame, session, sets);

            var footer = new VisualElement(); footer.AddToClassList("painter-footer"); frame.Frame.Add(footer);
            if (session != null) note = null;
            var status = new Label(note ?? Status(session, plugin)); status.AddToClassList("ft-note"); footer.Add(status);
            if (session?.Error == true || note != null && noteWarning) status.AddToClassList("warning");
            if (session != null)
            {
                var buttons = new VisualElement(); buttons.AddToClassList("painter-buttons"); footer.Add(buttons);
                var apply = MyAvatarEditor.PressButton("Apply all now", session.RequestCommit);
                apply.tooltip = "Painter exports every texture set's maps and Unity applies them, as when you save in Painter.";
                buttons.Add(apply);
                if (session.LiveSets.Any())
                {
                    var own = MyAvatarEditor.PressButton("Show the avatar's textures", session.ClearPreview);
                    own.tooltip = "Takes the live images off until the next stroke.";
                    buttons.Add(own);
                }
            }
        }

        private void InstallPlugin()
        {
            try
            {
                PainterApp.InstallPlugin();
                if (PainterApp.Running)
                    EditorUtility.DisplayDialog("Substance 3D Painter", "My Avatar's plugin is installed. Restart Painter to load it: its My Avatar panel then links projects to avatars.", "OK");
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Substance 3D Painter", "The plugin could not be installed: " + ex.Message, "OK");
            }
            Show();
        }

        private string Status(PainterLinkService.Session session, bool plugin)
        {
            if (session != null) return session.State;
            if (!plugin) return "Install the plugin, then open the avatar in Painter, or link a project from Painter's My Avatar panel.";
            bool project = !string.IsNullOrEmpty(avatar.painter.projectPath) && File.Exists(avatar.painter.projectPath);
            if (project) return "Open " + Path.GetFileName(avatar.painter.projectPath) + " in Painter: it links to this avatar on its own.";
            if (PainterApp.Running) return "Painter is running: link a project to " + avatar.name + " from its My Avatar panel (Window menu), or open the avatar in Substance.";
            return "Open the avatar in Substance to paint it.";
        }

        // The project's texture sets while it is linked; the ones mapped earlier otherwise.
        private List<string> TextureSets(PainterLinkService.Session session) =>
            session != null ? session.Structure.textureSets.Select(s => s.name).ToList() : avatar.painter.sets.Select(s => s.textureSet).ToList();

        private void Mapping(VisualElement parent, PainterLinkService.Session session, List<string> sets)
        {
            var form = new VisualElement(); form.AddToClassList("ft-custom"); form.AddToClassList("vrm-form"); form.AddToClassList("painter-map"); parent.Add(form);
            var title = new Label("Texture sets"); title.AddToClassList("opt-estimate__title"); form.Add(title);
            var choices = PainterMapping.Choices(avatar);
            var labels = new List<string> { "Nothing on this avatar" };
            labels.AddRange(choices.Select(c => c.Label));
            var live = new HashSet<string>(session?.LiveSets ?? Enumerable.Empty<string>());
            foreach (string set in sets)
            {
                var row = new VisualElement(); row.AddToClassList("vrm-row"); form.Add(row);
                var name = new Label(set); name.AddToClassList("vrm-row__label"); name.AddToClassList("painter-map__set"); row.Add(name);
                if (live.Contains(set)) { var dot = new VisualElement(); dot.AddToClassList("painter-map__live"); dot.tooltip = "Shown live"; name.Add(dot); }
                var material = PainterMapping.Material(avatar, set);
                int index = material ? choices.FindIndex(c => c.Material == material) + 1 : 0;
                var field = new DropdownField(labels, Mathf.Max(0, index));
                field.AddToClassList("vrm-field");
                field.AddToClassList("painter-map__field");
                field.RegisterValueChangedCallback(evt =>
                {
                    int chosen = labels.IndexOf(evt.newValue);
                    PainterMapping.Set(avatar, set, chosen > 0 ? choices[chosen - 1] : null);
                    session?.SendMapping();
                });
                row.Add(field);
            }
            var sizes = new VisualElement(); sizes.AddToClassList("vrm-row"); form.Add(sizes);
            var label = new Label("Live images"); label.AddToClassList("vrm-row__label"); sizes.Add(label);
            var options = new VisualElement(); options.AddToClassList("vrm-choices"); sizes.Add(options);
            foreach (var (size, text, tip) in Sizes)
            {
                int chosen = size;
                var choice = MyAvatarEditor.PressButton(text, () =>
                {
                    Undo.RecordObject(avatar, "My Avatar: Painter live images");
                    avatar.painter.previewSize = chosen;
                    EditorUtility.SetDirty(avatar);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
                    session?.SendMapping();
                    Show();
                });
                choice.AddToClassList("vrm-choice");
                choice.EnableInClassList("vrm-choice--on", avatar.painter.previewSize == size);
                choice.tooltip = tip;
                options.Add(choice);
            }
        }
    }
}
