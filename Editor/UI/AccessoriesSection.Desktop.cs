using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Net;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.VRChat;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed partial class AccessoriesSection
    {
        // What the last "Install N4N0 desktop" said, kept while the section redraws.
        private static string desktopStatus;
        private static bool desktopInstalling, desktopFailed;

        /// <summary>
        /// N4N0 on the avatar does nothing without Orbiters - N4N0 on this computer: when it is missing, a note says so with
        /// "Install N4N0 desktop", which downloads its latest release, installs it and opens it (<see cref="OrbitersDesktopApp"/>).
        /// </summary>
        private void DesktopPrompt(VisualElement parent, IReadOnlyCollection<OrbitersAttachment> installed)
        {
            if (!installed.Any(a => a && OrbInstaller.HasScreen(a.transform))) return;
            if (!desktopInstalling && OrbitersDesktopApp.Installed != null) { desktopStatus = null; return; }
            var row = new VisualElement(); row.AddToClassList("accessory-note"); parent.Add(row);
            var dot = new VisualElement(); dot.AddToClassList("accessory-note__dot"); dot.EnableInClassList("warning", !desktopInstalling); row.Add(dot);
            string text = desktopStatus ?? "N4N0 needs Orbiters - N4N0 on this computer to work: it runs N4N0's menu, captures and voice.";
            var label = new Label(text); label.AddToClassList("accessory-note__text"); row.Add(label);
            if (desktopInstalling) return;
            row.Add(Small(desktopFailed ? "Try again" : "Install N4N0 desktop", async () =>
            {
                desktopInstalling = true; desktopFailed = false;
                // The notes are drawn again on each step, this one among them.
                void Say(string words) { desktopStatus = words; RefreshNotes(); }
                Say("Finding the latest Orbiters - N4N0…");
                try { await OrbitersDesktopApp.InstallAndOpenAsync(Say); }
                catch (Exception e) { desktopFailed = true; Say(e.Message); }
                finally { desktopInstalling = false; Refresh(); }
            }));
        }
    }
}
