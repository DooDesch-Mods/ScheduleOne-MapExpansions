using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MapExpansions.Connections;
using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapExpansions.World
{
    /// <summary>
    /// Draws again the part of a cut triangle that lies outside the boxes. The cut takes whole triangles, so a large one
    /// leaves a hole far wider than the connection, with sky behind it. Each large cut triangle is split until every
    /// piece is outside the boxes or shorter than <see cref="FineEdge"/>; the outside pieces go into a patch renderer
    /// in world space with the materials of the cut renderer. The rest of the hole is narrower than the tunnel shell.
    /// </summary>
    internal sealed class MeshPatch
    {
        internal struct V
        {
            public Vector3 P, N;
            public Vector2 U;

            public V(Vector3 p, Vector3 n, Vector2 u)
            {
                P = p;
                N = n;
                U = u;
            }

            public static V Mid(V a, V b) => new((a.P + b.P) * 0.5f, (a.N + b.N).normalized, (a.U + b.U) * 0.5f);
        }

        private const float FineEdge = 1f;
        private const int MaxDepth = 10;
        private const int MaxPieces = 60000;

        private readonly IReadOnlyList<OrientedBox> _boxes;
        private readonly List<Vector3> _pos = new();
        private readonly List<Vector3> _nrm = new();
        private readonly List<Vector2> _uv = new();
        private readonly List<List<int>> _subs = new();

        public int Submeshes => _subs.Count;
        public int Large { get; private set; }
        public int Pieces { get; private set; }
        public bool Capped { get; private set; }

        public MeshPatch(IReadOnlyList<OrientedBox> boxes, int submeshes)
        {
            _boxes = boxes;
            for (int i = 0; i < submeshes; i++) _subs.Add(new List<int>());
        }

        public static bool IsLarge(Vector3 a, Vector3 b, Vector3 c)
        {
            float e = FineEdge * FineEdge;
            return (b - a).sqrMagnitude > e || (c - b).sqrMagnitude > e || (a - c).sqrMagnitude > e;
        }

        /// <summary>A cut triangle, corners in world space; small ones are left out.</summary>
        public void Add(int sub, V a, V b, V c)
        {
            if (!IsLarge(a.P, b.P, c.P)) return;
            Large++;
            Split(sub, a, b, c, 0);
        }

        private void Split(int sub, V a, V b, V c, int depth)
        {
            if (!MeshCarver.Hits(a.P, b.P, c.P, _boxes))
            {
                Emit(sub, a, b, c);
                return;
            }
            if (depth >= MaxDepth || !IsLarge(a.P, b.P, c.P)) return;
            if (Pieces >= MaxPieces)
            {
                Capped = true;
                return;
            }
            V ab = V.Mid(a, b), bc = V.Mid(b, c), ca = V.Mid(c, a);
            Split(sub, a, ab, ca, depth + 1);
            Split(sub, ab, b, bc, depth + 1);
            Split(sub, ca, bc, c, depth + 1);
            Split(sub, ab, bc, ca, depth + 1);
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
            }
            Pieces++;
        }

        /// <summary>
        /// The patch renderer under the expansion root, drawn where <paramref name="source"/> is drawn: in the same
        /// LOD level of its LOD group, if it has one. Null when no piece is outside the boxes.
        /// </summary>
        public MeshRenderer Build(MeshRenderer source, Material[] materials, BuiltExpansion owner, string name)
        {
            if (Pieces == 0) return null;
            var mesh = owner.Own(new Mesh { name = name });
            if (_pos.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = _pos.ToArray();
            mesh.normals = _nrm.ToArray();
            mesh.uv = _uv.ToArray();
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
            go.transform.SetParent(owner.Root.transform, true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new Il2CppReferenceArray<Material>(materials);
            mr.shadowCastingMode = source.shadowCastingMode;
            mr.receiveShadows = source.receiveShadows;
            mr.renderingLayerMask = source.renderingLayerMask;
            mr.lightProbeUsage = source.lightProbeUsage;
            JoinLod(source, mr, owner);
            return mr;
        }

        /// <summary>Puts <paramref name="patch"/> into the LOD level of <paramref name="source"/>, if it has one; undone at teardown.</summary>
        internal static void JoinLod(MeshRenderer source, MeshRenderer patch, BuiltExpansion owner)
        {
            var group = source.GetComponentInParent<LODGroup>();
            if (group == null) return;
            var lods = group.GetLODs();
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
                joined[rs.Length] = patch;
                lods[i].renderers = joined;
                group.SetLODs(lods);
                int level = i;
                owner.OnTeardown.Add(() =>
                {
                    if (group == null) return;
                    var now = group.GetLODs();
                    if (level < now.Length) now[level].renderers = rs;
                    group.SetLODs(now);
                });
                return;
            }
            Core.Log.Warning($"carve: '{MeshCarver.Path(source.transform)}' is under the LOD group '{group.name}' but in none of its levels; " +
                             "its patch is drawn at every distance");
        }
    }
}
