using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
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

        internal static void Toggle(string why)
        {
            if (IsOpen) Close(why);
            else Open(why);
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
            Selection.Clear();
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
            if (!IsOpen) return;
            IsOpen = false;
            Camera = null;
            Gizmo.Release();
            Selection.Clear();
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
            bool overPanel = Panel.IsPointerOver(Input.mousePosition);

            FlyCamera.Tick(Camera, typing, overPanel);

            if (!typing && !FlyCamera.Looking)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (ctrl && Input.GetKeyDown(KeyCode.S)) Saving.SaveAll("ctrl+s");
            }

            if (!FlyCamera.Looking)
            {
                // The gizmo takes the click first: a handle over another copy moves the selection, it does not change it.
                bool gizmoTookMouse = Gizmo.Tick(Camera.Camera, overPanel);
                if (!gizmoTookMouse && Input.GetMouseButtonDown(0) && !overPanel)
                    Selection.Pick(Camera.Camera, Input.mousePosition, "click");
            }
            Selection.Tick();
            Panel.Tick();
        }
    }
}
