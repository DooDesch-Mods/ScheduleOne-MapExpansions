#if DEBUG
using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.NPCs;
using UnityEngine;
using UnityEngine.AI;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Traces how employees re-plan a walk (<c>mxreach trace on|off</c>). NPCMovement.UpdateDestination calls
    /// SetDestination again with interruptExistingCallback false each time the agent stops short of its destination; an
    /// employee that keeps doing that for one destination walks in a loop. Each re-plan after the first logs the foot
    /// position, the point the agent was sent to, whether the NPC's own path cache supplied the path, and the path.
    /// </summary>
    internal static class RepathTrace
    {
        internal static bool On;
        private const int LoopAt = 50;

        private sealed class Walk
        {
            internal Vector3 Destination;
            internal int Replans;
            internal float Started;
        }

        private static readonly Dictionary<IntPtr, Walk> Walks = new();

        internal static void Toggle(bool on)
        {
            On = on;
            Walks.Clear();
            Core.Log.Msg($"[dev] mxreach trace: {(on ? "re-plans of employee walks are logged" : "off")}");
        }

        [HarmonyPatch(typeof(NPCMovement), nameof(NPCMovement.SetDestination),
            new[] { typeof(Vector3), typeof(Il2CppSystem.Action<NPCMovement.WalkResult>), typeof(bool), typeof(float), typeof(float) })]
        private static class SetDestinationPatch
        {
            private static void Prefix(NPCMovement __instance, Vector3 pos, bool interruptExistingCallback, float cacheMaxDistSqr, out string __state)
            {
                __state = null;
                if (!On) return;
                try
                {
                    var npc = __instance._npc;
                    if (npc == null || npc.TryCast<Employee>() == null) return;
                    var key = __instance.Pointer;
                    if (interruptExistingCallback || !Walks.TryGetValue(key, out var walk) || (walk.Destination - pos).sqrMagnitude > 0.01f)
                    {
                        Walks[key] = new Walk { Destination = pos, Started = Time.time };
                        return;
                    }
                    walk.Replans++;
                    var foot = __instance.FootPosition;
                    bool reach = __instance.GetClosestReachablePoint(pos, out var closest);
                    bool cached = reach && __instance._pathCache?.GetPath(__instance._agent.transform.position, closest, cacheMaxDistSqr) != null;
                    __state = $"{__instance.gameObject.name} re-plan {walk.Replans} after {Time.time - walk.Started:0.0} s toward {pos} from foot {foot} " +
                              $"({Vector3.Distance(foot, pos):0.00} m), closest point {(reach ? closest.ToString() : "none")} " +
                              $"({(reach ? Vector3.Distance(foot, closest).ToString("0.00") : "-")} m), path cache {(cached ? "HIT" : "miss")} " +
                              $"({__instance._pathCache?.Paths?.Count ?? -1} paths)";
                }
                catch (Exception e)
                {
                    Core.Log.Warning($"[dev] mxreach trace: prefix failed for {__instance?.gameObject?.name}: {e.Message}");
                }
            }

            private static void Postfix(NPCMovement __instance, string __state)
            {
                if (__state == null) return;
                try
                {
                    var agent = __instance._agent;
                    var corners = agent.path?.corners;
                    string line = $"{__state}, sent to {agent.destination}, path {agent.pathStatus} {corners?.Length ?? 0} corners" +
                                  (corners != null && corners.Length > 0 ? $" ending {corners[corners.Length - 1]}" : "");
                    var walk = Walks[__instance.Pointer];
                    if (walk.Replans == LoopAt) Core.Log.Warning($"[dev] mxreach trace: LOOP {line}");
                    else if (walk.Replans < LoopAt || walk.Replans % 50 == 0) Core.Log.Msg($"[dev] mxreach trace: {line}");
                }
                catch (Exception e)
                {
                    Core.Log.Warning($"[dev] mxreach trace: postfix failed for {__instance?.gameObject?.name}: {e.Message}");
                }
            }
        }

        /// <summary>One line per finished harvest, since a harvest takes about a second and a sampled behaviour misses it.</summary>
        [HarmonyPatch(typeof(Il2CppScheduleOne.NPCs.Behaviour.HarvestPotBehaviour), nameof(Il2CppScheduleOne.NPCs.Behaviour.HarvestPotBehaviour.OnActionSuccess))]
        private static class HarvestPatch
        {
            private static void Postfix(Il2CppScheduleOne.NPCs.Behaviour.HarvestPotBehaviour __instance)
            {
                if (!On) return;
                try
                {
                    Core.Log.Msg($"[dev] mxreach trace: harvest by {__instance._botanist?.gameObject.name} at pot {__instance._pot?.transform.position}");
                }
                catch (Exception e)
                {
                    Core.Log.Warning($"[dev] mxreach trace: harvest postfix failed: {e.Message}");
                }
            }
        }
    }
}
#endif
