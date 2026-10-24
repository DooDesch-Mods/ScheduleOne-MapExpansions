using UnityEngine;

namespace MapExpansions.Lifecycle
{
    /// <summary>Turns expansion-local coordinates into world coordinates and back.</summary>
    internal readonly struct Frame
    {
        public readonly Vector3 Offset;
        public readonly int QuarterTurns;
        public readonly Quaternion Rotation;

        public Frame(Vector3 offset, int quarterTurns)
        {
            Offset = offset;
            QuarterTurns = quarterTurns & 3;
            Rotation = Quaternion.Euler(0f, 90f * QuarterTurns, 0f);
        }

        public static Frame Of(ExpansionDefinition def) => new(def.Offset, def.QuarterTurns);

        public Vector3 ToWorld(Vector3 local) => Offset + Rotation * local;

        public Vector3 ToLocal(Vector3 world) => Quaternion.Inverse(Rotation) * (world - Offset);

        public Vector3 Resolve(PathPoint p) => p.Side == WorldSide.Main ? p.Position : ToWorld(p.Position);

        /// <summary>World x/z of a local x/z point.</summary>
        public Vector2 ToWorldXZ(Vector2 local)
        {
            var w = ToWorld(new Vector3(local.x, 0f, local.y));
            return new Vector2(w.x, w.z);
        }

        /// <summary>World box around a local box.</summary>
        public Bounds ToWorld(Bounds local)
        {
            var b = new Bounds(ToWorld(local.center), Vector3.zero);
            var e = local.extents;
            for (int i = 0; i < 8; i++)
                b.Encapsulate(ToWorld(local.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            return b;
        }

        /// <summary>World x/z rectangle around a local x/z rectangle.</summary>
        public Rect ToWorld(Rect local)
        {
            var a = ToWorldXZ(local.min);
            var b = ToWorldXZ(local.max);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        public override string ToString() => $"offset {Offset}, {QuarterTurns * 90} degrees";
    }
}
