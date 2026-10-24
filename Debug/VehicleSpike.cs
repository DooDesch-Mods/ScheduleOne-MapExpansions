#if DEBUG
using System.Collections;
using System.Globalization;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppPathfinding;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;
using AstarPath = Il2Cpp.AstarPath;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Vehicle AI spike commands, all under <c>mxveh</c>:
    ///
    /// mxveh info                              A* version, graphs, node counts, nearest General node for probes
    /// mxveh near x y z                        nearest node per graph for one point
    /// mxveh grid cx cz w d [node]             add and scan a grid graph for vehicles over the expansion
    /// mxveh connect ax ay az bx by bz         join the nearest General node at A to the nearest grid node at B
    /// mxveh path ax ay az bx by bz [mask]    A* path between two points, default mask General plus grid
    /// mxveh rehier                         recalculate the connected areas of all graphs
    /// mxveh mask on|off                       treat the grid graph as part of "General Vehicle Graph"
    /// mxveh spawn x y z [heading] [code]      spawn an NPC vehicle
    /// mxveh drive x y z [noroads]             navigate the last spawned vehicle there and watch it
    /// </summary>
    internal static class VehicleSpike
    {
        public const string GeneralName = "General Vehicle Graph";
        public const string GridName = "MapExpansions Vehicle Graph";
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        internal static bool MaskOn;
        internal static int GridIndex = -1;
        private static LandVehicle _vehicle;

        private static float F(string s) => float.Parse(s, Ci);
        private static GraphMask Bit(int index) => new GraphMask { value = 1 << index };
        private static Vector3 V(string[] p, int i) => new(F(p[i]), F(p[i + 1]), F(p[i + 2]));

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "info": Info(); break;
                case "near" when parts.Length > 4: Near(V(parts, 2)); break;
                case "grid" when parts.Length > 5:
                    Grid(F(parts[2]), F(parts[3]), F(parts[4]), F(parts[5]), parts.Length > 6 ? F(parts[6]) : 2f);
                    break;
                case "connect" when parts.Length > 7: Connect(V(parts, 2), V(parts, 5)); break;
                case "path" when parts.Length > 7:
                    PathTest(V(parts, 2), V(parts, 5), parts.Length > 8 ? int.Parse(parts[8], Ci) : -1);
                    break;
                case "rehier":
                    Astar.hierarchicalGraph.RecalculateAll();
                    Core.Log.Msg("[dev] mxveh rehier: hierarchical graph recalculated");
                    break;
                case "mask" when parts.Length > 2: Mask(parts[2] == "on"); break;
                case "spawn" when parts.Length > 4:
                    Spawn(V(parts, 2), parts.Length > 5 ? F(parts[5]) : 0f, parts.Length > 6 ? parts[6] : "shitbox");
                    break;
                case "drive" when parts.Length > 4: Drive(V(parts, 2), parts.Length > 5 && parts[5] == "noroads"); break;
                default:
                    Core.Log.Warning("[dev] mxveh info|near|grid|connect|mask|spawn|drive (see VehicleSpike docs)");
                    break;
            }
        }

        private static AstarPath Astar
        {
            get
            {
                var a = AstarPath.active;
                if (a == null) throw new InvalidOperationException("AstarPath.active is null");
                return a;
            }
        }

        private static void Info()
        {
            var a = Astar;
            Core.Log.Msg($"[dev] mxveh info: A* {AstarPath.Version}, {a.data.graphs.Length} graphs");
            for (int i = 0; i < a.data.graphs.Length; i++)
            {
                var g = a.data.graphs[i];
                if (g == null) { Core.Log.Msg($"[dev]   graph {i}: null"); continue; }
                int nodes = g.CountNodes();
                Core.Log.Msg($"[dev]   graph {i}: '{g.name}' {g.GetIl2CppType().Name} nodes={nodes}");
            }
            foreach (var p in new[] { new Vector3(0, 0, 0), new Vector3(-13.5f, 0, -173f), new Vector3(0, 0, -250f), new Vector3(0, 0, -450f) })
                Near(p);
        }

        private static void Near(Vector3 p)
        {
            var a = Astar;
            for (int i = 0; i < a.data.graphs.Length; i++)
            {
                var g = a.data.graphs[i];
                if (g == null) continue;
                var c = new NNConstraint { graphMask = Bit(i) };
                var nn = a.GetNearest(p, c);
                string where = nn.node == null ? "no node" : $"{nn.position} ({Vector3.Distance(p, nn.position):0.0} m) area {nn.node.Area} walkable {nn.node.Walkable}";
                Core.Log.Msg($"[dev] mxveh near {p} graph {i} '{g.name}': {where}");
            }
        }

        private static void Grid(float cx, float cz, float width, float depth, float node)
        {
            var a = Astar;
            if (GridIndex >= 0)
            {
                Core.Log.Warning("[dev] mxveh grid: a grid graph is already added; restart the session to rebuild it");
                return;
            }
            var t = System.Diagnostics.Stopwatch.StartNew();
            var g = a.data.AddGraph(Il2CppType.Of<GridGraph>()).TryCast<GridGraph>();
            if (g == null)
            {
                Core.Log.Error("[dev] mxveh grid: AddGraph did not return a GridGraph");
                return;
            }
            g.name = GridName;
            g.center = new Vector3(cx, 0f, cz);
            g.SetDimensions(Mathf.RoundToInt(width / node), Mathf.RoundToInt(depth / node), node);
            g.maxClimb = 1.5f;
            g.maxSlope = 35f;
            g.erodeIterations = 1;
            g.collision.heightCheck = true;
            g.collision.fromHeight = 200f;
            g.collision.heightMask = ~0 & ~(1 << LayerMask.NameToLayer("Ignore Raycast"));
            g.collision.collisionCheck = false;
            for (int i = 0; i < a.data.graphs.Length; i++)
                if (a.data.graphs[i] != null && a.data.graphs[i].Pointer == g.Pointer) GridIndex = i;
            long addMs = t.ElapsedMilliseconds;
            t.Restart();
            a.Scan(g);
            int nodes = g.CountNodes();
            int walkable = 0;
            var arr = g.nodes;
            if (arr != null)
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && arr[i].Walkable) walkable++;
            Core.Log.Msg($"[dev] mxveh grid: '{GridName}' index {GridIndex}, {g.width}x{g.depth} @ {node} m around ({cx}, {cz}); " +
                         $"add {addMs} ms, scan {t.ElapsedMilliseconds} ms, nodes {nodes}, walkable {walkable}");
        }

        private static void Connect(Vector3 a, Vector3 b)
        {
            if (GridIndex < 0)
            {
                Core.Log.Warning("[dev] mxveh connect: no grid graph yet");
                return;
            }
            var astar = Astar;
            var na = astar.GetNearest(a, new NNConstraint { graphMask = Bit(0) });
            var nb = astar.GetNearest(b, new NNConstraint { graphMask = Bit(GridIndex) });
            if (na.node == null || nb.node == null)
            {
                Core.Log.Error($"[dev] mxveh connect: General node {(na.node != null)}, grid node {(nb.node != null)}");
                return;
            }
            var nodeA = na.node;
            var nodeB = nb.node;
            uint cost = (uint)(Vector3.Distance((Vector3)nodeA.position, (Vector3)nodeB.position) * Int3.Precision);
            astar.AddWorkItem(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                nodeA.AddConnection(nodeB, cost);
                nodeB.AddConnection(nodeA, cost);
            })));
            astar.FlushWorkItems();
            Core.Log.Msg($"[dev] mxveh connect: General {(Vector3)nodeA.position} <-> grid {(Vector3)nodeB.position}, cost {cost}");
        }

        private static void PathTest(Vector3 a, Vector3 b, int mask)
        {
            if (mask < 0) mask = 1 | (GridIndex >= 0 ? 1 << GridIndex : 0);
            var t = System.Diagnostics.Stopwatch.StartNew();
            var path = ABPath.Construct(a, b, null);
            path.nnConstraint.graphMask = new GraphMask { value = mask };
            AstarPath.StartPath(path);
            path.BlockUntilCalculated();
            int count = path.vectorPath?.Count ?? 0;
            string end = count > 0 ? path.vectorPath[count - 1].ToString() : "-";
            var areas = new List<string>();
            var graphs = new List<string>();
            if (path.path != null)
                for (int i = 0; i < path.path.Count; i++)
                {
                    var n = path.path[i];
                    string g = n.GraphIndex.ToString(Ci);
                    if (graphs.Count == 0 || graphs[graphs.Count - 1] != g) graphs.Add(g);
                    string ar = n.Area.ToString(Ci);
                    if (areas.Count == 0 || areas[areas.Count - 1] != ar) areas.Add(ar);
                }
            Core.Log.Msg($"[dev] mxveh path {a} -> {b} mask {mask}: state {path.CompleteState}, error {path.error} '{path.errorLog}', " +
                         $"{count} points, length {(count > 1 ? path.GetTotalLength() : 0f):0} m, end {end}, graphs {string.Join(">", graphs)}, " +
                         $"areas {string.Join(">", areas)}, {t.ElapsedMilliseconds} ms");
        }

        private static void Mask(bool on)
        {
            MaskOn = on;
            int seekers = 0;
            foreach (var agent in Object.FindObjectsOfType<VehicleAgent>())
            {
                var s = agent.generalSeeker;
                if (s == null) continue;
                var m = s.graphMask;
                m.value = on && GridIndex >= 0 ? (m.value | (1 << GridIndex)) : (m.value & ~(GridIndex >= 0 ? 1 << GridIndex : 0));
                s.graphMask = m;
                seekers++;
            }
            Core.Log.Msg($"[dev] mxveh mask {(on ? "on" : "off")}: {seekers} general seekers updated, grid index {GridIndex}");
        }

        private static void Spawn(Vector3 pos, float heading, string code)
        {
            var vm = NetworkSingleton<VehicleManager>.Instance;
            if (vm == null)
            {
                Core.Log.Error("[dev] mxveh spawn: no VehicleManager");
                return;
            }
            _vehicle = vm.SpawnAndReturnVehicle(code, pos, Quaternion.Euler(0f, heading, 0f), false);
            if (_vehicle == null)
            {
                Core.Log.Error($"[dev] mxveh spawn: '{code}' did not spawn");
                return;
            }
            if (MaskOn) Mask(true);
            Core.Log.Msg($"[dev] mxveh spawn: {code} at {pos}, agent {(_vehicle.GetComponentInChildren<VehicleAgent>() != null)}");
        }

        private static void Drive(Vector3 target, bool noRoads)
        {
            if (_vehicle == null)
            {
                Core.Log.Warning("[dev] mxveh drive: spawn a vehicle first");
                return;
            }
            var agent = _vehicle.GetComponentInChildren<VehicleAgent>();
            if (agent == null)
            {
                Core.Log.Error("[dev] mxveh drive: vehicle has no VehicleAgent");
                return;
            }
            if (noRoads) agent.Flags.UseRoads = false;
            var settings = new NavigationSettings { ensureProximityToGraph = false };
            string result = null;
            var cb = DelegateSupport.ConvertDelegate<VehicleAgent.NavigationCallback>(
                new Action<VehicleAgent.ENavigationResult>(r =>
                {
                    result = r.ToString();
                    Core.Log.Msg($"[dev] mxveh drive: navigation callback {r}");
                }));
            Core.Log.Msg($"[dev] mxveh drive: from {_vehicle.transform.position} to {target}, roads {(!noRoads)}, onGraph {agent.IsOnVehicleGraph()}");
            agent.Navigate(target, settings, cb);
            MelonCoroutines.Start(Watch(agent, target, () => result));
        }

        private static IEnumerator Watch(VehicleAgent agent, Vector3 target, Func<string> result)
        {
            var tr = agent.transform;
            Vector3 last = tr.position;
            float start = Time.time, lastLog = 0f, travelled = 0f;
            int jumps = 0;
            while (agent != null && Time.time - start < 300f)
            {
                yield return null;
                if (agent == null) break;
                Vector3 p = tr.position;
                float step = Vector3.Distance(p, last);
                if (step > 8f)
                {
                    jumps++;
                    Core.Log.Warning($"[dev] mxveh drive: vehicle jumped {step:0.0} m in one frame {last} -> {p}");
                }
                else travelled += step;
                last = p;
                if (Time.time - lastLog > 3f)
                {
                    lastLog = Time.time;
                    Core.Log.Msg($"[dev] mxveh drive: at {p}, {Vector3.Distance(p, target):0} m left, autoDriving={agent.AutoDriving}, speed {_vehicle?.Speed_Kmh:0} km/h");
                }
                if (result() != null && !agent.AutoDriving) break;
            }
            Core.Log.Msg($"[dev] mxveh drive: watch ended after {Time.time - start:0} s, {travelled:0} m driven, {jumps} jumps, " +
                         $"result {result() ?? "none"}, {Vector3.Distance(tr.position, target):0} m from target");
        }
    }

    /// <summary>
    /// While the mask is on, every lookup of "General Vehicle Graph" also covers the expansion grid. The vehicle code
    /// samples the graph by name for the distance gate, the destination snap and the teleporter.
    /// </summary>
    [HarmonyPatch(typeof(GraphMask), nameof(GraphMask.FromGraphName))]
    internal static class FromGraphNamePatch
    {
        private static void Postfix(string graphName, ref GraphMask __result)
        {
            if (!VehicleSpike.MaskOn || VehicleSpike.GridIndex < 0 || graphName != VehicleSpike.GeneralName) return;
            __result.value |= 1 << VehicleSpike.GridIndex;
        }
    }
}
#endif
