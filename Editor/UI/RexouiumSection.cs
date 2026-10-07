using Orbiters.Toolkit.Editor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Rexouium's own options (RexouiumOptions): its feather groups and ear size, shown only on avatars that have them.
    internal sealed class RexouiumSection : AvatarSection
    {
        internal RexouiumSection(MyAvatar avatar) : base("Rexouium")
        {
            var options = RexouiumOptions.Find(TextureOptimization.AvatarRoot(avatar));
            if (!options.Any) { style.display = DisplayStyle.None; return; }
            if (options.Feathers.Count > 0)
            {
                var label = new Label("Feathers"); label.AddToClassList("physics-card__label"); Body.Add(label);
                var grid = new VisualElement(); grid.AddToClassList("rexouium__feathers"); Body.Add(grid);
                foreach (var group in options.Feathers)
                {
                    var item = group;
                    var row = new VisualElement(); row.AddToClassList("rexouium__feather");
                    row.tooltip = "Show the " + item.Label.ToLowerInvariant() + " feathers: what the avatar starts with in VRChat (its “" + item.Parameter + "” menu toggle), shown in the scene too.";
                    row.Add(new ToggleSwitch(RexouiumOptions.Shown(options, item), on => RexouiumOptions.SetShown(options, item, on)));
                    var name = new Label(item.Label); name.AddToClassList("rexouium__feather-name"); row.Add(name);
                    grid.Add(row);
                }
            }
            if (options.Ears != null) Body.Add(Ears(options));
        }

        private static VisualElement Ears(RexouiumOptions.Options options)
        {
            var row = new VisualElement(); row.AddToClassList("physics-stretch"); row.tooltip = "Ear size: smaller to the left, bigger to the right (EarsSmall and EarsBig).";
            var label = new Label("Ears"); label.AddToClassList("physics-card__label"); label.AddToClassList("physics-stretch__label"); row.Add(label);
            var slider = new Slider(-100f, 100f); slider.SetValueWithoutNotify(RexouiumOptions.EarSize(options)); row.Add(slider);
            var value = new Label(EarText(slider.value)); value.AddToClassList("physics-stretch__value"); row.Add(value);
            IVisualElementScheduledItem apply = null;
            System.Action write = null;
            void Flush() { apply?.Pause(); var pending = write; write = null; pending?.Invoke(); }
            slider.RegisterValueChangedCallback(evt =>
            {
                // The ears follow the handle at once in the number; the blendshapes are written once it rests.
                float size = Mathf.Round(evt.newValue);
                value.text = EarText(size);
                write = () => RexouiumOptions.SetEarSize(options, size);
                apply?.Pause();
                apply = slider.schedule.Execute(Flush).StartingIn(60);
            });
            slider.RegisterCallback<PointerUpEvent>(_ => Flush(), TrickleDown.TrickleDown);
            slider.RegisterCallback<DetachFromPanelEvent>(_ => Flush());
            return row;
        }

        private static string EarText(float size) => Mathf.Abs(size) < .5f ? "0" : (size > 0 ? "+" : "") + Mathf.RoundToInt(size) + "%";
    }
}
