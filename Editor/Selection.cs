using MapExpansions.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The selected copy and its outline. A click picks the copy whose collider the ray hits first; a copy without
    /// a collider under the cursor is found by its renderer bounds, nearest first.
    /// </summary>
    internal static class Selection
    {
        internal static MainCopies.Placed Current { get; private set; }

        internal static Bounds CurrentBounds { get; private set; }

        private static GameObject _outline;
        private static LineRenderer _line;

        internal static void Pick(Camera cam, Vector3 screen, string why)
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
                    if (!hit.transform.IsChildOf(p.Built.Local.transform)) continue;
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
                Core.Log.Msg($"pick ({why}): no copy under the cursor at {screen}; selection cleared");
                Clear();
                return;
            }
            Select(best, $"{why}, {how} at {bestDist:0.0} m");
        }

        internal static void Select(MainCopies.Placed p, string why)
        {
            if (p?.Built?.Local == null)
            {
                Core.Log.Warning($"select ({why}): the copy has no local object; nothing selected");
                return;
            }
            Current = p;
            CurrentBounds = Copies.Bounds(p);
            Core.Log.Msg($"select ({why}): {Copies.Key(p)} at {p.Built.Local.transform.position}, bounds {CurrentBounds.size}");
            Panel.Note($"Ausgewählt: {p.Built.Def.Id}");
        }

        internal static void Clear()
        {
            Current = null;
            if (_outline != null) _outline.SetActive(false);
        }

        internal static void Tick()
        {
            if (Current?.Built?.Local == null)
            {
                if (Current != null)
                {
                    Core.Log.Warning("the selected copy is gone; selection cleared");
                    Clear();
                }
                return;
            }
            CurrentBounds = Copies.Bounds(Current);
            DrawOutline(CurrentBounds);
        }

        private static void DrawOutline(Bounds b)
        {
            if (_outline == null)
            {
                _outline = new GameObject("MapExpansionsEditor_Outline");
                Object.DontDestroyOnLoad(_outline);
                _line = _outline.AddComponent<LineRenderer>();
                _line.useWorldSpace = true;
                _line.loop = false;
                _line.material = Gizmo.Material(new Color(1f, 0.75f, 0.1f));
                _line.startColor = _line.endColor = new Color(1f, 0.75f, 0.1f);
                _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _line.receiveShadows = false;
            }
            _outline.SetActive(true);
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
            _line.positionCount = pts.Length;
            for (int i = 0; i < pts.Length; i++) _line.SetPosition(i, pts[i]);
            float dist = EditorMode.Camera != null ? Vector3.Distance(EditorMode.Camera.transform.position, c) : 10f;
            _line.startWidth = _line.endWidth = Mathf.Max(0.01f, dist * 0.002f);
        }
    }
}
