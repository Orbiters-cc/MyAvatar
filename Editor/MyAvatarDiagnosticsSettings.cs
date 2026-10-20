using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// "My Avatar · Diagnostics" card of the Orbiters settings window: the opt-in for <see cref="MyAvatarIssueReporter"/>
    /// and the lowest severity it sends, with what sharing sends (the Toolkit reporter's consent text).
    /// </summary>
    [InitializeOnLoad]
    internal static class MyAvatarDiagnosticsSettings
    {
        static MyAvatarDiagnosticsSettings() => OrbitersSettingsWindow.RegisterSection("My Avatar · Diagnostics", Build);

        private static VisualElement Build()
        {
            var reporter = MyAvatarIssueReporter.Reporter;
            var box = new VisualElement();
            var row = new VisualElement();
            row.AddToClassList("orb-settings__feature");
            row.AddToClassList("orb-settings__feature--first");
            row.style.paddingTop = 0;
            box.Add(row);
            var texts = new VisualElement();
            texts.AddToClassList("orb-settings__feature-texts");
            row.Add(texts);
            var label = new Label("Share My Avatar diagnostics");
            label.AddToClassList("orb-settings__feature-label");
            texts.Add(label);
            var description = new Label(reporter.ConsentText);
            description.AddToClassList("orb-settings__feature-description");
            texts.Add(description);

            var level = new SegmentedControl(OrbitersIssueReporter.LevelNames, index => reporter.MinSeverityLevel = index + 1)
            {
                tooltip = "The least severe entry sent: Info, Warning, Error or Exception."
            };
            level.SetIndex(reporter.MinSeverityLevel - 1);
            level.style.marginTop = 4;
            level.SetEnabled(reporter.ShareIssueLogs);

            var toggle = new ToggleSwitch(reporter.ShareIssueLogs, on =>
            {
                reporter.ShareIssueLogs = on;
                level.SetEnabled(on);
            });
            toggle.AddToClassList("orb-settings__feature-toggle");
            row.Add(toggle);

            var from = new Label("Send from");
            from.AddToClassList("orb-settings__caption");
            from.style.marginTop = 2;
            box.Add(from);
            box.Add(level);
            return box;
        }
    }
}
