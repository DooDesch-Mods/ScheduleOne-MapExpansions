using MapExpansions.Source;
using MelonLoader;

[assembly: MelonInfo(typeof(MapExpansions.Core), "MapExpansions", "0.0.0-dev", "DooDesch")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace MapExpansions
{
    public sealed class Core : MelonMod
    {
        internal static MelonLogger.Instance Log { get; private set; }

        internal static MelonPreferences_Entry<int> CaptureCycles;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            var cat = MelonPreferences.CreateCategory("MapExpansions");
            CaptureCycles = cat.CreateEntry("CaptureCycles", 1,
                description: "How often the source scene is captured in the menu. Values above 1 only test repeatability.");
            HarmonyInstance.PatchAll(typeof(Core).Assembly);
            Log.Msg("initialized");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            SourceCapture.OnSceneLoaded(sceneName);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
                SourceCapture.OnMenuReady();
        }
    }
}
