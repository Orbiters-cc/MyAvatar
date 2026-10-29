using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.Net;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.VRChat;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    internal sealed partial class AccessoriesSection
    {
        private const string DesktopPage = "https://orbiters.cc/n4n0";
        private const double DesktopDoneSeconds = 8;

        /// <summary>Where Orbiters - N4N0 is installed, or null (<see cref="OrbitersDesktopApp.Installed"/>; tests replace it).</summary>
        internal static Func<string> DesktopInstalled = () => OrbitersDesktopApp.Installed;

        // What the last "Install N4N0 desktop" said, kept while the section redraws, and when it finished.
        private static string desktopStatus;
        private static bool desktopInstalling, desktopFailed;
        private static double desktopDoneAt = double.NegativeInfinity;

        /// <summary>
        /// N4N0 on the avatar does nothing without Orbiters - N4N0 on this computer: when it is missing, a card says so with
        /// "Install N4N0 desktop", which downloads its latest release, installs it and opens it (<see cref="OrbitersDesktopApp"/>).
        /// The card follows the install (its steps over a running light), says when the app is open, then goes.
        /// </summary>
        private void DesktopPrompt(VisualElement parent, IReadOnlyCollection<OrbitersAttachment> installed)
        {
            if (!installed.Any(a => a && OrbInstaller.HasScreen(a.transform))) return;
            bool done = EditorApplication.timeSinceStartup - desktopDoneAt < DesktopDoneSeconds;
            if (!desktopInstalling && !done && DesktopInstalled() != null) { desktopStatus = null; return; }

            var card = new VisualElement(); card.AddToClassList("desktop-card"); parent.Add(card);
            card.EnableInClassList("desktop-card--working", desktopInstalling);
            card.EnableInClassList("desktop-card--failed", desktopFailed && !desktopInstalling);
            card.EnableInClassList("desktop-card--done", done && !desktopInstalling);
            var top = new VisualElement(); top.AddToClassList("desktop-card__top"); card.Add(top);
            var texts = new VisualElement(); texts.AddToClassList("desktop-card__texts"); top.Add(texts);
            var eyebrow = new Label(done ? "READY" : "ORBITERS - N4N0"); eyebrow.AddToClassList("desktop-card__eyebrow"); texts.Add(eyebrow);
            var title = new Label(desktopInstalling ? "Installing N4N0 desktop" : done ? "Orbiters - N4N0 is open" : desktopFailed ? "The install stopped" : "Get N4N0's desktop app");
            title.AddToClassList("desktop-card__title"); texts.Add(title);
            var line = new Label(done ? "Find it in the tray: sign in there to capture and share."
                : desktopStatus ?? "N4N0 needs it on this computer: it runs N4N0's menu, captures and voice.");
            line.AddToClassList("desktop-card__line"); texts.Add(line);
            var tile = new VisualElement(); tile.AddToClassList("desktop-card__tile"); top.Add(tile);
            if (done)
            {
                var check = new VectorIcon(IconGlyph.Check); check.AddToClassList("desktop-card__check"); tile.Add(check);
            }
            else tile.Add(N4N0Logo.Image(new Color(0f, 0.855f, 0.427f)));

            if (desktopInstalling)
            {
                // Steps whose length is unknown: a light running along a track.
                var track = new VisualElement(); track.AddToClassList("desktop-card__track"); card.Add(track);
                var light = new VisualElement(); light.AddToClassList("desktop-card__light"); track.Add(light);
                card.schedule.Execute(() =>
                {
                    float width = track.resolvedStyle.width;
                    if (float.IsNaN(width) || width <= 0f) return;
                    float t = (float)(EditorApplication.timeSinceStartup % 1.4 / 1.4);
                    light.style.left = Mathf.Lerp(-0.35f * width, width, t);
                }).Every(16);
                return;
            }
            if (done)
            {
                // Said for a moment, then the card goes.
                card.schedule.Execute(RefreshNotes).StartingIn((long)(DesktopDoneSeconds * 1000) + 50);
                return;
            }
            var actions = new VisualElement(); actions.AddToClassList("desktop-card__actions"); card.Add(actions);
            var install = MyAvatarEditor.Button(desktopFailed ? "Try again" : "Install N4N0 desktop", () => { });
            install.AddToClassList("mcb-button--primary"); install.AddToClassList("desktop-card__install");
            install.tooltip = "Download Orbiters - N4N0's latest release, install it for this Windows account and open it.";
            ButtonInteraction.RegisterImmediateClick(install, InstallDesktop);
            actions.Add(install);
            var more = MyAvatarEditor.Button("What is it?", () => { });
            more.AddToClassList("avatar-link"); more.AddToClassList("desktop-card__more");
            more.tooltip = DesktopPage;
            ButtonInteraction.RegisterImmediateClick(more, () => Application.OpenURL(DesktopPage));
            actions.Add(more);
        }

        private async void InstallDesktop()
        {
            if (desktopInstalling) return;
            desktopInstalling = true; desktopFailed = false;
            // The notes are drawn again on each step, this one among them.
            void Say(string words) { desktopStatus = words; RefreshNotes(); }
            Say("Finding the latest Orbiters - N4N0…");
            try
            {
                await OrbitersDesktopApp.InstallAndOpenAsync(Say);
                desktopDoneAt = EditorApplication.timeSinceStartup;
                desktopStatus = null;
            }
            catch (Exception e) { desktopFailed = true; Say(e.Message); }
            finally { desktopInstalling = false; Refresh(); }
        }
    }
}
