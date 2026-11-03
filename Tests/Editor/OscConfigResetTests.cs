using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;

// My Avatar's switch for Toolkit's OSC config reset at upload: in the settings window under My Avatar, last, on by default,
// and off it stops the reset (Toolkit's VrchatOscConfigTests check what the reset deletes).
public sealed class OscConfigResetTests
{
    [Test] public void TheSwitchIsMyAvatarsLastOnByDefault()
    {
        var feature = OrbitersFeatures.Find(VrchatOscConfig.Feature);
        Assert.IsNotNull(feature, "My Avatar registers the switch");
        Assert.AreEqual("My Avatar", feature.Product);
        Assert.IsTrue(feature.Default);
        Assert.AreEqual(FeatureStage.Stable, feature.Stage);
        var mine = OrbitersFeatures.ByProduct().Single(p => p.Product == "My Avatar").Features;
        Assert.AreSame(feature, mine.Last(), "it shows below My Avatar's other features");
    }

    [Test] public void OffStopsTheReset()
    {
        var feature = OrbitersFeatures.Find(VrchatOscConfig.Feature);
        bool had = EditorPrefs.HasKey(feature.PrefKey), before = EditorPrefs.GetBool(feature.PrefKey, feature.Default);
        try
        {
            OrbitersFeatures.SetEnabled(VrchatOscConfig.Feature, false);
            Assert.IsFalse(VrchatOscConfig.Enabled);
            Assert.IsNull(VrchatOscConfig.AfterUpload(System.IO.Path.GetTempPath(), "avtr_00000000-0000-0000-0000-000000000001"));
            OrbitersFeatures.SetEnabled(VrchatOscConfig.Feature, true);
            Assert.IsTrue(VrchatOscConfig.Enabled);
        }
        finally
        {
            if (had) EditorPrefs.SetBool(feature.PrefKey, before); else EditorPrefs.DeleteKey(feature.PrefKey);
        }
    }
}
