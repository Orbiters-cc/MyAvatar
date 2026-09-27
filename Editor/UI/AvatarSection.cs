using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // A section of the My Avatar inspector laid out like the thumbnail studio: a title with room for small actions, then
    // its content, in one card unless the section lays out its own cards.
    internal class AvatarSection : VisualElement
    {
        internal VisualElement Body { get; }
        internal VisualElement Actions { get; }

        internal AvatarSection(string title, bool card = true)
        {
            AddToClassList("avatar-section");
            var header = new VisualElement(); header.AddToClassList("avatar-section__header"); Add(header);
            var label = new Label(title); label.AddToClassList("section-title"); label.AddToClassList("avatar-section__title"); header.Add(label);
            Actions = new VisualElement(); Actions.AddToClassList("avatar-section__actions"); header.Add(Actions);
            Body = new VisualElement(); Body.AddToClassList(card ? "avatar-card" : "avatar-section__body"); Add(Body);
        }
    }
}
