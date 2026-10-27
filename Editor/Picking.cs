using UnityEngine;

namespace MapExpansions.Editor
{
    internal enum PickTool
    {
        Remove,
        Block,
        Panel,
        Opening,
    }

    /// <summary>
    /// The pick tools of the toolbar: a remove rule, a blocker, a panel or a doorway is set where the user clicks. The
    /// button starts the tool, a box under the cursor shows what a click makes, the click makes it as one step. Shift
    /// and a click keeps the tool, Escape or the button again ends it. The catalog placing works the same way.
    /// </summary>
    internal static class Picking
    {
        private static readonly Color RemoveColor = new(1f, 0.35f, 0.3f);
        private static readonly Color BoxColor = new(0.2f, 0.85f, 1f);

        private static PickTool? _tool;
        private static LineRenderer _line;
        private static PickTool? _lineFor;

        internal static bool Active => _tool != null;

        internal static PickTool? Tool => _tool;

        /// <summary>The hint over the view: what to click.</summary>
        internal static string Hint => _tool switch
        {
            PickTool.Remove => "Entfernen: Objekt anklicken",
            PickTool.Block => "Blocker: Fläche einer Kopie anklicken",
            PickTool.Panel => "Panel: Fläche einer Kopie anklicken",
            PickTool.Opening => "Öffnung: Wand des ausgehöhlten Objekts anklicken",
            _ => null,
        };

        /// <summary>Starts a tool; the same tool again ends it.</summary>
        internal static void Begin(PickTool tool, string why)
        {
            if (_tool == tool)
            {
                Cancel(why);
                return;
            }
            if (tool == PickTool.Opening && AreaEdits.HollowRule(why) == null) return;
            Catalog.CancelPlace(why);
            _tool = tool;
            Core.Log.Msg($"pick ({why}): {tool} with the mouse");
            Panel.Note(Hint + ".");
        }

        internal static void Cancel(string why)
        {
            if (_tool == null) return;
            Core.Log.Msg($"pick ({why}): {_tool} stopped");
            _tool = null;
            Hide();
        }

        private static void Hide()
        {
            if (_line != null && _line.gameObject.activeSelf) _line.gameObject.SetActive(false);
        }

        /// <summary>Shows the preview under the cursor and makes it on a click; true when a tool owns the mouse.</summary>
        internal static bool Tick(Camera cam, bool overPanel)
        {
            if (_tool == null)
            {
                Hide();
                return false;
            }
            if (cam == null)
            {
                Cancel("the camera is gone");
                return false;
            }
            if (overPanel)
            {
                Hide();
                return false;
            }
            var tool = _tool.Value;
            var ray = cam.ScreenPointToRay(EditorInput.MousePosition);
            if (AreaEdits.Preview(tool, ray, out var corners)) Draw(tool, corners);
            else Hide();
            if (EditorInput.LeftDown(0))
            {
                bool more = EditorInput.Key(KeyCode.LeftShift) || EditorInput.Key(KeyCode.RightShift);
                // A click on nothing usable leaves the tool on; the status line says why.
                if (AreaEdits.Create(tool, ray, more ? "click, shift" : "click") && !more) Cancel("made");
            }
            return true;
        }

        private static void Draw(PickTool tool, Vector3[] k)
        {
            var color = tool == PickTool.Remove ? RemoveColor : BoxColor;
            if (_line == null) _line = Selection.NewLine("MapExpansionsEditor_Pick", color);
            if (_lineFor != tool)
            {
                _line.material = Gizmo.Material(color);
                _line.startColor = _line.endColor = color;
                _lineFor = tool;
            }
            _line.gameObject.SetActive(true);
            // One path over all twelve edges, as the box gizmo draws them.
            int[] path = { 0, 1, 5, 4, 0, 2, 3, 7, 6, 2, 3, 1, 5, 7, 6, 4 };
            _line.positionCount = path.Length;
            var centre = Vector3.zero;
            for (int i = 0; i < path.Length; i++) _line.SetPosition(i, k[path[i]]);
            foreach (var c in k) centre += c / 8f;
            float dist = FlyCamera.ViewDistance(EditorMode.Camera?.Camera, centre);
            _line.startWidth = _line.endWidth = Mathf.Max(0.01f, dist * 0.002f);
        }
    }
}
