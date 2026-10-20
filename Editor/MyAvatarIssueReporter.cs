using Orbiters.Toolkit.Editor;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// My Avatar's opt-in diagnostics (off by default): its own warnings and errors, sent by Orbiters Toolkit's shared
    /// reporter, which masks them and bounds what is sent. Its card is <see cref="MyAvatarDiagnosticsSettings"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class MyAvatarIssueReporter
    {
        internal const string ToolName = "My Avatar";

        internal static IssueReporterOptions Options() => new IssueReporterOptions
        {
            ToolName = ToolName,
            SettingsKey = "MyAvatar",
            Namespaces = new[] { "Orbiters.MyAvatar." },
            TestNamespaces = new[] { "Orbiters.MyAvatar.Editor.Tests" }
        };

        // Registered as the editor loads, so what My Avatar logs while it starts is not missed.
        internal static readonly OrbitersIssueReporter Reporter = OrbitersIssueReporter.Register(Options());

        static MyAvatarIssueReporter()
        {
        }
    }
}
