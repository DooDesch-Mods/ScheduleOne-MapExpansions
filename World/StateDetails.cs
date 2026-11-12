using MapExpansions.Lifecycle;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Grass and bush tufts a world state takes out of the main terrain, such as under a new floor slab. The tufts are
    /// drawn from the terrain's detail map, not from objects, so no other rule reaches them.
    /// </summary>
    internal static class StateDetails
    {
        private static readonly List<Lease> Active = new();

        internal sealed class Lease : IDisposable
        {
            internal string Owner;
            internal readonly List<DetailLayers.Cleared> Cleared = new();

            /// <summary>Puts the tufts back, last cleared first, so overlapping rectangles end as they started.</summary>
            public void Dispose()
            {
                var errors = new List<string>();
                for (int i = Cleared.Count - 1; i >= 0; i--)
                {
                    var c = Cleared[i];
                    if (DetailLayers.Restore(c, out string why))
                        Core.Log.Msg($"terrain details: {Owner}: {c.Tufts} tufts back on '{c.Terrain}'");
                    else errors.Add($"'{c.Terrain}': {why}");
                    Cleared.RemoveAt(i);
                }
                Active.Remove(this);
                if (errors.Count > 0) throw new InvalidOperationException($"terrain details: {Owner}: not all tufts came back ({string.Join("; ", errors)})");
            }
        }

        /// <summary>Clears the tufts of every main terrain the rule's name selects inside its area.</summary>
        internal static Lease Apply(BuiltExpansion b, SceneIndex index, ObjectRule rule, string owner)
        {
            if (index == null || rule.Area.size == Vector3.zero) throw new InvalidOperationException($"{owner}: clearing details needs a scene index and an area");
            var part = index.Of(WorldSide.Main, b) ?? throw new InvalidOperationException($"{owner}: clearing details has no main-world index");
            var area = rule.Area;
            var rect = Rect.MinMaxRect(area.min.x, area.min.z, area.max.x, area.max.z);
            var lease = new Lease { Owner = owner };
            int matched = 0;
            try
            {
                foreach (var t in part.Terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    if (!SceneIndex.Matches(t.transform, rule.Name ?? "", rule.Parent)) continue;
                    var tp = t.transform.position;
                    var ts = t.terrainData.size;
                    if (!rect.Overlaps(Rect.MinMaxRect(tp.x, tp.z, tp.x + ts.x, tp.z + ts.z))) continue;
                    matched++;
                    var cleared = DetailLayers.Clear(t, rect, out string detail);
                    if (cleared == null) throw new InvalidOperationException($"{owner}: clearing details on '{t.name}' failed: {detail}");
                    lease.Cleared.Add(cleared);
                    Core.Log.Msg($"terrain details: {owner}: {cleared.Tufts} tufts cleared on '{t.name}' ({detail})");
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            if (matched == 0) throw new InvalidOperationException($"{owner}: no main terrain named '{rule.Name}' lies under the area");
            Active.Add(lease);
            return lease;
        }

        /// <summary>
        /// Before the world goes: the terrain data can outlive the scene in memory, so the tufts go back where it still
        /// exists rather than staying cleared for the next save that loads.
        /// </summary>
        internal static void Reset()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var lease = Active[i];
                try { lease.Dispose(); }
                catch (Exception e) { Core.Log.Warning(e.Message); }
            }
            Active.Clear();
        }
    }
}
