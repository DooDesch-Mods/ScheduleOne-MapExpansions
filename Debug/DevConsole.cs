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
