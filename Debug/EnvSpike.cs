#if DEBUG
using Il2Cpp;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.FX;
using Il2CppScheduleOne.Weather;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Environment commands, all under <c>mxenv</c>:
    ///
    /// mxenv fog off|on     the screen fog render feature and the volumetric fog
    /// mxenv info           camera range, skybox, shadow distance, and the large world-edge objects of Main
    /// </summary>
    internal static class EnvSpike
    {
        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "fog" when parts.Length > 2: Fog(parts[2] == "on"); break;
                case "info": Info(); break;
                default:
                    Core.Log.Warning("[dev] mxenv fog off|on, mxenv info");
                    break;
            }
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
            RenderSettings.fog = on && RenderSettings.fog;
            Core.Log.Msg($"[dev] mxenv fog {(on ? "on" : "off")}: {feature}; {volumetric}; RenderSettings.fog {RenderSettings.fog}");
        }

        private static void Info()
        {
            var cam = Camera.main;
            Core.Log.Msg($"[dev] mxenv info: camera {(cam != null ? $"'{cam.name}' near {cam.nearClipPlane} far {cam.farClipPlane}" : "none")}, " +
                         $"skybox {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "none")}, shadow distance {QualitySettings.shadowDistance}, " +
                         $"lod bias {QualitySettings.lodBias}");
            var placed = DevConsole.PlacedRoot;
            var big = new List<(string name, Bounds b)>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (placed != null && r.transform.IsChildOf(placed)) continue;
                var b = r.bounds;
                if (b.size.x > 400f || b.size.z > 400f) big.Add((r.name, b));
            }
            foreach (var h in big.OrderByDescending(h => h.b.size.x * h.b.size.z).Take(20))
                Core.Log.Msg($"[dev] mxenv info: Main '{h.name}' x {h.b.min.x:0}..{h.b.max.x:0} z {h.b.min.z:0}..{h.b.max.z:0} y {h.b.min.y:0}..{h.b.max.y:0}");
            Core.Log.Msg($"[dev] mxenv info: {big.Count} Main renderers wider than 400 m");
        }
    }
}
#endif
