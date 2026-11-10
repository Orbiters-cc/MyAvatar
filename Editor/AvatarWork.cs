using System;
using System.Collections.Generic;
using Orbiters.Toolkit.Editor;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// What is working on an avatar now, in one place (it was three lock sets: the Inspector's, the accessory drop's and the
    /// gallery's, each asked separately). Three kinds of work hold an avatar:
    /// <list type="bullet">
    /// <item><see cref="Kind.Inspector"/>: one operation of the avatar's Inspector at a time (a texture drop, an accessory
    /// drop as the Inspector shows it, Undo, Save);</item>
    /// <item><see cref="Kind.Accessory"/>: one accessory drop job at a time, also one resumed after a domain reload without an
    /// Inspector (it runs inside the Inspector's operation when started there, hence a kind of its own);</item>
    /// <item><see cref="Kind.Gallery"/>: gallery installations running (several at once: their downloads overlap).</item>
    /// </list>
    /// Main thread only. Holds are released by disposing them; the script reload forgets them with the work they guarded.
    /// </summary>
    internal static class AvatarWork
    {
        internal enum Kind { Inspector, Accessory, Gallery }

        private sealed class Held
        {
            public MyAvatar avatar;
            public readonly int[] counts = new int[3];
            public bool Any => counts[0] + counts[1] + counts[2] > 0;
        }

        private static readonly Dictionary<int, Held> Avatars = new Dictionary<int, Held>();

        /// <summary>Raised the editor frame after the last work on an avatar ended (queued drops start then).</summary>
        internal static event Action<MyAvatar> Freed;

        /// <summary>
        /// Holds <paramref name="avatar"/> for one work of <paramref name="kind"/>; null when one of that kind already holds it
        /// (<see cref="Kind.Gallery"/> is shared: never null).
        /// </summary>
        internal static IDisposable TryEnter(MyAvatar avatar, Kind kind)
        {
            if (!avatar) return null;
            int id = avatar.GetInstanceID();
            if (!Avatars.TryGetValue(id, out var held)) Avatars[id] = held = new Held { avatar = avatar };
            if (kind != Kind.Gallery && held.counts[(int)kind] > 0) return null;
            held.counts[(int)kind]++;
            return new Hold(id, kind);
        }

        /// <summary>Whether any work holds the avatar.</summary>
        internal static bool Busy(MyAvatar avatar) => avatar && Avatars.TryGetValue(avatar.GetInstanceID(), out var held) && held.Any;

        /// <summary>Whether work of <paramref name="kind"/> holds the avatar.</summary>
        internal static bool Holds(MyAvatar avatar, Kind kind) =>
            avatar && Avatars.TryGetValue(avatar.GetInstanceID(), out var held) && held.counts[(int)kind] > 0;

        /// <summary>Whether an accessory drop or a gallery installation is adding something to the avatar.</summary>
        internal static bool Installing(MyAvatar avatar) => Holds(avatar, Kind.Accessory) || Holds(avatar, Kind.Gallery);

        private sealed class Hold : IDisposable
        {
            private readonly int id;
            private readonly Kind kind;
            private bool released;

            public Hold(int id, Kind kind) { this.id = id; this.kind = kind; }

            public void Dispose()
            {
                if (released) return;
                released = true;
                if (!Avatars.TryGetValue(id, out var held)) return;
                held.counts[(int)kind]--;
                if (held.Any) return;
                Avatars.Remove(id);
                var avatar = held.avatar;
                // Next frame: whoever waits starts after the work that just ended has fully unwound.
                if (Freed != null) OrbitersEditorHost.Once("MyAvatar.AvatarWork.Freed", () => { if (avatar && !Busy(avatar)) Freed?.Invoke(avatar); }, 0, HostWhen.Always);
            }
        }
    }
}
