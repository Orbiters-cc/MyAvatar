using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Checks: the avatar built the way an upload builds it (VRCFury, Modular Avatar, MCB) in a hidden copy, then what won't
    /// work in VRChat: menus and parameters, animators, holes in the body, what Quest loses (for the version Quest players
    /// see) and, when the other platform's version is open, whether both sync the same parameters. My Avatar's own
    /// optimization is left out: names stay as they are. The avatar never changes.
    /// </summary>
    internal sealed class ChecksSection : AvatarSection
    {
        private static readonly string[] StyleSheets = { "Packages/orbiters.myavatar/Editor/UI/optimization.uss", "Packages/orbiters.myavatar/Editor/UI/checks.uss" };
        private static readonly Color Glow = new Color(0f, .85f, .43f, .18f);

        private sealed class Group
        {
            public string Name;
            public List<AvatarIssue> Issues;
        }

        private sealed class Run
        {
            public int Key;
            public GameObject Root;
            // Names as they were: the objects may be deleted while the check runs.
            public string RootName, CounterpartName;
            public string Signature;
            public HiddenAvatarBuild Build, Other;
            public QuestParity.Counterpart Counterpart;
            public bool ForQuest, Cancelled;
            public List<Group> Groups;
            public string Error;
            public DateTime When;
            // The check shown before this one, shown again if this one is cancelled (the progress bar's Cancel).
            public Run Previous;
            public bool Running => Groups == null && Error == null;
        }

        // The last check of each avatar, kept while the editor runs: the Inspector rebuilds often.
        private static readonly Dictionary<int, Run> Runs = new Dictionary<int, Run>();

        private readonly MyAvatar avatar;
        private readonly VisualElement hero, results;
        private IVisualElementScheduledItem poll;
        private string shown;

        internal ChecksSection(MyAvatar avatar) : base("Checks", card: false)
        {
            this.avatar = avatar;
            ToolHero.Style(this);
            foreach (var path in StyleSheets)
                if (AssetDatabase.LoadAssetAtPath<StyleSheet>(path) is StyleSheet sheet) styleSheets.Add(sheet);
            AddToClassList("chk");
            hero = new VisualElement(); Body.Add(hero);
            results = new VisualElement(); results.AddToClassList("chk-results");
            RegisterCallback<AttachToPanelEvent>(_ => { poll = schedule.Execute(Refresh).Every(500); Refresh(); });
            RegisterCallback<DetachFromPanelEvent>(_ => poll?.Pause());
            Refresh();
        }

        private GameObject Root => avatar ? TextureOptimization.AvatarRoot(avatar) : null;

        private Run Current => avatar && Runs.TryGetValue(avatar.GetInstanceID(), out var run) && run.Root == Root ? run : null;

        private void Refresh()
        {
            var run = Current;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            // The signature is measured again only after the editor reported a change.
            bool stale = run != null && !run.Running && run.Error == null && run.Root != null && AvatarSignature.Of(run.Root) != run.Signature;
            string state = (run == null ? "none" : run.Running ? "running:" + Progress(run) : (run.Error ?? "done") + ":" + stale + ":" + run.When.Ticks + ":" + Ago(run.When)) +
                           ":" + playing;
            if (state == shown) return;
            shown = state;
            ShowHero(run, stale, playing);
        }

        private static string Progress(Run run) =>
            run.Other != null ? "Building “" + run.CounterpartName + "” to compare…"
            : run.Build != null ? "Building “" + run.RootName + "” as an upload would…" : "Starting…";

        private void ShowHero(Run run, bool stale, bool playing)
        {
            hero.Clear();
            var issues = run?.Groups?.SelectMany(g => g.Issues).ToList() ?? new List<AvatarIssue>();
            int problems = issues.Count(i => i.Level == IssueLevel.Problem), warnings = issues.Count(i => i.Level == IssueLevel.Warning);
            int notes = issues.Count - problems - warnings;
            string eyebrow = run == null ? "NOT CHECKED YET" : run.Running ? "CHECKING…" : run.Error != null ? "THE CHECK STOPPED"
                : problems + warnings == 0 ? "NOTHING TO FIX" + (notes > 0 ? ", " + Count(notes, "NOTE") : "")
                : Count(problems, "PROBLEM") + (problems > 0 && warnings > 0 ? ", " : "") + Count(warnings, "WARNING");
            string eyebrowClass = run == null || run.Running ? "muted" : run.Error != null || problems + warnings > 0 ? "warning" : null;
            var frame = new ToolHero(hero, Glow, eyebrow, eyebrowClass, "Check the avatar as it uploads",
                "Builds a hidden copy the way an upload does (VRCFury, Modular Avatar, MCB) and looks for what won't work in VRChat: menus and parameters, " +
                "animators, holes in the body, what Quest loses. Your avatar doesn't change.");
            frame.Frame.name = "checks-hero";
            var ring = new VisualElement(); ring.AddToClassList("opt-badge"); frame.Badge.Add(ring);
            var icon = new VectorIcon(IconGlyph.Check); icon.AddToClassList("opt-badge__icon"); ring.Add(icon);

            bool running = run != null && run.Running;
            var check = MyAvatarEditor.Button(running ? "Checking…" : run == null ? "Check" : "Check again", null);
            check.AddToClassList("ft-primary");
            check.tooltip = playing ? "Leave Play Mode to check." : "Build a hidden copy as an upload would (the editor pauses while it builds, as at upload) and check it.";
            check.SetEnabled(!running && !playing);
            check.clicked += () =>
            {
                // The press shows at once; the build starts on the next frame.
                check.SetEnabled(false);
                shown = null;
                ShowHero(new Run { Root = Root, RootName = Root ? Root.name : "" }, false, false);
                schedule.Execute(Start);
            };
            frame.Actions.Add(check);

            var line = new Label(); line.AddToClassList("ft-note"); frame.Frame.Add(line);
            if (playing && !running) line.text = "Leave Play Mode to check: the check builds a copy of the avatar.";
            else if (run == null) line.text = "The editor pauses while the copy builds, as it does at upload. Its progress bar shows each step; Cancel there stops the check.";
            else if (running) line.text = Progress(run) + " Cancel on the progress bar stops it.";
            else if (run.Error != null) { line.text = "The build stopped: " + run.Error; line.AddToClassList("warning"); }
            else
            {
                line.text = "Checked " + Ago(run.When) + (run.CounterpartName != null ? ", with “" + run.CounterpartName + "” (the same avatar on the other platform)" : "") +
                            (stale ? ". The avatar changed since: check again." : ".");
                if (stale) line.AddToClassList("warning");
            }
            if (run?.Groups != null)
            {
                frame.Frame.Add(results);
                ShowResults(run.Groups);
            }
        }

        private static string Count(int n, string what) => n == 0 ? "" : n + " " + what + (n == 1 ? "" : "S");

        private static string Ago(DateTime when)
        {
            var span = DateTime.Now - when;
            return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? (int)span.TotalMinutes + " min ago" : "at " + when.ToString("HH:mm");
        }

        private void ShowResults(List<Group> groups)
        {
            results.Clear();
            foreach (var group in groups)
            {
                var card = new VisualElement(); card.AddToClassList("chk-group"); results.Add(card);
                var head = new VisualElement(); head.AddToClassList("chk-group__head"); card.Add(head);
                var title = new Label(group.Name); title.AddToClassList("opt-estimate__title"); head.Add(title);
                int problems = group.Issues.Count(i => i.Level == IssueLevel.Problem), warnings = group.Issues.Count(i => i.Level == IssueLevel.Warning);
                var value = new Label(group.Issues.Count == 0 ? "Nothing to fix"
                    : string.Join(" · ", new[] { problems > 0 ? problems + " problem" + (problems == 1 ? "" : "s") : null,
                        warnings > 0 ? warnings + " warning" + (warnings == 1 ? "" : "s") : null,
                        group.Issues.Count - problems - warnings > 0 ? group.Issues.Count - problems - warnings + " note" + (group.Issues.Count - problems - warnings == 1 ? "" : "s") : null }
                        .Where(s => s != null)));
                value.AddToClassList("chk-group__value");
                value.EnableInClassList("chk-group__value--problem", problems > 0);
                value.EnableInClassList("chk-group__value--warning", problems == 0 && warnings > 0);
                value.EnableInClassList("chk-group__value--note", problems + warnings == 0 && group.Issues.Count > 0);
                head.Add(value);
                foreach (var issue in group.Issues.OrderByDescending(i => i.Level))
                {
                    var row = new VisualElement(); row.AddToClassList("chk-issue"); card.Add(row);
                    var dot = new VisualElement(); dot.AddToClassList("chk-issue__dot"); dot.AddToClassList("chk-issue__dot--" + issue.Level.ToString().ToLowerInvariant()); row.Add(dot);
                    var texts = new VisualElement(); texts.AddToClassList("chk-issue__texts"); row.Add(texts);
                    var name = new Label(issue.Title); name.AddToClassList("chk-issue__title"); texts.Add(name);
                    var detail = new Label(issue.Detail); detail.AddToClassList("chk-issue__detail"); texts.Add(detail);
                    if (!string.IsNullOrEmpty(issue.Where)) { var where = new Label(issue.Where); where.AddToClassList("chk-issue__where"); texts.Add(where); }
                }
            }
        }

        // ---- Running ----

        private void Start()
        {
            Check(avatar);
            shown = null;
            Refresh();
        }

        /// <summary>Starts checking the avatar <paramref name="avatar"/> is on; the section shows the run and its results.</summary>
        internal static void Check(MyAvatar avatar)
        {
            var root = avatar ? TextureOptimization.AvatarRoot(avatar) : null;
            if (root == null) return;
            int key = avatar.GetInstanceID();
            Runs.TryGetValue(key, out var old);
            var previous = old == null ? null : old.Running ? old.Previous : old;
            if (previous != null) previous.Previous = null;
            var run = new Run { Key = key, Root = root, RootName = root.name, When = DateTime.Now, Previous = previous };
            if (old != null && old.Running) Cancel(old);
            Runs[key] = run;
            try
            {
                // VRCFury would stop the build with a question a hidden build can't answer: these are the results.
                var blockers = HiddenAvatarBuild.Blockers(root);
                if (blockers.Count > 0)
                {
                    blockers.Add(new AvatarIssue(IssueLevel.Note, "The other checks wait for these",
                        "VRCFury stops the build on them with a question only you can answer, so the avatar isn't built: fix them and check again."));
                    run.Groups = new List<Group> { new Group { Name = "Upload", Issues = blockers } };
                    run.Signature = AvatarSignature.Of(root);
                    return;
                }
                run.Counterpart = QuestParity.CounterpartOf(root);
                run.CounterpartName = run.Counterpart?.Avatar.name;
                run.ForQuest = QuestParity.ForQuest(root, run.Counterpart);
                run.Build = HiddenAvatarBuild.Start(root, Skip);
                run.Build.Completed += () => Built(run);
                if (run.Build.Finished) Built(run);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                run.Error = ex.Message;
                Release(run);
            }
        }

        /// <summary>Stops a running check (another check of the avatar replaces it); the check shown before it comes back.</summary>
        private static void Cancel(Run run)
        {
            if (!run.Running) return;
            run.Cancelled = true;
            Release(run);
            Restore(run);
        }

        private static void Restore(Run run)
        {
            if (!Runs.TryGetValue(run.Key, out var now) || now != run) return;
            if (run.Previous != null) Runs[run.Key] = run.Previous;
            else Runs.Remove(run.Key);
        }

        /// <summary>The last check's issues, by group, or null while it runs or before any (tools, tests).</summary>
        internal static IReadOnlyList<(string Group, IReadOnlyList<AvatarIssue> Issues)> Results(MyAvatar avatar) =>
            avatar && Runs.TryGetValue(avatar.GetInstanceID(), out var run) && run.Groups != null
                ? run.Groups.Select(g => (g.Name, (IReadOnlyList<AvatarIssue>)g.Issues)).ToList() : null;

        // My Avatar's optimization renames objects and parameters (the issues would name them so) and VRCFury's compressor
        // reshuffles the synced parameters (and may ask whether to): the checks look at the avatar as its creator made it.
        private static bool Skip(Type type) =>
            type == typeof(UploadOptimizer.Structure) || type == typeof(UploadOptimizer.Materials) || type.FullName == "VF.Hooks.ParameterCompressorHook";

        private static void Built(Run run)
        {
            if (!run.Running) return;
            if (run.Cancelled || run.Build.Cancelled || run.Other != null && run.Other.Cancelled)
            {
                run.Cancelled = true;
                Release(run);
                Restore(run);
                return;
            }
            if (run.Build.Error != null) { run.Error = run.Build.Error; Release(run); return; }
            if (!run.Build.Finished) return;
            // What the check saw, measured once the build saved what it saves (VRCFury saves every unsaved asset first).
            if (run.Signature == null && run.Root != null) run.Signature = AvatarSignature.Of(run.Root);
            if (run.Counterpart != null && run.Other == null)
            {
                // A counterpart deleted since fails to build: the comparison says so.
                run.Other = HiddenAvatarBuild.Start(run.Counterpart.Avatar, Skip);
                run.Other.Completed += () => Built(run);
                if (!run.Other.Finished) return;
            }
            if (run.Other != null && !run.Other.Finished) return;
            // Play Mode or a reload closed every copy: nothing is left to check.
            if (run.Build.Copy == null) { run.Error = run.Other?.Error ?? "the copy was closed"; Release(run); return; }
            try
            {
                run.Groups = Checks(run);
                run.When = DateTime.Now;
                Debug.Log("[My Avatar] Checks of “" + run.RootName + "”: " + string.Join("; ", run.Groups.Select(g => g.Name + " " + g.Issues.Count)) + ".");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                run.Error = ex.Message;
            }
            finally { Release(run); }
        }

        private static List<Group> Checks(Run run)
        {
            var copy = run.Build.Copy;
            // Read once for the checks that need it: what the built avatar animates and reads.
            AvatarUsage usage = null;
            AvatarUsage Usage() => usage ??= new AvatarUsage(copy);
            return new List<Group>
            {
                Guarded("Menu and parameters", () => MenuAudit.Find(copy, Usage())),
                Guarded("Animators", () => AnimatorFlow.Find(copy)),
                Guarded("Holes in the body", () => BodyHoles.Find(copy, Usage())),
                Guarded("Quest", () => Quest(run, copy)),
            };
        }

        // One check's issues; a check that throws says so in its own group, the others still show.
        private static Group Guarded(string name, Func<List<AvatarIssue>> find)
        {
            try { return new Group { Name = name, Issues = find() }; }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return new Group { Name = name, Issues = new List<AvatarIssue> { new AvatarIssue(IssueLevel.Warning, "This check stopped", ex.Message) } };
            }
        }

        private static List<AvatarIssue> Quest(Run run, GameObject copy)
        {
            var issues = new List<AvatarIssue>();
            if (run.ForQuest) issues.AddRange(QuestParity.Find(copy));
            else if (run.Counterpart == null)
                issues.Add(new AvatarIssue(IssueLevel.Note, "Checked as a PC avatar",
                    "No Quest version of it is open (another avatar with the same blueprint ID, or its Android version in the SDK's build panel) and the build target is PC: " +
                    "what Quest loses isn't checked."));
            else issues.Add(new AvatarIssue(IssueLevel.Note, "Quest players see “" + run.CounterpartName + "”", "Check it for what Quest loses."));
            if (run.Other == null) return issues;
            if (run.Other.Error == null) issues.AddRange(QuestParity.Compare(copy, run.Other.Copy, run.CounterpartName));
            else issues.Add(new AvatarIssue(IssueLevel.Warning, "“" + run.CounterpartName + "” couldn't be built to compare", "The build stopped: " + run.Other.Error + "."));
            return issues;
        }

        private static void Release(Run run)
        {
            run.Build?.Release();
            run.Other?.Release();
        }
    }
}
