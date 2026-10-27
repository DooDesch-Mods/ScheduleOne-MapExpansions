using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Phone;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Entering and leaving the editor. While it is open the player camera stands free of the player (vanilla's own
    /// transform override, so the game's look code leaves it alone), the cursor is free, the HUD is hidden and the
    /// player cannot walk. Leaving puts every one of these back.
    /// </summary>
    internal static class EditorMode
    {
        /// <summary>Name in the camera's list of open UI elements; the game blocks walking while the list is not empty.</summary>
        private const string UiElement = "MapExpansionsEditor";

        internal static bool IsOpen { get; private set; }

        internal static PlayerCamera Camera { get; private set; }

        private static bool _hudWasOn;

        /// <summary>Whether the camera drew the held item before the editor hid it.</summary>
        private static bool _viewmodelWasOn;

        /// <summary>Held in a field: deregistering compares delegates, and a second conversion is another object.</summary>
        private static GameInput.ExitDelegate _exit;

        /// <summary>Pixels the mouse must travel with the left button held before a click becomes a selection frame.</summary>
        private const float FramePixels = 6f;

        /// <summary>The frame the editor opened in; the key that opened it does not also count as an editor key.</summary>
        private static int _openedFrame = -1;

        private static bool _pressing;
        private static Vector2 _pressAt;
        private static bool _framing;

        /// <summary>True while the player walks after Tab: Tab or the open key brings the editor back where it was.</summary>
        internal static bool Playing { get; private set; }

        private static Vector3 _resumePos;
        private static float _resumeYaw, _resumePitch;

        internal static void Toggle(string why)
        {
            if (IsOpen) Close(why);
            else if (Playing) Resume(why);
            else Open(why);
        }

        /// <summary>Leaves the editor and puts the player on the ground below the camera, to walk the change.</summary>
        internal static void Play(string why)
        {
            if (!IsOpen)
            {
                Core.Log.Msg($"play ({why}): the editor is not open");
                return;
            }
            var pos = FlyCamera.Position;
            _resumePos = pos;
            _resumeYaw = FlyCamera.Yaw;
            _resumePitch = FlyCamera.Pitch;
            var target = pos;
            if (Physics.Raycast(pos, Vector3.down, out var hit, 500f, ~0, QueryTriggerInteraction.Ignore)) target = hit.point;
            else Core.Log.Warning($"play ({why}): no ground below the camera at {pos}; the player stands at the camera");
            Close(why);
            if (!PlayerSingleton<PlayerMovement>.InstanceExists || PlayerSingleton<PlayerMovement>.Instance == null)
            {
                Core.Log.Error($"play ({why}): there is no player movement; the player stays where it was");
                return;
            }
            PlayerSingleton<PlayerMovement>.Instance.Teleport(target, true);
            Playing = true;
            Core.Log.Msg($"play ({why}): player at {target}; Tab or the open key goes back to the camera at {pos}");
        }

        /// <summary>Opens the editor again at the camera pose that Tab left.</summary>
        internal static void Resume(string why)
        {
            // Tab is also the phone key; when the phone took the same press it is shut again.
            if (PlayerSingleton<Phone>.InstanceExists && PlayerSingleton<Phone>.Instance != null && PlayerSingleton<Phone>.Instance.IsOpen)
            {
                PlayerSingleton<Phone>.Instance.SetIsOpen(false);
                Core.Log.Msg($"resume ({why}): the phone opened with the same key; it is closed again");
            }
            if (!Open(why)) return;
            Playing = false;
            FlyCamera.Place(_resumePos, _resumeYaw, _resumePitch);
            Core.Log.Msg($"resume ({why}): camera back at {_resumePos}");
        }

        internal static bool Open(string why)
        {
            if (IsOpen)
            {
                Core.Log.Msg($"open ({why}): the editor is already open");
                return true;
            }
            if (!PlayerSingleton<PlayerCamera>.InstanceExists || PlayerSingleton<PlayerCamera>.Instance == null)
            {
                Core.Log.Warning($"open ({why}): there is no player camera; the editor opens only in a loaded world");
                return false;
            }
            if (GameInput.IsTyping)
            {
                Core.Log.Msg($"open ({why}): a text field has the keyboard; the editor stays closed");
                return false;
            }
            var cam = PlayerSingleton<PlayerCamera>.Instance;
            if (cam.activeUIElements != null && cam.activeUIElements.Count > 0)
            {
                Core.Log.Warning($"open ({why}): another screen is open ({cam.activeUIElements[0]}); close it first");
                return false;
            }

            Camera = cam;
            var t = cam.transform;
            // Lerp time 0 sets the pose at once; from here on the fly camera moves the transform itself.
            cam.OverrideTransform(t.position, t.rotation, 0f, false);
            cam.AddActiveUIElement(UiElement);
            cam.FreeMouse(true);
            FlyCamera.Begin(t.position, t.rotation);

            _hudWasOn = false;
            if (Singleton<HUD>.InstanceExists && Singleton<HUD>.Instance.canvas != null)
            {
                _hudWasOn = Singleton<HUD>.Instance.canvas.enabled;
                Singleton<HUD>.Instance.canvas.enabled = false;
            }
            else Core.Log.Warning($"open ({why}): no HUD canvas found; the HUD stays as it is");

            // The held item would float in front of the free camera; vanilla hides it the same way for its own camera moves.
            _viewmodelWasOn = ViewmodelDrawn(cam);
            if (PlayerSingleton<PlayerInventory>.InstanceExists && PlayerSingleton<PlayerInventory>.Instance != null)
                PlayerSingleton<PlayerInventory>.Instance.SetViewmodelVisible(false);
            else Core.Log.Warning($"open ({why}): no player inventory found; the held item stays visible");

            RegisterExit();
            IsOpen = true;
            _openedFrame = Time.frameCount;
            Panel.Mount();
            Core.Log.Msg($"open ({why}): editor open at {t.position}, {Copies.Count()} placed copies");
            return true;
        }

        internal static void Close(string why)
        {
            if (!IsOpen)
            {
                Core.Log.Msg($"close ({why}): the editor is not open");
                return;
            }
            IsOpen = false;
            Gizmo.Release();
            EndFrame();
            Panel.Unmount();
            DeregisterExit();

            var cam = Camera;
            Camera = null;
            if (cam != null)
            {
                cam.RemoveActiveUIElement(UiElement);
                cam.StopTransformOverride(0f, true, true);
                cam.LockMouse(true);
                if (_viewmodelWasOn && PlayerSingleton<PlayerInventory>.InstanceExists && PlayerSingleton<PlayerInventory>.Instance != null)
                    PlayerSingleton<PlayerInventory>.Instance.SetViewmodelVisible(true);
            }
            else Core.Log.Warning($"close ({why}): the player camera is gone; nothing to give back");
            if (_hudWasOn && Singleton<HUD>.InstanceExists && Singleton<HUD>.Instance.canvas != null)
                Singleton<HUD>.Instance.canvas.enabled = true;
            Core.Log.Msg($"close ({why}): back in the game");
        }

        /// <summary>The world is unloading; everything the editor built goes with it, so only the state is reset.</summary>
        internal static void SceneGone()
        {
            Playing = false;
            if (!IsOpen) return;
            IsOpen = false;
            Camera = null;
            Gizmo.Release();
            Selection.Clear();
            EndFrame();
            Panel.Unmount();
            _exit = null;
            Core.Log.Warning("the world unloaded while the editor was open; the editor is closed");
        }

        private static bool ViewmodelDrawn(PlayerCamera cam)
        {
            int layer = LayerMask.NameToLayer("Viewmodel");
            return layer >= 0 && cam.Camera != null && (cam.Camera.cullingMask & (1 << layer)) != 0;
        }

        private static void RegisterExit()
        {
            // The game clears its exit listeners on every scene change, so the listener is put in at each opening.
            DeregisterExit();
            _exit = (GameInput.ExitDelegate)(Action<ExitAction>)(action =>
            {
                try
                {
                    if (action == null || action.Type != ExitType.Primary || !IsOpen) return;
                    action.Use();
                    Close("escape");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"escape could not close the editor: {e}");
                }
            });
            // Above the pause menu, so Escape leaves the editor first.
            GameInput.RegisterExitListener(_exit, 2);
        }

        private static void DeregisterExit()
        {
            if (_exit == null) return;
            GameInput.DeregisterExitListener(_exit);
            _exit = null;
        }

        internal static void Tick()
        {
            if (!IsOpen) return;
            if (Camera == null)
            {
                Core.Log.Warning("the player camera is gone; the editor closes");
                SceneGone();
                return;
            }
            // The game turns its HUD back on by itself, for example when a load finishes after the editor opened.
            if (Singleton<HUD>.InstanceExists && Singleton<HUD>.Instance.canvas != null && Singleton<HUD>.Instance.canvas.enabled)
            {
                Singleton<HUD>.Instance.canvas.enabled = false;
                _hudWasOn = true;
                Core.Log.Msg("the game turned the HUD on while the editor is open; it is hidden again");
            }
            bool typing = GameInput.IsTyping;
            bool overPanel = Panel.IsPointerOver(EditorInput.MousePosition);

            FlyCamera.Tick(Camera, typing, overPanel);
            bool camBusy = FlyCamera.Looking || FlyCamera.Orbiting;

            if (!typing && !camBusy && !Gizmo.Dragging && Time.frameCount != _openedFrame && Keys()) return;

            if (!camBusy)
            {
                // The gizmo takes the click first: a handle over another copy moves the selection, it does not change it.
                bool gizmoTookMouse = Gizmo.Tick(Camera.Camera, overPanel);
                if (!gizmoTookMouse) Mouse(overPanel);
            }
            else EndFrame();
            Selection.Tick();
            Panel.Tick();
        }

        private static bool Ctrl => EditorInput.Key(KeyCode.LeftControl) || EditorInput.Key(KeyCode.RightControl);
        private static bool Shift => EditorInput.Key(KeyCode.LeftShift) || EditorInput.Key(KeyCode.RightShift);

        /// <summary>The editor keys; true when one of them closed the editor.</summary>
        private static bool Keys()
        {
            if (Ctrl)
            {
                if (EditorInput.KeyDown(KeyCode.S)) Saving.SaveAll("ctrl+s");
                else if (EditorInput.KeyDown(KeyCode.Z) && Shift) Edits.Redo("ctrl+shift+z");
                else if (EditorInput.KeyDown(KeyCode.Z)) Edits.Undo("ctrl+z");
                else if (EditorInput.KeyDown(KeyCode.Y)) Edits.Redo("ctrl+y");
                else if (EditorInput.KeyDown(KeyCode.D)) Edits.Duplicate(Selection.All, "ctrl+d");
                return false;
            }
            if (EditorInput.KeyDown(KeyCode.W)) Gizmo.SetMode(GizmoMode.Move, "w");
            else if (EditorInput.KeyDown(KeyCode.E)) Gizmo.SetMode(GizmoMode.Rotate, "e");
            else if (EditorInput.KeyDown(KeyCode.R)) Gizmo.SetMode(GizmoMode.Scale, "r");
            else if (EditorInput.KeyDown(KeyCode.Delete)) Edits.Delete(Selection.All, "delete");
            else if (EditorInput.KeyDown(KeyCode.End)) Edits.Drop(Selection.All, "end");
            else if (EditorInput.KeyDown(KeyCode.F)) FrameSelection("f");
            else if (EditorInput.KeyDown(KeyCode.Tab))
            {
                Play("tab");
                return true;
            }
            return false;
        }

        internal static void FrameSelection(string why)
        {
            if (Selection.Count == 0)
            {
                Core.Log.Msg($"frame ({why}): nothing is selected");
                Panel.Note("Nichts ausgewählt.");
                return;
            }
            FlyCamera.Frame(Selection.CurrentBounds, why);
        }

        /// <summary>A click picks, Ctrl and a click adds or takes out, a drag over the world draws a selection frame.</summary>
        private static void Mouse(bool overPanel)
        {
            var mouse = EditorInput.MousePosition;
            if (EditorInput.LeftDown(0) && !overPanel)
            {
                _pressing = true;
                _pressAt = mouse;
                _framing = false;
            }
            if (!_pressing) return;
            if (!_framing && Vector2.Distance(mouse, _pressAt) > FramePixels) _framing = true;
            var rect = Rect.MinMaxRect(Mathf.Min(_pressAt.x, mouse.x), Mathf.Min(_pressAt.y, mouse.y),
                Mathf.Max(_pressAt.x, mouse.x), Mathf.Max(_pressAt.y, mouse.y));
            if (EditorInput.Left(0))
            {
                if (_framing) Panel.ShowFrame(rect);
                return;
            }
            _pressing = false;
            if (_framing) Selection.Frame(Camera.Camera, rect, Ctrl, "frame");
            else Selection.Pick(Camera.Camera, _pressAt, Ctrl, Ctrl ? "ctrl+click" : "click");
            EndFrame();
        }

        private static void EndFrame()
        {
            _pressing = false;
            _framing = false;
            Panel.ShowFrame(null);
        }
    }
}
