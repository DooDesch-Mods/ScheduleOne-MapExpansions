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
            for (int k = 0; k < stretches.Count; k++)
            {
                var (style, from, to) = stretches[k];
                if (style == RouteStyle.Existing || to - from < 0.1f) continue;
                int i0 = curve.IndexAt(from), i1 = curve.IndexAt(to);
                if (i1 <= i0) continue;
                // An end of the built range, where the road goes on as an existing one or the route stops.
                bool openStart = k == 0 || stretches[k - 1].style == RouteStyle.Existing;
                bool openEnd = k == stretches.Count - 1 || stretches[k + 1].style == RouteStyle.Existing;
                AddBoxes(conn, style, i0, i1, openStart, openEnd);

                float chunkLength = style == RouteStyle.Tunnel ? def.LightSpacing * 2f : 32f;
                int start = i0;
                while (start < i1)
                {
                    int end = Mathf.Min(i1, curve.IndexAt(curve.Distance[start] + chunkLength));
                    if (end <= start) end = Mathf.Min(i1, start + 1);
                    // A short rest joins the chunk before it.
                    if (i1 - end < 4) end = i1;
                    var go = BuildChunk(b, conn, style, start, end, start == i0, end == i1, start == i0 && openStart, end == i1 && openEnd,
                                        mats, layer, chunks);
                    if (go != null)
                    {
                        conn.SurfaceChunks.Add(go);
                        chunks++;
                        if (style == RouteStyle.Tunnel) lights += AddLights(conn, go.transform, start, end, mats);
                    }
                    start = end;
                }
            }
            var tunnels = stretches.Where(s => s.style == RouteStyle.Tunnel && s.to - s.from >= 0.1f).ToList();
            int covers = 0;
            if (tunnels.Count > 0)
            {
                if (Cover(b, conn, tunnels[0].from, def.StartCover, true, mats, layer)) covers++;
                if (Cover(b, conn, tunnels[tunnels.Count - 1].to, def.EndCover, false, mats, layer)) covers++;
            }
            var range = conn.BuiltRange;
            AddApproach(conn, range);
            Core.Log.Msg($"tunnel: '{b.Id}' {conn.Name}: route {curve.Length:0} m, {curve.Count} samples, " +
                         $"{string.Join(", ", stretches.Select(s => $"{s.style} {s.from:0}..{s.to:0}"))}; built {range.from:0}..{range.to:0} m in {chunks} chunks, " +
                         $"{lights} lamps, {covers} rock mantles, {conn.Interior.Count} clearance boxes, layer {LayerMask.LayerToName(layer)}, {sw.ElapsedMilliseconds} ms");
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
                new(SubWalkway, new(hw, 0f), new(hw, ch)),
                new(SubWalkway, new(hw, ch), new(w, ch)),
                new(SubWalkway, new(-w, ch), new(-hw, ch)),
                new(SubWalkway, new(-hw, ch), new(-hw, 0f)),
            };
            if (median)
            {
                float mw = conn.Curve.MedianWidth[s], half = mw * 0.5f, top = half * 0.6f;
                float mh = d.MedianHeight * Mathf.Clamp01(mw / MedianFullWidth);
                // One road run per carriageway, so each gets its own centre line.
                runs.Add(new Run(SubRoad, new(-hw, 0f), new(-half, 0f)));
                runs.Add(new Run(SubRoad, new(half, 0f), new(hw, 0f)));
                runs.Add(new Run(SubWalkway, new(-half, 0f), new(-top, mh), new(top, mh), new(half, 0f)));
            }
            else runs.Add(new Run(SubRoad, new(-hw, 0f), new(hw, 0f)));
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

        /// <param name="first">The chunk starts its stretch.</param>
        /// <param name="last">The chunk ends its stretch.</param>
        /// <param name="openFirst">The chunk starts the built range, see <see cref="BuiltConnection.BuiltRange"/>.</param>
        /// <param name="openLast">The chunk ends the built range.</param>
        private static GameObject BuildChunk(BuiltExpansion b, BuiltConnection conn, RouteStyle style, int i0, int i1, bool first, bool last,
                                             bool openFirst, bool openLast, Materials mats, int layer, int number)
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
            if (median)
            {
                MedianCap(md, conn, i0, origin, -curve.Forward(i0), mats.Walkway.Tiling);
                MedianCap(md, conn, i1, origin, curve.Forward(i1), mats.Walkway.Tiling);
            }
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
                if (openFirst) WingWall(md, conn, i0, origin, def.StartWingWalls, true, mats.Wall.Tiling);
                if (openLast) WingWall(md, conn, i1, origin, def.EndWingWalls, false, mats.Wall.Tiling);
            }
            if (openFirst) EndCap(md, conn, style, i0, origin, -curve.Forward(i0), mats);
            if (openLast) EndCap(md, conn, style, i1, origin, curve.Forward(i1), mats);
            AttachMesh(b, go, md, mats);
            return go;
        }

        /// <summary>Gives <paramref name="go"/> the mesh of <paramref name="md"/> with the four materials and a mesh collider on it.</summary>
        private static void AttachMesh(BuiltExpansion b, GameObject go, MeshData md, Materials mats)
        {
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
        }

        /// <summary>Metres by which a rock mantle stays inside the facade's outline, so the facade's edge stands proud of the rock.</summary>
        private const float CoverInset = 0.3f;

        /// <summary>
        /// The rock mantle over the tunnel behind the portal at route distance <paramref name="at"/>, over
        /// <paramref name="length"/> metres into the tunnel: the facade's outline, a little inside it, swept along the
        /// route, with flanks from the foot of the vault down to <see cref="WingDepth"/> under the road at 45°. At the
        /// portal the facade closes it and the flanks get a face of their own a few centimetres behind the facade; the
        /// far end is closed against the shell.
        /// </summary>
        private static bool Cover(BuiltExpansion b, BuiltConnection conn, float at, float length, bool atStart, Materials mats, int layer)
        {
            if (length <= 0f) return false;
            var d = conn.Definition;
            var curve = conn.Curve;
            string end = atStart ? "start" : "end";
            if (d.PortalMargin <= CoverInset)
            {
                Core.Log.Warning($"tunnel: {conn.Name}: rock mantle at the {end} portal left out; it follows the facade, and PortalMargin {d.PortalMargin:0.##} m builds none");
                return false;
            }
            float far = atStart ? Mathf.Min(curve.Length, at + length) : Mathf.Max(0f, at - length);
            int portal = curve.IndexAt(at), back = curve.IndexAt(far);
            int i0 = Mathf.Min(portal, back), i1 = Mathf.Max(portal, back);
            if (i1 <= i0)
            {
                Core.Log.Warning($"tunnel: {conn.Name}: rock mantle at the {end} portal left out; {length:0.##} m cover no sample of the route");
                return false;
            }

            var go = new GameObject($"{conn.Root.name}.Cover.{end}") { layer = layer };
            go.transform.SetParent(conn.Root.transform, false);
            Vector3 origin = curve.Points[i0];
            go.transform.position = origin;

            var md = new MeshData();
            var profiles = new List<List<Run>>(i1 - i0 + 1);
            for (int s = i0; s <= i1; s++) profiles.Add(CoverRuns(conn, s));
            for (int k = 0; k < profiles[0].Count; k++) Sweep(md, curve, profiles, k, i0, i1, origin, mats.Shell.Tiling);

            Vector3 outward = atStart ? -curve.Forward(portal) : curve.Forward(portal);
            CoverFlanks(md, conn, portal, origin, outward, -outward * 0.05f, mats.Shell.Tiling);
            CoverFlanks(md, conn, back, origin, -outward, Vector3.zero, mats.Shell.Tiling);
            CoverEnd(md, conn, back, origin, -outward, mats.Shell.Tiling);

            AttachMesh(b, go, md, mats);
            Core.Log.Msg($"tunnel: '{b.Id}' {conn.Name}: rock mantle at the {end} portal over {Mathf.Abs(curve.Distance[i1] - curve.Distance[i0]):0.#} m " +
                         $"({curve.Distance[i0]:0}..{curve.Distance[i1]:0} m)");
            return true;
        }

        /// <summary>Half width and rise of a rock mantle's vault at sample s, and the half width of its flanks' foot.</summary>
        private static (float x, float rise, float foot) CoverSize(BuiltConnection conn, int s)
        {
            var d = conn.Definition;
            float m = d.PortalMargin - CoverInset, x = HalfWidth(conn, s) + d.ShellThickness + m;
            return (x, d.CrownHeight + d.ShellThickness - d.WallHeight + m, x + d.WallHeight + WingDepth);
        }

        /// <summary>
        /// The mantle's cross-section from the left flank's foot over the vault to the right one's, facing out. Flanks and
        /// vault are runs of their own, so the edge between them keeps its own normals and is not shaded round.
        /// </summary>
        private static List<Run> CoverRuns(BuiltConnection conn, int s)
        {
            var (x, rise, foot) = CoverSize(conn, s);
            float h = conn.Definition.WallHeight;
            return new List<Run>
            {
                new(SubShell, new Vector2(-foot, -WingDepth), new Vector2(-x, h)),
                Arc(SubShell, x, h, rise, false),
                new(SubShell, new Vector2(x, h), new Vector2(foot, -WingDepth)),
            };
        }

        /// <summary>The faces of both flanks across the route at sample s, moved by <paramref name="shift"/>.</summary>
        private static void CoverFlanks(MeshData md, BuiltConnection conn, int s, Vector3 origin, Vector3 facing, Vector3 shift, float tiling)
        {
            var (x, _, foot) = CoverSize(conn, s);
            float h = conn.Definition.WallHeight;
            Vector3 c = conn.Curve.Points[s] - origin + shift, r = conn.Curve.Right(s);
            Vector3 W(float px, float py) => c + r * px + Vector3.up * py;
            foreach (float sign in new[] { -1f, 1f })
                Quad(md, W(sign * x, h), W(sign * foot, -WingDepth), W(sign * x, -WingDepth), W(sign * x, -WingDepth), facing, tiling, r);
        }

        /// <summary>Closes the mantle's vault against the outside of the shell at sample s.</summary>
        private static void CoverEnd(MeshData md, BuiltConnection conn, int s, Vector3 origin, Vector3 facing, float tiling)
        {
            var d = conn.Definition;
            var (x, rise, _) = CoverSize(conn, s);
            float e = HalfWidth(conn, s) + d.ShellThickness;
            var shell = Outline(e, d.WallHeight, d.CrownHeight + d.ShellThickness - d.WallHeight);
            var outer = Outline(x, d.WallHeight, rise);
            Vector3 c = conn.Curve.Points[s] - origin, r = conn.Curve.Right(s);
            Vector3 W(Vector2 p) => c + r * p.x + Vector3.up * p.y;
            for (int i = 0; i < shell.Count - 1; i++)
                Quad(md, W(shell[i]), W(shell[i + 1]), W(outer[i + 1]), W(outer[i]), facing, tiling, r);
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
        /// side of the run. A road run spans one texture repeat across when it is wider than the tiling, with the
        /// middle of the texture, where the road texture draws its centre line, in the middle of the run; a wider span
        /// would repeat the line beside it.
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
                float mid = (pts[0].x + pts[m - 1].x) * 0.5f, span = Mathf.Max(tiling, Mathf.Abs(pts[m - 1].x - pts[0].x));
                for (int k = 0; k < m; k++)
                {
                    var p = pts[k];
                    md.Vertices.Add(c + r * p.x + Vector3.up * p.y);
                    float u = first.Sub == SubRoad ? 0.5f + (p.x - mid) / span : across[k] / tiling;
                    md.Uvs.Add(new Vector2(u, v));
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

        /// <summary>
        /// Closes the end of the median barrier at sample s; the sweep only draws its sides, so where it starts in full
        /// height it would be open. Between two chunks the caps of both lie inside the barrier.
        /// </summary>
        private static void MedianCap(MeshData md, BuiltConnection conn, int s, Vector3 origin, Vector3 facing, float tiling)
        {
            var curve = conn.Curve;
            float mw = curve.MedianWidth[s];
            float mh = conn.Definition.MedianHeight * Mathf.Clamp01(mw / MedianFullWidth);
            if (mw <= 0f || mh <= 0.01f) return;
            float half = mw * 0.5f, top = half * 0.6f;
            Vector3 c = curve.Points[s] - origin, r = curve.Right(s);
            Vector3 W(float x, float y) => c + r * x + Vector3.up * y;
            Quad(md, W(-half, 0f), W(-top, mh), W(top, mh), W(half, 0f), facing, tiling, r, SubWalkway);
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

        /// <summary>
        /// Closes the cross-section where the built range ends: the walkway blocks on both sides and, outside a tunnel,
        /// whose ring closes the rest, the slab and the guard walls.
        /// </summary>
        private static void EndCap(MeshData md, BuiltConnection conn, RouteStyle style, int s, Vector3 origin, Vector3 facing, Materials mats)
        {
            var d = conn.Definition;
            var curve = conn.Curve;
            float hw = curve.RoadWidth[s] * 0.5f, w = HalfWidth(conn, s), ch = d.CurbHeight;
            Vector3 c = curve.Points[s] - origin, r = curve.Right(s);
            Vector3 W(float x, float y) => c + r * x + Vector3.up * y;
            Quad(md, W(hw, 0f), W(w, 0f), W(w, ch), W(hw, ch), facing, mats.Walkway.Tiling, r, SubWalkway);
            Quad(md, W(-w, 0f), W(-hw, 0f), W(-hw, ch), W(-w, ch), facing, mats.Walkway.Tiling, r, SubWalkway);
            if (style == RouteStyle.Tunnel) return;
            float o = style == RouteStyle.Road ? w + GuardThickness : w;
            Quad(md, W(-o, SlabBottom), W(o, SlabBottom), W(o, 0f), W(-o, 0f), facing, mats.Shell.Tiling, r, SubShell);
            if (style != RouteStyle.Road) return;
            float g = d.GuardHeight;
            Quad(md, W(w, 0f), W(o, 0f), W(o, g), W(w, g), facing, mats.Wall.Tiling, r, SubWall);
            Quad(md, W(-o, 0f), W(-w, 0f), W(-w, g), W(-o, g), facing, mats.Wall.Tiling, r, SubWall);
        }

        /// <summary>Metres between two cross-sections of a wing wall.</summary>
        private const float WingStep = 1f;

        /// <summary>Metres a wing wall reaches below the road, into the ground beside it.</summary>
        private const float WingDepth = 2f;

        /// <summary>
        /// Height of a wing wall over the road at sample s: where the outside of the shell meets the portal frame's
        /// outer edge, so the wall stays inside the frame's outline.
        /// </summary>
        private static float WingTop(BuiltConnection conn, int s)
        {
            var d = conn.Definition;
            float e = HalfWidth(conn, s) + d.ShellThickness, m = Mathf.Max(0f, d.PortalMargin);
            float rise = d.CrownHeight + d.ShellThickness - d.WallHeight + m;
            float x = Mathf.Clamp01(e / (e + m));
            return d.WallHeight + rise * Mathf.Sqrt(1f - x * x);
        }

        /// <summary>
        /// The tunnel's walls running on in front of the portal at sample s, on the given sides, between the inside of
        /// the wall and the outside of the shell, along the road over <see cref="ConnectionDefinition.ApproachLength"/>
        /// metres. Past the end of the route they go straight on.
        /// </summary>
        private static void WingWall(MeshData md, BuiltConnection conn, int s, Vector3 origin, WingWalls sides, bool atStart, float tiling)
        {
            if (sides == WingWalls.None) return;
            var d = conn.Definition;
            var curve = conn.Curve;
            float length = d.ApproachLength;
            if (length < WingStep)
            {
                Core.Log.Warning($"tunnel: {conn.Name}: wing walls at the {(atStart ? "start" : "end")} left out; ApproachLength {length:0.##} m is shorter than {WingStep} m");
                return;
            }
            float w = HalfWidth(conn, s), e = w + d.ShellThickness, top = WingTop(conn, s);
            int n = Mathf.CeilToInt(length / WingStep);
            float d0 = curve.Distance[s], dir = atStart ? -1f : 1f;
            var centres = new Vector3[n + 1];
            var rights = new Vector3[n + 1];
            for (int k = 0; k <= n; k++)
            {
                float dist = d0 + dir * length * k / n;
                if (dist < 0f) centres[k] = curve.Points[0] - curve.Forward(0) * -dist;
                else if (dist > curve.Length) centres[k] = curve.Points[curve.Count - 1] + curve.Forward(curve.Count - 1) * (dist - curve.Length);
                else centres[k] = curve.PointAt(dist);
                centres[k] -= origin;
                rights[k] = k == 0 ? curve.Right(s) : curve.Right(curve.IndexAt(dist));
            }
            Vector3 outward = (centres[n] - centres[n - 1]).normalized;
            // Seen from outside looking in, left is the route's left at the start and its right at the end.
            foreach (var (side, sign) in new[] { (WingWalls.Left, atStart ? -1f : 1f), (WingWalls.Right, atStart ? 1f : -1f) })
            {
                if ((sides & side) == 0) continue;
                Vector3 P(int k, float x, float y) => centres[k] + rights[k] * (sign * x) + Vector3.up * y;
                for (int k = 0; k < n; k++)
                {
                    Vector3 along = (centres[k + 1] - centres[k]).normalized, across = rights[k] * sign;
                    Quad(md, P(k, w, -WingDepth), P(k + 1, w, -WingDepth), P(k + 1, w, top), P(k, w, top), -across, tiling, along, SubWall);
                    Quad(md, P(k, e, -WingDepth), P(k + 1, e, -WingDepth), P(k + 1, e, top), P(k, e, top), across, tiling, along, SubWall);
                    Quad(md, P(k, w, top), P(k + 1, w, top), P(k + 1, e, top), P(k, e, top), Vector3.up, tiling, along, SubWall, across);
                }
                Quad(md, P(n, w, -WingDepth), P(n, e, -WingDepth), P(n, e, top), P(n, w, top), outward, tiling, rights[n] * sign, SubWall);
            }
        }

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

        /// <summary>A flat quad with planar texture coordinates along <paramref name="right"/> and <paramref name="up"/> (default: world up).</summary>
        private static void Quad(MeshData md, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing, float tiling, Vector3 right, int sub = SubShell,
                                 Vector3? up = null)
        {
            int i = md.Vertices.Count;
            var v = up ?? Vector3.up;
            foreach (var p in new[] { a, b, c, d })
            {
                md.Vertices.Add(p);
                md.Uvs.Add(new Vector2(Vector3.Dot(p, right) / tiling, Vector3.Dot(p, v) / tiling));
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
        /// stays, because the footprint starts at the road surface. Approach boxes over the same road, as wide as it,
        /// let the clearing cut the objects the definition names; on a side with a wing wall they reach into the wall
        /// and up to its top, so what they cut ends behind it.
        /// </summary>
        private static void AddApproach(BuiltConnection conn, (float from, float to) range)
        {
            if (range.to <= range.from) return;
            var curve = conn.Curve;
            var def = conn.Definition;
            float length = Mathf.Max(0f, def.ApproachLength);
            void Add(float a, float c, int portal, WingWalls wings, bool atStart)
            {
                if (c - a < 0.1f) return;
                int ia = curve.IndexAt(a), ic = curve.IndexAt(c);
                Vector3 pa = curve.PointAt(a), pc = curve.PointAt(c);
                float w = Mathf.Max(HalfWidth(conn, ia), HalfWidth(conn, ic));
                conn.ApproachFootprint.Add(OrientedBox.Along(pa, pc, w * 2f, -0.02f, def.WallHeight));

                float half = Mathf.Max(curve.RoadWidth[ia], curve.RoadWidth[ic]) * 0.5f, top = def.CrownHeight;
                float minX = -half, maxX = half;
                // The sample on the portal is the last one of the leg before it, so the built side is the next one at the start.
                int inside = atStart ? Mathf.Min(curve.Count - 1, portal + 1) : portal;
                bool tunnel = curve.Style[inside] == RouteStyle.Tunnel;
                if (tunnel && wings != WingWalls.None)
                {
                    float wall = HalfWidth(conn, portal) + def.ShellThickness * 0.5f;
                    // Left seen from outside looking in is the route's left at the start and its right at the end.
                    bool negative = (wings & (atStart ? WingWalls.Left : WingWalls.Right)) != 0;
                    bool positive = (wings & (atStart ? WingWalls.Right : WingWalls.Left)) != 0;
                    if (negative) minX = -wall;
                    if (positive) maxX = wall;
                    top = Mathf.Max(top, WingTop(conn, portal));
                }
                var shift = Vector3.Cross(Vector3.up, new Vector3(pc.x - pa.x, 0f, pc.z - pa.z).normalized) * ((minX + maxX) * 0.5f);
                conn.Approach.Add(OrientedBox.Along(pa + shift, pc + shift, maxX - minX, -0.15f, top));
            }
            Add(Mathf.Max(0f, range.from - length), range.from, curve.IndexAt(range.from), def.StartWingWalls, true);
            Add(range.to, Mathf.Min(curve.Length, range.to + length), curve.IndexAt(range.to), def.EndWingWalls, false);
        }

        /// <summary>
        /// Clearance, blocking and footprint boxes for samples i0..i1, about 2 m each. Footprint boxes reach a little past
        /// their neighbours, but not past an open end of the built range, where the ground in front of the portal stays.
        /// </summary>
        private static void AddBoxes(BuiltConnection conn, RouteStyle style, int i0, int i1, bool openStart, bool openEnd)
        {
            var def = conn.Definition;
            var curve = conn.Curve;
            float top = style == RouteStyle.Tunnel ? def.CrownHeight : def.WallHeight;
            float footprintTop = style == RouteStyle.Tunnel ? def.CrownHeight + 0.5f : def.WallHeight;
            const int step = 4;
            for (int s = i0, e; s < i1; s = e)
            {
                // A box's ends stand across its own chord. At an open end the portal stands across the route's direction
                // there, and on a bend a chord over several samples is turned against it: one corner of the box reaches
                // past the portal and cuts what lies outside. The last samples before an open end get a box each.
                bool nearEnd = (openStart && s < i0 + step) || (openEnd && s + step > i1 - step);
                e = Mathf.Min(i1, s + (nearEnd ? 1 : step));
                float w = Mathf.Max(HalfWidth(conn, s), HalfWidth(conn, e));
                float footprintWidth = style == RouteStyle.Tunnel ? w + def.ShellThickness * 0.6f : w;
                Vector3 a = curve.Points[s], bb = curve.Points[e];
                // Straight boxes along a bend leave a wedge between each other on the outer side, and the terrain in that
                // wedge stands inside the tube; each box reaches a little into the next. Clearance and blocking boxes
                // stop at the ends of the range, so nothing outside a portal is cut.
                var chord = (bb - a).normalized;
                float back = s == i0 ? 0f : openStart ? Mathf.Min(FootprintOverlap, curve.Distance[s] - curve.Distance[i0]) : FootprintOverlap;
                float ahead = e == i1 ? 0f : openEnd ? Mathf.Min(FootprintOverlap, curve.Distance[i1] - curve.Distance[e]) : FootprintOverlap;
                Vector3 ia = a - chord * back, ib = bb + chord * ahead;
                conn.Interior.Add(OrientedBox.Along(ia, ib, w * 2f, -0.15f, top));
                conn.Blocking.Add(OrientedBox.Along(ia, ib, w * 2f, 0.3f, top));
                // A hole cell whose centre lies in the footprint opens all of the cell, and at a portal half of it lies
                // outside, beside the road, with nothing under it. The last metre before an open end is a portal strip:
                // only terrain above the road becomes a hole there, and the floor covers what stays below it. The
                // footprint, overlap included, stops at the strip.
                float lo = openStart ? curve.Distance[i0] + PortalStrip : float.MinValue;
                float hi = openEnd ? curve.Distance[i1] - PortalStrip : float.MaxValue;
                if (s == i0 && openStart)
                    conn.PortalFootprint.Add(OrientedBox.Along(a, curve.PointAt(lo), footprintWidth * 2f, -0.02f, footprintTop));
                if (e == i1 && openEnd)
                    conn.PortalFootprint.Add(OrientedBox.Along(curve.PointAt(hi), bb, footprintWidth * 2f, -0.02f, footprintTop));
                float da = s > i0 || !openStart ? curve.Distance[s] - FootprintOverlap : curve.Distance[s];
                float db = e < i1 || !openEnd ? curve.Distance[e] + FootprintOverlap : curve.Distance[e];
                da = Mathf.Max(da, lo);
                db = Mathf.Min(db, hi);
                if (db - da < 0.05f) continue;
                Vector3 fa = a + chord * (da - curve.Distance[s]), fb = bb + chord * (db - curve.Distance[e]);
                conn.Footprint.Add(OrientedBox.Along(fa, fb, footprintWidth * 2f, -0.02f, footprintTop));
            }
        }

        private const float FootprintOverlap = 1f;

        /// <summary>Length of the strip at an open end of the built range where terrain under the road stays.</summary>
        private const float PortalStrip = 1f;
    }
}
