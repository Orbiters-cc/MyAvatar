using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Refit;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Parameters;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>
    /// What the gallery needs to know about the avatar: the platform the project builds for, its avatar base (from its
    /// body's model file, a tool on the avatar such as MCB, the base's known project paths, or the user's own choice) and
    /// the parameter memory left.
    /// </summary>
    internal sealed class GalleryAvatarContext
    {
        public string Platform = AvatarPlatform.Current;
        public int? BaseId;
        public string BaseName;
        /// <summary>How the base was found: "model" (Orbiters knows the body's file), "tool", "project", "name", "chosen" or null.</summary>
        public string BaseSource;
        public ParameterBudget Budget;
        public bool HasDescriptor;

        private const string ChoiceKey = "Orbiters.MyAvatar.GalleryBase.";

        public string BaseLabel => BaseId.HasValue ? BaseName : "Unknown base";

        internal static string Key(MyAvatar avatar) => avatar ? ChoiceKey + GlobalObjectId.GetGlobalObjectIdSlow(avatar) : null;

        /// <summary>The user's own answer for this avatar (0 clears it).</summary>
        internal static void Choose(MyAvatar avatar, int baseId)
        {
            string key = Key(avatar);
            if (key == null) return;
            if (baseId > 0) EditorPrefs.SetInt(key, baseId); else EditorPrefs.DeleteKey(key);
        }

        internal static async Task<GalleryAvatarContext> DetectAsync(MyAvatar avatar, IReadOnlyList<GalleryBase> bases, CancellationToken cancellation)
        {
            var context = new GalleryAvatarContext();
            if (!avatar) return context;
            context.Budget = AvatarParameterBudget.Estimate(avatar.gameObject);
            context.HasDescriptor = avatar.GetComponent("VRCAvatarDescriptor") != null;
            bases = bases ?? Array.Empty<GalleryBase>();
            GalleryBase Named(string name) => string.IsNullOrWhiteSpace(name) ? null : bases.FirstOrDefault(b => string.Equals(b.name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            void Use(GalleryBase found, string source) { if (found == null || context.BaseId.HasValue) return; context.BaseId = found.id; context.BaseName = found.name; context.BaseSource = source; }

            int chosen = EditorPrefs.GetInt(Key(avatar), 0);
            if (chosen > 0) Use(bases.FirstOrDefault(b => b.id == chosen), "chosen");

            // A tool on the avatar (MCB) answers at once; its base is named after the model ("RexouiumRig1.6_V265").
            if (!context.BaseId.HasValue) Use(Named(CustomBases.Describe(avatar.transform)?.BaseName) ?? Within(CustomBases.Describe(avatar.transform)?.BaseName, bases), "tool");
            var body = AttachmentPlanner.Body(avatar.transform);
            if (!context.BaseId.HasValue && body != null)
            {
                try
                {
                    var identity = await BaseFingerprint.IdentifyAsync(body, cancellation);
                    if (identity != null && identity.Known)
                    {
                        context.BaseId = identity.AvatarBaseId;
                        context.BaseName = identity.BaseName;
                        context.BaseSource = "model";
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { Debug.Log("[My Avatar] Could not recognise the avatar's base: " + ex.Message); }
            }
            if (!avatar) return context;
            if (!context.BaseId.HasValue) Use(ByProjectPaths(avatar, bases), "project");
            if (!context.BaseId.HasValue) Use(ByName(avatar, body, bases), "name");
            return context;
        }

        // A base registered with the project paths of its model files: the avatar uses one of them.
        private static GalleryBase ByProjectPaths(MyAvatar avatar, IReadOnlyList<GalleryBase> bases)
        {
            var paths = new HashSet<string>(avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Select(r => r.sharedMesh ? AssetDatabase.GetAssetPath(r.sharedMesh) : null).Where(p => !string.IsNullOrEmpty(p))
                .Select(Normalize), StringComparer.OrdinalIgnoreCase);
            if (paths.Count == 0) return null;
            return bases.FirstOrDefault(b => b.patterns?.paths?.pathsList?.Any(p => paths.Contains(Normalize(p))) == true);
        }

        // The base's name in the avatar's or its body model's name ("Rexouium1.6 Default Setup").
        private static GalleryBase ByName(MyAvatar avatar, SkinnedMeshRenderer body, IReadOnlyList<GalleryBase> bases)
        {
            string model = body && body.sharedMesh ? System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(body.sharedMesh)) : "";
            return Within(avatar.name + " " + model, bases);
        }

        // The longest registered base name found in the text.
        private static GalleryBase Within(string text, IReadOnlyList<GalleryBase> bases)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string lower = text.ToLowerInvariant();
            return bases.Where(b => !string.IsNullOrWhiteSpace(b.name) && b.name.Length >= 4 && lower.Contains(b.name.ToLowerInvariant()))
                .OrderByDescending(b => b.name.Length).FirstOrDefault();
        }

        private static string Normalize(string path) => (path ?? "").Replace('\\', '/').Trim().TrimStart('/');
    }
}
