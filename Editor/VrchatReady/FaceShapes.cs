using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.FaceTracking;

namespace Orbiters.MyAvatar.Editor.VrchatReady
{
    /// <summary>
    /// The face's blendshapes for VRChat's lip sync and blink, found by name: VRChat's own (vrc.v_aa...), VRoid's
    /// (Fcl_MTH_A, Fcl_EYE_Close, with or without the mesh name before them), MMD's Japanese vowels (あいうえお) and common
    /// English ones (mouth_a, blink...). Names are compared as <see cref="FaceTrackingNames.Normalize"/> gives them:
    /// lower case, letters and digits only.
    /// </summary>
    internal static class FaceShapes
    {
        /// <summary>VRChat's visemes in the descriptor's order.</summary>
        internal static readonly string[] Visemes = { "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" };

        /// <summary>The five vowels, as VRM names them.</summary>
        internal static readonly string[] Vowels = { "A", "I", "U", "E", "O" };
        internal const int A = 0, I = 1, U = 2, E = 3, O = 4;

        // Each viseme from the five vowels, for a face with no shape of its own for it (VRM and VRoid faces): the vowels
        // as they are, each consonant from the vowel its mouth is closest to (F and S spread like I, D, N and TH open a
        // little like E, K opens like A, CH and R round the lips like U and O). Silence and P, B, M (closed lips) stay
        // empty: the face at rest.
        private static readonly int[] FromVowel = { -1, -1, I, E, E, A, U, I, E, O, A, E, I, O, U };

        // A vowel the face lacks borrows the closest one it has.
        private static readonly int[][] Closest =
        {
            new[] { A, E, O, I, U }, new[] { I, E, A, U, O }, new[] { U, O, I, E, A }, new[] { E, I, A, O, U }, new[] { O, U, A, E, I },
        };

        // Exact names per vowel (normalized), best first, then endings (VRoid puts the mesh's name before its own).
        private static readonly string[][] VowelNames =
        {
            new[] { "vrcvaa", "vaa", "a", "あ", "aa", "moutha", "mtha", "vowela" },
            new[] { "vrcvih", "vih", "i", "い", "ih", "mouthi", "mthi", "voweli" },
            new[] { "vrcvou", "vou", "u", "う", "ou", "mouthu", "mthu", "vowelu" },
            new[] { "vrcve", "ve", "e", "え", "eh", "mouthe", "mthe", "vowele" },
            new[] { "vrcvoh", "voh", "o", "お", "oh", "moutho", "mtho", "vowelo" },
        };
        private static readonly string[][] VowelEndings =
        {
            new[] { "fclmtha", "mtha", "moutha" }, new[] { "fclmthi", "mthi", "mouthi" }, new[] { "fclmthu", "mthu", "mouthu" },
            new[] { "fclmthe", "mthe", "mouthe" }, new[] { "fclmtho", "mtho", "moutho" },
        };

        // Both eyes closed: VRChat's eyelids take one shape.
        private static readonly string[] BlinkNames = { "vrcblink", "blink", "blinkboth", "eyeblink", "eyeclose", "eyeclosed", "eyesclosed", "closeeyes", "まばたき", "fcleyeclose" };
        private static readonly string[] BlinkEndings = { "fcleyeclose", "eyeclose", "blink" };

        /// <summary>
        /// The 15 visemes from VRChat's own names when the face has them (vrc.v_aa, v_aa), each missing one from the vowels;
        /// null entries stay empty. <paramref name="vowels"/> are the five vowels' shapes (A, I, U, E, O; null when unknown).
        /// </summary>
        internal static string[] Fill(IList<string> names, string[] vowels)
        {
            var normalized = Index(names);
            var result = new string[Visemes.Length];
            for (int v = 0; v < Visemes.Length; v++)
            {
                string own = Visemes[v].ToLowerInvariant();
                result[v] = Find(normalized, new[] { "vrcv" + own, "v" + own }, null);
            }
            var fromVowels = FromVowels(vowels);
            for (int v = 0; v < Visemes.Length; v++) if (result[v] == null) result[v] = fromVowels[v];
            return result;
        }

        /// <summary>The visemes from the five vowels alone (see <see cref="FromVowel"/>), a missing vowel borrowing the closest one.</summary>
        internal static string[] FromVowels(string[] vowels)
        {
            var result = new string[Visemes.Length];
            if (vowels == null || vowels.All(string.IsNullOrEmpty)) return result;
            for (int v = 0; v < Visemes.Length; v++)
            {
                int vowel = FromVowel[v];
                if (vowel < 0) continue;
                result[v] = Closest[vowel].Select(c => vowels[c]).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            }
            return result;
        }

        /// <summary>The face's shape for one vowel (<see cref="A"/>...), or null.</summary>
        internal static string Vowel(IList<string> names, int vowel) => Find(Index(names), VowelNames[vowel], VowelEndings[vowel]);

        /// <summary>The five vowels' shapes on this face (null for those it lacks).</summary>
        internal static string[] Vowels5(IList<string> names) => Enumerable.Range(0, 5).Select(v => Vowel(names, v)).ToArray();

        /// <summary>The face's shape closing both eyes, or null.</summary>
        internal static string Blink(IList<string> names) => Find(Index(names), BlinkNames, BlinkEndings);

        /// <summary>How many of the five vowels this face has, plus one for VRChat's own visemes: picks the face mesh.</summary>
        internal static int Score(IList<string> names)
        {
            var normalized = Index(names);
            int score = Enumerable.Range(0, 5).Count(v => Find(normalized, VowelNames[v], VowelEndings[v]) != null);
            if (normalized.ContainsKey("vrcvaa") || normalized.ContainsKey("vaa")) score++;
            return score;
        }

        // Normalized name → the first real name it stands for.
        private static Dictionary<string, string> Index(IList<string> names)
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in names ?? Array.Empty<string>())
            {
                string key = FaceTrackingNames.Normalize(name);
                if (key.Length > 0 && !index.ContainsKey(key)) index[key] = name;
            }
            return index;
        }

        private static string Find(Dictionary<string, string> index, string[] exact, string[] endings)
        {
            foreach (string name in exact)
                if (index.TryGetValue(name, out string found)) return found;
            if (endings == null) return null;
            foreach (string ending in endings)
            {
                // The shortest name with that ending: "Face.M_F00_000_00_Fcl_MTH_A" before longer look-alikes.
                string best = index.Where(pair => pair.Key.EndsWith(ending, StringComparison.Ordinal)).OrderBy(pair => pair.Key.Length).Select(pair => pair.Value).FirstOrDefault();
                if (best != null) return best;
            }
            return null;
        }
    }
}
