using UnityEngine;

namespace MapExpansions.Connections
{
    /// <summary>A box with its own rotation, tested against points, boxes and triangles by separating axes.</summary>
    internal readonly struct OrientedBox
    {
        public readonly Vector3 Center;
        public readonly Quaternion Rotation;
        public readonly Vector3 Half;
        private readonly Vector3 _ax, _ay, _az;

        public OrientedBox(Vector3 center, Quaternion rotation, Vector3 half)
        {
            Center = center;
            Rotation = rotation;
            Half = half;
            _ax = rotation * Vector3.right;
            _ay = rotation * Vector3.up;
            _az = rotation * Vector3.forward;
        }

        /// <summary>Box from a to b along its length, with the bottom face on the segment.</summary>
        public static OrientedBox Along(Vector3 a, Vector3 b, float width, float bottom, float top)
        {
            var d = b - a;
            var flat = new Vector3(d.x, 0f, d.z);
            var rot = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat) : Quaternion.identity;
            float length = flat.magnitude;
            float mid = (bottom + top) * 0.5f;
            // The box stays upright; its height covers the climb of the segment.
            float half = (top - bottom) * 0.5f + Mathf.Abs(d.y) * 0.5f;
            return new OrientedBox((a + b) * 0.5f + Vector3.up * mid, rot, new Vector3(width * 0.5f, half, length * 0.5f));
        }

        public Bounds WorldBounds
        {
            get
            {
                var e = new Vector3(
                    Mathf.Abs(_ax.x) * Half.x + Mathf.Abs(_ay.x) * Half.y + Mathf.Abs(_az.x) * Half.z,
                    Mathf.Abs(_ax.y) * Half.x + Mathf.Abs(_ay.y) * Half.y + Mathf.Abs(_az.y) * Half.z,
                    Mathf.Abs(_ax.z) * Half.x + Mathf.Abs(_ay.z) * Half.y + Mathf.Abs(_az.z) * Half.z);
                return new Bounds(Center, e * 2f);
            }
        }

        public bool Contains(Vector3 p, float margin = 0f)
        {
            var d = p - Center;
            return Mathf.Abs(Vector3.Dot(d, _ax)) <= Half.x + margin
                && Mathf.Abs(Vector3.Dot(d, _ay)) <= Half.y + margin
                && Mathf.Abs(Vector3.Dot(d, _az)) <= Half.z + margin;
        }

        public bool Intersects(Bounds b)
        {
            if (!WorldBounds.Intersects(b)) return false;
            Vector3 t = b.center - Center;
            Vector3 e = b.extents;
            Vector3[] axes = { _ax, _ay, _az };
            float[] h = { Half.x, Half.y, Half.z };
            for (int i = 0; i < 3; i++)
            {
                var a = axes[i];
                float rb = Mathf.Abs(a.x) * e.x + Mathf.Abs(a.y) * e.y + Mathf.Abs(a.z) * e.z;
                if (Mathf.Abs(Vector3.Dot(t, a)) > h[i] + rb) return false;
            }
            return true;
        }

        /// <summary>Triangle against box, all 13 separating axes.</summary>
        public bool Intersects(Vector3 a, Vector3 b, Vector3 c)
        {
            // Into box space, where the box is axis aligned.
            Vector3 v0 = Local(a), v1 = Local(b), v2 = Local(c);
            float ex = Half.x, ey = Half.y, ez = Half.z;
            if (Mathf.Max(v0.x, Mathf.Max(v1.x, v2.x)) < -ex || Mathf.Min(v0.x, Mathf.Min(v1.x, v2.x)) > ex) return false;
            if (Mathf.Max(v0.y, Mathf.Max(v1.y, v2.y)) < -ey || Mathf.Min(v0.y, Mathf.Min(v1.y, v2.y)) > ey) return false;
            if (Mathf.Max(v0.z, Mathf.Max(v1.z, v2.z)) < -ez || Mathf.Min(v0.z, Mathf.Min(v1.z, v2.z)) > ez) return false;

            Vector3 f0 = v1 - v0, f1 = v2 - v1, f2 = v0 - v2;
            var n = Vector3.Cross(f0, f1);
            float r = ex * Mathf.Abs(n.x) + ey * Mathf.Abs(n.y) + ez * Mathf.Abs(n.z);
            if (Mathf.Abs(Vector3.Dot(n, v0)) > r) return false;

            Vector3[] edges = { f0, f1, f2 };
            Vector3[] units = { Vector3.right, Vector3.up, Vector3.forward };
            foreach (var u in units)
                foreach (var f in edges)
                {
                    var axis = Vector3.Cross(u, f);
                    if (axis.sqrMagnitude < 1e-10f) continue;
                    float p0 = Vector3.Dot(v0, axis), p1 = Vector3.Dot(v1, axis), p2 = Vector3.Dot(v2, axis);
                    float rr = ex * Mathf.Abs(axis.x) + ey * Mathf.Abs(axis.y) + ez * Mathf.Abs(axis.z);
                    if (Mathf.Max(p0, Mathf.Max(p1, p2)) < -rr || Mathf.Min(p0, Mathf.Min(p1, p2)) > rr) return false;
                }
            return true;
        }

        private Vector3 Local(Vector3 p)
        {
            var d = p - Center;
            return new Vector3(Vector3.Dot(d, _ax), Vector3.Dot(d, _ay), Vector3.Dot(d, _az));
        }

        public override string ToString() => $"box at {Center} half {Half} yaw {Rotation.eulerAngles.y:0}";
    }
}
