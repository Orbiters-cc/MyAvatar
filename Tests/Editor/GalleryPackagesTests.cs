#if MYAVATAR_VPM
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.MyAvatar.Editor.Gallery;
using Orbiters.Toolkit.Editor.Vpm;

namespace Orbiters.MyAvatar.Editor.Tests
{
    /// <summary>A project's packages and what its repositories offer, for plans in tests.</summary>
    internal sealed class TestVpmCatalog : IVpmCatalog
    {
        public readonly Dictionary<string, string> installed = new Dictionary<string, string>();
        public readonly List<VpmCandidate> available = new List<VpmCandidate>();
        public readonly Dictionary<string, string> names = new Dictionary<string, string>();
        public string Installed(string id) => installed.TryGetValue(id, out var v) ? v : null;
        public bool Developed(string id) => false;
        public IEnumerable<VpmCandidate> Versions(string id) => VpmCandidates.Newest(available.Where(c => c.Id == id));
        public string DisplayName(string id) => names.TryGetValue(id, out var name) ? name : id;
        public IReadOnlyDictionary<string, string> InstalledDependencies(string id) =>
            installed.TryGetValue(id, out var v) ? available.FirstOrDefault(c => c.Id == id && c.Version == v)?.Dependencies ?? new Dictionary<string, string>() : new Dictionary<string, string>();
        public IEnumerable<string> InstalledPackages() => installed.Keys;

        public TestVpmCatalog Add(string id, string name, string version, params (string id, string range)[] deps)
        {
            names[id] = name;
            available.Add(new VpmCandidate { Id = id, Version = version, DisplayName = name, RepositoryUrl = "https://vpm.example.org/" + id + "/index.json", Dependencies = deps.ToDictionary(d => d.id, d => d.range) });
            return this;
        }

        /// <summary>The N4N0 sample avatar project: VRChat SDK 3.7.5 and three tools that only work below 3.8.</summary>
        internal static TestVpmCatalog SampleProject(bool oscUpdate = true)
        {
            var catalog = new TestVpmCatalog();
            foreach (string v in new[] { "3.7.5", "3.7.6", "3.10.6" })
            {
                catalog.Add("com.vrchat.base", "VRChat SDK - Base", v);
                catalog.Add("com.vrchat.avatars", "VRChat SDK - Avatars", v, ("com.vrchat.base", v));
            }
            catalog.Add("vrchat.blackstartx.gesture-manager", "Gesture Manager", "3.9.3", ("com.vrchat.avatars", ">=3.5.2 <3.8.X"))
                .Add("vrchat.blackstartx.gesture-manager", "Gesture Manager", "3.9.8", ("com.vrchat.avatars", ">=3.5.2 < 3.11.X"))
                .Add("dev.vrlabs.av3manager", "Avatars 3.0 Manager", "2.0.34", ("com.vrchat.avatars", "3.2 - 3.7"))
                .Add("dev.vrlabs.av3manager", "Avatars 3.0 Manager", "2.1.2", ("com.vrchat.avatars", "3.2 - 3.10"))
                .Add("dev.hai-vr.fuck-osc-config", "Haï ~ Auto-reset OSC config file", "1.1.5", ("com.vrchat.avatars", ">=3.1.0 <3.8.0-a"))
                .Add("com.vrcfury.vrcfury", "VRCFury", "1.1436.0")
                .Add("orbiters.toolkit", "Orbiters Toolkit", "0.4.11");
            if (oscUpdate) catalog.Add("dev.hai-vr.fuck-osc-config", "Haï ~ Auto-reset OSC config file", "1.1.6", ("com.vrchat.avatars", ">=3.1.0 <999.0.0-a"));
            foreach (var (id, v) in new[] { ("com.vrchat.base", "3.7.5"), ("com.vrchat.avatars", "3.7.5"), ("vrchat.blackstartx.gesture-manager", "3.9.3"),
                         ("dev.vrlabs.av3manager", "2.0.34"), ("dev.hai-vr.fuck-osc-config", "1.1.5"), ("orbiters.toolkit", "0.4.11") })
                catalog.installed[id] = v;
            return catalog;
        }

        internal static IEnumerable<VpmDependencyPlan.Requirement> N4N0 => new[]
        {
            new VpmDependencyPlan.Requirement("com.vrchat.base", ">=3.10.3", "N4N0"), new VpmDependencyPlan.Requirement("com.vrchat.avatars", ">=3.10.3", "N4N0"),
            new VpmDependencyPlan.Requirement("com.vrcfury.vrcfury", ">=1.1434.0", "N4N0"), new VpmDependencyPlan.Requirement("orbiters.toolkit", ">=0.4.10", "N4N0"),
        };

        internal static VpmDependencyPlan SamplePlan(bool oscUpdate = true) => VpmDependencyPlan.ResolveForApproval(N4N0, SampleProject(oscUpdate));
    }

    // The package dialog's model: rows, their sections and the dialog's wording in each state.
    public sealed class GalleryPackagesTests
    {
        private static GalleryPackagePlan Model(VpmDependencyPlan plan) => GalleryPackages.From(plan, "N4N0", _ => false);

        [Test] public void TheSampleProjectsPlanListsEveryPackageWithItsVersionsAndWhy()
        {
            var model = Model(TestVpmCatalog.SamplePlan());
            Assert.That(model.state, Is.EqualTo(GalleryPackagePlan.Review));
            var rows = model.rows.ToDictionary(r => r.id);
            Assert.That(model.rows.Select(r => r.group).Distinct(), Is.EqualTo(new[] { GalleryPackageRow.Sdk, GalleryPackageRow.Needed, GalleryPackageRow.Tools, GalleryPackageRow.Kept }),
                "SDK first, then what N4N0 needs, then the other tools, then what stays");

            var sdk = rows["com.vrchat.avatars"];
            Assert.That((sdk.Tag, sdk.Versions, sdk.group), Is.EqualTo(("Update", "3.7.5 → 3.10.6", GalleryPackageRow.Sdk)));
            Assert.That(sdk.why, Is.EqualTo("Needed by N4N0 (>=3.10.3)"));
            Assert.That((rows["com.vrcfury.vrcfury"].Tag, rows["com.vrcfury.vrcfury"].Versions, rows["com.vrcfury.vrcfury"].group), Is.EqualTo(("Install", "1.1436.0", GalleryPackageRow.Needed)));

            var gesture = rows["vrchat.blackstartx.gesture-manager"];
            Assert.That((gesture.Tag, gesture.Versions, gesture.group), Is.EqualTo(("Update", "3.9.3 → 3.9.8", GalleryPackageRow.Tools)));
            Assert.That(gesture.why, Is.EqualTo("3.9.3 works only with VRChat SDK - Avatars >=3.5.2 <3.8.X; 3.9.8 works with VRChat SDK - Avatars 3.10.6."));
            Assert.That(rows["dev.vrlabs.av3manager"].Versions, Is.EqualTo("2.0.34 → 2.1.2"));
            Assert.That(rows["dev.hai-vr.fuck-osc-config"].Versions, Is.EqualTo("1.1.5 → 1.1.6"));
            Assert.That(rows["orbiters.toolkit"].Tag, Is.EqualTo("Installed"));

            Assert.That(model.Eyebrow, Is.EqualTo("VRCHAT SDK UPDATE NEEDED"));
            Assert.That(model.Title("N4N0"), Is.EqualTo("N4N0 needs VRChat SDK 3.10.6 and 4 other package changes"));
            Assert.That(model.Text("N4N0"), Does.StartWith("This project has VRChat SDK 3.7.5. The tools below that only work with that version are changed"));
            Assert.That(model.Text("N4N0"), Does.Contain("N4N0 installs by itself"));
            Assert.That(model.Primary, Is.EqualTo("Update packages and continue"));
            Assert.That(model.Notes().Select(n => n.text), Has.Some.StartWith("Adds vpm.example.org/"), "the repositories it adds to the Creator Companion");
            Assert.That(model.Notes().Any(n => n.warning), Is.False, "nothing is removed");
        }

        [Test] public void ARemovalIsMarkedAndWarnedAbout()
        {
            var model = Model(TestVpmCatalog.SamplePlan(oscUpdate: false));
            var osc = model.rows.Single(r => r.id == "dev.hai-vr.fuck-osc-config");
            Assert.That((osc.Tag, osc.Versions, osc.kind, osc.group), Is.EqualTo(("Remove", "1.1.5", GalleryPackageRow.Remove, GalleryPackageRow.Tools)));
            Assert.That(osc.why, Does.StartWith("1.1.5 works only with VRChat SDK - Avatars >=3.1.0 <3.8.0-a, and none of its versions works with VRChat SDK - Avatars 3.10.6."));
            var warning = model.Notes().Single(n => n.warning).text;
            Assert.That(warning, Does.StartWith("Haï ~ Auto-reset OSC config file is removed from this project"));
        }

        [Test] public void ABlockedPlanSaysWhichPackageBlocksAndWhatToDo()
        {
            // Nothing offers a VRChat SDK the asset can use.
            var catalog = TestVpmCatalog.SampleProject();
            catalog.available.RemoveAll(c => c.Version == "3.10.6");
            var model = Model(VpmDependencyPlan.ResolveForApproval(TestVpmCatalog.N4N0, catalog));
            Assert.That(model.state, Is.EqualTo(GalleryPackagePlan.Blocked));
            var blocker = model.Blocker;
            Assert.That(blocker.name, Does.StartWith("VRChat SDK"));
            Assert.That(model.rows.First(), Is.SameAs(blocker), "what blocks comes first");
            Assert.That(model.Eyebrow, Is.EqualTo("A PACKAGE BLOCKS THIS INSTALLATION"));
            Assert.That(model.Title("N4N0"), Is.EqualTo(blocker.name + " keeps N4N0 from installing"));
            Assert.That(model.Text("N4N0"), Is.EqualTo(blocker.why).And.Not.Empty);
            Assert.That(model.Primary, Is.EqualTo("Check again"));
            Assert.That(model.Notes().Single(n => n.warning).text, Does.Contain("VRChat Creator Companion").And.Contain("Check again"));
        }

        [Test] public void FailedAndBusyStatesSayWhereThePlanIs()
        {
            var model = Model(TestVpmCatalog.SamplePlan());
            model.state = GalleryPackagePlan.Applying;
            Assert.That(model.Primary, Is.Null, "nothing to press while it changes the packages");
            Assert.That(model.Title("N4N0"), Is.EqualTo("Changing 6 packages for N4N0…"));
            model.state = GalleryPackagePlan.Reloading;
            Assert.That(model.Title("N4N0"), Is.EqualTo("Unity is taking in the new packages…"));
            model.state = GalleryPackagePlan.Failed;
            model.error = "VPM could not install VRCFury 1.1436.0.";
            model.changed.Add("VRChat SDK - Base 3.10.6");
            Assert.That(model.Primary, Is.EqualTo("Retry"));
            Assert.That(model.Text("N4N0"), Is.EqualTo(model.error));
            Assert.That(model.Notes().Select(n => n.text), Has.Some.EqualTo("Already changed: VRChat SDK - Base 3.10.6. Retry goes on from there."));
            Assert.That(model.Summary("N4N0"), Does.StartWith("The packages could not be changed: VPM could not install"));
        }

        [Test] public void AnApprovalHoldsOnlyForTheChangesItShowed()
        {
            var plan = TestVpmCatalog.SamplePlan();
            var approved = Model(plan);
            Assert.That(GalleryPackages.Covered(plan, approved), Is.True);
            // Part of it done (a retry): what is left was approved.
            var catalog = TestVpmCatalog.SampleProject();
            catalog.installed["com.vrchat.base"] = "3.10.6";
            var rest = VpmDependencyPlan.ResolveForApproval(TestVpmCatalog.N4N0, catalog);
            Assert.That(rest.Changes.Count(), Is.LessThan(plan.Changes.Count()));
            Assert.That(GalleryPackages.Covered(rest, approved), Is.True);
            // Another version than the one shown is shown again first.
            approved.rows.Single(r => r.id == "com.vrcfury.vrcfury").to = "1.1434.0";
            Assert.That(GalleryPackages.Covered(plan, approved), Is.False);
        }
    }
}
#endif
