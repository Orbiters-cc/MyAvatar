using System;
using System.IO;
using System.Linq;
using Orbiters.MyAvatar.Editor.Vrm;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Export to VRM: a .vrm file of the avatar as it uploads (VRCFury, Modular Avatar and MCB built, the Optimization level
    /// applied) for VRM apps, in VRM 0.x or 1.0, with what the file says about the model (its meta). Export waits for the
    /// exporter to confirm the model's license allows it. Needs UniVRM (one package per VRM version).
    /// </summary>
    internal sealed class VrmSection : AvatarSection
    {
        private static readonly string[] StyleSheets =
            { "Packages/orbiters.myavatar/Editor/UI/optimization.uss", "Packages/orbiters.myavatar/Editor/UI/vrm.uss" };
        private static readonly Color Glow = new Color(0f, .85f, .43f, .18f);

        private static readonly (int Version, string Name, string Sub, string Tooltip)[] Versions =
        {
            (0, "v0.x", "VSeeFace, most apps", "VRM 0.x: what most apps read (VSeeFace, VNyan, Warudo, cluster...). Springs collide with spheres only."),
            (1, "v1.0", "Current standard", "VRM 1.0: the current standard, for apps that read it. Springs per chain, capsule and plane colliders."),
        };

        private static readonly (string Name, string Detail, Func<VrmExportSettings, bool> Get, Action<VrmExportSettings, bool> Set)[] Permissions =
        {
            ("Commercial use", "Others may use the model to make money.", s => s.commercialUse, (s, v) => s.commercialUse = v),
            ("Violent use", "Others may use the model in violent scenes.", s => s.violentUse, (s, v) => s.violentUse = v),
            ("Sexual use", "Others may use the model in sexual scenes.", s => s.sexualUse, (s, v) => s.sexualUse = v),
            ("Redistribution", "Others may pass the file on.", s => s.redistribution, (s, v) => s.redistribution = v),
            ("Modification", "Others may change the model.", s => s.modification, (s, v) => s.modification = v),
        };

        private static readonly string[] Users = { "Only you", "People you allow", "Everyone" };

        // The running or last export of each avatar: the Inspector rebuilds often.
        private static readonly System.Collections.Generic.Dictionary<int, VrmExport.Job> Jobs = new System.Collections.Generic.Dictionary<int, VrmExport.Job>();

        private readonly MyAvatar avatar;
        private readonly VisualElement hero;
        private Label status;
        private Button export;
        private IVisualElementScheduledItem poll;

        internal VrmSection(MyAvatar avatar) : base("Export to VRM", card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            foreach (var path in StyleSheets)
                if (AssetDatabase.LoadAssetAtPath<StyleSheet>(path) is StyleSheet sheet) styleSheets.Add(sheet);
            AddToClassList("opt");
            hero = new VisualElement(); Body.Add(hero);
            RegisterCallback<AttachToPanelEvent>(_ => { poll = schedule.Execute(UpdateStatus).Every(300); });
            RegisterCallback<DetachFromPanelEvent>(_ => poll?.Pause());
            Show();
        }

        private VrmExportSettings Settings => avatar.vrmExport;

        private VrmExport.Job Job => avatar && Jobs.TryGetValue(avatar.GetInstanceID(), out var job) ? job : null;

        private void Show()
        {
            hero.Clear();
            if (!avatar) return;
            var writer = VrmExport.Writer(Settings.version);
            string uniVrm = VrmExport.Writers.Select(w => w.UniVrm).FirstOrDefault(v => v != null);
            var frame = new ToolHero(hero, Glow, VrmExport.Writers.Count == 0 ? "NEEDS UNIVRM" : "UNIVRM " + uniVrm, VrmExport.Writers.Count == 0 ? "warning" : null,
                "Export to VRM",
                "A .vrm file of the avatar as it uploads (VRCFury, Modular Avatar and MCB built, your Optimization level applied) for VSeeFace, Warudo, " +
                "VNyan and other VRM apps: MToon materials, springs from its PhysBones, visemes, blink and the expressions its face has.");
            frame.Frame.name = "vrm-hero";
            var ring = new VisualElement(); ring.AddToClassList("opt-badge"); frame.Badge.Add(ring);
            var icon = new VectorIcon(IconGlyph.Download); icon.AddToClassList("opt-badge__icon"); ring.Add(icon);

            var tiles = new VisualElement(); tiles.AddToClassList("ft-trackers"); tiles.AddToClassList("opt-levels"); frame.Frame.Add(tiles);
            foreach (var (version, name, sub, tip) in Versions)
            {
                int chosen = version;
                var tile = MyAvatarEditor.Button("", () => Change("My Avatar: VRM version", s => s.version = chosen));
                tile.AddToClassList("ft-tracker"); tile.AddToClassList("opt-level"); tile.AddToClassList("vrm-version");
                tile.EnableInClassList("ft-tracker--on", Settings.version == version);
                tile.SetEnabled(VrmExport.Writer(version) != null);
                tile.tooltip = VrmExport.Writer(version) != null ? tip : tip + " Needs UniVRM's " + (version == 0 ? "com.vrmc.univrm" : "com.vrmc.vrm") + " package.";
                var label = new Label(name); label.AddToClassList("ft-tracker__name"); label.pickingMode = PickingMode.Ignore; tile.Add(label);
                var detail = new Label(sub); detail.AddToClassList("ft-tracker__sub"); detail.pickingMode = PickingMode.Ignore; tile.Add(detail);
                tiles.Add(tile);
            }

            var form = new VisualElement(); form.AddToClassList("ft-custom"); form.AddToClassList("vrm-form"); frame.Frame.Add(form);
            var title = new Label("What the file says"); title.AddToClassList("opt-estimate__title"); form.Add(title);
            Field(form, "Name", Settings.title, TextureOptimization.AvatarRoot(avatar).name, v => Settings.title = v);
            Field(form, "Version", Settings.modelVersion, "1.0", v => Settings.modelVersion = v);
            Field(form, "Authors", Settings.authors, "Required: your name, the base's creator…", v => Settings.authors = v);
            Field(form, "Contact", Settings.contact, "Optional", v => Settings.contact = v);
            Field(form, "Reference", Settings.reference, "Optional: the base's page", v => Settings.reference = v);

            var users = new VisualElement(); users.AddToClassList("vrm-row"); form.Add(users);
            var usersLabel = new Label("Who can use it"); usersLabel.AddToClassList("vrm-row__label"); users.Add(usersLabel);
            var choices = new VisualElement(); choices.AddToClassList("vrm-choices"); users.Add(choices);
            for (int i = 0; i < Users.Length; i++)
            {
                int value = i;
                var choice = MyAvatarEditor.Button(Users[i], () => Change("My Avatar: VRM permissions", s => s.allowedUsers = value));
                choice.AddToClassList("vrm-choice");
                choice.EnableInClassList("vrm-choice--on", Settings.allowedUsers == i);
                choices.Add(choice);
            }
            foreach (var (name, detail, get, set) in Permissions)
                Switch(form, name, detail, get(Settings), on => Change("My Avatar: VRM permissions", s => set(s, on)));
            var license = Switch(form, "The model's license allows VRM", "Many bases forbid converting them to other formats: check the terms you bought it under. " +
                                 "The permissions above start at the most restrictive.", Settings.licenseAllows, on => Change("My Avatar: VRM license", s => s.licenseAllows = on));
            license.AddToClassList("vrm-license");

            var actions = new VisualElement(); actions.AddToClassList("vrm-actions"); frame.Frame.Add(actions);
            export = MyAvatarEditor.Button("Export…", null);
            export.AddToClassList("ft-primary");
            export.clicked += Export;
            actions.Add(export);
            status = new Label(); status.AddToClassList("ft-note"); status.AddToClassList("vrm-status"); actions.Add(status);
            UpdateStatus();
        }

        private void Field(VisualElement parent, string label, string value, string placeholder, Action<string> set)
        {
            var row = new VisualElement(); row.AddToClassList("vrm-row"); parent.Add(row);
            var name = new Label(label); name.AddToClassList("vrm-row__label"); row.Add(name);
            var field = new TextField { value = value ?? "", isDelayed = true };
            field.AddToClassList("vrm-field");
            var hint = new Label(placeholder); hint.AddToClassList("vrm-field__hint"); hint.pickingMode = PickingMode.Ignore; field.Add(hint);
            hint.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.Flex : DisplayStyle.None;
            field.RegisterValueChangedCallback(evt =>
            {
                hint.style.display = string.IsNullOrEmpty(evt.newValue) ? DisplayStyle.Flex : DisplayStyle.None;
                Change("My Avatar: VRM " + label.ToLowerInvariant(), s => set(evt.newValue), redraw: false);
            });
            row.Add(field);
        }

        private static VisualElement Switch(VisualElement parent, string name, string detail, bool on, Action<bool> set)
        {
            var row = new VisualElement(); row.AddToClassList("ft-feature");
            var texts = new VisualElement(); texts.AddToClassList("ft-feature__texts"); row.Add(texts);
            var label = new Label(name); label.AddToClassList("ft-feature__name"); texts.Add(label);
            var text = new Label(detail); text.AddToClassList("ft-feature__detail"); texts.Add(text);
            row.Add(new ToggleSwitch(on, set));
            parent.Add(row);
            return row;
        }

        // Settings change at once and with Undo.
        private void Change(string name, Action<VrmExportSettings> edit, bool redraw = true)
        {
            if (!avatar) return;
            Undo.RecordObject(avatar, name);
            edit(Settings);
            EditorUtility.SetDirty(avatar);
            PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
            if (redraw) Show(); else UpdateStatus();
        }

        // What keeps Export off, the export running, or the file it wrote.
        private void UpdateStatus()
        {
            if (status == null || !avatar) return;
            var job = Job;
            string missing = VrmExport.Writer(Settings.version) == null ? "UniVRM isn't installed for this version."
                : string.IsNullOrWhiteSpace(Settings.authors) ? "Add the authors."
                : !Settings.licenseAllows ? "Confirm the model's license allows VRM." : null;
            bool running = job != null && !job.Done;
            export.SetEnabled(missing == null && !running);
            status.RemoveFromClassList("warning");
            if (running)
            {
                var build = job.Build;
                status.text = build == null || !build.Finished
                    ? (build?.Building != null ? build.Building + " builds" : "Building") + " the avatar… " + (build != null ? build.Done + " of " + build.Total : "")
                    : "Writing the VRM…";
            }
            else if (missing != null) status.text = missing;
            else if (job?.Error != null) { status.text = "The export stopped: " + job.Error; status.AddToClassList("warning"); }
            else if (job != null) status.text = "Saved " + Path.GetFileName(job.Path) + " (" + VrcLoadingBar.Format(job.Bytes) + ")";
            else status.text = "Builds a hidden copy first, as an upload does: VRCFury freezes the editor for a few seconds.";
            status.tooltip = job != null && job.Done && job.Error == null ? job.Path : null;
        }

        private void Export()
        {
            var root = TextureOptimization.AvatarRoot(avatar);
            string name = string.IsNullOrWhiteSpace(Settings.title) ? root.name : Settings.title;
            string path = EditorUtility.SaveFilePanel("Export to VRM", "", MakeFileName(name), "vrm");
            if (string.IsNullOrEmpty(path)) return;
            // The press shows at once; the build starts on the next frame.
            export.SetEnabled(false);
            status.text = "Building the avatar…";
            schedule.Execute(() =>
            {
                var job = VrmExport.Start(avatar, Settings.version, path);
                Jobs[avatar.GetInstanceID()] = job;
                job.Completed += () => { UpdateStatus(); if (job.Error == null) EditorUtility.RevealInFinder(job.Path); };
                UpdateStatus();
            });
        }

        private static string MakeFileName(string name) => new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()) + ".vrm";
    }
}
