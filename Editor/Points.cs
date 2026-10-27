using System.Text.Json.Nodes;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    /// <summary>
    /// One gameplay point the editor edits: a location, a deal location, a parking lot entry or spot, a door of a
    /// building, a shop counter, or a corner of a region. Named by expansion, kind, entry key and the index of the
    /// point within the entry.
    /// </summary>
    internal sealed class PointRef
    {
        public string Expansion, Kind, Id;
        /// <summary>Lots: 0 is the entry, 1.. the spots. Buildings: the doors. Regions: the corners. Else 0.</summary>
        public int Sub;

        public string EntryKey => $"{Expansion}/{Kind}/{Id}";
        public string Key => $"{EntryKey}/{Sub}";
        public override string ToString() => Key;

        public BuiltExpansion Built => BuildPipeline.Get(Expansion);

        public static PointRef Parse(string key)
        {
            var p = (key ?? "").Split('/');
            if (p.Length < 3 || !Layers.PointKinds.Contains(p[1])) return null;
            int sub = p.Length > 3 && int.TryParse(p[3], out int s) ? s : 0;
            return new PointRef { Expansion = p[0], Kind = p[1], Id = p[2], Sub = sub };
        }

        public PointRef WithSub(int sub) => new() { Expansion = Expansion, Kind = Kind, Id = Id, Sub = sub };
    }

    /// <summary>
    /// The gameplay points of the built expansions as markers in the world: a post per point with a nose that shows
    /// its facing, and a line around each region. A click on a marker selects its entry, a drag moves the point over
    /// the ground. The game builds its delivery spots, regions, parking lots, doors and shops once per world build,
    /// so a change shows in the markers at once and in the game after the next load.
    /// </summary>
    internal static class Points
    {
        private const float PickPixels = 18f;
        private const float DragPixels = 4f;

        internal static readonly Dictionary<string, string> KindLabels = new()
        {
            [Layers.LocationsKind] = "Ort",
            [Layers.RegionsKind] = "Region",
            [Layers.DealsKind] = "Deal-Ort",
            [Layers.LotsKind] = "Parkplatz",
            [Layers.BuildingsKind] = "Gebäude",
            [Layers.ShopsKind] = "Laden",
        };

        private static readonly Dictionary<string, Color> KindColors = new()
        {
            [Layers.LocationsKind] = new Color(0.95f, 0.95f, 0.95f),
            [Layers.RegionsKind] = new Color(0.25f, 0.85f, 0.45f),
            [Layers.DealsKind] = new Color(1f, 0.45f, 0.75f),
            [Layers.LotsKind] = new Color(0.3f, 0.55f, 1f),
            [Layers.BuildingsKind] = new Color(1f, 0.6f, 0.2f),
            [Layers.ShopsKind] = new Color(0.75f, 0.45f, 1f),
        };

        private static readonly Color Hot = new(1f, 0.85f, 0.2f);

        internal static PointRef Current { get; private set; }

        private sealed class Marker
        {
            public GameObject Root;
            public Material Mat;
            public PointRef Ref;
            public Vector3 World;
        }

        private static GameObject _root;
        private static readonly List<Marker> Markers = new();
        private static readonly List<LineRenderer> Outlines = new();
        private static int _used, _usedLines;

        private static Marker _hover;
        private static PointRef _press;
        private static Vector2 _pressAt;
        private static bool _dragging;
        private static JsonObject _dragBefore;
        private static Vector3 _dragWorld;

        internal static bool Dragging => _dragging;

        // ---- reading points

        private static string SubLabel(string kind, int sub) => kind switch
        {
            Layers.LotsKind => sub == 0 ? "Zufahrt" : $"Stellplatz {sub}",
            Layers.BuildingsKind => $"Tür {sub + 1}",
            Layers.RegionsKind => $"Ecke {sub + 1}",
            Layers.ShopsKind => "Theke",
            _ => "Punkt",
        };

        internal static string Label(PointRef r) => $"{KindLabels.GetValueOrDefault(r.Kind, r.Kind)} {r.Id}";

        /// <summary>The points of one entry in its own coordinates, as the definition holds them now.</summary>
        private static List<LocalPoint> SubPoints(ExpansionDefinition def, string kind, string id)
        {
            switch (kind)
            {
                case Layers.LocationsKind:
                    var l = def.Locations.FirstOrDefault(x => x.Name == id);
                    return l == null ? null : new List<LocalPoint> { new(l.LocalPosition, l.LocalYaw) };
                case Layers.RegionsKind:
                    return def.Regions.FirstOrDefault(x => x.Name == id)?.Polygon.Select(p => new LocalPoint(new Vector3(p.x, 0f, p.y))).ToList();
                case Layers.DealsKind:
                    var d = def.DealLocations.FirstOrDefault(x => x.Id == id);
                    return d == null ? null : new List<LocalPoint> { d.StandPoint };
                case Layers.LotsKind:
                    var lot = def.ParkingLots.FirstOrDefault(x => x.Id == id);
                    return lot == null ? null : new[] { lot.Entry }.Concat(lot.Spots).ToList();
                case Layers.BuildingsKind:
                    return def.Buildings.FirstOrDefault(x => x.Id == id)?.Doors.ToList();
                case Layers.ShopsKind:
                    var s = def.Shops.FirstOrDefault(x => x.Id == id);
                    return s == null ? null : new List<LocalPoint> { s.Counter };
                default:
                    return null;
            }
        }

        /// <summary>Every entry key of the built expansions, kind by kind.</summary>
        internal static IEnumerable<(BuiltExpansion b, string kind, string id)> Entries()
        {
            foreach (var b in BuildPipeline.All)
            {
                if (b?.Definition == null) continue;
                var def = b.Definition;
                foreach (var x in def.Locations) yield return (b, Layers.LocationsKind, x.Name);
                foreach (var x in def.Regions) yield return (b, Layers.RegionsKind, x.Name);
                foreach (var x in def.DealLocations) yield return (b, Layers.DealsKind, x.Id);
                foreach (var x in def.ParkingLots) yield return (b, Layers.LotsKind, x.Id);
                foreach (var x in def.Buildings) yield return (b, Layers.BuildingsKind, x.Id);
                foreach (var x in def.Shops.Where(s => !string.IsNullOrEmpty(s.Id))) yield return (b, Layers.ShopsKind, x.Id);
            }
        }

        /// <summary>Locations and regions sit in the expansion frame, the places in the built root.</summary>
        private static bool InFrame(string kind) => kind is Layers.LocationsKind or Layers.RegionsKind;

        private static Vector3 ToWorld(BuiltExpansion b, string kind, LocalPoint p, out float yaw)
        {
            if (InFrame(kind))
            {
                yaw = b.Frame.Rotation.eulerAngles.y + p.Yaw;
                var w = b.Frame.ToWorld(p.Position);
                if (kind == Layers.RegionsKind) w.y = Ground(w, b.Frame.Offset.y);
                return w;
            }
            var t = b.Root.transform;
            yaw = t.eulerAngles.y + p.Yaw;
            return t.TransformPoint(p.Position);
        }

        private static LocalPoint ToLocal(BuiltExpansion b, string kind, Vector3 world, float worldYaw)
        {
            if (InFrame(kind))
                return new LocalPoint(b.Frame.ToLocal(world), Mathf.Repeat(worldYaw - b.Frame.Rotation.eulerAngles.y, 360f));
            var t = b.Root.transform;
            return new LocalPoint(t.InverseTransformPoint(world), Mathf.Repeat(worldYaw - t.eulerAngles.y, 360f));
        }

        /// <summary>Height of the first thing below a point, searched from 200 m above it.</summary>
        private static float Ground(Vector3 at, float fallback)
        {
            return Physics.Raycast(new Vector3(at.x, at.y + 200f, at.z), Vector3.down, out var hit, 600f, ~0, QueryTriggerInteraction.Ignore)
                ? hit.point.y
                : fallback;
        }

        internal static bool WorldOf(PointRef r, out Vector3 world, out float yaw)
        {
            world = default;
            yaw = 0f;
            var b = r?.Built;
            var pts = b == null ? null : SubPoints(b.Definition, r.Kind, r.Id);
            if (pts == null || r.Sub < 0 || r.Sub >= pts.Count) return false;
            world = ToWorld(b, r.Kind, pts[r.Sub], out yaw);
            return true;
        }

        // ---- selection

        internal static void Select(PointRef r, string why)
        {
            if (r == null) return;
            Selection.ClearQuietly();
            Areas.Deselect(why);
            Current = r;
            if (WorldOf(r, out var w, out _)) Core.Log.Msg($"point ({why}): {r} selected at {w}");
            else Core.Log.Warning($"point ({why}): {r} names no point of a built expansion; it is selected without a marker");
            Panel.Note("Ausgewählt: " + Label(r) + ", " + SubLabel(r.Kind, r.Sub));
        }

        internal static void Deselect(string why)
        {
            if (Current == null) return;
            if (_dragging) CancelDrag(why);
            Core.Log.Msg($"point ({why}): {Current} is no longer selected");
            Current = null;
        }

        // ---- markers

        private static void Build()
        {
            _root = new GameObject("MapExpansionsEditor_Points");
            Object.DontDestroyOnLoad(_root);
        }

        private static Marker NextMarker()
        {
            if (_used < Markers.Count) return Markers[_used++];
            var m = new Marker { Root = new GameObject("point"), Mat = Gizmo.Material(Color.white) };
            m.Root.transform.SetParent(_root.transform, false);
            var post = Part(PrimitiveType.Cylinder, m.Root.transform, m.Mat);
            post.localPosition = new Vector3(0f, 0.8f, 0f);
            post.localScale = new Vector3(0.18f, 0.8f, 0.18f);
            var nose = Part(PrimitiveType.Cube, m.Root.transform, m.Mat);
            nose.localPosition = new Vector3(0f, 1.45f, 0.3f);
            nose.localScale = new Vector3(0.12f, 0.12f, 0.5f);
            Markers.Add(m);
            _used++;
            return m;
        }

        private static Transform Part(PrimitiveType type, Transform parent, Material mat)
        {
            var g = GameObject.CreatePrimitive(type);
            Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            var r = g.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return g.transform;
        }

        private static LineRenderer NextLine()
        {
            if (_usedLines < Outlines.Count) return Outlines[_usedLines++];
            var g = new GameObject("region");
            g.transform.SetParent(_root.transform, false);
            var l = g.AddComponent<LineRenderer>();
            l.useWorldSpace = true;
            l.loop = true;
            l.material = Gizmo.Material(KindColors[Layers.RegionsKind]);
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.startWidth = l.endWidth = 0.3f;
            Outlines.Add(l);
            _usedLines++;
            return l;
        }

        /// <summary>Places a marker on every point and a line around every region. Regions far away are drawn too.</summary>
        private static void Draw(Camera cam)
        {
            _used = 0;
            _usedLines = 0;
            foreach (var (b, kind, id) in Entries())
            {
                var pts = SubPoints(b.Definition, kind, id);
                if (pts == null) continue;
                var world = new Vector3[pts.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    var r = new PointRef { Expansion = b.Id, Kind = kind, Id = id, Sub = i };
                    bool live = _dragging && Current?.Key == r.Key;
                    world[i] = live ? _dragWorld : ToWorld(b, kind, pts[i], out _);
                    ToWorld(b, kind, pts[i], out float yaw);
                    // Region corners are many and far apart; only the selected region's corners get posts.
                    if (kind == Layers.RegionsKind && Current?.EntryKey != r.EntryKey) continue;
                    var m = NextMarker();
                    m.Ref = r;
                    m.World = world[i];
                    m.Root.SetActive(true);
                    m.Root.transform.SetPositionAndRotation(world[i], Quaternion.Euler(0f, yaw, 0f));
                    float scale = Mathf.Clamp(Vector3.Distance(cam.transform.position, world[i]) * 0.05f, 1f, 6f);
                    m.Root.transform.localScale = Vector3.one * scale;
                    bool hot = Current?.Key == r.Key || _hover == m;
                    bool entry = Current?.EntryKey == r.EntryKey;
                    var c = KindColors[kind];
                    m.Mat.SetColor("_Color", hot ? Hot : entry ? Color.Lerp(c, Color.white, 0.5f) : c);
                }
                if (kind == Layers.RegionsKind && world.Length >= 2)
                {
                    var l = NextLine();
                    l.gameObject.SetActive(true);
                    l.positionCount = world.Length;
                    for (int i = 0; i < world.Length; i++) l.SetPosition(i, world[i] + Vector3.up * 0.3f);
                    l.startWidth = l.endWidth = Current?.EntryKey == $"{b.Id}/{kind}/{id}" ? 0.6f : 0.3f;
                }
            }
            for (int i = _used; i < Markers.Count; i++) Markers[i].Root.SetActive(false);
            for (int i = _usedLines; i < Outlines.Count; i++) Outlines[i].gameObject.SetActive(false);
        }

        private static Marker Hovered(Camera cam, Vector2 mouse)
        {
            Marker best = null;
            float bestD = PickPixels;
            for (int i = 0; i < _used; i++)
            {
                var m = Markers[i];
                // The post stands on the point; its middle is easier to hit than its foot.
                foreach (var h in new[] { 0f, 0.8f, 1.45f })
                {
                    var s = cam.WorldToScreenPoint(m.World + Vector3.up * h * m.Root.transform.localScale.y);
                    if (s.z <= 0f) continue;
                    float d = Vector2.Distance(mouse, new Vector2(s.x, s.y));
                    if (d < bestD)
                    {
                        bestD = d;
                        best = m;
                    }
                }
            }
            return best;
        }

        /// <summary>Returns true when a marker owns the mouse this frame.</summary>
        internal static bool Tick(Camera cam, bool overPanel)
        {
            if (cam == null) return false;
            if (_root == null) Build();
            _root.SetActive(true);
            Draw(cam);
            var mouse = EditorInput.MousePosition;

            if (_press != null)
            {
                if (EditorInput.Left(0))
                {
                    if (!_dragging && Vector2.Distance(mouse, _pressAt) > DragPixels) StartDrag();
                    if (_dragging && GroundUnder(cam, mouse, out var at)) _dragWorld = at;
                    return true;
                }
                if (_dragging) CommitDrag();
                _press = null;
                return true;
            }

            _hover = overPanel ? null : Hovered(cam, mouse);
            if (_hover != null && EditorInput.LeftDown(0))
            {
                Select(_hover.Ref, "click");
                _press = _hover.Ref;
                _pressAt = mouse;
                return true;
            }
            return false;
        }

        private static bool GroundUnder(Camera cam, Vector2 mouse, out Vector3 at)
        {
            var ray = cam.ScreenPointToRay(mouse);
            if (Physics.Raycast(ray, out var hit, 600f, ~0, QueryTriggerInteraction.Ignore))
            {
                at = hit.point;
                return true;
            }
            // Over the void: keep the height the point had.
            var plane = new Plane(Vector3.up, _dragWorld);
            if (plane.Raycast(ray, out float t))
            {
                at = ray.GetPoint(t);
                return true;
            }
            at = default;
            return false;
        }

        private static void StartDrag()
        {
            var b = Current?.Built;
            if (b == null || !WorldOf(Current, out _dragWorld, out _))
            {
                Core.Log.Warning($"point: {Current} cannot be dragged, it is not in a built expansion");
                _press = null;
                return;
            }
            _dragBefore = Layers.PointEntry(b.Definition, Current.Kind, Current.Id);
            _dragging = true;
            Core.Log.Msg($"point: drag of {Current} from {_dragWorld}");
        }

        private static void CommitDrag()
        {
            _dragging = false;
            var r = Current;
            if (r == null) return;
            if (!WorldOf(r, out var from, out float yaw)) return;
            if ((from - _dragWorld).sqrMagnitude < 1e-6f)
            {
                Core.Log.Msg($"point: {r} stayed where it was");
                return;
            }
            var to = _dragWorld;
            if (Snap.Grid)
            {
                to.x = Mathf.Round(to.x / Areas.Step) * Areas.Step;
                to.z = Mathf.Round(to.z / Areas.Step) * Areas.Step;
            }
            MoveTo(r, to, yaw, $"{Label(r)}: {SubLabel(r.Kind, r.Sub)} verschoben", "drag");
        }

        private static void CancelDrag(string why)
        {
            _dragging = false;
            _press = null;
            Core.Log.Warning($"point: drag cancelled, {why}; the point stays as it was");
        }

        internal static void Release()
        {
            if (_dragging) CancelDrag("the editor closed");
            _press = null;
            _hover = null;
            if (_root != null) _root.SetActive(false);
        }

        // ---- changes

        /// <summary>One change of one entry, as its layer entry before and after, and its place in the layer.</summary>
        internal sealed class PointEdit : Edits.Edit
        {
            public string Expansion, Kind, Id;
            public JsonObject Before, After;
            public bool LayerBefore, RemovedBefore, LayerAfter, RemovedAfter;
            public int SubAfter, SubBefore;

            public override bool Do() => Put(Expansion, Kind, Id, After, LayerAfter, RemovedAfter, SubAfter, "do");

            public override void Undo() => Put(Expansion, Kind, Id, Before, LayerBefore, RemovedBefore, SubBefore, "undo");
        }

        private static bool Put(string exp, string kind, string id, JsonObject entry, bool inLayer, bool removed, int sub, string why)
        {
            var b = BuildPipeline.Get(exp);
            if (b == null)
            {
                Core.Log.Error($"point ({why}): expansion '{exp}' is not built; {kind} '{id}' stays as it is");
                return false;
            }
            var copy = entry == null ? null : (JsonObject)JsonNode.Parse(entry.ToJsonString());
            if (!Layers.PutPoint(b.Definition, kind, id, copy)) return false;
            var state = Layers.StateOf(exp);
            if (inLayer) state.Points.Add((kind, id));
            else state.Points.Remove((kind, id));
            if (removed) state.RemovedPoints.Add((kind, id));
            else state.RemovedPoints.Remove((kind, id));
            Saving.MarkDirty(exp);
            var r = new PointRef { Expansion = exp, Kind = kind, Id = id, Sub = sub };
            if (entry == null)
            {
                if (Current?.EntryKey == r.EntryKey) Current = null;
            }
            else Current = r;
            Core.Log.Msg($"point ({why}): '{exp}' {kind} '{id}' {(entry == null ? "taken out" : "put in: " + entry.ToJsonString())}; " +
                         $"layer entry {inLayer}, removal {removed}; the game uses it after the next load");
            return true;
        }

        private static bool Change(PointRef r, Func<JsonObject, bool> change, string what, string why, Func<int> subAfter = null)
        {
            var b = r?.Built;
            if (b == null)
            {
                Core.Log.Warning($"point ({why}): nothing is selected or its expansion is not built; nothing changed");
                Panel.Note("Kein Punkt ausgewählt.");
                return false;
            }
            var before = Layers.PointEntry(b.Definition, r.Kind, r.Id);
            if (before == null)
            {
                Core.Log.Warning($"point ({why}): {r} is no longer in the definition");
                return false;
            }
            var after = (JsonObject)JsonNode.Parse(before.ToJsonString());
            if (!change(after)) return false;
            var state = Layers.StateOf(r.Expansion);
            bool layered = state.Points.Contains((r.Kind, r.Id));
            return Edits.Run(new PointEdit
            {
                Expansion = r.Expansion, Kind = r.Kind, Id = r.Id, Before = before, After = after,
                LayerBefore = layered, RemovedBefore = false, LayerAfter = true, RemovedAfter = false,
                SubBefore = r.Sub, SubAfter = subAfter?.Invoke() ?? r.Sub, What = what,
            }, why);
        }

        private static JsonObject PtJson(LocalPoint p) => new()
        {
            ["pos"] = new JsonArray(MathF.Round(p.Position.x, 3), MathF.Round(p.Position.y, 3), MathF.Round(p.Position.z, 3)),
            ["yaw"] = MathF.Round(p.Yaw, 3),
        };

        /// <summary>Writes one point of an entry; false when the entry has no point with that index.</summary>
        private static bool SetSub(JsonObject e, string kind, int sub, LocalPoint p)
        {
            switch (kind)
            {
                case Layers.LocationsKind:
                case Layers.DealsKind:
                    e["at"] = PtJson(p);
                    return true;
                case Layers.ShopsKind:
                    e["counter"] = PtJson(p);
                    return true;
                case Layers.LotsKind:
                    if (sub == 0)
                    {
                        e["entry"] = PtJson(p);
                        return true;
                    }
                    return SetIn(e["spots"] as JsonArray, sub - 1, PtJson(p));
                case Layers.BuildingsKind:
                    return SetIn(e["doors"] as JsonArray, sub, PtJson(p));
                case Layers.RegionsKind:
                    return SetIn(e["polygon"] as JsonArray, sub, new JsonArray(MathF.Round(p.Position.x, 3), MathF.Round(p.Position.z, 3)));
                default:
                    return false;
            }
        }

        private static bool SetIn(JsonArray a, int i, JsonNode value)
        {
            if (a == null || i < 0 || i >= a.Count) return false;
            a[i] = value;
            return true;
        }

        internal static bool MoveTo(PointRef r, Vector3 world, float worldYaw, string what, string why)
        {
            var b = r?.Built;
            if (b == null) return Change(r, _ => false, what, why);
            var local = ToLocal(b, r.Kind, world, worldYaw);
            return Change(r, e =>
            {
                if (SetSub(e, r.Kind, r.Sub, local)) return true;
                Core.Log.Warning($"point ({why}): {r} has no point {r.Sub}");
                return false;
            }, what, why);
        }

        /// <summary>Where the view centre meets the ground, and the facing back towards the camera.</summary>
        internal static bool ViewPoint(out Vector3 at, out float yaw)
        {
            var cam = EditorMode.Camera?.Camera;
            at = default;
            yaw = 0f;
            if (cam == null) return false;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            at = Physics.Raycast(ray, out var hit, 300f, ~0, QueryTriggerInteraction.Ignore) ? hit.point : ray.GetPoint(10f);
            var back = cam.transform.position - at;
            yaw = Mathf.Atan2(back.x, back.z) * Mathf.Rad2Deg;
            return true;
        }

        internal static bool Here(string why)
        {
            var r = Current;
            if (r == null || !ViewPoint(out var at, out float yaw))
            {
                Core.Log.Warning($"point ({why}): no point is selected or there is no editor camera");
                Panel.Note("Kein Punkt ausgewählt.");
                return false;
            }
            return MoveTo(r, at, yaw, $"{Label(r)}: {SubLabel(r.Kind, r.Sub)} an die Blickmitte", why);
        }

        /// <summary>A new entry at the view centre. Shops cannot be made here: they need copies and a seller from the code.</summary>
        internal static bool New(string kind, string id, string why)
        {
            if (kind == Layers.ShopsKind || !Layers.PointKinds.Contains(kind))
            {
                Core.Log.Warning($"point ({why}): '{kind}' cannot be made in the editor; it takes {string.Join(", ", Layers.PointKinds.Where(k => k != Layers.ShopsKind))}");
                Panel.Note("Läden kommen aus dem Code.");
                return false;
            }
            if (!ViewPoint(out var at, out float yaw))
            {
                Core.Log.Warning($"point ({why}): there is no editor camera");
                return false;
            }
            var b = BuildPipeline.All.Where(x => x?.Root != null).OrderBy(x => (x.Root.transform.position - at).sqrMagnitude).FirstOrDefault();
            if (b == null)
            {
                Core.Log.Warning($"point ({why}): no expansion is built");
                return false;
            }
            var def = b.Definition;
            if (string.IsNullOrEmpty(id))
                for (int n = 1; ; n++)
                {
                    id = $"{kind.TrimEnd('s')}-{n}";
                    if (Layers.PointEntry(def, kind, id) == null) break;
                }
            if (Layers.PointEntry(def, kind, id) != null)
            {
                Core.Log.Warning($"point ({why}): '{b.Id}' has a {kind} '{id}' already");
                Panel.Note($"{KindLabels[kind]} {id} gibt es schon.");
                return false;
            }
            var p = ToLocal(b, kind, at, yaw);
            var side = ToLocal(b, kind, at + Quaternion.Euler(0f, yaw, 0f) * Vector3.right * 3f, yaw);
            var e = new JsonObject { ["id"] = id };
            switch (kind)
            {
                case Layers.LocationsKind:
                    e["at"] = PtJson(p);
                    break;
                case Layers.DealsKind:
                    e["name"] = id;
                    e["at"] = PtJson(p);
                    if (def.Regions.Count > 0) e["region"] = def.Regions[0].Name;
                    break;
                case Layers.LotsKind:
                    e["name"] = id;
                    e["entry"] = PtJson(p);
                    e["spots"] = new JsonArray(PtJson(side));
                    break;
                case Layers.BuildingsKind:
                    e["name"] = id;
                    e["doors"] = new JsonArray(PtJson(p));
                    break;
                case Layers.RegionsKind:
                    float x = p.Position.x, z = p.Position.z, h = 10f;
                    e["polygon"] = new JsonArray(new JsonArray(x - h, z - h), new JsonArray(x + h, z - h), new JsonArray(x + h, z + h), new JsonArray(x - h, z + h));
                    e["unlocked"] = true;
                    e["standIn"] = 0;
                    break;
            }
            return Edits.Run(new PointEdit
            {
                Expansion = b.Id, Kind = kind, Id = id, Before = null, After = e,
                LayerBefore = false, RemovedBefore = false, LayerAfter = true, RemovedAfter = false,
                What = $"{KindLabels[kind]} {id} angelegt",
            }, why);
        }

        /// <summary>Adds a corner, spot or door next to the selected one.</summary>
        internal static bool AddSub(string why)
        {
            var r = Current;
            string name = r == null ? "" : r.Kind switch
            {
                Layers.RegionsKind => "polygon",
                Layers.LotsKind => "spots",
                Layers.BuildingsKind => "doors",
                _ => null,
            };
            if (r == null || name == null)
            {
                Core.Log.Warning($"point ({why}): {(r == null ? "nothing is selected" : r.Kind + " has one point only")}; nothing added");
                Panel.Note("Hier gibt es nichts hinzuzufügen.");
                return false;
            }
            int at = 0;
            return Change(r, e =>
            {
                if (e[name] is not JsonArray a || a.Count == 0) return false;
                int i = r.Kind == Layers.LotsKind ? Math.Max(0, r.Sub - 1) : Mathf.Clamp(r.Sub, 0, a.Count - 1);
                var copy = JsonNode.Parse(a[i].ToJsonString());
                if (r.Kind == Layers.RegionsKind)
                {
                    // Halfway to the next corner, so the outline keeps its shape.
                    var next = a[(i + 1) % a.Count];
                    copy = new JsonArray((a[i][0].GetValue<float>() + next[0].GetValue<float>()) / 2f, (a[i][1].GetValue<float>() + next[1].GetValue<float>()) / 2f);
                }
                else
                {
                    var pos = copy["pos"] as JsonArray;
                    pos[0] = pos[0].GetValue<float>() + 3f;
                }
                a.Insert(i + 1, copy);
                at = r.Kind == Layers.LotsKind ? i + 2 : i + 1;
                return true;
            }, $"{Label(r)}: {SubLabel(r.Kind, 1).Split(' ')[0]} hinzugefügt", why, () => at);
        }

        /// <summary>Takes the selected corner, spot or door out; a region keeps three corners, a building one door.</summary>
        internal static bool RemoveSub(string why)
        {
            var r = Current;
            if (r == null)
            {
                Core.Log.Warning($"point ({why}): nothing is selected; nothing taken out");
                return false;
            }
            (string name, int index, int min) = r.Kind switch
            {
                Layers.RegionsKind => ("polygon", r.Sub, 3),
                Layers.LotsKind => ("spots", r.Sub - 1, 0),
                Layers.BuildingsKind => ("doors", r.Sub, 1),
                _ => (null, -1, 0),
            };
            if (name == null || index < 0)
            {
                Core.Log.Warning($"point ({why}): {r} is the main point of its entry; delete the entry instead");
                Panel.Note("Das ist der Hauptpunkt. Ganzen Eintrag löschen.");
                return false;
            }
            return Change(r, e =>
            {
                if (e[name] is not JsonArray a || index >= a.Count) return false;
                if (a.Count <= min)
                {
                    Core.Log.Warning($"point ({why}): {r.EntryKey} needs at least {min} of them; nothing taken out");
                    Panel.Note($"Mindestens {min} nötig.");
                    return false;
                }
                a.RemoveAt(index);
                return true;
            }, $"{Label(r)}: {SubLabel(r.Kind, r.Sub)} entfernt", why, () => Math.Max(0, r.Sub - 1));
        }

        /// <summary>Takes the selected entry out. A code entry is written as a removal, a layer entry just goes.</summary>
        internal static bool Delete(string why)
        {
            var r = Current;
            var b = r?.Built;
            if (b == null)
            {
                Core.Log.Warning($"point ({why}): nothing is selected; nothing deleted");
                return false;
            }
            if (r.Kind == Layers.ShopsKind)
            {
                Core.Log.Warning($"point ({why}): shop '{r.Id}' comes from the code and cannot be deleted here");
                Panel.Note("Läden kommen aus dem Code.");
                return false;
            }
            var before = Layers.PointEntry(b.Definition, r.Kind, r.Id);
            var state = Layers.StateOf(r.Expansion);
            bool code = Layers.IsCodePoint(r.Expansion, r.Kind, r.Id);
            return Edits.Run(new PointEdit
            {
                Expansion = r.Expansion, Kind = r.Kind, Id = r.Id, Before = before, After = null,
                LayerBefore = state.Points.Contains((r.Kind, r.Id)), RemovedBefore = false, LayerAfter = false, RemovedAfter = code,
                SubBefore = r.Sub, What = $"{Label(r)} gelöscht",
            }, why);
        }

        /// <summary>Changes one field of the selected entry. Numbers and yes/no are read from the text.</summary>
        internal static bool SetField(string field, string value, string why)
        {
            var r = Current;
            value = (value ?? "").Trim();
            string[] allowed = r?.Kind switch
            {
                Layers.DealsKind => new[] { "name", "description", "region", "yaw" },
                Layers.LotsKind => new[] { "name", "yaw" },
                Layers.BuildingsKind => new[] { "name", "knock", "yaw" },
                Layers.RegionsKind => new[] { "unlocked", "standIn" },
                Layers.ShopsKind => new[] { "seller", "choiceText", "counterReach", "yaw" },
                Layers.LocationsKind => new[] { "yaw" },
                _ => System.Array.Empty<string>(),
            };
            if (r == null || !allowed.Contains(field))
            {
                Core.Log.Warning($"point ({why}): {(r == null ? "nothing is selected" : $"{r.Kind} has no field '{field}' (it has {string.Join(", ", allowed)})")}");
                return false;
            }
            if (field == "yaw")
            {
                if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float yaw) || !WorldOf(r, out var w, out _))
                {
                    Core.Log.Warning($"point ({why}): '{value}' is no angle");
                    return false;
                }
                // The field is the local facing; MoveTo takes the world facing.
                var b = r.Built;
                float baseYaw = InFrame(r.Kind) ? b.Frame.Rotation.eulerAngles.y : b.Root.transform.eulerAngles.y;
                return MoveTo(r, w, baseYaw + yaw, $"{Label(r)}: Richtung {yaw:0}", why);
            }
            return Change(r, e =>
            {
                switch (field)
                {
                    case "knock":
                    case "unlocked":
                        e[field] = value is "1" or "true" or "ja" or "an";
                        return true;
                    case "standIn":
                    case "counterReach":
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n))
                        {
                            Core.Log.Warning($"point ({why}): '{value}' is no number for {field}");
                            return false;
                        }
                        e[field] = field == "standIn" ? (JsonNode)(int)n : n;
                        return true;
                    default:
                        if (value.Length == 0 && field is "name" or "seller")
                        {
                            Core.Log.Warning($"point ({why}): {field} cannot be empty");
                            return false;
                        }
                        e[field] = value;
                        return true;
                }
            }, $"{Label(r)}: {field} {value}", why);
        }

        /// <summary>Screen points of the markers, for scripted clicks and drags.</summary>
        internal static string Describe(Camera cam)
        {
            if (cam == null) return "no camera";
            var parts = new List<string>();
            for (int i = 0; i < _used; i++)
            {
                var m = Markers[i];
                var s = cam.WorldToScreenPoint(m.World + Vector3.up * 0.8f * m.Root.transform.localScale.y);
                parts.Add($"{m.Ref} {(s.z > 0f ? $"{s.x:0} {s.y:0}" : "behind")}");
            }
            return parts.Count == 0 ? "no point markers" : string.Join("; ", parts);
        }
    }
}
