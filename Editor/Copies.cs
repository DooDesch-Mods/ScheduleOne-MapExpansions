using MapExpansions.World;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>The copies MapExpansions placed in the world, with the key the editor names each one by.</summary>
    internal static class Copies
    {
        /// <summary>expansion/owner/id - unique across all expansions, and the same key the layer file uses per expansion.</summary>
        internal static string Key(MainCopies.Placed p) => $"{p.Expansion.Id}/{p.LayerOwner}/{p.Built.Def.Id}";

        internal static int Count() => MainCopies.All.Count;

        internal static IEnumerable<MainCopies.Placed> All() => MainCopies.All.Where(p => p?.Built?.Handle != null);

        internal static MainCopies.Placed Find(string keyOrId)
        {
            if (string.IsNullOrEmpty(keyOrId)) return null;
            return All().FirstOrDefault(p => Key(p) == keyOrId)
                   ?? All().FirstOrDefault(p => p.Built.Def.Id == keyOrId);
        }

        /// <summary>Copies whose statically batched renderers were already reported, so the log names each once.</summary>
        private static readonly HashSet<string> BatchReported = new(StringComparer.Ordinal);

        /// <summary>
        /// World bounds of the copy's renderers; a 1 m box at its root when it draws nothing. A copy of a statically
        /// batched object keeps the batch's renderer, whose bounds belong to the whole batch, often from the world
        /// origin on. Such renderers are left out, and the copy's colliders stand in for them.
        /// </summary>
        internal static Bounds Bounds(MainCopies.Placed p)
        {
            var root = p.Built.Handle;
            bool any = false;
            int skipped = 0;
            var b = new Bounds(root.position, Vector3.one);
            string empty = null;
            void Add(Bounds x, Component from)
            {
                // A collider outside the physics scene, a particle system that has not played, or a renderer
                // without a mesh reports an empty box at the world origin; taking it in would stretch the copy's box
                // from there.
                if (x.size.sqrMagnitude < 1e-6f || x.center == Vector3.zero)
                {
                    empty ??= from.name;
                    return;
                }
                if (!any) { b = x; any = true; }
                else b.Encapsulate(x);
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled) continue;
                if (r.isPartOfStaticBatch)
                {
                    skipped++;
                    continue;
                }
                Add(r.bounds, r);
            }
            if (skipped > 0)
            {
                int colliders = 0;
                foreach (var c in root.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    Add(c.bounds, c);
                    colliders++;
                }
                if (BatchReported.Add(Key(p)))
                    Core.Log.Msg($"bounds: {Key(p)} has {skipped} statically batched renderers, whose box is the batch's; {colliders} colliders stand in for them");
            }
            if (empty != null && BatchReported.Add(Key(p) + "#empty"))
                Core.Log.Msg($"bounds: {Key(p)}: '{empty}' and maybe others report an empty box; left out");
            if (b.size.magnitude > 100f && BatchReported.Add(Key(p) + "#large"))
                Core.Log.Warning($"bounds: {Key(p)} spans {b.size} from {b.min} to {b.max}, root at {root.position}: {Farthest(root)}");
            return b;
        }

        /// <summary>The renderer or collider whose box reaches farthest from a copy's root, to name what stretches its bounds.</summary>
        private static string Farthest(Transform root)
        {
            Component best = null;
            var bestBox = new Bounds();
            float bestDist = -1f;
            void Try(Component c, Bounds x)
            {
                float d = Mathf.Max(Vector3.Distance(x.min, root.position), Vector3.Distance(x.max, root.position));
                if (d <= bestDist) return;
                best = c;
                bestBox = x;
                bestDist = d;
            }
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                if (r != null && r.enabled) Try(r, r.bounds);
            foreach (var c in root.GetComponentsInChildren<Collider>(false))
                if (c != null && c.enabled) Try(c, c.bounds);
            return best == null
                ? "no renderer or collider"
                : $"farthest is {best.GetIl2CppType().Name} on '{best.name}' ({bestBox.min} to {bestBox.max}, static batch {(best is Renderer rr && rr.isPartOfStaticBatch)})";
        }
    }
}
