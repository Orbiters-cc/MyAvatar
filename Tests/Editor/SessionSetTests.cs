using NUnit.Framework;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor.Tests
{
    // "Once per session" sets kept in SessionState (review bug 2): a new instance, as after a script reload, still knows
    // what was added, and the set stays bounded with the oldest entries going first.
    public sealed class SessionSetTests
    {
        private const string Key = "Orbiters.MyAvatar.Tests.SessionSet";

        [TearDown] public void TearDown() => SessionState.EraseString(Key);

        [Test] public void ASetSurvivesANewInstanceAndStaysBounded()
        {
            SessionState.EraseString(Key);
            var set = new SessionSet(Key);
            Assert.That(set.Add("a.png"), Is.True);
            Assert.That(set.Add("a.png"), Is.False);
            Assert.That(set.Add(12), Is.True);
            var again = new SessionSet(Key);
            Assert.That(again.Contains("a.png"), Is.True);
            Assert.That(again.Contains(12), Is.True);
            Assert.That(again.Contains("b.png"), Is.False);
            for (int i = 0; i < SessionSet.Limit; i++) again.Add("f" + i);
            var last = new SessionSet(Key);
            Assert.That(last.Contains("a.png"), Is.False, "the oldest went");
            Assert.That(last.Contains("f" + (SessionSet.Limit - 1)), Is.True);
        }
    }
}
