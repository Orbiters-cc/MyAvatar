using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEngine;
using Orbiters.AlterMesh;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// Fewer polygons for the uploaded copy through AlterMesh (Orbiters' simplifier, derived from Meshia). Each mesh is
    /// simplified as far as its surface stays within the quality's budget, measured: at quality 1 no point moves more than
    /// half a millimetre (half a pixel seen from a meter away), each step lower doubles it. The triangles an expression, a
    /// viseme or a small toggle's blendshape moves stay exactly as they are (the simplifier doesn't weigh blendshapes);
    /// broad shapes (a body type, a muscle flex, a pose corrective) vary smoothly and are carried through, and the surface
    /// is checked with each of them at full weight too. Borders and seams are kept (no holes, no torn UVs), so are weights
    /// and material slots.
    /// </summary>
    internal static class PolygonReduction
    {
        // Smaller meshes have too little to gain, and each polygon of them shows.
        private const int MinTriangles = 2000;
        // A blendshape moving at least this share of a mesh reshapes a broad area smoothly (a body type, a muscle flex, a pose
        // corrective); one moving less is an expression, a viseme or a small toggle (a face's are under a fifth of a body).
        private const float Broad = .25f;
        // Meshes simplified, by their source's content and budget: the estimate measures again at every change.
        private static readonly Dictionary<string, Mesh> Done = new Dictionary<string, Mesh>();
        private static readonly List<string> Order = new List<string>();
        private static readonly HashSet<string> Running = new HashSet<string>();

        /// <summary>Whether meshes are being simplified in the background for an estimate.</summary>
        internal static bool Measuring => Running.Count > 0;

        /// <summary>Runs on the main thread when meshes simplified in the background are ready.</summary>
        internal static event Action Ready;

        /// <summary>The largest change allowed, in meters on the avatar.</summary>
        internal static float Budget(float quality) => .0005f * Mathf.Pow(2f, 4f * (1f - Mathf.Clamp01(quality)));

        /// <summary>
        /// Simplifies the copy's meshes; returns how many triangles went. The meshes made go in <paramref name="made"/>.
        /// <paramref name="shapes"/> names the blendshapes each skinned mesh uploads with (null: all it has): the others are
        /// left out of the simplified mesh. In the <paramref name="background"/> (an estimate), a mesh not simplified yet
        /// stays as it is while it is, then <see cref="Ready"/> runs.
        /// </summary>
        internal static int Reduce(AvatarUsage usage, float quality, ICollection<Mesh> made, Func<SkinnedMeshRenderer, ICollection<string>> shapes = null,
            bool background = false)
        {
            int removed = 0;
            float budget = Budget(quality);
            foreach (var renderer in usage.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                var mesh = MeshOptimization.MeshOf(renderer);
                if (mesh == null || mesh.bounds.size.sqrMagnitude <= 0f) continue;
                int before = Triangles(mesh);
                if (before < MinTriangles) continue;
                // Meters on the avatar per unit of the mesh, as it stands.
                float units = renderer.bounds.size.magnitude / mesh.bounds.size.magnitude;
                if (units <= 0f || float.IsNaN(units)) continue;
                var skinned = renderer as SkinnedMeshRenderer;
                var uploaded = skinned != null && shapes != null ? shapes(skinned) : null;
                // The shapes that shape it: those animations use (or that upload, when known) and those set to a weight now.
                var used = new HashSet<string>(StringComparer.Ordinal);
                var weights = new Dictionary<string, float>(StringComparer.Ordinal);
                if (skinned != null && mesh.blendShapeCount > 0)
                {
                    // An estimate can't know which shapes build tools will animate: every one counts there, the closest it gets to
                    // the upload without building it (MasculineCanine at 8 mm: the estimate says 1,010, the upload 506).
                    if (background) used.UnionWith(Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName));
                    else used.UnionWith(uploaded ?? BlendShapePruning.Used(usage.Root, skinned));
                    for (int shape = 0; shape < mesh.blendShapeCount; shape++)
                    {
                        float weight = skinned.GetBlendShapeWeight(shape);
                        if (Mathf.Abs(weight) <= 1e-4f) continue;
                        used.Add(mesh.GetBlendShapeName(shape));
                        weights[mesh.GetBlendShapeName(shape)] = weight;
                    }
                }
                string key = Key(mesh, used) + "|" + (uploaded == null ? "all" : Key(mesh, uploaded)) + "|" +
                             string.Join(",", weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => w.Key + "=" + w.Value.ToString("R"))) + "@" + (budget / units).ToString("R");
                if (!Done.TryGetValue(key, out var simplified))
                {
                    if (background)
                    {
                        if (Running.Add(key)) Start(key, mesh, used, uploaded, weights, budget / units);
                        continue;
                    }
                    simplified = Simplify(mesh, used, uploaded, weights, budget / units, false).Result;
                    Remember(key, simplified);
                }
                if (simplified == null) continue;
                var copy = Object.Instantiate(simplified);
                copy.name = mesh.name; copy.hideFlags = HideFlags.None;
                MeshOptimization.SetMesh(renderer, copy);
                made.Add(copy);
                removed += before - Triangles(copy);
            }
            return removed;
        }

        // On a copy of the mesh: the estimate's own is gone before this finishes.
        private static async void Start(string key, Mesh mesh, ICollection<string> used, ICollection<string> uploaded, Dictionary<string, float> weights, float budget)
        {
            var source = Object.Instantiate(mesh);
            source.hideFlags = HideFlags.HideAndDontSave;
            Mesh result = null;
            try { result = await Simplify(source, used, uploaded, weights, budget, true); }
            catch (Exception ex) { Debug.LogException(ex); }
            finally
            {
                Object.DestroyImmediate(source);
                Remember(key, result);
                Running.Remove(key);
                Ready?.Invoke();
            }
        }

        // The part of the mesh no expression moves, simplified to the fewest triangles whose surface stays within the budget
        // (in the mesh's units), joined back to the rest; null when that removes nothing worth it. In the background, the simplifier
        // runs as jobs and the measures on another thread, and each step on the editor's thread is a frame of its own.
        private static async Task<Mesh> Simplify(Mesh mesh, ICollection<string> used, ICollection<string> uploaded, Dictionary<string, float> weights, float budget,
            bool background)
        {
            var reach = MeshParts.Reach(mesh, out var moved);
            bool Used(int shape) => used.Contains(mesh.GetBlendShapeName(shape)) && reach[shape] > 0;
            bool Wide(int shape) => Used(shape) && reach[shape] >= mesh.vertexCount * Broad;
            var locked = new bool[mesh.vertexCount];
            for (int shape = 0; shape < reach.Length; shape++)
                if (Used(shape) && !Wide(shape))
                    for (int v = 0; v < locked.Length; v++) locked[v] |= moved[shape][v];
            bool Locked(int a, int b, int c) => locked[a] || locked[b] || locked[c];

            // The simplifier gets the shape alone, the broad shapes given back afterwards: carried through every collapse, a
            // body's hundreds of shapes take gigabytes.
            var free = MeshParts.Subset(mesh, (a, b, c) => !Locked(a, b, c), Wide);
            var plain = MeshParts.Subset(mesh, (a, b, c) => !Locked(a, b, c));
            Mesh best = null;
            try
            {
                if (Triangles(free) < MinTriangles) return null;
                var slots = MeshParts.DropEmptySubmeshes(free);
                MeshParts.DropEmptySubmeshes(plain);
                var options = MeshSimplifierOptions.Default;
                options.PreserveBorderEdges = true;
                options.PreserveSurfaceCurvature = true;
                // Smart links join nearby vertices that aren't connected: across UV seams they mix texture islands (seen on
                // a body whose UVs carry four components).
                options.EnableSmartLink = false;
                // Where the surface was: at rest, as it stands (each broad shape at the weight it has now), and with each broad
                // shape alone at full weight. At rest and as it stands, how it shades too (its normals).
                var rest = free.vertices;
                var restNormals = free.normals;
                var standing = new Dictionary<string, float>(StringComparer.Ordinal);
                var single = new List<Dictionary<string, float>>();
                for (int shape = 0; shape < free.blendShapeCount; shape++)
                {
                    string name = free.GetBlendShapeName(shape);
                    single.Add(new Dictionary<string, float>(StringComparer.Ordinal) { [name] = 100f });
                    if (weights.TryGetValue(name, out float weight)) standing[name] = weight;
                }
                // At rest and as it stands first, every point: what fails there settles it soonest. Each shape alone on a
                // quarter of them.
                var poses = new List<Dictionary<string, float>> { new Dictionary<string, float>() };
                if (standing.Count > 0) poses.Add(standing);
                poses.AddRange(single);
                bool Shaded(Dictionary<string, float> pose) => restNormals.Length == rest.Length && (pose.Count == 0 || pose == standing);
                Vector3[] Points(Dictionary<string, float> pose)
                {
                    var points = Posed(free, rest, pose);
                    return points == null || Shaded(pose) ? points : points.Where((_, i) => i % 4 == 0).ToArray();
                }
                var surfaces = poses.Select(pose => (Pose: pose, Points: Points(pose), Normals: Shaded(pose) ? Turned(free, restNormals, pose) : null))
                    .Where(s => s.Points != null).ToList();
                if (background) await Task.Yield();

                async Task<Mesh> Try(float keep)
                {
                    var result = new Mesh();
                    var target = new MeshSimplificationTarget { Kind = MeshSimplificationTargetKind.RelativeTriangleCount, Value = keep };
                    if (background) await MeshSimplifier.SimplifyAsync(plain, target, options, result);
                    else MeshSimplifier.Simplify(plain, target, options, result);
                    MeshParts.TransferShapes(free, result, _ => true);
                    // The simplified surface in the same poses, read here; measured anywhere.
                    var vertices = result.vertices;
                    var triangles = result.triangles;
                    var resultNormals = result.normals;
                    var shaped = new List<(Vector3[] Points, Vector3[] Normals, Vector3[] Vertices, Vector3[] VertexNormals)>();
                    foreach (var (pose, points, normals) in surfaces)
                    {
                        var posed = Posed(result, vertices, pose);
                        if (posed == null) { Object.DestroyImmediate(result); return null; }
                        shaped.Add((points, normals, posed, normals != null && resultNormals.Length == vertices.Length ? Turned(result, resultNormals, pose) : null));
                    }
                    bool Fits() => shaped.All(s => MeshParts.Within(s.Points, s.Vertices, triangles, budget, s.Normals, s.VertexNormals));
                    if (background ? await Task.Run(Fits) : Fits()) return result;
                    Object.DestroyImmediate(result);
                    return null;
                }

                // Nothing to gain unless a twentieth can go; then halving the range between what fits and what doesn't.
                float high = .95f, low = .3f;
                best = await Try(high);
                if (best == null) return null;
                for (int step = 0; step < 4; step++)
                {
                    float keep = step == 0 ? low : (low + high) / 2f;
                    var result = await Try(keep);
                    if (result == null) { low = keep; continue; }
                    Object.DestroyImmediate(best);
                    best = result; high = keep;
                    if (step == 0) break;
                }
                if (background) await Task.Yield();
                var joined = MeshParts.Replace(mesh, Locked, best, slots, shape => uploaded == null || uploaded.Contains(mesh.GetBlendShapeName(shape)));
                joined.name = mesh.name;
                if (Triangles(joined) > Triangles(mesh) * .99f) { Object.DestroyImmediate(joined); return null; }
                joined.hideFlags = HideFlags.HideAndDontSave;
                return joined;
            }
            finally
            {
                Object.DestroyImmediate(free);
                Object.DestroyImmediate(plain);
                if (best != null) Object.DestroyImmediate(best);
            }
        }

        // The normals of the mesh with these blendshapes at these weights (the last frame, scaled), normalized.
        private static Vector3[] Turned(Mesh mesh, Vector3[] rest, Dictionary<string, float> pose)
        {
            var normals = (Vector3[])rest.Clone();
            var deltas = new Vector3[mesh.vertexCount];
            var moves = new Vector3[mesh.vertexCount];
            foreach (var pair in pose)
            {
                int shape = mesh.GetBlendShapeIndex(pair.Key);
                if (shape < 0) continue;
                int frame = mesh.GetBlendShapeFrameCount(shape) - 1;
                mesh.GetBlendShapeFrameVertices(shape, frame, moves, deltas, null);
                float scale = pair.Value / mesh.GetBlendShapeFrameWeight(shape, frame);
                for (int i = 0; i < normals.Length; i++) normals[i] += deltas[i] * scale;
            }
            for (int i = 0; i < normals.Length; i++) normals[i] = normals[i].normalized;
            return normals;
        }

        // The mesh's vertices with these blendshapes at these weights (the last frame, scaled); null when one is missing.
        private static Vector3[] Posed(Mesh mesh, Vector3[] rest, Dictionary<string, float> pose)
        {
            var points = (Vector3[])rest.Clone();
            var deltas = new Vector3[mesh.vertexCount];
            foreach (var pair in pose)
            {
                int shape = mesh.GetBlendShapeIndex(pair.Key);
                if (shape < 0) return null;
                int frame = mesh.GetBlendShapeFrameCount(shape) - 1;
                mesh.GetBlendShapeFrameVertices(shape, frame, deltas, null, null);
                float scale = pair.Value / mesh.GetBlendShapeFrameWeight(shape, frame);
                for (int i = 0; i < points.Length; i++) points[i] += deltas[i] * scale;
            }
            return points;
        }

        private static void Remember(string key, Mesh mesh)
        {
            Done[key] = mesh;
            Order.Add(key);
            while (Order.Count > 16)
            {
                if (Done.TryGetValue(Order[0], out var old) && old != null) Object.DestroyImmediate(old);
                Done.Remove(Order[0]);
                Order.RemoveAt(0);
            }
        }

        // The mesh's geometry and the blendshapes that count, hashed: copies of one mesh share it.
        private static string Key(Mesh mesh, ICollection<string> names)
        {
            unchecked
            {
                long hash = 17;
                foreach (var v in mesh.vertices) hash = hash * 31 + v.GetHashCode();
                for (int s = 0; s < mesh.subMeshCount; s++)
                    foreach (int i in mesh.GetIndices(s)) hash = hash * 31 + i;
                foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal)) hash = hash * 31 + name.GetHashCode();
                return mesh.vertexCount + ":" + mesh.subMeshCount + ":" + mesh.blendShapeCount + ":" + hash;
            }
        }

        private static int Triangles(Mesh mesh)
        {
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
            return (int)(indices / 3);
        }
    }
}
