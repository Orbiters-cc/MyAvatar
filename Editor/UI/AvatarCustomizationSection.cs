using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.PhysBones;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // The avatar's own options in one frame: who can grab, pose and stretch its hair, ears, tail and toes (their PhysBones),
    // one row each with an "All" row on top, and its looks when it has them (Rexouium's feather groups and ear size).
    // Rows are rebuilt only when the PhysBones or chains themselves change; any other edit (an Undo of a choice) updates
    // the rows in place so nothing jumps.
    internal sealed class AvatarCustomizationSection : AvatarSection
    {
        private readonly MyAvatar avatar;
        private readonly VisualElement physics, looks;
        private IVisualElementScheduledItem pending;
        private string structure;
        private readonly List<Action<List<PhysBonePartInfo>>> updates = new List<Action<List<PhysBonePartInfo>>>();

        internal AvatarCustomizationSection(MyAvatar avatar) : base("Avatar customization", card: false)
        {
            this.avatar = avatar;
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.myavatar/Editor/UI/avatar-customization.uss");
            if (sheet) styleSheets.Add(sheet);
            var frame = new VisualElement(); frame.AddToClassList("custom"); Body.Add(frame);
            var glow = new FaceTrackingGlow(new Color(.54f, .49f, 1f, .16f)); glow.AddToClassList("custom__glow"); glow.pickingMode = PickingMode.Ignore; frame.Add(glow);
            physics = new VisualElement(); physics.AddToClassList("custom__physics"); frame.Add(physics);
            looks = new VisualElement(); looks.AddToClassList("custom__looks"); frame.Add(looks);
            BuildLooks();
            EditorApplication.hierarchyChanged += Schedule;
            Undo.undoRedoPerformed += Schedule;
            RegisterCallback<DetachFromPanelEvent>(_ => { EditorApplication.hierarchyChanged -= Schedule; Undo.undoRedoPerformed -= Schedule; });
            Refresh();
        }

        private void Schedule()
        {
            pending?.Pause();
            pending = schedule.Execute(Refresh).StartingIn(300);
        }

        private void Refresh()
        {
            if (!avatar) return;
            var parts = PhysBoneParts.Scan(avatar.transform);
            string current = string.Join("|", parts.Select(p =>
                p.Part + ":" + string.Join(",", p.PhysBones.Select(b => b.GetInstanceID())) + ":" + string.Join(",", p.Unphysicked.Select(t => t.GetInstanceID()))));
            if (current != structure)
            {
                structure = current;
                BuildPhysics(parts);
            }
            foreach (var update in updates) update(parts);
            physics.style.display = parts.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            style.display = parts.Count == 0 && looks.childCount == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ---- Physics ----

        private void BuildPhysics(List<PhysBonePartInfo> parts)
        {
            physics.Clear(); updates.Clear();
            var header = new VisualElement(); header.AddToClassList("custom__columns"); physics.Add(header);
            Column(header, "", "custom__name");
            Column(header, "Grab", "custom__column--access").tooltip = "Who can grab it in VRChat.";
            Column(header, "Pose", "custom__column--access").tooltip = "Who can leave it in a new pose after grabbing it.";
            Column(header, "Stretch", "custom__stretch-title").tooltip = "How much longer it gets when someone pulls it, relative to its length.";
            var withBones = parts.Where(p => p.PhysBones.Count > 0).ToList();
            if (withBones.Count > 1) Row(physics, null, "All", "Every part at once.", () => Merge(withBones),
                current => Merge(current.Where(p => p.PhysBones.Count > 0).ToList()), all: true);
            foreach (var info in parts)
            {
                var part = info.Part;
                if (info.PhysBones.Count > 0)
                    Row(physics, info, PhysBoneParts.Label(part), null, () => info, current => current.FirstOrDefault(p => p.Part == part));
                if (info.Unphysicked.Count > 0) physics.Add(Missing(info));
            }
        }

        private static Label Column(VisualElement header, string text, string className)
        {
            var label = new Label(text); label.AddToClassList("custom__column"); label.AddToClassList(className); header.Add(label);
            return label;
        }

        // The parts' values together: a value only when every part has the same.
        private static PhysBonePartInfo Merge(List<PhysBonePartInfo> parts)
        {
            if (parts.Count == 0) return null;
            var merged = new PhysBonePartInfo();
            merged.PhysBones.AddRange(parts.SelectMany(p => p.PhysBones));
            merged.Grabbing = parts.All(p => p.Grabbing == parts[0].Grabbing) ? parts[0].Grabbing : null;
            merged.Posing = parts.All(p => p.Posing == parts[0].Posing) ? parts[0].Posing : null;
            merged.MaxStretch = parts.All(p => p.MaxStretch.HasValue && Mathf.Approximately(p.MaxStretch.Value, parts[0].MaxStretch ?? -1f)) ? parts[0].MaxStretch : null;
            return merged;
        }

        // One part's row (or the "All" row): <paramref name="target"/> gives the PhysBones a choice changes.
        private void Row(VisualElement parent, PhysBonePartInfo info, string label, string tooltip, Func<PhysBonePartInfo> target,
                         Func<List<PhysBonePartInfo>, PhysBonePartInfo> find, bool all = false)
        {
            var row = new VisualElement(); row.AddToClassList("custom__row"); if (all) row.AddToClassList("custom__row--all"); parent.Add(row);
            var name = new VisualElement(); name.AddToClassList("custom__name"); row.Add(name);
            var title = new Label(label); title.AddToClassList("custom__part"); name.Add(title);
            if (tooltip != null) title.tooltip = tooltip;
            if (info != null)
            {
                // The PhysBone count selects them.
                var count = MyAvatarEditor.Button(info.PhysBones.Count.ToString(), () =>
                {
                    var hosts = info.PhysBones.Where(b => b != null).Select(b => (UnityEngine.Object)b.gameObject).Distinct().ToArray();
                    if (hosts.Length == 0) return;
                    Selection.objects = hosts; EditorGUIUtility.PingObject(hosts[0]);
                });
                count.AddToClassList("custom__count");
                count.tooltip = $"{info.PhysBones.Count} PhysBone{(info.PhysBones.Count == 1 ? "" : "s")}: click to select them.";
                name.Add(count);
            }
            AccessPicker grab = null, pose = null;
            grab = new AccessPicker(access =>
            {
                PhysBoneParts.SetGrabbing(target().PhysBones, access);
                // Posing happens while grabbing: lowered and locked to match, right away.
                if (pose.Index > (int)access) { pose.Index = (int)access; PhysBoneParts.SetPosing(target().PhysBones, access); }
                pose.Limit((int)access);
                Schedule();
            });
            pose = new AccessPicker(access => { PhysBoneParts.SetPosing(target().PhysBones, access); Schedule(); });
            row.Add(grab); row.Add(pose);
            var stretch = new StretchControl(value => { PhysBoneParts.SetMaxStretch(target().PhysBones, value); Schedule(); });
            row.Add(stretch);
            updates.Add(parts =>
            {
                var current = find(parts);
                if (current == null) return;
                grab.Index = current.Grabbing.HasValue ? (int)current.Grabbing.Value : -1;
                pose.Index = current.Posing.HasValue ? (int)current.Posing.Value : -1;
                int limit = current.Grabbing.HasValue ? (int)current.Grabbing.Value : (int)PhysBoneAccess.Everyone;
                pose.Limit(limit);
                stretch.SetEnabled(limit != (int)PhysBoneAccess.Nobody);
                stretch.Show(current.MaxStretch, current.PhysBones.Count > 0 ? current.PhysBones.Where(b => b != null).Select(b => b.maxStretch).DefaultIfEmpty(0f).Average() : 0f);
                row.tooltip = !current.Grabbing.HasValue || !current.Posing.HasValue || !current.MaxStretch.HasValue
                    ? (all ? "The parts" : "These PhysBones") + " don't all match: a new choice applies to all of them." : null;
            });
        }

        private VisualElement Missing(PhysBonePartInfo info)
        {
            var row = new VisualElement(); row.AddToClassList("custom__missing");
            string noun = PhysBoneParts.Noun(info.Part);
            int n = info.Unphysicked.Count;
            var text = new Label(info.PhysBones.Count == 0
                ? $"{PhysBoneParts.Label(info.Part)}: {n} chain{(n == 1 ? " doesn't" : "s don't")} move yet."
                : $"{n} more {noun} chain{(n == 1 ? " doesn't" : "s don't")} move yet.");
            text.AddToClassList("custom__missing-text");
            text.tooltip = string.Join("\n", info.Unphysicked.Select(t => t.name));
            row.Add(text);
            var chains = info.Unphysicked.ToList();
            var add = MyAvatarEditor.Button("Add physics", null);
            add.clicked += () =>
            {
                // The row answers at once; the PhysBones are made on the next frame.
                add.SetEnabled(false); add.text = "Adding…";
                schedule.Execute(() =>
                {
                    var created = PhysBoneParts.AddPhysics(avatar.transform, info.Part, chains, info.PhysBones.Count > 0 ? info : null);
                    if (created.Count > 0) EditorGUIUtility.PingObject(created[0].gameObject);
                    Refresh();
                });
            };
            add.AddToClassList("custom__missing-button");
            add.tooltip = "Add a PhysBone to " + (n == 1 ? chains[0].name : $"these {n} chains") + ", under “" + PhysBoneParts.ContainerName + "” on the avatar.";
            row.Add(add);
            return row;
        }

        // ---- Looks ----

        private void BuildLooks()
        {
            looks.Clear();
            var options = RexouiumOptions.Find(TextureOptimization.AvatarRoot(avatar));
            if (!options.Any) { looks.style.display = DisplayStyle.None; return; }
            var title = new Label("Looks"); title.AddToClassList("custom__subtitle"); looks.Add(title);
            if (options.Feathers.Count > 0)
            {
                var line = new VisualElement(); line.AddToClassList("custom__chips-line"); looks.Add(line);
                var label = new Label("Feathers"); label.AddToClassList("custom__chips-label"); line.Add(label);
                var chips = new VisualElement(); chips.AddToClassList("custom__chips"); line.Add(chips);
                foreach (var group in options.Feathers)
                {
                    var item = group;
                    var chip = MyAvatarEditor.Button("", null);
                    chip.AddToClassList("custom__chip");
                    chip.tooltip = "Show the " + item.Label.ToLowerInvariant() + " feathers: what the avatar starts with in VRChat (its “" + item.Parameter + "” menu toggle), shown in the scene too.";
                    var check = new VectorIcon(IconGlyph.Check); check.AddToClassList("custom__chip-check"); chip.Add(check);
                    var text = new Label(item.Label); text.AddToClassList("custom__chip-text"); text.pickingMode = PickingMode.Ignore; chip.Add(text);
                    void Show(bool on) => chip.EnableInClassList("custom__chip--on", on);
                    Show(RexouiumOptions.Shown(options, item));
                    // The chip turns at once; the menu default and the scene follow.
                    chip.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) Show(!chip.ClassListContains("custom__chip--on")); }, TrickleDown.TrickleDown);
                    chip.clicked += () => { bool on = chip.ClassListContains("custom__chip--on"); RexouiumOptions.SetShown(options, item, on); };
                    chips.Add(chip);
                }
            }
            if (options.Ears != null) looks.Add(Ears(options));
        }

        private static VisualElement Ears(RexouiumOptions.Options options)
        {
            var row = new VisualElement(); row.AddToClassList("custom__slider-row"); row.tooltip = "Ear size: smaller to the left, bigger to the right (EarsSmall and EarsBig).";
            var label = new Label("Ear size"); label.AddToClassList("custom__chips-label"); row.Add(label);
            var track = new VisualElement(); track.AddToClassList("custom__centered"); row.Add(track);
            var center = new VisualElement(); center.AddToClassList("custom__center-tick"); center.pickingMode = PickingMode.Ignore; track.Add(center);
            var slider = new Slider(-100f, 100f); slider.SetValueWithoutNotify(RexouiumOptions.EarSize(options)); track.Add(slider);
            var value = new Label(EarText(slider.value)); value.AddToClassList("custom__value"); row.Add(value);
            IVisualElementScheduledItem apply = null;
            Action write = null;
            void Flush() { apply?.Pause(); var pending = write; write = null; pending?.Invoke(); }
            slider.RegisterValueChangedCallback(evt =>
            {
                // The number follows the handle at once; the blendshapes are written once it rests.
                float size = Mathf.Round(evt.newValue);
                value.text = EarText(size);
                write = () => RexouiumOptions.SetEarSize(options, size);
                apply?.Pause();
                apply = slider.schedule.Execute(Flush).StartingIn(60);
            });
            slider.RegisterCallback<PointerUpEvent>(_ => Flush(), TrickleDown.TrickleDown);
            slider.RegisterCallback<DetachFromPanelEvent>(_ => Flush());
            // Double-click back to the sculpted size.
            slider.RegisterCallback<MouseDownEvent>(e => { if (e.clickCount == 2) { slider.value = 0f; Flush(); } }, TrickleDown.TrickleDown);
            return row;
        }

        private static string EarText(float size) => Mathf.Abs(size) < .5f ? "0" : (size > 0 ? "+" : "") + Mathf.RoundToInt(size) + "%";

        // ---- Controls ----

        // Nobody, Only me, Everyone as three icons; -1 shows none (the PhysBones disagree). Options above the limit are locked.
        private sealed class AccessPicker : VisualElement
        {
            private static readonly IconGlyph[] Glyphs = { IconGlyph.PersonOff, IconGlyph.Person, IconGlyph.People };
            private readonly Button[] options = new Button[3];
            private int index = -1;

            public AccessPicker(Action<PhysBoneAccess> chosen)
            {
                AddToClassList("custom__access");
                for (int i = 0; i < options.Length; i++)
                {
                    int choice = i;
                    var option = new Button(() =>
                    {
                        if (!options[choice].enabledSelf) return;
                        chosen((PhysBoneAccess)choice);
                    });
                    option.AddToClassList("custom__access-option");
                    option.tooltip = PhysBoneParts.Label((PhysBoneAccess)i);
                    option.Add(new VectorIcon(Glyphs[i]));
                    // Shown chosen on press; the PhysBones change on release.
                    option.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0 && option.enabledSelf) Index = choice; }, TrickleDown.TrickleDown);
                    options[i] = option;
                    Add(option);
                }
            }

            public int Index
            {
                get => index;
                set
                {
                    index = value;
                    for (int i = 0; i < options.Length; i++) options[i].EnableInClassList("custom__access-option--on", i == value);
                    // None chosen: the PhysBones disagree.
                    EnableInClassList("custom__access--mixed", value < 0);
                    tooltip = value < 0 ? "These don't all match: a choice applies to all of them." : null;
                }
            }

            public void Limit(int highest)
            {
                for (int i = 0; i < options.Length; i++)
                {
                    options[i].SetEnabled(i <= highest);
                    options[i].tooltip = PhysBoneParts.Label((PhysBoneAccess)i) + (i <= highest ? "" : ": allow grabbing for them first.");
                }
            }
        }

        // A slider and its value; the PhysBones are written once the handle rests, when it is let go, or when the row goes.
        private sealed class StretchControl : VisualElement
        {
            private readonly Slider slider;
            private readonly Label value;
            private IVisualElementScheduledItem apply;
            private Action write;

            public StretchControl(Action<float> changed)
            {
                AddToClassList("custom__stretch");
                slider = new Slider(0f, PhysBoneParts.MaxStretchLimit); Add(slider);
                value = new Label(); value.AddToClassList("custom__value"); Add(value);
                slider.RegisterValueChangedCallback(evt =>
                {
                    float stretch = Mathf.Round(evt.newValue * 20f) / 20f;
                    value.text = Text(stretch);
                    write = () => changed(stretch);
                    apply?.Pause();
                    apply = slider.schedule.Execute(Flush).StartingIn(150);
                });
                slider.RegisterCallback<PointerUpEvent>(_ => Flush(), TrickleDown.TrickleDown);
                RegisterCallback<DetachFromPanelEvent>(_ => Flush());
            }

            private void Flush()
            {
                apply?.Pause();
                var pending = write; write = null;
                pending?.Invoke();
            }

            public void Show(float? stretch, float average)
            {
                // Left alone while dragged or about to be written.
                if (write != null) return;
                slider.SetValueWithoutNotify(Mathf.Min(stretch ?? average, PhysBoneParts.MaxStretchLimit));
                value.text = Text(stretch);
            }

            private static string Text(float? stretch) =>
                !stretch.HasValue ? "Mixed" : stretch.Value < 0.025f ? "Off" : $"+{Mathf.RoundToInt(stretch.Value * 100f)}%";
        }
    }
}
