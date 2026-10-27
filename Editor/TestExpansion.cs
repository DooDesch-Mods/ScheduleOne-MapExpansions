#if DEBUG
using MelonLoader.Utils;

namespace MapExpansions.Editor
{
    /// <summary>
    /// An expansion without a source scene, for checks of an edit layer over the main world. It is registered only
    /// when the file <c>UserData/MapExpansionsEditor/test-expansion</c> exists. Its code holds one rule; copies come
    /// from its layer file <c>Mods/editortest/map.json</c>. Debug builds only.
    /// </summary>
    internal static class TestExpansion
    {
        internal const string Id = "editortest";

        internal static void RegisterIfAsked()
        {
            var flag = Path.Combine(MelonEnvironment.UserDataDirectory, "MapExpansionsEditor", "test-expansion");
            if (!File.Exists(flag))
            {
                Core.Log.Msg($"test expansion: {flag} does not exist; '{Id}' is not registered");
                return;
            }
            var def = new ExpansionDefinition { Id = Id, DisplayName = "Editor test" };
            def.Hide.Add(ObjectRule.In(WorldSide.Main, "Sign", ObjectEffect.Hide, "Slums Gas Station"));
            bool ok = MapExpansionsApi.Register(def);
            if (ok) Core.Log.Msg($"test expansion: '{Id}' registered without a source scene");
            else Core.Log.Error($"test expansion: registering '{Id}' failed; the reason is logged by MapExpansions");
        }
    }
}
#endif
