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
                    Mesh clone;
                    if (mesh.isReadable)
                    {
                        clone = owner.Own(Object.Instantiate(mesh));
                        clone.name = mesh.name + " (MapExpansions)";
                    }
                    else
                    {
                        clone = CloneGpu(mesh, owner);
                        Core.Log.Msg($"carve: '{Path(mr.transform)}' shares the unreadable mesh '{mesh.name}' with {users - 1} other renderers; " +
                                     "it gets a copy from the GPU buffers");
                    }
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

        /// <summary>
        /// Copies an unreadable mesh through its GPU buffers: same vertex layout, vertex and index data and submeshes,
        /// owned by the build. The copy keeps no CPU data either, so it is cut and drawn on the GPU only.
        /// </summary>
        internal static Mesh CloneGpu(Mesh src, BuiltExpansion owner)
        {
            var clone = owner.Own(new Mesh { name = src.name + " (MapExpansions)" });
            clone.SetVertexBufferParams(src.vertexCount, src.GetVertexAttributes());
            int indexCount = 0;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var d = src.GetSubMesh(s);
                indexCount = Mathf.Max(indexCount, d.indexStart + d.indexCount);
            }
            clone.SetIndexBufferParams(indexCount, src.indexFormat);
            const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds |
                                          MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
            clone.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++) clone.SetSubMesh(s, src.GetSubMesh(s), flags);
            clone.bounds = src.bounds;
            // Drops the CPU copy; a later upload would otherwise write its zeros over the GPU data.
            clone.UploadMeshData(true);
            for (int st = 0; st < src.vertexBufferCount; st++)
                CopyBuffer(src.GetVertexBuffer(st), clone.GetVertexBuffer(st), $"vertex stream {st} of '{src.name}'");
            CopyBuffer(src.GetIndexBuffer(), clone.GetIndexBuffer(), $"index buffer of '{src.name}'");
            return clone;
        }

        private static void CopyBuffer(GraphicsBuffer from, GraphicsBuffer to, string what)
        {
            try
            {
                int bytes = from.count * from.stride;
                if (to.count * to.stride < bytes)
                    throw new InvalidOperationException($"{what}: the copy holds {to.count * to.stride} bytes, the source {bytes}");
                if (bytes % 4 == 0) Copy<int>(from, to, bytes / 4, 4, what);
                else Copy<ushort>(from, to, bytes / 2, 2, what);
            }
            finally
            {
                from.Dispose();
                to.Dispose();
            }
        }

        private static void Copy<T>(GraphicsBuffer from, GraphicsBuffer to, int n, int elem, string what) where T : unmanaged, IEquatable<T>
        {
            var data = new Il2CppStructArray<T>(n);
            from.InternalGetData(new Il2CppSystem.Array(data.Pointer), 0, 0, n, elem);
            to.InternalSetData(new Il2CppSystem.Array(data.Pointer), 0, 0, n, elem);
            var back = new Il2CppStructArray<T>(n);
            to.InternalGetData(new Il2CppSystem.Array(back.Pointer), 0, 0, n, elem);
            // A buffer the driver refused to write keeps zeros without an error.
            for (int i = 0; i < n; i += Mathf.Max(1, n / 64))
                if (!data[i].Equals(back[i]))
                    throw new InvalidOperationException($"{what}: the copy did not take the write (element {i})");
        }

        /// <summary>
        /// A readable copy of a renderer's mesh in the renderer's own space with the winding reversed, so it draws the
        /// faces the original culls. Normals are recalculated for the reversed faces. One submesh per material of the
        /// renderer; null when the renderer has no mesh or no triangles.
        /// </summary>
        internal static Mesh BuildBack(MeshRenderer mr, string name)
        {
            var mf = mr.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return null;
            bool batched = mr.isPartOfStaticBatch;
            Matrix4x4 toWorld = batched
                ? (mr.staticBatchRootTransform != null ? mr.staticBatchRootTransform.localToWorldMatrix : Matrix4x4.identity)
                : mr.transform.localToWorldMatrix;
            Matrix4x4 toLocal = mr.transform.worldToLocalMatrix * toWorld;
            int first = batched ? mr.subMeshStartIndex : 0;
            int count = batched ? Mathf.Min(mr.sharedMaterials.Length, mesh.subMeshCount - first) : mesh.subMeshCount;
            bool wide = mesh.indexFormat == IndexFormat.UInt32;

            var subs = new List<int[]>();
            int minV = int.MaxValue, maxV = int.MinValue;
            for (int s = first; s < first + count; s++)
            {
                var d = mesh.GetSubMesh(s);
                if (d.topology != MeshTopology.Triangles || d.indexCount == 0)
                {
                    subs.Add(Array.Empty<int>());
                    continue;
                }
                int[] idx;
                if (mesh.isReadable)
                {
                    var src = mesh.GetIndices(s, false);
                    idx = new int[src.Length];
                    for (int i = 0; i < idx.Length; i++) idx[i] = src[i];
                }
                else idx = ReadIndices(mesh, wide, d.indexStart, d.indexCount);
                for (int i = 0; i < idx.Length; i++)
                {
                    idx[i] += d.baseVertex;
                    if (idx[i] < minV) minV = idx[i];
                    if (idx[i] > maxV) maxV = idx[i];
                }
                subs.Add(idx);
            }
            if (minV > maxV) return null;
            Vector3[] local;
            if (mesh.isReadable)
            {
                var all = mesh.vertices;
                local = new Vector3[maxV - minV + 1];
                for (int i = 0; i < local.Length; i++) local[i] = toLocal.MultiplyPoint3x4(all[minV + i]);
            }
            else local = ReadPositions(mesh, minV, maxV - minV + 1, toLocal);

            var back = new Mesh { name = name };
            if (local.Length > 65535) back.indexFormat = IndexFormat.UInt32;
            back.vertices = local;
            back.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++)
            {
                var src = subs[s];
                var arr = new Il2CppStructArray<int>(src.Length);
                for (int t = 0; t + 2 < src.Length; t += 3)
                {
                    arr[t] = src[t] - minV;
                    arr[t + 1] = src[t + 2] - minV;
                    arr[t + 2] = src[t + 1] - minV;
                }
                back.SetTriangles(arr, s, false);
            }
            back.RecalculateNormals();
            back.RecalculateBounds();
            return back;
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
