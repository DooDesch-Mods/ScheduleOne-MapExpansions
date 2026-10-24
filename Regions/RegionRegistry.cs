using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.Cartel;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Levelling;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Regions
{
    /// <summary>
    /// Map regions of the expansions. Each region gets a number after the vanilla ones (6 and up), in registration
    /// order, and is added to the map and the cartel influence when the main world wakes. Where vanilla code needs a
    /// vanilla region, the region's stand-in answers. Nothing about these regions goes into the save: they come back
    /// from the definitions on every load.
    /// </summary>
    internal static class RegionRegistry
    {
        public const int FirstId = 6;

        internal sealed class Entry
        {
            public string ExpansionId;
            public RegionDefinition Definition;
            public EMapRegion Region;
            public EMapRegion StandIn;

            /// <summary>Outline in world x/z.</summary>
            public Vector2[] World;

            public PolygonalZone Zone;
        }

        private static readonly List<Entry> Entries = new();

        public static IReadOnlyList<Entry> All => Entries;

        public static bool IsOurs(EMapRegion region) => (int)region >= FirstId && Entries.Any(e => e.Region == region);

        public static Entry Get(EMapRegion region) => Entries.FirstOrDefault(e => e.Region == region);

        /// <summary>Numbers the regions of every captured expansion. Runs before the main world wakes.</summary>
        public static void Assign()
        {
            Entries.Clear();
            int next = FirstId;
            foreach (var def in Registry.Usable())
            {
                if (Registry.GetState(def.Id) != ExpansionState.Captured)
                {
                    Core.Log.Msg($"regions: '{def.Id}' is {Registry.GetState(def.Id)}, its {def.Regions.Count} regions are not added");
                    continue;
                }
                var frame = Frame.Of(def);
                foreach (var r in def.Regions)
                {
                    var world = r.Polygon.Select(p => frame.ToWorldXZ(p)).ToArray();
                    Entries.Add(new Entry
                    {
                        ExpansionId = def.Id,
                        Definition = r,
                        Region = (EMapRegion)next,
                        StandIn = (EMapRegion)r.StandInRegion,
                        World = world,
                    });
                    Core.Log.Msg($"regions: '{def.Id}' region '{r.Name}' is {next}, stand-in {(EMapRegion)r.StandInRegion}, " +
                                 $"{world.Length} points, unlocked by default {r.UnlockedByDefault}");
                    next++;
                }
            }
        }

        /// <summary>Id of the expansion whose region contains the world point, or null.</summary>
        public static string ExpansionAt(Vector3 world)
        {
            var p = new Vector2(world.x, world.z);
            foreach (var e in Entries)
                if (Inside(e.World, p)) return e.ExpansionId;
            return null;
        }

        private static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private static bool InMainWorld(Component c) => c != null && c.gameObject.scene.name == "Main";

        /// <summary>Appends a map region for every entry. The zone needs its points before it wakes, so it is built inactive.</summary>
        private static void AddToMap(Map map)
        {
            Assign();
            if (Entries.Count == 0)
            {
                Core.Log.Msg("regions: no expansion regions to add to the map");
                return;
            }
            var old = map.Regions;
            var arr = new Il2CppReferenceArray<MapRegionData>(old.Length + Entries.Count);
            for (int i = 0; i < old.Length; i++) arr[i] = old[i];
            int n = old.Length;
            foreach (var e in Entries)
            {
                var go = new GameObject($"MapExpansions.{e.ExpansionId}.Region.{e.Definition.Name}");
                go.SetActive(false);
                go.transform.SetParent(map.transform, false);
                var points = new GameObject("Points").transform;
                points.SetParent(go.transform, false);
                for (int i = 0; i < e.World.Length; i++)
                {
                    var t = new GameObject("P" + i).transform;
                    t.SetParent(points, false);
                    t.position = new Vector3(e.World[i].x, 0f, e.World[i].y);
                }
                var zone = go.AddComponent<PolygonalZone>();
                zone.PointContainer = points;
                go.SetActive(true);
                e.Zone = zone;

                var data = new MapRegionData
                {
                    Region = e.Region,
                    Name = e.Definition.Name,
                    UnlockedByDefault = e.Definition.UnlockedByDefault,
                    StartingNPCs = new Il2CppReferenceArray<NPC>(0),
                    RegionDeliveryLocations = new Il2CppReferenceArray<DeliveryLocation>(0),
                    AdjacentRegions = new Il2CppReferenceArray<MapRegionData.RegionContainer>(0),
                    RegionBounds = zone,
                };
                // Map.Awake has already run its unlock loop over the vanilla regions.
                if (data.UnlockedByDefault) data.SetUnlocked();
                arr[n++] = data;
                int zonePoints = zone.points?.Length ?? -1;
                if (zonePoints != e.World.Length)
                    Core.Log.Warning($"regions: '{e.ExpansionId}' region {(int)e.Region} zone took {zonePoints} of {e.World.Length} points");
            }
            map.Regions = arr;
            Core.Log.Msg($"regions: {Entries.Count} expansion regions added to the map ({arr.Length} regions)");
        }

        [HarmonyPatch(typeof(Map), nameof(Map.Awake))]
        private static class MapAwakePatch
        {
            private static void Postfix(Map __instance)
            {
                if (!InMainWorld(__instance))
                {
                    Core.Log.Msg($"regions: Map woke in scene '{__instance?.gameObject.scene.name}', not the main world; no regions added");
                    return;
                }
                try
                {
                    AddToMap(__instance);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"regions: adding the expansion regions to the map failed: {e}");
                }
            }
        }

        /// <summary>
        /// Every region needs influence data, or the server-side influence change throws. The start value is the
        /// stand-in's default, so the cartel stands in the new region as it does next door.
        /// </summary>
        [HarmonyPatch(typeof(CartelInfluence), nameof(CartelInfluence.Awake))]
        private static class InfluenceAwakePatch
        {
            private static void Prefix(CartelInfluence __instance)
            {
                if (!InMainWorld(__instance))
                {
                    Core.Log.Msg("regions: CartelInfluence woke outside the main world; no influence data added");
                    return;
                }
                try
                {
                    if (Entries.Count == 0)
                    {
                        Core.Log.Msg("regions: no expansion regions; no influence data added");
                        return;
                    }
                    var old = __instance.DefaultRegionInfluence;
                    var arr = new Il2CppReferenceArray<CartelInfluence.RegionInfluenceData>(old.Length + Entries.Count);
                    for (int i = 0; i < old.Length; i++) arr[i] = old[i];
                    int n = old.Length;
                    foreach (var e in Entries)
                    {
                        float start = 0f;
                        bool found = false;
                        for (int i = 0; i < old.Length; i++)
                            if (old[i] != null && old[i].Region == e.StandIn)
                            {
                                start = old[i].Influence;
                                found = true;
                            }
                        if (!found) Core.Log.Warning($"regions: stand-in {e.StandIn} has no default influence; region {(int)e.Region} starts at 0");
                        arr[n++] = new CartelInfluence.RegionInfluenceData(e.Region, start);
                    }
                    __instance.DefaultRegionInfluence = arr;
                    Core.Log.Msg($"regions: influence data added for {Entries.Count} expansion regions");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"regions: adding cartel influence data failed: {e}");
                }
            }
        }

        /// <summary>
        /// Vanilla unlocks region + 1 when a region's influence drops through 0.3. For an expansion region that number
        /// is no region, the lookup fails and the client is dropped; the change is applied here without the unlock.
        /// </summary>
        [HarmonyPatch(typeof(CartelInfluence), nameof(CartelInfluence.RpcLogic___ChangeInfluence_1267088319))]
        private static class ChangeInfluencePatch
        {
            private static bool Prefix(CartelInfluence __instance, EMapRegion region, float oldInfluence, float newInfluence)
            {
                if (!IsOurs(region)) return true;
                try
                {
                    var data = __instance.GetRegionData(region);
                    if (data == null)
                    {
                        Core.Log.Warning($"regions: influence change for region {(int)region} without influence data; ignored");
                        return false;
                    }
                    data.Influence = Mathf.Clamp01(newInfluence);
                    if (!Mathf.Approximately(oldInfluence, newInfluence)) __instance.OnInfluenceChanged?.Invoke(region, oldInfluence, newInfluence);
                    Core.Log.Msg($"regions: region {(int)region} cartel influence {oldInfluence:0.###} -> {data.Influence:0.###}");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"regions: influence change for region {(int)region} failed: {e}");
                }
                return false;
            }
        }

        /// <summary>The global cartel activity rate averages the unlocked regions; expansion regions stay out of it.</summary>
        [HarmonyPatch(typeof(CartelActivities), "GetInfluenceFraction")]
        private static class InfluenceFractionPatch
        {
            private static bool Prefix(ref float __result)
            {
                if (Entries.Count == 0) return true;
                var map = Singleton<Map>.Instance;
                var cartel = NetworkSingleton<Cartel>.Instance;
                if (map == null || cartel == null || cartel.Influence == null)
                {
                    Core.Log.Warning("regions: influence fraction without map or cartel; vanilla computes it");
                    return true;
                }
                float sum = 0f;
                int count = 0;
                foreach (var r in map.Regions)
                {
                    if (r == null || !r.IsUnlocked || IsOurs(r.Region)) continue;
                    sum += cartel.Influence.GetInfluence(r.Region);
                    count++;
                }
                __result = count == 0 ? 0f : sum / count;
                return false;
            }
        }

        /// <summary>Cartel activities exist for the vanilla regions only; an expansion region uses its stand-in's.</summary>
        [HarmonyPatch(typeof(CartelActivities), nameof(CartelActivities.GetRegionalActivities))]
        private static class RegionalActivitiesPatch
        {
            private static void Postfix(CartelActivities __instance, EMapRegion region, ref CartelRegionActivities __result)
            {
                if (__result != null || !IsOurs(region)) return;
                var e = Get(region);
                __result = __instance.GetRegionalActivities(e.StandIn);
                if (__result == null) Core.Log.Warning($"regions: stand-in {e.StandIn} of region {(int)region} has no cartel activities either");
            }
        }

        /// <summary>The cartel save holds the vanilla regions only; expansion regions start from their default.</summary>
        [HarmonyPatch(typeof(CartelInfluence), nameof(CartelInfluence.GetAllRegionInfluence))]
        private static class SaveInfluencePatch
        {
            private static void Postfix(ref Il2CppReferenceArray<CartelInfluence.RegionInfluenceData> __result)
            {
                if (__result == null || Entries.Count == 0) return;
                var kept = __result.Where(d => d == null || !IsOurs(d.Region)).ToArray();
                if (kept.Length == __result.Length) return;
                __result = new Il2CppReferenceArray<CartelInfluence.RegionInfluenceData>(kept);
            }
        }

        /// <summary>The rank save lists the unlocked regions; expansion regions are left out, so the save loads without the mod.</summary>
        [HarmonyPatch(typeof(LevelManager), nameof(LevelManager.GetSaveString))]
        private static class SaveRankPatch
        {
            private static void Postfix(LevelManager __instance, ref string __result)
            {
                if (Entries.Count == 0) return;
                try
                {
                    var map = Singleton<Map>.Instance;
                    if (map == null)
                    {
                        Core.Log.Warning("regions: rank save without a map; saved as vanilla wrote it");
                        return;
                    }
                    var unlocked = map.GetUnlockedRegions();
                    var kept = new Il2CppSystem.Collections.Generic.List<EMapRegion>();
                    int dropped = 0;
                    for (int i = 0; i < unlocked.Count; i++)
                    {
                        if (IsOurs(unlocked[i])) dropped++;
                        else kept.Add(unlocked[i]);
                    }
                    if (dropped == 0) return;
                    __result = new RankData((int)__instance.Rank, __instance.Tier, __instance.XP, __instance.TotalXP, kept).GetJson();
                    Core.Log.Msg($"regions: rank save written without {dropped} expansion regions");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"regions: filtering the rank save failed; it holds the expansion regions: {e}");
                }
            }
        }

        /// <summary>Drops the zones when the main world goes. The map region data goes with the map.</summary>
        public static void Teardown()
        {
            int zones = 0;
            foreach (var e in Entries)
            {
                if (e.Zone != null)
                {
                    Object.Destroy(e.Zone.gameObject);
                    zones++;
                }
                e.Zone = null;
            }
            Core.Log.Msg($"regions: {zones} region zones removed");
        }
    }
}
