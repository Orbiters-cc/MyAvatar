using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace Orbiters.MyAvatar.Editor
{
    /// <summary>
    /// Texture sizes from what players see: how many texels a texture spreads over a meter of the avatar's surface (its
    /// UVs against the mesh as it stands), against how many screen pixels a meter of surface covers from a given distance.
    /// A texture is sharp from that distance when it has a texel for every pixel there; closer, it softens.
    /// </summary>
    internal static class TextureDensity
    {
        /// <summary>
        /// Screen pixels across a meter of surface seen from one meter away, about the same in today's headsets (Quest 3:
        /// 2,064 px over 96° per eye) and on a 1440p monitor: twice as many from half the distance.
        /// </summary>
        internal const float PixelsPerMeter = 1000f;
        internal const float DefaultDistance = 1f, Nearest = .25f, Farthest = 4f;
        internal const int MinSize = 128;

        /// <summary>The slider's stops, in meters.</summary>
        internal static readonly float[] Distances = { .25f, .35f, .5f, .7f, 1f, 1.4f, 2f, 3f, 4f };

        // Textures read by direction, not position: matcaps, reflections, rim and sphere maps. Whole words of the slot's
        // name ("_MatCapTex", "_RimTex", "_ReflCube"), so "Primary", "Trim" or "Atmosphere" don't count.
        private static readonly HashSet<string> ViewWords = new HashSet<string> { "matcap", "cube", "cubemap", "reflection", "refl", "sphere", "rim", "envmap", "environment" };

        internal static float Clamp(float distance) => distance <= 0f ? DefaultDistance : Mathf.Clamp(distance, Nearest, Farthest);

        // Metallic, roughness and smoothness: shading detail that reads the same at half the size of the colour on top.
        private static readonly string[] SurfaceWords = { "metal", "rough", "smooth", "gloss" };

        internal static bool SurfaceMap(string slot) => Words(slot).Any(w => SurfaceWords.Any(s => w.StartsWith(s, StringComparison.Ordinal)));

        internal static bool ViewMapped(string slot) => Words(slot).Any(ViewWords.Contains);

        // The name's words, and each with the next one ("Mat Cap" is "matcap", "Env Map" "envmap").
        private static IEnumerable<string> Words(string name)
        {
            var tokens = TextureMatching.Tokens(name ?? "", stem: false);
            for (int i = 0; i < tokens.Count; i++)
            {
                yield return tokens[i];
                if (i + 1 < tokens.Count) yield return tokens[i] + tokens[i + 1];
            }
        }

        /// <summary>
        /// The size a texture needs to stay sharp from <paramref name="distance"/>: from its measured meters of surface per
        /// texture width when it has one, else the sizes My Avatar always used at one meter (the body and face 2048 px, the
        /// rest 512 px) scaled with the distance. A power of two; a texture is never made larger than its image.
        /// </summary>
        internal static int Cap(float? metersPerTexture, bool body, bool view, float distance)
        {
            distance = Clamp(distance);
            if (view) return Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.RoundToInt(512f / distance)), 128, 1024);
            if (metersPerTexture == null) return Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.RoundToInt((body ? TextureOptimization.BodyMaxSize : TextureOptimization.MaxSize) / distance)), 64, 4096);
            // A tenth of slack before the next power of two: 1,100 needed texels fit in 1,024. Below 128 px a texture saves
            // next to nothing and blurs whatever detail a small part has up close.
            float needed = PixelsPerMeter / distance * metersPerTexture.Value / 1.1f;
            return Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(1, Mathf.CeilToInt(needed))), MinSize, 8192);
        }

        /// <summary>From how close a texture of <paramref name="width"/> texels stays sharp, in meters.</summary>
        internal static float SharpFrom(int width, float metersPerTexture) =>
            width <= 0 || metersPerTexture <= 0f ? 0f : PixelsPerMeter * metersPerTexture / width;

        // ---- Measuring ----

        private static readonly Dictionary<int, (string Key, Dictionary<Texture, float> Meters)> Cache = new Dictionary<int, (string, Dictionary<Texture, float>)>();

        /// <summary>
        /// For each texture the avatar's materials show through their main UVs, the meters of surface its full width covers
        /// on its most demanding use (where it is stretched the least). Kept until the avatar's renderers, meshes, scale,
        /// blendshape weights, materials or imported assets change.
        /// </summary>
        internal static Dictionary<Texture, float> MetersPerTexture(MyAvatar avatar, List<TextureSlot> slots)
        {
            string key = TextureOptimization.ChangeKey(avatar);
            int id = avatar.GetInstanceID();
            if (Cache.TryGetValue(id, out var cached) && cached.Key == key) return cached.Meters;
            var result = new Dictionary<Texture, float>();
            var geometry = new Dictionary<Renderer, (List<Vector3> World, List<Vector2> Uv, Mesh Mesh)>();
            try
            {
                foreach (var slot in slots.Where(s => s.existing && !ViewMapped(s.property + " " + s.description)))
                {
                    var tiling = slot.material && slot.material.HasProperty(slot.property) ? slot.material.GetTextureScale(slot.property) : Vector2.one;
                    float repeat = Mathf.Abs(tiling.x * tiling.y);
                    if (repeat < 1e-6f) continue;
                    foreach (var (renderer, index) in slot.parts)
                    {
                        if (!renderer) continue;
                        if (!geometry.TryGetValue(renderer, out var shape)) geometry[renderer] = shape = Geometry(renderer);
                        if (shape.Mesh == null || index >= shape.Mesh.subMeshCount || shape.Uv.Count != shape.World.Count) continue;
                        var (world, uv) = Areas(shape.Mesh, index, shape.World, shape.Uv);
                        if (world <= 1e-8f || uv <= 1e-8f) continue;
                        float meters = Mathf.Sqrt(world / (uv * repeat));
                        if (!result.TryGetValue(slot.existing, out float most) || meters > most) result[slot.existing] = meters;
                    }
                }
            }
            finally
            {
                foreach (var shape in geometry.Values) if (shape.Mesh != null && shape.World != null && shape.Mesh.name == Baked) UnityEngine.Object.DestroyImmediate(shape.Mesh);
            }
            // Avatars gone since (an upload's build copy) leave the cache.
            foreach (int gone in Cache.Keys.Where(k => k != id && EditorUtility.InstanceIDToObject(k) == null).ToList()) Cache.Remove(gone);
            Cache[id] = (key, result);
            return result;
        }

        private const string Baked = "My Avatar density bake";

        // The mesh as it stands (a skinned mesh posed and shaped as now), in world units, with its main UVs.
        private static (List<Vector3> World, List<Vector2> Uv, Mesh Mesh) Geometry(Renderer renderer)
        {
            Mesh mesh;
            var world = new List<Vector3>();
            if (renderer is SkinnedMeshRenderer skinned)
            {
                if (skinned.sharedMesh == null) return (null, null, null);
                mesh = new Mesh { name = Baked };
                skinned.BakeMesh(mesh);
                mesh.GetVertices(world);
                var uv = new List<Vector2>(); skinned.sharedMesh.GetUVs(0, uv);
                // The bake leaves the renderer's own scale out: only its position and rotation apply (a mesh imported at
                // scale 100 would measure ten thousand times its surface with the full matrix).
                var toWorld = SkinnedMeshBounds.BakedToWorld(skinned);
                for (int i = 0; i < world.Count; i++) world[i] = toWorld.MultiplyPoint3x4(world[i]);
                // The baked copy has the source's submeshes.
                return (world, uv, mesh);
            }
            if (!renderer.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) return (null, null, null);
            mesh = filter.sharedMesh;
            mesh.GetVertices(world);
            for (int i = 0; i < world.Count; i++) world[i] = renderer.transform.TransformPoint(world[i]);
            var uvs = new List<Vector2>(); mesh.GetUVs(0, uvs);
            return (world, uvs, mesh);
        }

        // Surface and UV areas of one submesh's triangles.
        private static (float World, float Uv) Areas(Mesh mesh, int submesh, List<Vector3> world, List<Vector2> uv)
        {
            if (mesh.GetTopology(submesh) != MeshTopology.Triangles) return (0f, 0f);
            var indices = mesh.GetIndices(submesh);
            double worldArea = 0, uvArea = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                worldArea += Vector3.Cross(world[b] - world[a], world[c] - world[a]).magnitude * .5;
                var u = uv[b] - uv[a]; var v = uv[c] - uv[a];
                uvArea += Math.Abs(u.x * v.y - u.y * v.x) * .5;
            }
            return ((float)worldArea, (float)uvArea);
        }
    }
}
