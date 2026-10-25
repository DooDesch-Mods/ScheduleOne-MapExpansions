using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Weather;
using UnityEngine;

namespace MapExpansions.Connections
{
    /// <summary>
    /// Keeps the weather out of a tunnel while the camera is inside it. The game hides rain and low clouds under a roof
    /// with a height mask that covers only 512 m around the map's centre; past it a tunnel counts as open sky, so it
    /// rains inside and the low cloud layer lies over the walls as a blue haze. While the camera is in a built stretch,
    /// the renderers of the rain and the low clouds are off, and they come back when it leaves. A weather volume that
    /// appears while the camera is inside is caught by the next rescan. <see cref="TunnelShadows"/> decides when the
    /// camera is inside.
    /// </summary>
    internal static class TunnelWeather
    {
        private const float RescanSeconds = 1f;

        private static readonly string[] Effects = { "RainEffect", "LowCloudsMesh" };
        private static readonly List<Renderer> Hidden = new();
        private static bool _inside;
        private static float _nextScan;
        private static bool _noManagerReported;

        public static void Apply(bool inside, string why)
        {
            if (inside)
            {
                if (!_inside || Time.unscaledTime >= _nextScan) HideAll();
            }
            else if (_inside) Restore(why);
        }

        private static void HideAll()
        {
            bool entering = !_inside;
            _inside = true;
            _nextScan = Time.unscaledTime + RescanSeconds;
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            if (env == null)
            {
                if (!_noManagerReported) Core.Log.Warning("tunnel weather: no EnvironmentManager; rain and low clouds stay in the tunnel");
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
            if (entering) Core.Log.Msg($"tunnel weather: camera inside, {hidden} rain and cloud renderers off");
            else if (hidden > 0) Core.Log.Msg($"tunnel weather: {hidden} rain and cloud renderers appeared inside; off as well");
        }

        private static bool IsEffect(Transform t)
        {
            for (; t != null; t = t.parent)
                foreach (var name in Effects)
                    if (t.name == name) return true;
            return false;
        }

        public static void Restore(string why)
        {
            if (!_inside) return;
            _inside = false;
            int restored = 0, gone = 0;
            foreach (var r in Hidden)
            {
                if (r == null) { gone++; continue; }
                r.enabled = true;
                restored++;
            }
            Hidden.Clear();
            Core.Log.Msg($"tunnel weather: {why}, {restored} rain and cloud renderers on again" + (gone > 0 ? $", {gone} destroyed meanwhile" : ""));
        }
    }
}
