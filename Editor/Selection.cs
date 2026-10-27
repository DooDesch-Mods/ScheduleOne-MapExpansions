using MapExpansions.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The selected copies and their outlines. A click picks the copy whose collider the ray hits first; a copy without
    /// a collider under the cursor is found by its renderer bounds, nearest first. Ctrl adds to the selection or takes
    /// a copy out of it, a frame drawn with the mouse selects every copy whose centre lies inside. Copies are held by
    /// key, so a copy that is built anew stays selected.
    /// </summary>
    internal static class Selection
    {
        private static readonly List<string> Keys = new();
        private static readonly List<LineRenderer> Lines = new();

        /// <summary>The selected copies that stand in the world, in selection order.</summary>
        internal static IReadOnlyList<MainCopies.Placed> All =>
            Keys.Select(Copies.Find).Where(p => p?.Built?.Handle != null).ToList();

        /// <summary>The first selected copy; the inspector shows it.</summary>
        internal static MainCopies.Placed Current => All.FirstOrDefault();

        /// <summary>Bounds around every selected copy.</summary>
        internal static Bounds CurrentBounds { get; private set; }

        internal static int Count => Keys.Count;

        internal static bool Contains(string key) => Keys.Contains(key);

        internal static void Pick(Camera cam, Vector3 screen, bool add, string why)
        {
            if (cam == null)
            {
                Core.Log.Warning($"pick ({why}): no camera");
                return;
            }
            var ray = cam.ScreenPointToRay(screen);
            MainCopies.Placed best = null;
            float bestDist = float.MaxValue;
            string how = "";

            if (Physics.Raycast(ray, out var hit, 2000f, ~0, QueryTriggerInteraction.Ignore))
            {
                foreach (var p in Copies.All())
                {
                    if (!hit.transform.IsChildOf(p.Built.Handle)) continue;
                    best = p;
                    bestDist = hit.distance;
                    how = $"collider '{hit.collider.name}'";
                    break;
                }
                if (best == null) bestDist = hit.distance;
            }
            foreach (var p in Copies.All())
            {
                if (p == best) continue;
                if (!Copies.Bounds(p).IntersectRay(ray, out float d) || d >= bestDist) continue;
                best = p;
                bestDist = d;
                how = "renderer bounds";
            }

            if (best == null)
            {
                if (add)
                {
                    Core.Log.Msg($"pick ({why}): no copy under the cursor at {screen}; the selection stays");
                    return;
                }
                Core.Log.Msg($"pick ({why}): no copy under the cursor at {screen}; selection cleared");
                Clear();
                Areas.Deselect(why);
                Points.Deselect(why);
                return;
            }
            Select(best, $"{why}, {how} at {bestDist:0.0} m", add);
        }

        /// <summary>Selects every copy whose bounds centre lies inside a screen rectangle.</summary>
        internal static void Frame(Camera cam, Rect screen, bool add, string why)
        {
            Areas.Deselect(why);
            Points.Deselect(why);
            var found = new List<string>();
            foreach (var p in Copies.All())
            {
                var s = cam.WorldToScreenPoint(Copies.Bounds(p).center);
                if (s.z > 0f && screen.Contains(new Vector2(s.x, s.y))) found.Add(Copies.Key(p));
            }
            if (!add) Keys.Clear();
            foreach (var k in found)
                if (!Keys.Contains(k)) Keys.Add(k);
            Refresh();
            Core.Log.Msg($"select ({why}): frame {screen.xMin:0},{screen.yMin:0} to {screen.xMax:0},{screen.yMax:0} holds {found.Count} copies; {Keys.Count} selected");
            Panel.Note($"{Keys.Count} ausgewählt");
        }

        /// <summary>Selects a copy; with <paramref name="add"/> a selected copy goes out and another one comes in.</summary>
        internal static void Select(MainCopies.Placed p, string why, bool add = false)
        {
            if (p?.Built?.Handle == null)
            {
                Core.Log.Warning($"select ({why}): the copy has no local object; nothing selected");
                return;
            }
            Areas.Deselect(why);
            Points.Deselect(why);
            var key = Copies.Key(p);
            if (add && Keys.Remove(key))
            {
                Refresh();
                Core.Log.Msg($"select ({why}): {key} taken out; {Keys.Count} selected");
                Panel.Note($"Abgewählt: {p.Built.Def.Id}");
                return;
            }
            if (!add) Keys.Clear();
            Keys.Add(key);
            Refresh();
            Core.Log.Msg($"select ({why}): {key} at {p.Built.Handle.position}, bounds {Copies.Bounds(p).size}; {Keys.Count} selected");
            Panel.Note(Keys.Count == 1 ? $"Ausgewählt: {p.Built.Def.Id}" : $"{Keys.Count} ausgewählt");
        }

        /// <summary>Replaces the selection by keys, for a step that made or brought back copies.</summary>
        internal static void Set(IEnumerable<string> keys, string why)
        {
            Areas.Deselect(why);
            Points.Deselect(why);
            Keys.Clear();
            Keys.AddRange(keys.Distinct());
            Refresh();
            Core.Log.Msg($"select ({why}): {Keys.Count} copies: {string.Join(", ", Keys)}");
        }

        /// <summary>A copy was built anew under the same key; nothing changes but the bounds.</summary>
        internal static void Replace(string key)
        {
            if (Keys.Contains(key)) Refresh();
        }

        internal static void Remove(string key)
        {
            if (Keys.Remove(key)) Refresh();
        }

        internal static void Clear()
        {
            Keys.Clear();
            foreach (var l in Lines) l.gameObject.SetActive(false);
        }

        /// <summary>Clears the copies for a box selection, without touching the box selection.</summary>
        internal static void ClearQuietly()
        {
            if (Keys.Count > 0) Core.Log.Msg($"select: {Keys.Count} copies leave the selection for a box");
            Clear();
        }

        private static void Refresh()
        {
            var all = All;
            if (all.Count == 0) return;
            var b = Copies.Bounds(all[0]);
            foreach (var p in all.Skip(1)) b.Encapsulate(Copies.Bounds(p));
            CurrentBounds = b;
        }

        internal static void Tick()
        {
            var all = All;
            if (all.Count < Keys.Count)
            {
                var gone = Keys.Where(k => Copies.Find(k)?.Built?.Handle == null).ToList();
                foreach (var k in gone) Keys.Remove(k);
                Core.Log.Warning($"selected copies are gone: {string.Join(", ", gone)}; they leave the selection");
            }
            Refresh();
            for (int i = 0; i < all.Count; i++) DrawOutline(i, Copies.Bounds(all[i]));
            for (int i = all.Count; i < Lines.Count; i++) Lines[i].gameObject.SetActive(false);
        }

        private static void DrawOutline(int i, Bounds b)
        {
            while (Lines.Count <= i)
            {
                var go = new GameObject("MapExpansionsEditor_Outline" + Lines.Count);
                Object.DontDestroyOnLoad(go);
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.loop = false;
                line.material = Gizmo.Material(new Color(1f, 0.75f, 0.1f));
                line.startColor = line.endColor = new Color(1f, 0.75f, 0.1f);
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                Lines.Add(line);
            }
            var l = Lines[i];
            l.gameObject.SetActive(true);
            var c = b.center;
            var e = b.extents;
            Vector3 P(float x, float y, float z) => c + new Vector3(e.x * x, e.y * y, e.z * z);
            // One path over all twelve edges of the box; four edges are drawn twice.
            var pts = new[]
            {
                P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), P(-1, -1, -1),
                P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), P(-1, 1, -1),
                P(1, 1, -1), P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1),
            };
            l.positionCount = pts.Length;
            for (int k = 0; k < pts.Length; k++) l.SetPosition(k, pts[k]);
            float dist = EditorMode.Camera != null ? Vector3.Distance(EditorMode.Camera.transform.position, c) : 10f;
            l.startWidth = l.endWidth = Mathf.Max(0.01f, dist * 0.002f);
        }
    }
}
