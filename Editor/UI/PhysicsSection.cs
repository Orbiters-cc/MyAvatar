using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.PhysBones;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Hair, tail and toes: who can grab and pose them in VRChat and how far they stretch, set on their PhysBones.
    // Chains named like these parts that have no PhysBone can get one in a click.
    internal sealed class PhysicsSection : AvatarSection
    {
        private static readonly SegmentedControl.Option[] Choices =
        {
            new SegmentedControl.Option(PhysBoneParts.Label(PhysBoneAccess.Nobody), IconGlyph.PersonOff),
            new SegmentedControl.Option(PhysBoneParts.Label(PhysBoneAccess.OnlyMe), IconGlyph.Person),
            new SegmentedControl.Option(PhysBoneParts.Label(PhysBoneAccess.Everyone), IconGlyph.People),
        };
        private readonly MyAvatar avatar;
        private IVisualElementScheduledItem pending;

        internal PhysicsSection(MyAvatar avatar) : base("Hair, tail & toes", card: false)
        {
            this.avatar = avatar;
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
            Body.Clear();
            if (parts.Count == 0)
            {
                var card = new VisualElement(); card.AddToClassList("avatar-card");
                var empty = new Label("No hair, tail or toe bones found on this avatar. They are recognised by name, like “Hair_Front”, “Tail1” or “Toe_L”.");
                empty.AddToClassList("avatar-caption");
                card.Add(empty);
                Body.Add(card);
                return;
            }
            foreach (var part in parts) Body.Add(Card(part));
        }

        private VisualElement Card(PhysBonePartInfo info)
        {
            var card = new VisualElement(); card.AddToClassList("avatar-card"); card.AddToClassList("physics-card");
            var header = new VisualElement(); header.AddToClassList("avatar-card__header"); card.Add(header);
            var title = new Label(PhysBoneParts.Label(info.Part)); title.AddToClassList("avatar-card__title"); header.Add(title);
            if (info.PhysBones.Count > 0)
            {
                var hosts = info.PhysBones.Select(p => (Object)p.gameObject).Distinct().ToArray();
                var count = MyAvatarEditor.Button($"{info.PhysBones.Count} PhysBone{(info.PhysBones.Count == 1 ? "" : "s")}", () => { Selection.objects = hosts; EditorGUIUtility.PingObject(hosts[0]); });
                count.AddToClassList("avatar-link");
                count.tooltip = "Select these PhysBones.";
                header.Add(count);

                var physBones = info.PhysBones;
                SegmentedControl posable = null;
                ScrubDial stretch = null;
                var grab = Row(card, "Grab", "Who can grab it in VRChat.", new SegmentedControl(Choices, index =>
                {
                    var access = (PhysBoneAccess)index;
                    PhysBoneParts.SetGrabbing(physBones, access);
                    // Posing and stretching happen while grabbing: lower and lock them to match, right away.
                    if (posable.Index > index) posable.SetIndex(index);
                    Limit(posable, stretch, access);
                }));
                grab.SetIndex(info.Grabbing.HasValue ? (int)info.Grabbing.Value : -1);
                posable = Row(card, "Pose", "Who can leave it in a new pose after grabbing it.", new SegmentedControl(Choices, index => PhysBoneParts.SetPosing(physBones, (PhysBoneAccess)index)));
                posable.SetIndex(info.Posing.HasValue ? (int)info.Posing.Value : -1);
                stretch = Stretch(card, info);
                Limit(posable, stretch, info.Grabbing ?? PhysBoneAccess.Everyone);
                if (!info.Grabbing.HasValue || !info.Posing.HasValue || !info.MaxStretch.HasValue)
                {
                    var mixed = new Label("These PhysBones don’t all match; a new choice applies to all of them.");
                    mixed.AddToClassList("avatar-caption");
                    mixed.AddToClassList("physics-card__hint");
                    card.Add(mixed);
                }
            }
            if (info.Unphysicked.Count > 0) card.Add(Missing(info));
            return card;
        }

        private static void Limit(SegmentedControl posable, ScrubDial stretch, PhysBoneAccess grab)
        {
            for (int i = 0; i < Choices.Length; i++)
            {
                posable.SetOptionEnabled(i, i <= (int)grab);
                posable.SetOptionTooltip(i, i <= (int)grab ? null : "Allow grabbing for them first.");
            }
            stretch.SetEnabled(grab != PhysBoneAccess.Nobody);
        }

        // Labels line up with the stretch dial's, like Turn and Zoom in the thumbnail studio.
        private static T Row<T>(VisualElement card, string label, string tooltip, T control) where T : VisualElement
        {
            var row = new VisualElement(); row.AddToClassList("physics-row"); row.tooltip = tooltip;
            var text = new Label(label); text.AddToClassList("physics-row__label"); row.Add(text);
            control.AddToClassList("physics-row__control"); row.Add(control);
            card.Add(row);
            return control;
        }

        private ScrubDial Stretch(VisualElement card, PhysBonePartInfo info)
        {
            var physBones = info.PhysBones;
            bool mixed = !info.MaxStretch.HasValue;
            IVisualElementScheduledItem apply = null;
            ScrubDial dial = null;
            dial = new ScrubDial("Stretch", 0f, PhysBoneParts.MaxStretchLimit, 0f, 0.05f, 5, 90f, 0.03f,
                value => mixed ? "Mixed" : value < 0.025f ? "Off" : $"+{Mathf.RoundToInt(value * 100f)}%",
                value =>
                {
                    float stretch = Mathf.Round(value * 20f) / 20f;
                    if (mixed) { mixed = false; dial.SetValueWithoutNotify(value); }
                    // The readout follows the dial at once; the PhysBones are written once it rests.
                    apply?.Pause();
                    apply = dial.schedule.Execute(() => PhysBoneParts.SetMaxStretch(physBones, stretch)).StartingIn(150);
                });
            dial.SetValueWithoutNotify(Mathf.Min(info.MaxStretch ?? physBones.Average(p => p.maxStretch), PhysBoneParts.MaxStretchLimit));
            dial.tooltip = "How much longer it gets when someone pulls it. Drag the ruler; double-click for off.";
            dial.AddToClassList("physics-stretch");
            card.Add(dial);
            return dial;
        }

        private VisualElement Missing(PhysBonePartInfo info)
        {
            var row = new VisualElement(); row.AddToClassList("physics-missing");
            string noun = info.Part == PhysBonePart.Hair ? "hair" : info.Part == PhysBonePart.Tail ? "tail" : "toe";
            int n = info.Unphysicked.Count;
            var text = new Label(info.PhysBones.Count == 0
                ? $"{n} {noun} chain{(n == 1 ? " doesn’t" : "s don’t")} move yet."
                : $"{n} more {noun} chain{(n == 1 ? " doesn’t" : "s don’t")} move yet.");
            text.AddToClassList("physics-missing__text");
            text.tooltip = string.Join("\n", info.Unphysicked.Select(t => t.name));
            row.Add(text);
            var chains = info.Unphysicked.ToList();
            var add = MyAvatarEditor.Button("Add physics", () =>
            {
                var like = info.PhysBones.Count > 0 ? info : null;
                var created = PhysBoneParts.AddPhysics(avatar.transform, info.Part, chains, like);
                if (created.Count > 0) EditorGUIUtility.PingObject(created[0].gameObject);
                Refresh();
            });
            add.AddToClassList("physics-missing__button");
            add.AddToClassList("mcb-button--primary");
            add.tooltip = "Add a PhysBone to " + (n == 1 ? chains[0].name : $"these {n} chains") + ", under “" + PhysBoneParts.ContainerName + "” on the avatar.";
            row.Add(add);
            return row;
        }
    }
}
