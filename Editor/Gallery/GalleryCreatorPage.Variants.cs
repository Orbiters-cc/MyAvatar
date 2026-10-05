using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor.Gallery
{
    internal sealed partial class GalleryCreatorPage
    {
        private static readonly string[] AttachModes = { "auto", "configured", "merge", "parent" };
        private static readonly string[] AttachLabels = { "Automatic", "Its own setup", "Merge armature", "Follow a bone" };
        private static readonly string[] Platforms = { AvatarPlatform.Pc, AvatarPlatform.Android, AvatarPlatform.Ios };

        // ---- 3. Packages ----

        private void VariantsStep(VisualElement step)
        {
            Hint("Add one package per platform or avatar base that needs its own setup. A PC and a Quest package can share a version number.", step);
            foreach (var variant in draft.variants.ToList()) Variant(step, variant);
            var actions = GalleryUI.Box("creator-actions", step);
            actions.Add(GalleryUI.Button("Add another package", () =>
            {
                var used = new HashSet<string>(draft.variants.SelectMany(v => v.platforms));
                draft.variants.Add(new GalleryVariantDraft { label = "Package " + (draft.variants.Count + 1), platforms = Platforms.Where(p => !used.Contains(p)).Take(1).DefaultIfEmpty(AvatarPlatform.Current).ToList() });
                Build();
            }));
        }

        // The prefab asset a chosen object stands for, or why there is none.
        private static string PrefabPath(GameObject chosen, out string problem)
        {
            problem = null;
            var source = chosen.scene.IsValid() ? PrefabUtility.GetCorrespondingObjectFromOriginalSource(chosen) : chosen;
            string path = source ? AssetDatabase.GetAssetPath(source) : null;
            if (string.IsNullOrEmpty(path))
            {
                problem = chosen.name + " is not saved as a prefab: drag it from the Hierarchy into the Project window, then choose that prefab.";
                return null;
            }
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                problem = chosen.name + " is a model, not a prefab: drag it into the scene, set it up, then drag it back into the Project window to make a prefab.";
                return null;
            }
            return path;
        }

        private void Variant(VisualElement parent, GalleryVariantDraft variant)
        {
            var card = GalleryUI.Box("creator-variant", parent);
            var header = GalleryUI.Box("creator-variant__header", card);
            header.Add(Field(variant.label, value => variant.label = value, placeholder: "Setup name (VRCFury, Modular Avatar…)"));
            if (draft.variants.Count > 1) header.Add(GalleryUI.Button("Remove", () => { draft.variants.Remove(variant); Build(); }, "gallery-link"));

            Label("From", card);
            var source = new SegmentedControl(new[] { "Prefabs in this project", ".unitypackage" }, index => { variant.source = index == 1 ? "package" : "prefab"; variant.report = null; Build(); });
            source.SetIndex(variant.source == "package" ? 1 : 0);
            card.Add(source);
            if (variant.source == "package")
            {
                var row = GalleryUI.Box("creator-row", card); row.style.marginTop = 6;
                GalleryUI.Text(string.IsNullOrEmpty(variant.packagePath) ? "No package chosen" : Path.GetFileName(variant.packagePath), "creator-hint", row).style.flexGrow = 1;
                row.Add(GalleryUI.Button("Choose…", () =>
                {
                    string path = EditorUtility.OpenFilePanel("Package to publish", "", "unitypackage");
                    if (string.IsNullOrEmpty(path)) return;
                    variant.packagePath = path; variant.report = null; Build();
                }));
            }
            else
            {
                foreach (string guid in variant.prefabGuids.ToList())
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    var row = GalleryUI.Box("creator-prefab", card);
                    GalleryUI.Text(prefab ? prefab.name : "Missing prefab", "creator-prefab__name", row);
                    var id = guid;
                    row.Add(GalleryUI.Button("Remove", () => { variant.prefabGuids.Remove(id); variant.report = null; Build(); }, "gallery-link"));
                }
                Label(variant.prefabGuids.Count == 0 ? "Prefab" : "Add prefab", card);
                // Accepts a prefab from the Project, or an object from the Hierarchy (its prefab); anything else says why.
                var picker = new ObjectField { objectType = typeof(GameObject), allowSceneObjects = true };
                picker.AddToClassList("creator-field");
                var note = GalleryUI.Text("", "creator-report__line creator-picker-note");
                note.style.display = DisplayStyle.None;
                void Note(string text, bool error)
                {
                    note.text = text; note.EnableInClassList("creator-report__line--error", error);
                    note.style.display = DisplayStyle.Flex;
                }
                picker.RegisterValueChangedCallback(evt =>
                {
                    var chosen = evt.newValue as GameObject;
                    if (chosen == null) return;
                    string path = PrefabPath(chosen, out string problem);
                    picker.SetValueWithoutNotify(null);
                    if (path == null) { Note(problem, true); return; }
                    string guid = AssetDatabase.AssetPathToGUID(path);
                    if (variant.prefabGuids.Contains(guid)) { Note(Path.GetFileNameWithoutExtension(path) + " is already in this package.", false); return; }
                    variant.prefabGuids.Add(guid);
                    variant.report = null;
                    Build();
                });
                card.Add(picker);
                card.Add(note);
            }

            Label("Platforms", card);
            var platforms = GalleryUI.Box("creator-row", card);
            foreach (string platform in Platforms)
            {
                string id = platform;
                var chip = GalleryUI.Button(AvatarPlatform.Label(platform), () =>
                {
                    if (variant.platforms.Contains(id)) { if (variant.platforms.Count > 1) variant.platforms.Remove(id); }
                    else variant.platforms.Add(id);
                    Build();
                }, "creator-chip");
                chip.EnableInClassList("creator-chip--on", variant.platforms.Contains(platform));
                platforms.Add(chip);
            }

            Label("Avatar bases", card);
            var scope = new SegmentedControl(new[] { "Any avatar", "Specific bases" }, index => { variant.baseScope = index == 1 ? "bases" : "any"; Build(); });
            scope.SetIndex(variant.baseScope == "bases" ? 1 : 0);
            card.Add(scope);
            if (variant.baseScope == "bases")
            {
                var chosen = GalleryUI.Box("creator-row", card); chosen.style.marginTop = 6;
                foreach (int baseId in variant.baseIds.ToList())
                {
                    int id = baseId;
                    var chip = GalleryUI.Button((bases.FirstOrDefault(b => b.id == baseId)?.name ?? "#" + baseId) + "  ×", () => { variant.baseIds.Remove(id); Build(); }, "creator-chip creator-chip--on");
                    chosen.Add(chip);
                }
                var add = new SearchableDropdownField(null, "Avatar bases", bases.Where(b => !variant.baseIds.Contains(b.id)).OrderBy(b => b.name).Select(b => new KeyValuePair<string, string>(b.id.ToString(), b.name)),
                    null, value => { if (int.TryParse(value, out int id)) { variant.baseIds.Add(id); Build(); } }, "Add a base…");
                card.Add(add);
                Hint("Only bases registered on Orbiters are listed.", card);
            }

            var actions = GalleryUI.Box("creator-actions", card);
            actions.Add(GalleryUI.Button(variant.report == null ? "Build package" : "Build again", () => _ = BuildVariant(variant), variant.report == null ? "mcb-button--primary" : null));
            if (variant.report != null && variant.report.Publishable && avatar)
                actions.Add(GalleryUI.Button("Test on " + avatar.name, () => TestVariant(variant)));
            if (variant.report != null) Report(card, variant);
            if (!string.IsNullOrEmpty(variant.testJob))
            {
                var job = GalleryJournal.All.FirstOrDefault(j => j.id == variant.testJob);
                if (job != null && job.Waiting)
                {
                    var question = GalleryUI.Box("creator-report", card);
                    GallerySheets.Question(question, job, accept => { GalleryInstaller.Answer(job, accept); Build(); });
                }
                else if (job != null) GalleryUI.Text("Test: " + job.status, "creator-report__line" + (job.stage == GalleryJob.Failed ? " creator-report__line--error" : ""), card);
            }
        }

        private async Task BuildVariant(GalleryVariantDraft variant)
        {
            Message("Building " + variant.label + "…", false);
            await Task.Yield();
            try { await GalleryPublisher.BuildAsync(draft, variant, avatar); }
            catch (Exception ex) { Message("The package could not be built: " + ex.Message, true); return; }
            Message(variant.report.Publishable ? variant.label + " is ready." : variant.label + " needs changes before it can be published.", !variant.report.Publishable);
            Build();
        }

        private void Report(VisualElement card, GalleryVariantDraft variant)
        {
            var report = variant.report;
            var box = GalleryUI.Box("creator-report", card);
            void Line(string text, string style = null) => GalleryUI.Text(text, "creator-report__line" + (style != null ? " creator-report__line--" + style : ""), box);
            Line($"{report.files} files · {GalleryUI.Size(report.bytes)} · {(report.parameterBits > 0 ? report.parameterBits + " bits of parameters" : "no synced parameters")}" +
                 (report.containsCode ? $" · {report.codeFiles.Count} code file{(report.codeFiles.Count == 1 ? "" : "s")}" : ""));
            if (report.containsCode) Line("Contains code: buyers are asked before it is imported, unless Orbiters marked you a trusted creator.", "muted");
            if (report.dependencies.Count > 0) Line("Needs " + string.Join(", ", report.dependencies.Select(d => d.displayName + " " + d.range)) + ": My Avatar installs them for buyers.", "muted");
            foreach (string error in report.errors) Line(error, "error");
            foreach (string warning in report.warnings) Line(warning, "warning");
            if (report.excluded.Count > 0)
            {
                Line($"Left out ({report.excluded.Count}):", "muted");
                foreach (string excluded in report.excluded.Take(8)) Line("   " + excluded, "muted");
                if (report.excluded.Count > 8) Line($"   …and {report.excluded.Count - 8} more", "muted");
            }
            if (report.prefabs.Count == 0) return;
            foreach (var setup in variant.manifest.setups.ToList()) Setup(card, variant, setup);
            if (report.prefabs.Count > 1)
            {
                var add = GalleryUI.Button("Add a setup choice", () =>
                {
                    variant.manifest.setups.Add(new GallerySetup { key = "setup-" + (variant.manifest.setups.Count + 1), label = "Setup " + (variant.manifest.setups.Count + 1), prefabs = new List<GallerySetupPrefab> { GalleryPublisher.Copy(report.prefabs[0]) } });
                    Build();
                }, "gallery-link");
                add.style.alignSelf = Align.FlexStart; add.style.marginTop = 4;
                card.Add(add);
            }
        }

        // One setup buyers can choose (left hand, right hand…): which prefabs it places and how each attaches.
        private void Setup(VisualElement card, GalleryVariantDraft variant, GallerySetup setup)
        {
            var box = GalleryUI.Box("creator-setup", card);
            if (variant.manifest.setups.Count > 1)
            {
                var head = GalleryUI.Box("creator-variant__header", box);
                head.Add(Field(setup.label, value => setup.label = value));
                head.Add(GalleryUI.Button("Remove", () => { variant.manifest.setups.Remove(setup); Build(); }, "gallery-link"));
            }
            foreach (var prefab in variant.report.prefabs)
            {
                var row = GalleryUI.Box("creator-prefab", box);
                var placed = setup.prefabs.FirstOrDefault(p => p.guid == prefab.guid);
                var toggle = new Toggle { value = placed != null }; toggle.AddToClassList("creator-prefab__toggle");
                var item = prefab;
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue && setup.prefabs.All(p => p.guid != item.guid)) setup.prefabs.Add(GalleryPublisher.Copy(item));
                    if (!evt.newValue && setup.prefabs.Count > 1) setup.prefabs.RemoveAll(p => p.guid == item.guid);
                    Build();
                });
                row.Add(toggle);
                GalleryUI.Text(prefab.name, "creator-prefab__name", row).tooltip = prefab.path;
                if (placed == null) continue;
                var mode = new PopupField<string>(AttachLabels.ToList(), Math.Max(0, Array.IndexOf(AttachModes, placed.attach?.mode ?? "auto")));
                mode.AddToClassList("creator-prefab__mode");
                mode.RegisterValueChangedCallback(evt => { placed.attach = new GalleryAttach { mode = AttachModes[AttachLabels.ToList().IndexOf(evt.newValue)], bone = placed.attach?.bone ?? "Head" }; Build(); });
                row.Add(mode);
                if (placed.attach?.mode == "parent")
                {
                    var bones = Enum.GetNames(typeof(HumanBodyBones)).Where(n => n != "LastBone").ToList();
                    var bone = new PopupField<string>(bones, Math.Max(0, bones.IndexOf(placed.attach.bone ?? "Head")));
                    bone.AddToClassList("creator-prefab__mode");
                    bone.RegisterValueChangedCallback(evt => { placed.attach.bone = evt.newValue; draft.Save(); });
                    row.Add(bone);
                }
            }
        }

        private void TestVariant(GalleryVariantDraft variant)
        {
            GalleryJob job;
            try { job = GalleryPublisher.Test(draft, variant, avatar); }
            catch (Exception ex) { Message(ex.Message, true); return; }
            GalleryJournal.Changed += Watch;
            void Watch(GalleryJob changed)
            {
                if (changed.id != job.id) return;
                if (changed.Terminal || changed.Waiting) { GalleryJournal.Changed -= Watch; schedule.Execute(Build); }
            }
            Message("Testing " + variant.label + " on " + avatar.name + "…", false);
        }
    }
}
