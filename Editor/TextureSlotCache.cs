using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// <see cref="TextureMatching.Slots"/> once per avatar state. What a shader's texture properties say about themselves
    /// (cleaned description, role, secondary) is worked out once per shader; an avatar's slots are kept until its renderers,
    /// their names and the objects above them, their meshes, bones or materials, a material's shader, name or textures, or
    /// any project asset change. Checking that is reading those values, with no text work and no asset lookups.
    /// </summary>
    internal static class TextureSlotCache
    {
        /// <summary>A texture property of a shader, as every material using that shader shows it.</summary>
        internal sealed class Template
        {
            public int id;
            public string property, description, role;
            public bool tex2D, hidden, secondary;
        }

        private sealed class Entry
        {
            public MyAvatar avatar;
            public int assets;
            public long[] state;
            // The texture properties each material's slots read, in slot order, to see textures change.
            public Dictionary<Material, int[]> textures;
            public List<TextureSlot> slots;
        }

        private static readonly Dictionary<Shader, Template[]> Templates = new Dictionary<Shader, Template[]>();
        private static readonly Dictionary<(int avatar, bool includeAll), Entry> Avatars = new Dictionary<(int, bool), Entry>();
        // Any import, move or deletion: texture sizes and paths, model paths and what Shader.Find returns may change.
        private static int assets;

        private static readonly List<Renderer> Renderers = new List<Renderer>();
        private static readonly List<Material> Materials = new List<Material>();
        private static readonly HashSet<Material> Seen = new HashSet<Material>();
        private static readonly List<long> State = new List<long>();

        internal static int Builds { get; private set; }

        internal static List<TextureSlot> Slots(MyAvatar avatar, bool includeAll)
        {
            var renderers = AvatarRenderers(avatar);
            var key = (avatar.GetInstanceID(), includeAll);
            if (Avatars.TryGetValue(key, out var entry) && entry.avatar == avatar && entry.assets == assets)
            {
                Fill(State, avatar, renderers, entry.textures);
                if (Same(State, entry.state)) return Copy(entry.slots);
            }
            var textures = new Dictionary<Material, int[]>();
            var slots = TextureMatching.Build(avatar, renderers, includeAll, textures);
            Builds++;
            Fill(State, avatar, renderers, textures);
            if (Avatars.Count > 16) Prune();
            Avatars[key] = new Entry { avatar = avatar, assets = assets, state = State.ToArray(), textures = textures, slots = slots };
            return Copy(slots);
        }

        /// <summary>Forgets every avatar's slots (tests, and edits made without any of the signals above).</summary>
        internal static void Clear() { Avatars.Clear(); Templates.Clear(); }

        internal static Template[] Of(Shader shader)
        {
            if (Templates.TryGetValue(shader, out var known)) return known;
            var list = new List<Template>();
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                string property = shader.GetPropertyName(i), description = TextureMatching.CleanDescription(shader.GetPropertyDescription(i));
                list.Add(new Template { id = shader.GetPropertyNameId(i), property = property, description = description,
                    role = TextureMatching.SlotRole(property, description), secondary = TextureMatching.Secondary(property, description),
                    tex2D = shader.GetPropertyTextureDimension(i) == TextureDimension.Tex2D,
                    hidden = (shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0 });
            }
            return Templates[shader] = list.ToArray();
        }

        private static List<Renderer> AvatarRenderers(MyAvatar avatar)
        {
            Renderers.Clear();
            avatar.GetComponentsInChildren(true, Renderers);
            Renderers.RemoveAll(r => (r.hideFlags & HideFlags.DontSave) != 0 || (r.gameObject.hideFlags & HideFlags.DontSave) != 0);
            return Renderers;
        }

        private static List<TextureSlot> Copy(List<TextureSlot> slots)
        {
            var copy = new List<TextureSlot>(slots.Count);
            foreach (var slot in slots) copy.Add(slot.Copy());
            return copy;
        }

        private static bool Same(List<long> state, long[] known)
        {
            if (state.Count != known.Length) return false;
            for (int i = 0; i < known.Length; i++) if (state[i] != known[i]) return false;
            return true;
        }

        // Everything Build reads from the scene, as numbers. Names count by their hash code.
        private static void Fill(List<long> state, MyAvatar avatar, List<Renderer> renderers, Dictionary<Material, int[]> textures)
        {
            state.Clear(); Seen.Clear();
            var root = avatar.transform;
            state.Add(root.name.GetHashCode());
            var animator = root.GetComponent<Animator>();
            var hips = animator && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            state.Add(hips ? hips.GetInstanceID() : 0);
            state.Add(renderers.Count);
            foreach (var renderer in renderers)
            {
                state.Add(renderer.GetInstanceID());
                state.Add((renderer.enabled ? 1 : 0) | (renderer.gameObject.activeInHierarchy ? 2 : 0));
                for (var t = renderer.transform; t && t != root; t = t.parent) { state.Add(t.GetInstanceID()); state.Add(t.name.GetHashCode()); }
                state.Add(-1);
                Mesh mesh;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    mesh = skinned.sharedMesh;
                    var bones = skinned.bones;
                    state.Add(bones.Length);
                    foreach (var bone in bones) state.Add(bone ? bone.GetInstanceID() : 0);
                }
                else mesh = renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                state.Add(mesh ? mesh.GetInstanceID() : 0);
                Materials.Clear();
                renderer.GetSharedMaterials(Materials);
                state.Add(Materials.Count);
                foreach (var material in Materials)
                {
                    state.Add(material ? material.GetInstanceID() : 0);
                    if (!material || !Seen.Add(material)) continue;
                    var shader = material.shader;
                    state.Add(shader ? shader.GetInstanceID() : 0);
                    state.Add(material.name.GetHashCode());
                    state.Add(material.GetTag("OriginalShader", false).GetHashCode());
                    if (!textures.TryGetValue(material, out var ids)) { state.Add(long.MinValue); continue; }
                    foreach (int id in ids)
                    {
                        var texture = material.HasProperty(id) ? material.GetTexture(id) : null;
                        state.Add(texture ? texture.GetInstanceID() : 0);
                    }
                }
            }
        }

        private static void Prune()
        {
            var gone = new List<(int, bool)>();
            foreach (var pair in Avatars) if (!pair.Value.avatar) gone.Add(pair.Key);
            foreach (var key in gone) Avatars.Remove(key);
            if (Avatars.Count > 16) Avatars.Clear();
        }

        private static readonly HashSet<string> ShaderFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".shader", ".cginc", ".hlsl", ".hlslinc", ".glslinc", ".compute", ".shadergraph", ".shadersubgraph", ".orlshader", ".orlsource",
        };

        private static bool ShaderFile(string path) => ShaderFiles.Contains(System.IO.Path.GetExtension(path));

        internal sealed class AssetWatcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (imported.Length == 0 && deleted.Length == 0 && moved.Length == 0) return;
                assets++;
                // Shaders made by a scripted importer (lilToon's containers) are found by the type they import as.
                bool shaders = Array.Exists(deleted, ShaderFile) || Array.Exists(moved, ShaderFile) ||
                    Array.Exists(imported, p => ShaderFile(p) || AssetDatabase.GetMainAssetTypeAtPath(p) == typeof(Shader));
                if (shaders) Templates.Clear();
            }
        }
    }
}
