#if DEBUG
using System.Collections;
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppPathfinding;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Math;
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
    /// mxveh push vx vy vz                     give the last spawned vehicle this velocity and log where it goes for 2 s
    /// mxveh gas throttle [seconds]            hold the last spawned vehicle's throttle and log speed and pitch each second
    /// mxveh wall box|blocker|mesh x y z yaw   a 0.5 x 2 x 6 m test wall made at runtime: box on Default, box on VehicleBlocker, mesh on Default
    /// mxveh roads [x0 z0 x1 z1]               road links in a rectangle (default Serena Flats) with the ground gap at each end
    /// mxveh traffic [x0 z0 x1 z1]             every vehicle agent in a rectangle: mode, ground gap, nearest road link and side
    /// mxveh clear                             destroy the vehicles and test walls these commands made
    /// </summary>
    internal static class VehicleTest
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static LandVehicle _vehicle;
        private static readonly List<LandVehicle> Spawned = new();
        private static readonly List<GameObject> Walls = new();

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
                case "push" when parts.Length > 4: Push(V(parts, 2)); break;
                case "gas" when parts.Length > 2: MelonCoroutines.Start(Gas(F(parts[2]), parts.Length > 3 ? F(parts[3]) : 20f)); break;
                case "wall" when parts.Length > 6: Wall(parts[2], V(parts, 3), F(parts[6])); break;
                case "roads": Roads(Area(parts)); break;
                case "traffic": Traffic(Area(parts)); break;
                case "clear": Clear(); break;
                default:
                    Core.Log.Warning("[dev] mxveh info|near|path|spawn|drive|calc|push|gas|wall|roads|traffic|clear (see VehicleTest docs)");
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
            Spawned.Add(_vehicle);
            if (_vehicle.GetComponentInChildren<VehicleAgent>() is { } agent) VehicleNavigation.ApplyMask(agent);
            Core.Log.Msg($"[dev] mxveh spawn: {code} at {pos}, agent {(_vehicle.GetComponentInChildren<VehicleAgent>() != null)}");
        }

        private static void Wall(string kind, Vector3 pos, float yaw)
        {
            if (kind.StartsWith("copy:", StringComparison.Ordinal))
            {
                Copy(kind.Substring(5), pos, yaw);
                return;
            }
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"MapExpansions.TestWall.{kind}.{Walls.Count}";
            go.transform.SetPositionAndRotation(pos + Vector3.up, Quaternion.Euler(0f, yaw, 0f));
            go.transform.localScale = new Vector3(0.5f, 2f, 6f);
            switch (kind)
            {
                case "box": break;
                case "blocker": go.layer = LayerMask.NameToLayer("VehicleBlocker"); break;
                case "mesh":
                    Object.DestroyImmediate(go.GetComponent<BoxCollider>());
                    var src = go.GetComponent<MeshFilter>().sharedMesh;
                    var copy = new Mesh { name = go.name };
                    copy.SetVertices(src.vertices);
                    copy.SetTriangles(src.triangles, 0);
                    copy.RecalculateBounds();
                    go.AddComponent<MeshCollider>().sharedMesh = copy;
                    break;
                default:
                    Core.Log.Warning($"[dev] mxveh wall: '{kind}' is not box, blocker or mesh");
                    Object.Destroy(go);
                    return;
            }
            Walls.Add(go);
            Core.Log.Msg($"[dev] mxveh wall: {go.name} at {go.transform.position}, yaw {yaw}, layer {LayerMask.LayerToName(go.layer)}, collider {go.GetComponent<Collider>().GetIl2CppType().Name}");
        }

        /// <summary>
        /// A copy of a named object with its colliders, without parent, at pos and yaw. Tells whether an object stops cars
        /// because of what it is or because of where it stands.
        /// </summary>
        private static void Copy(string name, Vector3 pos, float yaw)
        {
            var src = GameObject.Find(name);
            if (src == null)
            {
                Core.Log.Warning($"[dev] mxveh wall copy: no active object '{name}'");
                return;
            }
            var go = Object.Instantiate(src);
            go.name = $"MapExpansions.TestWall.copy.{Walls.Count}";
            go.transform.SetParent(null, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            go.transform.localScale = src.transform.lossyScale;
            Walls.Add(go);
            var cols = go.GetComponentsInChildren<Collider>();
            Core.Log.Msg($"[dev] mxveh wall copy: '{name}' (at {src.transform.position}, scale {src.transform.lossyScale}, scene {src.scene.name}) -> {go.name} at {pos}, " +
                         $"scene {go.scene.name}, colliders {string.Join(", ", cols.Select(c => $"{c.GetIl2CppType().Name}@{LayerMask.LayerToName(c.gameObject.layer)} {c.bounds.size}"))}");
        }

        /// <summary>Serena Flats in world space, unless the command names a rectangle.</summary>
        private static Rect Area(string[] parts) =>
            parts.Length > 5 ? Rect.MinMaxRect(F(parts[2]), F(parts[3]), F(parts[4]), F(parts[5])) : Rect.MinMaxRect(-135f, -935f, 495f, -335f);

        private static int GroundMask => LayerMask.GetMask("Default") | LayerMask.GetMask("Terrain");

        /// <summary>Height above the first ground under a point (Default or Terrain, from 3 m above it), NaN when none is within 40 m.</summary>
        private static float Gap(Vector3 p, Transform skip = null)
        {
            var hits = Physics.RaycastAll(p + Vector3.up * 3f, Vector3.down, 43f, GroundMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue, gap = float.NaN;
            foreach (var h in hits)
            {
                if (skip != null && h.collider.transform.IsChildOf(skip)) continue;
                if (h.distance < nearest) { nearest = h.distance; gap = p.y - h.point.y; }
            }
            return gap;
        }

        private static string P(Vector3 p) => $"({p.x:0.0},{p.y:0.0},{p.z:0.0})";

        private static bool In(Rect r, Vector3 p) => r.Contains(new Vector2(p.x, p.z));

        private static void Roads(Rect r)
        {
            int n = 0;
            foreach (var link in NodeLink.validNodeLinks)
            {
                if (link == null || link.end == null) continue;
                Vector3 a = link.Start.position, b = link.end.position;
                if (!In(r, a) && !In(r, b)) continue;
                n++;
                string owner = link.transform.parent != null ? link.transform.parent.name : "-";
                Core.Log.Msg($"[dev] mxveh roads {n}: {P(a)} -> {P(b)} {Vector3.Distance(a, b):0.0} m{(link.oneWay ? " one-way" : "")}" +
                             $"{(link.enabled ? "" : " disabled")} gap {Gap(a):0.00}/{Gap(b):0.00} under '{owner}'");
            }
            Core.Log.Msg($"[dev] mxveh roads: {n} road links in {r}");
        }

        private static void Traffic(Rect r)
        {
            int n = 0;
            foreach (var agent in Object.FindObjectsOfType<VehicleAgent>())
            {
                var v = agent.GetComponentInParent<LandVehicle>();
                if (v == null || !In(r, v.transform.position)) continue;
                n++;
                Vector3 pos = v.transform.position;
                NodeLink best = null;
                float bestD = float.MaxValue;
                foreach (var link in NodeLink.validNodeLinks)
                {
                    if (link == null || link.end == null) continue;
                    float d = Vector3.Distance(link.GetClosestPoint(pos), pos);
                    if (d < bestD) { bestD = d; best = link; }
                }
                string road = "no road link";
                if (best != null)
                {
                    Vector3 dir = best.end.position - best.Start.position;
                    dir.y = 0f;
                    dir.Normalize();
                    Vector3 off = pos - best.GetClosestPoint(pos);
                    float side = Vector3.Dot(off, Vector3.Cross(Vector3.up, dir));
                    float heading = Vector3.Dot(v.transform.forward, dir);
                    road = $"link {P(best.Start.position)}->{P(best.end.position)}{(best.oneWay ? " one-way" : "")} {bestD:0.0} m away, " +
                           $"{(side >= 0 ? "right" : "left")} {Mathf.Abs(side):0.0} m, heading {(heading >= 0 ? "with" : "against")} it ({heading:0.00})";
                }
                string route = "no path";
                var vp = agent.path?.vectorPath;
                if (vp != null && vp.Count > 1)
                {
                    int near = 0;
                    float nearD = float.MaxValue;
                    for (int i = 0; i < vp.Count; i++)
                    {
                        float d = Vector3.Distance(vp[i], pos);
                        if (d < nearD) { nearD = d; near = i; }
                    }
                    int next = Mathf.Min(near + 1, vp.Count - 1);
                    route = $"path {vp.Count} points, nearest {near} {P(vp[near])} {nearD:0.0} m away gap {Gap(vp[near], v.transform):0.00}, " +
                            $"next {P(vp[next])} {Vector3.Distance(vp[near], vp[next]):0.0} m on";
                }
                Core.Log.Msg($"[dev] mxveh traffic {n}: '{v.VehicleName}' {P(pos)} kinematic {v.Rb.isKinematic} auto {agent.AutoDriving} " +
                             $"speed {v.Speed_Kmh:0} km/h gap {Gap(pos, v.transform):0.00}, {road}; {route}");
            }
            Core.Log.Msg($"[dev] mxveh traffic: {n} vehicle agents in {r}");
        }

        private static void Clear()
        {
            int cars = 0, walls = 0;
            foreach (var v in Spawned)
            {
                if (v == null) continue;
                v.DestroyVehicle();
                cars++;
            }
            Spawned.Clear();
            _vehicle = null;
            foreach (var w in Walls)
            {
                if (w == null) continue;
                Object.Destroy(w);
                walls++;
            }
            Walls.Clear();
            Core.Log.Msg($"[dev] mxveh clear: {cars} vehicles and {walls} test walls destroyed");
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
            Core.Log.Msg($"[dev] mxveh drive: from {_vehicle.transform.position} to {target}, roads {(!noRoads)}, onGraph {agent.IsOnVehicleGraph()}, " +
                         $"mass {_vehicle.GetComponent<Rigidbody>()?.mass:0} kg, top speed {_vehicle.TopSpeed:0} km/h");
            agent.Navigate(target, settings, cb);
            MelonCoroutines.Start(Watch(agent, target, () => result));
        }

        /// <summary>
        /// Seats the player at the wheel and drives on the game's own throttle path, as a player holding the key would. A
        /// car without a driver stops being simulated as soon as the camera loses sight of it.
        /// </summary>
        private static IEnumerator Gas(float throttle, float seconds)
        {
            var v = _vehicle;
            if (v == null)
            {
                Core.Log.Warning("[dev] mxveh gas: spawn a vehicle first");
                yield break;
            }
            var rb = v.GetComponent<Rigidbody>();
            Core.Log.Msg($"[dev] mxveh gas {throttle} for {seconds:0} s: mass {rb?.mass:0} kg, motor {v.motorTorque.Evaluate(0f):0} Nm, gearing {v.diffGearing:0.00}, top speed {v.TopSpeed:0} km/h");
            if (!v.LocalPlayerIsDriver) v.EnterVehicle();
            yield return new WaitForSeconds(1f);
            if (v == null) yield break;
            Core.Log.Msg($"[dev] mxveh gas: player at the wheel {v.LocalPlayerIsDriver}");
            v.overrideControls = true;
            v.throttleOverride = throttle;
            v.steerOverride = 0f;
            for (int i = 1; i <= seconds && v != null; i++)
            {
                rb?.WakeUp();
                yield return new WaitForSeconds(1f);
                if (v == null) break;
                var t = v.transform;
                float pitch = Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, t.right), t.up, t.right);
                Core.Log.Msg($"[dev] mxveh gas t{i}: pos {t.position}, {v.Speed_Kmh:0.0} km/h, pitch {pitch:0.0}, kinematic {rb?.isKinematic}");
            }
            if (v == null)
            {
                Core.Log.Warning("[dev] mxveh gas: the vehicle is gone");
                yield break;
            }
            v.throttleOverride = 0f;
            v.overrideControls = false;
            Core.Log.Msg("[dev] mxveh gas: done, controls released, the player stays at the wheel");
        }

        private static void Push(Vector3 velocity)
        {
            var rb = _vehicle != null ? _vehicle.GetComponent<Rigidbody>() : null;
            if (rb == null)
            {
                Core.Log.Warning($"[dev] mxveh push: {(_vehicle == null ? "spawn a vehicle first" : "the vehicle has no Rigidbody")}");
                return;
            }
            rb.isKinematic = false;
            rb.velocity = velocity;
            int def = LayerMask.NameToLayer("Default");
            var own = new List<string>();
            foreach (var c in _vehicle.GetComponentsInChildren<Collider>())
                if (c.enabled && !c.isTrigger)
                    own.Add($"{c.name}@{LayerMask.LayerToName(c.gameObject.layer)}{(Physics.GetIgnoreLayerCollision(c.gameObject.layer, def) ? " ignores Default" : "")}");
            int veh = LayerMask.NameToLayer("Vehicle");
            var hitsLayers = new List<string>();
            for (int l = 0; l < 32; l++)
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(l)) && !Physics.GetIgnoreLayerCollision(veh, l)) hitsLayers.Add(LayerMask.LayerToName(l));
            Core.Log.Msg($"[dev] mxveh push: layer Vehicle collides with {string.Join(", ", hitsLayers)}; Default with Default {!Physics.GetIgnoreLayerCollision(0, 0)}, " +
                         $"body include {rb.includeLayers.value:X8} exclude {rb.excludeLayers.value:X8}");
            foreach (var c in _vehicle.GetComponentsInChildren<Collider>())
                if (c.enabled && !c.isTrigger)
                    Core.Log.Msg($"[dev] mxveh push: collider '{c.name}' include {c.includeLayers.value:X8} exclude {c.excludeLayers.value:X8} priority {c.layerOverridePriority}");
            Core.Log.Msg($"[dev] mxveh push: {_vehicle.transform.position} with {velocity}, kinematic {rb.isKinematic}, detect collisions {rb.detectCollisions}, " +
                         $"mode {rb.collisionDetectionMode}, solid colliders: {string.Join(", ", own)}");
            MelonCoroutines.Start(Track(rb));
        }

        private static IEnumerator Track(Rigidbody rb)
        {
            for (int i = 1; i <= 8 && rb != null; i++)
            {
                yield return new WaitForSeconds(0.25f);
                if (rb == null) break;
                var hits = new List<string>();
                foreach (var c in Physics.OverlapSphere(rb.worldCenterOfMass, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                    if (!c.transform.IsChildOf(rb.transform)) hits.Add($"{c.name}@{LayerMask.LayerToName(c.gameObject.layer)}");
                Core.Log.Msg($"[dev] mxveh push: after {i * 0.25f:0.00} s at {rb.position}, velocity {rb.velocity}, within 2.5 m: {string.Join(", ", hits)}");
                Core.Log.Msg($"[dev] mxveh push: contacts {Contacts(rb)}");
            }
            if (rb == null) Core.Log.Warning("[dev] mxveh push: the vehicle is gone");
        }

        /// <summary>
        /// For each solid collider of the car, the world colliders its bounds touch, whether the pair is set to ignore
        /// each other and how deep they overlap.
        /// </summary>
        private static string Contacts(Rigidbody rb)
        {
            var parts = new List<string>();
            foreach (var c in rb.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled || c.isTrigger || c.GetIl2CppType().Name == "WheelCollider") continue;
                var b = c.bounds;
                var pairs = new List<string>();
                foreach (var w in Physics.OverlapBox(b.center, b.extents + Vector3.one * 0.2f, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (w.transform.IsChildOf(rb.transform)) continue;
                    string body = w.attachedRigidbody != null ? $" on body '{w.attachedRigidbody.name}' kinematic {w.attachedRigidbody.isKinematic}" : "";
                    bool overlap = Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, w, w.transform.position, w.transform.rotation, out _, out float depth);
                    pairs.Add($"{w.name}@{LayerMask.LayerToName(w.gameObject.layer)} {w.GetIl2CppType().Name}{body}{(Physics.GetIgnoreCollision(c, w) ? " IGNORED" : "")}{(overlap ? $" in {depth:0.00} m" : "")}");
                }
                parts.Add($"'{c.name}' {c.GetIl2CppType().Name}: {(pairs.Count == 0 ? "-" : string.Join("; ", pairs))}");
            }
            return string.Join(" | ", parts);
        }

        /// <summary>Where kinematic driving puts the car: the closest path point, the ground under it, the point ahead.</summary>
        private static string PathProbe(VehicleAgent agent)
        {
            var path = agent.path;
            if (path == null || path.vectorPath == null || path.vectorPath.Count < 2) return ", no path";
            var box = _vehicle.boundingBox.transform;
            var reference = box.position - box.up * (_vehicle.BoundingBoxDimensions.y * 0.5f);
            var closest = PathUtility.GetClosestPointOnPath(path, agent.transform.position, out int start, out _, out float lerp);
            var ahead = PathUtility.GetAheadPoint(path, reference, 1f);
            string ground = "none";
            float best = float.MaxValue;
            foreach (var h in Physics.RaycastAll(closest + Vector3.up * 2f, Vector3.down, 3f, LayerMask.GetMask("Default", "Terrain"), QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(agent.transform) || h.distance >= best) continue;
                best = h.distance;
                ground = $"{h.point.y:0.00} '{h.collider.name}'";
            }
            return $", path segment {start}/{path.vectorPath.Count - 1} at {lerp:0.00}, closest {closest}, ground {ground}, reference {reference}, 1 m ahead {ahead}";
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
                    Core.Log.Msg($"[dev] mxveh drive: at {p}, {Vector3.Distance(p, target):0} m left, autoDriving={agent.AutoDriving}, speed {_vehicle?.Speed_Kmh:0} km/h, throttle {_vehicle?.throttleOverride:0.00}, kinematic {agent.KinematicMode}, target speed {agent.targetSpeed:0}{PathProbe(agent)}");
                }
                if (result() != null && !agent.AutoDriving) break;
            }
            Core.Log.Msg($"[dev] mxveh drive: watch ended after {Time.time - start:0} s, {travelled:0} m driven, {jumps} jumps, " +
                         $"result {result() ?? "none"}, {Vector3.Distance(tr.position, target):0} m from target");
        }
    }
}
#endif
