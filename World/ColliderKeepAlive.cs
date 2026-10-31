using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Tools;
using MapExpansions.Lifecycle;
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
            int detached = TakeOut(colliders, null);
            // A collider that was only held by a world state must not return to its group when that state goes.
            if (StateHolds.Count > 0)
                foreach (var key in colliders) StateHolds.MarkPermanent(key);
            Core.Log.Msg($"keep-alive: {detached} switched-off colliders taken out of their collider groups");
            return detached;
        }

        private static int TakeOut(HashSet<IntPtr> colliders, Action<Collider, OptimizedColliderGroup> taken)
        {
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
                {
                    if (cols[i] == null || !colliders.Contains(cols[i].Pointer)) arr[k++] = cols[i];
                    else taken?.Invoke(cols[i], g);
                }
                detached += cols.Length - keep;
                g.Colliders = arr;
            }
            return detached;
        }

        private static readonly ColliderDetachLedger<Collider, OptimizedColliderGroup> StateHolds = new();

        /// <summary>
        /// Takes colliders a world state switched off out of their groups, in one scan for the whole transition, and
        /// notes the groups in <paramref name="lease"/> for <see cref="Restore"/>.
        /// </summary>
        internal static int DetachForState(HashSet<IntPtr> colliders, HashSet<IntPtr> lease)
        {
            var fresh = new HashSet<IntPtr>();
            foreach (var key in colliders)
                if (lease.Add(key) && StateHolds.Retain(key)) fresh.Add(key);
            if (fresh.Count == 0)
            {
                Core.Log.Msg($"keep-alive: {colliders.Count} state colliders need no new group scan");
                return 0;
            }
            int detached = TakeOut(fresh, (c, g) => StateHolds.Record(c.Pointer, c, g));
            Core.Log.Msg($"keep-alive: state took {detached} group memberships of {fresh.Count} colliders out in one scan");
            return detached;
        }

        /// <summary>
        /// Gives a state's colliders back to their groups once no other state holds them, one array rebuild per group.
        /// A restored collider follows its group's current distance switch.
        /// </summary>
        internal static int Restore(HashSet<IntPtr> lease)
        {
            if (lease.Count == 0) return 0;
            var back = new Dictionary<IntPtr, (OptimizedColliderGroup group, List<Collider> colliders)>();
            int gone = 0;
            foreach (var key in lease)
            {
                var entry = StateHolds.Release(key);
                if (entry == null) continue;
                foreach (var group in entry.Groups)
                {
                    if (group == null || entry.Collider == null) { gone++; continue; }
                    if (!back.TryGetValue(group.Pointer, out var batch))
                        back[group.Pointer] = batch = (group, new List<Collider>());
                    batch.colliders.Add(entry.Collider);
                }
            }
            lease.Clear();
            int restored = 0;
            foreach (var (group, added) in back.Values)
            {
                try
                {
                    var cols = group.Colliders;
                    int old = cols?.Length ?? 0;
                    var arr = new Il2CppReferenceArray<Collider>(old + added.Count);
                    for (int i = 0; i < old; i++) arr[i] = cols[i];
                    for (int i = 0; i < added.Count; i++) arr[old + i] = added[i];
                    group.Colliders = arr;
                    foreach (var c in added) c.enabled = group.collidersEnabled;
                    restored += added.Count;
                }
                catch (Exception e)
                {
                    Core.Log.Error($"keep-alive: group '{group.name}' did not take back {added.Count} colliders; they ignore its distance switch until the next world load: {e.Message}");
                }
            }
            Core.Log.Msg($"keep-alive: {restored} state colliders back in {back.Count} collider groups" + (gone > 0 ? $", {gone} memberships gone with their objects" : ""));
            return restored;
        }

        internal static void Reset() => StateHolds.Clear();
    }
}
