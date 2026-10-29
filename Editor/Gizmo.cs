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

    /// <summary>
    /// The gizmo at the selection, drawn as in Unity and Unreal. Moving shows three arrows with cone tips and three
    /// small squares between them: a drag on an arrow moves the selection along that axis, a drag on a square moves it
    /// in that plane. Scaling shows the axes with cube tips; a drag scales evenly from the centre. Turning shows a ring
    /// around the vertical axis; a drag sideways turns the selection around its centre. The copies follow the mouse
    /// while dragging, and the change is one step of the history on release.
    /// </summary>
    internal static class Gizmo
    {
        private const float HandlePixels = 10f;
        private const int RingPoints = 64;
        private const int ConeSides = 16;

        /// <summary>Where along an axis the shaft ends and the cone begins, as a share of the gizmo size.</summary>
        private const float ShaftEnd = 0.8f;
        private const float ConeRadius = 0.065f;
        /// <summary>The plane squares span this share of the size, starting this far from the pivot.</summary>
        private const float PlaneFrom = 0.2f;
        private const float PlaneSize = 0.22f;

        /// <summary>Handle numbers: 0 to 2 the axes X, Y, Z, 3 to 5 the plane whose normal is X, Y or Z.</summary>
        private const int FirstPlane = 3;

        private static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };
        private static readonly Color[] AxisColors = { new(0.9f, 0.2f, 0.2f), new(0.3f, 0.85f, 0.3f), new(0.25f, 0.45f, 1f) };
        private static readonly Color Hot = new(1f, 0.85f, 0.2f);
        private static readonly Color RingColor = new(0.3f, 0.85f, 0.3f);
        private const float PlaneAlpha = 0.35f;
        private const float PlaneHotAlpha = 0.65f;

        internal static GizmoMode Mode { get; private set; } = GizmoMode.Move;

        private static GameObject _root;
        private static readonly GameObject[] Shafts = new GameObject[3];
        private static readonly GameObject[] Cones = new GameObject[3];
        private static readonly GameObject[] Cubes = new GameObject[3];
        private static readonly GameObject[] Planes = new GameObject[3];
        private static readonly Material[] Mats = new Material[3];
        private static readonly Material[] PlaneMats = new Material[3];
        private static LineRenderer _ring;
        private static Material _ringMat;
        private static Mesh _cone;

        private static int _hover = -1;
        private static int _drag = -1;
        private static GizmoMode _dragMode;
        private static Vector3 _dragPivot;
        private static Vector2 _dragMouse;
        private static float _dragT0;
        private static Vector3 _dragHit0;
        private static readonly List<(MainCopies.Placed p, Vector3 pos, Quaternion rot, Vector3 scale)> Start = new();
        private static (Quaternion turn, float scale, Vector3 move) _live = (Quaternion.identity, 1f, Vector3.zero);
        /// <summary>The point a running drag moves, or null when it moves the selected copies.</summary>
        private static PointRef _point;
        private static float _pointYaw;

        private static Shader _shader;
        private static bool _shaderLogged;

        internal static bool Dragging => _drag >= 0;

        /// <summary>The change of the running drag for the cursor readout, or null without a drag.</summary>
        internal static string Readout
        {
            get
            {
                if (_drag < 0) return null;
                switch (_dragMode)
                {
                    case GizmoMode.Rotate: return Panel.Num(Mathf.DeltaAngle(0f, _live.turn.eulerAngles.y), 0, true) + "°";
                    case GizmoMode.Scale: return "× " + Panel.Num(_live.scale, 2);
                }
                if (_drag < FirstPlane) return "XYZ"[_drag] + "  " + Panel.Num(Vector3.Dot(_live.move, Axes[_drag]), 2, true) + " m";
                var (u, v) = PlaneAxes(_drag - FirstPlane);
                return $"{"XYZ"[u]} {Panel.Num(_live.move[u], 2, true)}  {"XYZ"[v]} {Panel.Num(_live.move[v], 2, true)} m";
            }
        }

        internal static void SetMode(GizmoMode mode, string why)
        {
            if (_drag >= 0) Cancel("the mode changed");
            Mode = mode;
            Core.Log.Msg($"gizmo ({why}): mode {mode}");
            Panel.Note(mode switch { GizmoMode.Move => "Verschieben", GizmoMode.Rotate => "Drehen", _ => "Skalieren" });
        }

        /// <summary>A colour material drawn over everything, for handles and outlines; alpha below one blends.</summary>
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
            if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            return m;
        }

        /// <summary>A cone of height one on a base of radius one, its apex up, for arrow tips.</summary>
        internal static Mesh Cone()
        {
            if (_cone != null) return _cone;
            var v = new Vector3[ConeSides + 2];
            v[0] = Vector3.up;
            v[1] = Vector3.zero;
            for (int k = 0; k < ConeSides; k++)
            {
                float a = k * Mathf.PI * 2f / ConeSides;
                v[2 + k] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            }
            var tris = new int[ConeSides * 6];
            for (int k = 0; k < ConeSides; k++)
            {
                int next = (k + 1) % ConeSides;
                tris[k * 6] = 0;
                tris[k * 6 + 1] = 2 + next;
                tris[k * 6 + 2] = 2 + k;
                tris[k * 6 + 3] = 1;
                tris[k * 6 + 4] = 2 + k;
                tris[k * 6 + 5] = 2 + next;
            }
            _cone = new Mesh { name = "MapExpansionsEditor_Cone" };
            _cone.vertices = v;
            _cone.triangles = tris;
            _cone.RecalculateBounds();
            Object.DontDestroyOnLoad(_cone);
            return _cone;
        }

        /// <summary>A handle part without collider or shadow, under a parent.</summary>
        internal static GameObject Part(Transform parent, string name, Material mat, PrimitiveType? shape)
        {
            GameObject go;
            if (shape != null)
            {
                go = GameObject.CreatePrimitive(shape.Value);
                Object.Destroy(go.GetComponent<Collider>());
            }
            else
            {
                go = new GameObject();
                go.AddComponent<MeshFilter>().sharedMesh = Cone();
                go.AddComponent<MeshRenderer>();
            }
            go.name = name;
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        /// <summary>Places a cone with its base at <paramref name="from"/>, pointing along the axis.</summary>
        internal static void PlaceCone(GameObject cone, Vector3 from, Vector3 axis, float length, float radius)
        {
            var t = cone.transform;
            t.position = from;
            t.rotation = Quaternion.FromToRotation(Vector3.up, axis);
            t.localScale = new Vector3(radius, length, radius);
        }

        private static void Build()
        {
            _root = new GameObject("MapExpansionsEditor_Gizmo");
            Object.DontDestroyOnLoad(_root);
            for (int i = 0; i < 3; i++)
            {
                Mats[i] = Material(AxisColors[i]);
                Shafts[i] = Part(_root.transform, "axis" + "XYZ"[i], Mats[i], PrimitiveType.Cylinder);
                Cones[i] = Part(_root.transform, "cone" + "XYZ"[i], Mats[i], null);
                Cubes[i] = Part(_root.transform, "cube" + "XYZ"[i], Mats[i], PrimitiveType.Cube);
                var pc = AxisColors[i];
                pc.a = PlaneAlpha;
                PlaneMats[i] = Material(pc);
                Planes[i] = Part(_root.transform, "plane" + "XYZ"[i], PlaneMats[i], PrimitiveType.Quad);
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

        /// <summary>
        /// What the gizmo works on: the selected copies around the centre of their box, or else the selected point at
        /// its foot. False when neither is there.
        /// </summary>
        private static bool Target(out Vector3 pivot, out PointRef point, out float pointYaw)
        {
            point = null;
            pointYaw = 0f;
            pivot = default;
            if (Selection.All.Count > 0)
            {
                pivot = Selection.CurrentBounds.center;
                return true;
            }
            var r = Points.Current;
            if (r == null || !Points.WorldOf(r, out pivot, out pointYaw)) return false;
            point = r;
            return true;
        }

        /// <summary>A point has no size, so it moves under the scale mode; it turns only when it has a facing.</summary>
        private static GizmoMode ModeFor(PointRef point) =>
            point == null ? Mode : Mode == GizmoMode.Rotate && Points.HasFacing(point) ? GizmoMode.Rotate : GizmoMode.Move;

        /// <summary>A region corner keeps to the ground; it has no up arrow and moves only in the ground plane.</summary>
        private static bool Flat(PointRef point) => point != null && !Points.HasHeight(point);

        private static float Size(Camera cam, Vector3 pivot) => Mathf.Max(0.3f, FlyCamera.ViewDistance(cam, pivot) * 0.15f);

        /// <summary>The two axes that span the plane with normal <paramref name="n"/>.</summary>
        private static (int u, int v) PlaneAxes(int n) => n == 0 ? (1, 2) : n == 1 ? (0, 2) : (0, 1);

        /// <summary>Whether a handle shows in a mode: no up arrow and only the ground plane for a flat point.</summary>
        private static bool Shown(int handle, GizmoMode mode, bool flat)
        {
            if (mode == GizmoMode.Rotate) return false;
            if (handle < FirstPlane) return !(flat && handle == 1);
            return mode == GizmoMode.Move && (!flat || handle == FirstPlane + 1);
        }

        /// <summary>
        /// The four corners of a plane square. It sits in the quarter of its plane that faces the camera, as in Unity,
        /// so it never hides behind the pivot.
        /// </summary>
        private static Vector3[] PlaneCorners(Camera cam, Vector3 pivot, float size, int n)
        {
            var (u, v) = PlaneAxes(n);
            var toCam = cam.transform.position - pivot;
            var du = Axes[u] * (toCam[u] < 0f ? -1f : 1f);
            var dv = Axes[v] * (toCam[v] < 0f ? -1f : 1f);
            float a = PlaneFrom * size, b = (PlaneFrom + PlaneSize) * size;
            return new[] { pivot + du * a + dv * a, pivot + du * b + dv * a, pivot + du * b + dv * b, pivot + du * a + dv * b };
        }

        /// <summary>True when the cursor is over a handle or a drag runs, so a point marker under it does not take the click.</summary>
        internal static bool OverHandle(Camera cam, bool overPanel)
        {
            if (_drag >= 0) return true;
            if (cam == null || overPanel || !Target(out var pivot, out var point, out _)) return false;
            float size = Size(cam, pivot);
            var mode = ModeFor(point);
            return mode == GizmoMode.Rotate ? HoveredRing(cam, pivot, size) >= 0 : HoveredHandle(cam, pivot, size, mode, Flat(point)) >= 0;
        }

        /// <summary>Returns true when the gizmo owns the mouse this frame (a drag, or a press on a handle).</summary>
        internal static bool Tick(Camera cam, bool overPanel)
        {
            var sel = Selection.All;
            bool has = Target(out var at, out var point, out float pointYaw) && cam != null;
            if (_drag >= 0 && (!has || point?.Key != _point?.Key)) Cancel("the selection is gone");
            if (!has)
            {
                if (_root != null) _root.SetActive(false);
                return false;
            }
            if (_root == null) Build();
            _root.SetActive(true);

            var pivot = _drag >= 0 ? _dragPivot + _live.move : at;
            float size = Size(cam, pivot);
            var mode = _drag >= 0 ? _dragMode : ModeFor(point);
            bool ring = mode == GizmoMode.Rotate;
            bool flat = Flat(point);
            Draw(cam, pivot, size, mode, flat);

            if (_drag >= 0)
            {
                if (EditorInput.Left(0))
                {
                    _live = Live(cam);
                    if (_point != null) Points.LiveGizmo(_dragPivot + _live.move, _live.turn.eulerAngles.y);
                    foreach (var (p, pos, rot, scale) in Start)
                    {
                        if (p.Built.Handle == null) continue;
                        var t = p.Built.Handle;
                        t.SetPositionAndRotation(_dragPivot + _live.turn * ((pos - _dragPivot) * _live.scale) + _live.move, _live.turn * rot);
                        t.localScale = scale * _live.scale;
                    }
                    Paint();
                    return true;
                }
                Commit();
                return true;
            }

            _hover = overPanel ? -1 : ring ? HoveredRing(cam, pivot, size) : HoveredHandle(cam, pivot, size, mode, flat);
            Paint();
            if (_hover >= 0 && EditorInput.LeftDown(0))
            {
                _drag = _hover;
                _dragMode = mode;
                _dragPivot = pivot;
                _dragMouse = EditorInput.MousePosition;
                var ray = cam.ScreenPointToRay(EditorInput.MousePosition);
                _dragT0 = _dragMode == GizmoMode.Move && _drag < FirstPlane ? AxisParam(ray, pivot, Axes[_drag]) : 0f;
                _dragHit0 = _dragMode == GizmoMode.Move && _drag >= FirstPlane && PlaneHit(ray, pivot, Axes[_drag - FirstPlane], out var h0) ? h0 : pivot;
                _live = (Quaternion.identity, 1f, Vector3.zero);
                _point = point;
                _pointYaw = pointYaw;
                Start.Clear();
                if (point != null) Points.BeginGizmo(pivot);
                else
                    foreach (var p in sel)
                    {
                        var t = p.Built.Handle;
                        Start.Add((p, t.position, t.rotation, t.localScale));
                    }
                string what = point != null ? point.Key : $"{sel.Count} copies";
                Core.Log.Msg($"gizmo: {_dragMode} drag{(_dragMode == GizmoMode.Rotate ? "" : " on " + HandleName(_drag))} of {what} from {pivot}");
                return true;
            }
            return false;
        }

        private static string HandleName(int handle)
        {
            if (handle < FirstPlane) return "XYZ"[handle].ToString();
            var (u, v) = PlaneAxes(handle - FirstPlane);
            return $"{"XYZ"[u]}{"XYZ"[v]} plane";
        }

        /// <summary>Puts every part of the gizmo where it belongs this frame and hides what the mode does not use.</summary>
        private static void Draw(Camera cam, Vector3 pivot, float size, GizmoMode mode, bool flat)
        {
            bool scale = mode == GizmoMode.Scale;
            for (int i = 0; i < 3; i++)
            {
                bool axis = Shown(i, mode, flat);
                Shafts[i].SetActive(axis);
                Cones[i].SetActive(axis && !scale);
                Cubes[i].SetActive(axis && scale);
                if (axis)
                {
                    var t = Shafts[i].transform;
                    float shaft = (scale ? 0.88f : ShaftEnd) * size;
                    t.position = pivot + Axes[i] * (shaft * 0.5f);
                    t.rotation = Quaternion.FromToRotation(Vector3.up, Axes[i]);
                    // The primitive cylinder is two units high.
                    t.localScale = new Vector3(size * 0.025f, shaft * 0.5f, size * 0.025f);
                    if (scale)
                    {
                        var c = Cubes[i].transform;
                        c.position = pivot + Axes[i] * (0.94f * size);
                        c.rotation = Quaternion.identity;
                        c.localScale = Vector3.one * (0.12f * size);
                    }
                    else PlaceCone(Cones[i], pivot + Axes[i] * (ShaftEnd * size), Axes[i], (1f - ShaftEnd) * size, ConeRadius * size);
                }

                bool plane = Shown(FirstPlane + i, mode, flat);
                Planes[i].SetActive(plane);
                if (plane)
                {
                    var c = PlaneCorners(cam, pivot, size, i);
                    var q = Planes[i].transform;
                    q.position = (c[0] + c[2]) * 0.5f;
                    // The primitive quad lies in its own XY plane, facing along Z.
                    q.rotation = i == 0 ? Quaternion.LookRotation(Vector3.right) : i == 1 ? Quaternion.LookRotation(Vector3.up, Vector3.forward) : Quaternion.identity;
                    q.localScale = new Vector3(PlaneSize * size, PlaneSize * size, 1f);
                }
            }
            bool ring = mode == GizmoMode.Rotate;
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
            }
            var ray = cam.ScreenPointToRay(EditorInput.MousePosition);
            if (_drag < FirstPlane)
            {
                float t = AxisParam(ray, _dragPivot, Axes[_drag]) - _dragT0;
                return (Quaternion.identity, 1f, Axes[_drag] * Along(_drag, t));
            }
            int n = _drag - FirstPlane;
            // Seen edge-on, the plane gives no usable point; the selection stays where the last good frame put it.
            if (!PlaneHit(ray, _dragPivot, Axes[n], out var hit)) return _live;
            var d = hit - _dragHit0;
            var (u, v) = PlaneAxes(n);
            var move = Vector3.zero;
            move[u] = Along(u, d[u]);
            move[v] = Along(v, d[v]);
            return (Quaternion.identity, 1f, move);
        }

        /// <summary>A distance along one axis, on the grid and against the faces of other copies as the toolbar says.</summary>
        private static float Along(int axis, float t)
        {
            if (Snap.Grid) t = Mathf.Round(t / Snap.GridStep) * Snap.GridStep;
            if (Snap.Edges && _point == null) t += EdgeOffset(axis, t);
            return t;
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
            {
                bool hot = i == _drag || (_drag < 0 && i == _hover);
                Mats[i].SetColor("_Color", hot ? Hot : AxisColors[i]);
                int plane = FirstPlane + i;
                bool planeHot = plane == _drag || (_drag < 0 && plane == _hover);
                var pc = planeHot ? Hot : AxisColors[i];
                pc.a = planeHot ? PlaneHotAlpha : PlaneAlpha;
                PlaneMats[i].SetColor("_Color", pc);
            }
            var c = _hover >= 0 || _drag >= 0 ? Hot : RingColor;
            _ringMat.SetColor("_Color", c);
            _ring.startColor = _ring.endColor = c;
        }

        /// <summary>The handle under the cursor: a plane square first, since it lies off the axes, else the nearest arrow.</summary>
        private static int HoveredHandle(Camera cam, Vector3 pivot, float size, GizmoMode mode, bool flat)
        {
            var mouse = EditorInput.MousePosition;
            var a = cam.WorldToScreenPoint(pivot);
            if (a.z <= 0f) return -1;
            for (int n = 0; n < 3; n++)
            {
                if (!Shown(FirstPlane + n, mode, flat)) continue;
                var c = PlaneCorners(cam, pivot, size, n);
                var s = new Vector2[4];
                bool behind = false;
                for (int k = 0; k < 4; k++)
                {
                    var p = cam.WorldToScreenPoint(c[k]);
                    if (p.z <= 0f) behind = true;
                    s[k] = p;
                }
                if (!behind && InQuad(mouse, s)) return FirstPlane + n;
            }
            int best = -1;
            float bestD = HandlePixels;
            for (int i = 0; i < 3; i++)
            {
                if (!Shown(i, mode, flat)) continue;
                var b = cam.WorldToScreenPoint(pivot + Axes[i] * size);
                if (b.z <= 0f) continue;
                float d = DistanceToSegment(mouse, a, b);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>True when the point lies inside the convex quad, whichever way round its corners run.</summary>
        private static bool InQuad(Vector2 p, Vector2[] q)
        {
            int sign = 0;
            for (int k = 0; k < 4; k++)
            {
                var e = q[(k + 1) % 4] - q[k];
                float cross = e.x * (p.y - q[k].y) - e.y * (p.x - q[k].x);
                int s = cross > 0f ? 1 : cross < 0f ? -1 : 0;
                if (s == 0) continue;
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
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

        /// <summary>Where the ray meets the plane through the origin; false when it runs nearly along the plane.</summary>
        private static bool PlaneHit(Ray ray, Vector3 origin, Vector3 normal, out Vector3 hit)
        {
            hit = origin;
            float along = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(along) < 0.02f) return false;
            float t = Vector3.Dot(origin - ray.origin, normal) / along;
            if (t < 0f) return false;
            hit = ray.origin + ray.direction * t;
            return true;
        }

        private static void RestoreStart()
        {
            foreach (var (p, pos, rot, scale) in Start)
            {
                if (p.Built.Handle == null) continue;
                var t = p.Built.Handle;
                t.SetPositionAndRotation(pos, rot);
                t.localScale = scale;
            }
        }

        private static void Commit()
        {
            if (_point != null)
            {
                CommitPoint();
                return;
            }
            var sel = Start.Select(x => x.p).Where(p => p.Built.Handle != null).ToList();
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

        /// <summary>Writes the moved or turned point as one step; its marker drew the change while dragging.</summary>
        private static void CommitPoint()
        {
            var r = _point;
            var (turn, _, move) = _live;
            Points.EndGizmo();
            _drag = -1;
            _point = null;
            _live = (Quaternion.identity, 1f, Vector3.zero);
            float angle = Mathf.DeltaAngle(0f, turn.eulerAngles.y);
            bool turned = Mathf.Abs(angle) >= 0.01f;
            if (!turned && move.sqrMagnitude < 1e-8f)
            {
                Core.Log.Msg($"gizmo: {r} stayed where it was");
                return;
            }
            string what = $"{Points.Label(r)}: {Points.SubLabel(r.Kind, r.Sub)} {(turned ? $"um {angle:0.#}° gedreht" : "verschoben")}";
            if (!Points.MoveTo(r, _dragPivot + move, _pointYaw + angle, what, "drag"))
                Core.Log.Warning($"gizmo: the drag of {r} was not written; the point stays where it was");
        }

        private static void Cancel(string why)
        {
            if (_point != null) Points.EndGizmo();
            _point = null;
            RestoreStart();
            Start.Clear();
            Core.Log.Warning($"gizmo: drag cancelled, {why}; the copies are back at their start");
            _drag = -1;
            _live = (Quaternion.identity, 1f, Vector3.zero);
        }

        /// <summary>Screen points on each handle, for scripted drags: three axis points, two ring points, the plane squares.</summary>
        internal static string Describe(Camera cam)
        {
            if (cam == null || !Target(out var pivot, out var point, out _)) return "nothing is selected";
            float size = Size(cam, pivot);
            string P(Vector3 w)
            {
                var s = cam.WorldToScreenPoint(w);
                return s.z > 0f ? $"{s.x:0} {s.y:0}" : "behind";
            }
            var planes = string.Join(", ", Enumerable.Range(0, 3).Select(n =>
            {
                var c = PlaneCorners(cam, pivot, size, n);
                var (u, v) = PlaneAxes(n);
                return $"{"xyz"[u]}{"xyz"[v]} {P((c[0] + c[2]) * 0.5f)}";
            }));
            return $"pivot {P(pivot)}; x {P(pivot + Axes[0] * size * 0.7f)}; y {P(pivot + Axes[1] * size * 0.7f)}; z {P(pivot + Axes[2] * size * 0.7f)}; " +
                   $"ring {P(pivot + Vector3.right * size)} / {P(pivot + Vector3.forward * size)}; planes {planes}; mode {ModeFor(point)}; " +
                   $"target {(point != null ? point.Key : "copies")}; screen {Screen.width}x{Screen.height}";
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
