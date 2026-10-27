using System.Globalization;
using System.Text.Json.Nodes;
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
        private static SurfaceHandle _surface;
        private static string _pushed = "";
        private static float _sincePush;
        private static bool _warnedNoEventSystem;

        private static readonly List<string> Lines = new();

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
                    if (p == null) Core.Log.Warning($"panel: select '{key}' names no placed copy");
                    else Selection.Select(p, "hierarchy");
                    return State();
                })
                .OnCall("editor.save", _ => { Saving.SaveAll("toolbar"); return State(); })
                .OnCall("editor.close", _ => { EditorMode.Close("toolbar"); return ""; });
            _pushed = "";
            Core.Log.Msg($"panel: surface '{SurfaceId}' mounted: {Surfaces.IsMounted(SurfaceId)}");
        }

        internal static void Unmount()
        {
            if (_root == null) return;
            Surfaces.Unmount(SurfaceId);
            Object.Destroy(_root);
            _root = null;
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
                    ["size"] = Vec(Selection.CurrentBounds.size),
                    ["dirty"] = Saving.IsDirty(sel),
                };
            }
            var root = new JsonObject
            {
                ["copies"] = copies,
                ["selected"] = selected,
                ["dirty"] = Saving.DirtyCount,
                ["speed"] = Math.Round(FlyCamera.Speed, 1),
                ["log"] = new JsonArray(Lines.Select(l => (JsonNode)l).ToArray()),
            };
            return root.ToJsonString();
        }
    }
}
