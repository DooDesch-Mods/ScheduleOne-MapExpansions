using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Weather;
using UnityEngine;
using UnityEngine.VFX;

namespace MapExpansions.World
{
    /// <summary>
    /// Lets the weather reach the expansions. The game's weather volumes cover 800 m by 800 m around the map's centre, and
    /// past them it clears the weather for the player and answers "no weather" for NPCs, so an expansion stays dry while
    /// the map is in a storm. Inside an expansion's area a position counts as the nearest point of the weather's bounds,
    /// so it gets the weather of the map beside it. The rain's particles die outside the same bounds, so their effects get
    /// wider ones. They also die below the game's height map, which ends at the map's edge and answers with its last row
    /// past it, so while the camera is in an expansion the map's heights lie below everything; WeatherCover takes the roofs.
    /// </summary>
    internal static class WeatherReach
    {
        /// <summary>A little inside the bounds, so the volume lookup never falls between two volumes' edges.</summary>
        private const float Inset = 0.98f;

        /// <summary>How wide the effects may draw around the weather's centre; expansions lie within 1200 m of it.</summary>
        private const float EffectReach = 2400f;

        private const string MapHeights = "MinMaxHeight";
        private static readonly Vector2 BelowEverything = new Vector2(-2000f, -1000f);
        private const float RescanSeconds = 1f;

        private static bool _reported, _failedReported, _widenedReported;
        private static bool _mapIgnored, _noManagerReported;
        private static Vector2? _gameHeights;
        private static float _nextScan;

        public static void Reset()
        {
            _reported = _mapIgnored = false;
            _gameHeights = null;
        }

        /// <summary>Every frame: the rain ignores the game's height map while the camera is in an expansion.</summary>
        public static void Tick(Vector3 camera)
        {
            bool inArea = WeatherCover.InArea(camera);
            if (!inArea && !_mapIgnored) return;
            if (inArea == _mapIgnored && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + RescanSeconds;
            try { Apply(inArea); }
            catch (Exception e) { Failed("the rain's height map", e); }
        }

        private static void Apply(bool ignore)
        {
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            if (env == null)
            {
                if (!_noManagerReported) Core.Log.Warning("weather reach: no EnvironmentManager; the rain keeps the game's height map");
                _noManagerReported = true;
                return;
            }
            int changed = 0;
            foreach (VisualEffect effect in env.GetComponentsInChildren<VisualEffect>(true))
            {
                if (effect == null || !effect.HasVector2(MapHeights)) continue;
                Vector2 now = effect.GetVector2(MapHeights);
                if (ignore && now != BelowEverything)
                {
                    _gameHeights ??= now;
                    effect.SetVector2(MapHeights, BelowEverything);
                    changed++;
                }
                else if (!ignore && now == BelowEverything && _gameHeights != null)
                {
                    effect.SetVector2(MapHeights, _gameHeights.Value);
                    changed++;
                }
            }
            if (ignore != _mapIgnored)
                Core.Log.Msg($"weather reach: the camera {(ignore ? "entered" : "left")} an expansion, {changed} rain effect(s) {(ignore ? "ignore" : "read")} the game's height map; its heights are {_gameHeights}");
            _mapIgnored = ignore;
        }

        /// <summary>The point the weather reads for <paramref name="position"/>; false when the game's own answer stands.</summary>
        private static bool Project(EnvironmentManager env, Vector3 position, out Vector3 projected)
        {
            projected = position;
            if (!WeatherCover.InArea(position)) return false;
            Transform anchor = env._weatherBoundsAnchor != null ? env._weatherBoundsAnchor : env.transform;
            Vector3 local = anchor.InverseTransformPoint(position);
            Vector3 half = env._weatherBounds * 0.5f;
            if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.z) return false;
            local.x = Mathf.Clamp(local.x, -half.x * Inset, half.x * Inset);
            local.z = Mathf.Clamp(local.z, -half.z * Inset, half.z * Inset);
            projected = anchor.TransformPoint(local);
            return true;
        }

        private static void Failed(string what, Exception e)
        {
            if (_failedReported) return;
            _failedReported = true;
            Core.Log.Error($"weather reach: {what} failed; the game's bounds answer, the expansions stay dry: {e}");
        }

        [HarmonyPatch(typeof(WeatherEffectController), nameof(WeatherEffectController.Initialise))]
        private static class EffectPatch
        {
            private static void Postfix(WeatherEffectController __instance, WeatherVolume mainVolume)
            {
                try
                {
                    Vector3 overlap = mainVolume.VolumeSize;
                    overlap.x += mainVolume.BlendAmount;
                    overlap.y = 1f;
                    overlap.z = EffectReach;
                    Vector3 bounds = mainVolume.WeatherBounds;
                    bounds.x = bounds.z = EffectReach;
                    var effects = __instance.visualEffects;
                    for (int i = 0; i < effects.Count; i++)
                    {
                        effects[i].SetVectorParameterForAll("WeatherOverlapSize", overlap);
                        effects[i].SetVectorParameterForAll("WeatherBounds", bounds);
                    }
                    if (_widenedReported || effects.Count == 0) return;
                    _widenedReported = true;
                    Core.Log.Msg($"weather reach: '{__instance.name}' draws {effects.Count} effect(s) within {bounds} instead of {mainVolume.WeatherBounds}");
                }
                catch (Exception e) { Failed("widening an effect", e); }
            }
        }

        [HarmonyPatch(typeof(EnvironmentManager), "DetermineWeatherVolumeWithTarget")]
        private static class TargetPatch
        {
            private static bool Prefix(EnvironmentManager __instance)
            {
                try
                {
                    Transform player = __instance.Player;
                    if (player == null || !Project(__instance, player.position, out Vector3 at)) return true;
                    __instance._withinBounds = true;
                    var volumes = __instance._activeWeatherVolumes;
                    for (int i = 0; i < volumes.Count; i++)
                    {
                        WeatherVolume volume = volumes[i];
                        Vector3 local = volume.transform.InverseTransformPoint(at);
                        Vector3 half = volume.VolumeSize * 0.5f;
                        if (Mathf.Abs(local.x) > half.x || Mathf.Abs(local.z) > half.z) continue;
                        __instance._targetWeatherVolumeIndex = i;
                        __instance._targetWeatherVolume = volume;
                        __instance._currentWeatherConditions = volume.WeatherProfile.Conditions;
                        if (!_reported)
                        {
                            _reported = true;
                            Core.Log.Msg($"weather reach: the camera at {player.position} is past the weather's bounds, it gets the weather at {at}");
                        }
                        break;
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Failed("the camera's lookup", e);
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(EnvironmentManager), "GetWeatherProfileFromPosition")]
        private static class ProfilePatch
        {
            private static void Prefix(EnvironmentManager __instance, ref Vector3 position)
            {
                try
                {
                    if (Project(__instance, position, out Vector3 at)) position = at;
                }
                catch (Exception e) { Failed("a position's lookup", e); }
            }
        }
    }
}
