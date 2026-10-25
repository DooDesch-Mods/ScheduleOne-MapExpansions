using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MapExpansions.Connections
{
    /// <summary>
    /// Keeps the sun out of a tunnel while the camera is inside it. The shell shadows the inside only within the
    /// pipeline's shadow distance; past it the tunnel road lies in full sun, which reads as a bright dome ahead. While
    /// the camera is in a built stretch, the shadow distance reaches past what can be seen of the tunnel, and goes back
    /// when the camera leaves.
    /// </summary>
    internal static class TunnelShadows
    {
        private const float InsideDistance = 200f;
        private const float Margin = 1f;

        private static readonly List<OrientedBox> Boxes = new();
        private static UniversalRenderPipelineAsset _asset;
        private static float _original;
        private static bool _raised;
        private static bool _noPipelineReported;

        public static void Add(BuiltConnection conn)
        {
            Boxes.AddRange(conn.Interior);
            Core.Log.Msg($"tunnel shadows: {conn.Name} adds {conn.Interior.Count} boxes ({Boxes.Count} in all)");
        }

        public static void Clear()
        {
            Lower("teardown");
            TunnelFog.Restore("teardown");
            TunnelWeather.Restore("teardown");
            Boxes.Clear();
        }

        public static void Tick()
        {
            if (Boxes.Count == 0) return;
            var cam = Camera.main;
            if (cam == null)
            {
                if (_raised) Lower("no main camera");
                TunnelFog.Apply(false, "no main camera");
                TunnelWeather.Apply(false, "no main camera");
                return;
            }
            var p = cam.transform.position;
            bool inside = false;
            for (int i = 0; i < Boxes.Count && !inside; i++) inside = Boxes[i].Contains(p, Margin);
            if (inside) Raise();
            else if (_raised) Lower("camera left the tunnel");
            TunnelFog.Apply(inside, "camera left the tunnel");
            TunnelWeather.Apply(inside, "camera left the tunnel");
        }

        private static void Raise()
        {
            if (_asset == null)
            {
                var rp = GraphicsSettings.currentRenderPipeline;
                _asset = rp != null ? rp.TryCast<UniversalRenderPipelineAsset>() : null;
                if (_asset == null)
                {
                    if (!_noPipelineReported) Core.Log.Warning("tunnel shadows: the render pipeline is not URP; the tunnel keeps the default shadow distance");
                    _noPipelineReported = true;
                    return;
                }
            }
            if (_raised)
            {
                if (Mathf.Approximately(_asset.shadowDistance, Target)) return;
                // The game applied its graphics settings while the camera was inside; their value is the one to restore.
                _original = _asset.shadowDistance;
                Core.Log.Msg($"tunnel shadows: shadow distance was set to {_original:0} from outside; raised again");
            }
            else
            {
                _original = _asset.shadowDistance;
                _raised = true;
                Core.Log.Msg($"tunnel shadows: camera inside, shadow distance {_original:0} -> {Target:0}");
            }
            _asset.shadowDistance = Target;
        }

        private static float Target => Mathf.Max(_original, InsideDistance);

        private static void Lower(string why)
        {
            if (!_raised) return;
            _raised = false;
            if (_asset == null)
            {
                Core.Log.Warning($"tunnel shadows: {why}, but the pipeline asset is gone; nothing to restore");
                return;
            }
            _asset.shadowDistance = _original;
            Core.Log.Msg($"tunnel shadows: {why}, shadow distance back to {_original:0}");
        }
    }
}
