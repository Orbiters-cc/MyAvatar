using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.PhysBones;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Hair, tail and toes, one card each: who can grab and pose them in VRChat and how far they stretch, set on their
    // PhysBones. Chains named like these parts that have no PhysBone can get one in a click. The cards flow into as many
    // columns as the Inspector is wide.
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

        internal PhysicsSection(MyAvatar avatar) : base(null, card: false)
        {
            this.avatar = avatar;
            Body.AddToClassList("physics-grid");
            Body.RegisterCallback<GeometryChangedEvent>(_ => LayoutColumns());
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
            Body.Clear();
            // Nothing to show when the avatar has no hair, tail or toe bones: the section simply stays out of the way.
            foreach (var part in PhysBoneParts.Scan(avatar.transform)) Body.Add(Card(part));
            style.display = Body.childCount == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            LayoutColumns();
        }

        // As many equal columns as fit, like a gallery: a card alone on the last row keeps the width of the others.
        // Each card has Gap / 2 margins and the grid a negative Gap / 2 margin, so a column takes cardWidth + Gap.
        private const float MinCardWidth = 240f, Gap = 10f;

        private void LayoutColumns()
        {
            float width = Body.contentRect.width;
            if (float.IsNaN(width) || width <= 0f) return;
            int columns = Mathf.Max(1, Mathf.Min(Body.childCount, Mathf.FloorToInt(width / (MinCardWidth + Gap))));
            float cardWidth = Mathf.Floor((width - Gap * columns) / columns);
            foreach (var card in Body.Children())
                if (!Mathf.Approximately(card.resolvedStyle.width, cardWidth)) card.style.width = cardWidth;
        }

        private VisualElement Card(PhysBonePartInfo info)
        {
            var card = new VisualElement(); card.AddToClassList("avatar-card"); card.AddToClassList("physics-card");
            var header = new VisualElement(); header.AddToClassList("avatar-card__header"); card.Add(header);
            var title = new Label(PhysBoneParts.Label(info.Part)); title.AddToClassList("physics-card__title"); header.Add(title);
            if (info.PhysBones.Count > 0)
            {
                var hosts = info.PhysBones.Select(p => (Object)p.gameObject).Distinct().ToArray();
                var count = MyAvatarEditor.Button($"{info.PhysBones.Count} PhysBone{(info.PhysBones.Count == 1 ? "" : "s")}", () => { Selection.objects = hosts; EditorGUIUtility.PingObject(hosts[0]); });
                count.AddToClassList("avatar-link");
                count.tooltip = "Select these PhysBones.";
                header.Add(count);

                var physBones = info.PhysBones;
                SegmentedControl posable = null;
                Slider stretch = null;
                var grab = Choice(card, "Grab", "Who can grab it in VRChat.", new SegmentedControl(Choices, index =>
                {
                    var access = (PhysBoneAccess)index;
                    PhysBoneParts.SetGrabbing(physBones, access);
                    // Posing and stretching happen while grabbing: lower and lock them to match, right away.
                    if (posable.Index > index) posable.SetIndex(index);
                    Limit(posable, stretch, access);
                }));
                grab.SetIndex(info.Grabbing.HasValue ? (int)info.Grabbing.Value : -1);
                posable = Choice(card, "Pose", "Who can leave it in a new pose after grabbing it.", new SegmentedControl(Choices, index => PhysBoneParts.SetPosing(physBones, (PhysBoneAccess)index)));
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

        private static void Limit(SegmentedControl posable, Slider stretch, PhysBoneAccess grab)
        {
            for (int i = 0; i < Choices.Length; i++)
            {
                posable.SetOptionEnabled(i, i <= (int)grab);
                posable.SetOptionTooltip(i, i <= (int)grab ? null : "Allow grabbing for them first.");
            }
            stretch.parent.SetEnabled(grab != PhysBoneAccess.Nobody);
        }

        private static SegmentedControl Choice(VisualElement card, string label, string tooltip, SegmentedControl control)
        {
            var text = new Label(label); text.AddToClassList("physics-card__label"); text.tooltip = tooltip; card.Add(text);
            control.AddToClassList("orb-segmented--tiles");
            control.tooltip = tooltip;
            card.Add(control);
            return control;
        }

        private static Slider Stretch(VisualElement card, PhysBonePartInfo info)
        {
            var physBones = info.PhysBones;
            var row = new VisualElement(); row.AddToClassList("physics-stretch");
            row.tooltip = "How much longer it gets when someone pulls it, relative to its length.";
            var label = new Label("Stretch"); label.AddToClassList("physics-card__label"); label.AddToClassList("physics-stretch__label"); row.Add(label);
            var slider = new Slider(0f, PhysBoneParts.MaxStretchLimit); row.Add(slider);
            var value = new Label(); value.AddToClassList("physics-stretch__value"); row.Add(value);
            float current = info.MaxStretch ?? physBones.Average(p => p.maxStretch);
            slider.SetValueWithoutNotify(Mathf.Min(current, PhysBoneParts.MaxStretchLimit));
            value.text = StretchText(info.MaxStretch.HasValue ? current : (float?)null);
            IVisualElementScheduledItem apply = null;
            slider.RegisterValueChangedCallback(evt =>
            {
                // The number follows the handle at once; the PhysBones are written once the handle rests.
                float stretch = Mathf.Round(evt.newValue * 20f) / 20f;
                value.text = StretchText(stretch);
                apply?.Pause();
                apply = slider.schedule.Execute(() => PhysBoneParts.SetMaxStretch(physBones, stretch)).StartingIn(150);
            });
            card.Add(row);
            return slider;
        }

        private static string StretchText(float? stretch) =>
            !stretch.HasValue ? "Mixed" : stretch.Value < 0.025f ? "Off" : $"+{Mathf.RoundToInt(stretch.Value * 100f)}%";

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
