using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.MyAvatar.Editor
{
    // Quick optimization below the drop zone: the offer with its estimate, then the result with Undo and, once optimized,
    // a pointer to d4rkAvatarOptimizer for what texture settings cannot do.
    internal static class OptimizationCard
    {
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
            var row = new VisualElement(); row.AddToClassList("optimize-card__row"); card.Add(row);
            var texts = new VisualElement(); texts.AddToClassList("optimize-card__texts"); row.Add(texts);
            var heading = new VisualElement(); heading.AddToClassList("optimize-card__heading"); texts.Add(heading);
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
                var button = MyAvatarEditor.Button("Optimize", optimize);
                button.tooltip = $"Cap the body's textures at {TextureOptimization.BodyMaxSize} px and every other texture on this avatar at {TextureOptimization.MaxSize} px, and compress them for PC: BC1 for opaque colour, BC7 when alpha is used, " +
                    "BC5 for normal maps, with mipmaps and mipmap streaming on and crunch off. Ramps, lookup tables and HDR images are left alone.\n" +
                    "Textures also used by another avatar in the open scenes, or inside a package, are copied for this avatar first. Undo restores everything.";
                button.AddToClassList("mcb-button--primary"); button.AddToClassList("optimize-card__button"); buttons.Add(button);
            }
            if (applied) Suggestion(card, avatar, changed);
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
            if (type == null)
            {
#if MYAVATAR_VPM
                // The shared prompt installs it from its VPM repository; the Unity reload afterwards rebuilds this card.
                text.RemoveFromHierarchy();
                var prompt = new Orbiters.Toolkit.Editor.Vpm.DependencyPrompt(OptimizerPackage, "For advanced avatar optimization, use d4rkAvatarOptimizer.", "orbiters.myavatar");
                prompt.AddToClassList("optimize-suggestion__prompt"); row.Add(prompt);
#else
                text.text = "For advanced avatar optimization (meshes, materials, unused bones and blendshapes), use d4rkAvatarOptimizer.";
#endif
                var link = MyAvatarEditor.Button("d4rkAvatarOptimizer ↗", () => Application.OpenURL(TextureOptimization.OptimizerUrl));
                link.tooltip = TextureOptimization.OptimizerUrl; link.AddToClassList("avatar-link"); row.Add(link);
                return;
            }
            text.text = "d4rkAvatarOptimizer is installed: add it to optimize meshes and materials when you upload.";
            var add = MyAvatarEditor.Button("Add to avatar", () => {
                row.RemoveFromHierarchy();
                Undo.AddComponent(avatarRoot, type);
                changed();
            });
            add.AddToClassList("optimize-suggestion__button"); row.Add(add);
        }

        private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 * 1048576L ? "0.0" : "0") + " MB";
    }
}
