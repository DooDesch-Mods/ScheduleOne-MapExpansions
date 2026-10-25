#if DEBUG
using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Schedules;
using UnityEngine;
using UnityEngine.AI;

namespace MapExpansions.Debug
{
    /// <summary>
    /// The game warns "NPC walk to location not successful" without naming the NPC or the place. This names both, once
    /// per NPC and destination and then every 100th repeat, with whether the destination lies on the NavMesh.
    /// </summary>
    internal static class WalkFailures
    {
        private static readonly Dictionary<string, int> Counts = new();

        [HarmonyPatch(typeof(NPCSignal_WalkToLocation), nameof(NPCSignal_WalkToLocation.WalkCallback))]
        private static class WalkCallbackPatch
        {
            private static void Prefix(NPCSignal_WalkToLocation __instance, NPCMovement.WalkResult result)
            {
                try
                {
                    if (result == NPCMovement.WalkResult.Success || !__instance.IsActive) return;
                    var npc = __instance.npc;
                    var dest = __instance.Destination;
                    string id = npc != null ? npc.ID : "?";
                    Vector3 to = dest != null ? dest.position : Vector3.zero;
                    string key = $"{id}|{to}";
                    Counts.TryGetValue(key, out int n);
                    Counts[key] = ++n;
                    if (n != 1 && n % 100 != 0) return;
                    Vector3 from = npc != null ? npc.transform.position : Vector3.zero;
                    string onMesh = NavMesh.SamplePosition(to, out var hit, 2f, NavMesh.AllAreas)
                        ? $"NavMesh {Vector3.Distance(hit.position, to):F2} m away"
                        : "no NavMesh within 2 m";
                    Core.Log.Warning($"[dev] walk failed x{n}: '{id}' {result} from {from} to '{dest?.name}' {to} ({onMesh}), " +
                                     $"{Vector3.Distance(from, to):F1} m");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"[dev] walk failure report failed: {e}");
                }
            }
        }
    }
}
#endif
