using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;

namespace MapExpansions.Connections
{
    /// <summary>
    /// Clears the inside of a built connection in both worlds: renderers and colliders that stand in it are switched
    /// off or cut, terrain cells in its footprint become holes and trees in it go.
    /// </summary>
    internal static class Clearing
    {
        public static void Clear(BuiltExpansion b, BuiltConnection conn, SceneIndex index, IReadOnlyList<BuiltExpansion> all,
                                 TerrainOps.HoleMask holes, HashSet<IntPtr> offColliders)
        {
            if (conn.Interior.Count == 0)
            {
                Core.Log.Msg($"clear: '{b.Id}' {conn.Name} builds nothing; nothing to clear");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var def = conn.Definition;
            var area = conn.Interior[0].WorldBounds;
            foreach (var box in conn.Interior) area.Encapsulate(box.WorldBounds);

            bool Skip(Transform t)
            {
                if (t == null || t.IsChildOf(conn.Root.transform)) return true;
                // Everything the framework made (other connections, ground plates) carries the prefix.
                for (var p = t; p != null; p = p.parent)
                    if (p.name.StartsWith("MapExpansions.", StringComparison.Ordinal) && !all.Any(x => x.Root != null && x.Root.transform == p)) return true;
                foreach (var rule in def.Keep)
                {
                    var keepArea = rule.Area.size == Vector3.zero ? default : (rule.Side == WorldSide.Main ? rule.Area : b.Frame.ToWorld(rule.Area));
                    bool sideMatches = rule.Side == WorldSide.Main ? !all.Any(x => x.IsOurs(t)) : b.IsOurs(t);
                    if (sideMatches && SceneIndex.MatchesSelfOrAncestor(t, rule, keepArea)) return true;
                }
                return false;
            }

            // Renderers of every world.
            int off = 0, carved = 0, triangles = 0, kept = 0, failed = 0;
            var parts = new List<SceneIndex.Part> { index.Main };
            foreach (var x in all)
            {
                var p = index.Expansion(x.Id);
                if (p != null) parts.Add(p);
            }
            foreach (var part in parts)
                foreach (var r in part.Renderers)
                {
                    if (r == null || !r.enabled) continue;
                    var rb = r.bounds;
                    if (!rb.Intersects(area) || !conn.Interior.Any(box => box.Intersects(rb))) continue;
                    if (Skip(r.transform)) { kept++; continue; }
                    if (conn.Interior.Any(box => box.Contains(rb.center)))
                    {
                        r.enabled = false;
                        off++;
                        Core.Log.Msg($"clear: '{b.Id}' {conn.Name}: '{MeshCarver.Path(r.transform)}' stands inside, switched off");
                        continue;
                    }
                    var mr = r.TryCast<MeshRenderer>();
                    if (mr == null) continue;
                    var outcome = MeshCarver.CarveRenderer(mr, conn.Interior, index, b, out int cut);
                    if (outcome == MeshCarver.Outcome.Carved)
                    {
                        carved++;
                        triangles += cut;
                        Core.Log.Msg($"clear: '{b.Id}' {conn.Name}: '{MeshCarver.Path(r.transform)}' {cut} triangles cut");
                    }
                    else if (outcome == MeshCarver.Outcome.Failed)
                    {
                        r.enabled = false;
                        failed++;
                    }
                }

            // Colliders, found by the physics scene so the collider groups the keep-alive switched on count too.
            var seen = new HashSet<IntPtr>();
            int colOff = 0, colCut = 0, colTris = 0, colKept = 0;
            foreach (var box in conn.Interior)
                foreach (var c in Physics.OverlapBox(box.Center, box.Half, box.Rotation, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c == null || !seen.Add(c.Pointer) || !c.enabled) continue;
                    if (c.TryCast<TerrainCollider>() != null) continue;
                    if (Skip(c.transform)) { colKept++; continue; }
                    var mc = c.TryCast<MeshCollider>();
                    if (mc != null)
                    {
                        var outcome = MeshCarver.CarveCollider(mc, conn.Interior, b, out int cut);
                        if (outcome == MeshCarver.Outcome.Carved)
                        {
                            colCut++;
                            colTris += cut;
                            Core.Log.Msg($"clear: '{b.Id}' {conn.Name}: collider '{MeshCarver.Path(c.transform)}' {cut} triangles cut");
                            continue;
                        }
                        if (outcome == MeshCarver.Outcome.Untouched) continue;
                    }
                    else
                    {
                        var cb = c.bounds;
                        if (!conn.Blocking.Any(x => x.Intersects(cb))) continue;
                    }
                    c.enabled = false;
                    offColliders.Add(c.Pointer);
                    colOff++;
                    var size = c.bounds.size;
                    string line = $"clear: '{b.Id}' {conn.Name}: collider '{MeshCarver.Path(c.transform)}' ({c.GetIl2CppType().Name}, {size.x:0}x{size.y:0}x{size.z:0} m) switched off";
                    if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > 50f) Core.Log.Warning(line + "; it is large and may carry ground outside the connection");
                    else Core.Log.Msg(line);
                }

            // Terrain: holes in the footprint, trees out of the inside.
            var foot = conn.Footprint[0].WorldBounds;
            foreach (var box in conn.Footprint) foot.Encapsulate(box.WorldBounds);
            var footRect = Rect.MinMaxRect(foot.min.x, foot.min.z, foot.max.x, foot.max.z);
            int cells = 0, trees = 0;
            var terrains = new List<Terrain>(index.Main.Terrains);
            foreach (var p in parts.Skip(1)) terrains.AddRange(p.Terrains);
            foreach (var t in terrains)
            {
                if (t == null || !t.enabled || t.terrainData == null) continue;
                cells += holes.Cut(t, footRect, (x, z, h) =>
                {
                    var q = new Vector3(x, h, z);
                    foreach (var box in conn.Footprint)
                        if (box.Contains(q)) return true;
                    return false;
                }, $"{b.Id} {conn.Name}");
                trees += TerrainOps.RemoveTrees(t, w => conn.Interior.Any(box => box.Contains(w + Vector3.up * 0.5f, 1.5f)), b, $"{b.Id} {conn.Name}");
            }

            Core.Log.Msg($"clear: '{b.Id}' {conn.Name} in {sw.ElapsedMilliseconds} ms: renderers {off} off, {carved} cut ({triangles} triangles), " +
                         $"{failed} off after a failed cut, {kept} kept; colliders {colOff} off, {colCut} cut ({colTris} triangles), {colKept} kept; " +
                         $"{cells} terrain cells marked as holes, {trees} trees removed");
        }
    }
}
