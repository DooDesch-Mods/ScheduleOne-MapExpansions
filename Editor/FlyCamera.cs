using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The editor camera, as in Unreal: hold the right mouse button to look, WASD to fly, Q and E down and up, Shift
    /// faster, the wheel changes the speed while looking and moves forward otherwise. Alt and the left button orbit
    /// around the selection, F frames it. Without a held button the cursor is free for the docks and the world.
    /// T switches to a top view: an orthographic camera straight down, the right button pans and the wheel zooms.
    /// </summary>
    internal static class FlyCamera
    {
        private const float LookDegreesPerUnit = 2f;
        private const float MinSpeed = 1f, MaxSpeed = 200f;

        internal static Vector3 Position { get; private set; }
        internal static float Yaw { get; private set; }
        internal static float Pitch { get; private set; }
        internal static float Speed { get; private set; } = 12f;

        /// <summary>True while the right mouse button holds the cursor for looking.</summary>
        internal static bool Looking { get; private set; }

        /// <summary>True while Alt and the left mouse button turn the camera around the orbit point.</summary>
        internal static bool Orbiting { get; private set; }

        private static Vector3 _orbitPoint;

        /// <summary>A direction the console drives instead of the keys, in camera space; zero for none.</summary>
        internal static Vector3 Scripted;

        /// <summary>True while the camera looks straight down without perspective.</summary>
        internal static bool Top { get; private set; }

        /// <summary>Height of the top view above the point it opened on; high enough to clear every roof.</summary>
        private const float TopHeight = 400f;

        /// <summary>Half the height of the top view in metres; kept between two openings.</summary>
        private static float _topSize = 40f;

        private static Vector3 _beforePos;
        private static float _beforeYaw, _beforePitch;
        private static bool _wasOrtho;
        private static float _wasSize, _wasFar;

        /// <summary>How far a point seems from the camera, for sizing handles: in the top view the zoom decides.</summary>
        internal static float ViewDistance(Camera cam, Vector3 point) =>
            cam != null && cam.orthographic ? cam.orthographicSize * 2f : cam != null ? Vector3.Distance(cam.transform.position, point) : 10f;

        /// <summary>Switches the top view on or off; off puts back the camera pose and projection from before.</summary>
        internal static void SetTop(bool on, Camera cam, string why)
        {
            if (on == Top) return;
            if (cam == null)
            {
                Core.Log.Warning($"top view ({why}): no editor camera; nothing switched");
                return;
            }
            if (on)
            {
                _beforePos = Position;
                _beforeYaw = Yaw;
                _beforePitch = Pitch;
                _wasOrtho = cam.orthographic;
                _wasSize = cam.orthographicSize;
                _wasFar = cam.farClipPlane;
                // Over the selection when there is one, else over the point the camera looks at.
                Vector3 focus;
                if (Selection.Count > 0) focus = Selection.CurrentBounds.center;
                else if (Physics.Raycast(Position, Rotation * Vector3.forward, out var hit, 500f, ~0, QueryTriggerInteraction.Ignore)) focus = hit.point;
                else focus = Position + Rotation * Vector3.forward * 20f;
                Position = new Vector3(focus.x, focus.y + TopHeight, focus.z);
                Yaw = 0f;
                Pitch = 90f;
                cam.orthographic = true;
                cam.orthographicSize = _topSize;
                cam.farClipPlane = Mathf.Max(_wasFar, TopHeight + 400f);
                Top = true;
                Core.Log.Msg($"top view ({why}): on over {focus}, {_topSize * 2f:0} m high, far clip {cam.farClipPlane:0}");
                Panel.Note("Draufsicht.");
            }
            else
            {
                cam.orthographic = _wasOrtho;
                cam.orthographicSize = _wasSize;
                cam.farClipPlane = _wasFar;
                Top = false;
                Place(_beforePos, _beforeYaw, _beforePitch);
                Core.Log.Msg($"top view ({why}): off, camera back at {_beforePos}");
                Panel.Note("Perspektive.");
            }
        }

        /// <summary>The camera is gone with the world; the next opening starts in perspective.</summary>
        internal static void ForgetTop()
        {
            if (Top) Core.Log.Msg("top view: the camera is gone; the view starts in perspective next time");
            Top = false;
        }

        internal static void Begin(Vector3 pos, Quaternion rot)
        {
            Position = pos;
            var e = rot.eulerAngles;
            Yaw = e.y;
            Pitch = e.x > 180f ? e.x - 360f : e.x;
            Looking = false;
            Orbiting = false;
            Scripted = Vector3.zero;
        }

        internal static void Place(Vector3 pos, float yaw, float pitch)
        {
            Position = pos;
            Yaw = yaw;
            Pitch = Mathf.Clamp(pitch, -89f, 89f);
        }

        internal static Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

        /// <summary>Puts the camera in front of a box, looking at its centre from the current direction.</summary>
        internal static void Frame(Bounds b, string why)
        {
            float dist = Mathf.Max(3f, b.extents.magnitude * 2.2f);
            Position = b.center - Rotation * Vector3.forward * dist;
            Core.Log.Msg($"frame ({why}): camera at {Position}, {dist:0.0} m from {b.center}");
        }

        private static bool Alt => EditorInput.Key(KeyCode.LeftAlt) || EditorInput.Key(KeyCode.RightAlt);

        internal static void Tick(PlayerCamera cam, bool typing, bool overPanel)
        {
            float dt = Time.unscaledDeltaTime;
            bool rmb = EditorInput.Left(1);
            if (rmb && !Looking && !overPanel && EditorInput.LeftDown(1))
            {
                Looking = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (!rmb && Looking)
            {
                Looking = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (Top)
            {
                TopTick(cam, typing, overPanel, dt);
                return;
            }

            if (!Looking && !Orbiting && Alt && !overPanel && EditorInput.LeftDown(0))
            {
                Orbiting = true;
                // Around the selection when there is one, else around the point ten metres ahead.
                _orbitPoint = Selection.Count > 0 ? Selection.CurrentBounds.center : Position + Rotation * Vector3.forward * 10f;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                Core.Log.Msg($"orbit: around {_orbitPoint}");
            }
            else if (Orbiting && !EditorInput.Left(0))
            {
                Orbiting = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            if (Orbiting)
            {
                float dist = Vector3.Distance(Position, _orbitPoint);
                Yaw += EditorInput.Axis("Mouse X") * LookDegreesPerUnit;
                Pitch = Mathf.Clamp(Pitch - EditorInput.Axis("Mouse Y") * LookDegreesPerUnit, -89f, 89f);
                Position = _orbitPoint - Rotation * Vector3.forward * dist;
                cam.transform.SetPositionAndRotation(Position, Rotation);
                return;
            }

            var move = Vector3.zero;
            if (Looking)
            {
                Yaw += EditorInput.Axis("Mouse X") * LookDegreesPerUnit;
                Pitch = Mathf.Clamp(Pitch - EditorInput.Axis("Mouse Y") * LookDegreesPerUnit, -89f, 89f);
                if (!typing)
                {
                    if (EditorInput.Key(KeyCode.W)) move.z += 1f;
                    if (EditorInput.Key(KeyCode.S)) move.z -= 1f;
                    if (EditorInput.Key(KeyCode.D)) move.x += 1f;
                    if (EditorInput.Key(KeyCode.A)) move.x -= 1f;
                    if (EditorInput.Key(KeyCode.E)) move.y += 1f;
                    if (EditorInput.Key(KeyCode.Q)) move.y -= 1f;
                }
                float wheel = EditorInput.Wheel;
                if (wheel != 0f) Speed = Mathf.Clamp(Speed * Mathf.Pow(1.25f, wheel), MinSpeed, MaxSpeed);
            }
            else if (!overPanel && !Catalog.Placing)
            {
                float wheel = EditorInput.Wheel;
                if (wheel != 0f) Position += Rotation * Vector3.forward * (wheel * Speed * 0.25f);
            }
            if (Scripted != Vector3.zero) move = Scripted;

            if (move != Vector3.zero)
            {
                float boost = EditorInput.Key(KeyCode.LeftShift) ? 3f : 1f;
                var up = new Vector3(0f, move.y, 0f);
                var flat = Rotation * new Vector3(move.x, 0f, move.z);
                Position += (flat + up).normalized * (Speed * boost * dt);
            }
            cam.transform.SetPositionAndRotation(Position, Rotation);
        }

        /// <summary>The top view: the right button drags the map, WASD pans while it is held, the wheel zooms.</summary>
        private static void TopTick(PlayerCamera cam, bool typing, bool overPanel, float dt)
        {
            var view = cam.Camera;
            if (Looking)
            {
                // The world follows the mouse: one unit of mouse axis moves a fixed share of the view.
                float perUnit = _topSize * 0.03f;
                Position -= new Vector3(EditorInput.Axis("Mouse X"), 0f, EditorInput.Axis("Mouse Y")) * perUnit;
                if (!typing)
                {
                    var pan = Vector3.zero;
                    if (EditorInput.Key(KeyCode.W)) pan.z += 1f;
                    if (EditorInput.Key(KeyCode.S)) pan.z -= 1f;
                    if (EditorInput.Key(KeyCode.D)) pan.x += 1f;
                    if (EditorInput.Key(KeyCode.A)) pan.x -= 1f;
                    float boost = EditorInput.Key(KeyCode.LeftShift) ? 3f : 1f;
                    if (pan != Vector3.zero) Position += pan.normalized * (_topSize * boost * dt);
                }
            }
            if (!overPanel && !Catalog.Placing)
            {
                float wheel = EditorInput.Wheel;
                if (wheel != 0f) _topSize = Mathf.Clamp(_topSize * Mathf.Pow(0.85f, wheel), 5f, 800f);
            }
            if (view != null) view.orthographicSize = _topSize;
            cam.transform.SetPositionAndRotation(Position, Rotation);
        }
    }
}
