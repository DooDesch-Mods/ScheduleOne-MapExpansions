using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Weather;

namespace MapExpansions.Connections
{
    /// <summary>
    /// Keeps the screen fog out of a tunnel while the camera is inside it. The game's fog pass fades by distance and
    /// height, not by what is enclosed, so a few metres of tunnel already lie under a grey haze. The pass is off while the
    /// camera is in a built stretch and on again when it leaves. <see cref="TunnelShadows"/> decides when the camera is
    /// inside.
    /// </summary>
    internal static class TunnelFog
    {
        /// <summary>Whether the pass goes off in a tunnel; the dev console switches it to compare.</summary>
        public static bool Enabled = true;

        private static bool _off;
        private static bool _noFeatureReported;

        public static void Apply(bool inside, string why)
        {
            if (inside && Enabled) SwitchOff();
            else if (_off) Restore(why);
        }

        private static void SwitchOff()
        {
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            var feature = env != null ? env._fogFeature : null;
            if (feature == null)
            {
                if (!_noFeatureReported)
                    Core.Log.Warning($"tunnel fog: {(env == null ? "no EnvironmentManager" : "no fog feature")}; the tunnel keeps the fog");
                _noFeatureReported = true;
                return;
            }
            if (!feature.isActive) return;
            feature.SetActive(false);
            Core.Log.Msg(_off ? "tunnel fog: fog pass was switched on from outside; off again" : "tunnel fog: camera inside, fog pass off");
            _off = true;
        }

        public static void Restore(string why)
        {
            if (!_off) return;
            _off = false;
            var env = NetworkSingleton<EnvironmentManager>.Instance;
            var feature = env != null ? env._fogFeature : null;
            if (feature == null)
            {
                Core.Log.Warning($"tunnel fog: {why}, but the fog feature is gone; nothing to restore");
                return;
            }
            feature.SetActive(true);
            Core.Log.Msg($"tunnel fog: {why}, fog pass on again");
        }
    }
}
