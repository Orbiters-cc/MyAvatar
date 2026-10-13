using System;
using System.Collections.Generic;
using UnityEngine.UIElements;
#if MYAVATAR_VPM
using Orbiters.Toolkit.Editor.Vpm;
#endif

namespace Orbiters.MyAvatar.Editor
{
    // What a one-click setup (GoGoLoco, face tracking) or a tool to install downloads, shown before anything changes:
    // Toolkit's dependency plan, each package with what needs it and the VPM repositories it adds to the Creator Companion
    // (used by every project). Its button installs them (a setup then finishes on the avatar once Unity has imported them,
    // PackageSetup); Cancel changes nothing. Without the Creator Companion's resolver it says what to add by hand. The plan
    // is read once the prompt is on screen, so an answer that is ready at once still finds it there.
    internal sealed class PackageSetupPrompt : VisualElement
    {
        private bool started, detached;

        /// <param name="what">What is added, as in "Before adding GoGoLoco".</param>
        /// <param name="done">The note to show (null when cancelled) and whether it is a warning; the prompt is done.</param>
        internal PackageSetupPrompt(PackageSetup setup, MyAvatar avatar, IList<string> missing, string what, Action<string, bool> done)
        {
            AddToClassList("package-setup");
            style.marginTop = 10;
            var status = new Label("Checking what " + what + " needs…"); status.AddToClassList("ft-note"); Add(status);
            RegisterCallback<DetachFromPanelEvent>(_ => detached = true);
            // Started on screen, after the attach (the section may still be adding it).
            RegisterCallback<AttachToPanelEvent>(_ => Start(() =>
            {
#if MYAVATAR_VPM
                Read(() => setup.PlanAsync(missing), "Before adding " + what, plan => plan.NothingToDo ? "Add " + what : "Download and add", accepted =>
                {
                    string note = setup.Install(avatar, accepted);
                    done(note, !setup.Waiting(avatar));
                }, status, done);
#else
                done(setup.Unavailable(missing), true);
#endif
            }));
        }

        private void Start(Action read)
        {
            detached = false;
            if (started) return;
            started = true;
            schedule.Execute(() => { if (!detached) read(); else started = false; });
        }

#if MYAVATAR_VPM
        /// <summary>A tool to install (no setup on an avatar follows): the same plan, then <paramref name="install"/> (its note, and whether it is a warning).</summary>
        internal PackageSetupPrompt(Func<System.Threading.Tasks.Task<VpmDependencyPlan>> plan, string what, Func<VpmDependencyPlan, (string note, bool warning)> install, Action<string, bool> done)
        {
            AddToClassList("package-setup");
            style.marginTop = 10;
            var status = new Label("Checking what " + what + " needs…"); status.AddToClassList("ft-note"); Add(status);
            RegisterCallback<DetachFromPanelEvent>(_ => detached = true);
            RegisterCallback<AttachToPanelEvent>(_ => Start(() => Read(plan, "Before installing " + what, read => read.NothingToDo ? "Open " + what : "Install", accepted =>
            {
                var (note, warning) = install(accepted);
                done(note, warning);
            }, status, done)));
        }

        private async void Read(Func<System.Threading.Tasks.Task<VpmDependencyPlan>> read, string title, Func<VpmDependencyPlan, string> confirm,
            Action<VpmDependencyPlan> install, Label status, Action<string, bool> done)
        {
            VpmDependencyPlan plan;
            try { plan = await read(); }
            catch (Exception ex)
            {
                if (!detached) done("Could not read what to download: " + ex.Message, true);
                return;
            }
            // Gone meanwhile (the section was built again): the new one asks again.
            if (detached) return;
            status.RemoveFromHierarchy();
            DependencyPlanView view = null;
            view = new DependencyPlanView(plan, title, accepted =>
            {
                // Installing blocks the editor: the pressed state paints first.
                view.SetEnabled(false);
                schedule.Execute(() => install(accepted)).StartingIn(40);
            }, () => done(null, false), confirm(plan));
            Add(view);
        }
#endif
    }
}
