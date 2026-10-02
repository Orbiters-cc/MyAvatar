using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // Clothes and accessories on an avatar with a known custom base (its MCB component says which, or Orbiters recognises
    // its model): which ones lack the custom base's blendshapes, the question each needs, and the refits the answers start.
    // The checks run in the background; the refits run through ReFit, installed the first time the user asks for one.
    internal static class AccessoryFit
    {
        internal const string Tool = "My Avatar";
        internal const string Ask = "ask", AddShapes = "shapes", Refit = "refit", Place = "place", Install = "install", Done = "done", Failed = "failed";

        // Last refit of each accessory this session, for the commission window.
        private static readonly Dictionary<int, RefitBatchResult> Results = new Dictionary<int, RefitBatchResult>();
        // Avatars whose accessories were checked against a custom base this session.
        private static readonly HashSet<string> Scanned = new HashSet<string>();

        internal static bool IsQuestion(MyAvatar.AccessoryNote note) => note != null && (note.fit == Ask || note.fit == AddShapes || note.fit == Refit);

        internal static string DismissKey(OrbitersAttachment attachment, string baseKey) =>
            GlobalObjectId.GetGlobalObjectIdSlow(attachment) + "|" + baseKey;

        /// <summary>True once per avatar and custom base: the accessories already on it are then checked once.</summary>
        internal static bool FirstLook(MyAvatar avatar, CustomBaseState state) =>
            avatar && state != null && state.Known && Scanned.Add(avatar.GetInstanceID() + "|" + state.Info.Key);

        /// <summary>The question an accessory needs on the avatar's custom base, or null when it needs nothing.</summary>
        internal static async Task<MyAvatar.AccessoryNote> CheckAsync(MyAvatar avatar, OrbitersAttachment attachment, CancellationToken cancellation)
        {
            var state = await CustomBaseDetection.DetectAsync(avatar.transform);
            if (state == null || !state.Known || !attachment) return null;
            var suggestion = await FitCheck.CheckAsync(attachment.gameObject, state, cancellation);
            if (!attachment || suggestion.Advice == FitAdvice.None) return null;
            return new MyAvatar.AccessoryNote
            {
                accessory = attachment,
                fit = suggestion.Advice == FitAdvice.AskFit ? Ask : suggestion.Advice == FitAdvice.Refit ? Refit : AddShapes,
                fitBase = BaseName(state.Info), fitVersion = state.Info.Version, fitKey = state.Info.Key, fitShapes = suggestion.ShapeCount,
                fitShapeNames = suggestion.Missing.Values.SelectMany(s => s).Distinct().ToList(),
            };
        }

        // The custom base as users know it, without its version (shown apart); a generic name reads as "your custom base".
        internal static string BaseName(CustomBaseInfo info)
        {
            string name = info.Name ?? "";
            if (!string.IsNullOrEmpty(info.Version) && name.EndsWith(" " + info.Version, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - info.Version.Length - 1);
            return string.IsNullOrWhiteSpace(name) || string.Equals(name, "Custom base", StringComparison.OrdinalIgnoreCase) ? null : name;
        }

        internal static string Text(MyAvatar.AccessoryNote note, string item)
        {
            string shapes = note.fitShapes + " blendshape" + (note.fitShapes == 1 ? "" : "s");
            note = new MyAvatar.AccessoryNote { fit = note.fit, text = note.text, fitShapes = note.fitShapes, fitRough = note.fitRough,
                fitBase = string.IsNullOrEmpty(note.fitBase) ? "your custom base" : note.fitBase };
            switch (note.fit)
            {
                case Ask: return $"Your avatar uses {note.fitBase}. Does {item} fit your body?";
                case AddShapes: return $"{item} lacks {shapes} of {note.fitBase}, like its flexing. Add them so it moves with your body?";
                case Refit: return $"{item} was made for the original base. Refit it to {note.fitBase}?";
                case Place: return $"Line {item} up with the original body, shown in blue: move it if it is off, then refit.";
                case Install: return $"ReFit fits {item} to {note.fitBase}. Install it to continue.";
                case Done:
                    return $"{item} now follows {note.fitBase}: {shapes} move with your body." +
                           (note.fitRough ? " Some spots could not be fitted exactly: a creator can refit it by hand." : "");
                default: return note.text;
            }
        }

        /// <summary>
        /// Refits the accessory: <see cref="RefitMode.Shapes"/> adds the blendshapes its meshes lack; <see cref="RefitMode.Fit"/>
        /// fits every mesh from the original base first. Each mesh only gets the shapes near it.
        /// </summary>
        internal static async Task<RefitBatchResult> RunAsync(MyAvatar avatar, OrbitersAttachment attachment, RefitMode mode, CustomBaseOriginal original,
            IList<string> asked, Action<float, string> progress, CancellationToken cancellation)
        {
            var state = await CustomBaseDetection.DetectAsync(avatar.transform);
            if (state == null || !state.Known) throw new InvalidOperationException("My Avatar does not recognise this avatar's custom base any more.");
            if (!attachment) throw new InvalidOperationException("This accessory was removed.");
            var suggestion = await FitCheck.CheckAsync(attachment.gameObject, state, cancellation);
            // What the user answered for: the shapes of the question, even when the accessory moved since (a pose preview).
            bool checkedAgain = suggestion.Missing.Count > 0;
            var meshes = mode == RefitMode.Fit || !checkedAgain ? RefitCandidates.Meshes(attachment.gameObject, state.Info.Body) : suggestion.Meshes;
            var batch = new RefitBatch
            {
                Avatar = RefitRecords.AvatarRoot(attachment.transform), Body = state.Info.Body, Renderers = meshes, Mode = mode, Original = original,
                BaseKey = state.Info.Key, BaseName = state.Info.Name, Tool = Tool, Tightness = RefitPreferences.Tightness,
                CoverDifferentBaseBody = mode == RefitMode.Fit && ClothingCoverage.Eligible(attachment, suggestion.MadeForOriginal),
            };
            foreach (var mesh in meshes)
                batch.ShapesByRenderer[mesh] = suggestion.Missing.TryGetValue(mesh, out var shapes) ? shapes
                    : !checkedAgain && asked != null ? asked.ToList() : new List<string>();
            var result = meshes.Count == 0 ? new RefitBatchResult() : await RefitRunner.RunAsync(batch, progress, cancellation);
            if (attachment) Results[attachment.GetInstanceID()] = result;
            return result;
        }

        internal static bool Refitted(OrbitersAttachment attachment) =>
            attachment && attachment.GetComponentsInChildren<OrbitersRefit>(true).Any(r => r.Applied);

        /// <summary>Puts every refitted mesh of the accessory back as it was, with Undo.</summary>
        internal static void Restore(OrbitersAttachment attachment)
        {
            if (!attachment) return;
            foreach (var record in attachment.GetComponentsInChildren<OrbitersRefit>(true)) RefitRecords.Remove(record);
        }

        internal static bool CanCommission(OrbitersAttachment attachment) =>
            RefitEngine.Available && attachment && Results.TryGetValue(attachment.GetInstanceID(), out var result) && Commissionable(result) != null;

        /// <summary>Opens ReFit's window on the roughest mesh of the accessory's last refit, to ask a creator.</summary>
        internal static void Commission(OrbitersAttachment attachment)
        {
            if (!attachment || !Results.TryGetValue(attachment.GetInstanceID(), out var result)) return;
            var item = Commissionable(result);
            if (item != null) RefitEngine.Current?.OpenCommission(item.Job, item.Outcome);
        }

        private static RefitItemResult Commissionable(RefitBatchResult result) =>
            result.Items.FirstOrDefault(i => i.Job != null && i.Outcome != null && !i.Outcome.Cancelled && (i.Outcome.Rough || !i.Outcome.Success));

        // ---- Lining up with the original base -------------------------------------------------------------------------

        /// <summary>
        /// The original base shown over the body while the user lines the accessory up with it, with a move handle on the
        /// accessory in the Scene view: nothing is selected, so My Avatar stays in the Inspector. One at a time, kept for the
        /// session (not by the Inspector, which may close meanwhile) until refitted, cancelled or its accessory is gone.
        /// </summary>
        internal sealed class Placement : IDisposable
        {
            public OrbitersAttachment Attachment;
            public CustomBaseOriginal Original;
            public RefitGhost Ghost;

            internal static Placement Active { get; private set; }

            internal static Placement For(OrbitersAttachment attachment) =>
                Active != null && attachment && Active.Attachment == attachment ? Active : null;

            internal void Activate()
            {
                if (Active != null && Active != this) Active.Dispose();
                Active = this;
                SceneView.duringSceneGui += Handle;
                AssemblyReloadEvents.beforeAssemblyReload += Dispose;
                SceneView.RepaintAll();
            }

            // A move handle on the accessory, without selecting it.
            private void Handle(SceneView view)
            {
                if (!Attachment) { Dispose(); return; }
                var t = Attachment.transform;
                var rotation = Tools.pivotRotation == PivotRotation.Local ? t.rotation : Quaternion.identity;
                EditorGUI.BeginChangeCheck();
                var position = Handles.PositionHandle(t.position, rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(t, "Move " + Attachment.name);
                    t.position = position;
                }
            }

            /// <summary>Hands the original over to the refit, which disposes it; the preview goes away.</summary>
            public CustomBaseOriginal Take()
            {
                var original = Original;
                Original = null;
                Dispose();
                return original;
            }

            public void Dispose()
            {
                SceneView.duringSceneGui -= Handle;
                AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
                if (Active == this) Active = null;
                SceneView.RepaintAll();
                Ghost?.Dispose();
                Ghost = null;
                Original?.Dispose();
                Original = null;
            }
        }

        internal static async Task<Placement> PlaceAsync(MyAvatar avatar, OrbitersAttachment attachment)
        {
            var state = await CustomBaseDetection.DetectAsync(avatar.transform);
            if (state?.Info == null || !state.Info.CanFit)
                throw new InvalidOperationException("Refitting from the original base needs MCB on the avatar: it provides the original body.");
            if (!attachment) throw new InvalidOperationException("This accessory was removed.");
            var original = state.Info.ResolveOriginal();
            try
            {
                var ghost = RefitGhost.Show(original.Avatar, original.Body, state.Info.Body);
                LineUp(attachment, ghost);
                var placement = new Placement { Attachment = attachment, Original = original, Ghost = ghost };
                placement.Activate();
                return placement;
            }
            catch
            {
                original.Dispose();
                throw;
            }
        }

        // Clothing made for the original base sits on its hips: when the accessory's hips are a little off the original's,
        // it is moved there (a larger gap is a different rig, left to the user).
        private static void LineUp(OrbitersAttachment attachment, RefitGhost ghost)
        {
            if (ghost.Hips == null) return;
            var root = RefitRecords.AvatarRoot(attachment.transform);
            var animator = root.GetComponent<Animator>();
            if (animator == null) animator = root.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => !a.transform.IsChildOf(attachment.transform));
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips == null) return;
            var itemHips = attachment.links.Where(l => l.to == hips && l.from != null).Select(l => l.from).FirstOrDefault()
                           ?? attachment.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t != attachment.transform && t.name == hips.name);
            if (itemHips == null) return;
            var offset = ghost.Hips.position - itemHips.position;
            if (offset.magnitude < 0.005f || offset.magnitude > 0.5f) return;
            Undo.RecordObject(attachment.transform, "Line up " + attachment.name + " with the original base");
            attachment.transform.position += offset;
        }
    }
}
