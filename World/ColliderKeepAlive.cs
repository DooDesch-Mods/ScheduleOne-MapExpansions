using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Tools;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// The main world switches the colliders of many objects off while the local player is more than 30 m away
    /// (OptimizedColliderGroup). A road that vehicles drive and a graph that is scanned need those colliders
    /// whatever the player's position, so the groups along a connection get an unlimited range.
    /// </summary>
    internal static class ColliderKeepAlive
    {
        private const float Unlimited = 1e6f;

        /// <summary>Groups whose colliders come within <paramref name="radius"/> of a point get an unlimited range.</summary>
        public static int KeepNear(IReadOnlyList<Vector3> points, float radius, string what)
        {
            float sq = radius * radius;
            int groups = 0, colliders = 0;
            foreach (var g in Object.FindObjectsOfType<OptimizedColliderGroup>(true))
            {
                if (g == null) continue;
                var cols = g.Colliders;
                if (cols == null || cols.Length == 0) continue;
                bool near = false;
                for (int i = 0; i < cols.Length && !near; i++)
                {
                    var c = cols[i];
                    if (c == null) continue;
                    var p = c.transform.position;
                    foreach (var q in points)
                    {
                        float dx = p.x - q.x, dz = p.z - q.z;
                        if (dx * dx + dz * dz < sq) { near = true; break; }
                    }
                }
                if (!near) continue;
                try
                {
                    g.ColliderEnableMaxDistance = Unlimited;
                    g.sqrColliderEnableMaxDistance = Unlimited * Unlimited;
                    g.SetCollidersEnabled(true);
                    groups++;
                    colliders += cols.Length;
                }
                catch (Exception e)
                {
                    Core.Log.Error($"keep-alive: group '{g.name}' could not be kept on; its colliders follow the player distance: {e.Message}");
                }
            }
            Core.Log.Msg($"keep-alive: {groups} collider groups ({colliders} colliders) along {what} stay on at any distance");
            return groups;
        }

        /// <summary>
        /// Takes a collider out of every group, so a collider the framework switched off is not switched on again
        /// when the group's distance check changes.
        /// </summary>
        public static int Detach(HashSet<IntPtr> colliders)
        {
            if (colliders.Count == 0) return 0;
            int detached = 0;
            foreach (var g in Object.FindObjectsOfType<OptimizedColliderGroup>(true))
            {
                var cols = g?.Colliders;
                if (cols == null) continue;
                int keep = 0;
                for (int i = 0; i < cols.Length; i++)
                    if (cols[i] == null || !colliders.Contains(cols[i].Pointer)) keep++;
                if (keep == cols.Length) continue;
                var arr = new Il2CppReferenceArray<Collider>(keep);
                for (int i = 0, k = 0; i < cols.Length; i++)
                    if (cols[i] == null || !colliders.Contains(cols[i].Pointer)) arr[k++] = cols[i];
                detached += cols.Length - keep;
                g.Colliders = arr;
            }
            Core.Log.Msg($"keep-alive: {detached} switched-off colliders taken out of their collider groups");
            return detached;
        }
    }
}
