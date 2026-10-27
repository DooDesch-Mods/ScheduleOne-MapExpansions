#if DEBUG
using System.Collections;
using System.Globalization;
using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.FX;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Weather;
using MapExpansions.Lifecycle;
using MapExpansions.Regions;
using MapExpansions.Source;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Dev console commands for testing expansions. Compiled out of Release.
    ///
    /// mxstate                         state of every expansion, its connections, regions and navigation links
    /// mxwhere                         player position, region and the expansion under it
    /// mxtp x y z | id:location        teleport the player to a world point or to a named place of an expansion
    /// mxcam ex ey ez tx ty tz | off   park the camera at a world position looking at a world point
    /// mxframe [seconds]               frame time statistics over a sample window
    /// mxfind name [main|exp]          renderers whose name contains name
    /// mxfog [feature|volumetric] on|off the screen fog render feature, the volumetric fog, or both
    /// mxbox x0 y0 z0 x1 y1 z1 [filter] renderers and colliders whose bounds meet a world box, with their paths
    /// mxline ax az bx bz [step] [y]   every surface a ray down from height y (80) meets, sampled along a line
    /// mxray ax ay az bx by bz         every collider a ray from a to b meets, nearest first
    /// mxedges x0 y0 z0 x1 y1 z1 filter open mesh edges inside a world box, as chains, of renderers whose path contains filter
    /// mxlight                         directional lights, shadow distance and the render pipeline's light layers
    /// mxhover [use]                   what the player's interaction cast hits now, with layers, and what it hovers;
    ///                                 use presses interact on the hovered object
    /// mxlight x y z [radius]          point and spot lights near a point, and how the renderers there are lit (lightmap, probes)
    /// mxlod x y z [radius]            LOD groups near a point: levels, their renderers, which draw this frame
    /// mxmats x y z [radius]           renderers whose bounds reach within radius of a point: mesh, materials, mirroring
    /// mxocc on|off                    occlusion culling of every camera
    /// mxforcelod x y z radius level   forces the LOD groups near a point to one level, -1 to let them choose
    /// mxviews [on|off]                which world the camera's side draws, or separate the views on and off
    /// mxhide filter [on]              stop (or resume) drawing renderers and terrains whose path contains filter
    /// mxterrain filter [pixelError]   render settings of terrains whose name contains filter, optionally a new pixel error
    /// mxproperty code                 ownership, grids, loading docks, idle points and employees of a property
    /// mxagents                        NavMesh agents that are enabled but off the NavMesh
    /// mxarrest                        arrest the local player where they stand, as the police would
    /// mxshop [id|close]               list the shops, pick a shop's seller choice, or close every shop screen
    /// mxbuy code                      buy a listing through Ray's dialogue handler, as his confirmation would
    /// mxdoors x y z [radius] [open]   working doors, knock doors and door renderers near a point; open tries each working door as the player
    /// mxnav ...                       NPC navigation tests, see NavTest
    /// mxveh ...                       vehicle navigation tests, see VehicleTest
    /// </summary>
    internal static class DevConsole
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static int _lastFrame = -1;
        private static string _lastSig = "";

        internal static float F(string s) => float.Parse(s, Ci);

        internal static Vector3 V(string[] p, int i) => new(F(p[i]), F(p[i + 1]), F(p[i + 2]));

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            string cmd = parts[0].ToLowerInvariant();
            if (!cmd.StartsWith("mx", StringComparison.Ordinal)) return false;

            // The string overload calls the list overload, so one submission can reach both prefixes.
            string sig = string.Join(" ", parts);
            if (Time.frameCount == _lastFrame && sig == _lastSig) return true;
            _lastFrame = Time.frameCount;
            _lastSig = sig;

            try
            {
                switch (cmd)
                {
                    case "mxstate": State(); break;
                    case "mxwhere": Where(); break;
                    case "mxnpc" when parts.Length > 1: Npc(parts[1]); break;
                    case "mxtp": Tp(parts); break;
                    case "mxcam": Cam(parts); break;
                    case "mxframe": MelonCoroutines.Start(Frame(parts.Length > 1 ? F(parts[1]) : 3f)); break;
                    case "mxfind" when parts.Length > 1: Find(parts[1], parts.Length > 2 ? parts[2] : ""); break;
                    case "mxfog" when parts.Length > 2 && parts[1] == "tunnel":
                        Connections.TunnelFog.Enabled = parts[2] == "on";
                        Core.Log.Msg($"[dev] mxfog tunnel: fog pass off in tunnels {Connections.TunnelFog.Enabled}");
                        break;
                    case "mxfog" when parts.Length > 2: Fog(parts[1], parts[2] == "on"); break;
                    case "mxfog" when parts.Length > 1: Fog("all", parts[1] == "on"); break;
                    case "mxbox" when parts.Length > 6: Box(V(parts, 1), V(parts, 4), parts.Length > 7 ? parts[7] : ""); break;
                    case "mxhover": Hover(parts.Length > 1 && parts[1] == "use"); break;
                    case "mxlight" when parts.Length > 3: LightsNear(V(parts, 1), parts.Length > 4 ? F(parts[4]) : 8f); break;
                    case "mxlight": Lights(); break;
                    case "mxlod" when parts.Length > 3: Lods(V(parts, 1), parts.Length > 4 ? F(parts[4]) : 3f); break;
                    case "mxocc" when parts.Length > 1:
                        foreach (var cam in Camera.allCameras) cam.useOcclusionCulling = parts[1] == "on";
                        Core.Log.Msg($"[dev] mxocc: occlusion culling {(parts[1] == "on" ? "on" : "off")} for {Camera.allCamerasCount} cameras; scene has occlusion data: {Camera.main != null && Camera.main.useOcclusionCulling}");
                        break;
                    case "mxforcelod" when parts.Length > 5:
                    {
                        var at = V(parts, 1);
                        int level = int.Parse(parts[5], CultureInfo.InvariantCulture), forced = 0;
                        foreach (var g in Object.FindObjectsOfType<LODGroup>(false))
                            if (g != null && (g.transform.position - at).magnitude <= F(parts[4])) { g.ForceLOD(level); forced++; }
                        Core.Log.Msg($"[dev] mxforcelod: {forced} LOD groups within {parts[4]} m of {at} forced to level {level} (-1 lets them choose)");
                        break;
                    }
                    case "mxmats" when parts.Length > 3: Mats(V(parts, 1), parts.Length > 4 ? F(parts[4]) : 0.5f); break;
                    case "mxanchor" when parts.Length > 7: Anchor(V(parts, 1), F(parts[4]), V(parts, 5)); break;
                    case "mxviews" when parts.Length > 1:
                        World.SeparateViews.Enabled = parts[1] == "on";
                        Core.Log.Msg($"[dev] mxviews: views separated {World.SeparateViews.Enabled}");
                        break;
                    case "mxviews": Core.Log.Msg(World.SeparateViews.Describe(Camera.main != null ? Camera.main.transform.position : Vector3.zero)); break;
                    case "mxmap" when parts.Length > 4: World.PhoneMap.Focus(V(parts, 1), parts.Length > 4 ? F(parts[4]) : 1f); break;
                    case "mxmap": Core.Log.Msg(World.PhoneMap.Describe()); break;
                    case "mxphone":
                        if (Il2CppScheduleOne.UI.Phone.Phone.ActiveApp == null) Core.Log.Msg("[dev] mxphone: no app is open");
                        else
                        {
                            Il2CppScheduleOne.UI.Phone.Phone.Instance.RequestCloseApp();
                            Core.Log.Msg("[dev] mxphone: the open app was asked to close");
                        }
                        break;
                    case "mxcover": Core.Log.Msg(World.WeatherCover.Describe(Camera.main != null ? Camera.main.transform.position : Vector3.zero)); break;
                    case "mxhide" when parts.Length > 1:
                        bool on = parts.Length > 2 && parts[^1] == "on";
                        Hide(string.Join(" ", parts, 1, parts.Length - (on ? 2 : 1)), on);
                        break;
                    case "mxterrain" when parts.Length > 1: TerrainInfo(parts[1], parts.Length > 2 ? F(parts[2]) : -1f); break;
                    case "mxwalk" when parts.Length > 2: MelonCoroutines.Start(Walk(F(parts[1]), F(parts[2]))); break;
                    case "mxray" when parts.Length > 6: Ray(V(parts, 1), V(parts, 4)); break;
                    case "mxcollider" when parts.Length > 2: Colliders(string.Join(" ", parts, 1, parts.Length - 2), parts[^1] == "on"); break;
                    case "mxedges" when parts.Length > 7: Edges(V(parts, 1), V(parts, 4), string.Join(" ", parts, 7, parts.Length - 7)); break;
                    case "mxline" when parts.Length > 4: Line(F(parts[1]), F(parts[2]), F(parts[3]), F(parts[4]), parts.Length > 5 ? F(parts[5]) : 4f, parts.Length > 6 ? F(parts[6]) : 80f); break;
                    case "mxproperty" when parts.Length > 1: PropertyInfo(parts[1]); break;
                    case "mxagents": Agents(); break;
                    case "mxarrest": Arrest(); break;
                    case "mxshop": World.Shops.Choose(parts.Length > 1 ? parts[1] : ""); break;
                    case "mxdeal" when parts.Length > 2: Deal(parts[1], string.Join(" ", parts, 2, parts.Length - 2)); break;
                    case "mxdealcheck" when parts.Length > 1: DealCheck(parts[1]); break;
                    case "mxbuy" when parts.Length > 1: World.RealtorListings.Buy(parts[1]); break;
                    case "mxdoors" when parts.Length > 3: Doors(V(parts, 1), parts.Length > 4 ? F(parts[4]) : 8f, parts.Length > 5 && parts[5] == "open"); break;
                    case "mxnav": NavTest.Handle(parts); break;
                    case "mxveh": VehicleTest.Handle(parts); break;
                    default:
                        Core.Log.Warning($"[dev] unknown command '{cmd}'");
                        return false;
                }
            }
            catch (Exception e)
            {
                Core.Log.Error($"[dev] {cmd} failed: {e}");
            }
            return true;
        }

        private static void State()
        {
            foreach (var id in MapExpansionsApi.Expansions)
            {
                var b = BuildPipeline.Get(id);
                string conns = b == null ? "not built" : string.Join(", ", b.Connections.Select(c =>
                {
                    var (from, to) = c.BuiltRange;
                    return $"{c.Name} {c.Curve.Length:0} m, built {from:0}..{to:0} m, {c.Interior.Count} boxes";
                }));
                Core.Log.Msg($"[dev] mxstate: '{id}' {MapExpansionsApi.GetState(id)}; {conns}");
            }
            foreach (var r in RegionRegistry.All)
                Core.Log.Msg($"[dev] mxstate: region {(int)r.Region} '{r.Definition.Name}' of '{r.ExpansionId}', stand-in {r.StandIn}, " +
                             $"zone {(r.Zone != null ? "present" : "missing")}");
            Core.Log.Msg($"[dev] mxstate: {NavLinks.Count} NavMesh links, vehicle graph mask extra {Navigation.VehicleNavigation.ExtraMask}");
        }

        /// <summary>Lists what a save of the property refers to: grid and dock GUIDs in the order the game keeps them.</summary>
        private static void PropertyInfo(string code)
        {
            Il2CppScheduleOne.Property.Property p = null;
            foreach (var x in Il2CppScheduleOne.Property.Property.Properties)
                if (x != null && string.Equals(x.PropertyCode, code, StringComparison.OrdinalIgnoreCase)) { p = x; break; }
            if (p == null)
            {
                Core.Log.Warning($"[dev] mxproperty: no property has the code '{code}'");
                return;
            }
            Core.Log.Msg($"[dev] mxproperty: '{p.PropertyCode}' '{p.PropertyName}' at {p.transform.position}, owned {p.IsOwned}, " +
                         $"{p.Employees.Count} of {p.EmployeeCapacity} employees, {p.EmployeeIdlePoints?.Length ?? 0} idle points");
            for (int i = 0; i < p.Grids.Count; i++)
            {
                var g = p.Grids[i];
                Core.Log.Msg($"[dev] mxproperty: grid {i} '{g.name}' guid {g.GUID}, {g.Tiles.Count} tiles, at {g.transform.position}, " +
                             $"determinant {g.transform.localToWorldMatrix.determinant:0.##}");
            }
            for (int i = 0; i < p.LoadingDocks.Length; i++)
            {
                var d = p.LoadingDocks[i];
                Core.Log.Msg($"[dev] mxproperty: dock {i} '{d.name}' guid {d.GUID}, at {d.transform.position}");
            }
            if (p.EmployeeIdlePoints != null)
                for (int i = 0; i < p.EmployeeIdlePoints.Length; i++)
                    Core.Log.Msg($"[dev] mxproperty: idle point {i} at {p.EmployeeIdlePoints[i].position}");
            foreach (var e in p.Employees)
                Core.Log.Msg($"[dev] mxproperty: employee '{e.ID}' at {e.transform.position}");
        }

        private static void Npc(string idPart)
        {
            int found = 0;
            foreach (var npc in Il2CppScheduleOne.NPCs.NPCManager.NPCRegistry)
            {
                if (npc == null || npc.ID == null || npc.ID.IndexOf(idPart, StringComparison.OrdinalIgnoreCase) < 0) continue;
                found++;
                var pos = npc.transform.position;
                var sm = npc.GetComponentInChildren<Il2CppScheduleOne.NPCs.NPCScheduleManager>(true);
                var action = sm != null && sm.ActiveAction != null ? $"{sm.ActiveAction.GetIl2CppType().Name} '{sm.ActiveAction.name}'" : "none";
                var car = npc.IsInVehicle && npc.CurrentVehicle != null ? $"in '{npc.CurrentVehicle.name}' at {npc.CurrentVehicle.transform.position}" : "on foot";
                string exp = MapExpansionsApi.ExpansionAt(pos) ?? "main world";
                var customer = npc.GetComponent<Il2CppScheduleOne.Economy.Customer>();
                var regionData = Singleton<Map>.Instance?.GetRegionData(npc.Region);
                string deal = customer == null ? "not a customer" : $"customer, {regionData?.RegionDeliveryLocations?.Length ?? 0} deal places in the region";
                Core.Log.Msg($"[dev] mxnpc: '{npc.ID}' at {pos} ({exp}), region {(int)npc.Region}, visible {npc.isVisible}, action {action}, {car}, {deal}");
            }
            if (found == 0) Core.Log.Warning($"[dev] mxnpc: no NPC id contains '{idPart}'");
        }

        /// <summary>Every enabled NavMesh agent that is not on the NavMesh, with its NPC, position and nearest NavMesh point.</summary>
        private static void Agents()
        {
            var agents = UnityEngine.Object.FindObjectsOfType<UnityEngine.AI.NavMeshAgent>();
            int off = 0;
            foreach (var a in agents)
            {
                if (a == null || !a.enabled || !a.gameObject.activeInHierarchy || a.isOnNavMesh) continue;
                off++;
                var pos = a.transform.position;
                var npc = a.GetComponentInParent<Il2CppScheduleOne.NPCs.NPC>();
                string near = UnityEngine.AI.NavMesh.SamplePosition(pos, out var hit, 10f, UnityEngine.AI.NavMesh.AllAreas)
                    ? $"NavMesh {Vector3.Distance(hit.position, pos):F2} m away at {hit.position}"
                    : "no NavMesh within 10 m";
                string exp = MapExpansionsApi.ExpansionAt(pos) ?? "main world";
                Core.Log.Msg($"[dev] mxagents: '{(npc != null ? npc.ID : a.name)}' at {pos} ({exp}), agent type {a.agentTypeID}, {near}");
            }
            Core.Log.Msg($"[dev] mxagents: {off} of {agents.Length} agents are enabled but off the NavMesh");
        }

        /// <summary>
        /// Gives a customer a contract at the named deal location, as if the player had accepted a deal there, so the
        /// customer walks to its stand point. Host only. The product is a stand-in; the deal is for the walk.
        /// </summary>
        private static void Deal(string npcId, string place)
        {
            var npc = Il2CppScheduleOne.NPCs.NPCManager.GetNPC(npcId);
            if (npc == null)
            {
                Core.Log.Warning($"[dev] mxdeal: no NPC '{npcId}'");
                return;
            }
            var customer = npc.GetComponent<Il2CppScheduleOne.Economy.Customer>();
            if (customer == null)
            {
                Core.Log.Warning($"[dev] mxdeal: '{npcId}' is no customer");
                return;
            }
            if (customer.CurrentContract != null)
            {
                Core.Log.Warning($"[dev] mxdeal: '{npcId}' has a contract already");
                return;
            }
            Il2CppScheduleOne.Economy.DeliveryLocation loc = null;
            foreach (var l in Object.FindObjectsOfType<Il2CppScheduleOne.Economy.DeliveryLocation>())
                if (l != null && (string.Equals(l.LocationName, place, StringComparison.OrdinalIgnoreCase) || string.Equals(l.gameObject.name, place, StringComparison.OrdinalIgnoreCase)))
                    loc = l;
            if (loc == null)
            {
                Core.Log.Warning($"[dev] mxdeal: no deal location named '{place}'");
                return;
            }
            var tm = NetworkSingleton<Il2CppScheduleOne.GameTime.TimeManager>.Instance;
            var qm = NetworkSingleton<Il2CppScheduleOne.Quests.QuestManager>.Instance;
            if (tm == null || qm == null)
            {
                Core.Log.Warning($"[dev] mxdeal: time manager {tm != null}, quest manager {qm != null}; no deal");
                return;
            }
            // A customer from across the map would walk for hours of game time; it starts 20 m away instead.
            var standAt = loc.CustomerStandPoint != null ? loc.CustomerStandPoint.position : loc.transform.position;
            if (Vector3.Distance(npc.transform.position, standAt) > 40f)
            {
                var near = standAt + loc.transform.forward * 20f;
                if (UnityEngine.AI.NavMesh.SamplePosition(near, out var hit, 15f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    npc.Movement.Warp(hit.position);
                    Core.Log.Msg($"[dev] mxdeal: '{npcId}' warped to {hit.position}, {Vector3.Distance(hit.position, standAt):0.0} m from the stand point");
                }
                else Core.Log.Warning($"[dev] mxdeal: no NavMesh near {near}; '{npcId}' walks from {npc.transform.position}");
            }
            var products = new Il2CppScheduleOne.Product.ProductList();
            products.entries.Add(new Il2CppScheduleOne.Product.ProductList.Entry("ogkush", Il2CppScheduleOne.ItemFramework.EQuality.Standard, 1));
            var window = new Il2CppScheduleOne.Quests.QuestWindowConfig
            {
                IsEnabled = true,
                WindowStartTime = Il2CppScheduleOne.GameTime.TimeManager.AddMinutesTo24HourTime(tm.CurrentTime, -15),
                WindowEndTime = Il2CppScheduleOne.GameTime.TimeManager.AddMinutesTo24HourTime(tm.CurrentTime, 240),
            };
            var info = new Il2CppScheduleOne.Quests.ContractInfo(50f, products, loc.GUID.ToString(), window, true, 1, 0, false);
            var contract = qm.ContractAccepted(customer, info, true, Il2Cpp.GUIDManager.GenerateUniqueGUID().ToString(), null);
            Core.Log.Msg($"[dev] mxdeal: '{npcId}' at {npc.transform.position} has contract {(contract != null ? contract.name : "none")} at '{loc.LocationName}', " +
                         $"stand point {loc.CustomerStandPoint?.position}");
        }

        private static void DealCheck(string npcId)
        {
            var npc = Il2CppScheduleOne.NPCs.NPCManager.GetNPC(npcId);
            var customer = npc?.GetComponent<Il2CppScheduleOne.Economy.Customer>();
            if (customer == null)
            {
                Core.Log.Warning($"[dev] mxdealcheck: '{npcId}' is no customer");
                return;
            }
            var c = customer.CurrentContract;
            var stand = c?.DeliveryLocation?.CustomerStandPoint;
            float dist = stand == null ? -1f : Vector3.Distance(npc.transform.position, stand.position);
            Core.Log.Msg($"[dev] mxdealcheck: '{npcId}' at {npc.transform.position}, contract {(c != null ? c.name : "none")}, " +
                         $"location '{c?.DeliveryLocation?.LocationName}', {dist:0.0} m from the stand point, at deal location {customer.IsAtDealLocation()}");
        }

        private static void Arrest()
        {
            var p = Player.Local;
            if (p == null)
            {
                Core.Log.Warning("[dev] mxarrest: no local player");
                return;
            }
            Core.Log.Msg($"[dev] mxarrest: arresting '{p.PlayerName}' at {p.transform.position} ({MapExpansionsApi.ExpansionAt(p.transform.position) ?? "main world"})");
            p.Arrest_Server();
        }

        private static void Where()
        {
            var p = Player.Local;
            if (p == null)
            {
                Core.Log.Warning("[dev] mxwhere: no local player");
                return;
            }
            var pos = p.transform.position;
            var map = Singleton<Map>.Instance;
            string region = map != null ? ((int)map.GetRegionFromPosition(pos)).ToString(Ci) : "no map";
            string exp = MapExpansionsApi.ExpansionAt(pos) ?? "-";
            string local = exp != "-" && MapExpansionsApi.TryToLocal(exp, pos, out var l) ? $", local {l}" : "";
            Core.Log.Msg($"[dev] mxwhere: {pos}, region {region}, player region {(int)p.CurrentRegion}, expansion {exp}{local}");
        }

        private static void Tp(string[] parts)
        {
            Vector3 pos;
            if (parts.Length == 2 && parts[1].Contains(':'))
            {
                var bits = parts[1].Split(':');
                if (!MapExpansionsApi.TryResolve(new LocationRef(bits[0], bits[1]), out pos, out _))
                {
                    Core.Log.Warning($"[dev] mxtp: location '{parts[1]}' is unknown or not placed");
                    return;
                }
            }
            else if (parts.Length >= 4) pos = V(parts, 1);
            else
            {
                Core.Log.Warning("[dev] mxtp x y z | id:location");
                return;
            }
            var mv = PlayerSingleton<PlayerMovement>.Instance;
            if (mv == null)
            {
                Core.Log.Warning("[dev] mxtp: no PlayerMovement");
                return;
            }
            mv.Teleport(pos, false);
            Core.Log.Msg($"[dev] mxtp: teleported to {pos}");
        }

        private static void Cam(string[] parts)
        {
            var cam = PlayerSingleton<PlayerCamera>.Instance;
            if (cam == null)
            {
                Core.Log.Warning("[dev] mxcam: no PlayerCamera");
                return;
            }
            if (parts.Length > 1 && parts[1] == "off")
            {
                cam.StopTransformOverride(0f, true, true);
                Core.Log.Msg("[dev] mxcam: camera back on the player");
                return;
            }
            if (parts.Length < 7)
            {
                Core.Log.Warning("[dev] mxcam ex ey ez tx ty tz | off");
                return;
            }
            var eye = V(parts, 1);
            var at = V(parts, 4);
            cam.OverrideTransform(eye, Quaternion.LookRotation(at - eye), 0f, false);
            Core.Log.Msg($"[dev] mxcam: eye {eye} looking at {at}");
        }

        /// <summary>
        /// Pushes the player's CharacterController along a compass yaw like walking forward, and names the
        /// collider it stops at: the player-side view of an "invisible wall" that rays from the camera miss.
        /// </summary>
        private static IEnumerator Walk(float yaw, float metres)
        {
            var mv = PlayerSingleton<PlayerMovement>.Instance;
            var cc = mv != null ? mv.Controller : null;
            if (cc == null)
            {
                Core.Log.Warning("[dev] mxwalk: no PlayerMovement or CharacterController");
                yield break;
            }
            var dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var start = cc.transform.position;
            int stuck = 0, pushes = 0, traced = 0;
            var after = start;
            for (int frame = 0; frame < 600; frame++)
            {
                yield return null;
                var before = cc.transform.position;
                // Something between our Move and this frame moved the player: name what overlaps them at that moment.
                var pushed = before - after;
                if (frame > 0 && pushed.magnitude > 0.002f && pushes++ < 6)
                {
                    Core.Log.Warning($"[dev] mxwalk: frame {frame} the player was moved by {pushed} ({Vector3.Dot(pushed, dir):0.00} m along the walk) from {after} to {before}");
                    float h = Math.Max(0f, cc.height * 0.5f - cc.radius);
                    var seen = new HashSet<IntPtr>();
                    foreach (var dy in new[] { -h, 0f, h })
                    foreach (var c in Physics.OverlapSphere(after + cc.center + Vector3.up * dy, cc.radius + 0.1f, ~0, QueryTriggerInteraction.Collide))
                    {
                        if (c == null || c.transform.IsChildOf(cc.transform) || !seen.Add(c.Pointer)) continue;
                        Core.Log.Msg($"[dev] mxwalk   overlapping '{PathOf(c.transform, 6)}' {c.GetIl2CppType().Name} layer {LayerMask.LayerToName(c.gameObject.layer)} " +
                                     $"trigger {c.isTrigger}, enabled {c.enabled}, bounds {B(c.bounds)}");
                    }
                }
                float step = 2.5f * Time.deltaTime;
                cc.Move(dir * step + Vector3.down * step);
                float moved = Vector3.Dot(cc.transform.position - before, dir);
                after = cc.transform.position;
                // Frame by frame once the walk slows down, to see how the player stops where nothing is in the way.
                if (moved < step * 0.5f && traced++ < 25)
                    Core.Log.Msg($"[dev] mxwalk   frame {frame}: {before.x:0.000},{before.y:0.000},{before.z:0.000} -> {after.x:0.000},{after.y:0.000},{after.z:0.000}, " +
                                 $"moved {moved:0.0000} of {step:0.0000}, pushed before {(frame > 0 ? Vector3.Dot(pushed, dir) : 0f):0.0000}, flags {cc.collisionFlags}, grounded {cc.isGrounded}");
                if (Vector3.Dot(cc.transform.position - start, dir) >= metres)
                {
                    Core.Log.Msg($"[dev] mxwalk: walked {metres:0.0} m from {start} to {cc.transform.position}, nothing in the way");
                    yield break;
                }
                stuck = moved < step * 0.2f ? stuck + 1 : 0;
                if (stuck < 15) continue;

                var pos = cc.transform.position;
                // Rays at knee, hip and head height; the IL2CPP build strips the capsule casts.
                var centre = pos + cc.center;
                float half = Math.Max(0f, cc.height * 0.5f - cc.radius);
                var hits = new[] { -half, 0f, half }
                    .SelectMany(dy => Physics.RaycastAll(centre + Vector3.up * dy, dir, cc.radius + 0.3f, ~0, QueryTriggerInteraction.Ignore).ToArray())
                    .Where(h => h.collider != null && !h.collider.transform.IsChildOf(cc.transform)).OrderBy(h => h.distance).ToList();
                Core.Log.Warning($"[dev] mxwalk: stuck at {pos} after {Vector3.Dot(pos - start, dir):0.00} m, flags {cc.collisionFlags}, radius {cc.radius:0.00}, " +
                                 $"height {cc.height:0.00}, centre {cc.center}, skin {cc.skinWidth:0.00}, step {cc.stepOffset:0.00}, slope {cc.slopeLimit:0}, {hits.Count} collider(s) ahead");
                foreach (var h in hits)
                    Core.Log.Msg($"[dev] mxwalk   {h.distance:0.00} m at {h.point} '{PathOf(h.collider.transform, 6)}' {h.collider.GetIl2CppType().Name} layer {LayerMask.LayerToName(h.collider.gameObject.layer)} trigger {h.collider.isTrigger}");
                // Solid colliders the capsule would touch one step further on, whichever way their faces point.
                var near = new HashSet<IntPtr>();
                foreach (var dy in new[] { -half, 0f, half })
                    foreach (var c in Physics.OverlapSphere(centre + Vector3.up * dy + dir * 0.1f, cc.radius + cc.skinWidth + 0.05f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (c == null || c.transform.IsChildOf(cc.transform) || !near.Add(c.Pointer)) continue;
                        bool ignored = Physics.GetIgnoreCollision(cc, c) || Physics.GetIgnoreLayerCollision(cc.gameObject.layer, c.gameObject.layer);
                        Core.Log.Msg($"[dev] mxwalk   touching '{PathOf(c.transform, 6)}' {c.GetIl2CppType().Name} layer {LayerMask.LayerToName(c.gameObject.layer)} " +
                                     $"enabled {c.enabled}, ignored by the player {ignored}, bounds {B(c.bounds)}");
                    }
                yield break;
            }
            Core.Log.Warning($"[dev] mxwalk: gave up after 600 frames at {cc.transform.position}, {pushes} frames moved the player outside the walk");
        }

        private static IEnumerator Frame(float seconds)
        {
            var samples = new List<float>();
            float end = Time.realtimeSinceStartup + seconds;
            yield return null;
            while (Time.realtimeSinceStartup < end)
            {
                samples.Add(Time.unscaledDeltaTime * 1000f);
                yield return null;
            }
            if (samples.Count == 0)
            {
                Core.Log.Warning("[dev] mxframe: no frames sampled");
                yield break;
            }
            samples.Sort();
            float P(float q) => samples[Math.Min(samples.Count - 1, (int)(q * samples.Count))];
            Core.Log.Msg($"[dev] mxframe: {samples.Count} frames in {seconds:0.#} s, avg {samples.Average():0.00} ms, p50 {P(0.5f):0.00}, " +
                         $"p95 {P(0.95f):0.00}, max {samples[samples.Count - 1]:0.00}");
        }

        private static void Find(string part, string scope)
        {
            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>(true))
            {
                if (r.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string exp = BuildPipeline.All.FirstOrDefault(b => b.IsOurs(r.transform))?.Id;
                if ((scope == "exp" && exp == null) || (scope == "main" && exp != null)) continue;
                var b = r.bounds;
                var t = r.transform;
                Core.Log.Msg($"[dev] mxfind {exp ?? "main"} '{r.name}' under '{(t.parent != null ? t.parent.name : "-")}' enabled {r.enabled}: " +
                             $"x {b.min.x:0}..{b.max.x:0} y {b.min.y:0}..{b.max.y:0} z {b.min.z:0}..{b.max.z:0}");
                if (++n >= 25) break;
            }
            Core.Log.Msg($"[dev] mxfind '{part}' {scope}: {n} renderers listed");
        }

        /// <summary>
        /// Every working door, knock door and door renderer within a radius, so doubled, dead or unreachable doors show.
        /// With open, each working door the player may use is opened from outside the way its handle does.
        /// </summary>
        private static void Doors(Vector3 c, float radius, bool open)
        {
            int n = 0;
            foreach (var d in Object.FindObjectsOfType<Il2CppScheduleOne.Doors.DoorController>(true))
            {
                if (d == null || Vector3.Distance(d.transform.position, c) > radius) continue;
                n++;
                bool can = d.CanPlayerAccess(Il2CppScheduleOne.Doors.EDoorSide.Exterior, out var reason);
                int ints = (d.ExteriorIntObjs?.Length ?? 0) + (d.InteriorIntObjs?.Length ?? 0);
                Core.Log.Msg($"[dev] mxdoors working '{PathOf(d.transform, 6)}' {d.GetIl2CppType().Name} at {d.transform.position}: active {d.gameObject.activeInHierarchy}, " +
                             $"enabled {d.enabled}, spawned {d.IsSpawned}, access {d.PlayerAccess}, player may open {can}{(can ? "" : $" ({reason})")}, open {d.IsOpen}, handles {ints}, " +
                             $"player blocker {(d.PlayerBlocker == null ? "none" : d.PlayerBlocker.enabled ? "up" : "down")}");
                if (open && can && !d.IsOpen)
                {
                    d.ExteriorHandleInteracted();
                    Core.Log.Msg($"[dev] mxdoors opened '{d.name}' from outside: open {d.IsOpen}");
                }
            }
            foreach (var s in Object.FindObjectsOfType<Il2CppScheduleOne.Doors.StaticDoor>(true))
            {
                if (s == null || Vector3.Distance(s.transform.position, c) > radius) continue;
                n++;
                Core.Log.Msg($"[dev] mxdoors knock '{PathOf(s.transform, 6)}' at {s.transform.position}: active {s.gameObject.activeInHierarchy}, usable {s.Usable}, " +
                             $"knock {s.CanKnock}, building '{(s.Building != null ? s.Building.BuildingName : "none")}', interactable {(s.IntObj != null && s.IntObj.enabled)}");
            }
            foreach (var r in Object.FindObjectsOfType<Renderer>(false))
            {
                if (r == null || !r.enabled || r.name.IndexOf("door", StringComparison.OrdinalIgnoreCase) < 0 && PathOf(r.transform, 3).IndexOf("door", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (Vector3.Distance(r.bounds.center, c) > radius) continue;
                Core.Log.Msg($"[dev] mxdoors renderer '{PathOf(r.transform, 6)}': {B(r.bounds)}");
            }
            Core.Log.Msg($"[dev] mxdoors {n} doors within {radius} m of {c}");
        }

        private static string PathOf(Transform t, int depth = 5)
        {
            var names = new List<string>();
            for (var p = t; p != null && names.Count < depth; p = p.parent) names.Add(p.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static string B(Bounds b) => $"x {b.min.x:0.0}..{b.max.x:0.0} y {b.min.y:0.0}..{b.max.y:0.0} z {b.min.z:0.0}..{b.max.z:0.0}";

        private static void Box(Vector3 a, Vector3 c, string filter)
        {
            var box = new Bounds();
            box.SetMinMax(Vector3.Min(a, c), Vector3.Max(a, c));
            bool Match(Transform t) => filter.Length == 0 || PathOf(t, 8).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            int r = 0, col = 0;
            foreach (var x in Object.FindObjectsOfType<Renderer>(false))
            {
                if (x == null || !x.bounds.Intersects(box) || !Match(x.transform)) continue;
                var m = x.sharedMaterial;
                Core.Log.Msg($"[dev] mxbox renderer '{PathOf(x.transform)}' {x.GetIl2CppType().Name} enabled {x.enabled} layer {LayerMask.LayerToName(x.gameObject.layer)}: " +
                             $"{B(x.bounds)}, material '{(m != null ? m.name : "-")}' shader '{(m != null && m.shader != null ? m.shader.name : "-")}'");
                if (++r >= 80) break;
            }
            foreach (var x in Physics.OverlapBox(box.center, box.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
            {
                if (x == null || !Match(x.transform)) continue;
                Core.Log.Msg($"[dev] mxbox collider '{PathOf(x.transform)}' {x.GetIl2CppType().Name} trigger {x.isTrigger} layer {LayerMask.LayerToName(x.gameObject.layer)}: {B(x.bounds)}");
                if (++col >= 80) break;
            }
            Core.Log.Msg($"[dev] mxbox {B(box)} '{filter}': {r} renderers, {col} enabled colliders listed (80 each at most)");
        }

        /// <summary>
        /// The open edges (used by one triangle only) of matching mesh renderers inside a world box, chained, with a
        /// point every few metres. Vertices are welded by position, so seams of split normals do not count as open.
        /// </summary>
        private static void Edges(Vector3 a, Vector3 c, string filter)
        {
            var box = new Bounds();
            box.SetMinMax(Vector3.Min(a, c), Vector3.Max(a, c));
            int renderers = 0;
            foreach (var mr in Object.FindObjectsOfType<MeshRenderer>(false))
            {
                if (mr == null || !mr.bounds.Intersects(box)) continue;
                if (PathOf(mr.transform, 8).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var mesh = World.MeshCarver.BuildBack(mr, "mxedges");
                if (mesh == null)
                {
                    Core.Log.Warning($"[dev] mxedges '{PathOf(mr.transform)}': no triangles could be read");
                    continue;
                }
                renderers++;
                var verts = mesh.vertices;
                var toWorld = mr.transform.localToWorldMatrix;
                var ids = new Dictionary<Vector3Int, int>();
                var points = new List<Vector3>();
                var weld = new int[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    var w = toWorld.MultiplyPoint3x4(verts[i]);
                    var key = Vector3Int.RoundToInt(w * 100f);
                    if (!ids.TryGetValue(key, out int id))
                    {
                        id = points.Count;
                        ids[key] = id;
                        points.Add(w);
                    }
                    weld[i] = id;
                }
                // Per edge: how often it is used, and the third corner of the last triangle using it.
                var uses = new Dictionary<long, int>();
                var thirds = new Dictionary<long, int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var tris = mesh.GetTriangles(s);
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            int p = weld[tris[t + e]], q = weld[tris[t + (e + 1) % 3]];
                            long k = p < q ? ((long)p << 32) | (uint)q : ((long)q << 32) | (uint)p;
                            uses[k] = uses.TryGetValue(k, out int n) ? n + 1 : 1;
                            thirds[k] = weld[tris[t + (e + 2) % 3]];
                        }
                }
                Object.Destroy(mesh);
                var next = new Dictionary<int, List<int>>();
                int open = 0;
                foreach (var kv in uses)
                {
                    if (kv.Value != 1) continue;
                    int p = (int)(kv.Key >> 32), q = (int)(kv.Key & 0xffffffff);
                    if (!box.Contains((points[p] + points[q]) * 0.5f)) continue;
                    open++;
                    if (!next.TryGetValue(p, out var lp)) next[p] = lp = new List<int>();
                    if (!next.TryGetValue(q, out var lq)) next[q] = lq = new List<int>();
                    lp.Add(q);
                    lq.Add(p);
                }
                Core.Log.Msg($"[dev] mxedges '{PathOf(mr.transform)}': {points.Count} welded vertices, {open} open edges in {B(box)}");
                var seen = new HashSet<int>();
                int chains = 0;
                // Chains start at an end (one neighbour) first, then at whatever is left (closed loops).
                foreach (var start in next.Keys.OrderBy(k => next[k].Count == 1 ? 0 : 1).ToList())
                {
                    if (seen.Contains(start)) continue;
                    var chain = new List<int> { start };
                    seen.Add(start);
                    int cur = start;
                    while (true)
                    {
                        int nxt = -1;
                        foreach (var n in next[cur])
                            if (!seen.Contains(n)) { nxt = n; break; }
                        if (nxt < 0) break;
                        seen.Add(nxt);
                        chain.Add(nxt);
                        cur = nxt;
                    }
                    float len = 0f;
                    for (int i = 1; i < chain.Count; i++) len += Vector3.Distance(points[chain[i - 1]], points[chain[i]]);
                    if (len < 2f) continue;
                    var sb = new System.Text.StringBuilder();
                    float acc = 0f;
                    for (int i = 0; i < chain.Count; i++)
                    {
                        if (i > 0) acc += Vector3.Distance(points[chain[i - 1]], points[chain[i]]);
                        if (i == 0 || i == chain.Count - 1 || acc >= 3f)
                        {
                            var p = points[chain[i]];
                            sb.Append($" ({p.x:0.0},{p.y:0.0},{p.z:0.0})");
                            acc = 0f;
                        }
                    }
                    Core.Log.Msg($"[dev] mxedges chain {++chains}: {chain.Count} points, {len:0.0} m:{sb}");
                    // Per edge: its midpoint's x, how far the triangle's third corner stands above the edge where it
                    // stands over it, and how far the triangle faces up or down (0 for a wall, 1 for flat).
                    var eb = new System.Text.StringBuilder();
                    for (int i = 1; i < chain.Count; i++)
                    {
                        int p = chain[i - 1], q = chain[i];
                        long k = p < q ? ((long)p << 32) | (uint)q : ((long)q << 32) | (uint)p;
                        if (!thirds.TryGetValue(k, out int o)) continue;
                        Vector3 e0 = points[p], e1 = points[q], e2 = points[o];
                        var d = new Vector2(e1.x - e0.x, e1.z - e0.z);
                        float u = d.sqrMagnitude < 1e-8f ? 0.5f : Mathf.Clamp01(Vector2.Dot(new Vector2(e2.x - e0.x, e2.z - e0.z), d) / d.sqrMagnitude);
                        float dy = e2.y - Mathf.Lerp(e0.y, e1.y, u);
                        float ny = Mathf.Abs(Vector3.Cross(e1 - e0, e2 - e0).normalized.y);
                        eb.Append($" {(e0.x + e1.x) * 0.5f:0}:{dy:0.0}/{ny:0.00}");
                    }
                    Core.Log.Msg($"[dev] mxedges chain {chains} edges x:rise/normal-up:{eb}");
                    if (chains >= 30) break;
                }
            }
            Core.Log.Msg($"[dev] mxedges '{filter}' {B(box)}: {renderers} renderers read");
        }

        /// <summary>Switches drawing of every renderer and terrain whose path contains the filter, to find what draws where.</summary>
        private static void Hide(string filter, bool show)
        {
            bool Match(Transform t) => PathOf(t, 8).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            int r = 0, t = 0;
            foreach (var x in Object.FindObjectsOfType<Renderer>(true))
            {
                if (x == null || !Match(x.transform)) continue;
                x.forceRenderingOff = !show;
                r++;
            }
            foreach (var x in Object.FindObjectsOfType<Terrain>(true))
            {
                if (x == null || !Match(x.transform)) continue;
                x.drawHeightmap = show;
                t++;
            }
            Core.Log.Msg($"[dev] mxhide '{filter}' {(show ? "on" : "off")}: {r} renderers, {t} terrains");
        }

        /// <summary>Switches every collider whose path contains the filter on or off.</summary>
        private static void Colliders(string filter, bool on)
        {
            int n = 0;
            foreach (var c in Object.FindObjectsOfType<Collider>(true))
            {
                if (c == null || PathOf(c.transform, 8).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                c.enabled = on;
                n++;
                Core.Log.Msg($"[dev] mxcollider '{PathOf(c.transform, 6)}' {c.GetIl2CppType().Name} {(on ? "on" : "off")}");
            }
            Core.Log.Msg($"[dev] mxcollider '{filter}' {(on ? "on" : "off")}: {n} colliders");
        }

        /// <summary>Lists the render settings of terrains whose name contains the filter; a pixel error of 0 or more is set.</summary>
        private static void TerrainInfo(string filter, float pixelError)
        {
            int n = 0;
            foreach (var t in Object.FindObjectsOfType<Terrain>(true))
            {
                if (t == null || t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                n++;
                if (pixelError >= 0f) t.heightmapPixelError = pixelError;
                var td = t.terrainData;
                Core.Log.Msg($"[dev] mxterrain '{t.name}': pixel error {t.heightmapPixelError:0.##}, max LOD {t.heightmapMaximumLOD}, " +
                             $"instanced {t.drawInstanced}, basemap distance {t.basemapDistance:0}, heightmap res {td?.heightmapResolution}, " +
                             $"holes res {td?.holesResolution}, holes compressed {td?.enableHolesTextureCompression}, size {td?.size}");
            }
            if (n == 0) Core.Log.Warning($"[dev] mxterrain: no terrain name contains '{filter}'");
            // A quality level can override every terrain's own pixel error.
            if (pixelError >= 0f && (QualitySettings.terrainQualityOverrides & TerrainQualityOverrides.PixelError) != 0)
                QualitySettings.terrainPixelError = pixelError;
            Core.Log.Msg($"[dev] mxterrain: quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}' overrides " +
                         $"{QualitySettings.terrainQualityOverrides}, pixel error {QualitySettings.terrainPixelError:0.##}");
        }

        private static void Line(float ax, float az, float bx, float bz, float step, float fromY)
        {
            var a = new Vector2(ax, az);
            var b = new Vector2(bx, bz);
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / Mathf.Max(0.25f, step)));
            for (int i = 0; i <= n; i++)
            {
                var p = Vector2.Lerp(a, b, (float)i / n);
                var hits = Physics.RaycastAll(new Vector3(p.x, fromY, p.y), Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore);
                var sorted = hits.ToArray().OrderByDescending(h => h.point.y).Take(4)
                    .Select(h => $"{h.point.y:0.00} '{PathOf(h.collider.transform, 3)}' {h.collider.GetIl2CppType().Name}");
                Core.Log.Msg($"[dev] mxline ({p.x:0.0}, {p.y:0.0}): {string.Join(" | ", sorted)}");
            }
        }

        private static void Ray(Vector3 a, Vector3 b)
        {
            var d = b - a;
            // Back faces count too: a character controller stops at a mesh face from either side, a plain ray only at its front.
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            List<RaycastHit> hits;
            try { hits = Physics.RaycastAll(a, d.normalized, d.magnitude, ~0, QueryTriggerInteraction.Ignore).ToArray().OrderBy(h => h.distance).ToList(); }
            finally { Physics.queriesHitBackfaces = backfaces; }
            foreach (var h in hits)
            {
                string side = Vector3.Dot(h.normal, d) > 0f ? "back face" : "front face";
                Core.Log.Msg($"[dev] mxray {h.distance:0.00} m at {h.point} normal {h.normal} ({side}) '{PathOf(h.collider.transform, 5)}' {h.collider.GetIl2CppType().Name} layer {LayerMask.LayerToName(h.collider.gameObject.layer)}");
                var mc = h.collider.TryCast<MeshCollider>();
                if (mc != null) RayTriangle(mc, h.triangleIndex);
            }
            Core.Log.Msg($"[dev] mxray {a} -> {b}: {hits.Count} hits");
        }

        /// <summary>The mesh a hit collider uses and whether any clearance box of a built connection meets the hit triangle.</summary>
        private static void RayTriangle(MeshCollider mc, int triangle)
        {
            var mesh = mc.sharedMesh;
            if (mesh == null) { Core.Log.Msg("[dev] mxray   no mesh on the collider"); return; }
            string about = $"mesh '{mesh.name}' readable {mesh.isReadable}, convex {mc.convex}, {mesh.vertexCount} vertices, triangle {triangle}";
            if (!mesh.isReadable || triangle < 0) { Core.Log.Msg($"[dev] mxray   {about}"); return; }
            var tris = mesh.triangles;
            var verts = mesh.vertices;
            if (triangle * 3 + 2 >= tris.Length) { Core.Log.Msg($"[dev] mxray   {about} is past the {tris.Length / 3} triangles"); return; }
            var m = mc.transform.localToWorldMatrix;
            Vector3 p0 = m.MultiplyPoint3x4(verts[tris[triangle * 3]]);
            Vector3 p1 = m.MultiplyPoint3x4(verts[tris[triangle * 3 + 1]]);
            Vector3 p2 = m.MultiplyPoint3x4(verts[tris[triangle * 3 + 2]]);
            Core.Log.Msg($"[dev] mxray   {about}: {p0} {p1} {p2}");
            var centre = (p0 + p1 + p2) / 3f;
            foreach (var id in MapExpansionsApi.Expansions)
            {
                var built = BuildPipeline.Get(id);
                if (built == null) continue;
                foreach (var c in built.Connections)
                {
                    int meets = -1, nearest = -1;
                    float best = float.MaxValue;
                    for (int i = 0; i < c.Interior.Count; i++)
                    {
                        if (meets < 0 && c.Interior[i].Intersects(p0, p1, p2)) meets = i;
                        float dist = Vector3.Distance(c.Interior[i].Center, centre);
                        if (dist < best) { best = dist; nearest = i; }
                    }
                    string near = nearest < 0 ? "no boxes" : $"nearest {nearest} {c.Interior[nearest]} {best:0.0} m away";
                    Core.Log.Msg($"[dev] mxray   {c.Name}: {(meets >= 0 ? $"clearance box {meets} meets it" : "no clearance box meets it")}, {near}");
                }
            }
        }

        private static void Lights()
        {
            foreach (var l in Object.FindObjectsOfType<Light>(false))
            {
                if (l.type != LightType.Directional) continue;
                Core.Log.Msg($"[dev] mxlight '{PathOf(l.transform)}' enabled {l.enabled} intensity {l.intensity:0.00} shadows {l.shadows} " +
                             $"cullingMask {l.cullingMask} renderingLayerMask {l.renderingLayerMask}");
            }
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var urp = rp != null ? rp.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>() : null;
            string layers = urp == null ? $"pipeline {(rp != null ? rp.GetIl2CppType().Name : "none")}" :
                $"URP shadowDistance {urp.shadowDistance:0} cascades {urp.shadowCascadeCount} lightLayers {urp.useRenderingLayers}";
            Core.Log.Msg($"[dev] mxlight: quality shadowDistance {QualitySettings.shadowDistance:0}; {layers}");
        }

        private static void Hover(bool use)
        {
            var im = Singleton<Il2CppScheduleOne.Interaction.InteractionManager>.Instance;
            var cam = PlayerSingleton<PlayerCamera>.Instance;
            if (im == null || cam == null)
            {
                Core.Log.Warning($"[dev] mxhover: no {(im == null ? "InteractionManager" : "PlayerCamera")}");
                return;
            }
            int mask = im.Interaction_SearchMask.value;
            var layers = new List<string>();
            for (int l = 0; l < 32; l++)
                if ((mask & (1 << l)) != 0) layers.Add(LayerMask.LayerToName(l));
            var o = cam.transform.position;
            var f = cam.transform.forward;
            Core.Log.Msg($"[dev] mxhover camera {o} forward {f}; search mask {string.Join(", ", layers)}; " +
                         $"hovered '{(im.HoveredInteractableObject != null ? PathOf(im.HoveredInteractableObject.transform, 5) : "none")}', " +
                         $"blocked {im.IsAnythingBlockingInteraction()}");
            var hits = Physics.RaycastAll(o, f, 4f, ~0, QueryTriggerInteraction.Collide).ToArray().OrderBy(h => h.distance).ToList();
            foreach (var h in hits)
            {
                var io = h.collider.GetComponentInParent<Il2CppScheduleOne.Interaction.InteractableObject>();
                int layer = h.collider.gameObject.layer;
                Core.Log.Msg($"[dev] mxhover hit {h.distance:0.00} m '{PathOf(h.collider.transform, 5)}' {h.collider.GetIl2CppType().Name} trigger {h.collider.isTrigger}, " +
                             $"layer {LayerMask.LayerToName(layer)} in mask {(mask & (1 << layer)) != 0}, interactable '{(io != null ? io.name : "none")}'");
            }
            Core.Log.Msg($"[dev] mxhover {hits.Count} hits within 4 m");
            if (!use) return;
            var hovered = im.HoveredInteractableObject;
            if (hovered == null)
            {
                Core.Log.Warning("[dev] mxhover use: nothing is hovered");
                return;
            }
            hovered.StartInteract();
            hovered.EndInteract();
            Core.Log.Msg($"[dev] mxhover use: interacted with '{PathOf(hovered.transform, 5)}', state {hovered._interactionState}");
        }

        private static void Lods(Vector3 c, float radius)
        {
            int groups = 0;
            var cam = Camera.main;
            foreach (var g in Object.FindObjectsOfType<LODGroup>(false))
            {
                if (g == null || (g.transform.position - c).magnitude > radius) continue;
                groups++;
                float dist = cam != null ? (cam.transform.position - g.transform.TransformPoint(g.localReferencePoint)).magnitude : -1f;
                Core.Log.Msg($"[dev] mxlod '{PathOf(g.transform, 6)}' at {g.transform.position}: enabled {g.enabled}, size {g.size:0.00}, " +
                             $"reference {g.localReferencePoint}, lossy scale {g.transform.lossyScale}, fade {g.fadeMode}, camera {dist:0.0} m away, bias {QualitySettings.lodBias:0.00}");
                var lods = g.GetLODs();
                for (int i = 0; i < lods.Length; i++)
                {
                    var rs = lods[i].renderers;
                    var names = new List<string>();
                    if (rs != null)
                        foreach (var r in rs)
                            names.Add(r == null ? "null" : $"'{PathOf(r.transform, 2)}' enabled {r.enabled} visible {r.isVisible} batched {r.isPartOfStaticBatch} " +
                                                       $"bounds {r.bounds.center}/{r.bounds.size} root {(r.staticBatchRootTransform == null ? "none" : PathOf(r.staticBatchRootTransform, 3))}");
                    Core.Log.Msg($"[dev] mxlod   level {i} below {lods[i].screenRelativeTransitionHeight:0.000}: {string.Join("; ", names)}");
                }
            }
            foreach (var r in Object.FindObjectsOfType<Renderer>(false))
            {
                if (r == null || (r.bounds.center - c).magnitude > radius || r.GetComponentInParent<LODGroup>() != null) continue;
                Core.Log.Msg($"[dev] mxlod no group: '{PathOf(r.transform, 4)}' enabled {r.enabled} visible {r.isVisible} batched {r.isPartOfStaticBatch}");
            }
            Core.Log.Msg($"[dev] mxlod: {groups} LOD groups within {radius} m of {c}");
        }

        private static void Mats(Vector3 c, float radius)
        {
            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>(false))
            {
                if (r == null || (r.bounds.ClosestPoint(c) - c).magnitude > radius) continue;
                n++;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null && mf.sharedMesh != null ? $"{mf.sharedMesh.name} ({mf.sharedMesh.subMeshCount} submeshes)" : "no mesh";
                var mats = r.sharedMaterials.Select(m => m == null ? "null" : $"{m.name} [{(m.shader != null ? m.shader.name : "no shader")}" +
                                                         $"{(m.HasProperty("_Cull") ? $", cull {m.GetFloat("_Cull"):0}" : "")}]");
                var s = r.transform.lossyScale;
                Core.Log.Msg($"[dev] mxmats '{PathOf(r.transform, 6)}': enabled {r.enabled}, forced off {r.forceRenderingOff}, visible {r.isVisible}, mesh {mesh}, " +
                             $"scale {s}{(s.x * s.y * s.z < 0f ? " MIRRORED" : "")}, bounds {r.bounds.center}/{r.bounds.size}, materials {string.Join(", ", mats)}");
            }
            Core.Log.Msg($"[dev] mxmats: {n} renderers within {radius} m of {c}");
        }

        private static void LightsNear(Vector3 c, float radius)
        {
            int lights = 0;
            foreach (var l in Object.FindObjectsOfType<Light>(true))
            {
                if (l == null || l.type == LightType.Directional || (l.transform.position - c).magnitude > radius) continue;
                lights++;
                Core.Log.Msg($"[dev] mxlight near '{PathOf(l.transform, 6)}' {l.type} at {l.transform.position}: active {l.gameObject.activeInHierarchy}, " +
                             $"enabled {l.enabled}, intensity {l.intensity:0.00}, range {l.range:0.0}, culling {l.cullingMask}");
            }
            var byKind = new Dictionary<string, int>();
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>(false))
            {
                if (r == null || (r.bounds.center - c).magnitude > radius) continue;
                var root = r.transform.root.name;
                var side = root.StartsWith("MapExpansions.", StringComparison.Ordinal) ? (PathOf(r.transform, 99).Contains("/Copies/") ? "copy" : "expansion") : "main";
                var anchor = r.probeAnchor == null ? "own place" : $"anchor '{PathOf(r.probeAnchor, 3)}' at {r.probeAnchor.position}";
                var key = $"{side} lightmap {(r.lightmapIndex is >= 0 and < 65534 ? "baked" : "none")} probes {r.lightProbeUsage} reflections {r.reflectionProbeUsage} from {anchor}";
                byKind[key] = byKind.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            foreach (var kv in byKind) Core.Log.Msg($"[dev] mxlight near renderers {kv.Key}: {kv.Value}");
            Core.Log.Msg($"[dev] mxlight near: {lights} point/spot lights within {radius} m of {c}; ambient {RenderSettings.ambientMode} {RenderSettings.ambientLight}");
        }

        /// <summary>
        /// Lights every renderer within <paramref name="radius"/> of <paramref name="c"/> by the light and reflection
        /// probes at <paramref name="at"/>, to compare a building's look under another place's probes.
        /// </summary>
        private static void Anchor(Vector3 c, float radius, Vector3 at)
        {
            var anchor = new GameObject("MapExpansions.DevProbeAnchor").transform;
            anchor.position = at;
            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>(false))
            {
                if (r == null || (r.bounds.center - c).magnitude > radius) continue;
                r.probeAnchor = anchor;
                n++;
            }
            Core.Log.Msg($"[dev] mxanchor: {n} renderers within {radius} m of {c} now take their probes at {at}");
        }

        /// <summary>Switches the fog pass ("feature"), the volumetric fog ("volumetric") or both ("all").</summary>
        private static void Fog(string which, bool on)
        {
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            string feature;
            if (which == "volumetric") feature = "fog feature untouched";
            else if (env == null) feature = "no EnvironmentManager";
            else if (env._fogFeature == null) feature = "no fog feature";
            else
            {
                env._fogFeature.SetActive(on);
                feature = $"fog feature active {env._fogFeature.isActive}";
            }
            var fx = Singleton<EnvironmentFX>.Instance;
            string volumetric;
            if (which == "feature") volumetric = "volumetric fog untouched";
            else if (fx == null) volumetric = "no EnvironmentFX";
            else if (fx.VolumetricFog == null) volumetric = "no volumetric fog";
            else
            {
                fx.VolumetricFog.enabled = on;
                volumetric = $"volumetric fog enabled {fx.VolumetricFog.enabled}";
            }
            Core.Log.Msg($"[dev] mxfog {which} {(on ? "on" : "off")}: {feature}; {volumetric}");
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new[] { typeof(string) })]
    internal static class SubmitCommandStringPatch
    {
        private static bool Prefix(string args) => !DevConsole.TryHandle(args);
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class SubmitCommandListPatch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args) => !DevConsole.TryHandle(args);
    }
}
#endif
