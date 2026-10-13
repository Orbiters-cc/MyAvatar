using System;
using UnityEditor;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// An Undo step that work finishing later (after an await) may join, so one Undo takes it all back: it joins only while
    /// nothing else was done since (Unity starts a new Undo group for every action of the user), else it stays its own
    /// step. An edit made meanwhile is never folded into it.
    /// </summary>
    internal sealed class UndoTail
    {
        private readonly int group;
        private int next;

        /// <param name="group">The step to join.</param>
        /// <param name="next">The current Undo group right after that step was closed.</param>
        internal UndoTail(int group, int next) { this.group = group; this.next = next; }

        /// <summary>A tail for the step <paramref name="group"/>, closed just now.</summary>
        internal static UndoTail After(int group) => new UndoTail(group, Undo.GetCurrentGroup());

        /// <summary>Whether work run now would join the step.</summary>
        internal bool Joins => Undo.GetCurrentGroup() == next;

        /// <summary>Runs <paramref name="work"/> (synchronous) and makes it part of the step when nothing came in between.</summary>
        internal void Join(Action work)
        {
            bool joins = Joins;
            // Not joining: a step of its own, never added to the user's last one.
            if (!joins) Undo.IncrementCurrentGroup();
            int own = Undo.GetCurrentGroup();
            work();
            Undo.CollapseUndoOperations(joins ? group : own);
            // Closed again: what follows is not part of it unless it joins too.
            Undo.IncrementCurrentGroup();
            next = Undo.GetCurrentGroup();
        }
    }
}
