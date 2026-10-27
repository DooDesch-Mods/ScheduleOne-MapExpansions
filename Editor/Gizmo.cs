using MapExpansions.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    internal enum GizmoMode
    {
        Move,
        Rotate,
        Scale,
    }

    /// <summary>Snapping the toolbar switches: a move grid, a turn step, and the faces of other copies.</summary>
    internal static class Snap
    {
        internal const float GridStep = 0.5f;
        internal const float TurnStep = 15f;
        internal const float ScaleStep = 0.05f;
        /// <summary>How near a face of another copy must come before the moved selection lies against it.</summary>
        internal const float EdgeReach = 0.25f;

        internal static bool Grid = true;
        internal static bool Turn = true;
        internal static bool Edges;
    }

    /// <summary>
    /// The gizmo at the selection. Moving and scaling show three axis handles: a drag along one moves the selection
    /// along that axis, or scales it evenly from its centre. Turning shows a ring around the vertical axis; a drag
    /// sideways turns the selection around its centre. The copies follow the mouse while dragging, and the change is
    /// one step of the history on release.
    /// </summary>
    internal static class Gizmo
    {
        private const float HandlePixels = 10f;
        private const int RingPoints = 64;

        private static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };
        private static readonly Color[] AxisColors = { new(0.9f, 0.2f, 0.2f), new(0.3f, 0.85f, 0.3f), new(0.25f, 0.45f, 1f) };
        private static readonly Color Hot = new(1f, 0.85f, 0.2f);
        private static readonly Color RingColor = new(0.3f, 0.85f, 0.3f);

        internal static GizmoMode Mode { get; private set; } = GizmoMode.Move;

        private static GameObject _root;
        private static readonly GameObject[] Handles = new GameObject[3];
        private static readonly Material[] Mats = new Material[3];
        private static LineRenderer _ring;
        private static Material _ringMat;

        private static int _hover = -1;
        private static int _drag = -1;
        private static GizmoMode _dragMode;
        private static Vector3 _dragPivot;
        private static Vector2 _dragMouse;
        private static float _dragT0;
        private static readonly List<(MainCopies.Placed p, Vector3 pos, Quaternion rot, Vector3 scale)> Start = new();
        private static (Quaternion turn, float scale, Vector3 move) _live = (Quaternion.identity, 1f, Vector3.zero);

        private static Shader _shader;
        private static bool _shaderLogged;

        internal static bool Dragging => _drag >= 0;

        internal static void SetMode(GizmoMode mode, string why)
        {
            if (_drag >= 0) Cancel("the mode changed");
            Mode = mode;
            Core.Log.Msg($"gizmo ({why}): mode {mode}");
            Panel.Note(mode switch { GizmoMode.Move => "Verschieben", GizmoMode.Rotate => "Drehen", _ => "Skalieren" });
        }

        /// <summary>A colour material drawn over everything, for handles and outlines.</summary>
        internal static Material Material(Color c)
        {
            if (_shader == null)
            {
                _shader = Shader.Find("Hidden/Internal-Colored");
                if (_shader == null)
                {
                    _shader = Shader.Find("Universal Render Pipeline/Unlit");
                    Core.Log.Warning("gizmo: shader Hidden/Internal-Colored is missing; handles use URP Unlit and hide behind walls");
                }
                else if (!_shaderLogged) Core.Log.Msg("gizmo: handles draw with Hidden/Internal-Colored over the world");
                _shaderLogged = true;
            }
            var m = new Material(_shader) { renderQueue = 5000 };
            m.SetColor("_Color", c);
            m.SetColor("_BaseColor", c);
            if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
            if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
            return m;
        }

        private static void Build()
        {
            _root = new GameObject("MapExpansionsEditor_Gizmo");
            Object.DontDestroyOnLoad(_root);
            for (int i = 0; i < 3; i++)
            {
                var h = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                h.name = "axis" + "XYZ"[i];
                Object.Destroy(h.GetComponent<Collider>());
                h.transform.SetParent(_root.transform, false);
                Mats[i] = Material(AxisColors[i]);
                var r = h.GetComponent<MeshRenderer>();
                r.sharedMaterial = Mats[i];
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                Handles[i] = h;
            }
            var ring = new GameObject("ring");
            ring.transform.SetParent(_root.transform, false);
            _ring = ring.AddComponent<LineRenderer>();
            _ring.useWorldSpace = true;
            _ring.loop = true;
            _ring.positionCount = RingPoints;
            _ringMat = Material(RingColor);
            _ring.material = _ringMat;
            _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ring.receiveShadows = false;
        }

        /// <summary>Returns true when the gizmo owns the mouse this frame (a drag, or a press on a handle).</summary>
        internal static bool Tick(Camera cam, bool overPanel)
        {
            var sel = Selection.All;
            if (sel.Count == 0 || cam == null)
            {
                if (_drag >= 0) Cancel("the selection is gone");
                if (_root != null) _root.SetActive(false);
                return false;
            }
            if (_root == null) Build();
            _root.SetActive(true);

            var pivot = _drag >= 0 ? _dragPivot + _live.move : Selection.CurrentBounds.center;
            float size = Mathf.Max(0.3f, Vector3.Distance(cam.transform.position, pivot) * 0.15f);
            bool ring = (_drag >= 0 ? _dragMode : Mode) == GizmoMode.Rotate;
            for (int i = 0; i < 3; i++)
            {
                Handles[i].SetActive(!ring);
                var t = Handles[i].transform;
                t.position = pivot + Axes[i] * (size * 0.5f);
                t.rotation = Quaternion.FromToRotation(Vector3.up, Axes[i]);
                // Scaling draws thicker handles, so the mode reads at a glance.
                float thick = Mode == GizmoMode.Scale ? 0.08f : 0.04f;
                t.localScale = new Vector3(size * thick, size * 0.5f, size * thick);
            }
            _ring.gameObject.SetActive(ring);
            if (ring)
            {
                for (int k = 0; k < RingPoints; k++)
                {
                    float a = k * Mathf.PI * 2f / RingPoints;
                    _ring.SetPosition(k, pivot + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * size);
                }
                _ring.startWidth = _ring.endWidth = size * 0.03f;
            }

            if (_drag >= 0)
            {
                if (EditorInput.Left(0))
                {
                    _live = Live(cam);
                    foreach (var (p, pos, rot, scale) in Start)
                    {
                        if (p.Built.Local == null) continue;
                        var t = p.Built.Local.transform;
                        t.SetPositionAndRotation(_dragPivot + _live.turn * ((pos - _dragPivot) * _live.scale) + _live.move, _live.turn * rot);
                        t.localScale = scale * _live.scale;
                    }
                    Paint();
                    return true;
                }
                Commit();
                return true;
            }

            _hover = overPanel ? -1 : ring ? HoveredRing(cam, pivot, size) : HoveredAxis(cam, pivot, size);
            Paint();
            if (_hover >= 0 && EditorInput.LeftDown(0))
            {
                _drag = _hover;
                _dragMode = Mode;
                _dragPivot = pivot;
                _dragMouse = EditorInput.MousePosition;
                _dragT0 = _dragMode == GizmoMode.Move ? AxisParam(cam.ScreenPointToRay(EditorInput.MousePosition), pivot, Axes[_drag]) : 0f;
                _live = (Quaternion.identity, 1f, Vector3.zero);
                Start.Clear();
                foreach (var p in sel)
                {
                    var t = p.Built.Local.transform;
                    Start.Add((p, t.position, t.rotation, t.localScale));
                }
                Core.Log.Msg($"gizmo: {_dragMode} drag{(_dragMode == GizmoMode.Rotate ? "" : " on " + "XYZ"[_drag])} of {sel.Count} copies from {pivot}");
                return true;
            }
            return false;
        }

        /// <summary>The change the mouse makes since the drag began, snapped as the toolbar says.</summary>
        private static (Quaternion turn, float scale, Vector3 move) Live(Camera cam)
        {
            var mouse = EditorInput.MousePosition - _dragMouse;
            switch (_dragMode)
            {
                case GizmoMode.Rotate:
                {
                    float angle = mouse.x * 0.4f;
                    if (Snap.Turn) angle = Mathf.Round(angle / Snap.TurnStep) * Snap.TurnStep;
                    return (Quaternion.Euler(0f, angle, 0f), 1f, Vector3.zero);
                }
                case GizmoMode.Scale:
                {
                    float s = Mathf.Exp((mouse.x + mouse.y) * 0.004f);
                    if (Snap.Grid) s = Mathf.Max(Snap.ScaleStep, Mathf.Round(s / Snap.ScaleStep) * Snap.ScaleStep);
                    return (Quaternion.identity, s, Vector3.zero);
                }
                default:
                {
                    var ray = cam.ScreenPointToRay(EditorInput.MousePosition);
                    float t = AxisParam(ray, _dragPivot, Axes[_drag]) - _dragT0;
                    if (Snap.Grid) t = Mathf.Round(t / Snap.GridStep) * Snap.GridStep;
                    if (Snap.Edges) t += EdgeOffset(_drag, t);
                    return (Quaternion.identity, 1f, Axes[_drag] * t);
                }
            }
        }

        /// <summary>
        /// The extra offset along an axis that lays a face of the moved selection against the nearest face of another
        /// copy, or zero when none is within reach.
        /// </summary>
        private static float EdgeOffset(int axis, float t)
        {
            var b = Selection.CurrentBounds;
            float min = b.min[axis] + t, max = b.max[axis] + t;
            float best = 0f, bestAbs = Snap.EdgeReach;
            foreach (var p in Copies.All())
            {
                if (Selection.Contains(Copies.Key(p))) continue;
                var o = Copies.Bounds(p);
                foreach (var d in new[] { o.min[axis] - max, o.max[axis] - min, o.min[axis] - min, o.max[axis] - max })
                {
                    if (Mathf.Abs(d) >= bestAbs) continue;
                    bestAbs = Mathf.Abs(d);
                    best = d;
                }
            }
            return best;
        }

        private static void Paint()
        {
            for (int i = 0; i < 3; i++)
                Mats[i].SetColor("_Color", i == _drag || (_drag < 0 && i == _hover) ? Hot : AxisColors[i]);
            var c = _hover >= 0 || _drag >= 0 ? Hot : RingColor;
            _ringMat.SetColor("_Color", c);
            _ring.startColor = _ring.endColor = c;
        }

        private static int HoveredAxis(Camera cam, Vector3 pivot, float size)
        {
            var mouse = EditorInput.MousePosition;
            var a = cam.WorldToScreenPoint(pivot);
            if (a.z <= 0f) return -1;
            int best = -1;
            float bestD = HandlePixels;
            for (int i = 0; i < 3; i++)
            {
                var b = cam.WorldToScreenPoint(pivot + Axes[i] * size);
                if (b.z <= 0f) continue;
                float d = DistanceToSegment(mouse, a, b);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>0 when the cursor is within a few pixels of the ring, else -1.</summary>
        private static int HoveredRing(Camera cam, Vector3 pivot, float size)
        {
            var mouse = EditorInput.MousePosition;
            Vector2? prev = null;
            for (int k = 0; k <= RingPoints; k++)
            {
                float ang = k * Mathf.PI * 2f / RingPoints;
                var s = cam.WorldToScreenPoint(pivot + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * size);
                if (s.z <= 0f)
                {
                    prev = null;
                    continue;
                }
                var cur = new Vector2(s.x, s.y);
                if (prev != null && DistanceToSegment(mouse, prev.Value, cur, 0f) < HandlePixels) return 0;
                prev = cur;
            }
            return -1;
        }

        internal static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, float from = 0.2f)
        {
            var ab = b - a;
            float len = ab.sqrMagnitude;
            if (len < 1e-4f) return Vector2.Distance(p, a);
            // By default the first fifth of an axis is skipped: near the pivot all three handles overlap on screen.
            float t = Mathf.Clamp(Vector2.Dot(p - a, ab) / len, from, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>Parameter along the axis line of the point nearest the ray.</summary>
        private static float AxisParam(Ray ray, Vector3 origin, Vector3 axis)
        {
            var w = origin - ray.origin;
            float b = Vector3.Dot(axis, ray.direction);
            float d = Vector3.Dot(axis, w);
            float e = Vector3.Dot(ray.direction, w);
            float denom = 1f - b * b;
            // The ray runs along the axis; no point on the axis is nearer than another.
            if (denom < 1e-5f) return 0f;
            return (b * e - d) / denom;
        }

        private static void RestoreStart()
        {
            foreach (var (p, pos, rot, scale) in Start)
            {
                if (p.Built.Local == null) continue;
                var t = p.Built.Local.transform;
                t.SetPositionAndRotation(pos, rot);
                t.localScale = scale;
            }
        }

        private static void Commit()
        {
            var sel = Start.Select(x => x.p).Where(p => p.Built.Local != null).ToList();
            var (turn, scale, move) = _live;
            var mode = _dragMode;
            // The live drag moved only the local objects; the step moves them again together with their networked parts.
            RestoreStart();
            _drag = -1;
            Start.Clear();
            _live = (Quaternion.identity, 1f, Vector3.zero);
            if (sel.Count == 0)
            {
                Core.Log.Warning("gizmo: the dragged copies are gone; nothing is written");
                return;
            }
            string who = sel.Count == 1 ? sel[0].Built.Def.Id : $"{sel.Count} Kopien";
            switch (mode)
            {
                case GizmoMode.Rotate:
                    float angle = turn.eulerAngles.y;
                    if (angle > 180f) angle -= 360f;
                    if (Mathf.Abs(angle) < 0.01f) { Core.Log.Msg("gizmo: the selection did not turn"); return; }
                    Edits.Transform(sel, _dragPivot, turn, 1f, Vector3.zero, $"{who} um {angle:0.#}° gedreht", "drag");
                    break;
                case GizmoMode.Scale:
                    if (Mathf.Abs(scale - 1f) < 1e-4f) { Core.Log.Msg("gizmo: the selection kept its size"); return; }
                    Edits.Transform(sel, _dragPivot, Quaternion.identity, scale, Vector3.zero, $"{who} auf {scale:0.00} skaliert", "drag");
                    break;
                default:
                    Edits.MoveBy(sel, move, "drag");
                    break;
            }
        }

        private static void Cancel(string why)
        {
            RestoreStart();
            Start.Clear();
            Core.Log.Warning($"gizmo: drag cancelled, {why}; the copies are back at their start");
            _drag = -1;
            _live = (Quaternion.identity, 1f, Vector3.zero);
        }

        /// <summary>Screen points on each handle, for scripted drags: three axis points and two ring points.</summary>
        internal static string Describe(Camera cam)
        {
            if (Selection.Count == 0 || cam == null) return "nothing is selected";
            var pivot = Selection.CurrentBounds.center;
            float size = Mathf.Max(0.3f, Vector3.Distance(cam.transform.position, pivot) * 0.15f);
            string P(Vector3 w)
            {
                var s = cam.WorldToScreenPoint(w);
                return s.z > 0f ? $"{s.x:0} {s.y:0}" : "behind";
            }
            return $"pivot {P(pivot)}; x {P(pivot + Axes[0] * size * 0.7f)}; y {P(pivot + Axes[1] * size * 0.7f)}; z {P(pivot + Axes[2] * size * 0.7f)}; " +
                   $"ring {P(pivot + Vector3.right * size)} / {P(pivot + Vector3.forward * size)}; mode {Mode}; screen {Screen.width}x{Screen.height}";
        }

        /// <summary>Ends a drag without writing it and hides the handles.</summary>
        internal static void Release()
        {
            if (_drag >= 0) Cancel("the editor closed");
            if (_root != null) _root.SetActive(false);
            _hover = -1;
        }
    }
}
