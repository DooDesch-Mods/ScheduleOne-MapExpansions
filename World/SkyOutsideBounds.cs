using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Weather;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Keeps the sun, the moon and the sky following the clock past the game's weather area. The game evaluates the
    /// sky only while the player stands inside its weather bounds; outside it clears the weather and stops, so the
    /// light stays as it was when the player left: a sun at midnight, or a moon at noon after arriving in the evening.
    /// An expansion lies outside those bounds. While the player is there, the sky is evaluated each frame with the
    /// weather volume nearest to the player, as at the edge of the weather area.
    /// </summary>
    internal static class SkyOutsideBounds
    {
        /// <summary>At the border of the weather area the game's answer flips between frames; state changes are logged at most this often.</summary>
        private const float LogSeconds = 30f;

        private static bool _outside;
        private static bool _noManagerReported;
        private static int _failures;
        private static int _flips;
        private static float _nextLog;

        private static void LogChange(string line)
        {
            _flips++;
            if (Time.unscaledTime < _nextLog) return;
            Core.Log.Msg(_flips > 1 ? $"{line} ({_flips} changes since the last line)" : line);
            _flips = 0;
            _nextLog = Time.unscaledTime + LogSeconds;
        }

        public static void Tick()
        {
            if (WeatherCover.AreaCount == 0) return;
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            if (env == null)
            {
                if (!_noManagerReported) Core.Log.Warning("sky: no EnvironmentManager; the sky outside the weather area is not evaluated");
                _noManagerReported = true;
                return;
            }
            try
            {
                Evaluate(env);
            }
            catch (Exception e)
            {
                _failures++;
                if (_failures == 1 || _failures % 1000 == 0)
                    Core.Log.Error($"sky: evaluating the sky outside the weather area failed ({_failures}x); the light keeps its last state: {e}");
            }
        }

        private static void Evaluate(EnvironmentManager env)
        {
            var volumes = env._activeWeatherVolumes;
            var player = env.Player;
            var day = env._dayNightController;
            if (volumes == null || volumes.Count == 0 || player == null || day == null || day.TimeOfDayOverridden) return;
            // The game's _withinBounds also answers its weather lookups for NPCs and items, so it is computed here
            // from the player alone, as the game's Update does.
            if (InWeatherArea(env, player.position))
            {
                if (_outside) LogChange("sky: player back in the game's weather area; the game evaluates the sky again");
                _outside = false;
                return;
            }

            var volume = Nearest(env, volumes, player.position);
            if (volume == null || volume.WeatherProfile == null) return;
            var time = NetworkSingleton<TimeManager>.Instance;
            if (time == null) return;

            day.OnUpdateTime(time.NormalizedTimeOfDay);
            day.EvaluateSky(volume.WeatherProfile.SkySettings, null, 0f, null, 0f);
            if (!_outside)
                LogChange($"sky: player at {player.position} outside the game's weather area; the sky follows the clock with weather volume '{volume.name}'");
            _outside = true;
        }

        private static bool InWeatherArea(EnvironmentManager env, Vector3 p)
        {
            var local = env.GetWeatherAnchor().InverseTransformPoint(p);
            var half = env._weatherBounds * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.z;
        }

        /// <summary>The weather volume over the point of the weather area closest to <paramref name="p"/>.</summary>
        private static WeatherVolume Nearest(EnvironmentManager env, Il2CppFishNet.Object.Synchronizing.SyncList<WeatherVolume> volumes, Vector3 p)
        {
            var anchor = env.GetWeatherAnchor();
            var half = env._weatherBounds * 0.5f;
            var local = anchor.InverseTransformPoint(p);
            local.x = Mathf.Clamp(local.x, -half.x, half.x);
            local.z = Mathf.Clamp(local.z, -half.z, half.z);
            var edge = anchor.TransformPoint(local);

            WeatherVolume best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < volumes.Count; i++)
            {
                var v = volumes[i];
                if (v == null) continue;
                var inside = v.transform.InverseTransformPoint(edge);
                var size = v.VolumeSize * 0.5f;
                if (Mathf.Abs(inside.x) <= size.x && Mathf.Abs(inside.z) <= size.z) return v;
                float d = (v.transform.position - edge).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = v; }
            }
            return best;
        }
    }
}
