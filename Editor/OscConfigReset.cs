using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// The switch for Toolkit's reset of VRChat's OSC config at upload (<see cref="VrchatOscConfig"/>): Orbiters settings >
    /// Features > My Avatar, last of its features, on by default (kept in the user's EditorPrefs).
    /// </summary>
    [InitializeOnLoad]
    internal static class OscConfigReset
    {
        static OscConfigReset() => OrbitersFeatures.Register(new OrbitersFeature
        {
            Key = VrchatOscConfig.Feature, Product = "My Avatar", Label = "Reset VRChat's OSC config at upload",
            PrefKey = "Orbiters.MyAvatar.ResetOscConfig",
            Description = "After an upload, the OSC config VRChat saved for the avatar goes, for every VRChat account on this computer, so " +
                          "OSC apps (face tracking, Orbiters - N4N0) reach its new parameters. VRChat writes it again the next time it loads the avatar.",
            Stage = FeatureStage.Stable, Default = true, Order = 1000,
        });
    }
}
