using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MapExpansions.Connections
{
    /// <summary>
    /// Builds the stretches of a connection from a cross-section swept along the route: deck, curbs and walkways,
    /// then guard walls, edges or walls with vault and rock shell depending on the style. Each stretch is cut into
    /// chunks with a mesh collider on the same mesh; tunnel chunks carry a lamp.
    /// </summary>
    internal static class TunnelBuilder
    {
        private const int SubRoad = 0, SubWalkway = 1, SubWall = 2, SubShell = 3;
        private const float SlabBottom = -0.6f;
        private const float GuardThickness = 0.3f;
        private const int ArcSegments = 16;

        /// <summary>One run of the cross-section: points in (across, up) with the visible side on the left of the run.</summary>
        private sealed class Run
        {
            public readonly List<Vector2> Points = new();
            public int Sub;

            public Run(int sub, params Vector2[] pts)
            {
                Sub = sub;
                Points.AddRange(pts);
            }
        }

        private sealed class Materials
        {
            public MaterialResolver.Resolved Road, Walkway, Wall, Shell;
            public Material Lamp;

            public float Tiling(int sub) => sub switch
            {
                SubRoad => Road.Tiling,
                SubWalkway => Walkway.Tiling,
                SubWall => Wall.Tiling,
                _ => Shell.Tiling,
            };
        }

        public static BuiltConnection Build(BuiltExpansion b, ConnectionDefinition def, SceneIndex index)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var curve = new RouteCurve(def, b.Frame);
            var conn = new BuiltConnection(def, curve);
            conn.Root = new GameObject($"MapExpansions.{b.Id}.{conn.Name}");
            conn.Root.transform.SetParent(b.Root.transform, true);
            conn.Root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var mats = new Materials
            {
                Road = MaterialResolver.Resolve(def.RoadMaterial, "road", b, index),
                Walkway = MaterialResolver.Resolve(def.WalkwayMaterial, "walkway", b, index),
                Wall = MaterialResolver.Resolve(def.WallMaterial, "wall", b, index),
                Shell = MaterialResolver.Resolve(def.ShellMaterial, "shell", b, index),
                Lamp = MaterialResolver.Emissive(def.LightColor, 3f, b),
            };
            int layer = mats.Road.Layer >= 0 ? mats.Road.Layer : 0;

            int chunks = 0, lights = 0;
            var stretches = curve.Stretches();
            foreach (var (style, from, to) in stretches)
            {
                if (style == RouteStyle.Existing || to - from < 0.1f) continue;
                int i0 = curve.IndexAt(from), i1 = curve.IndexAt(to);
                if (i1 <= i0) continue;
                AddBoxes(conn, style, i0, i1);

                float chunkLength = style == RouteStyle.Tunnel ? def.LightSpacing * 2f : 32f;
                int start = i0;
                while (start < i1)
                {
                    int end = Mathf.Min(i1, curve.IndexAt(curve.Distance[start] + chunkLength));
                    if (end <= start) end = Mathf.Min(i1, start + 1);
                    // A short rest joins the chunk before it.
                    if (i1 - end < 4) end = i1;
                    var go = BuildChunk(b, conn, style, start, end, start == i0, end == i1, mats, layer, chunks);
                    if (go != null)
                    {
                        conn.SurfaceChunks.Add(go);
                        chunks++;
                        if (style == RouteStyle.Tunnel) lights += AddLights(conn, go.transform, start, end, mats);
                    }
                    start = end;
                }
            }
            var range = conn.BuiltRange;
            AddApproach(conn, range);
            Core.Log.Msg($"tunnel: '{b.Id}' {conn.Name}: route {curve.Length:0} m, {curve.Count} samples, " +
                         $"{string.Join(", ", stretches.Select(s => $"{s.style} {s.from:0}..{s.to:0}"))}; built {range.from:0}..{range.to:0} m in {chunks} chunks, " +
                         $"{lights} lamps, {conn.Interior.Count} clearance boxes, layer {LayerMask.LayerToName(layer)}, {sw.ElapsedMilliseconds} ms");
            return conn;
        }

        /// <summary>Half the width between the walls at sample s.</summary>
        private static float HalfWidth(BuiltConnection conn, int s) => conn.Curve.RoadWidth[s] * 0.5f + conn.Definition.WalkwayWidth;

        /// <summary>Median width below which the barrier sinks into the road, so a narrowing median ends in a ramp.</summary>
        private const float MedianFullWidth = 0.6f;

        /// <summary>
        /// The cross-section at sample s. Every sample of one chunk yields the same runs with the same point counts, so
        /// <paramref name="median"/> is decided per chunk: with it, a barrier stands in the middle, as narrow as the
        /// sample's median width.
        /// </summary>
        private static List<Run> Profile(BuiltConnection conn, RouteStyle style, int s, bool median)
        {
            var d = conn.Definition;
            float hw = conn.Curve.RoadWidth[s] * 0.5f, w = HalfWidth(conn, s), ch = d.CurbHeight;
            var runs = new List<Run>
            {
                new(SubRoad, new(-hw, 0f), new(hw, 0f)),
                new(SubWalkway, new(hw, 0f), new(hw, ch)),
                new(SubWalkway, new(hw, ch), new(w, ch)),
                new(SubWalkway, new(-w, ch), new(-hw, ch)),
                new(SubWalkway, new(-hw, ch), new(-hw, 0f)),
            };
            if (median)
            {
                float mw = conn.Curve.MedianWidth[s], half = mw * 0.5f, top = half * 0.6f;
                float mh = d.MedianHeight * Mathf.Clamp01(mw / MedianFullWidth);
                runs.Add(new Run(SubWalkway, new(-half, 0f), new(-top, mh), new(top, mh), new(half, 0f)));
            }
            switch (style)
            {
                case RouteStyle.Tunnel:
                {
                    runs.Add(new Run(SubWall, new(w, ch), new(w, d.WallHeight)));
                    runs.Add(Arc(SubWall, w, d.WallHeight, d.CrownHeight - d.WallHeight, true));
                    runs.Add(new Run(SubWall, new(-w, d.WallHeight), new(-w, ch)));
                    float e = w + d.ShellThickness;
                    runs.Add(new Run(SubShell, new(-e, SlabBottom), new(-e, d.WallHeight)));
                    runs.Add(Arc(SubShell, e, d.WallHeight, d.CrownHeight + d.ShellThickness - d.WallHeight, false));
                    runs.Add(new Run(SubShell, new(e, d.WallHeight), new(e, SlabBottom)));
                    runs.Add(new Run(SubShell, new(e, SlabBottom), new(-e, SlabBottom)));
                    break;
                }
                case RouteStyle.Road:
                {
                    float g = d.GuardHeight, o = w + GuardThickness;
                    runs.Add(new Run(SubWall, new(w, ch), new(w, g)));
                    runs.Add(new Run(SubWall, new(w, g), new(o, g)));
                    runs.Add(new Run(SubWall, new(o, g), new(o, SlabBottom)));
                    runs.Add(new Run(SubShell, new(o, SlabBottom), new(-o, SlabBottom)));
                    runs.Add(new Run(SubWall, new(-o, SlabBottom), new(-o, g)));
                    runs.Add(new Run(SubWall, new(-o, g), new(-w, g)));
                    runs.Add(new Run(SubWall, new(-w, g), new(-w, ch)));
                    break;
                }
                default:
                    runs.Add(new Run(SubWall, new(w, ch), new(w, SlabBottom)));
                    runs.Add(new Run(SubShell, new(w, SlabBottom), new(-w, SlabBottom)));
                    runs.Add(new Run(SubWall, new(-w, SlabBottom), new(-w, ch)));
                    break;
            }
            return runs;
        }

        /// <summary>
        /// Half ellipse over the walls. Inside, it runs from the right wall over the crown to the left one, so the
        /// visible side faces down; outside, it runs the other way round and faces up.
        /// </summary>
        private static Run Arc(int sub, float halfWidth, float baseY, float rise, bool inside)
        {
            var run = new Run(sub);
            for (int i = 0; i <= ArcSegments; i++)
            {
                float t = (float)i / ArcSegments * Mathf.PI;
                float a = inside ? t : Mathf.PI - t;
                run.Points.Add(new Vector2(halfWidth * Mathf.Cos(a), baseY + rise * Mathf.Sin(a)));
            }
            return run;
        }

        private sealed class MeshData
        {
            public readonly List<Vector3> Vertices = new();
            public readonly List<Vector2> Uvs = new();
            public readonly List<int>[] Tris = { new(), new(), new(), new() };
        }

        private static GameObject BuildChunk(BuiltExpansion b, BuiltConnection conn, RouteStyle style, int i0, int i1, bool first, bool last,
                                             Materials mats, int layer, int number)
        {
            var def = conn.Definition;
            var curve = conn.Curve;
            var go = new GameObject($"{conn.Root.name}.{style}.{number}");
            go.layer = layer;
            go.transform.SetParent(conn.Root.transform, false);
            Vector3 origin = curve.Points[i0];
            go.transform.position = origin;

            var md = new MeshData();
            bool median = false;
            for (int s = i0; s <= i1 && !median; s++) median = curve.MedianWidth[s] > 0f;
            var profiles = new List<List<Run>>(i1 - i0 + 1);
            for (int s = i0; s <= i1; s++) profiles.Add(Profile(conn, style, s, median));
            for (int k = 0; k < profiles[0].Count; k++) Sweep(md, curve, profiles, k, i0, i1, origin, mats.Tiling(profiles[0][k].Sub));
            if (style == RouteStyle.Tunnel)
            {
                if (first)
                {
                    Ring(md, conn, i0, origin, -curve.Forward(i0), mats.Shell.Tiling);
                    Facade(md, conn, i0, origin, -curve.Forward(i0), mats.Wall.Tiling);
                }
                if (last)
                {
                    Ring(md, conn, i1, origin, curve.Forward(i1), mats.Shell.Tiling);
                    Facade(md, conn, i1, origin, curve.Forward(i1), mats.Wall.Tiling);
                }
            }

            var mesh = b.Own(new Mesh { name = go.name });
            if (md.Vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(ToIl2Cpp(md.Vertices));
            mesh.SetUVs(0, ToIl2Cpp(md.Uvs));
            mesh.subMeshCount = 4;
            for (int s = 0; s < 4; s++) mesh.SetTriangles(ToIl2Cpp(md.Tris[s]), s, false);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new Il2CppReferenceArray<Material>(new[] { mats.Road.Material, mats.Walkway.Material, mats.Wall.Material, mats.Shell.Material });
            mr.shadowCastingMode = ShadowCastingMode.On;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            return go;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector3> ToIl2Cpp(List<Vector3> src)
        {
            var l = new Il2CppSystem.Collections.Generic.List<Vector3>(src.Count);
            foreach (var v in src) l.Add(v);
            return l;
        }

        private static Il2CppSystem.Collections.Generic.List<Vector2> ToIl2Cpp(List<Vector2> src)
        {
            var l = new Il2CppSystem.Collections.Generic.List<Vector2>(src.Count);
            foreach (var v in src) l.Add(v);
            return l;
        }

        private static Il2CppStructArray<int> ToIl2Cpp(List<int> src)
        {
            var a = new Il2CppStructArray<int>(src.Count);
            for (int i = 0; i < src.Count; i++) a[i] = src[i];
            return a;
        }

        /// <summary>
        /// Sweeps run <paramref name="index"/> of each sample's cross-section along samples i0..i1; quads face the left
        /// side of the run.
        /// </summary>
        private static void Sweep(MeshData md, RouteCurve curve, List<List<Run>> profiles, int index, int i0, int i1, Vector3 origin, float tiling)
        {
            var first = profiles[0][index];
            int m = first.Points.Count;
            var across = new float[m];
            int baseIndex = md.Vertices.Count;
            for (int s = i0; s <= i1; s++)
            {
                var pts = profiles[s - i0][index].Points;
                for (int k = 1; k < m; k++) across[k] = across[k - 1] + Vector2.Distance(pts[k - 1], pts[k]);
                Vector3 c = curve.Points[s] - origin, r = curve.Right(s);
                float v = curve.Distance[s] / tiling;
                for (int k = 0; k < m; k++)
                {
                    var p = pts[k];
                    md.Vertices.Add(c + r * p.x + Vector3.up * p.y);
                    md.Uvs.Add(new Vector2(across[k] / tiling, v));
                }
            }
            var tris = md.Tris[first.Sub];
            int rows = i1 - i0;
            for (int s = 0; s < rows; s++)
                for (int k = 0; k < m - 1; k++)
                {
                    int a = baseIndex + s * m + k, bb = a + 1, d = a + m, cc = d + 1;
                    tris.Add(a); tris.Add(d); tris.Add(cc);
                    tris.Add(a); tris.Add(cc); tris.Add(bb);
                }
        }

        /// <summary>Closes the gap between the inside and the shell at a tunnel mouth.</summary>
        private static void Ring(MeshData md, BuiltConnection conn, int s, Vector3 origin, Vector3 facing, float tiling)
        {
            var d = conn.Definition;
            var curve = conn.Curve;
            float w = HalfWidth(conn, s), e = w + d.ShellThickness;
            var inner = new List<Vector2> { new(w, 0f), new(w, d.WallHeight) };
            var outer = new List<Vector2> { new(e, SlabBottom), new(e, d.WallHeight) };
            for (int i = 1; i < ArcSegments; i++)
            {
                float a = (float)i / ArcSegments * Mathf.PI;
                inner.Add(new Vector2(w * Mathf.Cos(a), d.WallHeight + (d.CrownHeight - d.WallHeight) * Mathf.Sin(a)));
                outer.Add(new Vector2(e * Mathf.Cos(a), d.WallHeight + (d.CrownHeight + d.ShellThickness - d.WallHeight) * Mathf.Sin(a)));
            }
            inner.Add(new Vector2(-w, d.WallHeight));
            inner.Add(new Vector2(-w, 0f));
            outer.Add(new Vector2(-e, d.WallHeight));
            outer.Add(new Vector2(-e, SlabBottom));

            Vector3 c = curve.Points[s] - origin, r = curve.Right(s);
            Vector3 W(Vector2 p) => c + r * p.x + Vector3.up * p.y;
            int n = inner.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Quad(md, W(inner[i]), W(inner[j]), W(outer[j]), W(outer[i]), facing, tiling, r);
            }
        }

        /// <summary>
        /// A frame around the mouth: from the outside of the shell to the same outline <see cref="ConnectionDefinition.PortalMargin"/>
        /// wider and higher, so its top follows the vault. Its outer edge runs <see cref="FacadeDepth"/> back into the
        /// slope, so seen from the side it is a slab and not a sheet.
        /// </summary>
        private static void Facade(MeshData md, BuiltConnection conn, int s, Vector3 origin, Vector3 facing, float tiling)
        {
            var d = conn.Definition;
            var curve = conn.Curve;
            float m = d.PortalMargin;
            if (m <= 0f) return;
            float e = HalfWidth(conn, s) + d.ShellThickness, rise = d.CrownHeight + d.ShellThickness - d.WallHeight;
            var shell = Outline(e, d.WallHeight, rise);
            var outer = Outline(e + m, d.WallHeight, rise + m);

            Vector3 c = curve.Points[s] - origin, r = curve.Right(s), back = -facing * FacadeDepth;
            Vector3 W(Vector2 p) => c + r * p.x + Vector3.up * p.y;
            for (int i = 0; i < shell.Count - 1; i++)
            {
                Quad(md, W(shell[i]), W(shell[i + 1]), W(outer[i + 1]), W(outer[i]), facing, tiling, r, SubWall);
                var mid = (outer[i] + outer[i + 1]) * 0.5f;
                var outward = r * mid.x + Vector3.up * (mid.y - d.WallHeight);
                Quad(md, W(outer[i]), W(outer[i + 1]), W(outer[i + 1]) + back, W(outer[i]) + back, outward, tiling, r, SubWall);
            }
        }

        private const float FacadeDepth = 1.5f;

        /// <summary>Walls from the slab bottom up to <paramref name="wallHeight"/>, then a half ellipse over them, right to left.</summary>
        private static List<Vector2> Outline(float halfWidth, float wallHeight, float rise)
        {
            var pts = new List<Vector2> { new(halfWidth, SlabBottom), new(halfWidth, wallHeight) };
            for (int i = 1; i < ArcSegments; i++)
            {
                float a = (float)i / ArcSegments * Mathf.PI;
                pts.Add(new Vector2(halfWidth * Mathf.Cos(a), wallHeight + rise * Mathf.Sin(a)));
            }
            pts.Add(new Vector2(-halfWidth, wallHeight));
            pts.Add(new Vector2(-halfWidth, SlabBottom));
            return pts;
        }

        private static void Quad(MeshData md, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing, float tiling, Vector3 right, int sub = SubShell)
        {
            int i = md.Vertices.Count;
            foreach (var p in new[] { a, b, c, d })
            {
                md.Vertices.Add(p);
                md.Uvs.Add(new Vector2(Vector3.Dot(p, right) / tiling, p.y / tiling));
            }
            var tris = md.Tris[sub];
            // The face of triangle (a, b, c) is on the side Cross(b - a, c - a) points to, as in Mesh.RecalculateNormals.
            bool front = Vector3.Dot(Vector3.Cross(b - a, c - a), facing) > 0f;
            if (front) { tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3); }
            else { tris.Add(i); tris.Add(i + 2); tris.Add(i + 1); tris.Add(i); tris.Add(i + 3); tris.Add(i + 2); }
        }

        private static int AddLights(BuiltConnection conn, Transform chunk, int i0, int i1, Materials mats)
        {
            var def = conn.Definition;
            var curve = conn.Curve;
            int added = 0;
            float d0 = curve.Distance[i0], d1 = curve.Distance[i1];
            for (float d = d0 + def.LightSpacing * 0.5f; d < d1; d += def.LightSpacing)
            {
                int s = curve.IndexAt(d);
                Vector3 p = curve.Points[s] + Vector3.up * (def.CrownHeight - 0.4f);
                var go = new GameObject($"{chunk.name}.Lamp{added}");
                go.transform.SetParent(chunk, false);
                go.transform.SetPositionAndRotation(p, Quaternion.LookRotation(curve.Forward(s)));
                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = def.LightRange;
                light.intensity = def.LightIntensity;
                light.color = def.LightColor;
                light.shadows = LightShadows.None;

                if (mats.Lamp != null)
                {
                    var fixture = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    fixture.name = go.name + ".Fixture";
                    var col = fixture.GetComponent<Collider>();
                    if (col != null) Object.DestroyImmediate(col);
                    fixture.transform.SetParent(go.transform, false);
                    fixture.transform.localPosition = new Vector3(0f, 0.25f, 0f);
                    fixture.transform.localScale = new Vector3(0.5f, 0.1f, 1.2f);
                    var fr = fixture.GetComponent<MeshRenderer>();
                    fr.sharedMaterial = mats.Lamp;
                    fr.shadowCastingMode = ShadowCastingMode.Off;
                }
                added++;
            }
            return added;
        }

        /// <summary>
        /// Footprint boxes over the few metres of existing road before and after the built range. Terrain there that
        /// reaches above the road surface would lie across the threshold as a strip of ground; terrain under the road
        /// stays, because the footprint starts at the road surface.
        /// </summary>
        private static void AddApproach(BuiltConnection conn, (float from, float to) range)
        {
            if (range.to <= range.from) return;
            var curve = conn.Curve;
            float top = conn.Definition.WallHeight;
            void Add(float a, float c)
            {
                if (c - a < 0.1f) return;
                float w = Mathf.Max(HalfWidth(conn, curve.IndexAt(a)), HalfWidth(conn, curve.IndexAt(c)));
                conn.Footprint.Add(OrientedBox.Along(curve.PointAt(a), curve.PointAt(c), w * 2f, -0.02f, top));
            }
            Add(Mathf.Max(0f, range.from - ApproachLength), range.from);
            Add(range.to, Mathf.Min(curve.Length, range.to + ApproachLength));
        }

        private const float ApproachLength = 6f;

        /// <summary>Clearance, blocking and footprint boxes for samples i0..i1, about 2 m each.</summary>
        private static void AddBoxes(BuiltConnection conn, RouteStyle style, int i0, int i1)
        {
            var def = conn.Definition;
            var curve = conn.Curve;
            float top = style == RouteStyle.Tunnel ? def.CrownHeight : def.WallHeight;
            float footprintTop = style == RouteStyle.Tunnel ? def.CrownHeight + 0.5f : def.WallHeight;
            const int step = 4;
            for (int s = i0; s < i1; s += step)
            {
                int e = Mathf.Min(i1, s + step);
                float w = Mathf.Max(HalfWidth(conn, s), HalfWidth(conn, e));
                float footprintWidth = style == RouteStyle.Tunnel ? w + def.ShellThickness * 0.6f : w;
                Vector3 a = curve.Points[s], bb = curve.Points[e];
                // Straight boxes along a bend leave a wedge between each other on the outer side, and the terrain in that
                // wedge stands inside the tube; each box reaches a little into the next. Clearance and blocking boxes
                // stop at the ends of the range, so nothing outside a portal is cut.
                var along = (bb - a).normalized * FootprintOverlap;
                Vector3 ia = s > i0 ? a - along : a, ib = e < i1 ? bb + along : bb;
                conn.Interior.Add(OrientedBox.Along(ia, ib, w * 2f, -0.15f, top));
                conn.Blocking.Add(OrientedBox.Along(ia, ib, w * 2f, 0.3f, top));
                conn.Footprint.Add(OrientedBox.Along(a - along, bb + along, footprintWidth * 2f, -0.02f, footprintTop));
            }
        }

        private const float FootprintOverlap = 1f;
    }
}
