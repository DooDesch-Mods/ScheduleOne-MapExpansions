#if DEBUG
using System.Collections;
using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using MapExpansions.Navigation;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;
using static MapExpansions.Debug.DevConsole;

namespace MapExpansions.Debug
{
    /// <summary>
    /// NPC navigation tests, all under <c>mxnav</c>:
    ///
    /// mxnav sample x y z                   nearest NavMesh point per agent type
    /// mxnav path ax ay az bx by bz         path status and length for the Humanoid agent
    /// mxnav walk id|nearest x y z          walk an NPC there with its schedule off, watching for warps
    /// </summary>
    internal static class NavTest
    {
        internal static NPC Watched;
        internal static int WatchedWarps;

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "sample" when parts.Length > 4: Sample(V(parts, 2)); break;
                case "path" when parts.Length > 7: Path(V(parts, 2), V(parts, 5)); break;
                case "walk" when parts.Length > 5: Walk(parts[2], V(parts, 3)); break;
                default:
                    Core.Log.Warning("[dev] mxnav sample|path|walk (see NavTest docs)");
                    break;
            }
        }

        private static void Sample(Vector3 pos)
        {
            foreach (int id in NpcNavigation.AgentTypes())
            {
                var filter = new NavMeshQueryFilter { agentTypeID = id, areaMask = NavMesh.AllAreas };
                bool ok = NavMesh.SamplePosition(pos, out NavMeshHit hit, 20f, filter);
                Core.Log.Msg($"[dev] mxnav sample {pos} {NavMesh.GetSettingsNameFromID(id)}: {(ok ? $"{hit.position} ({hit.distance:0.0} m)" : "none within 20 m")}");
            }
        }

        private static void Path(Vector3 a, Vector3 b)
        {
            var filter = new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };
            var path = new NavMeshPath();
            var t = System.Diagnostics.Stopwatch.StartNew();
            bool ok = NavMesh.CalculatePath(a, b, filter, path);
            var corners = path.corners;
            float len = 0f;
            for (int i = 1; i < corners.Length; i++) len += Vector3.Distance(corners[i - 1], corners[i]);
            string last = corners.Length > 0 ? corners[corners.Length - 1].ToString() : "-";
            Core.Log.Msg($"[dev] mxnav path {a} -> {b}: ok={ok} status={path.status} corners={corners.Length} length={len:0} m end={last} ({t.ElapsedMilliseconds} ms)");
        }

        private static void Walk(string who, Vector3 target)
        {
            NPC npc = null;
            var me = Player.Local;
            float best = float.MaxValue;
            var reg = NPCManager.NPCRegistry;
            for (int i = 0; i < reg.Count; i++)
            {
                var n = reg[i];
                if (n == null || n.Movement == null || !n.gameObject.activeInHierarchy) continue;
                if (who != "nearest")
                {
                    if (string.Equals(n.ID, who, StringComparison.OrdinalIgnoreCase)) { npc = n; break; }
                    continue;
                }
                if (me == null) break;
                float d = Vector3.Distance(n.transform.position, me.transform.position);
                if (d < best) { best = d; npc = n; }
            }
            if (npc == null)
            {
                Core.Log.Warning($"[dev] mxnav walk: no NPC '{who}'");
                return;
            }
            var sched = npc.GetComponentInChildren<NPCScheduleManager>(true);
            if (sched != null) sched.DisableSchedule();
            else Core.Log.Warning($"[dev] mxnav walk: {npc.ID} has no schedule manager; its behaviour may override the walk");
            Watched = npc;
            WatchedWarps = 0;
            npc.Movement.SetDestination(target);
            Core.Log.Msg($"[dev] mxnav walk: {npc.ID} from {npc.transform.position} to {target}, schedule off");
            MelonCoroutines.Start(Watch(npc, target));
        }

        private static IEnumerator Watch(NPC npc, Vector3 target)
        {
            Vector3 last = npc.transform.position;
            float start = Time.time, lastLog = 0f, maxStep = 0f, travelled = 0f;
            while (npc != null && Watched == npc && Time.time - start < 600f)
            {
                yield return null;
                if (npc == null) break;
                Vector3 p = npc.transform.position;
                float step = Vector3.Distance(p, last);
                travelled += step;
                if (step > maxStep) maxStep = step;
                if (step > 3f) Core.Log.Warning($"[dev] mxnav walk: {npc.ID} jumped {step:0.0} m in one frame at {last} -> {p}");
                last = p;
                var agent = npc.Movement._agent;
                if (Time.time - lastLog > 3f)
                {
                    lastLog = Time.time;
                    Core.Log.Msg($"[dev] mxnav walk: {npc.ID} at {p}, {Vector3.Distance(p, target):0} m left, " +
                                 $"onLink={agent?.isOnOffMeshLink} path={agent?.pathStatus} hasPath={agent?.hasPath} moving={npc.Movement.IsMoving}");
                }
                if (Vector3.Distance(p, target) < 2f)
                {
                    Core.Log.Msg($"[dev] mxnav walk: {npc.ID} arrived after {Time.time - start:0} s, {travelled:0} m, largest step {maxStep:0.00} m, warps {WatchedWarps}");
                    yield break;
                }
            }
            Core.Log.Warning($"[dev] mxnav walk: watch ended without arrival after {Time.time - start:0} s, {travelled:0} m, largest step {maxStep:0.00} m, warps {WatchedWarps}");
        }
    }

    /// <summary>Reports every warp of the NPC that mxnav walk watches.</summary>
    [HarmonyPatch]
    internal static class WatchWarpPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(NPCMovement)))
                if (m.Name == "Warp") yield return m;
        }

        private static void Prefix(NPCMovement __instance)
        {
            var w = NavTest.Watched;
            if (w == null || __instance == null || w.Movement == null || w.Movement.Pointer != __instance.Pointer) return;
            NavTest.WatchedWarps++;
            Core.Log.Warning($"[dev] mxnav walk: {w.ID} warped (NPCMovement.Warp) at {__instance.transform.position}");
        }
    }
}
#endif
