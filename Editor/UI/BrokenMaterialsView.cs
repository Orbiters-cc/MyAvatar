using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // The materials card's first block when some of the avatar's materials don't render (BrokenMaterials): one row per
    // material with the shader it moves to and what it can't keep, one button for all of them, and the shader's own package
    // when installing it would make them render exactly as made.
    internal sealed class BrokenMaterialsView : VisualElement
    {
        internal BrokenMaterialsView(List<BrokenMaterial> broken, Action<Button, List<BrokenMaterial>> fix)
        {
            AddToClassList("materials__broken");
            var fixable = broken.Where(b => b.Target).ToList();
            var title = new Label(broken.Count == 1 ? "1 material doesn't render" : broken.Count + " materials don't render");
            title.AddToClassList("materials__broken-title"); Add(title);
            var caption = new Label(Caption(fixable)); caption.AddToClassList("optimize-card__text"); Add(caption);
            foreach (var entry in broken) Add(Row(entry));
            if (fixable.Count > 0)
            {
                var actions = new VisualElement(); actions.AddToClassList("materials__actions"); Add(actions);
                var button = MyAvatarEditor.Button(fixable.Count == 1 ? "Fix material" : "Fix " + fixable.Count + " materials", null);
                button.AddToClassList("mcb-button--primary");
                button.tooltip = "Move them to the shaders listed above in one step. Materials in this project are fixed where they are, so every avatar " +
                                 "using them renders again, and each original stays beside it as “name (old)”; materials inside a package or a model are copied " +
                                 "for this avatar. Undo restores everything.";
                button.clicked += () => fix(button, fixable);
                actions.Add(button);
            }
#if MYAVATAR_VPM
            foreach (string package in broken.Select(b => b.Installable).Where(p => p != null).Distinct())
                Add(Suggestion(package, broken.Count(b => b.Installable == package)));
#endif
        }

        private static string Caption(List<BrokenMaterial> fixable)
        {
            if (fixable.Count == 0) return "Unity draws them pink, and My Avatar can't fix them by itself.";
            var targets = fixable.Select(b => b.TargetName).Distinct().ToList();
            string names = targets.Count == 1 ? targets[0] : string.Join(", ", targets.Take(targets.Count - 1)) + " and " + targets[targets.Count - 1];
            if (fixable.All(b => b.Rebind))
                return "Unity draws them pink. " + names + (targets.Count == 1 ? " is" : " are") + " in this project: fixing puts them back on it with every setting; the originals stay beside them as “(old)” copies. Undo restores them.";
            return "Unity draws them pink: the shader they were made for isn't in this project. Fixing moves them to " + names +
                   ", keeping what it can; the originals stay beside them as “(old)” copies. Undo restores them.";
        }

        private static VisualElement Row(BrokenMaterial entry)
        {
            var row = new VisualElement(); row.AddToClassList("materials__row"); row.AddToClassList("materials__row--broken");
            var texts = new VisualElement(); texts.AddToClassList("materials__texts"); row.Add(texts);
            var name = new Label(entry.Material.name) { tooltip = entry.AssetPath }; name.AddToClassList("materials__name"); texts.Add(name);
            var change = new Label(entry.Change); change.AddToClassList("materials__detail"); texts.Add(change);
            string details = entry.Details;
            if (!string.IsNullOrEmpty(details))
            {
                var more = new Label(details); more.AddToClassList("materials__detail"); more.AddToClassList("materials__detail--more");
                more.EnableInClassList("warning", !entry.Target);
                texts.Add(more);
            }
            var show = MyAvatarEditor.Button("Show", () => EditorGUIUtility.PingObject(entry.Material));
            show.tooltip = "Show it in the Project window.";
            show.AddToClassList("avatar-link");
            row.Add(show);
            return row;
        }

#if MYAVATAR_VPM
        // Installing the shader the materials were made for keeps every setting; the shared prompt shows its plan first.
        private static VisualElement Suggestion(string package, int count)
        {
            string shader = package == MaterialShaders.LilToonPackage ? "lilToon" : "Poiyomi Toon";
            var row = new VisualElement(); row.AddToClassList("optimize-suggestion");
            var prompt = new Orbiters.Toolkit.Editor.Vpm.DependencyPrompt(package,
                "Or install " + shader + ": " + (count == 1 ? "its material renders" : "its " + count + " materials render") + " exactly as made.", "orbiters.myavatar");
            prompt.AddToClassList("optimize-suggestion__prompt"); row.Add(prompt);
            // The prompt's error line takes its height even empty; it shows again when an install fails.
            var error = prompt.Q<Label>(className: "orb-dependency__error");
            if (error != null && string.IsNullOrEmpty(error.text)) error.style.display = DisplayStyle.None;
            var actions = new VisualElement(); actions.AddToClassList("optimize-suggestion__actions"); prompt.Add(actions);
            var install = prompt.Q(className: "orb-dependency__install");
            if (install != null) actions.Add(install);
            return row;
        }
#endif
    }
}
