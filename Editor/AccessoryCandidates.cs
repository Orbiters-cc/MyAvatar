using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Orbiters.Toolkit.Editor.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // Picks what to put on the avatar among the prefabs and models of a drop. Creators ship variants (left/right hand,
    // PC/Quest, VRCFury/manual, late-join sync...): only the hand is asked, the rest follows sensible defaults.
    internal static class AccessoryCandidates
    {
        internal enum Hand { None, Left, Right }

        internal sealed class Candidate
        {
            public string path, name, family;
            public GameObject asset;
            public int score, renderers;
            public Hand hand;
            public bool vrcFury;
        }

        internal sealed class Choice
        {
            public readonly List<Candidate> all = new List<Candidate>();
            public Candidate best, left, right;
            /// <summary>Other items as good as the best one: an AI opinion for one, the user's pick for a collection.</summary>
            public readonly List<Candidate> rivals = new List<Candidate>();
            public bool NeedsHand => left != null && right != null;
        }

        private static readonly Regex Separators = new Regex(@"[\s_\-\.\(\)\[\]\+/]+");
        private static readonly string[] Mobile = { "quest", "android", "mobile", "ios" };
        private static readonly string[] Manual = { "manual", "novrcfury", "nonvrcfury", "withoutvrcfury", "legacy" };
        private static readonly string[] Examples = { "example", "sample", "demo", "preview", "showcase", "customization", "customisation", "optional" };
        private static readonly string[] Helpers = { "world", "bullet", "projectile", "placement", "reference", "helper", "target", "constraint", "raycast" };

        internal static Choice Choose(IEnumerable<string> assetPaths)
        {
            var choice = new Choice();
            var paths = assetPaths.Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                .Distinct().ToList();
            // A model or prefab another candidate is built from (prefab variants, nested prefabs) is not an entry itself.
            var used = new HashSet<string>(paths.Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => AssetDatabase.GetDependencies(p, true).Where(d => d != p)));
            bool mobile = EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
            foreach (var path in paths)
            {
                if (paths.Count > 1 && used.Contains(path)) continue;
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null) continue;
                int renderers = asset.GetComponentsInChildren<Renderer>(true).Length;
                if (renderers == 0) continue;
                var words = Words(path);
                var candidate = new Candidate
                {
                    path = path, asset = asset, name = asset.name, renderers = renderers,
                    vrcFury = VrcFury.Features(asset).Any(), hand = HandOf(words),
                };
                bool Has(IEnumerable<string> list) => list.Any(w => words.Contains(w) || string.Concat(words).Contains(w));
                candidate.score = (candidate.vrcFury ? 3 : 0) + (Has(Mobile) == mobile ? 0 : -3) + (Has(Manual) ? -2 : 0) +
                                  (Has(Examples) ? -2 : 0) + (Has(Helpers) ? -4 : 0) +
                                  (string.Concat(words).Contains("nolatejoin") ? -1 : 0) + (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                candidate.family = Family(asset.name);
                choice.all.Add(candidate);
            }
            if (choice.all.Count == 0) return choice;
            var ranked = choice.all.OrderByDescending(c => c.score).ThenBy(c => c.name.Length).ThenByDescending(c => c.renderers).ToList();
            choice.best = ranked[0];
            choice.rivals.AddRange(ranked.Skip(1).Where(c => c.score == choice.best.score && c.family != choice.best.family && c.hand == Hand.None).Take(11));
            // Hand variants of the chosen item: the same family and score apart from the side.
            if (choice.best.hand != Hand.None)
            {
                var sided = ranked.Where(c => c.family == choice.best.family && c.score == choice.best.score).ToList();
                choice.left = sided.FirstOrDefault(c => c.hand == Hand.Left);
                choice.right = sided.FirstOrDefault(c => c.hand == Hand.Right);
            }
            return choice;
        }

        private static List<string> Words(string path)
        {
            var text = path.Replace("Assets/", "").Replace(".prefab", "").Replace(".fbx", "");
            return Separators.Split(text.ToLowerInvariant()).Where(w => w.Length > 0).ToList();
        }

        private static Hand HandOf(List<string> words)
        {
            bool left = false, right = false;
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i], next = i + 1 < words.Count ? words[i + 1] : "";
                if (w == "lefthand" || w == "left" && (next == "hand" || next == "handed")) left = true;
                if (w == "righthand" || w == "right" && (next == "hand" || next == "handed")) right = true;
                if (w == "dominant" && next == "hand") right = true;
            }
            return left == right ? Hand.None : left ? Hand.Left : Hand.Right;
        }

        // The item a variant belongs to: its name without side, platform and variant words.
        private static string Family(string name)
        {
            var noise = new[] { "left", "right", "hand", "l", "r", "pc", "quest", "android", "mobile", "vrcfury", "manual", "wd", "on", "off", "variant", "prefab", "with", "no", "late", "join", "sync" };
            return string.Concat(Separators.Split(name.ToLowerInvariant()).Where(w => w.Length > 0 && !noise.Contains(w)));
        }
    }
}
