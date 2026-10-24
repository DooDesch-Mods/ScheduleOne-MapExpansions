#if DEBUG
using System.Collections;
using System.Globalization;
using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using MapExpansions.Source;
using MelonLoader;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Navigation spike commands, all under <c>mxnav</c>:
    ///
    /// mxnav add [dx dy dz]                 add the captured NavMesh data at an offset (default: the placed copy's)
    /// mxnav remove                         remove the added data, the links and the baked Employee surface
    /// mxnav extremes                       south edge of the main NavMesh and north edge of the expansion
    /// mxnav sample x y z                   nearest NavMesh point per agent type
    /// mxnav path ax ay az bx by bz         path status and length for the Humanoid agent
    /// mxnav link ax ay az bx by bz [w]     a seam link for every agent type
    /// mxnav walk id|nearest x y z          walk an NPC there with its schedule off, watching for warps
    /// mxnav bake cx cy cz sx sy sz         bake an Employee NavMesh over a box
    /// </summary>
    internal static class NavSpike
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly List<NavMeshDataInstance> Added = new();
        private static GameObject _employeeSurface;
        internal static NPC Watched;
        internal static int WatchedWarps;

        private static float F(string s) => float.Parse(s, Ci);
        private static Vector3 V(string[] p, int i) => new(F(p[i]), F(p[i + 1]), F(p[i + 2]));

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "add": Add(parts.Length > 4 ? V(parts, 2) : DevConsole.PlacedOffset); break;
                case "remove": Remove(); break;
                case "extremes": Extremes(); break;
                case "sample" when parts.Length > 4: Sample(V(parts, 2)); break;
                case "path" when parts.Length > 7: Path(V(parts, 2), V(parts, 5)); break;
                case "link" when parts.Length > 7:
                    NavLinks.Add(V(parts, 2), V(parts, 5), parts.Length > 8 ? F(parts[8]) : 4f, AgentTypes());
                    break;
                case "walk" when parts.Length > 5: Walk(parts[2], V(parts, 3)); break;
                case "bake" when parts.Length > 7: Bake(V(parts, 2), V(parts, 5)); break;
                default:
                    Core.Log.Warning("[dev] mxnav add|remove|extremes|sample|path|link|walk|bake (see DevConsole docs)");
                    break;
            }
        }

        private static IEnumerable<int> AgentTypes()
        {
            int n = NavMesh.GetSettingsCount();
            for (int i = 0; i < n; i++) yield return NavMesh.GetSettingsByIndex(i).agentTypeID;
        }

        private static int AgentId(string name)
        {
            foreach (int id in AgentTypes())
                if (NavMesh.GetSettingsNameFromID(id) == name) return id;
            return int.MinValue;
        }

        private static void Add(Vector3 offset)
        {
            if (Added.Count > 0) Remove();
            if (SourceCapture.NavMeshes.Count == 0)
            {
                Core.Log.Error("[dev] mxnav add: no captured NavMesh data");
                return;
            }
            var t = System.Diagnostics.Stopwatch.StartNew();
            foreach (var n in SourceCapture.NavMeshes)
            {
                if (n.Data == null)
                {
                    Core.Log.Error($"[dev] mxnav add: data of '{n.Name}' was unloaded");
                    continue;
                }
                var rot = DevConsole.PlacedRotation;
                var inst = NavMesh.AddNavMeshData(n.Data, rot * n.Position + offset, rot * n.Rotation);
                if (!inst.valid)
                {
                    Core.Log.Error($"[dev] mxnav add: AddNavMeshData failed for '{n.Name}'");
                    continue;
                }
                Added.Add(inst);
            }
            Core.Log.Msg($"[dev] mxnav add: {Added.Count}/{SourceCapture.NavMeshes.Count} data added at offset {offset} in {t.ElapsedMilliseconds} ms");
        }

        private static void Remove()
        {
            foreach (var inst in Added) inst.Remove();
            Core.Log.Msg($"[dev] mxnav remove: {Added.Count} data removed");
            Added.Clear();
            if (NavLinks.Count > 0) NavLinks.Clear();
            if (_employeeSurface != null)
            {
                Object.Destroy(_employeeSurface);
                _employeeSurface = null;
                Core.Log.Msg("[dev] mxnav remove: Employee surface removed");
            }
        }

        /// <summary>
        /// Scans columns of NavMesh samples (Humanoid) for the south edge of the main world and the north edge of the
        /// expansion. NavMesh.CalculateTriangulation is stripped from the game build.
        /// </summary>
        private static void Extremes()
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            var filter = new NavMeshQueryFilter { agentTypeID = 0, areaMask = NavMesh.AllAreas };
            float split = DevConsole.PlacedOffset.z + 344f + 20f;
            var main = new List<Vector3>();
            var exp = new List<Vector3>();
            int samples = 0;
            for (float x = -260f; x <= 350f; x += 5f)
            {
                // Main world: from the split northwards, the first hit is the column's south-most point.
                for (float z = split; z <= 0f; z += 2f)
                {
                    samples++;
                    if (FindColumn(x, z, filter, out NavMeshHit h) && h.position.z > split) { main.Add(h.position); break; }
                }
                // Expansion: from the split southwards, the first hit is the column's north-most point.
                for (float z = split; z >= split - 400f; z -= 2f)
                {
                    samples++;
                    if (FindColumn(x, z, filter, out NavMeshHit h) && h.position.z < split) { exp.Add(h.position); break; }
                }
            }
            Core.Log.Msg($"[dev] mxnav extremes: {samples} samples in {t.ElapsedMilliseconds} ms; main columns {main.Count}, expansion columns {exp.Count} (split z {split})");
            foreach (var p in main.OrderBy(p => p.z).Take(8)) Core.Log.Msg($"[dev]   main south {p}");
            foreach (var p in exp.OrderByDescending(p => p.z).Take(8)) Core.Log.Msg($"[dev]   expansion north {p}");
        }

        private static bool FindColumn(float x, float z, NavMeshQueryFilter filter, out NavMeshHit hit)
        {
            for (float y = -20f; y <= 80f; y += 10f)
                if (NavMesh.SamplePosition(new Vector3(x, y, z), out hit, 1.5f, filter)) return true;
            hit = default;
            return false;
        }

        private static void Sample(Vector3 pos)
        {
            foreach (int id in AgentTypes())
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

        private static void Bake(Vector3 center, Vector3 size)
        {
            int employee = AgentId("Employee");
            if (employee == int.MinValue)
            {
                Core.Log.Error("[dev] mxnav bake: no 'Employee' agent type in the NavMesh settings");
                return;
            }
            if (_employeeSurface != null) Object.Destroy(_employeeSurface);
            _employeeSurface = new GameObject("MapExpansions.EmployeeNav");
            _employeeSurface.transform.position = center;
            _employeeSurface.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<NavMeshSurface>());
            var s = _employeeSurface.GetComponent<NavMeshSurface>();
            s.agentTypeID = employee;
            s.collectObjects = CollectObjects.Volume;
            s.center = Vector3.zero;
            s.size = size;
            s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            var t = System.Diagnostics.Stopwatch.StartNew();
            s.BuildNavMesh();
            var data = s.navMeshData;
            Core.Log.Msg($"[dev] mxnav bake: Employee ({employee}) over {size} at {center} in {t.ElapsedMilliseconds} ms; data {(data != null ? "built" : "missing")}");
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
            var w = NavSpike.Watched;
            if (w == null || __instance == null || w.Movement == null || w.Movement.Pointer != __instance.Pointer) return;
            NavSpike.WatchedWarps++;
            Core.Log.Warning($"[dev] mxnav walk: {w.ID} warped (NPCMovement.Warp) at {__instance.transform.position}");
        }
    }
}
#endif
