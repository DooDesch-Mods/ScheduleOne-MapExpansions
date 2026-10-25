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
        }

        private static readonly List<Build> Builds = new();
        private static object _navRoutine;

        public static BuiltExpansion Get(string id) => Builds.FirstOrDefault(x => x.Expansion.Id == id)?.Expansion;

        public static IReadOnlyList<BuiltExpansion> All => Builds.Select(x => x.Expansion).ToList();

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
                var src = SourceCapture.Get(def.Source.Key);
                if (src == null || src.Root == null)
                {
                    Core.Log.Error($"build: '{def.Id}' source '{def.Source.Key}' is not in the cache; it is not built");
                    Registry.SetState(def.Id, ExpansionState.BuildFailed);
                    continue;
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
                    MainCopies.Build(b, b.Definition.Copies, "expansion");
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

            ObjectRules.Apply(b, index, def.Hide, offColliders);
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

        /// <summary>Navigation joins the vanilla graphs, which exist only once the save has loaded.</summary>
        private static IEnumerator JoinNavigation()
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var lm = Singleton<LoadManager>.Instance;
                if (lm != null && lm.IsGameLoaded && !lm.IsLoading) break;
                if (wait.Elapsed.TotalMinutes > 5)
                {
                    Core.Log.Error("build: the game did not finish loading within 5 minutes; navigation is not joined");
                    foreach (var x in Builds) Registry.SetState(x.Expansion.Id, ExpansionState.BuildFailed);
                    yield break;
                }
                yield return null;
            }
            for (int i = 0; i < 3; i++) yield return null;
            Core.Log.Msg($"build: game loaded after {wait.ElapsedMilliseconds} ms; joining navigation");

            foreach (var x in Builds.ToList())
            {
                var b = x.Expansion;
                if (Registry.GetState(b.Id) != ExpansionState.Placed)
                {
                    Core.Log.Warning($"build: '{b.Id}' is {Registry.GetState(b.Id)}; navigation is not joined");
                    continue;
                }
                bool failed = false;
                try
                {
                    x.Npc.AddSourceData(b);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"build: '{b.Id}' adding the NPC navigation data failed: {e}");
                    failed = true;
                }
                foreach (var conn in b.Connections)
                    yield return Guard(x.Npc.BakeConnection(b, conn), $"'{b.Id}' {conn.Name} NPC bake", () => failed = true);
                yield return Guard(x.Vehicles.Build(b), $"'{b.Id}' vehicle navigation", () => failed = true);

                if (failed)
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
            Properties.Clear();
            PoliceStations.Clear();
            Shops.Clear();
            MainCopies.Clear();
            TunnelShadows.Clear();
            WeatherCover.Clear();
            SeparateViews.Clear();
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
