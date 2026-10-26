using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.UI.Phone.Map;
using Il2CppTMPro;
using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Puts the expansions on the phone map. The map's picture becomes one square holding the main map and every
    /// expansion's sheet, with the main map's sea between them, and the map's frame moves to the middle of that
    /// square at the main map's scale. The game's points of interest, its focus on the player and everything that
    /// reads the frame and the picture (Yesla's map) land on the larger map without knowing about it. The main map
    /// and each sheet are laid over the picture at their own resolution, so the map is as sharp as before.
    /// </summary>
    internal static class PhoneMap
    {
        /// <summary>Largest side of the combined picture, in pixels.</summary>
        private const int MaxPicture = 3072;

        /// <summary>Sea around the outermost sheets, in metres.</summary>
        private const float Margin = 40f;

        private sealed class Sheet
        {
            public string Name;
            public Sprite Sprite;
            /// <summary>World x/z of the middle of the picture.</summary>
            public Vector2 Centre;
            public float Half;
            /// <summary>World x/z direction of the picture's top edge.</summary>
            public Vector2 Up;
        }

        /// <summary>Font size of an expansion's name on the map, in map units, when the main map has no name to copy.</summary>
        private const float LabelSize = 60f;

        /// <summary>Milliseconds per frame spent sampling the ground between the sheets.</summary>
        private const double FillBudgetMs = 4.0;

        /// <summary>The map as worked out before the ground between the sheets is sampled.</summary>
        private sealed class Plan
        {
            public List<BuiltExpansion> Built;
            public List<Sheet> Sheets;
            public Vector3 Origin;
            public float Scale, Side;
            public Vector2 Centre;
            public int Pixels;
            public MapFill Fill;
            /// <summary>The main map's sea, once read; the fill's picture is worked out after it.</summary>
            public Color? Sea;
        }

        private static bool _applied;
        private static Plan _plan;
        private static Texture2D _picture;
        private static Sprite _pictureSprite;
        private static readonly List<Object> Owned = new();

        public static void Tick()
        {
            if (_applied) return;
            var built = BuildPipeline.All.Where(b => b.Root != null && b.Definition.Map != null).ToList();
            if (built.Count == 0) return;
            if (!PlayerSingleton<MapApp>.InstanceExists || !Singleton<MapPositionUtility>.InstanceExists) return;
            try
            {
                var app = PlayerSingleton<MapApp>.Instance;
                _plan ??= Prepare(app, Singleton<MapPositionUtility>.Instance, built);
                if (_plan == null)
                {
                    _applied = true;
                    return;
                }
                if (!_plan.Fill.Step(FillBudgetMs)) return;
                if (_plan.Sea == null)
                {
                    _plan.Sea = SeaOf(_plan);
                    _plan.Fill.BeginPicture(_plan.Sea.Value);
                    return;
                }
                if (!_plan.Fill.PictureReady) return;
                _applied = true;
                Apply(app, Singleton<MapPositionUtility>.Instance, _plan);
            }
            catch (Exception e)
            {
                _applied = true;
                Core.Log.Error($"map: putting the expansions on the phone map failed; the map may show the main world only: {e}");
            }
        }

        public static void Reset()
        {
            _applied = false;
            _plan = null;
            foreach (var o in Owned)
                if (o != null) Object.Destroy(o);
            Owned.Clear();
            _picture = null;
            _pictureSprite = null;
        }

        /// <summary>The sheets and the square around them; null when there is nothing to put on the map.</summary>
        private static Plan Prepare(MapApp app, MapPositionUtility util, List<BuiltExpansion> built)
        {
            if (util.OriginPoint == null || util.EdgePoint == null)
            {
                Core.Log.Error("map: the map has no origin or edge point; the expansions are not on the map");
                return null;
            }
            if (app.MainMapSprite == null)
            {
                Core.Log.Error("map: the map app has no main map picture; the expansions are not on the map");
                return null;
            }

            var origin = util.OriginPoint.position;
            float half = Vector3.Distance(origin, util.EdgePoint.position);
            float scale = util.MapDimensions * 0.5f / half;
            var sheets = new List<Sheet>
            {
                new() { Name = "main", Sprite = app.MainMapSprite, Centre = new Vector2(origin.x, origin.z), Half = half, Up = Vector2.up },
            };
            foreach (var b in built)
            {
                var s = SheetOf(app, b);
                if (s != null) sheets.Add(s);
            }
            if (sheets.Count == 1)
            {
                Core.Log.Warning("map: no expansion sheet could be made; the phone map stays the main map");
                return null;
            }

            // The square around every sheet, centred on their middle.
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var s in sheets)
                foreach (var c in Corners(s))
                {
                    minX = Mathf.Min(minX, c.x);
                    maxX = Mathf.Max(maxX, c.x);
                    minZ = Mathf.Min(minZ, c.y);
                    maxZ = Mathf.Max(maxZ, c.y);
                }
            var centre = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            float side = Mathf.Max(maxX - minX, maxZ - minZ) + 2f * Margin;
            float density = sheets.Max(s => s.Sprite.rect.width / (2f * s.Half));
            int pixels = Mathf.Clamp(Mathf.CeilToInt(side * density), 1024, MaxPicture);

            // Cells a sheet covers by more than a cell are not sampled; the ones at its edge are, so the picture does
            // not fade into the sea under the sheet's border.
            bool Covered(Vector2 p)
            {
                foreach (var s in sheets)
                {
                    var d = p - s.Centre;
                    float inner = s.Half - 1.5f * MapFill.Cell;
                    if (Mathf.Abs(Vector2.Dot(d, Right(s))) < inner && Mathf.Abs(Vector2.Dot(d, s.Up)) < inner) return true;
                }
                return false;
            }
            var low = centre - Vector2.one * side * 0.5f;
            return new Plan
            {
                Built = built,
                Sheets = sheets,
                Origin = origin,
                Scale = scale,
                Side = side,
                Centre = centre,
                Pixels = pixels,
                Fill = new MapFill(low, side, Covered, built),
            };
        }

        private static void Apply(MapApp app, MapPositionUtility util, Plan plan)
        {
            var built = plan.Built;
            var sheets = plan.Sheets;
            var origin = plan.Origin;
            var centre = plan.Centre;
            float scale = plan.Scale, side = plan.Side;
            int pixels = plan.Pixels;
            if (!Render(plan)) return;

            // The frame: the same scale, centred on the square.
            float dimensions = side * scale;
            var shift = (new Vector2(origin.x, origin.z) - centre) * scale;
            util.OriginPoint.position = new Vector3(centre.x, origin.y, centre.y);
            util.EdgePoint.position = util.OriginPoint.position + new Vector3(side * 0.5f, 0f, 0f);
            util.MapDimensions = dimensions;
            util.Recalculate();

            float before = app.ContentRect.sizeDelta.x;
            app.ContentRect.sizeDelta = new Vector2(dimensions, dimensions);
            app.MainMapSprite = _pictureSprite;
            if (app.BackgroundImage != null) app.BackgroundImage.sprite = _pictureSprite;
            else Core.Log.Warning("map: the map app has no background image; only the overlays show the larger map");

            var parent = app.BackgroundImage != null ? app.BackgroundImage.transform : app.ContentRect.transform;
            int index = 0;
            foreach (var s in sheets) Overlay(parent, s, centre, scale, index++);
            int tunnelPieces = 0;
            foreach (var b in built)
                foreach (var conn in b.Connections)
                    tunnelPieces += OverlayTunnels(parent, conn.Curve, centre, scale, ref index);
            Core.Log.Msg($"map: {tunnelPieces} tunnel road pieces laid over the sheets");

            var labels = app.LabelGroup != null ? app.LabelGroup.GetComponent<RectTransform>() : null;
            if (labels != null) labels.anchoredPosition += shift;
            else Core.Log.Warning("map: the map app has no label group; the district names keep their place and no expansion name is written");
            int written = labels != null ? WriteLabels(labels, built, origin, scale) : 0;

            int pois = 0;
            foreach (var poi in Object.FindObjectsOfType<POI>(true))
            {
                if (poi == null) continue;
                poi.UpdatePosition();
                pois++;
            }

            try
            {
                WidenZoom(app, before > 0f ? before / dimensions : 1f);
            }
            catch (Exception e)
            {
                Core.Log.Warning($"map: the zoom out limit stays as it was; the whole map may not fit the phone: {e.Message}");
            }

            Core.Log.Msg($"map: {sheets.Count - 1} expansion sheet(s) on the phone map; the map covers {side:0} m around ({centre.x:0}, {centre.y:0}), " +
                         $"{dimensions:0} units at {scale:0.00} per metre, picture {pixels} px; district names moved by {shift}, {written} name(s) written, {pois} points of interest moved");
        }

        private static Sheet SheetOf(MapApp app, BuiltExpansion b)
        {
            var def = b.Definition.Map;
            if (def.HalfSize <= 0f)
            {
                Core.Log.Error($"map: '{b.Id}' map sheet has no size; it is not on the map");
                return null;
            }
            Sprite sprite;
            if (def.Picture == MapPictureSource.PrologueMap)
            {
                sprite = app.TutorialMapSprite;
                if (sprite == null)
                {
                    Core.Log.Error($"map: '{b.Id}' uses the prologue map, which the map app does not have; it is not on the map");
                    return null;
                }
            }
            else
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (def.Png == null || !ImageConversion.LoadImage(tex, def.Png, true))
                {
                    Object.Destroy(tex);
                    Core.Log.Error($"map: '{b.Id}' map PNG could not be read; it is not on the map");
                    return null;
                }
                Owned.Add(tex);
                sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                Owned.Add(sprite);
            }
            var up = b.Frame.Rotation * Vector3.forward;
            return new Sheet
            {
                Name = b.Id,
                Sprite = sprite,
                Centre = b.Frame.ToWorldXZ(def.Centre),
                Half = def.HalfSize,
                Up = new Vector2(up.x, up.z).normalized,
            };
        }

        private static Vector2 Right(Sheet s) => new(s.Up.y, -s.Up.x);

        /// <summary>World x/z corners in the order bottom-left, top-left, top-right, bottom-right of the picture.</summary>
        private static Vector2[] Corners(Sheet s)
        {
            var r = Right(s) * s.Half;
            var u = s.Up * s.Half;
            return new[] { s.Centre - r - u, s.Centre - r + u, s.Centre + r + u, s.Centre + r - u };
        }

        /// <summary>
        /// Draws the map into one picture: the main map's sea, the ground between the sheets, the connections' roads
        /// and every sheet over them.
        /// </summary>
        private static bool Render(Plan plan)
        {
            var sheets = plan.Sheets;
            float side = plan.Side;
            int pixels = plan.Pixels;
            var shader = Shader.Find("UI/Default");
            if (shader == null)
            {
                Core.Log.Error("map: the UI shader is missing; the expansions are not on the map");
                return false;
            }
            var mat = new Material(shader);
            var rt = RenderTexture.GetTemporary(pixels, pixels, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Texture2D ground = null, road = null;
            try
            {
                RenderTexture.active = rt;
                Vector2 low = plan.Centre - Vector2.one * side * 0.5f;
                Vector2 ToPixel(Vector2 w) => (w - low) / side * pixels;

                var sea = plan.Sea.Value;
                // The picture is sRGB and the clear colour is taken as linear; the sea read back from it is sRGB.
                GL.Clear(true, true, QualitySettings.activeColorSpace == ColorSpace.Linear ? sea.linear : sea);

                ground = plan.Fill.Picture();
                float covers = plan.Fill.Covers;
                Quad(mat, ground, new[] { low, low + new Vector2(0f, covers), low + new Vector2(covers, covers), low + new Vector2(covers, 0f) }, new Rect(0f, 0f, 1f, 1f), ToPixel);

                road = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "MapExpansions.MapRoad" };
                road.SetPixel(0, 0, MapFill.Road);
                road.Apply(false, false);
                int pieces = 0;
                foreach (var b in plan.Built)
                    foreach (var conn in b.Connections)
                        pieces += DrawRoute(mat, road, conn.Curve, ToPixel);

                foreach (var s in sheets) Draw(mat, s, ToPixel);
                Core.Log.Msg($"map: ground between the sheets drawn from {ground.width}x{ground.height} cells, {pieces} road pieces of the connections");

                var picture = new Texture2D(pixels, pixels, TextureFormat.RGB24, false) { name = "MapExpansions.PhoneMap", wrapMode = TextureWrapMode.Clamp };
                picture.ReadPixels(new Rect(0, 0, pixels, pixels), 0, 0);
                picture.Apply(false, true);
                _picture = picture;
                Owned.Add(picture);
                _pictureSprite = Sprite.Create(picture, new Rect(0, 0, pixels, pixels), new Vector2(0.5f, 0.5f));
                _pictureSprite.name = "MapExpansions.PhoneMap";
                Owned.Add(_pictureSprite);
                Core.Log.Msg($"map: picture drawn, {pixels} px, sea {sea}");
                return true;
            }
            catch (Exception e)
            {
                Core.Log.Error($"map: drawing the map picture failed; the expansions are not on the map: {e}");
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(mat);
                if (ground != null) Object.Destroy(ground);
                if (road != null) Object.Destroy(road);
            }
        }

        /// <summary>The sea: the colour in the corner of the main map, read back from a small picture of it.</summary>
        private static Color SeaOf(Plan plan)
        {
            var shader = Shader.Find("UI/Default");
            if (shader == null) throw new InvalidOperationException("the UI shader is missing");
            var mat = new Material(shader);
            var sheet = plan.Sheets[0];
            const int size = 64;
            var rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);
                var low = sheet.Centre - Vector2.one * sheet.Half;
                Vector2 ToPixel(Vector2 w) => (w - low) / (2f * sheet.Half) * size;
                Draw(mat, sheet, ToPixel);
                var seaAt = ToPixel(Corners(sheet)[0]) + new Vector2(1f, 1f);
                var probe = new Texture2D(1, 1, TextureFormat.RGB24, false);
                probe.ReadPixels(new Rect(Mathf.Floor(seaAt.x), Mathf.Floor(seaAt.y), 1, 1), 0, 0);
                probe.Apply();
                var sea = probe.GetPixel(0, 0);
                Object.Destroy(probe);
                Core.Log.Msg($"map: the main map's sea is {sea}");
                return sea;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(mat);
            }
        }

        private static void Draw(Material mat, Sheet s, Func<Vector2, Vector2> toPixel)
        {
            var tex = s.Sprite.texture;
            var r = s.Sprite.textureRect;
            Quad(mat, tex, Corners(s), new Rect(r.xMin / tex.width, r.yMin / tex.height, r.width / tex.width, r.height / tex.height), toPixel);
        }

        /// <summary>A texture's uv rectangle on world x/z corners in the order bottom-left, top-left, top-right, bottom-right.</summary>
        private static void Quad(Material mat, Texture tex, Vector2[] c, Rect uv, Func<Vector2, Vector2> toPixel)
        {
            mat.mainTexture = tex;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, RenderTexture.active.width, 0f, RenderTexture.active.height);
            mat.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            Vertex(toPixel(c[0]), uv.xMin, uv.yMin);
            Vertex(toPixel(c[1]), uv.xMin, uv.yMax);
            Vertex(toPixel(c[2]), uv.xMax, uv.yMax);
            Vertex(toPixel(c[3]), uv.xMax, uv.yMin);
            GL.End();
            GL.PopMatrix();
        }

        /// <summary>
        /// A connection's road at its width in the highway's colour, tunnels included. Drawn under the
        /// sheets, so only the stretch between them shows. Returns the pieces drawn.
        /// </summary>
        private static int DrawRoute(Material mat, Texture road, Connections.RouteCurve curve, Func<Vector2, Vector2> toPixel)
        {
            int pieces = 0;
            var unit = new Rect(0f, 0f, 1f, 1f);
            for (int i = 0; i + 1 < curve.Count; i++)
            {
                var a = new Vector2(curve.Points[i].x, curve.Points[i].z);
                var b = new Vector2(curve.Points[i + 1].x, curve.Points[i + 1].z);
                float length = Vector2.Distance(a, b);
                if (length < 0.01f) continue;
                var dir = (b - a) / length;
                float width = i < curve.RoadWidth.Count ? curve.RoadWidth[i] : curve.MaxRoadWidth;
                var across = new Vector2(-dir.y, dir.x) * width * 0.5f;

                // Pieces reach half a width past their ends so the bends close.
                float reach = width * 0.5f;
                var p = a - dir * reach;
                var q = b + dir * reach;
                Quad(mat, road, new[] { p - across, p + across, q + across, q - across }, unit, toPixel);
                pieces++;
            }
            return pieces;
        }

        private static void Vertex(Vector2 p, float u, float v)
        {
            GL.TexCoord2(u, v);
            GL.Vertex3(p.x, p.y, 0f);
        }

        /// <summary>A sheet at its own resolution over the combined picture, below the district names.</summary>
        private static void Overlay(Transform parent, Sheet s, Vector2 centre, float scale, int index)
        {
            var go = new GameObject("MapExpansions.Sheet." + s.Name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.SetSiblingIndex(index);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.one * (2f * s.Half * scale);
            rt.anchoredPosition = (s.Centre - centre) * scale;
            rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(s.Up.y, s.Up.x) * Mathf.Rad2Deg - 90f);
            var image = go.AddComponent<Image>();
            image.sprite = s.Sprite;
            image.raycastTarget = false;
        }

        /// <summary>
        /// A connection's tunnels as road over the sheets. A sheet is a picture from above, so it shows rock where the
        /// road runs underground and would cut the road off at the tunnel mouth. Returns the pieces laid.
        /// </summary>
        private static int OverlayTunnels(Transform parent, Connections.RouteCurve curve, Vector2 centre, float scale, ref int index)
        {
            int pieces = 0;
            for (int i = 0; i + 1 < curve.Count; i++)
            {
                if (i >= curve.Style.Count || curve.Style[i] != RouteStyle.Tunnel) continue;
                var a = new Vector2(curve.Points[i].x, curve.Points[i].z);
                var b = new Vector2(curve.Points[i + 1].x, curve.Points[i + 1].z);
                float length = Vector2.Distance(a, b);
                if (length < 0.01f) continue;
                float width = i < curve.RoadWidth.Count ? curve.RoadWidth[i] : curve.MaxRoadWidth;
                var dir = (b - a) / length;

                var go = new GameObject("MapExpansions.Tunnel");
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(parent, false);
                rt.SetSiblingIndex(index++);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                // Pieces reach half a width past their ends so the bends close.
                rt.sizeDelta = new Vector2(length + width, width) * scale;
                rt.anchoredPosition = ((a + b) * 0.5f - centre) * scale;
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
                var image = go.AddComponent<Image>();
                image.color = MapFill.Road;
                image.raycastTarget = false;
                pieces++;
            }
            return pieces;
        }

        /// <summary>
        /// The map zooms out as far as before in screen size: the larger map fits the phone where the main map did. Zooming
        /// in stops where it did, at the same metres per pixel.
        /// </summary>
        private static void WidenZoom(MapApp app, float factor)
        {
            var scroll = PinchScroll(app);
            if (scroll == null)
            {
                Core.Log.Warning("map: the map app has no pinchable scroll view; it cannot zoom out over the larger map");
                return;
            }
            var lower = ReadScale(scroll, "lowerScale");
            WriteScale(scroll, "lowerScale", lower * factor);
            Core.Log.Msg($"map: zoom out limit {lower} -> {ReadScale(scroll, "lowerScale")}, zoom in limit {ReadScale(scroll, "upperScale")} kept");
        }

        /// <summary>
        /// The map's scroll view. Its interop type cannot be used as a type argument here (the cast throws a
        /// VerificationException), so it is found by its class name and its fields are read by name.
        /// </summary>
        private static Component PinchScroll(MapApp app)
        {
            for (var t = app.ContentRect.transform; t != null; t = t.parent)
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && c.GetIl2CppType().Name == "PinchableScrollRect") return c;
            return null;
        }

        private static unsafe Vector3 ReadScale(Component scroll, string field) => *(Vector3*)ScaleField(scroll, field);

        private static unsafe void WriteScale(Component scroll, string field, Vector3 value) => *(Vector3*)ScaleField(scroll, field) = value;

        private static IntPtr ScaleField(Component scroll, string name)
        {
            var cls = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(scroll.Pointer);
            var field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(cls, name);
            if (field == IntPtr.Zero) throw new MissingFieldException("PinchableScrollRect", name);
            return scroll.Pointer + (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
        }

        /// <summary>The map's frame, zoom and the player's pin, for the dev console.</summary>
        public static string Describe()
        {
            if (!PlayerSingleton<MapApp>.InstanceExists) return "map: no map app";
            var app = PlayerSingleton<MapApp>.Instance;
            var c = app.ContentRect;
            var sb = new System.Text.StringBuilder();
            sb.Append($"map: applied {_applied}; content size {c.sizeDelta}, scale {c.localScale.x:0.000}, position {c.anchoredPosition}, pivot {c.pivot}");
            if (Singleton<MapPositionUtility>.InstanceExists)
            {
                var u = Singleton<MapPositionUtility>.Instance;
                sb.Append($"; frame origin {u.OriginPoint.position}, edge {u.EdgePoint.position}, {u.MapDimensions:0} units");
            }
            if (app.BackgroundImage != null)
            {
                var b = app.BackgroundImage.rectTransform;
                sb.Append($"; background anchors {b.anchorMin}-{b.anchorMax}, size {b.sizeDelta}, rect {b.rect.size}, sprite '{(app.BackgroundImage.sprite != null ? app.BackgroundImage.sprite.name : "-")}'");
                for (int i = 0; i < b.childCount; i++)
                {
                    var ch = b.GetChild(i).GetComponent<RectTransform>();
                    if (ch != null) sb.Append($"; child '{ch.name}' at {ch.anchoredPosition} size {ch.sizeDelta} rot {ch.localEulerAngles.z:0}");
                }
            }
            var scroll = PinchScroll(app);
            if (scroll != null) sb.Append($"; zoom {ReadScale(scroll, "lowerScale")}..{ReadScale(scroll, "upperScale")}");
            var me = Il2CppScheduleOne.PlayerScripts.Player.Local;
            if (me != null && me.PoI != null && me.PoI.UI != null)
                sb.Append($"; player at {me.transform.position}, pin at {me.PoI.UI.anchoredPosition}, map position {Singleton<MapPositionUtility>.Instance.GetMapPosition(me.transform.position)}");
            return sb.ToString();
        }

        /// <summary>Centres the map on a world point at a zoom, as the game centres it on the player.</summary>
        public static void Focus(Vector3 world, float zoom)
        {
            var app = PlayerSingleton<MapApp>.Instance;
            app.FocusPosition(Singleton<MapPositionUtility>.Instance.GetMapPosition(world));
            app.ContentRect.anchoredPosition *= zoom / app.ContentRect.localScale.x;
            app.ContentRect.localScale = Vector3.one * zoom;
            Core.Log.Msg($"map: centred on {world} at zoom {zoom}");
        }

        /// <summary>
        /// Writes the expansions' names as copies of the main map's own names: a building name like "Gas-Mart", a district
        /// name like "Canal". They sit in the map's label group, so they fade in with the main map's names as the player
        /// zooms in.
        /// </summary>
        private static int WriteLabels(RectTransform labels, List<BuiltExpansion> built, Vector3 oldOrigin, float scale)
        {
            Text building = null, district = null;
            foreach (var t in labels.GetComponentsInChildren<Text>(true))
            {
                if (building == null && t.alignment == TextAnchor.MiddleCenter) building = t;
                if (district == null && t.alignment == TextAnchor.UpperCenter) district = t;
            }
            district ??= building;
            if (building == null)
            {
                Core.Log.Warning("map: the main map has no names to copy; expansion names are written in the map's pin font");
                return WriteOwnLabels(labels, built, oldOrigin, scale);
            }
            Core.Log.Msg($"map: expansion names copy '{building.text.Replace("\n", " ")}' for buildings and '{district.text.Replace("\n", " ")}' for districts");

            int written = 0;
            foreach (var b in built)
                foreach (var l in b.Definition.Map.Labels)
                {
                    var template = l.Style == MapLabelStyle.District ? district : building;
                    var parent = template.rectTransform.parent != null ? template.rectTransform.parent.TryCast<RectTransform>() ?? labels : labels;
                    var copy = Object.Instantiate(template.gameObject, parent, false);
                    copy.name = "MapExpansions.Label." + l.Text.Replace("\n", " ");
                    copy.SetActive(true);
                    var text = copy.GetComponent<Text>();
                    text.text = l.Text;
                    if (l.Color.HasValue) text.color = l.Color.Value;
                    var w = b.Frame.ToWorldXZ(l.Position);
                    // The names are placed in the label group's units from the main map's old origin; the copy's parent
                    // may sit off the group's centre.
                    var inGroup = (w - new Vector2(oldOrigin.x, oldOrigin.z)) * scale;
                    var offset = parent == labels ? Vector2.zero : (Vector2)labels.InverseTransformPoint(parent.position);
                    copy.GetComponent<RectTransform>().anchoredPosition = inGroup - offset;
                    written++;
                    Core.Log.Msg($"map: '{b.Id}' {l.Style} name '{l.Text.Replace("\n", " ")}' written at world ({w.x:0}, {w.y:0})");
                }
            return written;
        }

        /// <summary>Names in the map's pin font, for a main map without names of its own to copy.</summary>
        private static int WriteOwnLabels(RectTransform labels, List<BuiltExpansion> built, Vector3 oldOrigin, float scale)
        {
            var template = labels.GetComponentInChildren<TMP_Text>(true);
            var font = template != null ? template.font : null;
            if (font == null)
            {
                // The main map's district names are part of its picture; the names are written in the font of the map's pins.
                var pin = labels.parent != null ? labels.parent.parent : null;
                var any = pin != null ? pin.GetComponentInChildren<TMP_Text>(true) : null;
                font = any != null ? any.font : TMP_Settings.defaultFontAsset;
                Core.Log.Msg($"map: the main map has no district name to copy; expansion names are written in '{(font != null ? font.name : "no font")}'");
            }
            int written = 0;
            foreach (var b in built)
                foreach (var l in b.Definition.Map.Labels)
                {
                    GameObject copy;
                    TMP_Text text;
                    if (template != null)
                    {
                        copy = Object.Instantiate(template.gameObject, labels, false);
                        text = copy.GetComponent<TMP_Text>();
                    }
                    else
                    {
                        copy = new GameObject("label", Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>());
                        copy.transform.SetParent(labels, false);
                        var made = copy.AddComponent<TextMeshProUGUI>();
                        if (font != null) made.font = font;
                        made.fontSize = LabelSize;
                        made.fontStyle = FontStyles.Bold;
                        made.alignment = TextAlignmentOptions.Center;
                        made.color = new Color(1f, 1f, 1f, 0.85f);
                        made.raycastTarget = false;
                        made.enableWordWrapping = false;
                        copy.GetComponent<RectTransform>().sizeDelta = new Vector2(LabelSize * 12f, LabelSize * 2f);
                        text = made;
                    }
                    copy.name = "MapExpansions.Label." + l.Text;
                    text.text = l.Text;
                    var w = b.Frame.ToWorldXZ(l.Position);
                    var rt = copy.GetComponent<RectTransform>();
                    rt.anchoredPosition = (w - new Vector2(oldOrigin.x, oldOrigin.z)) * scale;
                    written++;
                    Core.Log.Msg($"map: '{b.Id}' name '{l.Text}' written at world ({w.x:0}, {w.y:0})");
                }
            return written;
        }
    }
}
