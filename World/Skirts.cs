using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Walls hung from the open lower edges of a mesh. An edge is open when one triangle uses it; it is a lower edge
    /// when that triangle rises above it where it stands over it, or falls away from it steeply. Vertices are welded by
    /// position first, so seams of split normals or UVs do not count as open. Each wall is drawn from both sides in world
    /// space with the material of its edge's triangle.
    /// </summary>
    internal static class Skirts
    {
        private const string Name = "MapExpansions skirt ";

        public static void Apply(BuiltExpansion b, SceneIndex.Part part, ObjectRule rule, Bounds area, float bottom)
        {
            int renderers = 0, edges = 0, steepEdges = 0, failed = 0;
            var added = new List<Renderer>();
            foreach (var r in part.Renderers)
            {
                if (r == null || !r.enabled || r.name.StartsWith(Name, StringComparison.Ordinal)) continue;
                // A reversed back has the same edges as its original.
                if (r.name.StartsWith(ObjectRules.BackName, StringComparison.Ordinal)) continue;
                if (!r.bounds.Intersects(area) || !SceneIndex.MatchesSelfOrAncestor(r.transform, rule, area)) continue;
                var mr = r.TryCast<MeshRenderer>();
                if (mr == null) continue;
                try
                {
                    var skirt = Build(b, mr, area, bottom, out int n, out int s);
                    if (skirt == null) continue;
                    added.Add(skirt);
                    renderers++;
                    edges += n;
                    steepEdges += s;
                }
                catch (Exception e)
                {
                    failed++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: the skirt of '{MeshCarver.Path(r.transform)}' could not be built: {e.Message}");
                }
            }
            // The skirts join the index, so the clearing of the connections cuts them like the rock they hang from.
            part.Renderers.AddRange(added);
            string line = $"rules: '{b.Id}' {rule}: {renderers} skirts from {edges} open lower edges ({steepEdges} under a steep face) down to y {bottom:0.0}";
            if (failed > 0) Core.Log.Warning($"{line}; {failed} failed");
            else if (renderers == 0) Core.Log.Warning(line + "; no open lower edge in the area");
            else Core.Log.Msg(line);
        }

        /// <summary>Above this up component of its normal, a triangle below its open edge is a top face, not a wall.</summary>
        private const float FlatTop = 0.8f;

        private static MeshRenderer Build(BuiltExpansion b, MeshRenderer mr, Bounds area, float bottom, out int count, out int steep)
        {
            count = 0;
            steep = 0;
            var mesh = MeshCarver.BuildBack(mr, Name + mr.name);
            if (mesh == null)
            {
                Core.Log.Warning($"rules: skirt: '{MeshCarver.Path(mr.transform)}' has no readable triangles");
                return null;
            }
            var verts = mesh.vertices;
            var toWorld = mr.transform.localToWorldMatrix;
            var ids = new Dictionary<Vector3Int, int>();
            var points = new List<Vector3>();
            var weld = new int[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var w = toWorld.MultiplyPoint3x4(verts[i]);
                var key = Vector3Int.RoundToInt(w * 100f);
                if (!ids.TryGetValue(key, out int id))
                {
                    id = points.Count;
                    ids[key] = id;
                    points.Add(w);
                }
                weld[i] = id;
            }

            // Per edge: how often it is used, and the submesh and third corner of the last triangle using it.
            var uses = new Dictionary<long, (int n, int sub, int third)>();
            int subs = mesh.subMeshCount;
            for (int s = 0; s < subs; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int p = weld[tris[t + e]], q = weld[tris[t + (e + 1) % 3]], o = weld[tris[t + (e + 2) % 3]];
                        long k = p < q ? ((long)p << 32) | (uint)q : ((long)q << 32) | (uint)p;
                        uses[k] = (uses.TryGetValue(k, out var u) ? u.n + 1 : 1, s, o);
                    }
            }
            Object.Destroy(mesh);

            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var lists = new List<int>[subs];
            for (int s = 0; s < subs; s++) lists[s] = new List<int>();
            foreach (var kv in uses)
            {
                if (kv.Value.n != 1) continue;
                Vector3 p = points[(int)(kv.Key >> 32)], q = points[(int)(kv.Key & 0xffffffff)];
                var mid = (p + q) * 0.5f;
                if (!area.Contains(mid)) continue;
                var o = points[kv.Value.third];
                if (!Above(o, p, q))
                {
                    // A steep triangle that falls away from its open edge still leaves a slit under it.
                    if (Mathf.Abs(Vector3.Cross(q - p, o - p).normalized.y) > FlatTop) continue;
                    steep++;
                }
                if (Mathf.Max(p.y, q.y) <= bottom) continue;
                var side = Vector3.Cross(Vector3.up, q - p);
                side.y = 0f;
                if (side.sqrMagnitude < 1e-8f) continue;
                side.Normalize();
                Vector3 pb = new(p.x, Mathf.Min(bottom, p.y), p.z), qb = new(q.x, Mathf.Min(bottom, q.y), q.z);
                Quad(lists[kv.Value.sub], pos, nrm, uv, p, q, qb, pb, side);
                Quad(lists[kv.Value.sub], pos, nrm, uv, q, p, pb, qb, -side);
                count++;
            }
            if (count == 0) return null;

            var skirt = b.Own(new Mesh { name = Name + mr.name });
            if (pos.Count > 65535) skirt.indexFormat = IndexFormat.UInt32;
            skirt.vertices = pos.ToArray();
            skirt.normals = nrm.ToArray();
            skirt.uv = uv.ToArray();
            skirt.subMeshCount = subs;
            for (int s = 0; s < subs; s++)
            {
                var arr = new Il2CppStructArray<int>(lists[s].Count);
                for (int i = 0; i < lists[s].Count; i++) arr[i] = lists[s][i];
                skirt.SetTriangles(arr, s, false);
            }
            skirt.RecalculateBounds();
            skirt.RecalculateTangents();

            // Made at the world origin, so the world-space vertices stay where they are under the root.
            var go = new GameObject(Name + mr.name) { layer = mr.gameObject.layer };
            go.transform.SetParent(b.Root.transform, true);
            go.AddComponent<MeshFilter>().sharedMesh = skirt;
            var r = go.AddComponent<MeshRenderer>();
            var mats = mr.sharedMaterials;
            var used = new Material[subs];
            for (int s = 0; s < subs; s++) used[s] = s < mats.Length ? mats[s] : mats[mats.Length - 1];
            r.sharedMaterials = new Il2CppReferenceArray<Material>(used);
            r.shadowCastingMode = mr.shadowCastingMode;
            r.receiveShadows = mr.receiveShadows;
            r.renderingLayerMask = mr.renderingLayerMask;
            r.lightProbeUsage = mr.lightProbeUsage;
            MeshPatch.JoinLod(mr, r, b);
            SeparateViews.NoteMade(b, mr, r);
            return r;
        }

        /// <summary>
        /// Whether a triangle's third corner rises above its edge p-q where it stands over that edge. The edge's
        /// midpoint is no measure on a long sloping edge: a corner above one end can be below the middle.
        /// </summary>
        private static bool Above(Vector3 o, Vector3 p, Vector3 q)
        {
            var d = new Vector2(q.x - p.x, q.z - p.z);
            float t = d.sqrMagnitude < 1e-8f ? 0.5f : Mathf.Clamp01(Vector2.Dot(new Vector2(o.x - p.x, o.z - p.z), d) / d.sqrMagnitude);
            return o.y > Mathf.Lerp(p.y, q.y, t);
        }

        /// <summary>One face from the top edge a-b down to d-c, facing <paramref name="normal"/>.</summary>
        private static void Quad(List<int> tris, List<Vector3> pos, List<Vector3> nrm, List<Vector2> uv,
                                 Vector3 a, Vector3 bb, Vector3 c, Vector3 d, Vector3 normal)
        {
            int i = pos.Count;
            foreach (var p in new[] { a, bb, c, d })
            {
                pos.Add(p);
                nrm.Add(normal);
                uv.Add(new Vector2(p.x + p.z, p.y));
            }
            // A triangle a, b, c faces the side its cross product (b - a) x (c - a) points to.
            var face = Vector3.Cross(bb - a, c - a);
            if (Vector3.Dot(face, normal) > 0f)
            {
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }
            else
            {
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
                tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
            }
        }
    }
}
