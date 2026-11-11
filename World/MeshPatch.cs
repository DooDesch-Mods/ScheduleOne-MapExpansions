using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MapExpansions.Connections;
using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapExpansions.World
{
    /// <summary>
    /// Draws again the part of a cut triangle that lies outside the boxes. The cut takes whole triangles, so a large one
    /// leaves a hole far wider than the connection, with sky behind it. Each cut triangle is clipped against the faces of
    /// every box it reaches into, so the pieces outside end exactly on the box faces; they go into a patch renderer in
    /// world space with the materials of the cut renderer.
    /// </summary>
    internal sealed class MeshPatch
    {
        internal struct V
        {
            public Vector3 P, N;
            public Vector2 U, L;
            public Color C;

            public V(Vector3 p, Vector3 n, Vector2 u, Vector2 l, Color c)
            {
                P = p;
                N = n;
                U = u;
                L = l;
                C = c;
            }

            public static V Lerp(V a, V b, float t) =>
                new(Vector3.Lerp(a.P, b.P, t), Vector3.Lerp(a.N, b.N, t).normalized, Vector2.Lerp(a.U, b.U, t), Vector2.Lerp(a.L, b.L, t),
                    Color.Lerp(a.C, b.C, t));
        }

        private const int MaxPieces = 60000;

        /// <summary>A piece smaller than this (in square metres, doubled) is a sliver along a box face and is left out.</summary>
        private const float MinArea2 = 1e-6f;

        private readonly IReadOnlyList<OrientedBox> _boxes;
        private readonly List<Vector3> _pos = new();
        private readonly List<Vector3> _nrm = new();
        private readonly List<Vector2> _uv = new();
        private readonly List<Vector2> _uv2 = new();
        private readonly List<Color> _col = new();
        private readonly List<List<int>> _subs = new();

        public int Submeshes => _subs.Count;

        /// <summary>World positions of the pieces' corners.</summary>
        public IReadOnlyList<Vector3> Positions => _pos;

        /// <summary>Corner indices of the pieces in one submesh, three per piece.</summary>
        public IReadOnlyList<int> Indices(int sub) => _subs[sub];
        public int Cut { get; private set; }
        public int Pieces { get; private set; }
        public bool Capped { get; private set; }

        /// <summary>Whether the cut mesh has vertex colours or a second UV set; the patch carries them only then.</summary>
        public bool HasColors { get; set; }
        public bool HasUv2 { get; set; }

        public MeshPatch(IReadOnlyList<OrientedBox> boxes, int submeshes)
        {
            _boxes = boxes;
            for (int i = 0; i < submeshes; i++) _subs.Add(new List<int>());
        }

        /// <summary>A cut triangle, corners in world space.</summary>
        public void Add(int sub, V a, V b, V c)
        {
            Cut++;
            var pieces = new List<List<V>> { new() { a, b, c } };
            foreach (var box in _boxes)
            {
                var next = new List<List<V>>();
                foreach (var poly in pieces)
                {
                    if (!Hits(poly, box))
                    {
                        next.Add(poly);
                        continue;
                    }
                    // Peel off the part outside each face in turn; what is left after the sixth face is inside the box.
                    var rest = poly;
                    foreach (var (normal, d) in Faces(box))
                    {
                        Split(rest, normal, d, out var outside, out var inside);
                        if (outside.Count >= 3) next.Add(outside);
                        rest = inside;
                        if (rest.Count < 3) break;
                    }
                }
                pieces = next;
                if (pieces.Count == 0) return;
            }
            foreach (var poly in pieces)
                for (int i = 1; i + 1 < poly.Count; i++)
                {
                    if (Pieces >= MaxPieces)
                    {
                        Capped = true;
                        return;
                    }
                    if (Vector3.Cross(poly[i].P - poly[0].P, poly[i + 1].P - poly[0].P).sqrMagnitude < MinArea2 * MinArea2) continue;
                    Emit(sub, poly[0], poly[i], poly[i + 1]);
                }
        }

        private static bool Hits(List<V> poly, OrientedBox box)
        {
            for (int i = 1; i + 1 < poly.Count; i++)
                if (box.Intersects(poly[0].P, poly[i].P, poly[i + 1].P)) return true;
            return false;
        }

        /// <summary>The six faces of a box as outward normal and distance; a point is outside a face when dot(n, p) &gt; d.</summary>
        private static IEnumerable<(Vector3 normal, float d)> Faces(OrientedBox box)
        {
            var axes = new[] { box.Rotation * Vector3.right, box.Rotation * Vector3.up, box.Rotation * Vector3.forward };
            var half = new[] { box.Half.x, box.Half.y, box.Half.z };
            for (int i = 0; i < 3; i++)
            {
                float c = Vector3.Dot(axes[i], box.Center);
                yield return (axes[i], c + half[i]);
                yield return (-axes[i], -c + half[i]);
            }
        }

        /// <summary>Splits a convex polygon by a plane into the part outside it and the part inside it.</summary>
        private static void Split(List<V> poly, Vector3 normal, float d, out List<V> outside, out List<V> inside)
        {
            outside = new List<V>(poly.Count + 1);
            inside = new List<V>(poly.Count + 1);
            for (int i = 0; i < poly.Count; i++)
            {
                var cur = poly[i];
                var nxt = poly[(i + 1) % poly.Count];
                float dc = Vector3.Dot(normal, cur.P) - d, dn = Vector3.Dot(normal, nxt.P) - d;
                if (dc >= 0f) outside.Add(cur);
                if (dc <= 0f) inside.Add(cur);
                if ((dc > 0f && dn < 0f) || (dc < 0f && dn > 0f))
                {
                    var m = V.Lerp(cur, nxt, dc / (dc - dn));
                    outside.Add(m);
                    inside.Add(m);
                }
            }
        }

        private void Emit(int sub, V a, V b, V c)
        {
            var list = _subs[sub];
            foreach (var v in new[] { a, b, c })
            {
                list.Add(_pos.Count);
                _pos.Add(v.P);
                _nrm.Add(v.N);
                _uv.Add(v.U);
                _uv2.Add(v.L);
                _col.Add(v.C);
            }
            Pieces++;
        }

        /// <summary>
        /// The patch renderer under the expansion root, drawn where <paramref name="source"/> is drawn: in the same
        /// LOD level of its LOD group, if it has one. Null when no piece is outside the boxes.
        /// </summary>
        public MeshRenderer Build(MeshRenderer source, Material[] materials, BuiltExpansion owner, string name, Transform parent = null)
        {
            if (Pieces == 0) return null;
            var mesh = owner.Own(new Mesh { name = name });
            if (_pos.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = _pos.ToArray();
            mesh.normals = _nrm.ToArray();
            mesh.uv = _uv.ToArray();
            if (HasUv2) mesh.uv2 = _uv2.ToArray();
            if (HasColors) mesh.colors = _col.ToArray();
            mesh.subMeshCount = _subs.Count;
            for (int s = 0; s < _subs.Count; s++)
            {
                var arr = new Il2CppStructArray<int>(_subs[s].Count);
                for (int i = 0; i < _subs[s].Count; i++) arr[i] = _subs[s][i];
                mesh.SetTriangles(arr, s, false);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            // Made at the world origin, so the world-space vertices stay where they are under the root.
            var go = new GameObject(name) { layer = source.gameObject.layer };
            go.transform.SetParent(parent ?? owner.Root.transform, true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new Il2CppReferenceArray<Material>(materials);
            mr.shadowCastingMode = source.shadowCastingMode;
            mr.receiveShadows = source.receiveShadows;
            mr.renderingLayerMask = source.renderingLayerMask;
            mr.lightProbeUsage = source.lightProbeUsage;
            if (source.lightmapIndex >= 0 && source.lightmapIndex < 65534)
                Core.Log.Warning($"carve: '{MeshCarver.Path(source.transform)}' is lightmapped (map {source.lightmapIndex}); its patch is lit without the map");
            JoinLod(source, mr, owner);
            SeparateViews.NoteMade(owner, source, mr);
            return mr;
        }

        /// <summary>
        /// Puts <paramref name="made"/> into every LOD level of <paramref name="source"/>'s LOD group that holds
        /// <paramref name="source"/>, so it is drawn at the same distances. Teardown takes it out again, or
        /// <paramref name="undo"/> when the caller removes it earlier. Returns the number of levels joined, 0 when the
        /// source has no LOD group.
        /// </summary>
        internal static int JoinLod(MeshRenderer source, MeshRenderer made, BuiltExpansion owner, List<Action> undo = null)
        {
            var group = source.GetComponentInParent<LODGroup>();
            if (group == null) return 0;
            var lods = group.GetLODs();
            int joinedLevels = 0;
            for (int i = 0; i < lods.Length; i++)
            {
                var rs = lods[i].renderers;
                if (rs == null) continue;
                bool has = false;
                for (int k = 0; k < rs.Length; k++)
                    if (rs[k] != null && rs[k].Pointer == source.Pointer) has = true;
                if (!has) continue;
                var joined = new Il2CppReferenceArray<Renderer>(rs.Length + 1);
                for (int k = 0; k < rs.Length; k++) joined[k] = rs[k];
                joined[rs.Length] = made;
                // LOD is a struct: the indexer hands out a copy, so the changed level is written back.
                var level = lods[i];
                level.renderers = joined;
                lods[i] = level;
                joinedLevels++;
            }
            if (joinedLevels == 0)
            {
                Core.Log.Warning($"carve: '{MeshCarver.Path(source.transform)}' is under the LOD group '{group.name}' but in none of its levels; " +
                                 $"'{made.name}' is drawn at every distance");
                return 0;
            }
            group.SetLODs(lods);
            var ptr = made.Pointer;
            int found = 0;
            foreach (var lod in group.GetLODs())
                if (lod.renderers != null)
                    foreach (var r in lod.renderers)
                        if (r != null && r.Pointer == ptr) found++;
            if (found != joinedLevels)
                Core.Log.Warning($"carve: '{made.name}' should be in {joinedLevels} LOD levels of '{group.name}' but the group holds it in {found}; " +
                                 "it may be drawn at the wrong distances");
            (undo ?? owner.OnTeardown).Add(() =>
            {
                if (group == null) return;
                // Takes out only this renderer, so the joins of other renderers stay whatever order teardown runs in.
                var now = group.GetLODs();
                for (int i = 0; i < now.Length; i++)
                {
                    var rs = now[i].renderers;
                    if (rs == null) continue;
                    var kept = new List<Renderer>(rs.Length);
                    for (int k = 0; k < rs.Length; k++)
                        if (rs[k] != null && rs[k].Pointer != ptr) kept.Add(rs[k]);
                    if (kept.Count == rs.Length) continue;
                    var level = now[i];
                    level.renderers = new Il2CppReferenceArray<Renderer>(kept.ToArray());
                    now[i] = level;
                }
                group.SetLODs(now);
            });
            return joinedLevels;
        }
    }
}
