using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat.Budget;

namespace Orbiters.MyAvatar.Editor
{
    // The avatar against VRChat's limits: parameters before compression, bones, PhysBones and contacts.
    internal sealed class BudgetSection : AvatarSection
    {
        internal BudgetSection(MyAvatar avatar) : base("Avatar budget")
        {
            var panel = new AvatarBudgetPanel(() => AvatarBudget.Estimate(avatar ? avatar.gameObject : null));
            var refresh = new IconButton(IconGlyph.Refresh, "Refresh", "Count again", panel.Refresh);
            refresh.AddToClassList("orb-icon-button--small");
            Actions.Add(refresh);
            Body.Add(panel);
        }
    }
}
