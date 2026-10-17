using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Meshes;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor.Painter
{
    /// <summary>
    /// Which material of the avatar each texture set of its Painter project paints (<see cref="MyAvatar.painter"/>): kept by
    /// renderer and slot, so it follows the copies My Avatar makes of a material when textures are applied. New texture sets
    /// are matched by name: a project My Avatar made names them after the materials; another project's names are compared
    /// word by word.
    /// </summary>
    internal static class PainterMapping
    {
        /// <summary>A material slot of the avatar: a renderer showing it (path from the avatar) and the slot.</summary>
        internal sealed class Choice
        {
            public string Renderer;
            public int Slot;
            public Material Material;
            public string Label;
        }

        /// <summary>The avatar's materials, each once, by the first renderer slot showing it (mesh renderers only).</summary>
        internal static List<Choice> Choices(MyAvatar avatar)
        {
            var choices = new List<Choice>();
            var seen = new HashSet<Material>();
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer) || (renderer.hideFlags & HideFlags.DontSave) != 0) continue;
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (!material || !seen.Add(material)) continue;
                    choices.Add(new Choice
                    {
                        Renderer = Path(avatar, renderer), Slot = i, Material = material,
                        Label = material.name + "  (" + renderer.name + ")",
                    });
                }
            }
            return choices;
        }

        // "." for a renderer on the avatar's own object: an empty path means the texture set paints nothing.
        private static string Path(MyAvatar avatar, Renderer renderer)
        {
            string path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform);
            return string.IsNullOrEmpty(path) ? "." : path;
        }

        private static PainterSet Entry(MyAvatar avatar, string textureSet) =>
            avatar.painter.sets.FirstOrDefault(s => string.Equals(s.textureSet, textureSet, StringComparison.OrdinalIgnoreCase));

        /// <summary>The material a texture set paints now, or null.</summary>
        internal static Material Material(MyAvatar avatar, string textureSet)
        {
            var entry = Entry(avatar, textureSet);
            if (entry == null || string.IsNullOrEmpty(entry.renderer)) return null;
            var transform = entry.renderer == "." ? avatar.transform : avatar.transform.Find(entry.renderer);
            var renderer = transform ? transform.GetComponent<Renderer>() : null;
            if (!renderer) return null;
            var materials = renderer.sharedMaterials;
            return entry.slot >= 0 && entry.slot < materials.Length ? materials[entry.slot] : null;
        }

        /// <summary>The mapping's identity for a texture set (renderer and slot), or null when it paints nothing.</summary>
        internal static string Key(MyAvatar avatar, string textureSet)
        {
            var entry = Entry(avatar, textureSet);
            return entry == null || string.IsNullOrEmpty(entry.renderer) ? null : entry.renderer + "#" + entry.slot;
        }

        /// <summary>Sets what a texture set paints (null: nothing), with Undo.</summary>
        internal static void Set(MyAvatar avatar, string textureSet, Choice choice)
        {
            Undo.RecordObject(avatar, "My Avatar: Painter texture set");
            var entry = Entry(avatar, textureSet);
            if (entry == null) avatar.painter.sets.Add(entry = new PainterSet { textureSet = textureSet });
            entry.renderer = choice?.Renderer ?? "";
            entry.slot = choice?.Slot ?? 0;
            Dirty(avatar);
        }

        /// <summary>Matches the texture sets not mapped yet; true when one was.</summary>
        internal static bool AutoMap(MyAvatar avatar, IEnumerable<string> textureSets)
        {
            var choices = Choices(avatar);
            bool changed = false;
            foreach (string name in textureSets)
            {
                if (Entry(avatar, name) != null) continue;
                var choice = Best(name, choices);
                avatar.painter.sets.Add(new PainterSet { textureSet = name, renderer = choice?.Renderer ?? "", slot = choice?.Slot ?? 0 });
                changed = true;
            }
            if (changed) Dirty(avatar);
            return changed;
        }

        // The same name (as OBJ files and Painter write it), else the material sharing most of its words, when only one does.
        private static Choice Best(string textureSet, List<Choice> choices)
        {
            var exact = choices.Where(c => string.Equals(ObjWriter.Clean(c.Material.name), textureSet, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var words = new HashSet<string>(TextureMatching.Tokens(textureSet));
            if (words.Count == 0) return null;
            var scored = choices.Select(c =>
                {
                    var other = TextureMatching.Tokens(c.Material.name);
                    return (choice: c, score: other.Count == 0 ? 0 : other.Count(words.Contains) / (double)Math.Max(words.Count, other.Count));
                })
                .Where(p => p.score >= .5).OrderByDescending(p => p.score).ToList();
            if (scored.Count == 0 || scored.Count > 1 && scored[1].score >= scored[0].score) return null;
            return scored[0].choice;
        }

        private static void Dirty(MyAvatar avatar)
        {
            EditorUtility.SetDirty(avatar);
            PrefabUtility.RecordPrefabInstancePropertyModifications(avatar);
        }
    }
}
