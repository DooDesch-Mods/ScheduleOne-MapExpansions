using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The keys and the mouse as the editor reads them: Unity's input, plus presses and drags a console command queues.
    /// A queued step plays for one frame, so a scripted drag runs through the same frame-by-frame code as a real one.
    /// </summary>
    internal static class EditorInput
    {
        internal readonly struct Step
        {
            public readonly Vector2? Mouse;
            public readonly bool Left;
            public readonly KeyCode[] Held;
            public readonly KeyCode Down;

            public Step(Vector2? mouse, bool left, KeyCode[] held, KeyCode down = KeyCode.None)
            {
                Mouse = mouse;
                Left = left;
                Held = held ?? System.Array.Empty<KeyCode>();
                Down = down;
            }
        }

        private static readonly Queue<Step> Steps = new();
        private static Step? _now;
        private static bool _leftBefore;
        private static Vector2 _mouseBefore;

        internal static bool Scripted => _now != null;

        internal static void Queue(IEnumerable<Step> steps)
        {
            foreach (var s in steps) Steps.Enqueue(s);
        }

        internal static int Pending => Steps.Count;

        /// <summary>Takes the next queued step for this frame, if any.</summary>
        internal static void BeginFrame() => _now = Steps.Count > 0 ? Steps.Dequeue() : null;

        internal static void EndFrame()
        {
            _leftBefore = Left(0);
            _mouseBefore = MousePosition;
        }

        internal static bool KeyDown(KeyCode k) => Input.GetKeyDown(k) || (_now != null && _now.Value.Down == k);

        internal static bool Key(KeyCode k) =>
            Input.GetKey(k) || (_now != null && (System.Array.IndexOf(_now.Value.Held, k) >= 0 || _now.Value.Down == k));

        internal static Vector2 MousePosition => _now?.Mouse ?? (Vector2)Input.mousePosition;

        internal static bool Left(int button) => button == 0 && _now != null ? _now.Value.Left : Input.GetMouseButton(button);

        internal static bool LeftDown(int button) =>
            button == 0 && _now != null ? _now.Value.Left && !_leftBefore : Input.GetMouseButtonDown(button);

        /// <summary>Mouse movement as Unity's look axes give it; a scripted step moves by its pixel change.</summary>
        internal static float Axis(string name)
        {
            if (_now?.Mouse == null) return Input.GetAxis(name);
            var d = _now.Value.Mouse.Value - _mouseBefore;
            return (name == "Mouse X" ? d.x : d.y) * 0.1f;
        }
    }
}
