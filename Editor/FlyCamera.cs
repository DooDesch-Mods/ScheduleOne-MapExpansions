using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The editor camera, as in Unreal: hold the right mouse button to look, WASD to fly, Q and E down and up, Shift
    /// faster, the wheel changes the speed while looking and moves forward otherwise. Alt and the left button orbit
    /// around the selection, F frames it. Without a held button the cursor is free for the docks and the world.
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
                float wheel = Input.mouseScrollDelta.y;
                if (wheel != 0f) Speed = Mathf.Clamp(Speed * Mathf.Pow(1.25f, wheel), MinSpeed, MaxSpeed);
            }
            else if (!overPanel)
            {
                float wheel = Input.mouseScrollDelta.y;
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
    }
}
