using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// The frame a one-click tool is presented in (face tracking, GoGoLoco): a glowing badge for its logo, an eyebrow, a title
    /// with an optional version pill, a subtitle and its actions; under it, the credit to the tool's author. Styles:
    /// face-tracking.uss.
    /// </summary>
    internal sealed class ToolHero
    {
        private const string StyleSheetPath = "Packages/orbiters.myavatar/Editor/UI/face-tracking.uss";
        public readonly VisualElement Frame, Top, Badge, Actions;

        internal ToolHero(VisualElement parent, Color glow, string eyebrow, string eyebrowClass, string title, string subtitle, string version = null)
        {
            Frame = new VisualElement(); Frame.AddToClassList("ft-hero"); parent.Add(Frame);
            Top = new VisualElement(); Top.AddToClassList("ft-hero__top"); Frame.Add(Top);
            // The glow sits behind what the tool puts in the badge, inside the frame.
            Badge = new VisualElement(); Badge.AddToClassList("ft-hero__badge"); Top.Add(Badge);
            var light = new FaceTrackingGlow(glow); light.AddToClassList("ft-hero__glow"); Badge.Add(light);
            var texts = new VisualElement(); texts.AddToClassList("ft-hero__texts"); Top.Add(texts);
            var eye = new Label(eyebrow); eye.AddToClassList("ft-hero__eyebrow"); if (eyebrowClass != null) eye.AddToClassList(eyebrowClass); texts.Add(eye);
            var titleRow = new VisualElement(); titleRow.AddToClassList("ft-hero__title-row"); texts.Add(titleRow);
            var head = new Label(title); head.AddToClassList("ft-hero__title"); titleRow.Add(head);
            if (!string.IsNullOrEmpty(version)) { var pill = new Label(version); pill.AddToClassList("ft-hero__version"); titleRow.Add(pill); }
            if (!string.IsNullOrEmpty(subtitle)) { var sub = new Label(subtitle); sub.AddToClassList("ft-hero__subtitle"); texts.Add(sub); }
            Actions = new VisualElement(); Actions.AddToClassList("ft-hero__actions"); texts.Add(Actions);
        }

        /// <summary>Gives <paramref name="section"/> the frame's styles.</summary>
        internal static void Style(VisualElement section)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet) section.styleSheets.Add(sheet);
            section.AddToClassList("ft");
        }

        /// <summary>The credit line under a tool's frame: "<paramref name="caption"/> <paramref name="name"/>", the name linking to <paramref name="url"/>.</summary>
        internal static void Credit(VisualElement parent, string caption, string name, string url, string tooltip)
        {
            var credit = new VisualElement(); credit.AddToClassList("ft-credit"); parent.Add(credit);
            var text = new Label(caption); text.AddToClassList("avatar-caption"); credit.Add(text);
            var link = MyAvatarEditor.Button(name, () => Application.OpenURL(url));
            link.AddToClassList("avatar-link"); link.AddToClassList("ft-credit__link");
            link.tooltip = tooltip;
            credit.Add(link);
        }
    }
}
