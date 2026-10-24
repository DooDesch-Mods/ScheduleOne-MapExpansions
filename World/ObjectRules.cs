using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>Applies the object rules and ground plates of a definition.</summary>
    internal static class ObjectRules
    {
        /// <summary>Switches off what each rule selects. Colliders that were switched off are added to <paramref name="offColliders"/>.</summary>
        public static void Apply(BuiltExpansion b, SceneIndex index, IEnumerable<ObjectRule> rules, HashSet<IntPtr> offColliders)
        {
            foreach (var rule in rules)
            {
                var part = index.Of(rule.Side, b);
                if (part == null)
                {
                    Core.Log.Error($"rules: '{b.Id}' {rule}: the {rule.Side} side is not indexed");
                    continue;
                }
                var area = rule.Area.size == Vector3.zero ? default : (rule.Side == WorldSide.Main ? rule.Area : b.Frame.ToWorld(rule.Area));
                int renderers = 0, colliders = 0, terrains = 0;
                bool hide = rule.Effect != ObjectEffect.NoCollision;
                bool noCollision = rule.Effect != ObjectEffect.Hide;
                if (hide)
                    foreach (var r in part.Renderers)
                    {
                        if (r == null || !r.enabled || !SceneIndex.MatchesSelfOrAncestor(r.transform, rule, area)) continue;
                        r.enabled = false;
                        renderers++;
                    }
                if (noCollision)
                    foreach (var c in part.Colliders)
                    {
                        if (c == null || !c.enabled || !SceneIndex.MatchesSelfOrAncestor(c.transform, rule, area)) continue;
                        c.enabled = false;
                        offColliders.Add(c.Pointer);
                        colliders++;
                    }
                foreach (var t in part.Terrains)
                {
                    if (t == null || !SceneIndex.MatchesSelfOrAncestor(t.transform, rule, area)) continue;
                    // A switched-off terrain also drops out of Terrain.activeTerrains, which the vehicle graph scan reads.
                    if (hide) t.enabled = false;
                    if (noCollision)
                    {
                        var tc = t.GetComponent<TerrainCollider>();
                        if (tc != null && tc.enabled)
                        {
                            tc.enabled = false;
                            offColliders.Add(tc.Pointer);
                        }
                    }
                    terrains++;
                }
                string line = $"rules: '{b.Id}' {rule}: {renderers} renderers, {colliders} colliders, {terrains} terrains";
                if (renderers + colliders + terrains == 0) Core.Log.Warning(line + "; the rule matched nothing");
                else Core.Log.Msg(line);
            }
        }

        /// <summary>
        /// A large plate of the source (often statically batched, so it cannot be rescaled) is replaced by a plain quad
        /// over the plate's local area, with the same material and height. Its collider becomes a thin box over the
        /// same area.
        /// </summary>
        public static void GroundPlates(BuiltExpansion b, SceneIndex index, HashSet<IntPtr> offColliders)
        {
            var part = index.Expansion(b.Id);
            foreach (var plate in b.Definition.GroundPlates)
            {
                var r = part?.Renderers.FirstOrDefault(x => x != null && x.name == plate.Name);
                if (r == null)
                {
                    Core.Log.Error($"ground: '{b.Id}' has no renderer '{plate.Name}'; the plate stays as it is");
                    continue;
                }
                float y = r.bounds.center.y;
                var a = plate.LocalArea;
                Vector3[] local =
                {
                    new(a.xMin, 0f, a.yMin), new(a.xMin, 0f, a.yMax), new(a.xMax, 0f, a.yMax), new(a.xMax, 0f, a.yMin),
                };
                var go = new GameObject("MapExpansions.Ground." + plate.Name);
                go.transform.SetParent(b.Root.transform, false);
                go.layer = r.gameObject.layer;
                var mesh = b.Own(new Mesh { name = "MapExpansions.Ground." + plate.Name });
                var verts = new Vector3[4];
                var uvs = new Vector2[4];
                for (int i = 0; i < 4; i++)
                {
                    var w = b.Frame.ToWorld(local[i]);
                    w.y = y;
                    verts[i] = go.transform.InverseTransformPoint(w);
                    uvs[i] = new Vector2(local[i].x, local[i].z) / 10f;
                }
                mesh.vertices = verts;
                mesh.uv = uvs;
                // Clockwise seen from above, so the face points up.
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = r.sharedMaterials;
                r.enabled = false;

                int colliders = 0;
                foreach (var c in r.GetComponents<Collider>())
                {
                    if (!c.enabled) continue;
                    c.enabled = false;
                    offColliders.Add(c.Pointer);
                    colliders++;
                }
                if (colliders > 0)
                {
                    var box = go.AddComponent<BoxCollider>();
                    var lb = mesh.bounds;
                    box.center = lb.center - new Vector3(0f, 0.25f, 0f);
                    box.size = new Vector3(lb.size.x, 0.5f, lb.size.z);
                }
                var gb = mr.bounds;
                Core.Log.Msg($"ground: '{b.Id}' '{plate.Name}' replaced by a plate over x {gb.min.x:0}..{gb.max.x:0} z {gb.min.z:0}..{gb.max.z:0} at y {y:0.00}, " +
                             $"material {(r.sharedMaterial != null ? r.sharedMaterial.name : "none")}, {colliders} colliders replaced");
            }
        }

        /// <summary>Expansion terrain cells under a main-world terrain become holes, so the main world wins where both lie.</summary>
        public static void YieldToMainTerrains(BuiltExpansion b, SceneIndex index, TerrainOps.HoleMask holes)
        {
            var mainRects = new List<Rect>();
            foreach (var t in index.Main.Terrains)
            {
                if (t == null || t.terrainData == null || !t.enabled) continue;
                Vector3 p = t.transform.position;
                Vector3 s = t.terrainData.size;
                mainRects.Add(Rect.MinMaxRect(p.x, p.z, p.x + s.x, p.z + s.z));
            }
            var part = index.Expansion(b.Id);
            int cells = 0;
            foreach (var t in part.Terrains)
            {
                if (t == null) continue;
                foreach (var rect in mainRects)
                    cells += holes.Cut(t, rect, (x, z, h) => rect.Contains(new Vector2(x, z)), "under a main-world terrain");
            }
            Core.Log.Msg($"terrain: '{b.Id}' {cells} cells give way to {mainRects.Count} main-world terrains");
        }

        /// <summary>Reports terrain parts that end up lower than the definition allows.</summary>
        public static void CheckHeights(BuiltExpansion b, SceneIndex index)
        {
            var part = index.Expansion(b.Id);
            foreach (var t in part.Terrains)
            {
                if (t == null || t.terrainData == null) continue;
                float min = t.transform.position.y;
                if (min < b.Definition.MinimumWorldHeight)
                    Core.Log.Warning($"place: '{b.Id}' terrain '{t.name}' base lies at y {min:0.00}, below the allowed {b.Definition.MinimumWorldHeight}");
            }
        }

        internal static void Destroy(Object o)
        {
            if (o != null) Object.Destroy(o);
        }
    }
}
