using System;
using System.Collections.Generic;
using System.Linq;
#if MYAVATAR_VPM
using Orbiters.Toolkit.Editor.Vpm;
#endif

namespace Orbiters.MyAvatar.Editor.Gallery
{
    /// <summary>One package of the plan shown before a gallery installation changes the project's packages.</summary>
    internal sealed class GalleryPackageRow
    {
        public const string Install = "install", Update = "update", Downgrade = "downgrade", Remove = "remove", Keep = "keep", Blocked = "blocked", Missing = "missing";
        /// <summary>The section it is shown in: the VRChat SDK, what the asset needs, the project's other tools, what stays.</summary>
        public const string Sdk = "sdk", Needed = "needed", Tools = "tools", Kept = "kept";

        public string id, name, from, to, kind, group, why;

        public bool Changes => kind == Install || kind == Update || kind == Downgrade || kind == Remove;
        public bool Blocks => kind == Blocked || kind == Missing;

        /// <summary>"Update", "Remove", "Installed"…</summary>
        public string Tag
        {
            get
            {
                switch (kind)
                {
                    case Install: return "Install";
                    case Update: return "Update";
                    case Downgrade: return "Downgrade";
                    case Remove: return "Remove";
                    case Keep: return "Installed";
                    case Missing: return "Not found";
                    default: return "Blocks";
                }
            }
        }

        /// <summary>"3.7.5 → 3.10.6", "1.1436.0", "3.9.3".</summary>
        public string Versions
        {
            get
            {
                switch (kind)
                {
                    case Update: case Downgrade: return from + " → " + to;
                    case Install: case Keep: return to;
                    default: return from ?? "";
                }
            }
        }
    }

    /// <summary>
    /// The package plan of a gallery installation, as its dialog shows it, saved in the job (the journal survives the
    /// reload a VRChat SDK update causes): every package with its versions and why, then where the plan is (waiting for
    /// approval, being applied, Unity reloading, failed, blocked).
    /// </summary>
    internal sealed class GalleryPackagePlan
    {
        public const string Review = "review", Applying = "applying", Reloading = "reloading", Failed = "failed", Blocked = "blocked";

        public string state = Review;
        public List<GalleryPackageRow> rows = new List<GalleryPackageRow>();
        /// <summary>VPM repositories the plan adds to the user's Creator Companion settings.</summary>
        public List<string> repositories = new List<string>();
        /// <summary>Blocked: what the user can do.</summary>
        public string advice;
        /// <summary>Failed: what went wrong; and the packages already changed before it did.</summary>
        public string error;
        public List<string> changed = new List<string>();
        /// <summary>Applying: the step running, of how many, and 0..1 overall.</summary>
        public string progressText;
        public int step, steps;
        public float progress;

        public IEnumerable<GalleryPackageRow> Changes => rows.Where(r => r.Changes);
        public GalleryPackageRow Blocker => rows.FirstOrDefault(r => r.Blocks);
        public bool Busy => state == Applying || state == Reloading;

        private GalleryPackageRow SdkRow => rows.Where(r => r.group == GalleryPackageRow.Sdk && r.Changes)
            .OrderBy(r => r.id == "com.vrchat.avatars" ? 0 : 1).FirstOrDefault();

        private static string Plural(int count, string one, string many = null) => count == 1 ? one : many ?? one + "s";

        internal string Eyebrow
        {
            get
            {
                switch (state)
                {
                    case Applying: case Reloading: return "UPDATING PACKAGES";
                    case Failed: return "THE PACKAGES COULD NOT BE CHANGED";
                    case Blocked: return "A PACKAGE BLOCKS THIS INSTALLATION";
                    default: return SdkRow != null ? "VRCHAT SDK UPDATE NEEDED" : "PACKAGES NEEDED";
                }
            }
        }

        internal string Title(string asset)
        {
            int count = Changes.Count();
            switch (state)
            {
                case Applying: return $"Changing {count} {Plural(count, "package")} for {asset}…";
                case Reloading: return "Unity is taking in the new packages…";
                case Failed: return $"{asset} is not installed yet";
                case Blocked: return $"{Blocker?.name ?? "A package"} keeps {asset} from installing";
            }
            var sdk = SdkRow;
            if (sdk != null)
            {
                int others = Changes.Count(r => r.group != GalleryPackageRow.Sdk);
                return $"{asset} needs VRChat SDK {sdk.to}" + (others > 0 ? $" and {others} other package {Plural(others, "change")}" : "");
            }
            if (Changes.All(r => r.kind == GalleryPackageRow.Install)) return $"{asset} needs {count} {Plural(count, "package")} this project does not have yet";
            return $"{asset} needs {count} package {Plural(count, "change")}";
        }

        internal string Text(string asset)
        {
            switch (state)
            {
                case Applying: return $"Keep Unity open: the VRChat Creator Companion's resolver downloads them, Unity reloads its scripts, then {asset} installs by itself.";
                case Reloading: return $"Its scripts reload (this can take a minute), then {asset} installs by itself.";
                case Failed: return error ?? "The packages could not be changed.";
                case Blocked: return Blocker?.why ?? "No set of versions works with everything this project has.";
            }
            var sdk = SdkRow;
            bool tools = Changes.Any(r => r.group == GalleryPackageRow.Tools);
            string what = sdk != null
                ? $"This project has VRChat SDK {sdk.from ?? "(none)"}." + (tools ? " The tools below that only work with that version are changed to versions that work with the new one." : "")
                : tools ? "The tools below that do not work with the new versions are changed to versions that do."
                : "They come from the VPM repositories Orbiters knows. Nothing else in your project is changed.";
            return what + $" Nothing changes until you choose {Primary}; the packages then change together, Unity reloads its scripts and {asset} installs by itself.";
        }

        /// <summary>The primary button; null while the plan is being applied.</summary>
        internal string Primary
        {
            get
            {
                switch (state)
                {
                    case Applying: case Reloading: return null;
                    case Failed: return "Retry";
                    case Blocked: return "Check again";
                    default: return Changes.All(r => r.kind == GalleryPackageRow.Install) ? "Install packages and continue" : "Update packages and continue";
                }
            }
        }

        /// <summary>What to know before approving (warnings first): removals, repositories added, packages already changed, what to do.</summary>
        internal IEnumerable<(string text, bool warning)> Notes()
        {
            var removed = rows.Where(r => r.kind == GalleryPackageRow.Remove).Select(r => r.name).ToList();
            if (removed.Count > 0 && (state == Review || state == Failed))
                yield return ($"{string.Join(", ", removed)} {(removed.Count == 1 ? "is" : "are")} removed from this project: no version of {(removed.Count == 1 ? "it" : "them")} works with the new packages. " +
                              $"Add {(removed.Count == 1 ? "it" : "them")} again with the VRChat Creator Companion once {(removed.Count == 1 ? "its author supports" : "their authors support")} them.", true);
            if (repositories.Count > 0 && state == Review)
                yield return ($"Adds {string.Join(", ", repositories.Select(Host))} to your Creator Companion's repositories (every project uses them).", false);
            if (changed.Count > 0 && state == Failed)
                yield return ("Already changed: " + string.Join(", ", changed) + ". Retry goes on from there.", false);
            if (state == Blocked && !string.IsNullOrEmpty(advice)) yield return (advice, true);
        }

        internal static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath.TrimEnd('/') : url;

        /// <summary>The card's note and the job's status for the plan's state.</summary>
        internal string Summary(string asset) => state == Review ? Title(asset) + "." : state == Failed ? "The packages could not be changed: " + error : Title(asset);

        /// <summary>The plan's lines as plain text (the journal's question lines).</summary>
        internal List<string> Lines() => rows.Select(r => $"{r.Tag} {r.name} {r.Versions}".TrimEnd() + (string.IsNullOrEmpty(r.why) || r.kind == GalleryPackageRow.Keep ? "" : " — " + r.why)).ToList();
    }

    internal static class GalleryPackages
    {
#if MYAVATAR_VPM
        /// <summary>The dialog's model of a plan for <paramref name="asset"/>; <paramref name="repositoryAdded"/> tells the repositories the user has.</summary>
        internal static GalleryPackagePlan From(VpmDependencyPlan plan, string asset, Func<string, bool> repositoryAdded = null)
        {
            var model = new GalleryPackagePlan { state = plan.CanApply ? GalleryPackagePlan.Review : GalleryPackagePlan.Blocked };
            foreach (var step in plan.Steps)
            {
                var row = new GalleryPackageRow { id = step.Id, name = step.DisplayName ?? step.Id, from = step.From, to = step.To, kind = Kind(step.Action) };
                bool sdk = VpmDependencyPlan.Protected(step.Id);
                row.group = sdk ? GalleryPackageRow.Sdk : step.Action == VpmStepAction.Keep ? GalleryPackageRow.Kept
                    : step.Limiting ? GalleryPackageRow.Tools : GalleryPackageRow.Needed;
                // A package the asset (or what it needs) asks for says who; a tool changed, removed or blocking says what limits it.
                string needed = step.RequiredBy.Count > 0 ? "Needed by " + string.Join(", ", step.RequiredBy) : null;
                row.why = step.Limiting || row.Blocks ? step.Reason ?? needed : needed ?? step.Reason;
                model.rows.Add(row);
            }
            model.rows = model.rows.OrderBy(r => r.Blocks ? 0 : Order(r.group)).ToList();
            model.repositories = plan.Repositories.Where(url => !(repositoryAdded ?? VpmDependencies.RepositoryAdded)(url)).ToList();
            var blocker = model.Blocker;
            if (blocker != null) model.advice = Advice(blocker, asset);
            return model;
        }

        private static string Kind(VpmStepAction action)
        {
            switch (action)
            {
                case VpmStepAction.Install: return GalleryPackageRow.Install;
                case VpmStepAction.Upgrade: return GalleryPackageRow.Update;
                case VpmStepAction.Downgrade: return GalleryPackageRow.Downgrade;
                case VpmStepAction.Remove: return GalleryPackageRow.Remove;
                case VpmStepAction.Keep: return GalleryPackageRow.Keep;
                case VpmStepAction.Missing: return GalleryPackageRow.Missing;
                default: return GalleryPackageRow.Blocked;
            }
        }

        /// <summary>
        /// The changes of a plan read again after the user approved <paramref name="approved"/> are among those approved
        /// (the same package to the same version, or the same removal): it is applied without asking again.
        /// </summary>
        internal static bool Covered(VpmDependencyPlan plan, GalleryPackagePlan approved)
        {
            if (approved == null || !plan.CanApply) return false;
            return plan.Changes.All(step => approved.rows.Any(r => r.Changes && string.Equals(r.id, step.Id, StringComparison.OrdinalIgnoreCase) &&
                (step.Action == VpmStepAction.Remove ? r.kind == GalleryPackageRow.Remove : r.kind != GalleryPackageRow.Remove && r.to == step.To)));
        }
#endif

        private static int Order(string group) =>
            group == GalleryPackageRow.Sdk ? 1 : group == GalleryPackageRow.Needed ? 2 : group == GalleryPackageRow.Tools ? 3 : 4;

        /// <summary>The section heading of a group of rows.</summary>
        internal static string Heading(string group, string asset)
        {
            switch (group)
            {
                case GalleryPackageRow.Sdk: return "VRChat SDK";
                case GalleryPackageRow.Needed: return "Needed by " + asset;
                case GalleryPackageRow.Tools: return "Other tools in this project";
                default: return "Already installed";
            }
        }

        // What the user can do about the package that blocks.
        internal static string Advice(GalleryPackageRow blocker, string asset)
        {
            if (blocker.kind == GalleryPackageRow.Missing)
                return $"Add the VPM repository that offers {blocker.name} in the VRChat Creator Companion (Settings › Packages), then choose Check again.";
            if (blocker.from != null && !blocker.id.StartsWith("com.vrchat.", StringComparison.OrdinalIgnoreCase))
                return $"Update or remove {blocker.name} in the VRChat Creator Companion (or ask its author for a version that works with it), then choose Check again. Cancel leaves the project as it is.";
            return $"Change it in the VRChat Creator Companion, then choose Check again. Cancel leaves the project as it is.";
        }
    }
}
