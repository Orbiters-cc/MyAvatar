using NUnit.Framework;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>
    /// My Avatar's use of Orbiters Toolkit's shared issue reporter (masking, queueing, sending and the off-by-default
    /// setting are the Toolkit's, tested there): which logs are My Avatar's and what its opt-in says.
    /// </summary>
    public sealed class MyAvatarIssueReportingTests
    {
        [Test]
        public void TheReporterIsMyAvatars()
        {
            Assert.AreEqual("My Avatar", MyAvatarIssueReporter.Reporter.ToolName);
            Assert.AreEqual("MyAvatar", MyAvatarIssueReporter.Options().SettingsKey, "The opt-in is kept where it always was.");
        }

        [Test]
        public void OnlyMyAvatarsOwnLogsAreReported()
        {
            var reporter = MyAvatarIssueReporter.Reporter;
            const string own = "UnityEngine.Debug:LogWarning (object)\nOrbiters.MyAvatar.Sample:Run () (at Packages/sample.cs:12)\nUnityEditor.EditorApplication:Internal_CallUpdateFunctions ()";
            const string other = "UnityEngine.Debug:LogWarning (object)\nSomeVendor.Tool:Run () (at Assets/x.cs:3)\nOrbiters.MyAvatar.Sample:Caller ()";
            const string test = "UnityEngine.Debug:Log (object)\nOrbiters.MyAvatar.Editor.Tests.Sample:Case ()";
            const string calledByATest = own + "\nOrbiters.MyAvatar.Editor.Tests.OptimizationTests:AFailingEstimateIsShownUntilTriedAgain ()";
            const string exception = "System.Linq.Enumerable.First[TSource] (System.Collections.Generic.IEnumerable`1[T] source)\nOrbiters.MyAvatar.Sample.Run () (at Packages/sample.cs:12)";
            const string refitThrows = "Orbiters.ReFit.Editor.ReFitEngine.Run () (at Packages/orbiters.refit/Editor/ReFitEngine.cs:40)\nOrbiters.MyAvatar.Editor.Clothing.Fit ()";
            const string vrcfuryThrows = "VF.Builder.Find () (at Packages/com.vrcfury.vrcfury/Editor/Builder.cs:3)\nOrbiters.MyAvatar.Editor.BuildHook.OnPreprocessAvatar ()";
            Assert.True(reporter.IsOwnLog(own, LogType.Warning));
            Assert.False(reporter.IsOwnLog(other, LogType.Warning));
            Assert.False(reporter.IsOwnLog(test, LogType.Log));
            Assert.False(reporter.IsOwnLog(calledByATest, LogType.Warning), "A test's deliberate failure.");
            Assert.True(reporter.IsOwnLog(exception, LogType.Exception));
            Assert.False(reporter.IsOwnLog(exception, LogType.Error), "Linq is not the caller of Debug.LogError.");
            Assert.False(reporter.IsOwnLog(refitThrows, LogType.Exception), "ReFit's exception is ReFit's to report.");
            Assert.False(reporter.IsOwnLog(vrcfuryThrows, LogType.Exception), "VRCFury's exception is not My Avatar's.");
            Assert.False(reporter.IsOwnLog("UnityEngine.Debug:Log (object)\nUnityEditor.Foo:Bar ()", LogType.Exception));
            Assert.False(reporter.IsOwnLog(string.Empty, LogType.Error));
        }

        [Test]
        public void TheOptInSaysWhatIsSent()
        {
            string text = MyAvatarIssueReporter.Reporter.ConsentText;
            StringAssert.Contains("My Avatar", text);
            StringAssert.Contains("stack traces", text);
            StringAssert.Contains("every Unity project on this computer", text);
            StringAssert.Contains("Orbiters account", text);
        }
    }
}
