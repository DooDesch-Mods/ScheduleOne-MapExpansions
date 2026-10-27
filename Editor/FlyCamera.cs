using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// The editor camera, as in Unreal: hold the right mouse button to look, WASD to fly, Q and E down and up, Shift
    /// faster, the wheel changes the speed while looking and moves forward otherwise. Without the right button the
    /// cursor is free for the docks and the world.
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

        /// <summary>A direction the console drives instead of the keys, in camera space; zero for none.</summary>
        internal static Vector3 Scripted;

        internal static void Begin(Vector3 pos, Quaternion rot)
        {
            Position = pos;
            var e = rot.eulerAngles;
            Yaw = e.y;
            Pitch = e.x > 180f ? e.x - 360f : e.x;
            Looking = false;
            Scripted = Vector3.zero;
        }

        internal static void Place(Vector3 pos, float yaw, float pitch)
        {
            Position = pos;
            Yaw = yaw;
            Pitch = Mathf.Clamp(pitch, -89f, 89f);
        }

        internal static Quaternion Rotation => Quaternion.Euler(Pitch, Yaw, 0f);

        internal static void Tick(PlayerCamera cam, bool typing, bool overPanel)
        {
            float dt = Time.unscaledDeltaTime;
            bool rmb = Input.GetMouseButton(1);
            if (rmb && !Looking && !overPanel && Input.GetMouseButtonDown(1))
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

            var move = Vector3.zero;
            if (Looking)
            {
                Yaw += Input.GetAxis("Mouse X") * LookDegreesPerUnit;
                Pitch = Mathf.Clamp(Pitch - Input.GetAxis("Mouse Y") * LookDegreesPerUnit, -89f, 89f);
                if (!typing)
                {
                    if (Input.GetKey(KeyCode.W)) move.z += 1f;
                    if (Input.GetKey(KeyCode.S)) move.z -= 1f;
                    if (Input.GetKey(KeyCode.D)) move.x += 1f;
                    if (Input.GetKey(KeyCode.A)) move.x -= 1f;
                    if (Input.GetKey(KeyCode.E)) move.y += 1f;
                    if (Input.GetKey(KeyCode.Q)) move.y -= 1f;
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
                float boost = Input.GetKey(KeyCode.LeftShift) ? 3f : 1f;
                var up = new Vector3(0f, move.y, 0f);
                var flat = Rotation * new Vector3(move.x, 0f, move.z);
                Position += (flat + up).normalized * (Speed * boost * dt);
            }
            cam.transform.SetPositionAndRotation(Position, Rotation);
        }
    }
}
