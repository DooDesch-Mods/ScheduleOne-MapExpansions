using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Instancing;
using MapExpansions.Connections;
using MapExpansions.Lifecycle;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Draws each world only while the camera is on its side (<see cref="ExpansionDefinition.SeparateViews"/>). On the
    /// expansion's side the main world's map and terrains are not drawn, on the main side the expansion is not drawn;
    /// the connections are always drawn. The side changes in the middle of a connection's built stretch, where neither
    /// end can be seen, and the renderers switch over a few frames so the change costs no single long frame.
    /// Renderers are switched with <c>forceRenderingOff</c>, which leaves their enabled state to the game and to the
    /// object rules. Anything under a rigidbody can move between the worlds and is always drawn. The main world's
    /// trees are no renderers: the game's instancing manager draws them, and it is switched with the main side.
    /// </summary>
    internal static class SeparateViews
    {
        private const int PerFrame = 6000;
        private const float BoxMargin = 1f;

        private sealed class Group
        {
            public string Name;
            public readonly List<Renderer> Renderers = new();
            public readonly List<Terrain> Terrains = new();
        }

        private sealed class Switch
        {
            public BuiltConnection Connection;
            public float At;
            public bool EndIsExpansion;
        }

        private sealed class Zone
        {
            public BuiltExpansion Expansion;
            public readonly List<Vector2[]> Outlines = new();
            public readonly List<Switch> Switches = new();
            public readonly Group Main = new();
            public readonly Group Own = new();
        }

        private static readonly List<Zone> Zones = new();

        /// <summary>Renderers the framework made from a main-world renderer, under an expansion root.</summary>
        private static readonly HashSet<IntPtr> MadeFromMain = new();

        private static readonly List<(Group group, bool off)> Queue = new();
        private static int _queueGroup, _queueIndex, _switchFrames;
        private static bool _known;
        private static string _side;
        private static bool _noCameraReported;

        /// <summary>Whether the instancing manager drew before the main side was hidden; null while it is not hidden.</summary>
        private static bool? _instancingBefore;

        /// <summary>When false, everything is drawn on both sides; for comparing the views.</summary>
        public static bool Enabled = true;

        public static int ZoneCount => Zones.Count;

        /// <summary>The framework made <paramref name="made"/> from <paramref name="source"/> under the expansion's root.</summary>
        public static void NoteMade(BuiltExpansion owner, Renderer source, Renderer made)
        {
            if (made != null && source != null && !owner.IsOurs(source.transform)) MadeFromMain.Add(made.Pointer);
        }

        public static void Add(BuiltExpansion b, SceneIndex index)
        {
            var def = b.Definition;
            if (!def.SeparateViews) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var zone = new Zone { Expansion = b };
            zone.Main.Name = "main world";
            zone.Own.Name = $"'{b.Id}'";

            foreach (var r in def.Regions)
            {
                if (r.Polygon.Count < 3)
                {
                    Core.Log.Warning($"views: '{b.Id}' region {r.Name} has {r.Polygon.Count} points; it does not count as the expansion's side");
                    continue;
                }
                zone.Outlines.Add(r.Polygon.Select(b.Frame.ToWorldXZ).ToArray());
            }
            foreach (var conn in b.Connections)
            {
                var (from, to) = conn.BuiltRange;
                if (to <= from || conn.Definition.Route.Count < 2)
                {
                    Core.Log.Warning($"views: '{b.Id}' {conn.Name} has no built stretch; the side does not change in it");
                    continue;
                }
                // Route points may be given in either world's coordinates; the end that lies in a region is the expansion's.
                var curve = conn.Curve;
                bool startIn = InAnyOutline(zone, curve.Points[0]);
                bool endIn = InAnyOutline(zone, curve.Points[curve.Count - 1]);
                if (startIn == endIn)
                {
                    Core.Log.Warning($"views: '{b.Id}' {conn.Name} has {(startIn ? "both ends" : "neither end")} in a region; " +
                                     "the side does not change in it");
                    continue;
                }
                bool endIsExpansion = endIn;
                zone.Switches.Add(new Switch { Connection = conn, At = (from + to) * 0.5f, EndIsExpansion = endIsExpansion });
                Core.Log.Msg($"views: '{b.Id}' {conn.Name} changes the side at {(from + to) * 0.5f:0} m, at {conn.Curve.PointAt((from + to) * 0.5f)}");
            }

            var connectionRoots = b.Connections.Where(c => c.Root != null).Select(c => c.Root.transform).ToList();
            int moving = 0, shared = 0, fromMain = 0;
            foreach (var r in b.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (connectionRoots.Any(c => r.transform.IsChildOf(c))) continue;
                if (r.GetComponentInParent<Rigidbody>() != null) { moving++; continue; }
                if (MadeFromMain.Contains(r.Pointer)) { zone.Main.Renderers.Add(r); fromMain++; }
                else zone.Own.Renderers.Add(r);
            }
            foreach (var t in b.Root.GetComponentsInChildren<Terrain>(true)) zone.Own.Terrains.Add(t);

            var roots = def.MainViewRoots;
            int outside = 0;
            foreach (var r in index.Main.Renderers)
            {
                if (r == null) continue;
                var t = r.transform;
                if (!roots.Contains(t.root.name, StringComparer.Ordinal)) { outside++; continue; }
                if (IsShared(t, def.SharedViewObjects)) { shared++; continue; }
                if (r.GetComponentInParent<Rigidbody>() != null) { moving++; continue; }
                zone.Main.Renderers.Add(r);
            }
            foreach (var t in index.Main.Terrains)
                if (t != null && roots.Contains(t.transform.root.name, StringComparer.Ordinal)) zone.Main.Terrains.Add(t);

            Zones.Add(zone);
            Core.Log.Msg($"views: '{b.Id}' separates the views: main world {zone.Main.Renderers.Count} renderers ({fromMain} made from main objects) and " +
                         $"{zone.Main.Terrains.Count} terrains under {string.Join(", ", roots)}; expansion {zone.Own.Renderers.Count} renderers and " +
                         $"{zone.Own.Terrains.Count} terrains; always drawn: {shared} shared, {moving} under a rigidbody, {outside} under other main roots; " +
                         $"{sw.ElapsedMilliseconds} ms");
            _known = false;
        }

        private static bool IsShared(Transform t, List<string> names)
        {
            for (var p = t; p != null; p = p.parent)
                foreach (var n in names)
                    if (p.name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void Clear()
        {
            if (Zones.Count > 0)
            {
                int restored = 0;
                foreach (var z in Zones)
                {
                    restored += Apply(z.Main, false);
                    restored += Apply(z.Own, false);
                }
                SetInstancing(true);
                Core.Log.Msg($"views: teardown, {restored} renderers drawn again");
            }
            Zones.Clear();
            MadeFromMain.Clear();
            Queue.Clear();
            _known = false;
            _side = null;
        }

        public static void Tick()
        {
            if (Zones.Count == 0) return;
            var cam = Camera.main;
            if (cam == null)
            {
                if (!_noCameraReported) Core.Log.Msg("views: no main camera yet; both views stay as they are");
                _noCameraReported = true;
                return;
            }
            _noCameraReported = false;
            string side = Enabled ? SideOf(cam.transform.position, out _) : "both";
            if (!_known || side != _side)
            {
                Core.Log.Msg($"views: camera at {cam.transform.position} is {(side == null ? "in the main world" : side == "both" ? "on both sides (views not separated)" : $"in '{side}'")}" +
                             (_known ? $", was {(_side == null ? "main world" : $"'{_side}'")}" : ""));
                _known = true;
                _side = side;
                Plan(side);
            }
            Work();
        }

        /// <summary>The expansion whose side a point is on, or null for the main world.</summary>
        public static string SideOf(Vector3 p, out string why)
        {
            foreach (var z in Zones)
                foreach (var s in z.Switches)
                {
                    bool inside = false;
                    foreach (var box in s.Connection.Interior)
                        if (box.Contains(p, BoxMargin)) { inside = true; break; }
                    if (!inside) continue;
                    float d = s.Connection.Curve.Project(p, out _);
                    bool pastSwitch = d >= s.At;
                    why = $"{s.Connection.Name} at {d:0} m, side changes at {s.At:0} m";
                    return pastSwitch == s.EndIsExpansion ? z.Expansion.Id : null;
                }
            var xz = new Vector2(p.x, p.z);
            foreach (var z in Zones)
                foreach (var outline in z.Outlines)
                    if (Inside(outline, xz))
                    {
                        why = "inside a region";
                        return z.Expansion.Id;
                    }
            why = "outside every expansion";
            return null;
        }

        private static bool InAnyOutline(Zone z, Vector3 p)
        {
            var xz = new Vector2(p.x, p.z);
            foreach (var outline in z.Outlines)
                if (Inside(outline, xz)) return true;
            return false;
        }

        private static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        private static void Plan(string side)
        {
            Queue.Clear();
            _queueGroup = _queueIndex = _switchFrames = 0;
            SetInstancing(side == null || side == "both");
            foreach (var z in Zones)
            {
                bool all = side == "both";
                bool here = side == z.Expansion.Id;
                SetTerrains(z.Main, !all && here);
                SetTerrains(z.Own, !all && !here);
                Queue.Add((z.Main, !all && here));
                Queue.Add((z.Own, !all && !here));
            }
        }

        /// <summary>Switches up to <see cref="PerFrame"/> renderers of the planned change.</summary>
        private static void Work()
        {
            if (_queueGroup >= Queue.Count) return;
            int budget = PerFrame;
            _switchFrames++;
            while (budget > 0 && _queueGroup < Queue.Count)
            {
                var (group, off) = Queue[_queueGroup];
                var list = group.Renderers;
                int end = Math.Min(list.Count, _queueIndex + budget);
                for (int i = _queueIndex; i < end; i++)
                {
                    var r = list[i];
                    if (r != null) r.forceRenderingOff = off;
                }
                budget -= end - _queueIndex;
                _queueIndex = end;
                if (_queueIndex < list.Count) break;
                _queueGroup++;
                _queueIndex = 0;
            }
            if (_queueGroup >= Queue.Count)
                Core.Log.Msg($"views: switch done over {_switchFrames} frames: " +
                             string.Join(", ", Queue.Select(q => $"{q.group.Name} {(q.off ? "hidden" : "drawn")}")));
        }

        /// <summary>Applies one state to a whole group at once; returns the renderers set.</summary>
        private static int Apply(Group g, bool off)
        {
            SetTerrains(g, off);
            int n = 0;
            foreach (var r in g.Renderers)
            {
                if (r == null) continue;
                r.forceRenderingOff = off;
                n++;
            }
            return n;
        }

        /// <summary>Hides the instanced main-world trees, or gives the instancing manager back the state it had.</summary>
        private static void SetInstancing(bool draw)
        {
            if (draw && _instancingBefore == null) return;
            var im = Singleton<InstancingManager>.Instance;
            if (im == null)
            {
                Core.Log.Warning("views: no instancing manager; the main world's instanced trees stay as they are");
                return;
            }
            if (!draw)
            {
                if (_instancingBefore == null) _instancingBefore = im._drawInstancedObjects;
                im._drawInstancedObjects = false;
                Core.Log.Msg($"views: instanced trees off (they were {(_instancingBefore.Value ? "on" : "off")})");
            }
            else if (_instancingBefore != null)
            {
                im._drawInstancedObjects = _instancingBefore.Value;
                Core.Log.Msg($"views: instanced trees back {(_instancingBefore.Value ? "on" : "off")}");
                _instancingBefore = null;
            }
        }

        private static void SetTerrains(Group g, bool off)
        {
            foreach (var t in g.Terrains)
            {
                if (t == null) continue;
                t.drawHeightmap = !off;
                t.drawTreesAndFoliage = !off;
            }
        }

        public static string Describe(Vector3 p)
        {
            if (Zones.Count == 0) return "views: no expansion separates the views";
            string side = SideOf(p, out string why);
            bool busy = _queueGroup < Queue.Count;
            return $"views: {Zones.Count} expansions separate the views; at {p} {(side == null ? "main world" : $"'{side}'")} ({why}); " +
                   $"separation {(Enabled ? "on" : "off")}; applied side {(_side == null ? "main world" : $"'{_side}'")}{(busy ? ", switch in progress" : "")}";
        }
    }
}
