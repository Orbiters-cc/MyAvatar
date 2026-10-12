using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.MyAvatar.Editor.FaceTracking;
using Orbiters.MyAvatar.Editor.Optimization;
using Orbiters.Toolkit.Editor;
using Orbiters.Toolkit.Editor.VRChat;
using Orbiters.Toolkit.Editor.VRChat.Checks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Checks: the avatar built the way an upload builds it (VRCFury, Modular Avatar, MCB) in a hidden copy, then what won't
    /// work in VRChat: menus and parameters, animators, holes in the body, what Quest loses (and, when the other platform's
    /// version is open, whether both sync the same parameters). My Avatar's own optimization is left out: names stay as
    /// they are. The avatar never changes.
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
            public GameObject Root;
            public string Signature;
            public HiddenAvatarBuild Build, Other;
            public GameObject Counterpart;
            public List<Group> Groups;
            public string Error;
            public DateTime When;
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
            bool stale = run != null && !run.Running && run.Root != null && FaceTrackingTestBuild.SignatureOf(run.Root) != run.Signature;
            string state = run == null ? "none" : run.Running ? "running:" + Progress(run) : (run.Error ?? "done") + ":" + stale + ":" + run.When.Ticks;
            if (state == shown) return;
            shown = state;
            ShowHero(run, stale);
        }

        private static string Progress(Run run)
        {
            var build = run.Other ?? run.Build;
            if (build == null) return "Starting…";
            string who = build.Building != null ? build.Building + " builds" : "Building";
            return who + (run.Other != null ? " “" + run.Counterpart.name + "”" : "") + "… " + build.Done + " of " + build.Total;
        }

        private void ShowHero(Run run, bool stale)
        {
            hero.Clear();
            int problems = run?.Groups?.Sum(g => g.Issues.Count(i => i.Level == IssueLevel.Problem)) ?? 0;
            int warnings = run?.Groups?.Sum(g => g.Issues.Count(i => i.Level == IssueLevel.Warning)) ?? 0;
            string eyebrow = run == null ? "NOT CHECKED YET" : run.Running ? "CHECKING…" : run.Error != null ? "THE CHECK STOPPED"
                : problems + warnings == 0 ? "NOTHING TO FIX" : Count(problems, "PROBLEM") + (problems > 0 && warnings > 0 ? ", " : "") + Count(warnings, "WARNING");
            string eyebrowClass = run == null || run.Running ? "muted" : run.Error != null || problems + warnings > 0 ? "warning" : null;
            var frame = new ToolHero(hero, Glow, eyebrow, eyebrowClass, "Check the avatar as it uploads",
                "Builds a hidden copy the way an upload does (VRCFury, Modular Avatar, MCB) and looks for what won't work in VRChat: menus and parameters, " +
                "animators, holes in the body, what Quest loses. Your avatar doesn't change.");
            frame.Frame.name = "checks-hero";
            var ring = new VisualElement(); ring.AddToClassList("opt-badge"); frame.Badge.Add(ring);
            var icon = new VectorIcon(IconGlyph.Check); icon.AddToClassList("opt-badge__icon"); ring.Add(icon);

            var check = MyAvatarEditor.Button(run == null ? "Check" : "Check again", null);
            check.AddToClassList("ft-primary");
            check.tooltip = "Build a hidden copy as an upload would (about half a minute: VRCFury builds as it does at upload) and check it.";
            check.SetEnabled(run == null || !run.Running);
            check.clicked += () =>
            {
                // The press shows at once; the build starts on the next frame.
                check.SetEnabled(false);
                shown = null;
                ShowHero(new Run { Root = Root }, false);
                schedule.Execute(Start);
            };
            frame.Actions.Add(check);

            var line = new Label(); line.AddToClassList("ft-note"); frame.Frame.Add(line);
            if (run == null) line.text = "VRCFury freezes the editor for a few seconds while it builds, as it does at upload.";
            else if (run.Running) line.text = Progress(run);
            else if (run.Error != null) { line.text = "The build stopped: " + run.Error; line.AddToClassList("warning"); }
            else
            {
                line.text = "Checked " + Ago(run.When) + (run.Counterpart != null ? ", with “" + run.Counterpart.name + "” (the same avatar on the other platform)" : "") +
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
            return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? (int)span.TotalMinutes + " min ago" : when.ToString("HH:mm");
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
            if (Runs.TryGetValue(avatar.GetInstanceID(), out var old)) Release(old);
            var run = new Run { Root = root, Signature = FaceTrackingTestBuild.SignatureOf(root), When = DateTime.Now };
            Runs[avatar.GetInstanceID()] = run;
            run.Counterpart = QuestParity.Counterparts(root).FirstOrDefault();
            run.Build = HiddenAvatarBuild.Start(root, Skip);
            run.Build.Completed += () => Built(run);
            if (run.Build.Finished) Built(run);
        }

        /// <summary>The last check's issues, by group, or null while it runs or before any (tools, tests).</summary>
        internal static IReadOnlyList<(string Group, IReadOnlyList<AvatarIssue> Issues)> Results(MyAvatar avatar) =>
            avatar && Runs.TryGetValue(avatar.GetInstanceID(), out var run) && run.Groups != null
                ? run.Groups.Select(g => (g.Name, (IReadOnlyList<AvatarIssue>)g.Issues)).ToList() : null;

        // My Avatar's optimization renames objects and parameters (the issues would name them so) and VRCFury's compressor
        // reshuffles the synced parameters: the checks look at the avatar as its creator made it.
        private static bool Skip(Type type) =>
            type == typeof(UploadOptimizer.Structure) || type == typeof(UploadOptimizer.Materials) || type.Name == "ParameterCompressorHook";

        private static void Built(Run run)
        {
            if (run.Build.Error != null) { run.Error = run.Build.Error; Release(run); return; }
            if (run.Counterpart != null && run.Other == null)
            {
                run.Other = HiddenAvatarBuild.Start(run.Counterpart, Skip);
                run.Other.Completed += () => Built(run);
                if (!run.Other.Finished) return;
            }
            try
            {
                var copy = run.Build.Copy;
                var quest = QuestParity.Find(copy);
                if (run.Other != null)
                {
                    if (run.Other.Error == null) quest.AddRange(QuestParity.Compare(copy, run.Other.Copy, run.Counterpart.name));
                    else quest.Add(new AvatarIssue(IssueLevel.Note, "“" + run.Counterpart.name + "” couldn't be built to compare", run.Other.Error));
                }
                run.Groups = new List<Group>
                {
                    new Group { Name = "Menu and parameters", Issues = MenuAudit.Find(copy) },
                    new Group { Name = "Animators", Issues = AnimatorFlow.Find(copy) },
                    new Group { Name = "Holes in the body", Issues = BodyHoles.Find(copy) },
                    new Group { Name = "Quest", Issues = quest },
                };
                run.When = DateTime.Now;
                Debug.Log("[My Avatar] Checks of “" + run.Root.name + "”: " + string.Join("; ", run.Groups.Select(g => g.Name + " " + g.Issues.Count)) + ".");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                run.Error = ex.Message;
            }
            finally { Release(run); }
        }

        private static void Release(Run run)
        {
            run.Build?.Release();
            run.Other?.Release();
        }
    }
}
