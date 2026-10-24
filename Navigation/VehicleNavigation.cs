using System.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppPathfinding;
using Il2CppScheduleOne.Vehicles.AI;
using MapExpansions.Lifecycle;
using MelonLoader;
using UnityEngine;
using AstarPath = Il2Cpp.AstarPath;
using Object = UnityEngine.Object;

namespace MapExpansions.Navigation
{
    /// <summary>
    /// Vehicle navigation for one placed expansion. Vanilla vehicles path on the A* recast graph "General Vehicle
    /// Graph", which ends at the map edge. Two graphs are added with its settings: one over the expansion and the
    /// approach to the connection, and a clean one over the built stretches, scanned with every other collider
    /// switched off so terrain and rock above or around the tunnel cannot block it. The graphs are joined to each
    /// other and to the vanilla graph by node connections across the road; a lookup of the vanilla graph by name
    /// covers the added ones too.
    /// </summary>
    internal sealed class VehicleNavigation
    {
        public const string GeneralGraphName = "General Vehicle Graph";
        public const string RoadNodesGraphName = "Road Nodes";
        private const float WatchInterval = 10f;

        /// <summary>Graph bits that every lookup of the vanilla vehicle graph also covers.</summary>
        internal static int ExtraMask;

        private sealed class Join
        {
            public string What;
            public int GraphA, GraphB;
            public Vector3 PointA, PointB;
            public GraphNode NodeA, NodeB;
        }

        private readonly string _id;
        private readonly List<Join> _joins = new();
        private int _areaGraph = -1, _corridorGraph = -1;
        private object _watch;
        private bool _alive = true;

        public VehicleNavigation(string id) => _id = id;

        private static AstarPath Astar => AstarPath.active ?? throw new InvalidOperationException("AstarPath.active is null");

        private static int IndexOf(AstarPath a, NavGraph g)
        {
            for (int i = 0; i < a.data.graphs.Length; i++)
                if (a.data.graphs[i] != null && a.data.graphs[i].Pointer == g.Pointer) return i;
            return -1;
        }

        private static int IndexOf(AstarPath a, string name)
        {
            for (int i = 0; i < a.data.graphs.Length; i++)
                if (a.data.graphs[i] != null && a.data.graphs[i].name == name) return i;
            return -1;
        }

        public IEnumerator Build(BuiltExpansion b)
        {
            AstarPath a;
            try { a = Astar; }
            catch (Exception e)
            {
                Core.Log.Error($"vehicle nav: '{b.Id}' {e.Message}; vehicles cannot drive into the expansion");
                yield break;
            }
            int general = IndexOf(a, GeneralGraphName);
            var src = general >= 0 ? a.data.graphs[general]?.TryCast<RecastGraph>() : null;
            if (src == null)
            {
                Core.Log.Error($"vehicle nav: '{b.Id}' no recast graph '{GeneralGraphName}'; vehicles cannot drive into the expansion");
                yield break;
            }

            // The expansion's area plus the approach of every connection.
            var def = b.Definition;
            Rect local = def.VehicleArea;
            var area = local.size != Vector2.zero ? b.Frame.ToWorld(local) : TerrainArea(b);
            var areaBounds = new Bounds(new Vector3(area.center.x, 0f, area.center.y), new Vector3(area.width, 0f, area.height));
            Bounds? corridor = null;
            foreach (var conn in b.Connections)
            {
                var range = conn.BuiltRange;
                for (int i = 0; i < conn.Curve.Count; i++)
                {
                    var p = conn.Curve.Points[i];
                    float d = conn.Curve.Distance[i];
                    areaBounds.Encapsulate(p);
                    if (range.to <= range.from || d < range.from || d > range.to) continue;
                    if (corridor == null) corridor = new Bounds(p, Vector3.zero);
                    else { var c = corridor.Value; c.Encapsulate(p); corridor = c; }
                }
                if (conn.Definition.VehicleJoin.HasValue) areaBounds.Encapsulate(b.Frame.Resolve(conn.Definition.VehicleJoin.Value));
            }
            areaBounds.Expand(new Vector3(30f, 0f, 30f));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var areaGraph = AddRecast(a, src, $"MapExpansions {b.Id} Vehicle Graph", areaBounds, full: true);
            if (areaGraph == null) yield break;
            _areaGraph = IndexOf(a, areaGraph);
            Scan(a, areaGraph, "area", null);
            Core.Log.Msg($"vehicle nav: '{b.Id}' area graph {_areaGraph} over x {areaBounds.min.x:0}..{areaBounds.max.x:0} z {areaBounds.min.z:0}..{areaBounds.max.z:0} " +
                         $"scanned in {sw.ElapsedMilliseconds} ms, {areaGraph.CountNodes()} nodes");
            yield return null;

            if (corridor != null && b.Connections.Count > 0)
            {
                var c = corridor.Value;
                float maxCrown = b.Connections.Max(x => x.Definition.CrownHeight);
                float halfWidth = b.Connections.Max(x => x.HalfInnerWidth);
                var cb = new Bounds();
                cb.SetMinMax(c.min - new Vector3(halfWidth + 4f, 2f, halfWidth + 4f), c.max + new Vector3(halfWidth + 4f, maxCrown + 1f, halfWidth + 4f));
                sw.Restart();
                var corridorGraph = AddRecast(a, src, $"MapExpansions {b.Id} Corridor Graph", cb, full: false);
                if (corridorGraph != null)
                {
                    _corridorGraph = IndexOf(a, corridorGraph);
                    Scan(a, corridorGraph, "corridor", b);
                    Core.Log.Msg($"vehicle nav: '{b.Id}' corridor graph {_corridorGraph} over {cb.min}..{cb.max} scanned in {sw.ElapsedMilliseconds} ms, " +
                                 $"{corridorGraph.CountNodes()} nodes");
                }
                yield return null;
            }

            // Joins: vanilla graph to the area graph at the join point, the area graph into and out of the corridor.
            foreach (var conn in b.Connections)
            {
                var curve = conn.Curve;
                float hw = conn.Definition.RoadWidth * 0.5f;
                if (conn.Definition.VehicleJoin.HasValue)
                {
                    var j = b.Frame.Resolve(conn.Definition.VehicleJoin.Value);
                    float d = curve.Project(j, out _);
                    AddJoins($"{conn.Name} vanilla", general, _areaGraph, curve, d, d, hw);
                }
                else Core.Log.Warning($"vehicle nav: '{b.Id}' {conn.Name} has no VehicleJoin; vehicles cannot drive from the main world into it");
                var range = conn.BuiltRange;
                if (_corridorGraph >= 0 && range.to > range.from)
                {
                    AddJoins($"{conn.Name} entry", _areaGraph, _corridorGraph, curve, Mathf.Max(0f, range.from - 3f), range.from + 3f, hw);
                    AddJoins($"{conn.Name} exit", _corridorGraph, _areaGraph, curve, range.to - 3f, Mathf.Min(curve.Length, range.to + 3f), hw);
                }
            }
            ApplyJoins(a, "joined");

            ExtraMask = (_areaGraph >= 0 ? 1 << _areaGraph : 0) | (_corridorGraph >= 0 ? 1 << _corridorGraph : 0);
            int seekers = UpdateSeekers();
            AddRoadNodes(a, b);
            Core.Log.Msg($"vehicle nav: '{b.Id}' graph mask {ExtraMask} added to '{GeneralGraphName}' lookups, {seekers} vehicle seekers updated");

            foreach (var conn in b.Connections) CheckPath(conn);
            _watch = MelonCoroutines.Start(Watch());
        }

        private static Rect TerrainArea(BuiltExpansion b)
        {
            Rect? r = null;
            foreach (var t in b.Content.GetComponentsInChildren<Terrain>(true))
            {
                var p = t.transform.position;
                var s = t.terrainData.size;
                var tr = Rect.MinMaxRect(p.x, p.z, p.x + s.x, p.z + s.z);
                r = r == null ? tr : Rect.MinMaxRect(Mathf.Min(r.Value.xMin, tr.xMin), Mathf.Min(r.Value.yMin, tr.yMin), Mathf.Max(r.Value.xMax, tr.xMax), Mathf.Max(r.Value.yMax, tr.yMax));
            }
            if (r == null) Core.Log.Warning($"vehicle nav: '{b.Id}' has no VehicleArea and no terrains; the area graph covers the connections only");
            return r ?? new Rect(b.Frame.Offset.x, b.Frame.Offset.z, 0f, 0f);
        }

        private static RecastGraph AddRecast(AstarPath a, RecastGraph src, string name, Bounds bounds, bool full)
        {
            RecastGraph g;
            try
            {
                g = a.data.AddGraph(Il2CppType.Of<RecastGraph>()).TryCast<RecastGraph>();
            }
            catch (Exception e)
            {
                Core.Log.Error($"vehicle nav: adding '{name}' failed: {e.Message}");
                return null;
            }
            if (g == null)
            {
                Core.Log.Error($"vehicle nav: AddGraph did not return a RecastGraph for '{name}'");
                return null;
            }
            g.name = name;
            g.cellSize = src.cellSize;
            g.walkableHeight = src.walkableHeight;
            g.walkableClimb = src.walkableClimb;
            g.characterRadius = src.characterRadius;
            g.maxSlope = src.maxSlope;
            g.maxEdgeLength = src.maxEdgeLength;
            g.contourMaxError = src.contourMaxError;
            g.minRegionSize = src.minRegionSize;
            g.useTiles = src.useTiles;
            g.editorTileSize = src.editorTileSize;
            g.mask = src.mask;
            g.rasterizeColliders = full ? src.rasterizeColliders : true;
            g.rasterizeMeshes = full && src.rasterizeMeshes;
            g.rasterizeTerrain = full && src.rasterizeTerrain;
            g.rasterizeTrees = full && src.rasterizeTrees;
            g.colliderRasterizeDetail = src.colliderRasterizeDetail;
            g.terrainSampleSize = src.terrainSampleSize;
            g.rotation = Vector3.zero;
            float height = full ? Mathf.Max(160f, bounds.size.y) : bounds.size.y;
            g.forcedBoundsCenter = new Vector3(bounds.center.x, full ? 40f : bounds.center.y, bounds.center.z);
            g.forcedBoundsSize = new Vector3(bounds.size.x, height, bounds.size.z);
            return g;
        }

        /// <summary>
        /// Scans a graph. For the corridor, every collider in its bounds that the framework did not build is switched
        /// off for the duration of the scan, which runs in one frame, so no physics step sees the gap.
        /// </summary>
        private static void Scan(AstarPath a, RecastGraph g, string what, BuiltExpansion corridorOf)
        {
            var off = new List<Collider>();
            if (corridorOf != null)
            {
                var bounds = new Bounds(g.forcedBoundsCenter, g.forcedBoundsSize);
                foreach (var c in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
                {
                    if (c == null || !c.enabled) continue;
                    if (corridorOf.Connections.Any(x => x.Root != null && c.transform.IsChildOf(x.Root.transform))) continue;
                    c.enabled = false;
                    off.Add(c);
                }
            }
            try
            {
                a.Scan(g);
            }
            catch (Exception e)
            {
                Core.Log.Error($"vehicle nav: scanning the {what} graph '{g.name}' failed: {e}");
            }
            finally
            {
                foreach (var c in off)
                    if (c != null) c.enabled = true;
            }
            if (off.Count > 0) Core.Log.Msg($"vehicle nav: {off.Count} foreign colliders were off during the {what} scan and are back on");
        }

        /// <summary>Three node pairs across the road: one on the centre line and one on each side of it.</summary>
        private void AddJoins(string what, int graphA, int graphB, Connections.RouteCurve curve, float dA, float dB, float halfRoad)
        {
            if (graphA < 0 || graphB < 0)
            {
                Core.Log.Error($"vehicle nav: '{_id}' join {what}: graph {(graphA < 0 ? "A" : "B")} is missing");
                return;
            }
            int iA = curve.IndexAt(dA), iB = curve.IndexAt(dB);
            foreach (float o in new[] { -0.5f, 0f, 0.5f })
            {
                _joins.Add(new Join
                {
                    What = $"{what} {o:+0.0;-0.0;0}",
                    GraphA = graphA,
                    GraphB = graphB,
                    PointA = curve.Points[iA] + curve.Right(iA) * (o * halfRoad),
                    PointB = curve.Points[iB] + curve.Right(iB) * (o * halfRoad),
                });
            }
        }

        private static GraphNode Nearest(AstarPath a, Vector3 p, int graph, out Vector3 at)
        {
            var nn = a.GetNearest(p, new NNConstraint { graphMask = new GraphMask { value = 1 << graph } });
            at = nn.position;
            if (nn.node == null) return null;
            var d = nn.position - p;
            if (Mathf.Abs(d.y) > 2.5f || new Vector2(d.x, d.z).magnitude > 4f) return null;
            return nn.node;
        }

        /// <summary>Finds the nodes of every join that is not connected and connects them in one work item.</summary>
        private int ApplyJoins(AstarPath a, string verb)
        {
            var todo = new List<(Join j, GraphNode na, GraphNode nb, uint cost)>();
            foreach (var j in _joins)
            {
                if (Connected(j)) continue;
                var na = Nearest(a, j.PointA, j.GraphA, out var atA);
                var nb = Nearest(a, j.PointB, j.GraphB, out var atB);
                if (na == null || nb == null)
                {
                    Core.Log.Error($"vehicle nav: '{_id}' join {j.What}: no node near {(na == null ? $"{j.PointA} in graph {j.GraphA} (nearest {atA})" : $"{j.PointB} in graph {j.GraphB} (nearest {atB})")}");
                    continue;
                }
                uint cost = (uint)Mathf.Max(1000f, Vector3.Distance((Vector3)na.position, (Vector3)nb.position) * Int3.Precision);
                todo.Add((j, na, nb, cost));
            }
            if (todo.Count == 0) return 0;
            a.AddWorkItem(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                foreach (var (_, na, nb, cost) in todo)
                {
                    na.AddConnection(nb, cost);
                    nb.AddConnection(na, cost);
                }
            })));
            a.FlushWorkItems();
            a.hierarchicalGraph.RecalculateAll();
            foreach (var (j, na, nb, _) in todo)
            {
                j.NodeA = na;
                j.NodeB = nb;
                Core.Log.Msg($"vehicle nav: '{_id}' {verb} {j.What}: graph {j.GraphA} {(Vector3)na.position} <-> graph {j.GraphB} {(Vector3)nb.position}");
            }
            return todo.Count;
        }

        private static bool Connected(Join j) =>
            j.NodeA != null && j.NodeB != null && !j.NodeA.Destroyed && !j.NodeB.Destroyed && j.NodeA.ContainsConnection(j.NodeB) && j.NodeB.ContainsConnection(j.NodeA);

        /// <summary>A graph update can rebuild tiles and drop the joins; they are restored every few seconds.</summary>
        private IEnumerator Watch()
        {
            while (_alive)
            {
                float until = Time.realtimeSinceStartup + WatchInterval;
                while (_alive && Time.realtimeSinceStartup < until) yield return null;
                if (!_alive) yield break;
                AstarPath a = AstarPath.active;
                if (a == null)
                {
                    Core.Log.Warning($"vehicle nav: '{_id}' watch ends, AstarPath is gone");
                    yield break;
                }
                int lost = _joins.Count(j => !Connected(j));
                if (lost == 0) continue;
                Core.Log.Warning($"vehicle nav: '{_id}' {lost} of {_joins.Count} graph joins were lost, restoring");
                try { ApplyJoins(a, "restored"); }
                catch (Exception e) { Core.Log.Error($"vehicle nav: '{_id}' restoring the joins failed: {e}"); }
            }
        }

        internal static int UpdateSeekers()
        {
            int n = 0;
            foreach (var agent in Object.FindObjectsOfType<VehicleAgent>(true))
                if (ApplyMask(agent)) n++;
            return n;
        }

        internal static bool ApplyMask(VehicleAgent agent)
        {
            var s = agent != null ? agent.generalSeeker : null;
            if (s == null || ExtraMask == 0) return false;
            var m = s.graphMask;
            m.value |= ExtraMask;
            s.graphMask = m;
            return true;
        }

        /// <summary>
        /// Stuck vehicles are put back onto the nearest node of "Road Nodes". The expansion's own road nodes and nodes
        /// along each connection join that graph, so a vehicle stuck there stays where it is.
        /// </summary>
        private static void AddRoadNodes(AstarPath a, BuiltExpansion b)
        {
            int index = IndexOf(a, RoadNodesGraphName);
            var g = index >= 0 ? a.data.graphs[index]?.TryCast<PointGraph>() : null;
            if (g == null)
            {
                Core.Log.Warning($"vehicle nav: '{b.Id}' no point graph '{RoadNodesGraphName}'; stuck vehicles return to the main world's roads");
                return;
            }
            var points = new List<Vector3>();
            foreach (var p in b.Source.RoadNodes) points.Add(b.Frame.ToWorld(p));
            foreach (var conn in b.Connections)
                for (float d = 0f; d <= conn.Curve.Length; d += 20f)
                    points.Add(conn.Curve.PointAt(d) + Vector3.up * 0.3f);
            int added = 0;
            try
            {
                a.AddWorkItem(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
                {
                    foreach (var p in points)
                        if (g.AddNode((Int3)p) != null) added++;
                })));
                a.FlushWorkItems();
                g.RebuildNodeLookup();
            }
            catch (Exception e)
            {
                Core.Log.Error($"vehicle nav: '{b.Id}' adding road nodes failed after {added}: {e}");
                return;
            }
            Core.Log.Msg($"vehicle nav: '{b.Id}' {added} road nodes added ({b.Source.RoadNodes.Count} from the source, {points.Count - b.Source.RoadNodes.Count} along connections)");
        }

        /// <summary>Path from before the connection to after it, on the vanilla graph plus the added ones.</summary>
        private void CheckPath(BuiltConnection conn)
        {
            var curve = conn.Curve;
            Vector3 s = curve.Points[0], e = curve.Points[curve.Count - 1];
            var path = ABPath.Construct(s, e, null);
            int general = AstarPath.active != null ? IndexOf(AstarPath.active, GeneralGraphName) : -1;
            path.nnConstraint.graphMask = new GraphMask { value = (general >= 0 ? 1 << general : 0) | ExtraMask };
            path.calculatePartial = true;
            AstarPath.StartPath(path);
            path.BlockUntilCalculated();
            int count = path.vectorPath?.Count ?? 0;
            var end = count > 0 ? path.vectorPath[count - 1] : s;
            float miss = Vector3.Distance(end, e);
            var graphs = new List<int>();
            if (path.path != null)
                for (int i = 0; i < path.path.Count; i++)
                {
                    int gi = (int)path.path[i].GraphIndex;
                    if (graphs.Count == 0 || graphs[graphs.Count - 1] != gi) graphs.Add(gi);
                }
            string line = $"vehicle nav: '{_id}' {conn.Name} path check {s} -> {e}: {path.CompleteState}, {count} points, " +
                          $"{(count > 1 ? path.GetTotalLength() : 0f):0} m, ends {miss:0.0} m from the target, graphs {string.Join(">", graphs)}";
            if (path.CompleteState == PathCompleteState.Complete && miss < 5f) Core.Log.Msg(line);
            else Core.Log.Error(line + "; vehicles will not drive through the connection");
        }

        public void Teardown()
        {
            _alive = false;
            if (_watch != null) MelonCoroutines.Stop(_watch);
            _watch = null;
            _joins.Clear();
            ExtraMask = 0;
            Core.Log.Msg($"vehicle nav: '{_id}' released; the added graphs go with the scene");
        }
    }

    /// <summary>Every lookup of the vanilla vehicle graph by name covers the expansion graphs too.</summary>
    [HarmonyPatch(typeof(GraphMask), nameof(GraphMask.FromGraphName))]
    internal static class FromGraphNamePatch
    {
        private static void Postfix(string graphName, ref GraphMask __result)
        {
            if (VehicleNavigation.ExtraMask == 0 || graphName != VehicleNavigation.GeneralGraphName) return;
            __result.value |= VehicleNavigation.ExtraMask;
        }
    }

    /// <summary>A vehicle spawned after the build paths on the expansion graphs too.</summary>
    [HarmonyPatch(typeof(VehicleAgent), nameof(VehicleAgent.Start))]
    internal static class VehicleAgentStartPatch
    {
        private static void Postfix(VehicleAgent __instance)
        {
            try
            {
                VehicleNavigation.ApplyMask(__instance);
            }
            catch (Exception e)
            {
                Core.Log.Warning($"vehicle nav: could not extend the graph mask of '{__instance?.name}': {e.Message}");
            }
        }
    }
}
