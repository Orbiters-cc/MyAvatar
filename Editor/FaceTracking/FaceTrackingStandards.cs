using System;
using System.Collections.Generic;
using System.Linq;

namespace Orbiters.MyAvatar.Editor.FaceTracking
{
    /// <summary>One of the blendshape standards Adjerry91's templates animate, with the shapes an avatar needs for it.</summary>
    internal sealed class FaceTrackingStandard
    {
        public readonly string Id, Label, Prefab;
        // Synced parameter bits the template adds (its parameters asset, counted for 7.0.5).
        public readonly int Bits;
        // The shapes that make the face move: an avatar that has most of them is worth setting up.
        public readonly string[] Core;

        private FaceTrackingStandard(string id, string label, string prefab, int bits, string[] core)
        { Id = id; Label = label; Prefab = prefab; Bits = bits; Core = core; }

        private static string[] Sided(params string[] names) =>
            names.SelectMany(n => n.Contains("{S}") ? new[] { n.Replace("{S}", "Left"), n.Replace("{S}", "Right") } : new[] { n }).ToArray();

        private static readonly string[] UeCore = Sided("EyeClosed{S}", "EyeWide{S}", "EyeSquint{S}", "BrowDown{S}", "BrowInnerUp", "BrowOuterUp{S}",
            "JawOpen", "MouthClosed", "JawLeft", "JawRight", "JawForward", "MouthLeft", "MouthRight", "MouthSmile{S}", "MouthFrown{S}",
            "MouthUpperUp{S}", "MouthLowerDown", "MouthStretch{S}", "LipFunnel", "LipPucker", "LipSuckUpper", "LipSuckLower", "CheekPuff{S}");

        public static readonly FaceTrackingStandard UnifiedExpressions = new FaceTrackingStandard("UE", "Unified Expressions", "VF_UE_VRCFT", 167,
            UeCore.Append("TongueOut").ToArray());
        public static readonly FaceTrackingStandard UnifiedExpressionsTongueSteps = new FaceTrackingStandard("UE_TongueSteps", "Unified Expressions (tongue steps)",
            "VF_UE_TongueSteps_VRCFT", 167, UeCore.Append("TongueOutStep1").ToArray());
        public static readonly FaceTrackingStandard ARKit = new FaceTrackingStandard("ARKit", "ARKit (Perfect Sync)", "VF_ARKit_VRCFT", 141,
            Sided("browDown{S}", "browInnerUp", "browOuterUp{S}", "cheekPuff", "cheekSquint{S}", "eyeBlink{S}", "eyeLookDown{S}", "eyeLookIn{S}",
                "eyeLookOut{S}", "eyeLookUp{S}", "eyeSquint{S}", "eyeWide{S}", "jawForward", "jawLeft", "jawOpen", "jawRight", "mouthClose",
                "mouthDimple{S}", "mouthFrown{S}", "mouthFunnel", "mouthLeft", "mouthLowerDown{S}", "mouthPress{S}", "mouthPucker", "mouthRight",
                "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper", "mouthSmile{S}", "mouthStretch{S}", "mouthUpperUp{S}",
                "noseSneer{S}", "tongueOut"));
        public static readonly FaceTrackingStandard SRanipal = new FaceTrackingStandard("SRanipal", "SRanipal", "VF_SRanipal_VRCFT", 138,
            Sided("Eye_{S}_Blink", "Eye_{S}_Wide", "Eye_{S}_squeeze", "Eye_{S}_Up", "Eye_{S}_Down", "Eye_{S}_Left", "Eye_{S}_Right",
                "Jaw_Open", "Jaw_Forward", "Jaw_Left", "Jaw_Right", "Mouth_Ape_Shape", "Mouth_Upper_{S}", "Mouth_Lower_{S}",
                "Mouth_Upper_Overturn", "Mouth_Lower_Overturn", "Mouth_Pout", "Mouth_Smile_{S}", "Mouth_Sad_{S}", "Cheek_Puff_{S}", "Cheek_Suck",
                "Mouth_Upper_Up{S}", "Mouth_Lower_Down{S}", "Mouth_Upper_Inside", "Mouth_Lower_Inside", "Mouth_Lower_Overlay",
                "Tongue_LongStep1", "Tongue_Up", "Tongue_Down", "Tongue_Left", "Tongue_Right", "Tongue_Roll"));

        // Ties go to the first: Unified Expressions is what VRCFaceTracking speaks natively.
        public static readonly FaceTrackingStandard[] All = { UnifiedExpressions, UnifiedExpressionsTongueSteps, ARKit, SRanipal };

        public static FaceTrackingStandard Find(string id) => All.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// Finds, for a blendshape a template animates, the shape of this face that does the same: the same name, or another
    /// standard's name, or a common avatar naming (e.g. Rexouium's "LipTrack_JawOpen", "Eye_LookDown_L", "EyesWide_L").
    /// Names are compared without case or separators; "{S}" stands for the side ("Left" or "L").
    /// </summary>
    internal static class FaceTrackingNames
    {
        // Each row: the names the templates use for one movement, then other names avatars give it, best first.
        private static readonly (string templates, string aliases)[] Rows =
        {
            ("EyeClosed{S}|eyeBlink{S}|Eye_{S}_Blink", "EyeClosed{S}|Blink{S}|EyeBlink{S}|EyeClose{S}|EyesClosed{S}"),
            ("EyeWide{S}|eyeWide{S}|Eye_{S}_Wide", "EyeWide{S}|EyesWide{S}"),
            ("EyeSquint{S}|eyeSquint{S}|Eye_{S}_squeeze", "EyeSquint{S}|EyeSqueeze{S}|EyesSqueeze{S}|EyeSquint"),
            ("EyeLookUp{S}|eyeLookUp{S}|Eye_{S}_Up", "EyeLookUp{S}|EyeUp{S}|LookUp{S}"),
            ("EyeLookDown{S}|eyeLookDown{S}|Eye_{S}_Down", "EyeLookDown{S}|EyeDown{S}|LookDown{S}"),
            // In looks toward the nose: the left eye looking right.
            ("EyeLookInLeft|eyeLookInLeft|Eye_Left_Right", "EyeLookInLeft|EyeLookIn_L|EyeLookRight_L|LookRight_L"),
            ("EyeLookInRight|eyeLookInRight|Eye_Right_Left", "EyeLookInRight|EyeLookIn_R|EyeLookLeft_R|LookLeft_R"),
            ("EyeLookOutLeft|eyeLookOutLeft|Eye_Left_Left", "EyeLookOutLeft|EyeLookOut_L|EyeLookLeft_L|LookLeft_L"),
            ("EyeLookOutRight|eyeLookOutRight|Eye_Right_Right", "EyeLookOutRight|EyeLookOut_R|EyeLookRight_R|LookRight_R"),
            ("Eye_{S}_Dilation", "EyeDilation{S}|PupilsBig{S}|PupilDilation{S}|PupilBig{S}"),
            ("Eye_{S}_Constrict", "EyeConstrict{S}|PupilsSmall{S}|PupilConstrict{S}|PupilSmall{S}"),
            ("EyeDilation", "EyeDilation|PupilsBig|PupilDilation"),
            ("EyeConstrict", "EyeConstrict|PupilsSmall|PupilConstrict"),
            ("BrowDown{S}|browDown{S}", "BrowDown{S}|BrowLower{S}|BrowAngry{S}"),
            ("BrowInnerUp|browInnerUp", "BrowInnerUp|BrowUp|BrowsUp|BrowInnerRaise"),
            ("BrowOuterUp{S}|browOuterUp{S}", "BrowOuterUp{S}|BrowUp{S}|BrowRaise{S}"),
            ("JawOpen|jawOpen|Jaw_Open", "JawOpen"),
            ("JawLeft|jawLeft|Jaw_Left", "JawLeft"),
            ("JawRight|jawRight|Jaw_Right", "JawRight"),
            ("JawForward|jawForward|Jaw_Forward", "JawForward|JawFoward"),
            ("MouthClosed|mouthClose|Mouth_Ape_Shape", "MouthClosed|MouthClose|MouthApeShape|MouthApe"),
            ("MouthSmile{S}|mouthSmile{S}|Mouth_Smile_{S}", "MouthSmile{S}|LipSmile{S}|Smile{S}"),
            ("MouthFrown{S}|mouthFrown{S}|Mouth_Sad_{S}", "MouthFrown{S}|LipFrown{S}|MouthSad{S}|Frown{S}"),
            ("MouthUpperUp{S}|mouthUpperUp{S}|Mouth_Upper_Up{S}", "MouthUpperUp{S}|UpperLipUp{S}|LipCornerTopUp{S}"),
            ("mouthLowerDown{S}|Mouth_Lower_Down{S}", "MouthLowerDown{S}|LowerLipDown{S}|LipCornerLowDown{S}"),
            ("MouthLowerDown", "MouthLowerDown|LowerLipDown"),
            ("MouthLeft|mouthLeft", "MouthLeft|MouthMoveLeft"),
            ("MouthRight|mouthRight", "MouthRight|MouthMoveRight"),
            ("Mouth_Upper_{S}", "MouthUpper{S}|UpperLip{S}"),
            ("Mouth_Lower_{S}", "MouthLower{S}|LowerLip{S}"),
            ("Mouth_Upper_Overturn", "MouthUpperOverturn|UpperLipRollOut|UpperLipOverturn|LipFunnelUpper"),
            ("Mouth_Lower_Overturn", "MouthLowerOverturn|LowerLipRollOut|LowerLipOverturn|LipFunnelLower"),
            ("LipSuckUpper|mouthRollUpper|Mouth_Upper_Inside", "LipSuckUpper|MouthRollUpper|MouthUpperInside|UpperLipIn|UpperLipInside"),
            ("LipSuckLower|mouthRollLower|Mouth_Lower_Inside", "LipSuckLower|MouthRollLower|MouthLowerInside|LowerLipIn|LowerLipInside"),
            ("Mouth_Lower_Overlay", "MouthLowerOverlay|LowerLipUp|LowerLipOverlay"),
            ("LipFunnel|mouthFunnel", "LipFunnel|MouthFunnel|Funnel"),
            ("LipPucker|mouthPucker|Mouth_Pout", "LipPucker|MouthPucker|MouthPout|PoutPucker|Pout|Pucker"),
            ("MouthStretch{S}|mouthStretch{S}", "MouthStretch{S}|LipStretch{S}"),
            ("MouthDimple{S}|mouthDimple{S}", "MouthDimple{S}|Dimple{S}"),
            ("MouthPress|mouthPressLeft", "MouthPress{S}|MouthPress"),
            ("mouthPressRight", "MouthPress{S}|MouthPress"),
            ("MouthRaiserUpper|mouthShrugUpper", "MouthRaiserUpper|MouthShrugUpper"),
            ("MouthRaiserLower|mouthShrugLower", "MouthRaiserLower|MouthShrugLower"),
            ("CheekPuff{S}|Cheek_Puff_{S}", "CheekPuff{S}|CheeksPuff{S}"),
            ("cheekPuff", "CheekPuff|CheeksPuff"),
            ("CheekSuck{S}", "CheekSuck{S}|CheeksIn{S}|CheekIn{S}"),
            ("Cheek_Suck", "CheekSuck|CheeksIn|CheekIn"),
            ("CheekSquint{S}|cheekSquint{S}", "CheekSquint{S}|CheekRaise{S}"),
            ("NoseSneer{S}|noseSneer{S}", "NoseSneer{S}|NoseSnarl{S}|NoseSneer|NoseSnarl"),
            ("NoseSneer", "NoseSneer|NoseSnarl"),
            ("TongueOut|tongueOut|Tongue_LongStep1|TongueOutStep1", "TongueOut|TongueExtend|TongueLongStep1|TongueLong"),
            ("Tongue_LongStep2|TongueOutStep2", "TongueLongStep2|TongueOutStep2|TongueExtend|TongueOut"),
            ("TongueUp|Tongue_Up", "TongueUp|TongueRaise"),
            ("TongueDown|Tongue_Down", "TongueDown"),
            ("TongueLeft|Tongue_Left", "TongueLeft"),
            ("TongueRight|Tongue_Right", "TongueRight"),
            ("TongueRoll|Tongue_Roll", "TongueRoll"),
            ("TongueUpLeftMorph|Tongue_UpLeft_Morph", "TongueUpLeftMorph|TongueUpLeft"),
            ("TongueUpRightMorph|Tongue_UpRight_Morph", "TongueUpRightMorph|TongueUpRight"),
            ("TongueDownLeftMorph|Tongue_DownLeft_Morph", "TongueDownLeftMorph|TongueDownLeft"),
            ("TongueDownRightMorph|Tongue_DownRight_Morph", "TongueDownRightMorph|TongueDownRight"),
        };

        // Prefixes avatars put before face tracking shapes.
        private static readonly string[] Prefixes = { "", "liptrack", "lt", "ft", "vrcft", "eyetrack", "et", "face" };

        // Template name → the other names to look for, best first.
        private static readonly Dictionary<string, List<string>> aliases = Build();

        private static Dictionary<string, List<string>> Build()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (templates, names) in Rows)
                foreach (var side in templates.Contains("{S}") ? new[] { "Left", "Right" } : new string[] { null })
                {
                    var list = names.Split('|').SelectMany(a => Expand(a, side)).Distinct().ToList();
                    foreach (var template in templates.Split('|').Select(t => side == null ? t : t.Replace("{S}", side)))
                        if (!map.ContainsKey(template)) map[template] = list;
                }
            return map;
        }

        private static IEnumerable<string> Expand(string alias, string side)
        {
            if (!alias.Contains("{S}")) { yield return Normalize(alias); yield break; }
            if (side == null) yield break;
            yield return Normalize(alias.Replace("{S}", side));
            yield return Normalize(alias.Replace("{S}", side.Substring(0, 1)));
        }

        internal static string Normalize(string name) => new string((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>The face's shape for <paramref name="template"/>, or null. <paramref name="shapes"/> maps normalized names to real ones.</summary>
        internal static string Resolve(string template, IReadOnlyDictionary<string, string> shapes)
        {
            if (shapes.TryGetValue(" " + template, out var exact)) return exact;
            var candidates = aliases.TryGetValue(template, out var list) ? list : new List<string> { Normalize(template) };
            foreach (string alias in candidates)
                foreach (string prefix in Prefixes)
                    if (shapes.TryGetValue(prefix + alias, out var found)) return found;
            return null;
        }

        /// <summary>A face's blendshapes for <see cref="Resolve"/>: exact names (marked with a leading space) and normalized ones.</summary>
        internal static Dictionary<string, string> Index(IEnumerable<string> names)
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in names)
            {
                index[" " + name] = name;
                string key = Normalize(name);
                if (!index.ContainsKey(key)) index[key] = name;
            }
            return index;
        }
    }
}
