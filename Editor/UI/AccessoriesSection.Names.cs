using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.VRChat.Attachments;
using Orbiters.Toolkit.Editor.VRChat.Refit;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    // With AI help on, short clean names for the accessories ("Glowsticks_Body ultipaw Variant" shows as "Glowsticks for
    // Ultipaw"), asked in the background after a drop and once for those already on the avatar. Only the shown name
    // changes: the object keeps its name, so nothing that refers to it by path breaks.
    internal sealed partial class AccessoriesSection
    {
        // Accessories already asked about this session: an answer is not asked for again.
        private static readonly HashSet<int> Named = new HashSet<int>();
        // The same for the items a drop offers, by asset path: their names while the user chooses.
        private static readonly Dictionary<string, string> OptionNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> OptionsAsked = new HashSet<string>(StringComparer.Ordinal);
        private bool namedInstalled;

        private void RefreshNames()
        {
            if (namedInstalled || !host.AiOn() || string.IsNullOrEmpty(Token())) return;
            namedInstalled = true;
            _ = NameAsync(AttachmentInstaller.Installed(avatar.transform));
        }

        /// <summary>The AI's name for an offered item once known, else its file name.</summary>
        private static string OptionName(string path, string fileName) => OptionNames.TryGetValue(path, out var name) ? name : fileName;

        // Named in the background while the choice is on screen; the cards show the names once they arrive.
        private async Task NameOptionsAsync(List<(string label, string value)> options)
        {
            string token = Token();
            if (!host.AiOn() || string.IsNullOrEmpty(token)) return;
            var ask = options.Where(o => OptionsAsked.Add(o.value)).Take(AccessoryAi.MaxNames).ToList();
            if (ask.Count == 0) return;
            try
            {
                var names = await AccessoryAi.NamesAsync(token, ask.Select(o => (o.label, o.value)).ToList(), AvatarBase(), CancellationToken.None);
                foreach (var pair in names) OptionNames[ask[pair.Key].value] = pair.Value;
                // Asked with another account or with AI help turned off since: the answer is not shown.
                if (avatar && host.AiOn() && Token() == token && names.Count > 0) RefreshChoice();
            }
            catch (Exception ex)
            {
                foreach (var option in ask) OptionsAsked.Remove(option.value);
                if (!(ex is OperationCanceledException)) Debug.Log("[My Avatar] Could not name the offered items: " + ex.Message);
            }
        }

        private string AvatarBase()
        {
            var state = CustomBaseDetection.Current(avatar.transform);
            return state?.Info != null ? AccessoryFit.BaseName(state.Info) ?? state.Info.BaseName : null;
        }

        private async Task NameAsync(List<OrbitersAttachment> attachments)
        {
            string token = Token();
            if (!host.AiOn() || string.IsNullOrEmpty(token)) return;
            var ask = attachments.Where(a => a && a.displayNameFor != a.name && Named.Add(a.GetInstanceID())).ToList();
            if (ask.Count == 0) return;
            try
            {
                var names = await AccessoryAi.NamesAsync(token, ask, AvatarBase(), CancellationToken.None);
                // Asked with another account or with AI help turned off since: the answer is not used.
                if (!avatar || !host.AiOn() || Token() != token) return;
                foreach (var pair in names)
                {
                    if (!pair.Key) continue;
                    pair.Key.displayName = pair.Value;
                    pair.Key.displayNameFor = pair.Key.name;
                    EditorUtility.SetDirty(pair.Key);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(pair.Key);
                }
                if (names.Count > 0) Refresh();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                foreach (var attachment in ask) if (attachment) Named.Remove(attachment.GetInstanceID());
                Debug.Log("[My Avatar] Could not name the accessories: " + ex.Message);
            }
        }
    }
}
