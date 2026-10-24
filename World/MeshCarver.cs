using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MapExpansions.Connections;
using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Cuts the triangles out of a mesh that reach into a connection's inside. Most world meshes are not readable on
    /// the CPU, so their positions and indices come back from the GPU buffers; a cut writes degenerate triangles over
    /// the cut ones in place, which keeps vertex layout, materials and static batches as they are.
    /// </summary>
    internal static class MeshCarver
    {
        public enum Outcome
        {
            Untouched,
            Carved,
            Failed,
        }

        /// <summary>World-space positions and triangle list of one renderer's part of a mesh.</summary>
        private sealed class Part
        {
            public int IndexStart;
            public int IndexCount;
            public int BaseVertex;
            public int[] Indices;
        }

        /// <summary>
        /// Cuts a renderer's triangles that intersect any of the boxes. A static batch keeps its combined mesh: only the
        /// renderer's own index range is rewritten. A mesh that other renderers share is cloned first.
        /// </summary>
        public static Outcome CarveRenderer(MeshRenderer mr, IReadOnlyList<OrientedBox> boxes, SceneIndex index, BuiltExpansion owner, out int cut)
        {
            cut = 0;
            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null)
            {
                Core.Log.Warning($"carve: '{Path(mr.transform)}' has no mesh; left as it is");
                return Outcome.Failed;
            }
            try
            {
                bool batched = mr.isPartOfStaticBatch;
                // Batched vertices are in the batch root's space; the root is the world origin unless it was moved.
                Matrix4x4 toWorld = batched
                    ? (mr.staticBatchRootTransform != null ? mr.staticBatchRootTransform.localToWorldMatrix : Matrix4x4.identity)
                    : mr.transform.localToWorldMatrix;
                int first = batched ? mr.subMeshStartIndex : 0;
                int count = Mathf.Min(mr.sharedMaterials.Length, mesh.subMeshCount - first);
                if (!batched) count = mesh.subMeshCount;
                if (count <= 0)
                {
                    Core.Log.Warning($"carve: '{Path(mr.transform)}' uses no submesh of '{mesh.name}'; left as it is");
                    return Outcome.Failed;
                }

                index.MeshUsers.TryGetValue(mesh.Pointer, out int users);
                if (!batched && users > 1)
                {
                    if (!mesh.isReadable)
                    {
                        Core.Log.Warning($"carve: '{Path(mr.transform)}' shares the unreadable mesh '{mesh.name}' with {users - 1} other renderers; " +
                                         "it is switched off instead of cut");
                        return Outcome.Failed;
                    }
                    var clone = owner.Own(Object.Instantiate(mesh));
                    clone.name = mesh.name + " (MapExpansions)";
                    mf.sharedMesh = clone;
                    mesh = clone;
                }

                if (mesh.isReadable && !batched)
                {
                    cut = CarveCpu(mesh, toWorld, boxes);
                    return cut > 0 ? Outcome.Carved : Outcome.Untouched;
                }
                cut = CarveGpu(mesh, first, count, toWorld, boxes, Path(mr.transform));
                return cut > 0 ? Outcome.Carved : Outcome.Untouched;
            }
            catch (Exception e)
            {
                Core.Log.Warning($"carve: cutting '{Path(mr.transform)}' ({mesh.name}) failed, it is switched off instead: {e.Message}");
                return Outcome.Failed;
            }
        }

        private static int CarveCpu(Mesh mesh, Matrix4x4 toWorld, IReadOnlyList<OrientedBox> boxes)
        {
            var verts = mesh.vertices;
            var world = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) world[i] = toWorld.MultiplyPoint3x4(verts[i]);
            int cut = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var tris = mesh.GetTriangles(s);
                var keep = new List<int>(tris.Length);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (Hits(world[tris[t]], world[tris[t + 1]], world[tris[t + 2]], boxes)) { cut++; continue; }
                    keep.Add(tris[t]);
                    keep.Add(tris[t + 1]);
                    keep.Add(tris[t + 2]);
                }
                if (keep.Count != tris.Length)
                {
                    var arr = new Il2CppStructArray<int>(keep.Count);
                    for (int i = 0; i < keep.Count; i++) arr[i] = keep[i];
                    mesh.SetTriangles(arr, s, false);
                }
            }
            return cut;
        }

        private static int CarveGpu(Mesh mesh, int first, int count, Matrix4x4 toWorld, IReadOnlyList<OrientedBox> boxes, string what)
        {
            bool wide = mesh.indexFormat == IndexFormat.UInt32;
            var parts = new List<Part>();
            int minV = int.MaxValue, maxV = int.MinValue;
            for (int s = first; s < first + count; s++)
            {
                var d = mesh.GetSubMesh(s);
                if (d.topology != MeshTopology.Triangles || d.indexCount == 0) continue;
                var p = new Part { IndexStart = d.indexStart, IndexCount = d.indexCount, BaseVertex = d.baseVertex };
                p.Indices = ReadIndices(mesh, wide, d.indexStart, d.indexCount);
                foreach (int i in p.Indices)
                {
                    int v = i + d.baseVertex;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
                parts.Add(p);
            }
            if (parts.Count == 0) return 0;
            var world = ReadPositions(mesh, minV, maxV - minV + 1, toWorld);

            int cut = 0;
            foreach (var p in parts)
            {
                bool changed = false;
                for (int t = 0; t + 2 < p.Indices.Length; t += 3)
                {
                    int a = p.Indices[t], b = p.Indices[t + 1], c = p.Indices[t + 2];
                    if (a == b && b == c) continue;
                    if (!Hits(world[a + p.BaseVertex - minV], world[b + p.BaseVertex - minV], world[c + p.BaseVertex - minV], boxes)) continue;
                    p.Indices[t + 1] = a;
                    p.Indices[t + 2] = a;
                    changed = true;
                    cut++;
                }
                if (changed) WriteIndices(mesh, wide, p, what);
            }
            return cut;
        }

        private static bool Hits(Vector3 a, Vector3 b, Vector3 c, IReadOnlyList<OrientedBox> boxes)
        {
            for (int i = 0; i < boxes.Count; i++)
                if (boxes[i].Intersects(a, b, c)) return true;
            return false;
        }

        private static int[] ReadIndices(Mesh mesh, bool wide, int start, int count)
        {
            var buf = mesh.GetIndexBuffer();
            try
            {
                var result = new int[count];
                if (wide)
                {
                    var arr = new Il2CppStructArray<int>(count);
                    buf.InternalGetData(new Il2CppSystem.Array(arr.Pointer), 0, start, count, 4);
                    for (int i = 0; i < count; i++) result[i] = arr[i];
                }
                else
                {
                    var arr = new Il2CppStructArray<ushort>(count);
                    buf.InternalGetData(new Il2CppSystem.Array(arr.Pointer), 0, start, count, 2);
                    for (int i = 0; i < count; i++) result[i] = arr[i];
                }
                return result;
            }
            finally
            {
                buf.Dispose();
            }
        }

        private static void WriteIndices(Mesh mesh, bool wide, Part p, string what)
        {
            var buf = mesh.GetIndexBuffer();
            try
            {
                if (wide)
                {
                    var arr = new Il2CppStructArray<int>(p.IndexCount);
                    for (int i = 0; i < p.IndexCount; i++) arr[i] = p.Indices[i];
                    buf.InternalSetData(new Il2CppSystem.Array(arr.Pointer), 0, p.IndexStart, p.IndexCount, 4);
                }
                else
                {
                    var arr = new Il2CppStructArray<ushort>(p.IndexCount);
                    for (int i = 0; i < p.IndexCount; i++) arr[i] = (ushort)p.Indices[i];
                    buf.InternalSetData(new Il2CppSystem.Array(arr.Pointer), 0, p.IndexStart, p.IndexCount, 2);
                }
            }
            finally
            {
                buf.Dispose();
            }
            // Read the range back: a buffer the driver refused to write keeps the old triangles without an error.
            var check = ReadIndices(mesh, wide, p.IndexStart, p.IndexCount);
            for (int i = 0; i < check.Length; i++)
                if (check[i] != p.Indices[i])
                    throw new InvalidOperationException($"the index buffer of '{mesh.name}' did not take the write (index {p.IndexStart + i})");
        }

        private static Vector3[] ReadPositions(Mesh mesh, int start, int count, Matrix4x4 toWorld)
        {
            if (mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
                throw new NotSupportedException($"positions of '{mesh.name}' are {mesh.GetVertexAttributeFormat(VertexAttribute.Position)}, not Float32");
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            int stride = mesh.GetVertexBufferStride(stream);
            int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            if (stride % 4 != 0 || offset % 4 != 0)
                throw new NotSupportedException($"vertex stride {stride} or position offset {offset} of '{mesh.name}' is not a multiple of 4");
            int floats = stride / 4;
            var buf = mesh.GetVertexBuffer(stream);
            try
            {
                var arr = new Il2CppStructArray<float>(count * floats);
                buf.InternalGetData(new Il2CppSystem.Array(arr.Pointer), 0, start * floats, count * floats, 4);
                var world = new Vector3[count];
                int o = offset / 4;
                for (int i = 0; i < count; i++)
                {
                    int k = i * floats + o;
                    world[i] = toWorld.MultiplyPoint3x4(new Vector3(arr[k], arr[k + 1], arr[k + 2]));
                }
                return world;
            }
            finally
            {
                buf.Dispose();
            }
        }

        /// <summary>
        /// Replaces a mesh collider's mesh by a copy without the triangles that intersect the boxes. False when the
        /// collider's mesh can be read neither on the CPU nor from the GPU.
        /// </summary>
        public static Outcome CarveCollider(MeshCollider mc, IReadOnlyList<OrientedBox> boxes, BuiltExpansion owner, out int cut)
        {
            cut = 0;
            var mesh = mc.sharedMesh;
            if (mesh == null) return Outcome.Untouched;
            try
            {
                Matrix4x4 toWorld = mc.transform.localToWorldMatrix;
                Vector3[] local;
                var tris = new List<int>();
                if (mesh.isReadable)
                {
                    local = mesh.vertices;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        if (mesh.GetTopology(s) == MeshTopology.Triangles) tris.AddRange(mesh.GetTriangles(s));
                }
                else
                {
                    bool wide = mesh.indexFormat == IndexFormat.UInt32;
                    int vc = mesh.vertexCount;
                    local = ReadPositions(mesh, 0, vc, Matrix4x4.identity);
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var d = mesh.GetSubMesh(s);
                        if (d.topology != MeshTopology.Triangles) continue;
                        foreach (int i in ReadIndices(mesh, wide, d.indexStart, d.indexCount)) tris.Add(i + d.baseVertex);
                    }
                }
                var keep = new List<int>(tris.Count);
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    var a = toWorld.MultiplyPoint3x4(local[tris[t]]);
                    var b = toWorld.MultiplyPoint3x4(local[tris[t + 1]]);
                    var c = toWorld.MultiplyPoint3x4(local[tris[t + 2]]);
                    if (Hits(a, b, c, boxes)) { cut++; continue; }
                    keep.Add(tris[t]);
                    keep.Add(tris[t + 1]);
                    keep.Add(tris[t + 2]);
                }
                if (cut == 0) return Outcome.Untouched;
                var copy = owner.Own(new Mesh { name = mesh.name + " (MapExpansions collider)" });
                if (local.Length > 65535) copy.indexFormat = IndexFormat.UInt32;
                copy.vertices = local;
                var arr = new Il2CppStructArray<int>(keep.Count);
                for (int i = 0; i < keep.Count; i++) arr[i] = keep[i];
                copy.SetTriangles(arr, 0, true);
                mc.sharedMesh = copy;
                return Outcome.Carved;
            }
            catch (Exception e)
            {
                Core.Log.Warning($"carve: collider '{Path(mc.transform)}' ({mesh.name}) could not be cut, it is switched off instead: {e.Message}");
                return Outcome.Failed;
            }
        }

        public static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && parts.Count < 4; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
