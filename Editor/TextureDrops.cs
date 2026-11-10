using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// A texture drop on an avatar, from files to applied materials, without any UI: expand the files, import them, match
    /// them to the avatar's slots, apply the confident matches as one Undo step, and, when allowed, ask Orbiters AI about the
    /// rest (<see cref="ResolveAsync"/>). The Inspector shows its progress and results; tools and tests call it directly.
    /// </summary>
    internal static class TextureDrops
    {
        /// <summary>What Orbiters AI may still be asked after a drop's local apply: the textures left and the slots open to them.</summary>
        internal sealed class AiRequest
        {
            public string Token;
            public object Payload;
            public List<TextureEntry> Entries, Sent;
            public List<TextureSlot> Slots;
            public string Folder;
        }

        /// <summary>What a drop did.</summary>
        internal sealed class Result
        {
            public List<TextureEntry> Entries;
            public string Folder;
            /// <summary>How many textures were applied.</summary>
            public int Applied;
            /// <summary>Null when nothing is left to ask Orbiters AI (or it may not be asked).</summary>
            public AiRequest Ai;
        }

        /// <summary>
        /// Imports and applies the textures in <paramref name="paths"/> to <paramref name="avatar"/>. <paramref name="scope"/>:
        /// only the materials of these objects (the accessories an accessory drop just added). <paramref name="tail"/>: the
        /// drop's Undo step, which the textures join when nothing else was done while they were read. <paramref name="aiAllowed"/>:
        /// whether Orbiters AI may be asked about what stays unmatched (with the account's token), asked once matching is done.
        /// </summary>
        internal static async Task<Result> ImportAsync(MyAvatar avatar, string[] paths, Transform[] scope, UndoTail tail, Func<bool> aiAllowed,
            Action<float, string> progress, CancellationToken cancellation)
        {
            progress(.04f, "Finding textures…");
            var physical = TextureImport.Physical(paths);
            var files = await Orbiters.Toolkit.Editor.DedicatedTask.Run(() => TextureImport.Expand(physical), cancellation);
            cancellation.ThrowIfCancellationRequested();
            var scoped = scope?.Where(t => t).ToList() ?? new List<Transform>();
            var slots = TextureChanges.Scoped(TextureMatching.Slots(avatar), scoped);
            foreach (var slot in slots) slot.fillOnly = scoped.Count > 0;
            if (slots.Count == 0) throw new InvalidOperationException("No editable texture slots were found below this avatar.");
            string folder = "Assets/Orbiters/MyAvatar/" + Guid.NewGuid().ToString("N");
            var entries = await TextureImport.ImportAsync(files, folder, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!avatar) return null;
            progress(.78f, "Matching " + entries.Count + " textures…");
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
            var stats = TextureAnalysis.Measure(entries.Select(e => e.texture));
            TextureMatching.Match(entries, slots, stats, TextureMemory.Load(avatar));
            string token = AuthenticationService.GetAuth()?.token;
            // Only unresolved textures are sent: measured on DeepSeek, adding resolved ones never produced a
            // useful correction and made fast (non-reasoning) answers less accurate.
            // A texture whose set already has a home that lacks the slot needs a shader change, not a guess elsewhere.
            var unmatched = entries.Where(e => !e.material && !(e.reason ?? "").StartsWith(TextureMatching.MissingSlotReason, StringComparison.Ordinal)).ToList();
            // Slots filled by this drop's local matches are left out, so an answer cannot displace them. Effect renderers
            // (particles, trails) and secondary layers (detail, matcap, rim...) stay available in the manual slot menu only.
            var open = slots.Where(s => !s.secondary && s.rendererKind != "effect" && unmatched.Any(e => TextureMatching.Compatible(e.role, s) && TextureMatching.CanAutoAssign(e, s)) &&
                !entries.Any(e => e.material == s.material && e.property == s.property)).ToList();
            bool ask = unmatched.Count > 0 && open.Count > 0 && !string.IsNullOrEmpty(token) && aiAllowed != null && aiAllowed();
            // A request carries a bounded number of slots: the ones most worth asking about.
            if (ask) open = TextureAi.Relevant(open, entries);
            object payload = ask ? TextureAi.Payload(unmatched, open, stats) : null;
            progress(.9f, "Applying to " + entries.Where(e => e.material).Select(e => e.material).Distinct().Count() + " materials…");
            await Task.Yield();
            cancellation.ThrowIfCancellationRequested();
            int count = 0;
            void Apply()
            {
                Undo.RecordObject(avatar, "My Avatar: texture set");
                count = TextureChanges.Apply(avatar, entries, folder, scoped);
                if (count == 0) { avatar.undoMaterials.Clear(); avatar.canRedo = false; }
                avatar.textures = entries; avatar.batchFolder = folder; avatar.batchScope = scoped; avatar.notice = null;
                TextureMemory.Record(avatar, entries);
                TextureChanges.Dirty(avatar);
            }
            if (tail != null) tail.Join(Apply); else Apply();
            progress(1f, "Done");
            return new Result
            {
                Entries = entries, Folder = folder, Applied = count,
                Ai = ask ? new AiRequest { Token = token, Payload = payload, Entries = entries, Sent = unmatched, Slots = open, Folder = folder } : null,
            };
        }

        /// <summary>Asks Orbiters AI (replaced in tests).</summary>
        internal delegate Task<TextureAi.Result> AskAi(string token, object payload, List<TextureEntry> sent, List<TextureSlot> slots, CancellationToken cancellation);

        /// <summary>
        /// Asks Orbiters AI where the textures a drop left go, and merges its confident answers into the same batch as one
        /// extra Undo step. <paramref name="current"/> is asked once the answer is in, after <paramref name="busy"/> stops
        /// (another operation on the avatar): false drops the answer (anything edited since makes it stale, it must never
        /// overwrite a newer state). True when the answer was merged.
        /// </summary>
        internal static async Task<bool> ResolveAsync(MyAvatar avatar, AiRequest request, AskAi ask, Func<bool> busy, Func<bool> current, CancellationToken cancellation)
        {
            var before = TextureChanges.Capture(avatar, request.Slots);
            var result = await ask(request.Token, request.Payload, request.Sent, request.Slots, cancellation);
            while (busy() && !cancellation.IsCancellationRequested) await Task.Delay(50, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!avatar || !current()) return false;
            TextureChanges.Revise(avatar, request.Entries, request.Folder, result.changes, before);
            TextureMemory.Record(avatar, request.Entries);
            return true;
        }

        /// <summary>Applies the current set's pending choices again (a slot chosen by hand in the results).</summary>
        internal static void ApplyChoices(MyAvatar avatar)
        {
            TextureChanges.Apply(avatar, avatar.textures, avatar.batchFolder);
            TextureMemory.Record(avatar, avatar.textures);
            TextureChanges.Dirty(avatar);
        }
    }
}
