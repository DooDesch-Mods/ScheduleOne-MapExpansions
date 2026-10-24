#if DEBUG
using System.Globalization;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.Cartel;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Levelling;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Region spike commands for a 7th map region, all under <c>mxreg</c>:
    ///
    /// mxreg add [x0 z0 x1 z1] [influence]   append region 6 with a rectangular polygon and cartel influence data
    /// mxreg probe [x y z]                   region lookups, unlock state, influence and cartel activities for 6
    /// mxreg unlock                          unlock region 6 and send the unlocked list through LevelManager
    /// mxreg influence delta                 change the cartel influence of region 6 through the server RPC
    /// mxreg stub7 on|off                    add or remove a placeholder region 7, which the 0.3 influence step unlocks
    /// </summary>
    internal static class RegionSpike
    {
        public const EMapRegion Region = (EMapRegion)6;
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static GameObject _zoneRoot;

        private static float F(string s) => float.Parse(s, Ci);

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "add":
                    bool rect = parts.Length > 5;
                    Add(rect ? F(parts[2]) : -120f, rect ? F(parts[3]) : -225f, rect ? F(parts[4]) : 480f, rect ? F(parts[5]) : -956f,
                        parts.Length > 6 ? F(parts[6]) : (parts.Length == 3 ? F(parts[2]) : 0.5f));
                    break;
                case "probe":
                    Probe(parts.Length > 4 ? new Vector3(F(parts[2]), F(parts[3]), F(parts[4])) : (Vector3?)null);
                    break;
                case "unlock": Unlock(); break;
                case "influence" when parts.Length > 2: Influence(F(parts[2])); break;
                case "stub7" when parts.Length > 2: Stub7(parts[2] == "on"); break;
                default:
                    Core.Log.Warning("[dev] mxreg add|probe|unlock|influence (see RegionSpike docs)");
                    break;
            }
        }

        private static Map MapInstance
        {
            get
            {
                var m = Singleton<Map>.Instance;
                if (m == null) throw new InvalidOperationException("Map.Instance is null");
                return m;
            }
        }

        private static void Add(float x0, float z0, float x1, float z1, float influence)
        {
            var map = MapInstance;
            if (map.GetRegionData(Region) != null)
            {
                Core.Log.Warning("[dev] mxreg add: region 6 is already in Map.Regions");
                return;
            }

            // PolygonalZone.Awake snapshots its points, so the points exist before the component wakes.
            _zoneRoot = new GameObject("MapExpansions.Region6");
            _zoneRoot.SetActive(false);
            var points = new GameObject("Points").transform;
            points.SetParent(_zoneRoot.transform, false);
            var corners = new[] { new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1) };
            for (int i = 0; i < corners.Length; i++)
            {
                var p = new GameObject("P" + i).transform;
                p.SetParent(points, false);
                p.position = corners[i];
            }
            var zone = _zoneRoot.AddComponent<PolygonalZone>();
            zone.PointContainer = points;
            _zoneRoot.SetActive(true);
            int zonePoints = zone.points?.Length ?? -1;

            var data = new MapRegionData
            {
                Region = Region,
                Name = "Serena Flats",
                UnlockedByDefault = false,
                StartingNPCs = new Il2CppReferenceArray<NPC>(0),
                RegionDeliveryLocations = new Il2CppReferenceArray<DeliveryLocation>(0),
                AdjacentRegions = new Il2CppReferenceArray<MapRegionData.RegionContainer>(0),
                RegionBounds = zone,
            };
            var old = map.Regions;
            var arr = new Il2CppReferenceArray<MapRegionData>(old.Length + 1);
            for (int i = 0; i < old.Length; i++) arr[i] = old[i];
            arr[old.Length] = data;
            map.Regions = arr;

            string influenceNote;
            var cartel = NetworkSingleton<Cartel>.Instance;
            if (cartel == null || cartel.Influence == null) influenceNote = "no cartel influence object";
            else
            {
                cartel.Influence.regionInfluence.Add(new CartelInfluence.RegionInfluenceData(Region, influence));
                influenceNote = $"influence entry {influence} added ({cartel.Influence.regionInfluence.Count} entries)";
            }
            Core.Log.Msg($"[dev] mxreg add: region 6 'Serena Flats' appended ({map.Regions.Length} regions), polygon x {x0}..{x1} z {z0}..{z1} " +
                         $"with {zonePoints} points; {influenceNote}");
        }

        private static void Probe(Vector3? extra)
        {
            var map = MapInstance;
            var native = Il2CppSystem.Enum.GetValues(Il2CppType.Of<EMapRegion>());
            Core.Log.Msg($"[dev] mxreg probe: native Enum.GetValues(EMapRegion) = {native.Length} values, Map.Regions = {map.Regions.Length}");
            var data = map.GetRegionData(Region);
            Core.Log.Msg($"[dev] mxreg probe: GetRegionData(6) = {(data == null ? "null" : $"'{data.Name}' unlocked {data.IsUnlocked}")}");

            var probes = new List<Vector3> { new(0, 0, 0), new(-5, 0, -130), new(-2, 10, -215), new(3, 40, -542), new(200, 0, -600), new(100, 0, -850) };
            if (extra.HasValue) probes.Add(extra.Value);
            foreach (var p in probes)
                Core.Log.Msg($"[dev] mxreg probe: GetRegionFromPosition{p} = {map.GetRegionFromPosition(p)} ({(int)map.GetRegionFromPosition(p)})");

            var unlocked = map.GetUnlockedRegions();
            var names = new List<string>();
            for (int i = 0; i < unlocked.Count; i++) names.Add(((int)unlocked[i]).ToString(Ci));
            Core.Log.Msg($"[dev] mxreg probe: GetUnlockedRegions = [{string.Join(",", names)}]");

            var player = Player.Local;
            if (player != null) Core.Log.Msg($"[dev] mxreg probe: player at {player.transform.position} region {(int)player.CurrentRegion}");
            else Core.Log.Warning("[dev] mxreg probe: no local player");

            var cartel = NetworkSingleton<Cartel>.Instance;
            if (cartel == null)
            {
                Core.Log.Warning("[dev] mxreg probe: no cartel");
                return;
            }
            Core.Log.Msg($"[dev] mxreg probe: cartel status {cartel.Status}, GetInfluence(6) = {cartel.Influence.GetInfluence(Region)}, " +
                         $"GetInfluence(Uptown) = {cartel.Influence.GetInfluence(EMapRegion.Uptown)}");
            var act = cartel.Activities.GetRegionalActivities(Region);
            Core.Log.Msg($"[dev] mxreg probe: GetRegionalActivities(6) = {(act == null ? "null" : act.name)}");
        }

        private static void Unlock()
        {
            var map = MapInstance;
            var data = map.GetRegionData(Region);
            if (data == null)
            {
                Core.Log.Warning("[dev] mxreg unlock: region 6 not added");
                return;
            }
            data.SetUnlocked();
            var lm = NetworkSingleton<LevelManager>.Instance;
            if (lm == null)
            {
                Core.Log.Error("[dev] mxreg unlock: no LevelManager");
                return;
            }
            var list = map.GetUnlockedRegions();
            lm.SetUnlockedRegions(null, list);
            Core.Log.Msg($"[dev] mxreg unlock: region 6 unlocked {data.IsUnlocked}; SetUnlockedRegions sent with {list.Count} regions");
        }

        /// <summary>
        /// CartelInfluence unlocks <c>region + 1</c> when a region's influence drops through 0.3, guarded only against
        /// Uptown. For region 6 that is <c>GetRegionData(7)</c>; the placeholder makes the lookup succeed so the step
        /// can be compared with and without it.
        /// </summary>
        private static void Stub7(bool on)
        {
            var map = MapInstance;
            var stub = (EMapRegion)7;
            var old = map.Regions;
            if (on)
            {
                if (map.GetRegionData(stub) != null)
                {
                    Core.Log.Warning("[dev] mxreg stub7: region 7 is already in Map.Regions");
                    return;
                }
                var six = map.GetRegionData(Region);
                if (six == null)
                {
                    Core.Log.Warning("[dev] mxreg stub7: add region 6 first, the stub borrows its polygon");
                    return;
                }
                var arr = new Il2CppReferenceArray<MapRegionData>(old.Length + 1);
                for (int i = 0; i < old.Length; i++) arr[i] = old[i];
                arr[old.Length] = new MapRegionData
                {
                    Region = stub,
                    Name = "Region 7 placeholder",
                    UnlockedByDefault = false,
                    StartingNPCs = new Il2CppReferenceArray<NPC>(0),
                    RegionDeliveryLocations = new Il2CppReferenceArray<DeliveryLocation>(0),
                    AdjacentRegions = new Il2CppReferenceArray<MapRegionData.RegionContainer>(0),
                    RegionBounds = six.RegionBounds,
                };
                map.Regions = arr;
                Core.Log.Msg($"[dev] mxreg stub7: placeholder region 7 appended ({map.Regions.Length} regions)");
                return;
            }
            int keep = 0;
            for (int i = 0; i < old.Length; i++) if (old[i].Region != stub) keep++;
            if (keep == old.Length)
            {
                Core.Log.Warning("[dev] mxreg stub7: no region 7 to remove");
                return;
            }
            var kept = new Il2CppReferenceArray<MapRegionData>(keep);
            for (int i = 0, k = 0; i < old.Length; i++) if (old[i].Region != stub) kept[k++] = old[i];
            map.Regions = kept;
            Core.Log.Msg($"[dev] mxreg stub7: placeholder removed ({map.Regions.Length} regions)");
        }

        private static void Influence(float delta)
        {
            var cartel = NetworkSingleton<Cartel>.Instance;
            if (cartel == null)
            {
                Core.Log.Warning("[dev] mxreg influence: no cartel");
                return;
            }
            float before = cartel.Influence.GetInfluence(Region);
            cartel.Influence.ChangeInfluence(Region, delta);
            Core.Log.Msg($"[dev] mxreg influence: ChangeInfluence(6, {delta}) sent, influence before {before}, right after {cartel.Influence.GetInfluence(Region)}, " +
                         $"local player {(Player.Local != null ? "present" : "missing")}");
        }
    }
}
#endif
