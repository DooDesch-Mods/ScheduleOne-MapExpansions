using System.Globalization;
using System.Text.Json.Nodes;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using Sideload.Api;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The docks: one Sideload page over the whole screen on its own overlay canvas. The middle of the page is empty
    /// and takes no pointer, so clicks there reach the world; whether the cursor is over a dock is asked of the
    /// event system, which knows the page's hit targets.
    /// </summary>
    internal static class Panel
    {
        internal const string SurfaceId = "mapexpansions-editor";
        private const string Bundle = "MapExpansions.Editor.Assets.editor";

        private static GameObject _root;
        private static RectTransform _frame;
        private static SurfaceHandle _surface;
        private static string _pushed = "";
        private static float _sincePush;
        private static bool _warnedNoEventSystem;

        private static readonly List<string> Lines = new();
        private static string _materialQuery = "";

        internal static bool Mounted => _root != null;

        internal static void Mount()
        {
            if (_root != null) return;
            if (!Surfaces.Available)
            {
                Core.Log.Warning("panel: Sideload cannot render surfaces here; the editor runs without docks");
                return;
            }
            _root = new GameObject("MapExpansionsEditor_Panel");
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            var page = new GameObject("page");
            var rect = page.AddComponent<RectTransform>();
            rect.SetParent(_root.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;

            _surface = Surfaces.Mount(rect, SurfaceId, Bundle)
                .OnCall("editor.state", _ => State())
                .OnCall("editor.select", key =>
                {
                    var p = Copies.Find(key);
                    bool add = EditorInput.Key(KeyCode.LeftControl) || EditorInput.Key(KeyCode.RightControl);
                    if (p == null) Core.Log.Warning($"panel: select '{key}' names no placed copy");
                    else Selection.Select(p, add ? "hierarchy ctrl+click" : "hierarchy", add);
                    return State();
                })
                .OnCall("editor.mode", m =>
                {
                    if (Enum.TryParse(m, true, out GizmoMode mode)) Gizmo.SetMode(mode, "toolbar");
                    else Core.Log.Warning($"panel: mode '{m}' is not Move, Rotate or Scale");
                    return State();
                })
                .OnCall("editor.snap", which =>
                {
                    switch (which)
                    {
                        case "grid": Snap.Grid = !Snap.Grid; break;
                        case "turn": Snap.Turn = !Snap.Turn; break;
                        case "edges": Snap.Edges = !Snap.Edges; break;
                        default:
                            Core.Log.Warning($"panel: snap '{which}' is not grid, turn or edges");
                            return State();
                    }
                    Core.Log.Msg($"snap (toolbar): grid {Snap.Grid}, turn {Snap.Turn}, edges {Snap.Edges}");
                    return State();
                })
                .OnCall("editor.undo", _ => { Edits.Undo("toolbar"); return State(); })
                .OnCall("editor.redo", _ => { Edits.Redo("toolbar"); return State(); })
                .OnCall("editor.dup", _ => { Edits.Duplicate(Selection.All, "toolbar"); return State(); })
                .OnCall("editor.del", _ => { Edits.Delete(Selection.All, "toolbar"); return State(); })
                .OnCall("editor.drop", _ => { Edits.Drop(Selection.All, "toolbar"); return State(); })
                .OnCall("editor.frame", _ => { EditorMode.FrameSelection("toolbar"); return State(); })
                .OnCall("editor.play", _ => { EditorMode.Play("toolbar"); return ""; })
                .OnCall("editor.save", _ => { Saving.SaveAll("toolbar"); return State(); })
                .OnCall("editor.close", _ => { EditorMode.Close("toolbar"); return ""; })
                .OnCall("editor.area", key =>
                {
                    var a = AreaRef.Parse(key ?? "");
                    if (a == null) Core.Log.Warning($"panel: area '{key}' is no rule, opening or box key");
                    else Areas.Select(a, "hierarchy");
                    return State();
                })
                .OnCall("editor.rule.new", e =>
                {
                    if (Enum.TryParse(e, true, out MapExpansions.ObjectEffect effect)) AreaEdits.NewRule(effect, "toolbar");
                    else Core.Log.Warning($"panel: '{e}' is no rule effect");
                    return State();
                })
                .OnCall("editor.box.new", k =>
                {
                    if (Enum.TryParse(k, true, out MapExpansions.BoxKind kind)) AreaEdits.NewBox(kind, "toolbar");
                    else Core.Log.Warning($"panel: '{k}' is neither Panel nor Block");
                    return State();
                })
                .OnCall("editor.opening.new", _ => { AreaEdits.NewOpening("toolbar"); return State(); })
                .OnCall("editor.apply", _ => { AreaEdits.Apply("toolbar"); return State(); })
                .OnCall("editor.rule.effect", e =>
                {
                    if (Enum.TryParse(e, true, out MapExpansions.ObjectEffect effect))
                        AreaEdits.ChangeRule(Areas.Current, r => r.Effect = effect, $"Wirkung {effect}", "inspector");
                    else Core.Log.Warning($"panel: '{e}' is no rule effect");
                    return State();
                })
                .OnCall("editor.rule.name", text =>
                {
                    var t = (text ?? "").Trim();
                    if (t.Length == 0) Core.Log.Warning("panel: an empty name matches every object; the name stays");
                    else AreaEdits.ChangeRule(Areas.Current, r => r.Name = t, $"Name {t}", "inspector");
                    return State();
                })
                .OnCall("editor.rule.parent", text =>
                {
                    var t = (text ?? "").Trim();
                    AreaEdits.ChangeRule(Areas.Current, r => r.Parent = t.Length == 0 ? null : t, t.Length == 0 ? "ohne Eltern" : $"Eltern {t}", "inspector");
                    return State();
                })
                .OnCall("editor.box.material", name =>
                {
                    if (ObjectRules.LoadedMaterial(name ?? "") == null) Core.Log.Warning($"panel: no loaded material '{name}'; the panel keeps its material");
                    else AreaEdits.ChangeBox(Areas.Current, x => x.Material = name, $"Material {name}", "inspector");
                    return State();
                })
                .OnCall("editor.materials", q =>
                {
                    _materialQuery = q ?? "";
                    Core.Log.Msg($"panel: material search '{_materialQuery}'");
                    return State();
                });

            // The selection frame: an outline over the page that takes no pointer.
            var frame = new GameObject("frame");
            _frame = frame.AddComponent<RectTransform>();
            _frame.SetParent(_root.transform, false);
            _frame.anchorMin = _frame.anchorMax = Vector2.zero;
            _frame.pivot = Vector2.zero;
            var fill = frame.AddComponent<Image>();
            fill.color = new Color(1f, 0.75f, 0.1f, 0.12f);
            fill.raycastTarget = false;
            var edge = frame.AddComponent<Outline>();
            edge.effectColor = new Color(1f, 0.75f, 0.1f, 0.9f);
            edge.effectDistance = new Vector2(1.5f, 1.5f);
            frame.SetActive(false);
            _pushed = "";
            Core.Log.Msg($"panel: surface '{SurfaceId}' mounted: {Surfaces.IsMounted(SurfaceId)}");
        }

        internal static void Unmount()
        {
            if (_root == null) return;
            Surfaces.Unmount(SurfaceId);
            Object.Destroy(_root);
            _root = null;
            _frame = null;
            _surface = null;
            Core.Log.Msg("panel: surface unmounted");
        }

        /// <summary>Every UI object the event system finds under a screen point, nearest first.</summary>
        internal static List<GameObject> HitsAt(Vector2 screen)
        {
            var hits = new List<GameObject>();
            var es = EventSystem.current;
            if (es == null)
            {
                if (!_warnedNoEventSystem) Core.Log.Warning("panel: there is no event system; every click goes to the world");
                _warnedNoEventSystem = true;
                return hits;
            }
            var data = new PointerEventData(es) { position = screen };
            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(data, results);
            for (int i = 0; i < results.Count; i++)
                if (results[i].gameObject != null) hits.Add(results[i].gameObject);
            return hits;
        }

        internal static bool IsPointerOver(Vector2 screen)
        {
            if (_root == null) return false;
            foreach (var go in HitsAt(screen))
                if (go.transform.IsChildOf(_root.transform)) return true;
            return false;
        }

        /// <summary>Shows the selection frame over a screen rectangle in pixels, or hides it.</summary>
        internal static void ShowFrame(Rect? screen)
        {
            if (_frame == null) return;
            if (screen == null)
            {
                if (_frame.gameObject.activeSelf) _frame.gameObject.SetActive(false);
                return;
            }
            float k = _root.GetComponent<Canvas>().scaleFactor;
            if (k <= 0f) k = 1f;
            var r = screen.Value;
            _frame.anchoredPosition = new Vector2(r.xMin / k, r.yMin / k);
            _frame.sizeDelta = new Vector2(r.width / k, r.height / k);
            _frame.SetAsLastSibling();
            if (!_frame.gameObject.activeSelf) _frame.gameObject.SetActive(true);
        }

        /// <summary>A line for the console dock.</summary>
        internal static void Note(string line)
        {
            Lines.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
            if (Lines.Count > 30) Lines.RemoveAt(0);
        }

        internal static void Tick()
        {
            if (_surface == null) return;
            _sincePush += Time.unscaledDeltaTime;
            if (_sincePush < 0.1f) return;
            _sincePush = 0f;
            var s = State();
            if (s == _pushed) return;
            _pushed = s;
            _surface.Emit("editor.changed", s);
        }

        private static JsonArray Vec(Vector3 v) => new(Math.Round(v.x, 2), Math.Round(v.y, 2), Math.Round(v.z, 2));

        internal static string State()
        {
            var sel = Selection.Current;
            var all = Selection.All;
            var keys = new JsonArray(all.Select(p => (JsonNode)Copies.Key(p)).ToArray());
            var copies = new JsonArray();
            foreach (var p in Copies.All())
                copies.Add(new JsonObject
                {
                    ["key"] = Copies.Key(p),
                    ["id"] = p.Built.Def.Id,
                    ["owner"] = p.LayerOwner,
                    ["expansion"] = p.Expansion.Id,
                    ["dirty"] = Saving.IsDirty(p),
                });
            JsonNode selected = null;
            if (sel?.Built?.Local != null)
            {
                var d = sel.Built.Def;
                selected = new JsonObject
                {
                    ["key"] = Copies.Key(sel),
                    ["id"] = d.Id,
                    ["owner"] = sel.LayerOwner,
                    ["expansion"] = sel.Expansion.Id,
                    ["template"] = d.Template,
                    ["onto"] = d.Onto,
                    ["position"] = Vec(sel.Built.Local.transform.position),
                    ["yaw"] = Math.Round(sel.Built.Local.transform.eulerAngles.y, 1),
                    ["scale"] = Math.Round(d.Scale, 3),
                    ["size"] = Vec(Selection.CurrentBounds.size),
                    ["dirty"] = Saving.IsDirty(sel),
                };
            }
            var rules = new JsonArray();
            var boxes = new JsonArray();
            foreach (var b in BuildPipeline.All)
            {
                var state = Layers.StateOf(b.Id);
                foreach (var r in b.Definition.Hide.Take(400))
                    rules.Add(new JsonObject
                    {
                        ["key"] = AreaRef.OfRule(b.Id, r.Key).Key,
                        ["id"] = r.Key,
                        ["expansion"] = b.Id,
                        ["effect"] = r.Effect.ToString(),
                        ["layer"] = state.Rules.Contains(r.Key),
                        ["pending"] = AreaEdits.IsPending(b.Id, r.Key),
                        ["stale"] = AreaEdits.IsStale(b.Id, r.Key),
                        ["openings"] = r.Effect == MapExpansions.ObjectEffect.Hollow ? r.Openings.Count : 0,
                    });
                foreach (var (owner, _, d) in Layers.AllCopies(b.Definition))
                    foreach (var x in d.Boxes)
                        boxes.Add(new JsonObject
                        {
                            ["key"] = AreaRef.OfBox(b.Id, owner, d.Id, x.Id).Key,
                            ["id"] = x.Id,
                            ["copy"] = d.Id,
                            ["expansion"] = b.Id,
                            ["kind"] = x.Kind.ToString(),
                        });
            }
            JsonNode area = null;
            var cur = Areas.Current;
            if (cur != null)
            {
                var o = new JsonObject { ["key"] = cur.Key, ["label"] = cur.Label, ["kind"] = cur.Kind.ToString() };
                if (cur.Get(out var local, out _))
                {
                    o["min"] = Vec(local.min);
                    o["max"] = Vec(local.max);
                }
                var r = cur.RuleDef;
                if (cur.Kind != AreaKind.Box && r != null)
                {
                    o["effect"] = r.Effect.ToString();
                    o["name"] = r.Name;
                    o["parent"] = r.Parent ?? "";
                    o["side"] = r.Side.ToString();
                    o["pending"] = AreaEdits.IsPending(cur.Expansion, r.Key);
                    o["stale"] = AreaEdits.IsStale(cur.Expansion, r.Key);
                }
                var box = cur.Kind == AreaKind.Box ? cur.BoxDef : null;
                if (box != null)
                {
                    o["boxKind"] = box.Kind.ToString();
                    o["copy"] = cur.Copy;
                    o["material"] = box.Material ?? "";
                    if (box.Kind == MapExpansions.BoxKind.Panel)
                    {
                        o["query"] = _materialQuery;
                        o["materials"] = new JsonArray(AreaEdits.Materials(_materialQuery, 40).Select(m => (JsonNode)new JsonObject
                        {
                            ["name"] = m.name,
                            ["color"] = "#" + ColorUtility.ToHtmlStringRGB(m.color),
                        }).ToArray());
                    }
                }
                area = o;
            }
            var root = new JsonObject
            {
                ["rules"] = rules,
                ["boxes"] = boxes,
                ["area"] = area,
                ["pending"] = AreaEdits.PendingCount,
                ["stale"] = AreaEdits.StaleCount,
                ["copies"] = copies,
                ["selected"] = selected,
                ["keys"] = keys,
                ["mode"] = Gizmo.Mode.ToString(),
                ["grid"] = Snap.Grid,
                ["turn"] = Snap.Turn,
                ["edges"] = Snap.Edges,
                ["undo"] = Edits.UndoCount,
                ["redo"] = Edits.RedoCount,
                ["dirty"] = Saving.DirtyCount,
                ["speed"] = Math.Round(FlyCamera.Speed, 1),
                ["log"] = new JsonArray(Lines.Select(l => (JsonNode)l).ToArray()),
            };
            return root.ToJsonString();
        }
    }
}
