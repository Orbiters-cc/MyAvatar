using System;
using System.Collections.Generic;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// A set of strings kept for the editor session (in <see cref="SessionState"/>): unlike a static field, it survives
    /// script reloads, so "once per session" stays once per session after a recompile. Bounded: past
    /// <see cref="Limit"/> entries the oldest go. Main thread only.
    /// </summary>
    internal sealed class SessionSet
    {
        internal const int Limit = 4096;
        private readonly string key;
        private List<string> order;
        private HashSet<string> members;

        internal SessionSet(string key) { this.key = key; }

        private void Load()
        {
            if (members != null) return;
            string saved = SessionState.GetString(key, "");
            order = new List<string>(saved.Length == 0 ? Array.Empty<string>() : saved.Split('\n'));
            members = new HashSet<string>(order, StringComparer.Ordinal);
        }

        internal bool Contains(string value) { Load(); return members.Contains(value); }

        /// <summary>Adds <paramref name="value"/>; false when it was already in.</summary>
        internal bool Add(string value)
        {
            Load();
            if (value == null || value.IndexOf('\n') >= 0 || !members.Add(value)) return false;
            order.Add(value);
            if (order.Count > Limit) { members.Remove(order[0]); order.RemoveAt(0); }
            SessionState.SetString(key, string.Join("\n", order));
            return true;
        }

        internal bool Contains(int value) => Contains(value.ToString());
        internal bool Add(int value) => Add(value.ToString());
    }
}
