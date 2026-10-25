using MapExpansions.Lifecycle;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;

namespace MapExpansions.Connections
{
    /// <summary>
    /// Pushes vehicles the AI drives up the steep parts of a connection. Vanilla engines are tuned for the flat town;
    /// a slow car on a 20 % ramp gets less force from its engine than gravity pulls it back with, so an AI car that
    /// stops or slows down on the ramp rolls back and gives up. On a connection, an AI car under throttle gets the
    /// share of gravity that pulls it downhill back, scaled by its throttle. Cars a player drives keep vanilla physics.
    /// </summary>
    internal static class SlopeAssist
    {
        /// <summary>Horizontal distance from the route within which a car counts as on it.</summary>
        private const float Reach = 8f;

        private const float HeightReach = 3f;

        /// <summary>Gravity along the car's forward axis below which the assist stays off, about a 3 % grade.</summary>
        private const float MinPull = 0.3f;

        private static readonly List<Vector3> Points = new();
        private static Bounds _area;
        private static readonly HashSet<IntPtr> Assisted = new();
        private static bool _noManagerReported;

        public static void Add(BuiltConnection conn)
        {
            var pts = conn.Curve.Points;
            if (pts.Count == 0)
            {
                Core.Log.Warning($"slope assist: {conn.Name} has no route points; nothing added");
                return;
            }
            if (Points.Count == 0) _area = new Bounds(pts[0], Vector3.zero);
            foreach (var p in pts)
            {
                Points.Add(p);
                _area.Encapsulate(p);
            }
            _area.Expand(new Vector3(Reach * 2f, HeightReach * 2f, Reach * 2f));
            Core.Log.Msg($"slope assist: {conn.Name} adds {pts.Count} route points ({Points.Count} in all)");
        }

        public static void Clear()
        {
            Points.Clear();
            Assisted.Clear();
        }

        public static void FixedTick()
        {
            if (Points.Count == 0) return;
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Core.Log.Error($"slope assist failed and is off until the next build: {e}");
                Clear();
            }
        }

        private static void Tick()
        {
            var vm = NetworkSingleton<VehicleManager>.Instance;
            if (vm == null || vm.AllVehicles == null)
            {
                if (!_noManagerReported) Core.Log.Warning("slope assist: no VehicleManager; AI cars get no help on the connections");
                _noManagerReported = true;
                return;
            }
            foreach (var v in vm.AllVehicles)
            {
                if (v == null) continue;
                var ptr = v.Pointer;
                string off = Assist(v);
                if (off == null && Assisted.Add(ptr))
                    Core.Log.Msg($"slope assist: AI car '{v.name}' on a connection slope at {v.transform.position}");
                else if (off != null && Assisted.Remove(ptr))
                    Core.Log.Msg($"slope assist: AI car '{v.name}' no longer helped at {v.transform.position}: {off}");
            }
        }

        /// <summary>Pushes the car when it qualifies; null when it does, otherwise why not.</summary>
        private static string Assist(LandVehicle v)
        {
            var rb = v.Rb;
            if (rb == null) return "no rigidbody";
            if (rb.isKinematic) return "moved without physics";
            if (v.LocalPlayerIsDriver) return "a player drives";
            var agent = v.Agent;
            if (agent == null || !agent.AutoDriving) return "not driven by the AI";
            var pos = rb.position;
            if (!_area.Contains(pos) || !OnRoute(pos)) return "off the connection";
            var forward = v.transform.forward;
            float pull = Vector3.Dot(Physics.gravity, forward);
            if (pull > -MinPull) return "not uphill";
            float throttle = v.currentThrottle;
            if (throttle > 0f) rb.AddForce(forward * (-pull * Mathf.Min(throttle, 1f)), ForceMode.Acceleration);
            return null;
        }

        private static bool OnRoute(Vector3 pos)
        {
            const float reach2 = Reach * Reach;
            for (int i = 0; i < Points.Count; i++)
            {
                var p = Points[i];
                float dx = p.x - pos.x, dz = p.z - pos.z;
                if (dx * dx + dz * dz < reach2 && Mathf.Abs(p.y - pos.y) < HeightReach) return true;
            }
            return false;
        }
    }
}
