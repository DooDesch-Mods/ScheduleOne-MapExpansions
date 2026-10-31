using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// GUIDs of components a world state adds. Vanilla's Awake registers them in the static GUIDManager and nothing
    /// deregisters them on destroy, so the entries outlive the scene until the next LoadManager.CleanUp.
    /// </summary>
    internal static class GuidLeases
    {
        /// <summary>
        /// Takes the component's GUID out if the manager still maps it to this component. Works on a component whose
        /// Unity object is already gone: the interop wrapper and the GUID in its managed object remain readable.
        /// </summary>
        internal static void Release(Component component, string what)
        {
            if ((object)component == null)
            {
                Core.Log.Msg($"property guid: {what} was never created; nothing to deregister");
                return;
            }
            try
            {
                var registerable = component.Cast<Il2CppScheduleOne.IGUIDRegisterable>();
                var guid = registerable.GUID;
                if (!Il2Cpp.GUIDManager.IsGUIDAlreadyRegistered(guid))
                {
                    Core.Log.Msg($"property guid: {what} {guid} is not registered; nothing to deregister");
                    return;
                }
                var existing = Il2Cpp.GUIDManager.GetObject<Component>(guid);
                if ((object)existing != null && existing.Pointer != component.Pointer)
                {
                    Core.Log.Warning($"property guid: {what} {guid} now belongs to another object; it stays registered");
                    return;
                }
                Il2Cpp.GUIDManager.DeregisterObject(registerable);
            }
            catch (Exception e)
            {
                Core.Log.Warning($"property guid: deregistering {what} failed; a stale entry is cleared on the next re-apply or game start: {e.Message}");
            }
        }

        /// <summary>Throws when a live object holds the GUID. An entry of a destroyed object is stale and is removed.</summary>
        internal static void RequireFree(Il2CppSystem.Guid guid, string what)
        {
            if (!Il2Cpp.GUIDManager.IsGUIDAlreadyRegistered(guid)) return;
            var existing = Il2Cpp.GUIDManager.GetObject<Component>(guid);
            if ((object)existing == null || existing != null)
                throw new InvalidOperationException($"{what} GUID {guid} is already registered");
            Il2Cpp.GUIDManager.DeregisterObject(existing.Cast<Il2CppScheduleOne.IGUIDRegisterable>());
            Core.Log.Warning($"property guid: {what} {guid} was held by a destroyed object; the stale entry is removed");
        }
    }
}
