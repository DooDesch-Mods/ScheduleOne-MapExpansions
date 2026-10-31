using System.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppPathfinding;
using Il2CppScheduleOne.Vehicles.AI;
using MapExpansions.Connections;
using MapExpansions.Lifecycle;
using MelonLoader;
using UnityEngine;
using AstarPath = Il2Cpp.AstarPath;
using Object = UnityEngine.Object;

namespace MapExpansions.Navigation
{
    /// <summary>
    /// Vehicle navigation for one placed expansion. Vanilla vehicles path on the A* recast graph "General Vehicle
    /// Graph", which ends at the map edge. Up to three graphs are added with its settings: one over the expansion and
    /// the approach to the connection, a clean one over the built stretches, scanned with every other collider
    /// switched off so terrain and rock above or around the tunnel cannot block it, and one over the main world's own
    /// road before a built stretch, scanned from colliders, because the vanilla scan leaves out decks and ramps whose
    /// meshes it cannot read. The graphs are joined to each other and to the vanilla graph by node connections across
    /// the road; a lookup of the vanilla graph by name covers the added ones too.
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

        /// <summary>Per connection, the area node the first exit join reaches; the path check drives to it.</summary>
        private readonly Dictionary<BuiltConnection, Vector3> _exitPoints = new();
        private int _areaGraph = -1, _corridorGraph = -1, _roadGraph = -1;
        private object _watch;
        private bool _alive = true, _scanned;

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

            // The expansion's area plus the part of every connection behind its built range. The stretch before it lies on
            // the main world's roads; scanned into the area graph it would become islands that no join reaches.
            var def = b.Definition;
            Rect local = def.VehicleArea;
            var area = local.size != Vector2.zero ? b.Frame.ToWorld(local) : TerrainArea(b);
            var areaBounds = new Bounds(new Vector3(area.center.x, 0f, area.center.y), new Vector3(area.width, 0f, area.height));
            Bounds? corridor = null, road = null;
            foreach (var conn in b.Connections)
            {
                var range = conn.BuiltRange;
                for (int i = 0; i < conn.Curve.Count; i++)
                {
                    var p = conn.Curve.Points[i];
                    float d = conn.Curve.Distance[i];
                    bool built = range.to > range.from;
                    if (!built || d > range.to) areaBounds.Encapsulate(p);
                    if (built && d < range.from) road = Grow(road, p);
                    if (range.to <= range.from || d < range.from || d > range.to) continue;
                    corridor = Grow(corridor, p);
                }
                if (conn.Definition.VehicleJoin.HasValue && !(range.to > range.from && range.from > 0f))
                    areaBounds.Encapsulate(b.Frame.Resolve(conn.Definition.VehicleJoin.Value));
            }
            areaBounds.Expand(new Vector3(30f, 0f, 30f));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var areaGraph = AddRecast(a, src, $"MapExpansions {b.Id} Vehicle Graph", areaBounds, full: true);
            if (areaGraph == null) yield break;
            _areaGraph = IndexOf(a, areaGraph);
            Scan(a, areaGraph, "area", null, false);
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
                    corridorGraph.characterRadius = Mathf.Min(src.characterRadius, CorridorRadius);
                    _corridorGraph = IndexOf(a, corridorGraph);
                    Scan(a, corridorGraph, "corridor", b, false);
                    Core.Log.Msg($"vehicle nav: '{b.Id}' corridor graph {_corridorGraph} over {cb.min}..{cb.max} scanned in {sw.ElapsedMilliseconds} ms, " +
                                 $"{corridorGraph.CountNodes()} nodes, cell {corridorGraph.cellSize:0.##} m, radius {corridorGraph.characterRadius:0.##} m");
                }
                yield return null;
            }

            if (road != null)
            {
                var r = road.Value;
                float halfWidth = b.Connections.Max(x => x.HalfInnerWidth);
                var rb = new Bounds();
                rb.SetMinMax(r.min - new Vector3(halfWidth + 4f, 2f, halfWidth + 4f), r.max + new Vector3(halfWidth + 4f, 4f, halfWidth + 4f));
                sw.Restart();
                var roadGraph = AddRecast(a, src, $"MapExpansions {b.Id} Road Graph", rb, full: false);
                if (roadGraph != null)
                {
                    _roadGraph = IndexOf(a, roadGraph);
                    Scan(a, roadGraph, "road", null, true);
                    Core.Log.Msg($"vehicle nav: '{b.Id}' road graph {_roadGraph} over {rb.min}..{rb.max} scanned in {sw.ElapsedMilliseconds} ms, " +
                                 $"{roadGraph.CountNodes()} nodes");
                }
                yield return null;
            }

            // Joins: vanilla graph to the road graph (or the area graph) at the join point, into and out of the corridor.
            foreach (var conn in b.Connections)
            {
                var curve = conn.Curve;
                float hw = conn.Curve.MaxRoadWidth * 0.5f;
                var range = conn.BuiltRange;
                // A connection that starts on the main world's own road reaches the corridor over the road graph.
                int approach = _roadGraph >= 0 && range.to > range.from && range.from > 0f ? _roadGraph : _areaGraph;
                if (conn.Definition.VehicleJoin.HasValue)
                {
                    var j = b.Frame.Resolve(conn.Definition.VehicleJoin.Value);
                    float d = curve.Project(j, out _);
                    AddJoins($"{conn.Name} vanilla", general, approach, curve, d, d, hw);
                }
                else Core.Log.Warning($"vehicle nav: '{b.Id}' {conn.Name} has no VehicleJoin; vehicles cannot drive from the main world into it");
                if (_corridorGraph >= 0 && range.to > range.from)
                {
                    AddJoins($"{conn.Name} entry", approach, _corridorGraph, curve, Mathf.Max(0f, range.from - 3f), range.from + 3f, hw);
                    AddExitJoins(a, conn, range.to);
                }
                if (approach == _roadGraph) BridgeGaps(a, conn);
            }
            ApplyJoins(a, "joined");
            _scanned = true;
        }

        /// <summary>Lets the vehicles drive on the scanned graphs. No car routes into the expansion before this call.</summary>
        public void Open(BuiltExpansion b)
        {
            if (!_scanned)
            {
                Core.Log.Warning($"vehicle nav: '{b.Id}' has no scanned graphs to open; vehicles cannot drive into the expansion");
                return;
            }
            ExtraMask = (_areaGraph >= 0 ? 1 << _areaGraph : 0) | (_corridorGraph >= 0 ? 1 << _corridorGraph : 0) | (_roadGraph >= 0 ? 1 << _roadGraph : 0);
            int seekers = UpdateSeekers();
            AddRoadNodes(Astar, b);
            Core.Log.Msg($"vehicle nav: '{b.Id}' graph mask {ExtraMask} added to '{GeneralGraphName}' lookups, {seekers} vehicle seekers updated");

            foreach (var conn in b.Connections) CheckPath(conn);
            _watch = MelonCoroutines.Start(Watch());
        }

        private static Bounds Grow(Bounds? b, Vector3 p)
        {
            if (b == null) return new Bounds(p, Vector3.zero);
            var x = b.Value;
            x.Encapsulate(p);
            return x;
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

        /// <summary>
        /// The vanilla vehicle graph keeps 2.75 m from every edge. A tunnel carriageway between the wall's walkway and
        /// the median is about 6.5 m wide, which leaves one metre of graph, and nothing where the bend narrows it.
        /// Half a car's width plus a margin keeps the lanes in the graph.
        /// </summary>
        private const float CorridorRadius = 1.5f;

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
        /// off for the duration of the scan, which runs in one frame, so no physics step sees the gap. With
        /// <paramref name="readable"/>, every mesh collider whose mesh the scan cannot read gets a readable twin for the
        /// duration of the scan; the vanilla ramps are such colliders.
        /// </summary>
        private static void Scan(AstarPath a, RecastGraph g, string what, BuiltExpansion corridorOf, bool readable)
        {
            var bounds = new Bounds(g.forcedBoundsCenter, g.forcedBoundsSize);
            var off = new List<Collider>();
            if (corridorOf != null)
            {
                foreach (var c in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
                {
                    if (c == null || !c.enabled) continue;
                    if (corridorOf.Connections.Any(x => x.Root != null && c.transform.IsChildOf(x.Root.transform))) continue;
                    c.enabled = false;
                    off.Add(c);
                }
            }
            var twinNames = new List<string>();
            var twins = readable ? AddReadableTwins(bounds, g.mask, what, twinNames) : new List<GameObject>();
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
                foreach (var t in twins)
                {
                    if (t == null) continue;
                    var mc = t.GetComponent<MeshCollider>();
                    var m = mc != null ? mc.sharedMesh : null;
                    Object.DestroyImmediate(t);
                    if (m != null) Object.Destroy(m);
                }
            }
            if (off.Count > 0) Core.Log.Msg($"vehicle nav: {off.Count} foreign colliders were off during the {what} scan and are back on");
            if (twins.Count > 0)
            {
                string names = string.Join(", ", twinNames.GroupBy(x => x).Select(x => x.Count() > 1 ? $"{x.Key} x{x.Count()}" : x.Key));
                Core.Log.Msg($"vehicle nav: {twins.Count} readable collider twins were in the {what} scan and are gone: {names}");
            }
        }

        private static List<GameObject> AddReadableTwins(Bounds bounds, LayerMask mask, string what, List<string> names)
        {
            var twins = new List<GameObject>();
            foreach (var c in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, mask, QueryTriggerInteraction.Ignore))
            {
                var mc = c != null && c.enabled ? c.TryCast<MeshCollider>() : null;
                var mesh = mc != null ? mc.sharedMesh : null;
                if (mesh == null || mesh.isReadable) continue;
                Mesh copy;
                try { copy = World.MeshCarver.ReadableCopy(mesh, mesh.name + " (nav scan)"); }
                catch (Exception e)
                {
                    Core.Log.Warning($"vehicle nav: '{c.name}' stays out of the {what} scan, its mesh could not be read from the GPU: {e.Message}");
                    continue;
                }
                if (copy == null)
                {
                    Core.Log.Warning($"vehicle nav: '{c.name}' stays out of the {what} scan, its mesh has no triangles");
                    continue;
                }
                var t = new GameObject($"MapExpansions nav twin {c.name}") { layer = c.gameObject.layer };
                t.transform.SetParent(c.transform, false);
                var twin = t.AddComponent<MeshCollider>();
                twin.convex = mc.convex;
                twin.sharedMesh = copy;
                twins.Add(t);
                names.Add(c.name);
            }
            return twins;
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

        /// <summary>
        /// Joins the corridor to the area graph out of the built range. The area scan keeps a vehicle's radius away from
        /// the portal's edges, so its cover of the road can start metres past the portal. The area side of each pair is
        /// the first point past the portal, up to <see cref="MaxBridge"/> on and beyond the route's end, that the area
        /// graph covers and that the road reaches without a step.
        /// </summary>
        private void AddExitJoins(AstarPath a, BuiltConnection conn, float portal)
        {
            var curve = conn.Curve;
            int last = curve.Count - 1;
            Vector3 At(float d, int side)
            {
                int k = curve.IndexAt(d);
                var centre = d <= curve.Length ? curve.PointAt(d) : curve.Points[last] + curve.Forward(last) * (d - curve.Length);
                return centre + curve.Right(k) * (side * LaneOffset(curve, k));
            }
            bool median = curve.MedianWidth[curve.IndexAt(portal)] > 0f;
            foreach (int side in median ? new[] { -1, 1 } : new[] { -1, 0, 1 })
            {
                string what = $"{conn.Name} exit {(side == 0 ? "centre" : side < 0 ? "left lane" : "right lane")}";
                var from = At(portal - 3f, side);
                if (!Covers(a, _corridorGraph, from, out var inCorridor))
                    Core.Log.Warning($"vehicle nav: '{_id}' join {what}: the corridor graph has no node at {from}; joined from the nearest one");
                else from = inCorridor;
                bool joined = false;
                string why = "the area graph covers no point";
                for (float d = portal + 3f; d <= portal + MaxBridge; d += 1f)
                {
                    if (!Covers(a, _areaGraph, At(d, side), out var inArea)) continue;
                    if (!Continuous(from, inArea, out why)) continue;
                    _joins.Add(new Join { What = what, GraphA = _corridorGraph, GraphB = _areaGraph, PointA = from, PointB = inArea });
                    Core.Log.Msg($"vehicle nav: '{_id}' join {what} reaches the area graph {d - portal:0} m past the portal at {inArea}");
                    _exitPoints.TryAdd(conn, inArea);
                    joined = true;
                    break;
                }
                if (!joined)
                    Core.Log.Error($"vehicle nav: '{_id}' join {what}: no area node within {MaxBridge:0} m past the portal ({why}); " +
                                   "vehicles cannot leave the corridor there");
            }
        }

        /// <summary>
        /// The scan keeps a vehicle's radius away from every edge, and two colliders that meet flush, such as a ramp and
        /// the deck it runs onto, each get that edge. Along the main world's stretch of a connection, every gap in the
        /// road graph's cover of the route is joined across, where the surface under the route runs on without a step.
        /// Each lane line is checked on its own, and the centre line only where no median runs along it.
        /// </summary>
        private void BridgeGaps(AstarPath a, BuiltConnection conn)
        {
            foreach (int side in new[] { -1, 0, 1 })
                BridgeGaps(a, conn, side);
        }

        private void BridgeGaps(AstarPath a, BuiltConnection conn, int side)
        {
            var curve = conn.Curve;
            float until = conn.BuiltRange.from;
            string line = side == 0 ? "centre" : side < 0 ? "left lane" : "right lane";
            int lastCovered = -1;
            bool inGap = false;
            Vector3 At(int i) => curve.Points[i] + curve.Right(i) * (side * LaneOffset(curve, i));
            for (int i = 0; i < curve.Count && curve.Distance[i] <= until; i++)
            {
                if (side == 0 && curve.MedianWidth[i] > 0f)
                {
                    lastCovered = -1;
                    inGap = false;
                    continue;
                }
                if (!Covers(a, _roadGraph, At(i), out _))
                {
                    inGap = true;
                    continue;
                }
                if (inGap && lastCovered >= 0)
                {
                    Vector3 from = At(lastCovered), to = At(i);
                    float length = curve.Distance[i] - curve.Distance[lastCovered];
                    string what = $"{conn.Name} {line} gap {curve.Distance[lastCovered]:0}..{curve.Distance[i]:0} m";
                    if (length > MaxBridge)
                        Core.Log.Warning($"vehicle nav: '{_id}' {what} is {length:0} m long, more than {MaxBridge:0} m; not bridged");
                    else if (!Continuous(from, to, out string why))
                        Core.Log.Warning($"vehicle nav: '{_id}' {what} not bridged: {why}");
                    else
                        _joins.Add(new Join { What = what, GraphA = _roadGraph, GraphB = _roadGraph, PointA = from, PointB = to });
                }
                lastCovered = i;
                inGap = false;
            }
        }


        /// <summary>The graph has a node on the route point: within half a metre across and a metre and a half up or down.</summary>
        private static bool Covers(AstarPath a, int graph, Vector3 p, out Vector3 at)
        {
            var nn = a.GetNearest(p, new NNConstraint { graphMask = new GraphMask { value = 1 << graph } });
            at = nn.position;
            var d = nn.position - p;
            return nn.node != null && new Vector2(d.x, d.z).magnitude < 0.5f && Mathf.Abs(d.y) < 1.5f;
        }

        private const float MaxBridge = 12f;
        private const float MaxStep = 0.4f;

        /// <summary>The topmost surface under a line, sampled every metre, has no step higher than a kerb.</summary>
        private static bool Continuous(Vector3 from, Vector3 to, out string why)
        {
            int n = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(from, to)));
            float top = Mathf.Max(from.y, to.y) + 4f;
            float? last = null;
            for (int k = 0; k <= n; k++)
            {
                var p = Vector3.Lerp(from, to, k / (float)n);
                if (!Physics.Raycast(new Vector3(p.x, top, p.z), Vector3.down, out var hit, 12f, ~0, QueryTriggerInteraction.Ignore))
                {
                    why = $"no surface under ({p.x:0.0}, {p.z:0.0})";
                    return false;
                }
                if (last.HasValue && Mathf.Abs(hit.point.y - last.Value) > MaxStep)
                {
                    why = $"a step of {Mathf.Abs(hit.point.y - last.Value):0.00} m at ({p.x:0.0}, {p.z:0.0}) on '{hit.collider.name}'";
                    return false;
                }
                last = hit.point.y;
            }
            why = null;
            return true;
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
        /// "Road Nodes" is the road network of vanilla navigation: with roads on, a route enters it at the road link
        /// nearest the start and leaves it at the link nearest the destination, and a route with endAtRoad stops
        /// there. The expansion's road nodes and a chain along each connection join that graph, with a road link for
        /// each edge, so a route into the expansion stays on the road up to the destination. Stuck vehicles are put
        /// back onto the nearest node of this graph too.
        /// </summary>
        private void AddRoadNodes(AstarPath a, BuiltExpansion b)
        {
            int index = IndexOf(a, RoadNodesGraphName);
            var g = index >= 0 ? a.data.graphs[index]?.TryCast<PointGraph>() : null;
            if (g == null)
            {
                Core.Log.Warning($"vehicle nav: '{b.Id}' no point graph '{RoadNodesGraphName}'; vehicles leave the road network at the main world's edge");
                return;
            }
            var sourceNodes = new List<Vector3>();
            foreach (var p in b.Source.RoadNodes) sourceNodes.Add(b.Frame.ToWorld(p));
            var sourceLinks = new List<(int From, int To, bool OneWay)>(b.Source.RoadLinks);
            if (sourceNodes.Count > 1 && sourceLinks.Count == 0)
            {
                Core.Log.Warning($"vehicle nav: '{b.Id}' the source has no road links; its road nodes are chained by distance");
                for (int i = 0; i < sourceNodes.Count; i++)
                {
                    int n = Nearest(sourceNodes, 0, sourceNodes.Count, sourceNodes[i], i);
                    if (n >= 0 && !sourceLinks.Contains((n, i, false))) sourceLinks.Add((i, n, false));
                }
            }
            int oneWay = sourceLinks.Count(e => e.OneWay);
            if (oneWay > 0) Core.Log.Msg($"vehicle nav: '{b.Id}' {oneWay} of {sourceLinks.Count} source road links are one-way");

            // A connection ends at the source road node nearest its last point; the expansion's lanes meet it there.
            var joinAt = new List<int>();
            foreach (var conn in b.Connections)
                joinAt.Add(sourceNodes.Count > 0 ? Nearest(sourceNodes, 0, sourceNodes.Count, conn.Curve.Points[conn.Curve.Count - 1], -1) : -1);
            var roads = SourceRoads.Build(b.Id, sourceNodes, sourceLinks, new HashSet<int>(joinAt.Where(j => j >= 0)), b.Content?.transform);
            var points = new List<Vector3>(roads.Points);
            var edges = new List<(int From, int To, bool OneWay)>();
            foreach (var (from, to) in roads.Edges) edges.Add((from, to, true));
            int sourceCount = points.Count;

            // Vanilla road nodes run per lane and their links one way, with traffic on the right. Each connection gets
            // a lane each way, and its main-world end joins every lane nearby, so a vehicle turns onto it from either
            // direction instead of driving round a block to reach the one nearest node.
            var mainEnds = new List<(int Into, int OutOf, Vector3 At)>();
            int offRoad = 0;
            foreach (var conn in b.Connections)
            {
                var curve = conn.Curve;
                int steps = Mathf.Max(1, Mathf.CeilToInt(curve.Length / RoadLinkStep));
                int there = points.Count;
                for (int i = 0; i <= steps; i++)
                {
                    float d = curve.Length * i / steps;
                    int k = curve.IndexAt(d);
                    points.Add(OnRoad(curve.PointAt(d) + curve.Right(k) * LaneOffset(curve, k), ref offRoad));
                    if (i > 0) edges.Add((points.Count - 2, points.Count - 1, true));
                }
                int back = points.Count;
                for (int i = 0; i <= steps; i++)
                {
                    float d = curve.Length * (steps - i) / steps;
                    int k = curve.IndexAt(d);
                    points.Add(OnRoad(curve.PointAt(d) - curve.Right(k) * LaneOffset(curve, k), ref offRoad));
                    if (i > 0) edges.Add((points.Count - 2, points.Count - 1, true));
                }
                int thereEnd = back - 1, backEnd = points.Count - 1;
                int joint = joinAt[mainEnds.Count];
                if (joint >= 0 && roads.Leave[joint].Count > 0)
                {
                    foreach (var (lane, _) in roads.Leave[joint]) edges.Add((thereEnd, lane, true));
                    foreach (var (lane, _) in roads.Arrive[joint]) edges.Add((lane, back, true));
                }
                else Core.Log.Warning($"vehicle nav: '{b.Id}' {conn.Name} ends with no source road node to join");
                mainEnds.Add((there, backEnd, curve.Points[0]));
            }
            if (offRoad > 0) Core.Log.Warning($"vehicle nav: '{b.Id}' {offRoad} lane nodes found no road under them and keep the route's height");

            // A vanilla link can run tens of metres past the junction with no node near it; the lanes join the links
            // that pass, from the link's start into the expansion and out of it to the link's end.
            var passing = new List<(int End, Vector3 From, Vector3 To, bool OneWay, float Factor)>();
            var factors = new List<float>();
            foreach (var link in NodeLink.validNodeLinks)
            {
                if (link == null || link.end == null) continue;
                factors.Add(link.costFactor);
                for (int m = 0; m < mainEnds.Count; m++)
                    if (Vector3.Distance(link.GetClosestPoint(mainEnds[m].At), mainEnds[m].At) <= MainJoinRadius)
                        passing.Add((m, link.Start.position, link.end.position, link.oneWay, link.costFactor));
            }
            // Vanilla links weigh their length by a cost factor; the added edges use the usual one, or a route compares
            // a cheap vanilla detour with a full-price way into the expansion.
            factors.Sort();
            float factor = factors.Count > 0 ? factors[factors.Count / 2] : 1f;
            Core.Log.Msg($"vehicle nav: '{b.Id}' {factors.Count} main-world road links, cost factor {(factors.Count > 0 ? factors[0] : 1f):0.###}..{(factors.Count > 0 ? factors[^1] : 1f):0.###}, median {factor:0.###}");

            var nodes = new PointNode[points.Count];
            int added = 0, connected = 0, joined = 0, missing = 0;
            try
            {
                a.AddWorkItem(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
                {
                    var linkNodes = new (List<GraphNode> From, List<GraphNode> To)[passing.Count];
                    for (int k = 0; k < passing.Count; k++)
                        linkNodes[k] = (NodesAt(g, passing[k].From), NodesAt(g, passing[k].To));
                    for (int i = 0; i < points.Count; i++)
                        if ((nodes[i] = g.AddNode((Int3)points[i])) != null) added++;
                    foreach (var (from, to, one) in edges)
                        if (Connect(nodes[from], nodes[to], one, factor)) connected++;
                    for (int k = 0; k < passing.Count; k++)
                    {
                        var (m, _, _, one, f) = passing[k];
                        var (from, to) = linkNodes[k];
                        if (from.Count == 0 || to.Count == 0) { missing++; continue; }
                        foreach (var x in from) if (Connect(x, nodes[mainEnds[m].Into], true, f)) joined++;
                        foreach (var x in to) if (Connect(nodes[mainEnds[m].OutOf], x, true, f)) joined++;
                        if (one) continue;
                        foreach (var x in to) if (Connect(x, nodes[mainEnds[m].Into], true, f)) joined++;
                        foreach (var x in from) if (Connect(nodes[mainEnds[m].OutOf], x, true, f)) joined++;
                    }
                })));
                a.FlushWorkItems();
                g.RebuildNodeLookup();
            }
            catch (Exception e)
            {
                Core.Log.Error($"vehicle nav: '{b.Id}' adding road nodes failed after {added} nodes and {connected} edges: {e}");
                return;
            }

            _linkRoot = new GameObject($"MapExpansions road links {b.Id}");
            var ends = new Transform[points.Count];
            Transform At(int i)
            {
                if (ends[i] != null) return ends[i];
                var t = new GameObject($"road node {i}").transform;
                t.SetParent(_linkRoot.transform, false);
                t.position = points[i];
                return ends[i] = t;
            }
            void Link(Transform from, Transform to)
            {
                var link = from.gameObject.AddComponent<NodeLink>();
                // Disabled, so a later graph update does not apply it: Apply joins the nearest nodes of every graph.
                // The edge is already in the graph; the link only marks where a route enters and leaves the road.
                link.enabled = false;
                link.end = to;
                link.midPosition = (from.position + to.position) / 2f;
                NodeLink.validNodeLinks.Add(link);
                _links.Add(link);
            }
            // An edge under a metre joins lanes that already mark the road there; a link that short would read as a turn.
            foreach (var (from, to, _) in edges)
                if (Vector3.Distance(points[from], points[to]) >= 1f) Link(At(from), At(to));
            Transform Main(Vector3 at)
            {
                var t = new GameObject("main road node").transform;
                t.SetParent(_linkRoot.transform, false);
                t.position = at;
                return t;
            }
            foreach (var (m, from, to, _, _) in passing)
            {
                Link(Main(from), At(mainEnds[m].Into));
                Link(At(mainEnds[m].OutOf), Main(to));
            }
            Core.Log.Msg($"vehicle nav: '{b.Id}' {added} road nodes added ({sourceCount} in lanes along the expansion's roads, {points.Count - sourceCount} in two lanes along connections), " +
                         $"{connected} of {edges.Count} edges connected, {joined} edges to {passing.Count} main-world road links within {MainJoinRadius:0} m, {_links.Count} road links");
            foreach (var (m, from, to, one, _) in passing)
                Core.Log.Msg($"vehicle nav: '{b.Id}' connection {m} joins the main-world road link {from} -> {to}{(one ? " (one way)" : "")}");
            if (missing > 0) Core.Log.Warning($"vehicle nav: '{b.Id}' {missing} main-world road links have no road node at an end and are not joined");
            if (passing.Count == 0)
                Core.Log.Warning($"vehicle nav: '{b.Id}' no main-world road link within {MainJoinRadius:0} m of a connection's start; routes cannot enter it by road");
        }

        private static bool Connect(GraphNode x, GraphNode y, bool oneWay, float factor)
        {
            if (x == null || y == null || x == y) return false;
            uint cost = (uint)Math.Round((x.position - y.position).costMagnitude * factor);
            x.AddConnection(y, cost);
            if (!oneWay) y.AddConnection(x, cost);
            return true;
        }

        /// <summary>The nodes of the point graph on a road link's end, within half a metre; lanes can share a spot.</summary>
        private static List<GraphNode> NodesAt(PointGraph g, Vector3 p)
        {
            var list = new List<GraphNode>();
            for (int i = 0; i < g.nodeCount; i++)
            {
                var n = g.nodes[i];
                if (n != null && ((Vector3)n.position - p).sqrMagnitude < 0.25f) list.Add(n);
            }
            return list;
        }

        private static int Nearest(List<Vector3> points, int from, int to, Vector3 p, int skip)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = from; i < to; i++)
            {
                if (i == skip) continue;
                float d = (points[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private const float RoadLinkStep = 10f;

        /// <summary>
        /// Distance from the route to the lane the graph uses at sample k: the outer lane of each carriageway, as traffic
        /// keeps right. A carriageway holds its width over <see cref="LaneWidth"/> lanes, rounded, at least one.
        /// </summary>
        private static float LaneOffset(RouteCurve curve, int k)
        {
            float median = curve.MedianWidth[k], carriageway = (curve.RoadWidth[k] - median) * 0.5f;
            int lanes = Mathf.Max(1, Mathf.RoundToInt(carriageway / LaneWidth));
            return median * 0.5f + carriageway - carriageway / lanes * 0.5f;
        }

        private const float LaneWidth = 3.5f;

        /// <summary>Height of a lane node over the road, as the vanilla road nodes lie.</summary>
        private const float RoadNodeHeight = 0.05f;

        /// <summary>
        /// The point moved onto the road under it. Kinematic driving follows the path by its closest point to the car's
        /// underside; a path that floats over a slope pulls that point back down the slope by more than the car moves on
        /// in a step, and the car stands still.
        /// </summary>
        private static Vector3 OnRoad(Vector3 p, ref int missed)
        {
            int mask = LayerMask.GetMask("Default", "Terrain");
            float best = float.MaxValue;
            Vector3? ground = null;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 2.5f, Vector3.down, 6f, mask, QueryTriggerInteraction.Ignore))
            {
                if (h.distance >= best) continue;
                best = h.distance;
                ground = h.point;
            }
            if (ground.HasValue) return ground.Value + Vector3.up * RoadNodeHeight;
            missed++;
            return p + Vector3.up * 0.3f;
        }
        private const float MainJoinRadius = 10f;
        private readonly List<NodeLink> _links = new();
        private GameObject _linkRoot;

        /// <summary>Path from before the connection to after it, on the vanilla graph plus the added ones.</summary>
        private void CheckPath(BuiltConnection conn)
        {
            var curve = conn.Curve;
            // The route's end can lie in the strip past the portal that the area graph leaves bare; the check drives on
            // to the area node the exit join reaches, so a path that stops in the corridor fails it.
            Vector3 s = curve.Points[0], e = _exitPoints.TryGetValue(conn, out var exit) ? exit : curve.Points[curve.Count - 1];
            var path = ABPath.Construct(s, e, null);
            int general = AstarPath.active != null ? IndexOf(AstarPath.active, GeneralGraphName) : -1;
            path.nnConstraint.graphMask = new GraphMask { value = (general >= 0 ? 1 << general : 0) | ExtraMask };
            // No partial path: this A* version leaves the target node flagged after one, and the next search that
            // reaches that node throws and ends the pathfinding threads for every vehicle.
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
                          $"{(count > 1 ? path.GetTotalLength() : 0f):0} m, ends {miss:0.0} m from the target, graphs {string.Join(">", graphs)}" +
                          (path.error ? $", '{path.errorLog}'" : "");
            if (path.CompleteState == PathCompleteState.Complete && miss < 5f) Core.Log.Msg(line);
            else Core.Log.Error(line + "; vehicles will not drive through the connection");
        }

        public void Teardown()
        {
            _alive = false;
            if (_watch != null) MelonCoroutines.Stop(_watch);
            _watch = null;
            foreach (var link in _links) NodeLink.validNodeLinks.Remove(link);
            _links.Clear();
            if (_linkRoot != null) Object.Destroy(_linkRoot);
            _linkRoot = null;
            _joins.Clear();
            _exitPoints.Clear();
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
