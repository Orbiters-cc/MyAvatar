using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using Orbiters.Toolkit.Editor.VRChat.Optimization;
using UnityEditor;
using UnityEngine;
using Orbiters.AlterMesh;
using Object = UnityEngine.Object;

namespace Orbiters.MyAvatar.Editor.Optimization
{
    /// <summary>
    /// Fewer polygons for the uploaded copy through AlterMesh (Orbiters' simplifier, derived from Meshia). Each mesh is
    /// simplified as far as its surface stays within the quality's budget, measured: at quality 1, 99% of its points stay
    /// within half a millimetre of the simplified surface (half a pixel seen from a meter away) and none beyond twice that;
    /// each step lower doubles the budget. The triangles an expression, a viseme or a small toggle's blendshape moves stay
    /// exactly as they are (the simplifier doesn't weigh blendshapes); broad shapes (a body type, a muscle flex, a pose
    /// corrective) vary smoothly and are carried through, and the surface is checked with each of them at full weight too.
    /// Borders and seams are kept (no holes, no torn UVs), so are weights and material slots. A mesh the simplifier fails
    /// on keeps its polygons, whole.
    /// </summary>
    internal static class PolygonReduction
    {
        // Smaller meshes have too little to gain, and each polygon of them shows.
        private const int MinTriangles = 2000;
        // A blendshape moving at least this share of a mesh reshapes a broad area smoothly (a body type, a muscle flex, a pose
        // corrective); one moving less is an expression, a viseme or a small toggle (a face's are under a fifth of a body).
        private const float Broad = .25f;
        // Meshes simplified, by their source's content and budget: the estimate measures again at every change. At least
        // this many are kept, more when the avatars' last estimates need more (every mesh each of them measures).
        private const int Capacity = 16;

        private sealed class Request
        {
            public string Key;
            public Mesh Source;
            public ICollection<string> Used, Uploaded;
            public Dictionary<string, float> Weights;
            public float Budget;
        }

        // The cache and the background runs. Tests work in a state of their own (Isolated): the user's estimates keep theirs.
        private sealed class State
        {
            public readonly Dictionary<string, Mesh> Done = new Dictionary<string, Mesh>();
            // Keys from the least to the most recently used.
            public readonly List<string> Order = new List<string>();
            // What each avatar's last estimate needs, by the avatar's instance: kept whatever the capacity, and only what
            // some estimate still needs is simplified in the background.
            public readonly Dictionary<int, HashSet<string>> Wanted = new Dictionary<int, HashSet<string>>();
            // Simplified one after another in the background, each a few frames of its own.
            public readonly HashSet<string> Running = new HashSet<string>();
            public readonly Queue<Request> Queue = new Queue<Request>();
            public bool Working, Released;
            // Meshes the background runs and the cache hold, hidden and never saved: a script reload would leave them in
            // memory with nothing pointing at them, so they go before it, and the runs stop (their native memory freed at once).
            public readonly HashSet<Mesh> Held = new HashSet<Mesh>();
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();

            public bool Needed(string key) => Wanted.Values.Any(keys => keys.Contains(key));

            public Mesh Hold(Mesh mesh)
            {
                if (mesh == null) return null;
                mesh.hideFlags = HideFlags.HideAndDontSave;
                Held.Add(mesh);
                return mesh;
            }

            public void Free(Mesh mesh)
            {
                if (ReferenceEquals(mesh, null)) return;
                Held.Remove(mesh);
                if (mesh) Object.DestroyImmediate(mesh);
            }

            public void Release()
            {
                Released = true;
                try { Cancel.Cancel(); }
                catch (AggregateException ex) { Debug.LogException(ex); }
                Cancel.Dispose();
                foreach (var mesh in Held) if (mesh) Object.DestroyImmediate(mesh);
                Held.Clear();
                Done.Clear();
                Order.Clear();
                Wanted.Clear();
                Running.Clear();
                Queue.Clear();
                Working = false;
            }
        }

        private static State state = new State();
        // States set aside while a test works in its own.
        private static readonly List<State> Outer = new List<State>();
        // Meshes made from another by removing triangles only (the body under clothing), with that other mesh.
        private static readonly ConditionalWeakTable<Mesh, Mesh> Sources = new ConditionalWeakTable<Mesh, Mesh>();

        static PolygonReduction() => AssemblyReloadEvents.beforeAssemblyReload += () =>
        {
            foreach (var outer in Outer) outer.Release();
            Release();
        };

        /// <summary>Whether meshes are being simplified in the background for an estimate.</summary>
        internal static bool Measuring => state.Running.Count > 0;

        /// <summary>How many meshes the runs and the cache hold (tests).</summary>
        internal static int HeldCount => state.Held.Count;

        /// <summary>Runs on the main thread once every mesh asked for in the background is ready.</summary>
        internal static event Action Ready;

        /// <summary>The largest change allowed, in meters on the avatar.</summary>
        internal static float Budget(float quality) => .0005f * Mathf.Pow(2f, 4f * (1f - Mathf.Clamp01(quality)));

        /// <summary>
        /// Simplifies the copy's meshes; returns how many triangles went. The meshes made go in <paramref name="made"/>.
        /// <paramref name="shapes"/> names the blendshapes each skinned mesh uploads with (null: all it has): the others are
        /// left out of the simplified mesh. In the <paramref name="background"/> (an estimate for the avatar
        /// <paramref name="owner"/>), a mesh not simplified yet stays as it is while it is, then <see cref="Ready"/> runs.
        /// </summary>
        internal static int Reduce(AvatarUsage usage, float quality, ICollection<Mesh> made, Func<SkinnedMeshRenderer, ICollection<string>> shapes = null,
            bool background = false, int owner = 0)
        {
            var runs = state;
            int removed = 0;
            float budget = Budget(quality);
            var keys = new HashSet<string>();
            foreach (var renderer in usage.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                var mesh = MeshOptimization.MeshOf(renderer);
                if (mesh == null || mesh.vertexCount == 0) continue;
                int before = Triangles(mesh);
                if (before < MinTriangles) continue;
                Mesh simplified;
                try
                {
                    // Meters on the avatar per unit of the mesh: what its vertices go through to reach the world.
                    float units = Units(renderer, mesh);
                    if (!(units > 1e-6f) || float.IsInfinity(units)) continue;
                    var skinned = renderer as SkinnedMeshRenderer;
                    var uploaded = skinned != null && shapes != null ? shapes(skinned) : null;
                    // The shapes that shape it: those animations use (or that upload, when known) and those set to a weight now.
                    var used = new HashSet<string>(StringComparer.Ordinal);
                    var weights = new Dictionary<string, float>(StringComparer.Ordinal);
                    if (skinned != null && mesh.blendShapeCount > 0)
                    {
                        // An estimate can't know which shapes build tools will animate: every one counts there, the closest it gets
                        // to the upload without building it.
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
                    float local = budget / units;
                    string key = MeshKey(mesh) + "|" + Names(used) + "|" + (uploaded == null ? "all" : Names(uploaded)) + "|" +
                                 string.Join(",", weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => w.Key + "=" + w.Value.ToString("R", CultureInfo.InvariantCulture))) +
                                 "@" + local.ToString("R", CultureInfo.InvariantCulture);
                    keys.Add(key);
                    if (!Lookup(runs, key, out simplified))
                    {
                        if (background)
                        {
                            Enqueue(runs, new Request { Key = key, Source = mesh, Used = used, Uploaded = uploaded, Weights = weights, Budget = local });
                            continue;
                        }
                        // A mesh the simplifier fails on keeps its polygons: remembered as one with nothing to gain.
                        try { simplified = Simplify(runs, mesh, used, uploaded, weights, local, false, CancellationToken.None).GetAwaiter().GetResult(); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning("[My Avatar] Optimization: " + mesh.name + " keeps its polygons (" + ex.GetBaseException().Message + ").");
                            simplified = null;
                        }
                        simplified = Remember(runs, key, simplified);
                    }
                }
                catch (Exception ex)
                {
                    // Nothing of this mesh changed yet: it uploads as it is, the others go on.
                    Debug.LogWarning("[My Avatar] Optimization: " + mesh.name + " keeps its polygons (" + ex.GetBaseException().Message + ").");
                    continue;
                }
                if (simplified == null) continue;
                var copy = Object.Instantiate(simplified);
                copy.name = mesh.name; copy.hideFlags = HideFlags.None;
                MeshOptimization.SetMesh(renderer, copy);
                made.Add(copy);
                removed += before - Triangles(copy);
            }
            if (background)
            {
                // Avatars gone since need nothing.
                foreach (int gone in runs.Wanted.Keys.Where(k => k != 0 && k != owner && EditorUtility.InstanceIDToObject(k) == null).ToList()) runs.Wanted.Remove(gone);
                runs.Wanted[owner] = keys;
            }
            return removed;
        }

        /// <summary>
        /// Remembers that <paramref name="made"/> is <paramref name="source"/> with some triangles removed (its vertices,
        /// weights and blendshapes the same): the cache knows it by the source and the triangles left, without reading it
        /// whole.
        /// </summary>
        internal static void FewerTriangles(Mesh made, Mesh source)
        {
            if (made == null || source == null || made == source) return;
            Sources.Remove(made);
            Sources.Add(made, source);
        }

        /// <summary>
        /// Stops the background runs (their native memory freed at once) and destroys what they and the cache hold: before
        /// a script reload, which would leave them behind.
        /// </summary>
        internal static void Release()
        {
            state.Release();
            state = new State();
        }

        /// <summary>
        /// A cache and background runs of their own until disposed (tests): the user's stay as they are, theirs released then.
        /// </summary>
        internal static IDisposable Isolated()
        {
            var outer = state;
            Outer.Add(outer);
            state = new State();
            return new Scope(() =>
            {
                state.Release();
                Outer.Remove(outer);
                state = outer;
            });
        }

        private sealed class Scope : IDisposable
        {
            private Action end;
            public Scope(Action end) => this.end = end;
            public void Dispose() { end?.Invoke(); end = null; }
        }

        // Meters on the avatar per unit of the mesh: a skinned mesh's vertices reach the world through its bones' bind poses
        // (the typical one: a bone scaled to nothing doesn't count), a plain mesh through its transform. Bounds can't say:
        // an inactive renderer has none, and a tool may enlarge them (Modular Avatar's Mesh Settings).
        private static float Units(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer skinned)
            {
                var bones = skinned.bones;
                var bindposes = mesh.bindposes;
                var scales = new List<float>();
                for (int i = 0; i < Mathf.Min(bones.Length, bindposes.Length); i++)
                    if (bones[i] != null)
                    {
                        float scale = Scale(bones[i].localToWorldMatrix * bindposes[i]);
                        if (scale > 1e-6f && !float.IsNaN(scale)) scales.Add(scale);
                    }
                if (scales.Count > 0)
                {
                    scales.Sort();
                    return scales[scales.Count / 2];
                }
            }
            return Scale(renderer.transform.localToWorldMatrix);
        }

        private static float Scale(Matrix4x4 matrix)
        {
            var scale = matrix.lossyScale;
            return Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
        }

        // ---- Background runs ----

        private static void Enqueue(State runs, Request request)
        {
            if (!runs.Running.Add(request.Key)) return;
            // On a copy of the mesh: the estimate's own is gone before this runs.
            request.Source = runs.Hold(Object.Instantiate(request.Source));
            runs.Queue.Enqueue(request);
            if (!runs.Working) Work(runs);
        }

        // One mesh after another, each starting on a frame of its own: the estimate that asked goes on at once. A mesh no
        // estimate needs any more (a setting changed meanwhile, a slider moved on) is dropped unsimplified.
        private static async void Work(State runs)
        {
            runs.Working = true;
            var token = runs.Cancel.Token;
            while (runs.Queue.Count > 0)
            {
                await Task.Yield();
                if (runs.Released) return;
                var request = runs.Queue.Dequeue();
                if (!runs.Needed(request.Key))
                {
                    runs.Free(request.Source);
                    runs.Running.Remove(request.Key);
                    continue;
                }
                Mesh result = null;
                try { result = await Simplify(runs, request.Source, request.Used, request.Uploaded, request.Weights, request.Budget, true, token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Debug.LogWarning("[My Avatar] Optimization: " + request.Source.name + " keeps its polygons (" + ex.GetBaseException().Message + ")."); }
                // Released meanwhile: what this run held is gone with the rest.
                if (runs.Released) { runs.Free(result); return; }
                runs.Free(request.Source);
                // A mesh that failed is remembered as one with nothing to gain: it isn't tried again and again.
                Remember(runs, request.Key, result);
                runs.Running.Remove(request.Key);
            }
            runs.Working = false;
            try { Ready?.Invoke(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        private static bool Lookup(State runs, string key, out Mesh mesh)
        {
            if (!runs.Done.TryGetValue(key, out mesh)) return false;
            // A mesh destroyed from outside: made again.
            if (!ReferenceEquals(mesh, null) && mesh == null) { runs.Done.Remove(key); runs.Order.Remove(key); mesh = null; return false; }
            runs.Order.Remove(key);
            runs.Order.Add(key);
            return true;
        }

        // Returns the mesh kept for the key.
        private static Mesh Remember(State runs, string key, Mesh mesh)
        {
            // Two runs of one mesh (an upload beside an estimate's background run): the first result stays.
            if (runs.Done.TryGetValue(key, out var known))
            {
                if (!ReferenceEquals(known, mesh)) runs.Free(mesh);
                return known;
            }
            runs.Done[key] = mesh;
            runs.Order.Add(key);
            // The least recently used go first; what an avatar's last estimate measures stays, or it would simplify again forever.
            var needed = new HashSet<string>(runs.Wanted.Values.SelectMany(keys => keys));
            for (int i = 0; runs.Done.Count > Math.Max(Capacity, needed.Count) && i < runs.Order.Count;)
            {
                string old = runs.Order[i];
                if (needed.Contains(old) || old == key) { i++; continue; }
                runs.Free(runs.Done[old]);
                runs.Done.Remove(old);
                runs.Order.RemoveAt(i);
            }
            return mesh;
        }

        // ---- Simplifying ----

        // The part of the mesh no expression moves, simplified to the fewest triangles whose surface stays within the budget
        // (in the mesh's units), joined back to the rest; null when that removes nothing worth it. In the background, the
        // simplifier runs as jobs and the measures on another thread, and each step on the editor's thread is a frame of its own.
        private static async Task<Mesh> Simplify(State runs, Mesh mesh, ICollection<string> used, ICollection<string> uploaded, Dictionary<string, float> weights, float budget,
            bool background, CancellationToken token)
        {
            async Task Pause()
            {
                if (!background) return;
                await Task.Yield();
                token.ThrowIfCancellationRequested();
            }

            var reach = MeshParts.Reach(mesh, out var moved);
            bool Used(int shape) => used.Contains(mesh.GetBlendShapeName(shape)) && reach[shape] > 0;
            bool Wide(int shape) => Used(shape) && reach[shape] >= mesh.vertexCount * Broad;
            var locked = new bool[mesh.vertexCount];
            for (int shape = 0; shape < reach.Length; shape++)
                if (Used(shape) && !Wide(shape))
                    for (int v = 0; v < locked.Length; v++) locked[v] |= moved[shape][v];
            bool Locked(int a, int b, int c) => locked[a] || locked[b] || locked[c];
            await Pause();

            // The simplifier gets the shape alone, the broad shapes given back afterwards: carried through every collapse, a
            // body's hundreds of shapes take gigabytes.
            Mesh free = null, plain = null, best = null;
            try
            {
                free = runs.Hold(MeshParts.Subset(mesh, (a, b, c) => !Locked(a, b, c), Wide));
                await Pause();
                plain = runs.Hold(MeshParts.Subset(mesh, (a, b, c) => !Locked(a, b, c)));
                if (Triangles(free) < MinTriangles) return null;
                var slots = MeshParts.DropEmptySubmeshes(free);
                MeshParts.DropEmptySubmeshes(plain);
                var options = MeshSimplifierOptions.Default;
                options.PreserveBorderEdges = true;
                options.PreserveSurfaceCurvature = true;
                // Smart links join nearby vertices that aren't connected: across UV seams they mix texture islands (seen on
                // a body whose UVs carry four components).
                options.EnableSmartLink = false;
                await Pause();
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
                // The broad shapes' source, read once for every try; matching a try's vertices to it runs on another thread
                // in the background (seconds on a body, on the editor's thread it stalled the Inspector).
                var shapes = new MeshParts.ShapeSource(free);
                await Pause();

                async Task<Mesh> Try(float keep)
                {
                    var result = runs.Hold(new Mesh());
                    try
                    {
                        var target = new MeshSimplificationTarget { Kind = MeshSimplificationTargetKind.RelativeTriangleCount, Value = keep };
                        if (background) await MeshSimplifier.SimplifyAsync(plain, target, options, result, token);
                        else MeshSimplifier.Simplify(plain, target, options, result);
                        token.ThrowIfCancellationRequested();
                        var vertices = result.vertices;
                        var targetNormals = result.normals;
                        var matches = background ? await Task.Run(() => shapes.Match(vertices, targetNormals), token) : shapes.Match(vertices, targetNormals);
                        token.ThrowIfCancellationRequested();
                        shapes.Apply(result, matches, _ => true);
                        // The simplified surface in the same poses, read here; measured anywhere.
                        var triangles = result.triangles;
                        var resultNormals = result.normals;
                        var shaped = new List<(Vector3[] Points, Vector3[] Normals, Vector3[] Vertices, Vector3[] VertexNormals)>();
                        foreach (var (pose, points, normals) in surfaces)
                        {
                            var posed = Posed(result, vertices, pose);
                            if (posed == null) { runs.Free(result); return null; }
                            shaped.Add((points, normals, posed, normals != null && resultNormals.Length == vertices.Length ? Turned(result, resultNormals, pose) : null));
                        }
                        bool Fits() => shaped.All(s => MeshParts.Within(s.Points, s.Vertices, triangles, budget, s.Normals, s.VertexNormals));
                        bool fits = background ? await Task.Run(Fits, token) : Fits();
                        token.ThrowIfCancellationRequested();
                        if (fits) return result;
                        runs.Free(result);
                        return null;
                    }
                    catch
                    {
                        runs.Free(result);
                        throw;
                    }
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
                    runs.Free(best);
                    best = result; high = keep;
                    if (step == 0) break;
                }
                await Pause();
                var joined = runs.Hold(MeshParts.Replace(mesh, Locked, best, slots, shape => uploaded == null || uploaded.Contains(mesh.GetBlendShapeName(shape))));
                joined.name = mesh.name;
                if (Triangles(joined) > Triangles(mesh) * .99f) { runs.Free(joined); return null; }
                return joined;
            }
            finally
            {
                runs.Free(free);
                runs.Free(plain);
                runs.Free(best);
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

        // ---- Keys ----

        /// <summary>
        /// What a mesh holds, for the cache: an imported mesh by its asset and how it was imported (a new export or new import
        /// settings change it), any other by everything simplifying reads and keeps (every vertex attribute with the bone
        /// weights, the triangles, the bind poses, each blendshape's frames and offsets).
        /// </summary>
        internal static string MeshKey(Mesh mesh)
        {
            if (Sources.TryGetValue(mesh, out var source) && source != null) return MeshKey(source) + ":less:" + IndexKey(mesh);
            if (EditorUtility.IsPersistent(mesh) && !EditorUtility.IsDirty(mesh) && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long local))
                return guid + ":" + local + ":" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(mesh));
            ulong hash = 14695981039346656037UL;
            void Add(int value) { unchecked { hash = (hash ^ (uint)value) * 1099511628211UL; } }
            void AddAll(ReadOnlySpan<int> values) { foreach (int value in values) Add(value); }
            using (var data = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                var part = data[0];
                for (int stream = 0; stream < part.vertexBufferCount; stream++)
                {
                    var bytes = part.GetVertexData<byte>(stream);
                    // Vertex strides are whole 4-byte words.
                    if (bytes.Length % 4 == 0) AddAll(bytes.Reinterpret<int>(1).AsReadOnlySpan());
                    else foreach (byte b in bytes) Add(b);
                }
                if (part.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32) AddAll(part.GetIndexData<int>().AsReadOnlySpan());
                else foreach (ushort index in part.GetIndexData<ushort>()) Add(index);
                Add(part.subMeshCount);
                for (int s = 0; s < part.subMeshCount; s++)
                {
                    var sub = part.GetSubMesh(s);
                    Add(sub.indexStart); Add(sub.indexCount); Add(sub.baseVertex); Add((int)sub.topology);
                }
            }
            foreach (var pose in mesh.bindposes) for (int i = 0; i < 16; i++) Add(pose[i].GetHashCode());
            var deltas = new Vector3[mesh.vertexCount];
            var normals = new Vector3[mesh.vertexCount];
            var tangents = new Vector3[mesh.vertexCount];
            for (int shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                Add(mesh.GetBlendShapeName(shape).GetHashCode());
                for (int frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    Add(mesh.GetBlendShapeFrameWeight(shape, frame).GetHashCode());
                    mesh.GetBlendShapeFrameVertices(shape, frame, deltas, normals, tangents);
                    AddAll(MemoryMarshal.Cast<Vector3, int>(deltas));
                    AddAll(MemoryMarshal.Cast<Vector3, int>(normals));
                    AddAll(MemoryMarshal.Cast<Vector3, int>(tangents));
                }
            }
            return mesh.vertexCount + ":" + mesh.subMeshCount + ":" + mesh.blendShapeCount + ":" + hash.ToString("x16");
        }

        // The triangles of each submesh, hashed.
        private static string IndexKey(Mesh mesh)
        {
            ulong hash = 14695981039346656037UL;
            for (int s = 0; s < mesh.subMeshCount; s++)
                unchecked
                {
                    hash = (hash ^ (uint)mesh.GetTopology(s)) * 1099511628211UL;
                    foreach (int index in mesh.GetIndices(s, false)) hash = (hash ^ (uint)index) * 1099511628211UL;
                    hash = (hash ^ (uint)mesh.GetBaseVertex(s)) * 1099511628211UL;
                }
            return mesh.subMeshCount + ":" + hash.ToString("x16");
        }

        // The names, sorted and hashed: a body names hundreds.
        private static string Names(ICollection<string> names)
        {
            ulong hash = 14695981039346656037UL;
            foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
                unchecked
                {
                    foreach (char c in name) hash = (hash ^ c) * 1099511628211UL;
                    hash = (hash ^ 0xFFFF) * 1099511628211UL;
                }
            return names.Count + ":" + hash.ToString("x16");
        }

        private static int Triangles(Mesh mesh)
        {
            long indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
            return (int)(indices / 3);
        }
    }
}
