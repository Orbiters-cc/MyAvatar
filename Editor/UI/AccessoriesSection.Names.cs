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
        private bool namedInstalled;

        private void RefreshNames()
        {
            if (namedInstalled || !host.AiOn() || string.IsNullOrEmpty(Token())) return;
            namedInstalled = true;
            _ = NameAsync(AttachmentInstaller.Installed(avatar.transform));
        }

        private async Task NameAsync(List<OrbitersAttachment> attachments)
        {
            string token = Token();
            if (!host.AiOn() || string.IsNullOrEmpty(token)) return;
            var ask = attachments.Where(a => a && a.displayNameFor != a.name && Named.Add(a.GetInstanceID())).ToList();
            if (ask.Count == 0) return;
            try
            {
                var state = CustomBaseDetection.Current(avatar.transform);
                string avatarBase = state?.Info != null ? AccessoryFit.BaseName(state.Info) ?? state.Info.BaseName : null;
                var names = await AccessoryAi.NamesAsync(token, ask, avatarBase, CancellationToken.None);
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
