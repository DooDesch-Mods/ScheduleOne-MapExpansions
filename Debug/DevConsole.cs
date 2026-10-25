#if DEBUG
using System.Collections;
using System.Globalization;
using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.FX;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Weather;
using MapExpansions.Lifecycle;
using MapExpansions.Regions;
using MapExpansions.Source;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Dev console commands for testing expansions. Compiled out of Release.
    ///
    /// mxstate                         state of every expansion, its connections, regions and navigation links
    /// mxwhere                         player position, region and the expansion under it
    /// mxtp x y z | id:location        teleport the player to a world point or to a named place of an expansion
    /// mxcam ex ey ez tx ty tz | off   park the camera at a world position looking at a world point
    /// mxframe [seconds]               frame time statistics over a sample window
    /// mxfind name [main|exp]          renderers whose name contains name
    /// mxfog on|off                    the screen fog render feature and the volumetric fog
    /// mxbox x0 y0 z0 x1 y1 z1 [filter] renderers and colliders whose bounds meet a world box, with their paths
    /// mxline ax az bx bz [step]       every surface a ray from above meets, sampled along a line
    /// mxray ax ay az bx by bz         every collider a ray from a to b meets, nearest first
    /// mxedges x0 y0 z0 x1 y1 z1 filter open mesh edges inside a world box, as chains, of renderers whose path contains filter
    /// mxlight                         directional lights, shadow distance and the render pipeline's light layers
    /// mxhide filter [on]              stop (or resume) drawing renderers and terrains whose path contains filter
    /// mxterrain filter [pixelError]   render settings of terrains whose name contains filter, optionally a new pixel error
    /// mxnav ...                       NPC navigation tests, see NavTest
    /// mxveh ...                       vehicle navigation tests, see VehicleTest
    /// </summary>
    internal static class DevConsole
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static int _lastFrame = -1;
        private static string _lastSig = "";

        internal static float F(string s) => float.Parse(s, Ci);

        internal static Vector3 V(string[] p, int i) => new(F(p[i]), F(p[i + 1]), F(p[i + 2]));

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;
            string cmd = parts[0].ToLowerInvariant();
            if (!cmd.StartsWith("mx", StringComparison.Ordinal)) return false;

            // The string overload calls the list overload, so one submission can reach both prefixes.
            string sig = string.Join(" ", parts);
            if (Time.frameCount == _lastFrame && sig == _lastSig) return true;
            _lastFrame = Time.frameCount;
            _lastSig = sig;

            try
            {
                switch (cmd)
                {
                    case "mxstate": State(); break;
                    case "mxwhere": Where(); break;
                    case "mxtp": Tp(parts); break;
                    case "mxcam": Cam(parts); break;
                    case "mxframe": MelonCoroutines.Start(Frame(parts.Length > 1 ? F(parts[1]) : 3f)); break;
                    case "mxfind" when parts.Length > 1: Find(parts[1], parts.Length > 2 ? parts[2] : ""); break;
                    case "mxfog" when parts.Length > 1: Fog(parts[1] == "on"); break;
                    case "mxbox" when parts.Length > 6: Box(V(parts, 1), V(parts, 4), parts.Length > 7 ? parts[7] : ""); break;
                    case "mxlight": Lights(); break;
                    case "mxhide" when parts.Length > 1:
                        bool on = parts.Length > 2 && parts[^1] == "on";
                        Hide(string.Join(" ", parts, 1, parts.Length - (on ? 2 : 1)), on);
                        break;
                    case "mxterrain" when parts.Length > 1: TerrainInfo(parts[1], parts.Length > 2 ? F(parts[2]) : -1f); break;
                    case "mxray" when parts.Length > 6: Ray(V(parts, 1), V(parts, 4)); break;
                    case "mxedges" when parts.Length > 7: Edges(V(parts, 1), V(parts, 4), string.Join(" ", parts, 7, parts.Length - 7)); break;
                    case "mxline" when parts.Length > 4: Line(F(parts[1]), F(parts[2]), F(parts[3]), F(parts[4]), parts.Length > 5 ? F(parts[5]) : 4f); break;
                    case "mxnav": NavTest.Handle(parts); break;
                    case "mxveh": VehicleTest.Handle(parts); break;
                    default:
                        Core.Log.Warning($"[dev] unknown command '{cmd}'");
                        return false;
                }
            }
            catch (Exception e)
            {
                Core.Log.Error($"[dev] {cmd} failed: {e}");
            }
            return true;
        }

        private static void State()
        {
            foreach (var id in MapExpansionsApi.Expansions)
            {
                var b = BuildPipeline.Get(id);
                string conns = b == null ? "not built" : string.Join(", ", b.Connections.Select(c =>
                {
                    var (from, to) = c.BuiltRange;
                    return $"{c.Name} {c.Curve.Length:0} m, built {from:0}..{to:0} m, {c.Interior.Count} boxes";
                }));
                Core.Log.Msg($"[dev] mxstate: '{id}' {MapExpansionsApi.GetState(id)}; {conns}");
            }
            foreach (var r in RegionRegistry.All)
                Core.Log.Msg($"[dev] mxstate: region {(int)r.Region} '{r.Definition.Name}' of '{r.ExpansionId}', stand-in {r.StandIn}, " +
                             $"zone {(r.Zone != null ? "present" : "missing")}");
            Core.Log.Msg($"[dev] mxstate: {NavLinks.Count} NavMesh links, vehicle graph mask extra {Navigation.VehicleNavigation.ExtraMask}");
        }

        private static void Where()
        {
            var p = Player.Local;
            if (p == null)
            {
                Core.Log.Warning("[dev] mxwhere: no local player");
                return;
            }
            var pos = p.transform.position;
            var map = Singleton<Map>.Instance;
            string region = map != null ? ((int)map.GetRegionFromPosition(pos)).ToString(Ci) : "no map";
            string exp = MapExpansionsApi.ExpansionAt(pos) ?? "-";
            string local = exp != "-" && MapExpansionsApi.TryToLocal(exp, pos, out var l) ? $", local {l}" : "";
            Core.Log.Msg($"[dev] mxwhere: {pos}, region {region}, player region {(int)p.CurrentRegion}, expansion {exp}{local}");
        }

        private static void Tp(string[] parts)
        {
            Vector3 pos;
            if (parts.Length == 2 && parts[1].Contains(':'))
            {
                var bits = parts[1].Split(':');
                if (!MapExpansionsApi.TryResolve(new LocationRef(bits[0], bits[1]), out pos, out _))
                {
                    Core.Log.Warning($"[dev] mxtp: location '{parts[1]}' is unknown or not placed");
                    return;
                }
            }
            else if (parts.Length >= 4) pos = V(parts, 1);
            else
            {
                Core.Log.Warning("[dev] mxtp x y z | id:location");
                return;
            }
            var mv = PlayerSingleton<PlayerMovement>.Instance;
            if (mv == null)
            {
                Core.Log.Warning("[dev] mxtp: no PlayerMovement");
                return;
            }
            mv.Teleport(pos, false);
            Core.Log.Msg($"[dev] mxtp: teleported to {pos}");
        }

        private static void Cam(string[] parts)
        {
            var cam = PlayerSingleton<PlayerCamera>.Instance;
            if (cam == null)
            {
                Core.Log.Warning("[dev] mxcam: no PlayerCamera");
                return;
            }
            if (parts.Length > 1 && parts[1] == "off")
            {
                cam.StopTransformOverride(0f, true, true);
                Core.Log.Msg("[dev] mxcam: camera back on the player");
                return;
            }
            if (parts.Length < 7)
            {
                Core.Log.Warning("[dev] mxcam ex ey ez tx ty tz | off");
                return;
            }
            var eye = V(parts, 1);
            var at = V(parts, 4);
            cam.OverrideTransform(eye, Quaternion.LookRotation(at - eye), 0f, false);
            Core.Log.Msg($"[dev] mxcam: eye {eye} looking at {at}");
        }

        private static IEnumerator Frame(float seconds)
        {
            var samples = new List<float>();
            float end = Time.realtimeSinceStartup + seconds;
            yield return null;
            while (Time.realtimeSinceStartup < end)
            {
                samples.Add(Time.unscaledDeltaTime * 1000f);
                yield return null;
            }
            if (samples.Count == 0)
            {
                Core.Log.Warning("[dev] mxframe: no frames sampled");
                yield break;
            }
            samples.Sort();
            float P(float q) => samples[Math.Min(samples.Count - 1, (int)(q * samples.Count))];
            Core.Log.Msg($"[dev] mxframe: {samples.Count} frames in {seconds:0.#} s, avg {samples.Average():0.00} ms, p50 {P(0.5f):0.00}, " +
                         $"p95 {P(0.95f):0.00}, max {samples[samples.Count - 1]:0.00}");
        }

        private static void Find(string part, string scope)
        {
            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>(true))
            {
                if (r.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string exp = BuildPipeline.All.FirstOrDefault(b => b.IsOurs(r.transform))?.Id;
                if ((scope == "exp" && exp == null) || (scope == "main" && exp != null)) continue;
                var b = r.bounds;
                var t = r.transform;
                Core.Log.Msg($"[dev] mxfind {exp ?? "main"} '{r.name}' under '{(t.parent != null ? t.parent.name : "-")}' enabled {r.enabled}: " +
                             $"x {b.min.x:0}..{b.max.x:0} y {b.min.y:0}..{b.max.y:0} z {b.min.z:0}..{b.max.z:0}");
                if (++n >= 25) break;
            }
            Core.Log.Msg($"[dev] mxfind '{part}' {scope}: {n} renderers listed");
        }

        private static string PathOf(Transform t, int depth = 5)
        {
            var names = new List<string>();
            for (var p = t; p != null && names.Count < depth; p = p.parent) names.Add(p.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static string B(Bounds b) => $"x {b.min.x:0.0}..{b.max.x:0.0} y {b.min.y:0.0}..{b.max.y:0.0} z {b.min.z:0.0}..{b.max.z:0.0}";

        private static void Box(Vector3 a, Vector3 c, string filter)
        {
            var box = new Bounds();
            box.SetMinMax(Vector3.Min(a, c), Vector3.Max(a, c));
            bool Match(Transform t) => filter.Length == 0 || PathOf(t, 8).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            int r = 0, col = 0;
            foreach (var x in Object.FindObjectsOfType<Renderer>(false))
            {
                if (x == null || !x.bounds.Intersects(box) || !Match(x.transform)) continue;
                var m = x.sharedMaterial;
                Core.Log.Msg($"[dev] mxbox renderer '{PathOf(x.transform)}' {x.GetIl2CppType().Name} enabled {x.enabled} layer {LayerMask.LayerToName(x.gameObject.layer)}: " +
                             $"{B(x.bounds)}, material '{(m != null ? m.name : "-")}' shader '{(m != null && m.shader != null ? m.shader.name : "-")}'");
                if (++r >= 80) break;
            }
            foreach (var x in Physics.OverlapBox(box.center, box.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide))
            {
                if (x == null || !Match(x.transform)) continue;
                Core.Log.Msg($"[dev] mxbox collider '{PathOf(x.transform)}' {x.GetIl2CppType().Name} trigger {x.isTrigger} layer {LayerMask.LayerToName(x.gameObject.layer)}: {B(x.bounds)}");
                if (++col >= 80) break;
            }
            Core.Log.Msg($"[dev] mxbox {B(box)} '{filter}': {r} renderers, {col} enabled colliders listed (80 each at most)");
        }

        /// <summary>
        /// The open edges (used by one triangle only) of matching mesh renderers inside a world box, chained, with a
        /// point every few metres. Vertices are welded by position, so seams of split normals do not count as open.
        /// </summary>
        private static void Edges(Vector3 a, Vector3 c, string filter)
        {
            var box = new Bounds();
            box.SetMinMax(Vector3.Min(a, c), Vector3.Max(a, c));
            int renderers = 0;
            foreach (var mr in Object.FindObjectsOfType<MeshRenderer>(false))
            {
                if (mr == null || !mr.bounds.Intersects(box)) continue;
                if (PathOf(mr.transform, 8).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var mesh = World.MeshCarver.BuildBack(mr, "mxedges");
                if (mesh == null)
                {
                    Core.Log.Warning($"[dev] mxedges '{PathOf(mr.transform)}': no triangles could be read");
                    continue;
                }
                renderers++;
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
                // Per edge: how often it is used, and the third corner of the last triangle using it.
                var uses = new Dictionary<long, int>();
                var thirds = new Dictionary<long, int>();
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    var tris = mesh.GetTriangles(s);
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                        for (int e = 0; e < 3; e++)
                        {
                            int p = weld[tris[t + e]], q = weld[tris[t + (e + 1) % 3]];
                            long k = p < q ? ((long)p << 32) | (uint)q : ((long)q << 32) | (uint)p;
                            uses[k] = uses.TryGetValue(k, out int n) ? n + 1 : 1;
                            thirds[k] = weld[tris[t + (e + 2) % 3]];
                        }
                }
                Object.Destroy(mesh);
                var next = new Dictionary<int, List<int>>();
                int open = 0;
                foreach (var kv in uses)
                {
                    if (kv.Value != 1) continue;
                    int p = (int)(kv.Key >> 32), q = (int)(kv.Key & 0xffffffff);
                    if (!box.Contains((points[p] + points[q]) * 0.5f)) continue;
                    open++;
                    if (!next.TryGetValue(p, out var lp)) next[p] = lp = new List<int>();
                    if (!next.TryGetValue(q, out var lq)) next[q] = lq = new List<int>();
                    lp.Add(q);
                    lq.Add(p);
                }
                Core.Log.Msg($"[dev] mxedges '{PathOf(mr.transform)}': {points.Count} welded vertices, {open} open edges in {B(box)}");
                var seen = new HashSet<int>();
                int chains = 0;
                // Chains start at an end (one neighbour) first, then at whatever is left (closed loops).
                foreach (var start in next.Keys.OrderBy(k => next[k].Count == 1 ? 0 : 1).ToList())
                {
                    if (seen.Contains(start)) continue;
                    var chain = new List<int> { start };
                    seen.Add(start);
                    int cur = start;
                    while (true)
                    {
                        int nxt = -1;
                        foreach (var n in next[cur])
                            if (!seen.Contains(n)) { nxt = n; break; }
                        if (nxt < 0) break;
                        seen.Add(nxt);
                        chain.Add(nxt);
                        cur = nxt;
                    }
                    float len = 0f;
                    for (int i = 1; i < chain.Count; i++) len += Vector3.Distance(points[chain[i - 1]], points[chain[i]]);
                    if (len < 2f) continue;
                    var sb = new System.Text.StringBuilder();
                    float acc = 0f;
                    for (int i = 0; i < chain.Count; i++)
                    {
                        if (i > 0) acc += Vector3.Distance(points[chain[i - 1]], points[chain[i]]);
                        if (i == 0 || i == chain.Count - 1 || acc >= 3f)
                        {
                            var p = points[chain[i]];
                            sb.Append($" ({p.x:0.0},{p.y:0.0},{p.z:0.0})");
                            acc = 0f;
                        }
                    }
                    Core.Log.Msg($"[dev] mxedges chain {++chains}: {chain.Count} points, {len:0.0} m:{sb}");
                    // Per edge: its midpoint's x, how far the triangle's third corner stands above the edge where it
                    // stands over it, and how far the triangle faces up or down (0 for a wall, 1 for flat).
                    var eb = new System.Text.StringBuilder();
                    for (int i = 1; i < chain.Count; i++)
                    {
                        int p = chain[i - 1], q = chain[i];
                        long k = p < q ? ((long)p << 32) | (uint)q : ((long)q << 32) | (uint)p;
                        if (!thirds.TryGetValue(k, out int o)) continue;
                        Vector3 e0 = points[p], e1 = points[q], e2 = points[o];
                        var d = new Vector2(e1.x - e0.x, e1.z - e0.z);
                        float u = d.sqrMagnitude < 1e-8f ? 0.5f : Mathf.Clamp01(Vector2.Dot(new Vector2(e2.x - e0.x, e2.z - e0.z), d) / d.sqrMagnitude);
                        float dy = e2.y - Mathf.Lerp(e0.y, e1.y, u);
                        float ny = Mathf.Abs(Vector3.Cross(e1 - e0, e2 - e0).normalized.y);
                        eb.Append($" {(e0.x + e1.x) * 0.5f:0}:{dy:0.0}/{ny:0.00}");
                    }
                    Core.Log.Msg($"[dev] mxedges chain {chains} edges x:rise/normal-up:{eb}");
                    if (chains >= 30) break;
                }
            }
            Core.Log.Msg($"[dev] mxedges '{filter}' {B(box)}: {renderers} renderers read");
        }

        /// <summary>Switches drawing of every renderer and terrain whose path contains the filter, to find what draws where.</summary>
        private static void Hide(string filter, bool show)
        {
            bool Match(Transform t) => PathOf(t, 8).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            int r = 0, t = 0;
            foreach (var x in Object.FindObjectsOfType<Renderer>(true))
            {
                if (x == null || !Match(x.transform)) continue;
                x.forceRenderingOff = !show;
                r++;
            }
            foreach (var x in Object.FindObjectsOfType<Terrain>(true))
            {
                if (x == null || !Match(x.transform)) continue;
                x.drawHeightmap = show;
                t++;
            }
            Core.Log.Msg($"[dev] mxhide '{filter}' {(show ? "on" : "off")}: {r} renderers, {t} terrains");
        }

        /// <summary>Lists the render settings of terrains whose name contains the filter; a pixel error of 0 or more is set.</summary>
        private static void TerrainInfo(string filter, float pixelError)
        {
            int n = 0;
            foreach (var t in Object.FindObjectsOfType<Terrain>(true))
            {
                if (t == null || t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                n++;
                if (pixelError >= 0f) t.heightmapPixelError = pixelError;
                var td = t.terrainData;
                Core.Log.Msg($"[dev] mxterrain '{t.name}': pixel error {t.heightmapPixelError:0.##}, max LOD {t.heightmapMaximumLOD}, " +
                             $"instanced {t.drawInstanced}, basemap distance {t.basemapDistance:0}, heightmap res {td?.heightmapResolution}, " +
                             $"holes res {td?.holesResolution}, holes compressed {td?.enableHolesTextureCompression}, size {td?.size}");
            }
            if (n == 0) Core.Log.Warning($"[dev] mxterrain: no terrain name contains '{filter}'");
            // A quality level can override every terrain's own pixel error.
            if (pixelError >= 0f && (QualitySettings.terrainQualityOverrides & TerrainQualityOverrides.PixelError) != 0)
                QualitySettings.terrainPixelError = pixelError;
            Core.Log.Msg($"[dev] mxterrain: quality '{QualitySettings.names[QualitySettings.GetQualityLevel()]}' overrides " +
                         $"{QualitySettings.terrainQualityOverrides}, pixel error {QualitySettings.terrainPixelError:0.##}");
        }

        private static void Line(float ax, float az, float bx, float bz, float step)
        {
            var a = new Vector2(ax, az);
            var b = new Vector2(bx, bz);
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / Mathf.Max(0.25f, step)));
            for (int i = 0; i <= n; i++)
            {
                var p = Vector2.Lerp(a, b, (float)i / n);
                var hits = Physics.RaycastAll(new Vector3(p.x, 80f, p.y), Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore);
                var sorted = hits.ToArray().OrderByDescending(h => h.point.y).Take(4)
                    .Select(h => $"{h.point.y:0.00} '{PathOf(h.collider.transform, 3)}' {h.collider.GetIl2CppType().Name}");
                Core.Log.Msg($"[dev] mxline ({p.x:0.0}, {p.y:0.0}): {string.Join(" | ", sorted)}");
            }
        }

        private static void Ray(Vector3 a, Vector3 b)
        {
            var d = b - a;
            var hits = Physics.RaycastAll(a, d.normalized, d.magnitude, ~0, QueryTriggerInteraction.Ignore).ToArray().OrderBy(h => h.distance).ToList();
            foreach (var h in hits)
            {
                Core.Log.Msg($"[dev] mxray {h.distance:0.00} m at {h.point} normal {h.normal} '{PathOf(h.collider.transform, 5)}' {h.collider.GetIl2CppType().Name} layer {LayerMask.LayerToName(h.collider.gameObject.layer)}");
                var mc = h.collider.TryCast<MeshCollider>();
                if (mc != null) RayTriangle(mc, h.triangleIndex);
            }
            Core.Log.Msg($"[dev] mxray {a} -> {b}: {hits.Count} hits");
        }

        /// <summary>The mesh a hit collider uses and whether any clearance box of a built connection meets the hit triangle.</summary>
        private static void RayTriangle(MeshCollider mc, int triangle)
        {
            var mesh = mc.sharedMesh;
            if (mesh == null) { Core.Log.Msg("[dev] mxray   no mesh on the collider"); return; }
            string about = $"mesh '{mesh.name}' readable {mesh.isReadable}, convex {mc.convex}, {mesh.vertexCount} vertices, triangle {triangle}";
            if (!mesh.isReadable || triangle < 0) { Core.Log.Msg($"[dev] mxray   {about}"); return; }
            var tris = mesh.triangles;
            var verts = mesh.vertices;
            if (triangle * 3 + 2 >= tris.Length) { Core.Log.Msg($"[dev] mxray   {about} is past the {tris.Length / 3} triangles"); return; }
            var m = mc.transform.localToWorldMatrix;
            Vector3 p0 = m.MultiplyPoint3x4(verts[tris[triangle * 3]]);
            Vector3 p1 = m.MultiplyPoint3x4(verts[tris[triangle * 3 + 1]]);
            Vector3 p2 = m.MultiplyPoint3x4(verts[tris[triangle * 3 + 2]]);
            Core.Log.Msg($"[dev] mxray   {about}: {p0} {p1} {p2}");
            var centre = (p0 + p1 + p2) / 3f;
            foreach (var id in MapExpansionsApi.Expansions)
            {
                var built = BuildPipeline.Get(id);
                if (built == null) continue;
                foreach (var c in built.Connections)
                {
                    int meets = -1, nearest = -1;
                    float best = float.MaxValue;
                    for (int i = 0; i < c.Interior.Count; i++)
                    {
                        if (meets < 0 && c.Interior[i].Intersects(p0, p1, p2)) meets = i;
                        float dist = Vector3.Distance(c.Interior[i].Center, centre);
                        if (dist < best) { best = dist; nearest = i; }
                    }
                    string near = nearest < 0 ? "no boxes" : $"nearest {nearest} {c.Interior[nearest]} {best:0.0} m away";
                    Core.Log.Msg($"[dev] mxray   {c.Name}: {(meets >= 0 ? $"clearance box {meets} meets it" : "no clearance box meets it")}, {near}");
                }
            }
        }

        private static void Lights()
        {
            foreach (var l in Object.FindObjectsOfType<Light>(false))
            {
                if (l.type != LightType.Directional) continue;
                Core.Log.Msg($"[dev] mxlight '{PathOf(l.transform)}' enabled {l.enabled} intensity {l.intensity:0.00} shadows {l.shadows} " +
                             $"cullingMask {l.cullingMask} renderingLayerMask {l.renderingLayerMask}");
            }
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var urp = rp != null ? rp.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>() : null;
            string layers = urp == null ? $"pipeline {(rp != null ? rp.GetIl2CppType().Name : "none")}" :
                $"URP shadowDistance {urp.shadowDistance:0} cascades {urp.shadowCascadeCount} lightLayers {urp.useRenderingLayers}";
            Core.Log.Msg($"[dev] mxlight: quality shadowDistance {QualitySettings.shadowDistance:0}; {layers}");
        }

        private static void Fog(bool on)
        {
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            string feature;
            if (env == null) feature = "no EnvironmentManager";
            else if (env._fogFeature == null) feature = "no fog feature";
            else
            {
                env._fogFeature.SetActive(on);
                feature = $"fog feature active {env._fogFeature.isActive}";
            }
            var fx = Singleton<EnvironmentFX>.Instance;
            string volumetric;
            if (fx == null) volumetric = "no EnvironmentFX";
            else if (fx.VolumetricFog == null) volumetric = "no volumetric fog";
            else
            {
                fx.VolumetricFog.enabled = on;
                volumetric = $"volumetric fog enabled {fx.VolumetricFog.enabled}";
            }
            Core.Log.Msg($"[dev] mxfog {(on ? "on" : "off")}: {feature}; {volumetric}");
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new[] { typeof(string) })]
    internal static class SubmitCommandStringPatch
    {
        private static bool Prefix(string args) => !DevConsole.TryHandle(args);
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class SubmitCommandListPatch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args) => !DevConsole.TryHandle(args);
    }
}
#endif
