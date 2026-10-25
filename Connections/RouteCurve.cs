using MapExpansions.Lifecycle;
using UnityEngine;

namespace MapExpansions.Connections
{
    /// <summary>
    /// The centre line of a connection in world coordinates, sampled at short even steps. Corners between route
    /// points are rounded with a Catmull-Rom curve in x/z; the height follows the points without overshooting them,
    /// so a flat stretch stays flat up to its last point.
    /// </summary>
    internal sealed class RouteCurve
    {
        public const float Step = 0.5f;

        public readonly List<Vector3> Points = new();
        public readonly List<float> Distance = new();
        public readonly List<RouteStyle> Style = new();

        /// <summary>Road width and median width at each sample.</summary>
        public readonly List<float> RoadWidth = new(), MedianWidth = new();

        /// <summary>Index of the route point each sample starts from.</summary>
        public readonly List<int> Leg = new();

        public readonly Vector3[] Controls;
        public readonly RouteStyle[] Styles;
        private readonly float[] _roadWidths, _medianWidths;

        /// <summary>The widest road anywhere on the route.</summary>
        public float MaxRoadWidth { get; private set; }

        public int Count => Points.Count;
        public float Length => Distance.Count > 0 ? Distance[Distance.Count - 1] : 0f;

        public RouteCurve(ConnectionDefinition def, Frame frame)
        {
            int n = def.Route.Count;
            Controls = new Vector3[n];
            Styles = new RouteStyle[n];
            _roadWidths = new float[n];
            _medianWidths = new float[n];
            for (int i = 0; i < n; i++)
            {
                var rp = def.Route[i];
                Controls[i] = frame.Resolve(rp.Point);
                Styles[i] = rp.Style;
                _roadWidths[i] = rp.RoadWidth > 0f ? rp.RoadWidth : def.RoadWidth;
                _medianWidths[i] = Mathf.Max(0f, rp.MedianWidth);
                MaxRoadWidth = Mathf.Max(MaxRoadWidth, _roadWidths[i]);
            }
            Build();
        }

        private void Build()
        {
            int n = Controls.Length;
            float total = 0f;
            Vector3 last = Controls[0];
            Add(Controls[0], 0f, 0, 0f);
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 p1 = Controls[i], p2 = Controls[i + 1];
                // Where a built stretch meets Main's own road the curve ends on both sides: a portal faces along its first leg.
                Vector3 p0 = i == 0 ? p1 : Joined(i - 1, i) ? Controls[i - 1] : 2f * p1 - p2;
                Vector3 p3 = i + 2 >= n ? p2 : Joined(i, i + 1) ? Controls[i + 2] : 2f * p2 - p1;
                float chord = Vector3.Distance(p1, p2);
                int steps = Mathf.Max(1, Mathf.CeilToInt(chord / (Step * 0.5f)));
                float lo = Mathf.Min(p1.y, p2.y), hi = Mathf.Max(p1.y, p2.y);
                for (int s = 1; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    var p = CatmullRom(p0, p1, p2, p3, t);
                    p.y = Mathf.Clamp(p.y, lo, hi);
                    float d = Vector3.Distance(p, last);
                    // Keep samples about one step apart; the last sample of each leg lands on its route point.
                    if (d < Step && s < steps) continue;
                    total += d;
                    last = p;
                    Add(p, total, i, t);
                }
            }
        }

        private static bool Built(RouteStyle style) => style != RouteStyle.Existing;

        /// <summary>Whether the legs starting at route points a and b are both built or both Main's own road.</summary>
        private bool Joined(int a, int b) => Built(Styles[a]) == Built(Styles[b]);

        private void Add(Vector3 p, float d, int leg, float t)
        {
            Points.Add(p);
            Distance.Add(d);
            Leg.Add(leg);
            Style.Add(Styles[Mathf.Min(leg, Styles.Length - 1)]);
            int next = Mathf.Min(leg + 1, Controls.Length - 1);
            float ease = Mathf.SmoothStep(0f, 1f, t);
            RoadWidth.Add(Mathf.Lerp(_roadWidths[leg], _roadWidths[next], ease));
            MedianWidth.Add(Mathf.Lerp(_medianWidths[leg], _medianWidths[next], ease));
        }

        /// <summary>Centripetal Catmull-Rom; it does not loop or cusp on uneven point spacing.</summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            static float Knot(float k, Vector3 a, Vector3 b) => k + Mathf.Max(0.01f, Mathf.Sqrt(Vector3.Distance(a, b)));
            float t0 = 0f, t1 = Knot(t0, p0, p1), t2 = Knot(t1, p1, p2), t3 = Knot(t2, p2, p3);
            float u = Mathf.Lerp(t1, t2, t);
            Vector3 a1 = (t1 - u) / (t1 - t0) * p0 + (u - t0) / (t1 - t0) * p1;
            Vector3 a2 = (t2 - u) / (t2 - t1) * p1 + (u - t1) / (t2 - t1) * p2;
            Vector3 a3 = (t3 - u) / (t3 - t2) * p2 + (u - t2) / (t3 - t2) * p3;
            Vector3 b1 = (t2 - u) / (t2 - t0) * a1 + (u - t0) / (t2 - t0) * a2;
            Vector3 b2 = (t3 - u) / (t3 - t1) * a2 + (u - t1) / (t3 - t1) * a3;
            return (t2 - u) / (t2 - t1) * b1 + (u - t1) / (t2 - t1) * b2;
        }

        /// <summary>Horizontal direction of travel at a sample.</summary>
        public Vector3 Forward(int i)
        {
            int a = Mathf.Max(0, i - 1), b = Mathf.Min(Count - 1, i + 1);
            // At a joint with Main's own road the built side gives the direction, so a portal stands across its tunnel.
            if (i > 0 && i < Count - 1 && Built(Style[i]) != Built(Style[i + 1]))
            {
                if (Built(Style[i + 1])) a = i;
                else b = i;
            }
            var d = Points[b] - Points[a];
            d.y = 0f;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
        }

        public Vector3 Right(int i) => Vector3.Cross(Vector3.up, Forward(i));

        /// <summary>Sample index closest to a distance along the route.</summary>
        public int IndexAt(float distance)
        {
            if (distance <= 0f) return 0;
            if (distance >= Length) return Count - 1;
            int lo = 0, hi = Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (Distance[mid] < distance) lo = mid; else hi = mid;
            }
            return distance - Distance[lo] < Distance[hi] - distance ? lo : hi;
        }

        public Vector3 PointAt(float distance)
        {
            if (distance <= 0f) return Points[0];
            if (distance >= Length) return Points[Count - 1];
            int i = IndexAt(distance);
            int j = Distance[i] < distance ? Mathf.Min(Count - 1, i + 1) : Mathf.Max(0, i - 1);
            float span = Distance[j] - Distance[i];
            return Mathf.Abs(span) < 1e-5f ? Points[i] : Vector3.Lerp(Points[i], Points[j], (distance - Distance[i]) / span);
        }

        /// <summary>Distance along the route of the route point with this index.</summary>
        public float DistanceOfControl(int control)
        {
            if (control <= 0) return 0f;
            float d = Length;
            for (int i = Count - 1; i >= 0; i--)
                if (Leg[i] == control - 1) return Distance[i];
            return d;
        }

        /// <summary>Stretches of equal style, as distance ranges along the route.</summary>
        public List<(RouteStyle style, float from, float to)> Stretches()
        {
            var list = new List<(RouteStyle, float, float)>();
            RouteStyle current = Style[0];
            float from = 0f;
            for (int i = 1; i < Count; i++)
            {
                if (Style[i] == current) continue;
                // Sample i - 1 is the last one of the previous leg and lies on the route point between the two.
                list.Add((current, from, Distance[i - 1]));
                current = Style[i];
                from = Distance[i - 1];
            }
            list.Add((current, from, Length));
            return list;
        }

        /// <summary>Distance along the route of the sample closest to a world point.</summary>
        public float Project(Vector3 world, out float sideways)
        {
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < Count; i++)
            {
                float sq = (Points[i] - world).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            sideways = Vector3.Dot(world - Points[best], Right(best));
            return Distance[best];
        }
    }
}
