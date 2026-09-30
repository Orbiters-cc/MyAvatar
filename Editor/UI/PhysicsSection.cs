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
    // Hair, ears, tail and toes, one card each: who can grab and pose them in VRChat and how far they stretch, set on their
    // PhysBones. Chains named like these parts that have no PhysBone can get one in a click. The cards flow into as many
    // columns as the Inspector is wide. Cards are rebuilt only when the PhysBones or chains themselves change; any other
    // edit (a component added elsewhere, an Undo of a choice) updates the cards in place so nothing jumps.
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
        private string structure;
        private readonly List<Action<PhysBonePartInfo>> updates = new List<Action<PhysBonePartInfo>>();

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
            var parts = PhysBoneParts.Scan(avatar.transform);
            string current = Structure(parts);
            if (current == structure)
            {
                for (int i = 0; i < parts.Count; i++) updates[i](parts[i]);
                return;
            }
            structure = current;
            Body.Clear(); updates.Clear();
            // Nothing to show when the avatar has no hair, ear, tail or toe bones: the section simply stays out of the way.
            foreach (var part in parts) Body.Add(Card(part));
            style.display = Body.childCount == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            LayoutColumns();
        }

        // Which parts, PhysBones and chains without physics there are; the values shown on the cards are left out.
        private static string Structure(List<PhysBonePartInfo> parts) => string.Join("|", parts.Select(p =>
            p.Part + ":" + string.Join(",", p.PhysBones.Select(b => b.GetInstanceID())) + ":" + string.Join(",", p.Unphysicked.Select(t => t.GetInstanceID()))));

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
                var hosts = info.PhysBones.Select(p => (UnityEngine.Object)p.gameObject).Distinct().ToArray();
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
                posable = Choice(card, "Pose", "Who can leave it in a new pose after grabbing it.", new SegmentedControl(Choices, index => PhysBoneParts.SetPosing(physBones, (PhysBoneAccess)index)));
                stretch = Stretch(card, info, out var showStretch);
                var mixed = new Label("These PhysBones don’t all match; a new choice applies to all of them.");
                mixed.AddToClassList("avatar-caption");
                mixed.AddToClassList("physics-card__hint");
                card.Add(mixed);
                void Show(PhysBonePartInfo current)
                {
                    // Same index: the highlight stays put, so only what really changed moves.
                    int grabIndex = current.Grabbing.HasValue ? (int)current.Grabbing.Value : -1, poseIndex = current.Posing.HasValue ? (int)current.Posing.Value : -1;
                    if (grab.Index != grabIndex) grab.SetIndex(grabIndex);
                    if (posable.Index != poseIndex) posable.SetIndex(poseIndex);
                    showStretch(current);
                    Limit(posable, stretch, current.Grabbing ?? PhysBoneAccess.Everyone);
                    mixed.style.display = !current.Grabbing.HasValue || !current.Posing.HasValue || !current.MaxStretch.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
                }
                Show(info);
                updates.Add(Show);
            }
            else updates.Add(_ => { });
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

        private static Slider Stretch(VisualElement card, PhysBonePartInfo info, out Action<PhysBonePartInfo> show)
        {
            var physBones = info.PhysBones;
            var row = new VisualElement(); row.AddToClassList("physics-stretch");
            row.tooltip = "How much longer it gets when someone pulls it, relative to its length.";
            var label = new Label("Stretch"); label.AddToClassList("physics-card__label"); label.AddToClassList("physics-stretch__label"); row.Add(label);
            var slider = new Slider(0f, PhysBoneParts.MaxStretchLimit); row.Add(slider);
            var value = new Label(); value.AddToClassList("physics-stretch__value"); row.Add(value);
            IVisualElementScheduledItem apply = null;
            Action write = null;
            // Writes the value waiting for the handle to rest, at once: also when the handle is let go, and when the card goes
            // away first (the Inspector closed, the cards rebuilt), so no edit is lost.
            void Flush()
            {
                apply?.Pause();
                var pending = write; write = null;
                pending?.Invoke();
            }
            show = current =>
            {
                // Leave the handle alone while it is being dragged or its value is about to be written.
                if (write != null) return;
                float stretch = current.MaxStretch ?? current.PhysBones.Average(p => p.maxStretch);
                slider.SetValueWithoutNotify(Mathf.Min(stretch, PhysBoneParts.MaxStretchLimit));
                value.text = StretchText(current.MaxStretch.HasValue ? stretch : (float?)null);
            };
            slider.RegisterValueChangedCallback(evt =>
            {
                // The number follows the handle at once; the PhysBones are written once the handle rests.
                float stretch = Mathf.Round(evt.newValue * 20f) / 20f;
                value.text = StretchText(stretch);
                write = () => PhysBoneParts.SetMaxStretch(physBones, stretch);
                apply?.Pause();
                apply = slider.schedule.Execute(Flush).StartingIn(150);
            });
            slider.RegisterCallback<PointerUpEvent>(_ => Flush(), TrickleDown.TrickleDown);
            slider.RegisterCallback<DetachFromPanelEvent>(_ => Flush());
            card.Add(row);
            return slider;
        }

        private static string StretchText(float? stretch) =>
            !stretch.HasValue ? "Mixed" : stretch.Value < 0.025f ? "Off" : $"+{Mathf.RoundToInt(stretch.Value * 100f)}%";

        private VisualElement Missing(PhysBonePartInfo info)
        {
            var row = new VisualElement(); row.AddToClassList("physics-missing");
            string noun = PhysBoneParts.Noun(info.Part);
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
