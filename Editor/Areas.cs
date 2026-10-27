using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    internal enum AreaKind
    {
        Rule,
        Opening,
        Window,
        Box,
    }

    /// <summary>
    /// A box the editor edits: the area of a rule, an opening or window of a hollow rule, or a panel or block of a
    /// copy. It is named by keys, so it stays the same box when its rule or copy is put anew.
    /// </summary>
    internal sealed class AreaRef
    {
        public AreaKind Kind;
        public string Expansion;
        /// <summary>Rule key for rule kinds.</summary>
        public string Rule;
        /// <summary>Index into the openings or windows.</summary>
        public int Index;
        /// <summary>Layer owner, copy id and box id for boxes.</summary>
        public string Owner, Copy, Box;

        public string Key => Kind switch
        {
            AreaKind.Rule => $"{Expansion}/rule/{Rule}",
            AreaKind.Opening => $"{Expansion}/rule/{Rule}/opening/{Index}",
            AreaKind.Window => $"{Expansion}/rule/{Rule}/window/{Index}",
            _ => $"{Expansion}/{Owner}/{Copy}/box/{Box}",
        };

        public override string ToString() => Key;

        public BuiltExpansion Built => BuildPipeline.Get(Expansion);

        public ObjectRule RuleDef => Built?.Definition.Hide.FirstOrDefault(r => r.Key == Rule);

        public CopyDefinition CopyDef
        {
            get
            {
                var b = Built;
                var copy = Copy;
                return b == null ? null : Layers.CopyList(b.Definition, Owner)?.FirstOrDefault(c => c.Id == copy);
            }
        }

        public BoxDefinition BoxDef
        {
            get
            {
                var box = Box;
                return CopyDef?.Boxes.FirstOrDefault(x => x.Id == box);
            }
        }

        public MainCopies.Placed Placed => Copies.Find($"{Expansion}/{Owner}/{Copy}");

        /// <summary>From the rule's coordinates to the world: none for the main side, the expansion's frame otherwise.</summary>
        internal static Matrix4x4 RuleMatrix(BuiltExpansion b, ObjectRule r) =>
            r.Side == WorldSide.Main ? Matrix4x4.identity : Matrix4x4.TRS(b.Frame.Offset, b.Frame.Rotation, Vector3.one);

        /// <summary>The box in its own coordinates and the matrix that takes them into the world. False when it is gone.</summary>
        public bool Get(out Bounds local, out Matrix4x4 toWorld)
        {
            local = default;
            toWorld = Matrix4x4.identity;
            var b = Built;
            if (b == null) return false;
            if (Kind == AreaKind.Box)
            {
                var box = BoxDef;
                var p = Placed;
                if (box == null || p?.Built?.Local == null) return false;
                local = box.Box;
                toWorld = p.Built.Move;
                return true;
            }
            var r = RuleDef;
            if (r == null) return false;
            toWorld = RuleMatrix(b, r);
            switch (Kind)
            {
                case AreaKind.Rule:
                    if (r.Area.size == Vector3.zero) return false;
                    local = r.Area;
                    return true;
                case AreaKind.Opening:
                    if (Index < 0 || Index >= r.Openings.Count) return false;
                    local = r.Openings[Index];
                    return true;
                default:
                    if (Index < 0 || Index >= r.Windows.Count) return false;
                    local = r.Windows[Index];
                    return true;
            }
        }

        public string Label => Kind switch
        {
            AreaKind.Rule => $"Regel {Rule}",
            AreaKind.Opening => $"Öffnung {Index + 1} von {Rule}",
            AreaKind.Window => $"Fenster {Index + 1} von {Rule}",
            _ => $"{BoxDef?.Kind.ToString() ?? "Box"} {Box} an {Copy}",
        };

        public static AreaRef OfRule(string expansion, string rule) => new() { Kind = AreaKind.Rule, Expansion = expansion, Rule = rule };

        public static AreaRef OfBox(string expansion, string owner, string copy, string box) =>
            new() { Kind = AreaKind.Box, Expansion = expansion, Owner = owner, Copy = copy, Box = box };

        /// <summary>Reads a key as <see cref="Key"/> writes it.</summary>
        public static AreaRef Parse(string key)
        {
            int s = key.IndexOf('/');
            if (s > 0 && string.CompareOrdinal(key, s, "/rule/", 0, 6) == 0)
            {
                var exp = key.Substring(0, s);
                var rest = key.Substring(s + 6);
                var m = System.Text.RegularExpressions.Regex.Match(rest, @"^(.+)/(opening|window)/(\d+)$");
                if (m.Success)
                    return new AreaRef { Kind = m.Groups[2].Value == "window" ? AreaKind.Window : AreaKind.Opening, Expansion = exp, Rule = m.Groups[1].Value, Index = int.Parse(m.Groups[3].Value) };
                return rest.Length > 0 ? OfRule(exp, rest) : null;
            }
            var p = key.Split('/');
            if (p.Length == 5 && p[3] == "box") return OfBox(p[0], p[1], p[2], p[4]);
            return null;
        }
    }

    /// <summary>
    /// The selected box and its gizmo. Six square handles sit on the faces: a drag moves that face along its axis. Three
    /// arrows from the centre move the whole box. Moves snap to <see cref="Step"/> while the grid is on. The box changes
    /// in one step of the history on release.
    /// </summary>
    internal static class Areas
    {
        internal const float Step = 0.1f;
        private const float MinSize = 0.05f;
        private const float HandlePixels = 12f;

        private static readonly Color Wire = new(0.2f, 0.85f, 1f);
        private static readonly Color Face = new(0.95f, 0.95f, 0.95f);
        private static readonly Color Hot = new(1f, 0.85f, 0.2f);
        private static readonly Color[] AxisColors = { new(0.9f, 0.2f, 0.2f), new(0.3f, 0.85f, 0.3f), new(0.25f, 0.45f, 1f) };

        internal static AreaRef Current { get; private set; }

        private static GameObject _root;
        private static LineRenderer _wire;
        private static readonly GameObject[] Faces = new GameObject[6];
        private static readonly Material[] FaceMats = new Material[6];
        private static readonly GameObject[] Arrows = new GameObject[3];
        private static readonly Material[] ArrowMats = new Material[3];

        private static int _hover = -1;
        private static int _drag = -1;
        private static Bounds _start, _live;
        private static Matrix4x4 _m;
        private static Vector3 _dragOrigin, _dragDir;
        private static float _dragT0, _dragScale;

        internal static bool Dragging => _drag >= 0;

        /// <summary>The running drag for the cursor readout: the new side length, or the move of the box.</summary>
        internal static string Readout
        {
            get
            {
                if (_drag < 0) return null;
                if (_drag < 6)
                {
                    int axis = _drag / 2;
                    return "XYZ"[axis] + "  " + Panel.Num(_live.size[axis] * _dragScale, 2) + " m";
                }
                int a = _drag - 6;
                return "XYZ"[a] + "  " + Panel.Num((_live.center[a] - _start.center[a]) * _dragScale, 2, true) + " m";
            }
        }

        internal static void Select(AreaRef a, string why)
        {
            if (a == null) return;
            Selection.ClearQuietly();
            Points.Deselect(why);
            Current = a;
            if (!a.Get(out var local, out var m))
            {
                Core.Log.Warning($"area ({why}): {a} has no box to edit (a rule without area, or a box of a copy that is not built); it is selected without a gizmo");
                Panel.Note("Ausgewählt: " + a.Label);
                return;
            }
            Core.Log.Msg($"area ({why}): {a} selected, local {local.min} to {local.max}, world centre {m.MultiplyPoint3x4(local.center)}");
            Panel.Note("Ausgewählt: " + a.Label);
        }

        internal static void Deselect(string why)
        {
            if (Current == null) return;
            if (_drag >= 0) Cancel(why);
            Core.Log.Msg($"area ({why}): {Current} is no longer selected");
            Current = null;
        }

        /// <summary>World box around the selected box, for framing.</summary>
        internal static bool WorldBounds(out Bounds world)
        {
            world = default;
            if (Current == null || !Current.Get(out var local, out var m)) return false;
            world = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
            foreach (var c in Corners(local, m)) world.Encapsulate(c);
            return true;
        }

        private static Vector3[] Corners(Bounds b, Matrix4x4 m)
        {
            var c = new Vector3[8];
            for (int i = 0; i < 8; i++)
                c[i] = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
            return c;
        }

        private static void Build()
        {
            _root = new GameObject("MapExpansionsEditor_AreaGizmo");
            Object.DontDestroyOnLoad(_root);
            var w = new GameObject("wire");
            w.transform.SetParent(_root.transform, false);
            _wire = w.AddComponent<LineRenderer>();
            _wire.useWorldSpace = true;
            _wire.material = Gizmo.Material(Wire);
            _wire.startColor = _wire.endColor = Wire;
            _wire.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _wire.receiveShadows = false;
            for (int i = 0; i < 6; i++)
            {
                Faces[i] = Primitive(PrimitiveType.Cube, "face" + i, FaceMats[i] = Gizmo.Material(Face));
            }
            for (int i = 0; i < 3; i++)
            {
                Arrows[i] = Primitive(PrimitiveType.Cylinder, "arrow" + "XYZ"[i], ArrowMats[i] = Gizmo.Material(AxisColors[i]));
            }
        }

        private static GameObject Primitive(PrimitiveType type, string name, Material mat)
        {
            var g = GameObject.CreatePrimitive(type);
            g.name = name;
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(_root.transform, false);
            var r = g.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return g;
        }

        /// <summary>The world point of a handle: faces 0-5 are -x +x -y +y -z +z, 6-8 the arrow tips.</summary>
        private static Vector3 HandlePoint(int h, Bounds b, Matrix4x4 m, float size)
        {
            if (h < 6)
            {
                int axis = h / 2;
                var p = b.center;
                p[axis] = h % 2 == 0 ? b.min[axis] : b.max[axis];
                return m.MultiplyPoint3x4(p);
            }
            return m.MultiplyPoint3x4(b.center) + WorldAxis(m, h - 6) * size;
        }

        private static Vector3 WorldAxis(Matrix4x4 m, int axis)
        {
            var e = Vector3.zero;
            e[axis] = 1f;
            var v = m.MultiplyVector(e);
            return v.sqrMagnitude < 1e-10f ? e : v.normalized;
        }

        private static float AxisScale(Matrix4x4 m, int axis)
        {
            var e = Vector3.zero;
            e[axis] = 1f;
            return Mathf.Max(1e-4f, m.MultiplyVector(e).magnitude);
        }

        /// <summary>Returns true when the gizmo owns the mouse this frame.</summary>
        internal static bool Tick(Camera cam, bool overPanel)
        {
            Bounds b;
            Matrix4x4 m;
            if (Current == null || cam == null || !Current.Get(out b, out m))
            {
                if (_drag >= 0) Cancel("the box is gone");
                if (_root != null) _root.SetActive(false);
                return false;
            }
            if (_root == null) Build();
            _root.SetActive(true);
            if (_drag >= 0)
            {
                b = _live;
                m = _m;
            }
            var centre = m.MultiplyPoint3x4(b.center);
            float size = Mathf.Max(0.3f, FlyCamera.ViewDistance(cam, centre) * 0.12f);
            Draw(b, m, size);

            if (_drag >= 0)
            {
                if (EditorInput.Left(0))
                {
                    _live = LiveBox(cam);
                    Paint();
                    return true;
                }
                Commit();
                return true;
            }

            _hover = overPanel ? -1 : Hovered(cam, b, m, size);
            Paint();
            if (_hover >= 0 && EditorInput.LeftDown(0))
            {
                _drag = _hover;
                _start = _live = b;
                _m = m;
                int axis = _drag < 6 ? _drag / 2 : _drag - 6;
                _dragDir = WorldAxis(m, axis);
                _dragScale = AxisScale(m, axis);
                _dragOrigin = HandlePoint(_drag, b, m, size);
                _dragT0 = AxisParam(cam.ScreenPointToRay(EditorInput.MousePosition), _dragOrigin, _dragDir);
                Core.Log.Msg($"area: drag of {(_drag < 6 ? "face " + "-+"[_drag % 2] + "xyz"[axis] : "arrow " + "xyz"[axis])} of {Current} from {_dragOrigin}");
                return true;
            }
            return false;
        }

        private static Bounds LiveBox(Camera cam)
        {
            float t = AxisParam(cam.ScreenPointToRay(EditorInput.MousePosition), _dragOrigin, _dragDir) - _dragT0;
            if (Snap.Grid) t = Mathf.Round(t / Step) * Step;
            float d = t / _dragScale;
            var min = _start.min;
            var max = _start.max;
            if (_drag < 6)
            {
                int axis = _drag / 2;
                if (_drag % 2 == 0) min[axis] = Mathf.Min(min[axis] + d, max[axis] - MinSize);
                else max[axis] = Mathf.Max(max[axis] + d, min[axis] + MinSize);
            }
            else
            {
                int axis = _drag - 6;
                min[axis] += d;
                max[axis] += d;
            }
            var nb = new Bounds();
            nb.SetMinMax(min, max);
            return nb;
        }

        private static void Commit()
        {
            var a = Current;
            var before = _start;
            var after = _live;
            int handle = _drag;
            _drag = -1;
            if (a == null) return;
            if ((after.min - before.min).sqrMagnitude < 1e-8f && (after.max - before.max).sqrMagnitude < 1e-8f)
            {
                Core.Log.Msg($"area: {a} kept its size");
                return;
            }
            string what = handle < 6 ? $"{a.Label}: Seite verschoben" : $"{a.Label} verschoben";
            AreaEdits.SetBox(a, after, what, "drag");
        }

        private static void Cancel(string why)
        {
            _drag = -1;
            Core.Log.Warning($"area: drag cancelled, {why}; the box stays as it was");
        }

        private static int Hovered(Camera cam, Bounds b, Matrix4x4 m, float size)
        {
            var mouse = EditorInput.MousePosition;
            int best = -1;
            float bestD = HandlePixels;
            for (int h = 0; h < 6; h++)
            {
                var s = cam.WorldToScreenPoint(HandlePoint(h, b, m, size));
                if (s.z <= 0f) continue;
                float d = Vector2.Distance(mouse, new Vector2(s.x, s.y));
                if (d < bestD) { bestD = d; best = h; }
            }
            if (best >= 0) return best;
            var c = cam.WorldToScreenPoint(m.MultiplyPoint3x4(b.center));
            if (c.z <= 0f) return -1;
            for (int i = 0; i < 3; i++)
            {
                var tip = cam.WorldToScreenPoint(HandlePoint(6 + i, b, m, size));
                if (tip.z <= 0f) continue;
                float d = Gizmo.DistanceToSegment(mouse, c, tip);
                if (d < bestD) { bestD = d; best = 6 + i; }
            }
            return best;
        }

        private static void Draw(Bounds b, Matrix4x4 m, float size)
        {
            var k = Corners(b, m);
            // One path over all twelve edges, as the selection outline draws them.
            int[] path = { 0, 1, 5, 4, 0, 2, 3, 7, 6, 2, 3, 1, 5, 7, 6, 4 };
            _wire.positionCount = path.Length;
            for (int i = 0; i < path.Length; i++) _wire.SetPosition(i, k[path[i]]);
            _wire.startWidth = _wire.endWidth = size * 0.012f;
            for (int h = 0; h < 6; h++)
            {
                var t = Faces[h].transform;
                t.position = HandlePoint(h, b, m, size);
                t.rotation = m.rotation;
                t.localScale = Vector3.one * size * 0.08f;
            }
            var centre = m.MultiplyPoint3x4(b.center);
            for (int i = 0; i < 3; i++)
            {
                var dir = WorldAxis(m, i);
                var t = Arrows[i].transform;
                t.position = centre + dir * (size * 0.5f);
                t.rotation = Quaternion.FromToRotation(Vector3.up, dir);
                t.localScale = new Vector3(size * 0.035f, size * 0.5f, size * 0.035f);
            }
        }

        private static void Paint()
        {
            for (int h = 0; h < 6; h++)
                FaceMats[h].SetColor("_Color", h == _drag || (_drag < 0 && h == _hover) ? Hot : Face);
            for (int i = 0; i < 3; i++)
                ArrowMats[i].SetColor("_Color", 6 + i == _drag || (_drag < 0 && 6 + i == _hover) ? Hot : AxisColors[i]);
        }

        private static float AxisParam(Ray ray, Vector3 origin, Vector3 axis)
        {
            var w = origin - ray.origin;
            float b = Vector3.Dot(axis, ray.direction);
            float d = Vector3.Dot(axis, w);
            float e = Vector3.Dot(ray.direction, w);
            float denom = 1f - b * b;
            if (denom < 1e-5f) return 0f;
            return (b * e - d) / denom;
        }

        /// <summary>Screen points of the handles, for scripted drags: faces -x +x -y +y -z +z, then arrows x y z.</summary>
        internal static string Describe(Camera cam)
        {
            if (Current == null || cam == null) return "no box is selected";
            if (!Current.Get(out var b, out var m)) return $"{Current} has no box";
            var centre = m.MultiplyPoint3x4(b.center);
            float size = Mathf.Max(0.3f, FlyCamera.ViewDistance(cam, centre) * 0.12f);
            string P(Vector3 w)
            {
                var s = cam.WorldToScreenPoint(w);
                return s.z > 0f ? $"{s.x:0} {s.y:0}" : "behind";
            }
            var parts = new List<string>();
            string[] names = { "-x", "+x", "-y", "+y", "-z", "+z" };
            for (int h = 0; h < 6; h++) parts.Add($"{names[h]} {P(HandlePoint(h, b, m, size))}");
            for (int i = 0; i < 3; i++) parts.Add($"{"xyz"[i]} {P(centre + WorldAxis(m, i) * size * 0.7f)}");
            return $"{Current}: {string.Join("; ", parts)}; local {b.min} to {b.max}; screen {Screen.width}x{Screen.height}";
        }

        internal static void Release()
        {
            if (_drag >= 0) Cancel("the editor closed");
            if (_root != null) _root.SetActive(false);
            _hover = -1;
        }
    }
}
