using MapExpansions.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The move gizmo: three axis handles at the selection, drawn over the world. The handle under the cursor is
    /// picked on screen; dragging moves the copy along that axis by the point on the axis nearest the mouse ray.
    /// The copy follows the mouse while dragging, and the move is written into the definition on release.
    /// </summary>
    internal static class Gizmo
    {
        private const float HandlePixels = 10f;

        private static readonly Vector3[] Axes = { Vector3.right, Vector3.up, Vector3.forward };
        private static readonly Color[] AxisColors = { new(0.9f, 0.2f, 0.2f), new(0.3f, 0.85f, 0.3f), new(0.25f, 0.45f, 1f) };
        private static readonly Color Hot = new(1f, 0.85f, 0.2f);

        private static GameObject _root;
        private static readonly GameObject[] Handles = new GameObject[3];
        private static readonly Material[] Mats = new Material[3];

        private static int _hover = -1;
        private static int _drag = -1;
        private static Vector3 _dragPivot, _dragStartPos;
        private static float _dragT0;
        private static MainCopies.Placed _dragged;

        private static Shader _shader;
        private static bool _shaderLogged;

        internal static bool Dragging => _drag >= 0;

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
        }

        /// <summary>Returns true when the gizmo owns the mouse this frame (a drag, or a press on a handle).</summary>
        internal static bool Tick(Camera cam, bool overPanel)
        {
            var sel = Selection.Current;
            if (sel?.Built?.Local == null || cam == null)
            {
                if (_drag >= 0) Cancel("the selection is gone");
                if (_root != null) _root.SetActive(false);
                return false;
            }
            if (_root == null) Build();
            _root.SetActive(true);

            var pivot = _drag >= 0 ? _dragPivot + (sel.Built.Local.transform.position - _dragStartPos) : Selection.CurrentBounds.center;
            float size = Mathf.Max(0.3f, Vector3.Distance(cam.transform.position, pivot) * 0.15f);
            for (int i = 0; i < 3; i++)
            {
                var t = Handles[i].transform;
                t.position = pivot + Axes[i] * (size * 0.5f);
                t.rotation = Quaternion.FromToRotation(Vector3.up, Axes[i]);
                t.localScale = new Vector3(size * 0.04f, size * 0.5f, size * 0.04f);
            }

            if (_drag >= 0)
            {
                var ray = cam.ScreenPointToRay(Input.mousePosition);
                var delta = Axes[_drag] * (AxisParam(ray, _dragPivot, Axes[_drag]) - _dragT0);
                if (Input.GetMouseButton(0))
                {
                    sel.Built.Local.transform.position = _dragStartPos + delta;
                    Paint();
                    return true;
                }
                Commit(delta, "drag");
                return true;
            }

            _hover = overPanel ? -1 : HoveredAxis(cam, pivot, size);
            Paint();
            if (_hover >= 0 && Input.GetMouseButtonDown(0))
            {
                _drag = _hover;
                _dragged = sel;
                _dragPivot = pivot;
                _dragStartPos = sel.Built.Local.transform.position;
                _dragT0 = AxisParam(cam.ScreenPointToRay(Input.mousePosition), pivot, Axes[_drag]);
                Core.Log.Msg($"gizmo: drag {"XYZ"[_drag]} of {Copies.Key(sel)} from {_dragStartPos}");
                return true;
            }
            return false;
        }

        private static void Paint()
        {
            for (int i = 0; i < 3; i++)
                Mats[i].SetColor("_Color", i == _drag || (_drag < 0 && i == _hover) ? Hot : AxisColors[i]);
        }

        private static int HoveredAxis(Camera cam, Vector3 pivot, float size)
        {
            var mouse = (Vector2)Input.mousePosition;
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

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len = ab.sqrMagnitude;
            if (len < 1e-4f) return Vector2.Distance(p, a);
            // The first fifth of the axis is skipped: near the pivot all three handles overlap on screen.
            float t = Mathf.Clamp(Vector2.Dot(p - a, ab) / len, 0.2f, 1f);
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

        private static void Commit(Vector3 delta, string why)
        {
            var p = _dragged;
            _drag = -1;
            _dragged = null;
            if (p?.Built?.Local == null)
            {
                Core.Log.Warning($"gizmo ({why}): the dragged copy is gone; nothing is written");
                return;
            }
            // The live drag moved only the local object; Shift moves it again together with its networked parts.
            p.Built.Local.transform.position = _dragStartPos;
            Move(p, delta, why);
        }

        /// <summary>Moves a copy by a world offset and marks its layer entry for the next save.</summary>
        internal static void Move(MainCopies.Placed p, Vector3 delta, string why)
        {
            if (delta.sqrMagnitude < 1e-8f)
            {
                Core.Log.Msg($"move ({why}): {Copies.Key(p)} did not move");
                return;
            }
            var before = p.Built.Local.transform.position;
            MainCopies.Shift(p, delta);
            Saving.MarkDirty(p);
            Core.Log.Msg($"move ({why}): {Copies.Key(p)} by {delta.x:0.###} {delta.y:0.###} {delta.z:0.###} from {before} to {p.Built.Local.transform.position}");
            Panel.Note($"Verschoben: {p.Built.Def.Id} um {delta.magnitude:0.00} m");
        }

        private static void Cancel(string why)
        {
            if (_dragged?.Built?.Local != null) _dragged.Built.Local.transform.position = _dragStartPos;
            Core.Log.Warning($"gizmo: drag cancelled, {why}; the copy is back at its start");
            _drag = -1;
            _dragged = null;
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
