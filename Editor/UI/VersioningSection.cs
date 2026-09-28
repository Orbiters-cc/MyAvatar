#if MYAVATAR_UNITGIT
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using Orbiters.UnitGit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // The project's history from Unit Git in miniature: the branch, what is not saved yet, the last commits as a graph,
    // and where it is backed up. Without a history it offers to create one, then to put it on GitHub, GitLab or an
    // existing remote. Nothing here pushes; the full graph opens in Unit Git.
    internal sealed class VersioningSection : AvatarSection
    {
        private const int CommitCount = 5;
        private readonly Label note;
        private readonly VisualElement summary, graph, setup;
        private bool loading, working, linkOpen;
        private UnitGitSummary current;
        // The outcome of the last action, kept while the history is read again.
        private string message; private bool messageWarning;

        internal VersioningSection() : base("Versioning")
        {
            var refresh = new IconButton(IconGlyph.Refresh, "Refresh", "Read the history again", Load);
            refresh.AddToClassList("orb-icon-button--small");
            Actions.Add(refresh);
            var open = MyAvatarEditor.Button("Open Unit Git", UnitGitOverview.OpenWindow);
            open.AddToClassList("avatar-link");
            open.tooltip = "See the full history, branches and changes in Unit Git.";
            Actions.Add(open);

            summary = new VisualElement(); summary.AddToClassList("git-summary"); Body.Add(summary);
            graph = new VisualElement(); graph.AddToClassList("git-graph"); Body.Add(graph);
            setup = new VisualElement(); Body.Add(setup);
            note = new Label(); note.AddToClassList("avatar-section__note"); Body.Add(note);

            UnitGitOverview.Changed += Load;
            EditorApplication.focusChanged += OnFocus;
            RegisterCallback<DetachFromPanelEvent>(_ => { UnitGitOverview.Changed -= Load; EditorApplication.focusChanged -= OnFocus; });
            Load();
        }

        // Coming back to Unity after committing in a terminal or on the website: read it again.
        private void OnFocus(bool focused) { if (focused) Load(); }

        private async void Load()
        {
            if (loading || working) return;
            loading = true;
            if (current == null) SetNote("Reading the history…", false);
            try { current = await UnitGitOverview.LoadAsync(CommitCount); }
            catch (Exception ex) { current = null; SetNote(ex.Message, true); }
            finally { loading = false; }
            if (current != null && panel != null) Show(current);
        }

        private void Show(UnitGitSummary git)
        {
            summary.Clear(); graph.Clear(); setup.Clear();
            if (!string.IsNullOrEmpty(git.Error)) SetNote(git.Error, true); else SetNote(message, messageWarning);
            if (!git.GitAvailable)
            {
                Setup("Git is not installed. Unit Git needs it to keep the history of your project.",
                    Action("Get Git", () => Application.OpenURL("https://git-scm.com/downloads"), true));
                return;
            }
            if (!git.HasRepository || !git.HasCommits)
            {
                Setup("No history yet. Create one to keep checkpoints of your avatar you can go back to, and back it up online.",
                    Action("Create history", CreateHistory, true));
                return;
            }

            Chip(summary, IconGlyph.Branch, git.Branch, "Current branch");
            Chip(summary, null, git.Changes == 0 ? "All saved" : $"{git.Changes} change{(git.Changes == 1 ? "" : "s")}",
                git.Changes == 0 ? "Nothing new since the last commit." : "Files changed since the last commit. Save or commit them in Unit Git.", git.Changes > 0);
            if (git.Ahead > 0 || git.Behind > 0) Chip(summary, null, $"↑{git.Ahead} ↓{git.Behind}", "Commits to push ↑ and to pull ↓ from the remote.");
            var spacer = new VisualElement(); spacer.AddToClassList("git-summary__spacer"); summary.Add(spacer);
            var remote = new Label(string.IsNullOrEmpty(git.RemoteUrl) ? "Only on this computer" : RemoteName(git.RemoteUrl));
            remote.AddToClassList("git-summary__remote");
            remote.EnableInClassList("warning", string.IsNullOrEmpty(git.RemoteUrl));
            remote.tooltip = string.IsNullOrEmpty(git.RemoteUrl) ? "No backup online yet." : git.RemoteUrl;
            summary.Add(remote);

            for (int i = 0; i < git.Commits.Count; i++) graph.Add(Commit(git.Commits[i], i == 0, i == git.Commits.Count - 1));
            if (string.IsNullOrEmpty(git.RemoteUrl)) RemoteSetup();
        }

        // One row of the graph: the lane with its dot, the subject, and when.
        private static VisualElement Commit(UnitGitSummaryCommit commit, bool first, bool last)
        {
            var row = new VisualElement(); row.AddToClassList("git-commit");
            row.tooltip = $"{commit.ShortHash} · {commit.Author}" + (string.IsNullOrEmpty(commit.Decorations) ? "" : "\n" + commit.Decorations);
            var lane = new VisualElement(); lane.AddToClassList("git-lane"); row.Add(lane);
            var line = new VisualElement(); line.AddToClassList("git-lane__line");
            line.EnableInClassList("git-lane__line--first", first); line.EnableInClassList("git-lane__line--last", last);
            lane.Add(line);
            var dot = new VisualElement(); dot.AddToClassList("git-lane__dot"); dot.EnableInClassList("git-lane__dot--head", commit.IsHead); lane.Add(dot);
            var subject = new Label(commit.Subject); subject.AddToClassList("git-commit__subject"); row.Add(subject);
            if (!string.IsNullOrEmpty(commit.Release)) { var release = new Label(commit.Release); release.AddToClassList("git-commit__release"); release.tooltip = "Release checkpoint"; row.Add(release); }
            var date = new Label(Short(commit.RelativeDate)); date.AddToClassList("git-commit__date"); row.Add(date);
            return row;
        }

        private void RemoteSetup()
        {
            var row = new VisualElement(); row.AddToClassList("git-setup"); setup.Add(row);
            var text = new Label("Back it up online:"); text.AddToClassList("git-setup__text"); row.Add(text);
            row.Add(Action("GitHub", () => CreateRemote(UnitGitRemoteProvider.GitHub), false));
            row.Add(Action("GitLab", () => CreateRemote(UnitGitRemoteProvider.GitLab), false));
            row.Add(Action("Existing…", () => { linkOpen = !linkOpen; Show(current); }, false));
            if (!linkOpen) return;
            var link = new VisualElement(); link.AddToClassList("git-setup"); link.AddToClassList("git-link"); setup.Add(link);
            var url = new TextField { tooltip = "HTTPS or SSH URL of an empty repository, e.g. https://github.com/you/avatar.git" };
            url.AddToClassList("git-link__field");
            link.Add(url);
            link.Add(Action("Connect", () => Work("Connecting…", () => UnitGitOverview.ConnectRemoteAsync(url.value), "Connected. Push from Unit Git when you are ready."), true));
            url.schedule.Execute(() => url.Focus());
        }

        private void CreateHistory()
        {
            if (!EditorUtility.DisplayDialog("Create the project history",
                    "Unit Git will add a .gitignore made for VRChat projects and record the whole project as its first commit. This can take a minute on a big project.\n\nNothing is sent online.",
                    "Create history", "Cancel")) return;
            Work("Creating the history… (the first commit of a big project takes a while)", UnitGitOverview.InitializeAsync, "History created.");
        }

        private async void CreateRemote(UnitGitRemoteProvider provider)
        {
            string site = provider == UnitGitRemoteProvider.GitLab ? "GitLab" : "GitHub";
            string cli = provider == UnitGitRemoteProvider.GitLab ? "GitLab CLI (glab)" : "GitHub CLI (gh)";
            SetNote("Looking for " + cli + "…", false);
            if (!await UnitGitOverview.IsCliAvailableAsync(provider))
            {
                SetNote(null, false);
                if (EditorUtility.DisplayDialog(site + " CLI needed",
                        $"Creating the repository from Unity uses the {cli}. Install it, then reopen Unity.\n\nYou can also create an empty repository on the {site} website and use “Existing…” with its URL.",
                        "Get " + cli, "Cancel"))
                    Application.OpenURL(provider == UnitGitRemoteProvider.GitLab ? "https://gitlab.com/gitlab-org/cli#installation" : "https://cli.github.com/");
                return;
            }
            string name = UnitGitOverview.DefaultRepositoryName;
            int choice = EditorUtility.DisplayDialogComplex("Back up on " + site,
                $"Create the private repository “{name}” on your {site} account and connect it to this project?\n\nIf the {cli} is not signed in yet, your browser opens to sign in (the one-time code is copied for you). Nothing is pushed: push from Unit Git when you are ready.",
                "Create private", "Cancel", "Create public");
            if (choice == 1) return;
            Work($"Creating “{name}” on {site}… finish signing in in your browser if it opened.",
                () => UnitGitOverview.CreateRemoteAsync(provider, name, choice == 0), $"Connected to {site}. Push from Unit Git when you are ready.");
        }

        // The button shows the work at once; the history is read again once Git answers.
        private async void Work(string progress, Func<Task<string>> operation, string done)
        {
            if (working) return;
            working = true;
            Body.Query<Button>().ForEach(b => b.SetEnabled(false));
            message = null;
            SetNote(progress, false);
            string result = null; bool failed = false;
            try { await operation(); result = done; }
            catch (Exception ex) { result = ex.Message; failed = true; }
            finally { working = false; }
            if (panel == null) return;
            linkOpen &= failed;
            message = result; messageWarning = failed;
            SetNote(result, failed);
            Load();
        }

        private void Setup(string text, Button button)
        {
            var row = new VisualElement(); row.AddToClassList("git-setup"); setup.Add(row);
            var label = new Label(text); label.AddToClassList("git-setup__text"); label.AddToClassList("git-setup__text--wrap"); row.Add(label);
            row.Add(button);
        }

        private static Button Action(string text, Action action, bool primary)
        {
            var button = MyAvatarEditor.Button(text, action);
            button.AddToClassList("git-button");
            if (primary) button.AddToClassList("git-button--primary");
            return button;
        }

        private static void Chip(VisualElement parent, IconGlyph? glyph, string text, string tooltip, bool warning = false)
        {
            var chip = new VisualElement { tooltip = tooltip }; chip.AddToClassList("git-chip"); chip.EnableInClassList("warning", warning);
            if (glyph.HasValue) { var icon = new VectorIcon(glyph.Value); icon.AddToClassList("git-chip__icon"); chip.Add(icon); }
            var label = new Label(text); label.AddToClassList("git-chip__text"); chip.Add(label);
            parent.Add(chip);
        }

        private void SetNote(string text, bool warning)
        {
            note.text = text ?? string.Empty;
            note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            note.EnableInClassList("warning", warning);
        }

        // "https://github.com/you/avatar.git" or "git@gitlab.com:you/avatar.git" → "github.com/you/avatar".
        private static string RemoteName(string url)
        {
            var match = Regex.Match(url, @"^(?:[a-z+]+://)?(?:[^@/]+@)?([^/:]+)[:/](.+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value + "/" + match.Groups[2].Value : url;
        }

        // "3 hours ago" → "3h", "2 weeks ago" → "2w": the graph keeps a narrow date column.
        private static string Short(string relative)
        {
            var match = Regex.Match(relative ?? "", @"^(\d+)\s+(second|minute|hour|day|week|month|year)");
            if (!match.Success) return relative;
            string unit = match.Groups[2].Value;
            string suffix = unit == "minute" ? "min" : unit == "month" ? "mo" : unit.Substring(0, 1);
            return match.Groups[1].Value + suffix;
        }
    }
}
#endif
