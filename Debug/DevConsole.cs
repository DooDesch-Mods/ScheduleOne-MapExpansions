#if DEBUG
using System.Collections;
using System.Globalization;
using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using MapExpansions.Source;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Dev console commands for the stage 1 spikes. Compiled out of Release.
    ///
    /// mxplace [none|root] [dx dy dz]  copy the captured world to an offset; 'root' points every statically batched
    ///                                 renderer at the offset root, 'none' only moves the transforms
    /// mxunplace                       remove the placed copy
    /// mxlight [off]                   lightmap indices of the placed copy; 'off' clears them
    /// mxcam ex ey ez tx ty tz | off   park the camera at a world position looking at a world point
    /// mxframe [seconds]               frame time statistics over a sample window
    /// mxwhere                         player position
    /// mxtp x y z                      teleport the player
    /// mxnav ...                       navigation spike, see NavSpike
    /// mxveh ...                       vehicle AI spike, see VehicleSpike
    /// mxreg ...                       region 6 spike, see RegionSpike
    /// mxterrain ...                   terrain overlap, see TerrainSpike
    /// </summary>
    internal static class DevConsole
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly Vector3 DefaultOffset = new(0f, 0f, -700f);

        private static GameObject _placed;

        /// <summary>Offset of the placed copy, or the default offset while nothing is placed.</summary>
        internal static Vector3 PlacedOffset => _placed != null ? _placed.transform.position : DefaultOffset;
        private static int _lastFrame = -1;
        private static string _lastSig = "";

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
                    case "mxplace": Place(parts); break;
                    case "mxunplace": Unplace(); break;
                    case "mxlight": Light(parts.Length > 1 && parts[1] == "off"); break;
                    case "mxcam": Cam(parts); break;
                    case "mxframe": MelonCoroutines.Start(Frame(parts.Length > 1 ? F(parts[1]) : 3f)); break;
                    case "mxwhere": Where(); break;
                    case "mxtp": Tp(parts); break;
                    case "mxnav": NavSpike.Handle(parts); break;
                    case "mxveh": VehicleSpike.Handle(parts); break;
                    case "mxreg": RegionSpike.Handle(parts); break;
                    case "mxterrain": TerrainSpike.Handle(parts); break;
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

        private static float F(string s) => float.Parse(s, Ci);

        private static void Place(string[] parts)
        {
            string mode = parts.Length > 1 ? parts[1].ToLowerInvariant() : "root";
            Vector3 offset = parts.Length > 4 ? new Vector3(F(parts[2]), F(parts[3]), F(parts[4])) : DefaultOffset;
            if (mode != "root" && mode != "none")
            {
                Core.Log.Warning("[dev] mxplace [none|root] [dx dy dz]");
                return;
            }
            if (SourceCapture.Cache == null || SourceCapture.Cache.transform.childCount == 0)
            {
                Core.Log.Error("[dev] mxplace: no captured world in the cache");
                return;
            }
            if (_placed != null) Unplace();

            var t = System.Diagnostics.Stopwatch.StartNew();
            _placed = new GameObject("MapExpansions.Placed");
            _placed.transform.position = offset;
            var source = SourceCapture.Cache.transform.GetChild(0).gameObject;
            var copy = Object.Instantiate(source, _placed.transform);
            copy.name = source.name;
            long cloneMs = t.ElapsedMilliseconds;

            int batched = 0, rerooted = 0, failed = 0;
            string firstError = null;
            foreach (var mr in copy.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!mr.isPartOfStaticBatch) continue;
                batched++;
                if (mode != "root") continue;
                try
                {
                    mr.staticBatchRootTransform = _placed.transform;
                    if (mr.staticBatchRootTransform == _placed.transform) rerooted++;
                    else failed++;
                }
                catch (Exception e)
                {
                    failed++;
                    firstError ??= e.Message;
                }
            }
            string line = $"[dev] mxplace {mode} at {offset}: copy in {cloneMs} ms, {batched} batched renderers, " +
                          $"{rerooted} pointed at the offset root, {failed} failed";
            if (failed > 0) Core.Log.Error(line + (firstError != null ? $"; first error: {firstError}" : ""));
            else Core.Log.Msg(line);
        }

        private static void Unplace()
        {
            if (_placed == null)
            {
                Core.Log.Msg("[dev] mxunplace: nothing placed");
                return;
            }
            Object.Destroy(_placed);
            _placed = null;
            Core.Log.Msg("[dev] mxunplace: placed copy removed");
        }

        private static void Light(bool clear)
        {
            if (_placed == null)
            {
                Core.Log.Warning("[dev] mxlight: nothing placed");
                return;
            }
            var hist = new Dictionary<int, int>();
            foreach (var mr in _placed.GetComponentsInChildren<MeshRenderer>(true))
            {
                hist.TryGetValue(mr.lightmapIndex, out int n);
                hist[mr.lightmapIndex] = n + 1;
                if (clear) mr.lightmapIndex = -1;
            }
            string list = string.Join(", ", hist.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"));
            Core.Log.Msg($"[dev] mxlight: lightmapIndex histogram {list}{(clear ? "; all set to -1" : "")}");
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
            var eye = new Vector3(F(parts[1]), F(parts[2]), F(parts[3]));
            var at = new Vector3(F(parts[4]), F(parts[5]), F(parts[6]));
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
            float avg = samples.Average();
            float P(float q) => samples[Math.Min(samples.Count - 1, (int)(q * samples.Count))];
            int placed = _placed != null ? _placed.GetComponentsInChildren<MeshRenderer>(false).Length : 0;
            Core.Log.Msg($"[dev] mxframe: {samples.Count} frames in {seconds:0.#} s, avg {avg:0.00} ms, p50 {P(0.5f):0.00}, " +
                         $"p95 {P(0.95f):0.00}, max {samples[samples.Count - 1]:0.00}; placed renderers active {placed}");
        }

        private static void Where()
        {
            var p = Player.Local;
            if (p == null)
            {
                Core.Log.Warning("[dev] mxwhere: no local player");
                return;
            }
            Core.Log.Msg($"[dev] mxwhere: {p.transform.position}");
        }

        private static void Tp(string[] parts)
        {
            if (parts.Length < 4)
            {
                Core.Log.Warning("[dev] mxtp x y z");
                return;
            }
            var mv = PlayerSingleton<PlayerMovement>.Instance;
            if (mv == null)
            {
                Core.Log.Warning("[dev] mxtp: no PlayerMovement");
                return;
            }
            var pos = new Vector3(F(parts[1]), F(parts[2]), F(parts[3]));
            mv.Teleport(pos, false);
            Core.Log.Msg($"[dev] mxtp: teleported to {pos}");
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
