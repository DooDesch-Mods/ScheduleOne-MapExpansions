using System.Collections;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Persistence;
using MapExpansions.Connections;
using MapExpansions.Navigation;
using MapExpansions.Regions;
using MapExpansions.Source;
using MapExpansions.World;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// Phase 3 of the load: places every captured expansion in the main world, builds its connections, clears them,
    /// then joins NPC and vehicle navigation once the game has finished loading.
    /// </summary>
    internal static class BuildPipeline
    {
        public const string MainScene = "Main";

        private sealed class Build
        {
            public BuiltExpansion Expansion;
            public NpcNavigation Npc;
            public VehicleNavigation Vehicles;
            public bool NavigationFailed;
        }

        private static readonly List<Build> Builds = new();
        private static object _navRoutine;

        public static BuiltExpansion Get(string id) => Builds.FirstOrDefault(x => x.Expansion.Id == id)?.Expansion;

        public static IReadOnlyList<BuiltExpansion> All => Builds.Select(x => x.Expansion).ToList();

        /// <summary>The scene index of the last build, kept for rules applied one at a time afterwards.</summary>
        internal static SceneIndex Index { get; private set; }

        public static void OnMainInitialized()
        {
            if (Builds.Count > 0)
            {
                Core.Log.Warning($"build: main world initialized while {Builds.Count} expansions are still built; tearing them down first");
                Teardown();
            }
            var main = SceneManager.GetSceneByName(MainScene);
            if (!main.IsValid() || !main.isLoaded)
            {
                Core.Log.Error("build: main scene is not loaded; no expansion is built");
                return;
            }

            foreach (var def in Registry.Usable())
            {
                var state = Registry.GetState(def.Id);
                if (state != ExpansionState.Captured)
                {
                    Core.Log.Warning($"build: '{def.Id}' is {state}; it is not built");
                    continue;
                }
                var src = def.HasSource ? SourceCapture.Get(def.Source.Key) : null;
                if (def.HasSource && (src == null || src.Root == null))
                {
                    Core.Log.Error($"build: '{def.Id}' source '{def.Source.Key}' is not in the cache; it is not built");
                    Registry.SetState(def.Id, ExpansionState.BuildFailed);
                    continue;
                }
                try
                {
                    Layers.Apply(def);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"build: '{def.Id}' laying its layer over the definition failed: {e}");
                }
                var b = new BuiltExpansion(def, src);
                if (!Placement.Place(b, main))
                {
                    Registry.SetState(def.Id, ExpansionState.BuildFailed);
                    b.Teardown();
                    continue;
                }
                Builds.Add(new Build { Expansion = b, Npc = new NpcNavigation(), Vehicles = new VehicleNavigation(def.Id) });
            }
            if (Builds.Count == 0)
            {
                Core.Log.Msg("build: no expansion placed");
                return;
            }

            Physics.SyncTransforms();
            var built = Builds.Select(x => x.Expansion).ToList();
            var index = SceneIndex.Build(main, built);
            Index = index;
            foreach (var x in Builds.ToList())
            {
                var b = x.Expansion;
                try
                {
                    BuildWorld(b, index, built);
                    Places.Build(b);
                    WeatherCover.Add(b);
                    SeparateViews.Add(b, index);
                    Properties.Build(b);
                    PoliceStations.Build(b);
                    MainCopies.Build(b, b.Definition.Copies, "expansion", Layers.ExpansionOwner);
                    Shops.Build(b);
                    Registry.SetState(b.Id, ExpansionState.Placed);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"build: '{b.Id}' failed while building the world: {e}");
                    Registry.SetState(b.Id, ExpansionState.BuildFailed);
                }
            }
            PhysicsBounds.Extend(built);
            _saveDataLoaded = false;
            _navRoutine = MelonCoroutines.Start(JoinNavigation());
        }

        private static void BuildWorld(BuiltExpansion b, SceneIndex index, IReadOnlyList<BuiltExpansion> all)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var def = b.Definition;
            var offColliders = new HashSet<IntPtr>();
            var holes = new TerrainOps.HoleMask(b);

            var routePoints = new List<Vector3>();
            foreach (var c in def.Connections)
                foreach (var p in c.Route)
                    routePoints.Add(b.Frame.Resolve(p.Point));
            if (routePoints.Count > 0) ColliderKeepAlive.KeepNear(routePoints, 40f, $"'{b.Id}' connections");

            // The connections resolve their materials from renderers that the object rules may hide afterwards.
            foreach (var c in def.Connections)
            {
                var conn = TunnelBuilder.Build(b, c, index);
                if (conn != null)
                {
                    b.Connections.Add(conn);
                    TunnelShadows.Add(conn);
                    SlopeAssist.Add(conn);
                }
                else Core.Log.Error($"build: '{b.Id}' connection {c.Name} was not built");
            }

            ObjectRules.Apply(b, index, def.Hide, offColliders, holes);
            ObjectRules.GroundPlates(b, index, offColliders);
            ObjectRules.YieldToMainTerrains(b, index, holes);
            Physics.SyncTransforms();
            foreach (var conn in b.Connections) Clearing.Clear(b, conn, index, all, holes, offColliders);
            int cells = holes.Apply();
            int detached = ColliderKeepAlive.Detach(offColliders);
            ObjectRules.CheckHeights(b, index);
            Physics.SyncTransforms();
            Core.Log.Msg($"build: '{b.Id}' world built in {sw.ElapsedMilliseconds} ms: {b.Connections.Count} connections, " +
                         $"{offColliders.Count} colliders off ({detached} taken out of collider groups), {cells} terrain cells cut");
        }

        /// <summary>
        /// Game time between onLoadComplete and the vehicle scan. Mods place their buildings and props, which the scan must
        /// see as obstacles, within the first second of game time after the save has loaded. The host's loading screen
        /// waits two seconds of game time, and a long frame counts for little of that wait, so the scan still runs under it.
        /// </summary>
        private const float WorldSettleSeconds = 1.5f;

        private static bool _saveDataLoaded, _listensToLoad;

        /// <summary>
        /// The vanilla graphs exist only once the save has loaded, and the vehicle scan holds the main thread for seconds
        /// while it gathers its input, so it runs under the loading screen, where no player sees it stop the game.
        /// NPC navigation bakes over many frames once the game has loaded; then the vehicles get the scanned graphs and
        /// the expansion turns Ready, so a car waiting for its roads starts with everything else in place.
        /// </summary>
        private static IEnumerator JoinNavigation()
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var lm = Singleton<LoadManager>.Instance;
                if (lm != null && !_listensToLoad)
                {
                    lm.onLoadComplete.AddListener((UnityAction)new Action(() => _saveDataLoaded = true));
                    _listensToLoad = true;
                }
                if (_saveDataLoaded) break;
                if (lm != null && lm.IsGameLoaded && !lm.IsLoading)
                {
                    Core.Log.Warning("build: the game loaded with no load-complete event");
                    break;
                }
                if (wait.Elapsed.TotalMinutes > 5)
                {
                    Core.Log.Error("build: the game did not finish loading within 5 minutes; navigation is not joined");
                    foreach (var x in Builds) Registry.SetState(x.Expansion.Id, ExpansionState.BuildFailed);
                    yield break;
                }
                yield return null;
            }
            yield return new WaitForSeconds(WorldSettleSeconds);
            var loading = Singleton<LoadManager>.Instance;
            if (loading != null && loading.IsGameLoaded && !loading.IsLoading)
                Core.Log.Warning("build: the loading screen closed before the vehicle scan; the player sees its long frame");
            Core.Log.Msg($"build: save data loaded after {wait.ElapsedMilliseconds} ms; scanning the vehicle graphs");
            foreach (var x in Builds.ToList())
            {
                var b = x.Expansion;
                if (Registry.GetState(b.Id) != ExpansionState.Placed || !b.Definition.HasSource) continue;
                yield return Guard(x.Vehicles.Build(b), $"'{b.Id}' vehicle navigation", () => x.NavigationFailed = true);
            }

            while (true)
            {
                var lm = Singleton<LoadManager>.Instance;
                if (lm != null && lm.IsGameLoaded && !lm.IsLoading) break;
                if (wait.Elapsed.TotalMinutes > 5)
                {
                    Core.Log.Error("build: the game did not finish loading within 5 minutes; NPC navigation is not joined");
                    foreach (var x in Builds) Registry.SetState(x.Expansion.Id, ExpansionState.BuildFailed);
                    yield break;
                }
                yield return null;
            }
            for (int i = 0; i < 3; i++) yield return null;
            Core.Log.Msg($"build: game loaded after {wait.ElapsedMilliseconds} ms; joining NPC navigation");

            foreach (var x in Builds.ToList())
            {
                var b = x.Expansion;
                if (Registry.GetState(b.Id) != ExpansionState.Placed)
                {
                    Core.Log.Warning($"build: '{b.Id}' is {Registry.GetState(b.Id)}; navigation is not joined");
                    continue;
                }
                if (!b.Definition.HasSource)
                {
                    // An edit layer brings no ground of its own to drive or walk on.
                    Core.Log.Msg($"build: '{b.Id}' has no source scene; no navigation to join");
                    Registry.SetState(b.Id, ExpansionState.Ready);
                    MapExpansionsApi.RaiseReady(b.Id);
                    continue;
                }
                try
                {
                    x.Npc.AddSourceData(b);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"build: '{b.Id}' adding the NPC navigation data failed: {e}");
                    x.NavigationFailed = true;
                }
                foreach (var conn in b.Connections)
                    yield return Guard(x.Npc.BakeConnection(b, conn), $"'{b.Id}' {conn.Name} NPC bake", () => x.NavigationFailed = true);

                try
                {
                    x.Vehicles.Open(b);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"build: '{b.Id}' opening the vehicle navigation failed: {e}");
                    x.NavigationFailed = true;
                }

                if (x.NavigationFailed)
                {
                    Registry.SetState(b.Id, ExpansionState.BuildFailed);
                    continue;
                }
                Registry.SetState(b.Id, ExpansionState.Ready);
                MapExpansionsApi.RaiseReady(b.Id);
            }
            _navRoutine = null;
        }

        /// <summary>Runs a step to its end; an exception ends only that step and is logged.</summary>
        private static IEnumerator Guard(IEnumerator inner, string what, Action onFail)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) yield break;
                    current = inner.Current;
                }
                catch (Exception e)
                {
                    Core.Log.Error($"build: {what} failed: {e}");
                    onFail();
                    yield break;
                }
                yield return current;
            }
        }

        public static void OnMainUnloaded()
        {
            if (Builds.Count == 0 && RegionRegistry.All.Count == 0) return;
            Teardown();
        }

        private static void Teardown()
        {
            if (_navRoutine != null) MelonCoroutines.Stop(_navRoutine);
            _navRoutine = null;
            NavLinks.Clear();
            ObjectRules.Clear();
            Index = null;
            Properties.Clear();
            PoliceStations.Clear();
            Shops.Clear();
            MainCopies.Clear();
            TunnelShadows.Clear();
            WeatherCover.Clear();
            SeparateViews.Clear();
            PhoneMap.Reset();
            SlopeAssist.Clear();
            PhysicsBounds.Restore("teardown");
            foreach (var x in Builds)
            {
                var id = x.Expansion.Id;
                try
                {
                    x.Vehicles.Teardown();
                    x.Npc.Teardown(id);
                    x.Expansion.Teardown();
                }
                catch (Exception e)
                {
                    Core.Log.Error($"teardown: '{id}' failed: {e}");
                }
                if (Registry.GetState(id) != ExpansionState.Rejected) Registry.SetState(id, ExpansionState.Captured);
            }
            Builds.Clear();
            RegionRegistry.Teardown();
            Core.Log.Msg("teardown: main world left; all expansions removed");
        }
    }
}
