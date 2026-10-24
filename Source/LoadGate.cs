using HarmonyLib;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Persistence.Datas;

namespace MapExpansions.Source
{
    /// <summary>
    /// Holds back a save load or a join while the menu capture runs, and starts it once the capture is finished.
    /// A load that ran in parallel would switch scenes under the half-loaded source scene.
    /// </summary>
    internal static class LoadGate
    {
        private static Action _pending;

        public static bool Defer(string what, Action resume)
        {
            if (!SourceCapture.Busy) return false;
            if (_pending != null)
                Core.Log.Warning($"load gate: {what} replaces an earlier deferred load");
            _pending = resume;
            Core.Log.Msg($"load gate: {what} deferred until the capture is finished");
            return true;
        }

        public static void Release()
        {
            var next = _pending;
            _pending = null;
            if (next == null) return;
            Core.Log.Msg("load gate: starting the deferred load");
            try { next(); }
            catch (Exception e) { Core.Log.Error($"load gate: deferred load failed: {e}"); }
        }
    }

    [HarmonyPatch(typeof(LoadManager), nameof(LoadManager.StartGame))]
    internal static class StartGamePatch
    {
        private static bool Prefix(LoadManager __instance, SaveInfo info, bool allowLoadStacking, bool allowSaveBackup)
        {
            return !LoadGate.Defer("StartGame", () => __instance.StartGame(info, allowLoadStacking, allowSaveBackup));
        }
    }

    [HarmonyPatch(typeof(LoadManager), nameof(LoadManager.LoadAsClient))]
    internal static class LoadAsClientPatch
    {
        private static bool Prefix(LoadManager __instance, string steamId64)
        {
            return !LoadGate.Defer("LoadAsClient", () => __instance.LoadAsClient(steamId64));
        }
    }
}
