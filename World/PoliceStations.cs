using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using MapExpansions.Lifecycle;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Police stations of expansions. The game releases every arrested player at the main world's station and parks the
    /// car taken from them there; both read the station from the map. While an arrest in an expansion with its own
    /// station is handled, the map's station points at the expansion's release point and parking spaces instead.
    /// </summary>
    internal static class PoliceStations
    {
        private sealed class Station
        {
            public Transform ReleasePoint;
            public Il2CppReferenceArray<Transform> VehicleSpots;
        }

        private static readonly Dictionary<string, Station> Stations = new();

        public static void Build(BuiltExpansion b)
        {
            var def = b.Definition.PoliceStation;
            if (def == null)
            {
                Core.Log.Msg($"police: '{b.Id}' has no police station; arrests there end at the main world's station");
                return;
            }
            if (def.VehicleSpots.Count == 0)
            {
                Core.Log.Error($"police: '{b.Id}' police station has no vehicle spots; arrests there end at the main world's station");
                return;
            }
            var root = new GameObject("Police Station").transform;
            root.SetParent(b.Root.transform, false);
            var release = Point(root, "Release Point", def.ReleasePoint);
            var spots = new Il2CppReferenceArray<Transform>(def.VehicleSpots.Count);
            for (int i = 0; i < def.VehicleSpots.Count; i++)
            {
                var spot = Point(root, $"Vehicle Spot {i}", def.VehicleSpots[i]);
                spot.position = Places.Ground(spot.position - spot.forward * Places.HalfCarLength);
                spots[i] = spot;
            }
            Stations[b.Id] = new Station { ReleasePoint = release, VehicleSpots = spots };
            Core.Log.Msg($"police: '{b.Id}' releases arrested players at {release.position} and parks their cars on {spots.Length} spots");
        }

        public static void Clear() => Stations.Clear();

        private static Transform Point(Transform parent, string name, LocalPoint p)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = p.Position;
            t.localRotation = Quaternion.Euler(0f, p.Yaw, 0f);
            return t;
        }

        /// <summary>The station of the expansion a position lies in, or null in the main world or an expansion without one.</summary>
        private static Station At(Vector3 position, out string expansionId)
        {
            expansionId = MapExpansionsApi.ExpansionAt(position);
            if (expansionId == null) return null;
            if (Stations.TryGetValue(expansionId, out var s) && s.ReleasePoint != null) return s;
            return null;
        }

        /// <summary>
        /// Points the map's station at an expansion's station until <see cref="Restore"/>. Nested calls (the RPC and its
        /// logic both being patched) keep the first swap.
        /// </summary>
        private static class Swap
        {
            private static int _depth;
            private static Transform _release;
            private static Il2CppReferenceArray<Transform> _spots;

            public static void Begin(Station s, bool vehicles)
            {
                var station = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.Map.Map>.Instance?.PoliceStation;
                if (station == null)
                {
                    Core.Log.Error("police: the map has no police station; the arrest is handled by the game");
                    return;
                }
                if (_depth++ > 0) return;
                _release = station.SpawnPoint;
                _spots = station.PossessedVehicleSpawnPoints;
                station.SpawnPoint = s.ReleasePoint;
                if (vehicles) station.PossessedVehicleSpawnPoints = s.VehicleSpots;
            }

            public static void End()
            {
                if (_depth == 0) return;
                if (--_depth > 0) return;
                var station = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.Map.Map>.Instance?.PoliceStation;
                if (station == null)
                {
                    Core.Log.Error("police: the map's police station is gone; its release point could not be restored");
                    return;
                }
                station.SpawnPoint = _release;
                station.PossessedVehicleSpawnPoints = _spots;
            }
        }

        private static void BeginRelease(Player player)
        {
            try
            {
                if (player == null || !player.IsArrested) return;
                var s = At(player.transform.position, out var id);
                if (s == null) return;
                Swap.Begin(s, vehicles: false);
                Core.Log.Msg($"police: '{player.PlayerName}' arrested in '{id}' is released at {s.ReleasePoint.position}");
            }
            catch (Exception e)
            {
                Core.Log.Error($"police: release in an expansion failed; the game's station is used: {e}");
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.Free_Client))]
        private static class FreeClientPatch
        {
            private static void Prefix(Player __instance) => BeginRelease(__instance);
            private static void Finalizer() => Swap.End();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.RpcLogic___Free_Client_2166136261))]
        private static class FreeClientLogicPatch
        {
            private static void Prefix(Player __instance) => BeginRelease(__instance);
            private static void Finalizer() => Swap.End();
        }

        /// <summary>The arrest notice parks the car taken from the local player; the player still stands where they were arrested.</summary>
        [HarmonyPatch(typeof(ArrestNoticeScreen), nameof(ArrestNoticeScreen.Open))]
        private static class ArrestNoticePatch
        {
            private static void Prefix()
            {
                try
                {
                    var player = Player.Local;
                    if (player == null) return;
                    var s = At(player.transform.position, out var id);
                    if (s == null) return;
                    Swap.Begin(s, vehicles: true);
                    Core.Log.Msg($"police: car taken in '{id}' is parked at its station");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"police: parking the taken car in an expansion failed; the game's station is used: {e}");
                }
            }

            private static void Finalizer() => Swap.End();
        }
    }
}
