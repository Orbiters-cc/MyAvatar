using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Quick optimization below the drop zone, folded under its title: the offer with its estimate, then the result with
    // Undo and, once optimized, a pointer to d4rkAvatarOptimizer for what texture settings cannot do. It opens by itself
    // when a texture drop brings something to optimize; otherwise it stays folded unless the user opens it.
    internal static class OptimizationCard
    {
        private const string Disclaimer = "My Avatar is not associated with d4rkAvatarOptimizer.";
        // Per avatar: the texture set the user opened or closed the card for.
        private static readonly Dictionary<int, (string batch, bool open)> Folds = new Dictionary<int, (string batch, bool open)>();

        private const string OptimizerTooltip = "d4rkAvatarOptimizer merges meshes and materials and removes what the avatar never uses, when you upload it.\n" +
            "With VRCFury or Modular Avatar, its author recommends this upload-time workflow rather than “Create Optimized Copy”.";

        private const string OptimizerPackage = "d4rkpl4y3r.d4rkavataroptimizer";

        internal static void Populate(VisualElement root, MyAvatar avatar, Action optimize, Action revert, Action changed)
        {
            var record = avatar.optimization;
            bool applied = TextureOptimization.Applied(record);
            // Offered once a drop placed an image that compression applies to, then as long as something is left to optimize.
            bool dropped = avatar.textures.Any(e => e.applied && e.texture && TextureOptimization.Compressible(e.texture, out _));
            var plan = dropped ? TextureOptimization.Build(avatar) : null;
            // Once optimized, the offer waits for a new texture drop or an Undo: the result stays on screen as the answer.
            bool offer = plan != null && plan.changes.Count > 0 && (!applied || record.batch != avatar.batchFolder);
            if (!applied && !offer) return;

            var card = new VisualElement(); card.AddToClassList("optimize-card"); root.Add(card);
            var heading = new VisualElement(); heading.AddToClassList("optimize-card__header"); card.Add(heading);
            var chevron = new VectorIcon(IconGlyph.Chevron); chevron.AddToClassList("optimize-card__chevron"); heading.Add(chevron);
            var body = new VisualElement(); body.AddToClassList("optimize-card__body"); card.Add(body);
            int id = avatar.GetInstanceID();
            bool open = Folds.TryGetValue(id, out var fold) && fold.batch == avatar.batchFolder ? fold.open : offer;
            void Show(bool value)
            {
                open = value;
                card.EnableInClassList("optimize-card--open", open);
                body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            }
            Show(open);
            // Opens on press, like the other controls.
            heading.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                Show(!open);
                Folds[id] = (avatar.batchFolder, open);
            });
            var row = new VisualElement(); row.AddToClassList("optimize-card__row"); body.Add(row);
            var texts = new VisualElement(); texts.AddToClassList("optimize-card__texts"); row.Add(texts);
            int changedCount = TextureOptimization.Changed(record);
            var title = new Label(applied ? $"Optimized · {changedCount} texture{(changedCount == 1 ? "" : "s")}" : "Quick optimization");
            title.AddToClassList("optimize-card__title"); heading.Add(title);
            long before = applied ? record.bytesBefore : plan.before, after = applied ? record.bytesAfter : plan.after;
            if (before > 0 && after < before)
            {
                var chip = new Label("−" + Mathf.RoundToInt(100f * (before - after) / before) + "%"); chip.AddToClassList("optimize-card__chip"); heading.Add(chip);
            }
            string estimate = $"{Megabytes(before)} → {Megabytes(after)} texture memory (estimated VRAM on PC)";
            var text = new Label(applied ? estimate + (offer ? $" · {plan.changes.Count} more from the new textures" : "")
                : $"Cap {plan.changes.Count} texture{(plan.changes.Count == 1 ? "" : "s")} (body {TextureOptimization.BodyMaxSize} px, the rest {TextureOptimization.MaxSize} px) and compress for PC · {estimate}");
            text.AddToClassList("optimize-card__text"); texts.Add(text);
            text.tooltip = "Estimated from each texture's format, size and mipmaps, the way VRChat ranks texture memory. Download size is not estimated.";

            var buttons = new VisualElement(); buttons.AddToClassList("optimize-card__buttons"); row.Add(buttons);
            if (applied)
            {
                var undo = MyAvatarEditor.Button(offer ? "Undo" : "Undo optimization", revert);
                undo.tooltip = "Undo the optimization: restore the import settings and this avatar's materials. Copies made for it stay in the project.";
                undo.AddToClassList("optimize-card__button"); buttons.Add(undo);
            }
            if (offer)
            {
                // The result replaces the offer in the same, still open card.
                var button = MyAvatarEditor.Button("Optimize", () => { Folds[id] = (avatar.batchFolder, true); optimize(); });
                button.tooltip = $"Cap the body's textures at {TextureOptimization.BodyMaxSize} px and every other texture on this avatar at {TextureOptimization.MaxSize} px, and compress them for PC: BC1 for opaque colour, BC7 when alpha is used, " +
                    "BC5 for normal maps, with mipmaps and mipmap streaming on and crunch off. Ramps, lookup tables and HDR images are left alone.\n" +
                    "Textures also used by another avatar in the open scenes, or inside a package, are copied for this avatar first. Undo restores everything.";
                button.AddToClassList("mcb-button--primary"); button.AddToClassList("optimize-card__button"); buttons.Add(button);
            }
            if (applied) Suggestion(body, avatar, changed);
        }

        private static void Suggestion(VisualElement card, MyAvatar avatar, Action changed)
        {
            var type = TextureOptimization.OptimizerType;
            var avatarRoot = TextureOptimization.AvatarRoot(avatar);
            var present = type != null ? avatarRoot.GetComponentsInChildren(type, true).FirstOrDefault() : null;
            if (present is Behaviour behaviour && behaviour.enabled && behaviour.gameObject.activeInHierarchy) return;
            var row = new VisualElement { tooltip = OptimizerTooltip }; row.AddToClassList("optimize-suggestion"); card.Add(row);
            var text = new Label(); text.AddToClassList("optimize-suggestion__text"); row.Add(text);
            if (present)
            {
                text.text = "d4rkAvatarOptimizer is on this avatar but disabled, so it will not optimize it at upload.";
                return;
            }
            var link = MyAvatarEditor.Button("d4rkAvatarOptimizer ↗", () => Application.OpenURL(TextureOptimization.OptimizerUrl));
            link.tooltip = TextureOptimization.OptimizerUrl; link.AddToClassList("avatar-link"); link.AddToClassList("optimize-suggestion__link");
            if (type == null)
            {
#if MYAVATAR_VPM
                // The shared prompt installs it from its VPM repository; the Unity reload afterwards rebuilds this card.
                // Its text takes the full width, then Install and the link share one line.
                text.RemoveFromHierarchy();
                var prompt = new Orbiters.Toolkit.Editor.Vpm.DependencyPrompt(OptimizerPackage, "For advanced avatar optimization, use d4rkAvatarOptimizer.", "orbiters.myavatar");
                prompt.AddToClassList("optimize-suggestion__prompt"); row.Add(prompt);
                prompt.Q(className: "orb-dependency__texts")?.Add(Note());
                var row2 = new VisualElement(); row2.AddToClassList("optimize-suggestion__actions"); prompt.Add(row2);
                var install = prompt.Q(className: "orb-dependency__install");
                if (install != null) row2.Add(install);
                row2.Add(link);
#else
                text.text = "For advanced avatar optimization (meshes, materials, unused bones and blendshapes), use d4rkAvatarOptimizer.";
                row.Add(Note());
                var actions = new VisualElement(); actions.AddToClassList("optimize-suggestion__actions"); row.Add(actions);
                actions.Add(link);
#endif
                return;
            }
            text.text = "d4rkAvatarOptimizer is installed: add it to optimize meshes and materials when you upload.";
            row.Add(Note());
            var buttons = new VisualElement(); buttons.AddToClassList("optimize-suggestion__actions"); row.Add(buttons);
            var add = MyAvatarEditor.Button("Add to avatar", () => {
                row.RemoveFromHierarchy();
                Undo.AddComponent(avatarRoot, type);
                changed();
            });
            add.AddToClassList("optimize-suggestion__button"); buttons.Add(add);
            buttons.Add(link);
        }

        private static Label Note()
        {
            var note = new Label(Disclaimer); note.AddToClassList("optimize-suggestion__disclaimer");
            return note;
        }

        private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 * 1048576L ? "0.0" : "0") + " MB";
    }
}
