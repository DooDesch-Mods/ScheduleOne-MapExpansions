using MapExpansions.Lifecycle;
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
            // MelonLoader has already applied the [HarmonyPatch] classes; a second PatchAll would run every patch twice.
            Log.Msg($"initialized; {HarmonyInstance.GetPatchedMethods().Count()} methods patched");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            SourceCapture.OnSceneLoaded(sceneName);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
                SourceCapture.OnMenuReady();
            else if (sceneName == BuildPipeline.MainScene)
                BuildPipeline.OnMainInitialized();
        }

        public override void OnUpdate()
        {
            Connections.TunnelShadows.Tick();
            World.WeatherCover.Tick();
            World.SkyOutsideBounds.Tick();
            World.PhoneMap.Tick();
            World.SeparateViews.Tick();
            World.Properties.Tick();
            World.MainCopies.Tick();
            World.Shops.Tick();
        }

        public override void OnFixedUpdate()
        {
            Connections.SlopeAssist.FixedTick();
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName == BuildPipeline.MainScene)
                BuildPipeline.OnMainUnloaded();
        }
    }
}
