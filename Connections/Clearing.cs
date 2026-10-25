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
        /// <summary>
        /// How far terrain above or below a footprint still opens: on a low quality level the terrain is drawn metres off
        /// its heightmap, and a solid cell over a thin roof or under the road reaches into the tube as a spike.
        /// </summary>
        private const float LodSlack = 3f;

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
            // The approach counts for the objects the definition names; they are cut with the inside in one pass, so a
            // triangle that reaches into both is drawn again only outside both.
            bool approach = def.ClearApproach.Count > 0 && conn.Approach.Count > 0;
            var withApproach = new List<OrientedBox>(conn.Interior);
            if (approach)
            {
                withApproach.AddRange(conn.Approach);
                foreach (var box in conn.Approach) area.Encapsulate(box.WorldBounds);
            }

            bool OnApproach(Transform t)
            {
                foreach (var rule in def.ClearApproach)
                {
                    var ruleArea = rule.Area.size == Vector3.zero ? default : (rule.Side == WorldSide.Main ? rule.Area : b.Frame.ToWorld(rule.Area));
                    bool sideMatches = rule.Side == WorldSide.Main ? !all.Any(x => x.IsOurs(t)) : b.IsOurs(t);
                    if (sideMatches && SceneIndex.MatchesSelfOrAncestor(t, rule, ruleArea)) return true;
                }
                return false;
            }

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
            int off = 0, carved = 0, triangles = 0, kept = 0, failed = 0, approached = 0, inactive = 0;
            var parts = new List<SceneIndex.Part> { index.Main };
            foreach (var x in all)
            {
                var p = index.Expansion(x.Id);
                if (p != null) parts.Add(p);
            }
            var instanced = new List<Vector3>();
            foreach (var part in parts)
                foreach (var r in part.Renderers)
                {
                    if (r == null || !r.enabled) continue;
                    var rb = r.bounds;
                    if (!rb.Intersects(area)) continue;
                    bool inside = conn.Interior.Any(box => box.Intersects(rb));
                    bool onApproach = approach && conn.Approach.Any(box => box.Intersects(rb)) && OnApproach(r.transform);
                    if (!inside && !onApproach) continue;
                    if (Skip(r.transform)) { kept++; continue; }
                    // A switched-off object, such as the cable of a pole without a neighbour, stays as it is: the patch
                    // of its cut would be a visible object of its own.
                    if (!r.gameObject.activeInHierarchy) { inactive++; continue; }
                    var boxes = onApproach ? withApproach : conn.Interior;
                    if (onApproach) approached++;
                    // Instanced trees and the like share one mesh with thousands of copies and are drawn a second time
                    // by the instancing manager; both copies go.
                    if (InstancedObjects.IsInstanced(r))
                    {
                        r.enabled = false;
                        instanced.Add(r.transform.position);
                        off++;
                        continue;
                    }
                    if (conn.Interior.Any(box => box.Contains(rb.center)))
                    {
                        r.enabled = false;
                        off++;
                        Core.Log.Msg($"clear: '{b.Id}' {conn.Name}: '{MeshCarver.Path(r.transform)}' stands inside, switched off");
                        continue;
                    }
                    var mr = r.TryCast<MeshRenderer>();
                    if (mr == null) continue;
                    var outcome = MeshCarver.CarveRenderer(mr, boxes, index, b, out int cut);
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

            int instancedHidden = InstancedObjects.Hide(instanced, b, $"{b.Id} {conn.Name}");

            // Colliders, found by the physics scene so the collider groups the keep-alive switched on count too.
            var seen = new HashSet<IntPtr>();
            int colOff = 0, colCut = 0, colTris = 0, colKept = 0;
            foreach (var box in withApproach)
                foreach (var c in Physics.OverlapBox(box.Center, box.Half, box.Rotation, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c == null || !seen.Add(c.Pointer) || !c.enabled) continue;
                    if (c.TryCast<TerrainCollider>() != null) continue;
                    var cbounds = c.bounds;
                    bool onApproach = approach && conn.Approach.Any(x => x.Intersects(cbounds)) && OnApproach(c.transform);
                    if (!onApproach && !conn.Interior.Any(x => x.Intersects(cbounds))) continue;
                    if (Skip(c.transform)) { colKept++; continue; }
                    var mc = c.TryCast<MeshCollider>();
                    if (mc != null)
                    {
                        var outcome = MeshCarver.CarveCollider(mc, onApproach ? withApproach : conn.Interior, b, out int cut);
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
                    // A switched-off collider reports empty bounds.
                    var size = c.bounds.size;
                    c.enabled = false;
                    offColliders.Add(c.Pointer);
                    colOff++;
                    string line = $"clear: '{b.Id}' {conn.Name}: collider '{MeshCarver.Path(c.transform)}' ({c.GetIl2CppType().Name}, {size.x:0}x{size.y:0}x{size.z:0} m) switched off";
                    if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > 50f) Core.Log.Warning(line + "; it is large and may carry ground outside the connection");
                    else Core.Log.Msg(line);
                }

            // Terrain: holes in the footprint, trees out of the inside.
            var foot = conn.Footprint[0].WorldBounds;
            foreach (var box in conn.Footprint.Concat(conn.ApproachFootprint).Concat(conn.PortalFootprint)) foot.Encapsulate(box.WorldBounds);
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
                }, $"{b.Id} {conn.Name}", LodSlack);
                if (conn.ApproachFootprint.Count > 0)
                    cells += holes.Cut(t, footRect, (x, z, h) =>
                    {
                        var q = new Vector3(x, h, z);
                        foreach (var box in conn.ApproachFootprint)
                            if (box.Contains(q)) return true;
                        return false;
                    }, $"{b.Id} {conn.Name} approach", LodSlack, 0f);
                // No slack in the portal strips: a cell there that is not above the road stays, or the half of it
                // outside the portal opens beside the road.
                if (conn.PortalFootprint.Count > 0)
                    cells += holes.Cut(t, footRect, (x, z, h) =>
                    {
                        var q = new Vector3(x, h, z);
                        foreach (var box in conn.PortalFootprint)
                            if (box.Contains(q)) return true;
                        return false;
                    }, $"{b.Id} {conn.Name} portal", 0f, 0f);
                trees += TerrainOps.RemoveTrees(t, w => conn.Interior.Any(box => box.Contains(w + Vector3.up * 0.5f, 1.5f)), b, $"{b.Id} {conn.Name}");
            }

            Core.Log.Msg($"clear: '{b.Id}' {conn.Name} in {sw.ElapsedMilliseconds} ms: renderers {off} off, {carved} cut ({triangles} triangles), " +
                         $"{failed} off after a failed cut, {kept} kept, {inactive} inactive left as they are, {approached} also cut on the approach ({conn.Approach.Count} boxes, {def.ClearApproach.Count} rules), {instancedHidden}/{instanced.Count} instanced copies out of the draw; colliders {colOff} off, {colCut} cut ({colTris} triangles), {colKept} kept; " +
                         $"{cells} terrain cells marked as holes, {trees} trees removed");
        }
    }
}
