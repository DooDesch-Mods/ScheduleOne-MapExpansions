using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Weather;
using MapExpansions.Connections;
using MapExpansions.Lifecycle;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Gives the expansions a roof against the weather. The game decides cover from a height mask baked over 512 m
    /// around the map's centre; past it everything counts as open sky, and inside it the mask knows nothing of an
    /// expansion's buildings. Over the area of an expansion and of its built connections, cover comes from the world
    /// itself: a ray from high above that hits a roof, a ceiling or a hill before it reaches the position. NPCs, cars and
    /// wheels get that answer through <c>EnvironmentManager.IsPositionUnderCover</c>. The rain and the low clouds draw
    /// around the camera and only read the baked mask, so while the camera stands under cover in an expansion, or in a
    /// tunnel, their renderers are off; they come back when it leaves.
    /// </summary>
    internal static class WeatherCover
    {
        /// <summary>How far above a position the ray starts; above the highest roof and hill of an expansion.</summary>
        private const float RayHeight = 120f;

        /// <summary>A hit this close above a position is the ground it stands on, not a roof.</summary>
        private const float Clearance = 0.3f;

        private const float RescanSeconds = 1f;

        /// <summary>Static world geometry; vehicles, characters, items and triggers are not a roof.</summary>
        private static readonly int RoofMask = LayerMask.GetMask("Default", "Terrain");

        private static readonly string[] Effects = { "RainEffect", "LowCloudsMesh" };

        private static readonly List<(string owner, Rect area)> Areas = new();
        private static readonly List<Renderer> Hidden = new();
        private static bool _cameraCovered;
        private static float _nextScan;
        private static bool _noManagerReported;
        private static int _calls, _queries, _covered;

        public static int AreaCount => Areas.Count;

        public static void Add(BuiltExpansion b)
        {
            int before = Areas.Count;
            foreach (var r in b.Definition.Regions)
            {
                if (r.Polygon.Count < 3)
                {
                    Core.Log.Warning($"weather cover: '{b.Id}' region {r.Name} has {r.Polygon.Count} points; it gets no cover area");
                    continue;
                }
                var w = b.Frame.ToWorldXZ(r.Polygon[0]);
                var rect = new Rect(w, Vector2.zero);
                foreach (var p in r.Polygon) rect = Grow(rect, b.Frame.ToWorldXZ(p));
                Areas.Add(($"'{b.Id}' region {r.Name}", rect));
            }
            foreach (var g in b.Definition.GroundPlates)
                Areas.Add(($"'{b.Id}' ground plate {g.Name}", b.Frame.ToWorld(g.LocalArea)));
            foreach (var conn in b.Connections)
            {
                if (conn.Interior.Count == 0) continue;
                var first = conn.Interior[0].WorldBounds;
                var rect = Rect.MinMaxRect(first.min.x, first.min.z, first.max.x, first.max.z);
                foreach (var box in conn.Interior)
                {
                    var wb = box.WorldBounds;
                    rect = Grow(Grow(rect, new Vector2(wb.min.x, wb.min.z)), new Vector2(wb.max.x, wb.max.z));
                }
                Areas.Add(($"'{b.Id}' connection {conn.Name}", rect));
            }
            for (int i = before; i < Areas.Count; i++)
                Core.Log.Msg($"weather cover: {Areas[i].owner} covers x {Areas[i].area.xMin:0}..{Areas[i].area.xMax:0}, z {Areas[i].area.yMin:0}..{Areas[i].area.yMax:0}");
        }

        private static Rect Grow(Rect r, Vector2 p) =>
            Rect.MinMaxRect(Mathf.Min(r.xMin, p.x), Mathf.Min(r.yMin, p.y), Mathf.Max(r.xMax, p.x), Mathf.Max(r.yMax, p.y));

        public static void Clear()
        {
            ShowEffects("teardown");
            if (Areas.Count > 0) Core.Log.Msg($"weather cover: teardown, {Areas.Count} areas gone; {_covered} of {_queries} cover queries were under a roof");
            Areas.Clear();
            _calls = _queries = _covered = 0;
        }

        public static bool InArea(Vector3 p)
        {
            var xz = new Vector2(p.x, p.z);
            foreach (var (_, area) in Areas)
                if (area.Contains(xz)) return true;
            return false;
        }

        /// <summary>Whether a roof, ceiling or hill lies over <paramref name="p"/>.</summary>
        public static bool IsCovered(Vector3 p) =>
            Physics.Raycast(p + Vector3.up * RayHeight, Vector3.down, RayHeight - Clearance, RoofMask, QueryTriggerInteraction.Ignore);

        /// <summary>Cover for a weather entity; null leaves the answer to the game's mask.</summary>
        internal static bool? Query(Vector3 p)
        {
            if (Areas.Count == 0 || !InArea(p)) return null;
            bool covered = IsCovered(p);
            _queries++;
            if (covered) _covered++;
            if (_queries == 1) Core.Log.Msg($"weather cover: first cover query in an expansion at {p}: {(covered ? "under a roof" : "open sky")}");
            return covered;
        }

        public static void Tick()
        {
            if (Areas.Count == 0) return;
            var cam = Camera.main;
            if (cam == null)
            {
                ShowEffects("no main camera");
                return;
            }
            var p = cam.transform.position;
            bool covered = TunnelShadows.CameraInside || (InArea(p) && IsCovered(p));
            if (covered)
            {
                if (!_cameraCovered || Time.unscaledTime >= _nextScan) HideEffects();
            }
            else if (_cameraCovered) ShowEffects("camera under open sky");
        }

        private static void HideEffects()
        {
            bool entering = !_cameraCovered;
            _cameraCovered = true;
            _nextScan = Time.unscaledTime + RescanSeconds;
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            if (env == null)
            {
                if (!_noManagerReported) Core.Log.Warning("weather cover: no EnvironmentManager; rain and low clouds stay under the roof");
                _noManagerReported = true;
                return;
            }
            int hidden = 0;
            foreach (var r in env.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled || !IsEffect(r.transform)) continue;
                r.enabled = false;
                Hidden.Add(r);
                hidden++;
            }
            if (entering) Core.Log.Msg($"weather cover: camera under a roof, {hidden} rain and cloud renderers off");
            else if (hidden > 0) Core.Log.Msg($"weather cover: {hidden} rain and cloud renderers appeared under the roof; off as well");
        }

        private static bool IsEffect(Transform t)
        {
            for (; t != null; t = t.parent)
                foreach (var name in Effects)
                    if (t.name == name) return true;
            return false;
        }

        private static void ShowEffects(string why)
        {
            if (!_cameraCovered) return;
            _cameraCovered = false;
            int restored = 0, gone = 0;
            foreach (var r in Hidden)
            {
                if (r == null) { gone++; continue; }
                r.enabled = true;
                restored++;
            }
            Hidden.Clear();
            Core.Log.Msg($"weather cover: {why}, {restored} rain and cloud renderers on again" + (gone > 0 ? $", {gone} destroyed meanwhile" : ""));
        }

        public static string Describe(Vector3 p) =>
            $"weather cover: {Areas.Count} areas; at {p} {(InArea(p) ? "in an expansion" : "outside the expansions")}, " +
            $"{(IsCovered(p) ? "under a roof" : "open sky")}; camera effects {(_cameraCovered ? "off" : "on")}; " +
            $"{_covered} of {_queries} entity queries under a roof, {_calls} cover queries in all";

        [HarmonyPatch(typeof(EnvironmentManager), nameof(EnvironmentManager.IsPositionUnderCover))]
        private static class UnderCoverPatch
        {
            private static bool Prefix(Vector3 position, ref bool __result)
            {
                _calls++;
                bool? covered;
                try
                {
                    covered = Query(position);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"weather cover: cover query at {position} failed; the game's mask answers: {e}");
                    return true;
                }
                if (covered == null) return true;
                __result = covered.Value;
                return false;
            }
        }
    }
}
