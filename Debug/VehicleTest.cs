#if DEBUG
using System.Collections;
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppPathfinding;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Vehicles;
using Il2CppScheduleOne.Vehicles.AI;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;
using MapExpansions.Navigation;
using static MapExpansions.Debug.DevConsole;
using AstarPath = Il2Cpp.AstarPath;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Vehicle navigation tests, all under <c>mxveh</c>:
    ///
    /// mxveh info                              A* graphs and node counts, nearest node per graph for the player
    /// mxveh near x y z                        nearest node per graph for one point
    /// mxveh path ax ay az bx by bz [mask]    A* path between two points, default mask General plus the expansion graphs
    /// mxveh spawn x y z [heading] [code]      spawn an NPC vehicle
    /// mxveh drive x y z [noroads]             navigate the last spawned vehicle there and watch it
    /// mxveh calc ax ay az bx by bz            the road route steps of vanilla navigation, each logged, with the last vehicle's seekers
    /// </summary>
    internal static class VehicleTest
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static LandVehicle _vehicle;

        private static GraphMask Bit(int index) => new GraphMask { value = 1 << index };

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "info": Info(); break;
                case "near" when parts.Length > 4: Near(V(parts, 2)); break;
                case "path" when parts.Length > 7:
                    PathTest(V(parts, 2), V(parts, 5), parts.Length > 8 ? int.Parse(parts[8], Ci) : -1);
                    break;
                case "spawn" when parts.Length > 4:
                    Spawn(V(parts, 2), parts.Length > 5 ? F(parts[5]) : 0f, parts.Length > 6 ? parts[6] : "shitbox");
                    break;
                case "calc" when parts.Length > 7: Calc(V(parts, 2), V(parts, 5)); break;
                case "drive" when parts.Length > 4: Drive(V(parts, 2), parts.Length > 5 && parts[5] == "noroads"); break;
                default:
                    Core.Log.Warning("[dev] mxveh info|near|path|spawn|drive|calc (see VehicleTest docs)");
                    break;
            }
        }

        private static AstarPath Astar => AstarPath.active ?? throw new InvalidOperationException("AstarPath.active is null");

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
            if (Il2CppScheduleOne.PlayerScripts.Player.Local != null) Near(Il2CppScheduleOne.PlayerScripts.Player.Local.transform.position);
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

        private static void PathTest(Vector3 a, Vector3 b, int mask)
        {
            if (mask < 0) mask = 1 | VehicleNavigation.ExtraMask;
            var t = System.Diagnostics.Stopwatch.StartNew();
            var path = ABPath.Construct(a, b, null);
            path.nnConstraint.graphMask = new GraphMask { value = mask };
            // No partial path: this A* version leaves the target node flagged after one, which ends the path threads.
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

        /// <summary>
        /// The steps vanilla navigation takes with roads on: general graph to the road link nearest the start, road
        /// nodes to the link nearest the destination, general graph on to the destination. Vanilla reports only
        /// "Failed" for the whole route; this logs each step.
        /// </summary>
        private static void Calc(Vector3 start, Vector3 dest)
        {
            var agent = _vehicle != null ? _vehicle.GetComponentInChildren<VehicleAgent>() : null;
            if (agent == null)
            {
                Core.Log.Warning("[dev] mxveh calc: spawn a vehicle first, its seekers are used");
                return;
            }
            var gs = agent.generalSeeker;
            var rs = agent.roadSeeker;
            Core.Log.Msg($"[dev] mxveh calc: general seeker mask {gs.graphMask.value} tags {gs.traversableTags}, road seeker mask {rs.graphMask.value} " +
                         $"tags {rs.traversableTags}, FromGraphName {GraphMask.FromGraphName(VehicleNavigation.GeneralGraphName).value}");
            var startOnGraph = AstarUtility.GetClosestPointOnGraph(start, VehicleNavigation.GeneralGraphName);
            var destOnGraph = AstarUtility.GetClosestPointOnGraph(dest, VehicleNavigation.GeneralGraphName);
            Core.Log.Msg($"[dev] mxveh calc: start on graph {startOnGraph}, destination on graph {destOnGraph}");
            var entries = NodeLink.GetClosestLinks(start, 3);
            var exits = NodeLink.GetClosestLinks(dest, 3);
            if (entries == null || entries.Count == 0 || exits == null || exits.Count == 0)
            {
                Core.Log.Error($"[dev] mxveh calc: road links near start {entries?.Count ?? 0}, near destination {exits?.Count ?? 0}");
                return;
            }
            for (int i = 0; i < exits.Count; i++)
                Core.Log.Msg($"[dev] mxveh calc: exit link {i} '{exits[i].name}' start {exits[i].Start.position}, closest point {exits[i].GetClosestPoint(dest)}");
            Vector3 entryPoint = entries[0].GetClosestPoint(start);
            var exitLink = exits[0];
            Vector3 exitPoint = exitLink.GetClosestPoint(dest);
            Step("start to entry", startOnGraph, entryPoint, gs);
            Step("road", NodeLink.GetClosestLinks(entryPoint, 1)[0].Start.position, exitLink.Start.position, rs);
            Step("exit to destination", exitPoint, destOnGraph, gs);
            Step("direct", start, dest, gs);
        }

        private static void Step(string what, Vector3 a, Vector3 b, Seeker seeker)
        {
            var path = ABPath.Construct(a, b, null);
            path.nnConstraint.graphMask = seeker.graphMask;
            path.enabledTags = seeker.traversableTags;
            AstarPath.StartPath(path);
            path.BlockUntilCalculated();
            int count = path.vectorPath?.Count ?? 0;
            var graphs = new List<string>();
            if (path.path != null)
                for (int i = 0; i < path.path.Count; i++)
                {
                    string g = path.path[i].GraphIndex.ToString(Ci);
                    if (graphs.Count == 0 || graphs[graphs.Count - 1] != g) graphs.Add(g);
                }
            string line = $"[dev] mxveh calc {what}: {a} -> {b}: {path.CompleteState}, {count} points, {(count > 1 ? path.GetTotalLength() : 0f):0} m, " +
                          $"end {(count > 0 ? path.vectorPath[count - 1].ToString() : "-")}, graphs {string.Join(">", graphs)}" + (path.error ? $", '{path.errorLog}'" : "");
            if (path.error) Core.Log.Error(line);
            else Core.Log.Msg(line);
            if (what == "road" && count > 1)
            {
                var points = new List<string>();
                for (int i = 0; i < count; i++) points.Add($"({path.vectorPath[i].x:0},{path.vectorPath[i].z:0})");
                Core.Log.Msg($"[dev] mxveh calc road points: {string.Join(" ", points)}");
            }
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
            if (_vehicle.GetComponentInChildren<VehicleAgent>() is { } agent) VehicleNavigation.ApplyMask(agent);
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
}
#endif
