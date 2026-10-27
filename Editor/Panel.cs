using System.Globalization;
using System.Text.Json.Nodes;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using Sideload.Api;
using Il2CppTMPro;
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
        private static RectTransform _readout;
        private static TextMeshProUGUI _readoutText;
        private static string _readoutShown;
        private static RectTransform _tip;
        private static TextMeshProUGUI _tipText;
        private static string _tipWanted;
        private static string _tipShown;

        /// <summary>True while the key overlay is open; F1, its button and Escape close it.</summary>
        internal static bool Help { get; private set; }

        internal static void ToggleHelp(string why)
        {
            Help = !Help;
            Core.Log.Msg($"panel ({why}): key overlay {(Help ? "open" : "closed")}");
        }
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
                .OnCall("editor.snap", which => { Snap.Toggle(which, "toolbar"); return State(); })
                .OnCall("editor.snap.step", wv =>
                {
                    int eq = (wv ?? "").IndexOf('=');
                    if (eq <= 0) Core.Log.Warning($"panel: snap step '{wv}' is not grid=value or turn=value");
                    else Snap.SetStep(wv.Substring(0, eq), wv.Substring(eq + 1), "toolbar");
                    return State();
                })
                .OnCall("editor.tip", text => { ShowTip(text); return ""; })
                .OnCall("editor.undo", _ => { Edits.Undo("toolbar"); return State(); })
                .OnCall("editor.redo", _ => { Edits.Redo("toolbar"); return State(); })
                .OnCall("editor.dup", _ => { Edits.Duplicate(Selection.All, "toolbar"); return State(); })
                .OnCall("editor.del", _ => { Edits.Delete(Selection.All, "toolbar"); return State(); })
                .OnCall("editor.obstacle", v =>
                {
                    Edits.Obstacle(Selection.All, v == "on" ? true : v == "off" ? false : null, "inspector");
                    return State();
                })
                .OnCall("editor.set", fv =>
                {
                    int eq = (fv ?? "").IndexOf('=');
                    if (eq <= 0 || !float.TryParse(fv.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                        Core.Log.Warning($"panel: '{fv}' is not field=number");
                    else Edits.SetValue(Selection.Current, fv.Substring(0, eq), v, "inspector");
                    return State();
                })
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
                .OnCall("editor.pick", t =>
                {
                    if (Enum.TryParse(t, true, out PickTool tool)) Picking.Begin(tool, "toolbar");
                    else Core.Log.Warning($"panel: '{t}' is not Remove, Block, Panel or Opening");
                    return State();
                })
                .OnCall("editor.pick.cancel", _ => { Picking.Cancel("hint"); return State(); })
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
                .OnCall("editor.catalog.tab", t =>
                {
                    if (Enum.TryParse(t, true, out Catalog.Tab tab)) Catalog.SetTab(tab, "catalog tab");
                    else Core.Log.Warning($"panel: '{t}' is no catalog tab");
                    return State();
                })
                .OnCall("editor.catalog.search", q => { Catalog.Search(q, "catalog search"); return State(); })
                .OnCall("editor.catalog.place", i =>
                {
                    if (int.TryParse(i, out int index)) Catalog.BeginPlace(index, "catalog");
                    else Core.Log.Warning($"panel: catalog place '{i}' is no index");
                    return State();
                })
                .OnCall("editor.point", key =>
                {
                    var r = PointRef.Parse(key);
                    if (r == null) Core.Log.Warning($"panel: point '{key}' is no point key");
                    else Points.Select(r, "hierarchy");
                    return State();
                })
                .OnCall("editor.point.new", kind => { Points.New(kind, null, "toolbar"); return State(); })
                .OnCall("editor.point.here", _ => { Points.Here("inspector"); return State(); })
                .OnCall("editor.point.add", _ => { Points.AddSub("inspector"); return State(); })
                .OnCall("editor.point.remove", _ => { Points.RemoveSub("inspector"); return State(); })
                .OnCall("editor.point.delete", _ => { Points.Delete("inspector"); return State(); })
                .OnCall("editor.point.set", fv =>
                {
                    int eq = (fv ?? "").IndexOf('=');
                    if (eq <= 0) Core.Log.Warning($"panel: point field '{fv}' is not field=value");
                    else Points.SetField(fv.Substring(0, eq), fv.Substring(eq + 1), "inspector");
                    return State();
                })
                .OnCall("editor.place.cancel", _ => { Catalog.CancelPlace("hint"); return State(); })
                .OnCall("editor.help", _ => { ToggleHelp("button"); return State(); })
                .OnCall("editor.top", _ => { FlyCamera.SetTop(!FlyCamera.Top, EditorMode.Camera?.Camera, "toolbar"); return State(); })
                .OnCall("editor.bookmark", n =>
                {
                    if (int.TryParse(n, out int slot)) Bookmarks.Jump(slot, "status bar");
                    else Core.Log.Warning($"panel: bookmark '{n}' is no number");
                    return State();
                })
                .OnCall("editor.point.play", _ => { Points.PlayHere("inspector"); return ""; })
                .OnCall("editor.point.frame", _ => { EditorMode.FrameSelection("inspector"); return State(); })
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
            _readout = Label("readout", out _readoutText);
            _tip = Label("tip", out _tipText);
            _tipWanted = null;
            _tipShown = null;
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
            _readout = null;
            _readoutText = null;
            _readoutShown = null;
            _tip = null;
            _tipText = null;
            _tipWanted = null;
            _tipShown = null;
            Help = false;
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

        /// <summary>
        /// A small label beside the cursor, drawn by the mod over the page: the value of a drag, or the tooltip of a
        /// button. A tooltip built as a page element would rebuild the whole page on every hover, and once more when
        /// the pointer leaves.
        /// </summary>
        private static RectTransform Label(string name, out TextMeshProUGUI text)
        {
            text = null;
            var font = TMP_Settings.defaultFontAsset;
            if (font == null) font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();
            if (font == null)
            {
                Core.Log.Warning($"panel: no TextMeshPro font is loaded; the cursor {name} stays hidden");
                return null;
            }
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(_root.transform, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0f, 1f);
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.07f, 0.08f, 0.1f, 0.92f);
            bg.raycastTarget = false;
            var label = new GameObject("text");
            var lr = label.AddComponent<RectTransform>();
            lr.SetParent(rect, false);
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = new Vector2(8f, 2f);
            lr.offsetMax = new Vector2(-8f, -2f);
            text = label.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = 17f;
            text.color = new Color(0.9f, 0.91f, 0.93f);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.enableWordWrapping = false;
            text.richText = true;
            text.raycastTarget = false;
            go.SetActive(false);
            Core.Log.Msg($"panel: cursor {name} built with font '{font.name}'");
            return rect;
        }

        /// <summary>The page names the tooltip of the control under the pointer: text, a tab, the key; empty hides it.</summary>
        internal static void ShowTip(string raw) => _tipWanted = string.IsNullOrEmpty(raw) ? null : raw;

        /// <summary>Keeps the tooltip beside the cursor while it is over a dock; off the docks it hides.</summary>
        internal static void TickTip(Vector2 screen, bool overPanel)
        {
            if (_tip == null) return;
            if (_tipWanted == null || !overPanel)
            {
                if (_tip.gameObject.activeSelf) _tip.gameObject.SetActive(false);
                if (!overPanel) _tipWanted = null;
                return;
            }
            float k = _root.GetComponent<Canvas>().scaleFactor;
            if (k <= 0f) k = 1f;
            if (_tipWanted != _tipShown)
            {
                _tipShown = _tipWanted;
                int tab = _tipWanted.IndexOf('\t');
                string text = tab < 0 ? _tipWanted : _tipWanted.Substring(0, tab);
                string key = tab < 0 ? null : _tipWanted.Substring(tab + 1);
                _tipText.text = key == null ? text : $"{text}   <color=#8b93a1>{key}</color>";
                _tip.sizeDelta = new Vector2(_tipText.GetPreferredValues(_tipText.text).x + 18f, 28f);
            }
            // Below and right of the cursor, kept on the screen at the right and bottom edges.
            float x = screen.x / k + 14f, y = screen.y / k - 22f;
            float w = Screen.width / k;
            if (x + _tip.sizeDelta.x > w - 4f) x = w - 4f - _tip.sizeDelta.x;
            if (y - _tip.sizeDelta.y < 4f) y = screen.y / k + 12f + _tip.sizeDelta.y;
            _tip.anchoredPosition = new Vector2(x, y);
            _tip.SetAsLastSibling();
            if (!_tip.gameObject.activeSelf) _tip.gameObject.SetActive(true);
        }

        /// <summary>Shows a value beside the cursor, or hides it when the text is null.</summary>
        internal static void ShowReadout(string text, Vector2 screen)
        {
            if (_readout == null) return;
            if (string.IsNullOrEmpty(text))
            {
                if (_readout.gameObject.activeSelf) _readout.gameObject.SetActive(false);
                return;
            }
            if (text != _readoutShown)
            {
                _readoutShown = text;
                _readoutText.text = text;
                _readout.sizeDelta = new Vector2(_readoutText.GetPreferredValues(text).x + 18f, 28f);
            }
            float k = _root.GetComponent<Canvas>().scaleFactor;
            if (k <= 0f) k = 1f;
            _readout.anchoredPosition = new Vector2(screen.x / k + 20f, screen.y / k - 16f);
            _readout.SetAsLastSibling();
            if (!_readout.gameObject.activeSelf) _readout.gameObject.SetActive(true);
        }

        /// <summary>A value for the cursor readout: invariant digits with a decimal comma, a sign when asked.</summary>
        internal static string Num(float v, int digits, bool sign = false)
        {
            var text = Math.Round(v, digits).ToString("F" + digits, CultureInfo.InvariantCulture).Replace('.', ',');
            return sign && v >= 0f ? "+" + text : text;
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
            // A drag changes the selection's position every frame, and each push rebuilds the whole page, which costs
            // a visible hitch ten times a second. The cursor readout shows the value meanwhile; the release pushes
            // the final state.
            if (Gizmo.Dragging || Areas.Dragging || Points.Dragging) return;
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
                    ["kind"] = !string.IsNullOrEmpty(p.Built.Def.Model) ? "model" : !string.IsNullOrEmpty(p.Built.Def.Prefab) ? "prefab" : "copy",
                });
            JsonNode selected = null;
            if (sel?.Built?.Handle != null)
            {
                var d = sel.Built.Def;
                selected = new JsonObject
                {
                    ["key"] = Copies.Key(sel),
                    ["id"] = d.Id,
                    ["owner"] = sel.LayerOwner,
                    ["expansion"] = sel.Expansion.Id,
                    ["template"] = !string.IsNullOrEmpty(d.Model) ? "Modell " + d.Model : !string.IsNullOrEmpty(d.Prefab) ? "Prefab " + d.Prefab : d.Template,
                    ["onto"] = d.Onto,
                    ["position"] = Vec(sel.Built.Handle.position),
                    ["yaw"] = Math.Round(sel.Built.Handle.eulerAngles.y, 1),
                    ["scale"] = Math.Round(d.Scale, 3),
                    ["obstacle"] = Obstacles.Wanted(d),
                    ["obstacleSet"] = d.Obstacle.HasValue,
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
                        ["name"] = r.Name ?? "",
                        ["parent"] = r.Parent ?? "",
                        ["side"] = r.Side.ToString(),
                        ["bounded"] = r.Area.size != Vector3.zero,
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
            // An undo can take out the rule or box that is selected; the selection goes with it.
            if (cur != null && (cur.Kind == AreaKind.Box ? cur.BoxDef == null : cur.RuleDef == null))
            {
                Areas.Deselect("its rule or box is gone");
                cur = null;
            }
            if (cur != null)
            {
                var o = new JsonObject { ["key"] = cur.Key, ["label"] = cur.Label, ["kind"] = cur.Kind.ToString(), ["expansion"] = cur.Expansion };
                if (cur.Get(out var local, out _))
                {
                    o["min"] = Vec(local.min);
                    o["max"] = Vec(local.max);
                }
                var r = cur.RuleDef;
                if (cur.Kind is AreaKind.Opening or AreaKind.Window && r != null)
                {
                    // An opening is edited on its own; its rule's effect and name stay with the rule.
                    o["rule"] = AreaRef.OfRule(cur.Expansion, r.Key).Key;
                    o["ruleName"] = string.IsNullOrEmpty(r.Name) ? r.Key : r.Name;
                }
                else if (cur.Kind == AreaKind.Rule && r != null)
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
            var points = new JsonArray();
            foreach (var (b, kind, id) in Points.Entries())
            {
                var state = Layers.StateOf(b.Id);
                points.Add(new JsonObject
                {
                    ["key"] = $"{b.Id}/{kind}/{id}/0",
                    ["kind"] = kind,
                    ["label"] = Points.KindLabels[kind],
                    ["id"] = id,
                    ["layer"] = state.Points.Contains((kind, id)),
                });
            }
            JsonNode point = null;
            var pr = Points.Current;
            if (pr?.Built != null)
            {
                var entry = Layers.PointEntry(pr.Built.Definition, pr.Kind, pr.Id);
                var o = new JsonObject
                {
                    ["key"] = pr.Key,
                    ["entry"] = pr.EntryKey,
                    ["kind"] = pr.Kind,
                    ["label"] = Points.Label(pr),
                    ["sub"] = pr.Sub,
                    ["layer"] = Layers.StateOf(pr.Expansion).Points.Contains((pr.Kind, pr.Id)),
                    ["fields"] = entry == null ? null : JsonNode.Parse(entry.ToJsonString()),
                };
                if (Points.WorldOf(pr, out var w, out float yaw))
                {
                    o["position"] = Vec(w);
                    o["yaw"] = Math.Round(yaw, 1);
                }
                point = o;
            }
            var root = new JsonObject
            {
                ["points"] = points,
                ["point"] = point,
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
                ["gridStep"] = Snap.GridStep,
                ["turnStep"] = Snap.TurnStep,
                ["gridSteps"] = new JsonArray(Snap.GridSteps.Select(v => (JsonNode)v).ToArray()),
                ["turnSteps"] = new JsonArray(Snap.TurnSteps.Select(v => (JsonNode)v).ToArray()),
                ["picking"] = Picking.Hint,
                ["pickTool"] = Picking.Tool?.ToString(),
                ["turn"] = Snap.Turn,
                ["edges"] = Snap.Edges,
                ["undo"] = Edits.UndoCount,
                ["redo"] = Edits.RedoCount,
                ["nextUndo"] = Edits.NextUndo,
                ["nextRedo"] = Edits.NextRedo,
                ["dirty"] = Saving.DirtyCount,
                ["speed"] = Math.Round(FlyCamera.Speed, 1),
                ["top"] = FlyCamera.Top,
                ["help"] = Help,
                ["placing"] = Catalog.PlacingLabel,
                ["placeYaw"] = Catalog.PlaceYaw,
                ["bookmarks"] = Bookmarks.State(),
                ["log"] = new JsonArray(Lines.Select(l => (JsonNode)l).ToArray()),
                ["catalog"] = Catalog.State(),
            };
            return root.ToJsonString();
        }
    }
}
