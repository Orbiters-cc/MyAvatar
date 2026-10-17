using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// Open in Substance: a hidden copy of the avatar, built as an upload builds it (clothing merged onto the body), posed for
    /// painting (arms level as in a T-pose, legs apart, fingers spread, the mouth open by default) and centered on Painter's
    /// symmetry plane, with copies in natural poses above, below or beside it; then the project's files and its job for
    /// Painter. Blendshapes keep the avatar's values but those listed, on the painted copy, the copies or both. The user's
    /// avatar is never posed.
    /// </summary>
    internal sealed class PainterProjectWindow : EditorWindow
    {
        private static readonly string[] StyleSheets =
        {
            "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss", "Packages/orbiters.myavatar/Editor/UI/myavatar.uss",
            "Packages/orbiters.myavatar/Editor/UI/face-tracking.uss", "Packages/orbiters.myavatar/Editor/UI/vrm.uss",
            "Packages/orbiters.myavatar/Editor/Painter/painter.uss",
        };
        private static readonly string[] Placements = { "Above", "Below", "Beside" };
        private static readonly (int Size, string Label, string Tooltip)[] Resolutions =
        {
            (0, "As the textures", "Each texture set at its texture's size (2K without one)."),
            (2048, "2K", "Every texture set at 2048 px."),
            (4096, "4K", "Every texture set at 4096 px: sharper, heavier to paint and to upload."),
        };

        [SerializeField] private MyAvatar avatar;
        private HiddenAvatarBuild build;
        private PainterPose.Rest rest;
        private readonly PainterPose.Options options = new PainterPose.Options();
        private readonly List<string> copies = new List<string>();
        private int placement, resolution;
        private List<PainterPose.Part> parts = new List<PainterPose.Part>();
        private Bounds framed;
        private bool dirty;

        private PreviewRenderUtility preview;
        private RenderTexture picture;
        private readonly OrbitCamera orbit = new OrbitCamera();
        private Image view;
        private Label status;
        private Button create;
        private VisualElement panel, shapesHost;
        private bool shapesChosen;
        private Vector2 dragFrom;
        private int dragButton = -1;
        private double lastTick;

        private const string AvatarKey = "Orbiters.MyAvatar.PainterWindow.Avatar";

        internal static void Open(MyAvatar avatar)
        {
            SessionState.SetString(AvatarKey, PainterLinkService.Id(avatar));
            var window = GetWindow<PainterProjectWindow>(true, "Open in Substance", true);
            window.minSize = new Vector2(760, 560);
            if (window.avatar != avatar)
            {
                window.Release();
                window.avatar = avatar;
                window.Rebuild();
            }
            window.Show();
        }

        private void CreateGUI() => Rebuild();

        private void OnDisable() => Release();

        private void Release()
        {
            PainterPose.Release(parts);
            parts.Clear();
            build?.Release();
            build = null;
            rest = null;
            preview?.Cleanup();
            preview = null;
            OffscreenPreview.Release(ref picture);
        }

        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear();
            foreach (string path in StyleSheets)
                if (AssetDatabase.LoadAssetAtPath<StyleSheet>(path) is StyleSheet sheet && !root.styleSheets.Contains(sheet)) root.styleSheets.Add(sheet);
            root.AddToClassList("ft");
            root.AddToClassList("painter-window");
            // Opened again by Unity (its layout restored, a window copy): the avatar it was last opened for.
            if (!avatar && GlobalObjectId.TryParse(SessionState.GetString(AvatarKey, ""), out var id))
                avatar = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as MyAvatar;
            if (!avatar) { root.Add(new Label("The avatar is gone.")); return; }

            view = new Image { scaleMode = ScaleMode.StretchToFill };
            view.AddToClassList("painter-window__view");
            view.RegisterCallback<PointerDownEvent>(OnPointerDown);
            view.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            view.RegisterCallback<PointerUpEvent>(OnPointerUp);
            view.RegisterCallback<WheelEvent>(OnWheel);
            view.RegisterCallback<GeometryChangedEvent>(_ => dirty = true);
            root.Add(view);

            // The choices scroll; the button and its state stay in view under them.
            var side = new VisualElement(); side.AddToClassList("painter-window__side"); root.Add(side);
            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList("painter-window__panel");
            side.Add(scroll);
            panel = scroll;
            var title = new Label("Open " + avatar.name + " in Substance"); title.AddToClassList("ft-hero__title"); panel.Add(title);
            var intro = new Label("A copy of the avatar, built as it uploads, posed so every part is easy to reach. Your avatar in the scene stays as it is.");
            intro.AddToClassList("ft-hero__subtitle"); panel.Add(intro);

            Heading("Pose");
            Switch("Painting pose", "Arms level as in a T-pose, legs well apart: the armpits and the inner legs open. Centered for Painter's symmetry.",
                options.Pose, on => { options.Pose = on; Repose(); });
            Switch("Spread fingers", "Straight fingers apart from each other, the thumb away from the hand.", options.Fingers, on => { options.Fingers = on; Repose(); });

            Heading("Blendshapes");
            var shapesHint = new Label("Every blendshape keeps its value on the avatar, except these: set on the painted avatar, on the copies, or both.");
            shapesHint.AddToClassList("ft-note"); panel.Add(shapesHint);
            shapesHost = new VisualElement(); shapesHost.AddToClassList("painter-shapes"); panel.Add(shapesHost);
            ShowShapes();

            Heading("Copies in natural poses");
            var hint = new Label("They paint the same textures: see what a stroke looks like on a body at rest while you paint the posed one.");
            hint.AddToClassList("ft-note"); panel.Add(hint);
            var chips = new VisualElement(); chips.AddToClassList("vrm-choices"); chips.AddToClassList("painter-window__chips"); panel.Add(chips);
            foreach (string pose in PainterPose.NaturalPoses())
            {
                string chosen = pose;
                var chip = MyAvatarEditor.PressButton(pose, null);
                chip.AddToClassList("vrm-choice");
                chip.EnableInClassList("vrm-choice--on", copies.Contains(pose));
                chip.clicked += () =>
                {
                    if (!copies.Remove(chosen)) copies.Add(chosen);
                    chip.EnableInClassList("vrm-choice--on", copies.Contains(chosen));
                    Repose();
                };
                chips.Add(chip);
            }
            Choices("Place them", Placements.Select((p, i) => (i, p, (string)null)).ToArray(), () => placement, value => { placement = value; Repose(); });

            Heading("Texture size");
            Choices(null, Resolutions, () => resolution, value => resolution = value);

            var footer = new VisualElement(); footer.AddToClassList("painter-window__footer"); side.Add(footer);
            create = MyAvatarEditor.PressButton(PainterApp.Running ? "Send to Substance" : "Open in Substance", Create);
            create.AddToClassList("ft-primary");
            footer.Add(create);
            status = new Label(); status.AddToClassList("ft-note"); footer.Add(status);

            root.schedule.Execute(Tick).Every(16);
            if (build == null) StartBuild();
            UpdateStatus();
        }

        private void Heading(string text)
        {
            var heading = new Label(text); heading.AddToClassList("opt-estimate__title"); heading.AddToClassList("painter-window__heading"); panel.Add(heading);
        }

        private void Switch(string name, string detail, bool on, Action<bool> set)
        {
            var row = new VisualElement(); row.AddToClassList("ft-feature");
            var texts = new VisualElement(); texts.AddToClassList("ft-feature__texts"); row.Add(texts);
            var label = new Label(name); label.AddToClassList("ft-feature__name"); texts.Add(label);
            var text = new Label(detail); text.AddToClassList("ft-feature__detail"); texts.Add(text);
            row.Add(new ToggleSwitch(on, set));
            panel.Add(row);
        }

        // The blendshapes set apart from the avatar's values: each with its weight and where it goes, and one to add.
        private void ShowShapes()
        {
            if (shapesHost == null) return;
            shapesHost.Clear();
            if (build == null || !build.Copy || !build.Finished)
            {
                var wait = new Label("Listed once the copy is built."); wait.AddToClassList("ft-note"); shapesHost.Add(wait);
                return;
            }
            foreach (var shape in options.Shapes.ToList())
            {
                var row = new VisualElement(); row.AddToClassList("painter-shape"); shapesHost.Add(row);
                var name = new Label(shape.Name); name.AddToClassList("painter-shape__name"); name.tooltip = shape.Renderer; row.Add(name);
                var controls = new VisualElement(); controls.AddToClassList("painter-shape__controls"); row.Add(controls);
                var weight = new IntegerField { value = Mathf.RoundToInt(shape.Weight), isDelayed = true, tooltip = "Weight, 0 to 100" };
                weight.AddToClassList("painter-shape__weight");
                weight.RegisterValueChangedCallback(evt =>
                {
                    shape.Weight = Mathf.Clamp(evt.newValue, 0, 100);
                    weight.SetValueWithoutNotify(Mathf.RoundToInt(shape.Weight));
                    Changed();
                });
                controls.Add(weight);
                Chip(controls, "Painted", "On the avatar you paint.", () => shape.Painted, on => shape.Painted = on);
                Chip(controls, "Copies", "On the copies in natural poses.", () => shape.Copies, on => shape.Copies = on);
                var remove = MyAvatarEditor.PressButton("", () => { options.Shapes.Remove(shape); Changed(true); });
                remove.AddToClassList("painter-shape__remove");
                remove.tooltip = "Keep the avatar's value";
                var cross = new VectorIcon(IconGlyph.Close); cross.AddToClassList("painter-shape__remove-icon"); remove.Add(cross);
                controls.Add(remove);
            }
            var listed = new HashSet<string>(options.Shapes.Select(s => s.Renderer + "\n" + s.Name));
            var items = PainterPose.Shapes(build.Copy).Where(s => !listed.Contains(s.Renderer + "\n" + s.Name))
                .Select(s => new KeyValuePair<string, string>(s.Renderer + "\n" + s.Name, s.Name + "  ·  " + s.RendererName)).ToList();
            var add = new SearchableDropdownField(null, "Blendshapes", items, null, key =>
            {
                int split = key.IndexOf('\n');
                options.Shapes.Add(new PainterPose.Shape { Renderer = key.Substring(0, split), Name = key.Substring(split + 1) });
                Changed(true);
            }, "Add a blendshape…");
            add.AddToClassList("painter-shapes__add");
            shapesHost.Add(add);
        }

        private void Changed(bool list = false)
        {
            shapesChosen = true;
            if (list) ShowShapes();
            Repose();
        }

        private void Chip(VisualElement parent, string text, string tooltip, Func<bool> get, Action<bool> set)
        {
            var chip = MyAvatarEditor.PressButton(text, null);
            chip.AddToClassList("vrm-choice");
            chip.AddToClassList("painter-shape__chip");
            chip.tooltip = tooltip;
            chip.EnableInClassList("vrm-choice--on", get());
            chip.clicked += () => { set(!get()); chip.EnableInClassList("vrm-choice--on", get()); Changed(); };
            parent.Add(chip);
        }

        private void Choices(string label, (int Value, string Label, string Tooltip)[] items, Func<int> get, Action<int> set)
        {
            if (label != null) { var name = new Label(label); name.AddToClassList("ft-note"); name.AddToClassList("painter-window__label"); panel.Add(name); }
            var choices = new VisualElement(); choices.AddToClassList("vrm-choices"); choices.AddToClassList("painter-window__chips"); panel.Add(choices);
            var buttons = new List<(Button, int)>();
            foreach (var (value, text, tip) in items)
            {
                int chosen = value;
                var button = MyAvatarEditor.PressButton(text, null);
                button.AddToClassList("vrm-choice");
                button.tooltip = tip;
                button.clicked += () =>
                {
                    set(chosen);
                    foreach (var (b, v) in buttons) b.EnableInClassList("vrm-choice--on", v == get());
                };
                buttons.Add((button, value));
                choices.Add(button);
            }
            foreach (var (b, v) in buttons) b.EnableInClassList("vrm-choice--on", v == get());
        }

        // ---- The copy ----

        private void StartBuild()
        {
            var root = TextureOptimization.AvatarRoot(avatar);
            // Built as it uploads (clothing merged onto the armature), without the upload's optimization: the meshes stay whole.
            build = HiddenAvatarBuild.Start(root, type => type.Name == "ParameterCompressorHook", copy =>
            {
                foreach (var own in copy.GetComponentsInChildren<MyAvatar>(true)) own.uploadOptimization.level = OptimizationLevel.Off;
            });
            build.Completed += Built;
            if (build.Finished) Built();
        }

        private void Built()
        {
            if (build == null) return;
            if (build.Error != null || !build.Copy) { UpdateStatus(); return; }
            rest = new PainterPose.Rest(build.Copy);
            // The mouth open on the painted avatar, until the list is changed.
            if (!shapesChosen) options.Shapes = PainterPose.MouthOpen(build.Copy);
            ShowShapes();
            Repose();
            framed = PainterPose.Bounds(parts.Take(MainCount));
            orbit.TargetPivot = framed.center;
            orbit.TargetDistance = orbit.Fit(framed) * 1.15f;
            // The project's space faces +Z (see PainterPose.Frame).
            orbit.LookFrom(Vector3.forward);
            orbit.Snap();
            UpdateStatus();
        }

        private int MainCount;

        // The painted copy, then each copy in a natural pose, baked where it shows.
        private void Repose()
        {
            if (rest == null || build == null || !build.Copy) return;
            PainterPose.Release(parts);
            parts = new List<PainterPose.Part>();
            var copy = build.Copy;
            rest.Restore();
            // Centered from the avatar at rest: the pose turns limbs only, the middle stays where it is.
            var frame = PainterPose.Frame(copy);
            PainterPose.Paint(copy, options);
            parts.AddRange(PainterPose.Bake(copy, frame));
            MainCount = parts.Count;
            var main = PainterPose.Bounds(parts);
            for (int i = 0; i < copies.Count; i++)
            {
                rest.Restore();
                PainterPose.Natural(copy, copies[i], options);
                float step = i + 1;
                // Above and below stay on the symmetry plane; beside, they alternate right and left.
                var offset = placement == 0 ? new Vector3(0f, main.size.y * 1.08f * step, 0f)
                    : placement == 1 ? new Vector3(0f, -main.size.y * 1.08f * step, 0f)
                    : Vector3.right * (main.size.x * .6f + main.size.x * .9f * (i / 2 + 1)) * (i % 2 == 0 ? 1f : -1f);
                parts.AddRange(PainterPose.Bake(copy, Matrix4x4.Translate(offset) * frame));
            }
            rest.Restore();
            dirty = true;
        }

        private void UpdateStatus()
        {
            if (status == null) return;
            bool ready = build != null && build.Finished && build.Error == null && parts.Count > 0;
            create?.SetEnabled(ready);
            status.RemoveFromClassList("warning");
            if (build == null) status.text = "";
            else if (!build.Finished)
                status.text = (build.Building != null ? build.Building + " builds" : "Building") + " the copy… " + build.Done + " of " + build.Total +
                              ". VRCFury freezes the editor for a few seconds.";
            else if (build.Error != null) { status.text = "The copy could not be built: " + build.Error; status.AddToClassList("warning"); }
            else status.text = PainterApp.Running
                ? "Painter is open: it makes the project as soon as you send it (it asks before closing the project it has open)."
                : "Painter opens and makes the project: the avatar, a texture set per material, its textures in a “From Unity” layer.";
        }

        // ---- Preview ----

        private void Tick()
        {
            if (build != null && !build.Finished) UpdateStatus();
            double now = EditorApplication.timeSinceStartup;
            float dt = lastTick > 0 ? (float)(now - lastTick) : 0f;
            lastTick = now;
            bool moving = parts.Count > 0 && orbit.Glide(dt, framed.size.magnitude * 1e-4f);
            if (moving || dirty) Render();
        }

        private void Render()
        {
            dirty = false;
            if (view == null || parts.Count == 0) return;
            int width = Mathf.Max(64, Mathf.RoundToInt(view.contentRect.width)), height = Mathf.Max(64, Mathf.RoundToInt(view.contentRect.height));
            if (preview == null)
            {
                preview = new PreviewRenderUtility();
                preview.camera.allowHDR = false;
            }
            OffscreenPreview.EnsureTexture(ref picture, width, height, "Painter pose");
            var camera = preview.camera;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.13f, .13f, .14f);
            var all = PainterPose.Bounds(parts);
            orbit.Apply(camera, (float)width / height, all.size.magnitude);
            var rotation = orbit.Rotation;
            preview.ambientColor = new Color(.62f, .62f, .64f);
            preview.lights[0].intensity = 1.2f;
            preview.lights[0].transform.rotation = rotation * Quaternion.Euler(25f, -30f, 0f);
            preview.lights[1].intensity = .7f;
            preview.lights[1].transform.rotation = rotation * Quaternion.Euler(0f, 140f, 0f);
            foreach (var part in parts)
                for (int i = 0; i < part.Mesh.subMeshCount && i < part.Materials.Length; i++)
                    if (part.Materials[i]) preview.DrawMesh(part.Mesh, part.ToWorld, part.Materials[i], i);
            OffscreenPreview.Render(preview, picture);
            view.image = picture;
            view.MarkDirtyRepaint();
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            dragButton = evt.button;
            dragFrom = evt.position;
            view.CapturePointer(evt.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (dragButton < 0 || !view.HasPointerCapture(evt.pointerId)) return;
            var delta = (Vector2)evt.position - dragFrom;
            dragFrom = evt.position;
            if (dragButton == 1 || dragButton == 2 || evt.shiftKey) orbit.Pan(delta, view.contentRect.height);
            else orbit.Turn(delta);
            dirty = true;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            dragButton = -1;
            if (view.HasPointerCapture(evt.pointerId)) view.ReleasePointer(evt.pointerId);
        }

        private void OnWheel(WheelEvent evt)
        {
            float fit = orbit.Fit(framed);
            orbit.Zoom(evt.delta.y, fit * .1f, Mathf.Max(orbit.Fit(PainterPose.Bounds(parts)) * 1.6f, fit * 3f));
            dirty = true;
            evt.StopPropagation();
        }

        // ---- Making the project ----

        private void Create()
        {
            if (build == null || !build.Copy || parts.Count == 0) return;
            create.SetEnabled(false);
            status.text = "Writing the avatar for Painter…";
            // The press shows at once; the files are written on the next frame.
            rootVisualElement.schedule.Execute(() =>
            {
                try
                {
                    bool plugin = PainterApp.PluginCurrent;
                    if (!plugin) PainterApp.InstallPlugin();
                    PainterLinkService.EnsureRunning();
                    bool running = PainterApp.Running;
                    PainterProjectExport.Write(avatar, parts, resolution);
                    if (!running && !PainterApp.Launch()) throw new InvalidOperationException("Substance 3D Painter wasn't found.");
                    Debug.Log("[My Avatar] Sent " + avatar.name + " to Substance 3D Painter.");
                    // A Painter started before its plugin was installed or updated runs the old one (or none) until it restarts.
                    if (running && !plugin)
                        EditorUtility.DisplayDialog("Substance 3D Painter", "My Avatar's Painter plugin was just installed or updated: restart Painter to load it. " +
                                                    "The project for " + avatar.name + " is made when it starts again.", "OK");
                    Close();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    status.text = "The project could not be prepared: " + ex.Message;
                    status.AddToClassList("warning");
                    create.SetEnabled(true);
                }
            });
        }
    }
}
