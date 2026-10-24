using System.Collections;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using MelonLoader;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MapExpansions.Source
{
    /// <summary>
    /// Takes the original world geometry out of the prologue scene while the main menu is open.
    ///
    /// The prologue scene is a complete second game world with its own managers, network objects and a Map
    /// singleton. In the menu none of the main-world singletons exist yet, so its duplicates either destroy
    /// themselves or become short-lived instances that are cleaned up when the scene unloads. The capture loads
    /// the scene additively, switches every root off in the sceneLoaded callback (before Start runs), clones the
    /// "Map" root into an inactive container that survives scene changes, strips every component that is not a
    /// plain Unity rendering, collision or light component from the clone, and unloads the scene again.
    ///
    /// Clones live under an inactive parent, so none of their components ever run Awake or OnEnable.
    /// </summary>
    internal static class SourceCapture
    {
        public const string SourceScene = "Tutorial";
        private const string SourceRoot = "Map";

        /// <summary>True from the start of a capture until its scene is unloaded; save loading waits for it.</summary>
        public static bool Busy { get; private set; }

        /// <summary>The inactive container in DontDestroyOnLoad that holds the captured geometry.</summary>
        public static GameObject Cache { get; private set; }

        /// <summary>Baked navigation data of the source scene, kept alive for the navigation spike.</summary>
        public static readonly List<NavSource> NavMeshes = new();

        public sealed class NavSource
        {
            public NavMeshData Data;
            public int AgentTypeId;
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        /// <summary>Component types a clone keeps. Everything else is game logic, networking or editor data.</summary>
        private static readonly HashSet<string> Keep = new(StringComparer.Ordinal)
        {
            "UnityEngine.Transform",
            "UnityEngine.RectTransform",
            "UnityEngine.MeshFilter",
            "UnityEngine.MeshRenderer",
            "UnityEngine.LODGroup",
            "UnityEngine.BoxCollider",
            "UnityEngine.MeshCollider",
            "UnityEngine.SphereCollider",
            "UnityEngine.CapsuleCollider",
            "UnityEngine.Terrain",
            "UnityEngine.TerrainCollider",
            "UnityEngine.Light",
            "UnityEngine.ReflectionProbe",
            "UnityEngine.Rendering.Universal.UniversalAdditionalLightData",
        };

        /// <summary>
        /// One converted delegate for the whole session. remove_logMessageReceived only removes an instance equal to
        /// the one that was added, and every implicit conversion from a managed Action creates a new one.
        /// </summary>
        private static Application.LogCallback _logHandler;
        private static readonly Dictionary<string, int> SourceLog = new(StringComparer.Ordinal);
        private static int _sourceErrors, _sourceWarnings, _sourceExceptions;
        private static bool _rootsSwitchedOffInCallback;

        public static void OnMenuReady()
        {
            if (Busy)
            {
                Core.Log.Msg("capture: menu reloaded while a capture runs; the running capture continues");
                return;
            }
            if (Cache != null)
            {
                Core.Log.Msg($"capture: cache already present ({Cache.transform.childCount} roots), nothing to do");
                return;
            }
            int cycles = Core.CaptureCycles.Value;
#if DEBUG
            // Repeatability test from a test script without editing the preferences file of the slot.
            if (int.TryParse(Environment.GetEnvironmentVariable("MAPEXPANSIONS_CAPTURE_CYCLES"), out int envCycles))
            {
                Core.Log.Msg($"capture: MAPEXPANSIONS_CAPTURE_CYCLES={envCycles} overrides CaptureCycles={cycles}");
                cycles = envCycles;
            }
#endif
            MelonCoroutines.Start(Run(Math.Max(1, cycles)));
        }

        /// <summary>
        /// Called from Unity's sceneLoaded callback. Unity runs Awake and OnEnable before this callback and Start
        /// after it, so switching the roots off here keeps every Start and Update of the source scene from running.
        /// </summary>
        public static void OnSceneLoaded(string sceneName)
        {
            if (!Busy || sceneName != SourceScene) return;
            var scene = SceneManager.GetSceneByName(SourceScene);
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!root.activeSelf) continue;
                root.SetActive(false);
                n++;
            }
            _rootsSwitchedOffInCallback = true;
            Core.Log.Msg($"capture: switched off {n} source roots in sceneLoaded");
        }

        private static IEnumerator Run(int cycles)
        {
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                Busy = true;
                Exception failure = null;
                IEnumerator inner = Cycle(cycle, cycles);
                while (true)
                {
                    object current;
                    try
                    {
                        if (!inner.MoveNext()) break;
                        current = inner.Current;
                    }
                    catch (Exception e)
                    {
                        failure = e;
                        break;
                    }
                    yield return current;
                }
                Busy = false;
                if (failure != null)
                {
                    Core.Log.Error($"capture cycle {cycle}/{cycles} failed: {failure}");
                    LoadGate.Release();
                    yield break;
                }
                if (cycle < cycles && Cache != null)
                {
                    Object.Destroy(Cache);
                    Cache = null;
                    NavMeshes.Clear();
                    yield return null;
                }
            }
            LoadGate.Release();
        }

        private static IEnumerator Cycle(int cycle, int cycles)
        {
            var total = Stopwatch.StartNew();
            Core.Log.Msg($"capture cycle {cycle}/{cycles}: begin");
            UnityEngine.Debug.Log($"[MapExpansions] capture cycle {cycle} begin");
            var before = WorldSnapshot.Take($"c{cycle}.before");
            if (cycle == 1) before.LogAll();
            long memBefore = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();

            StartSourceLog();
            var guidsBefore = RegisteredGuids();
            _rootsSwitchedOffInCallback = false;
            var t = Stopwatch.StartNew();
            var load = SceneManager.LoadSceneAsync(SourceScene, LoadSceneMode.Additive);
            if (load == null)
                throw new InvalidOperationException($"LoadSceneAsync('{SourceScene}') returned null; the scene is not in the build");
            while (!load.isDone) yield return null;
            long loadMs = t.ElapsedMilliseconds;

            var scene = SceneManager.GetSceneByName(SourceScene);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"source scene '{SourceScene}' is not loaded after LoadSceneAsync");
            if (!_rootsSwitchedOffInCallback)
                Core.Log.Warning("capture: sceneLoaded callback did not switch the roots off; Start may have run on source objects");

            var loaded = WorldSnapshot.Take($"c{cycle}.loaded");
            loaded.LogDiff(before);

            GameObject sourceMap = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                root.SetActive(false);
                Core.Log.Msg($"capture: source root '{root.name}' children={root.transform.childCount}");
                if (root.name == SourceRoot) sourceMap = root;
            }
            if (sourceMap == null)
                throw new InvalidOperationException($"source root '{SourceRoot}' missing in scene '{SourceScene}'");

            CollectNavMeshes(scene);
            LogRendererState("source", sourceMap);

            t.Restart();
            Cache = new GameObject("MapExpansions.SourceCache");
            Cache.SetActive(false);
            Object.DontDestroyOnLoad(Cache);
            var clone = Object.Instantiate(sourceMap, Cache.transform);
            clone.name = SourceRoot;
            clone.SetActive(true);
            long cloneMs = t.ElapsedMilliseconds;

            t.Restart();
            Strip(clone);
            long stripMs = t.ElapsedMilliseconds;

            t.Restart();
            var unload = SceneManager.UnloadSceneAsync(scene);
            if (unload == null)
                throw new InvalidOperationException("UnloadSceneAsync returned null");
            while (!unload.isDone) yield return null;
            var unused = Resources.UnloadUnusedAssets();
            while (!unused.isDone) yield return null;
            long unloadMs = t.ElapsedMilliseconds;
            StopSourceLog();
            ReleaseSourceGuids(guidsBefore);

            VerifyCache(clone);
            var after = WorldSnapshot.Take($"c{cycle}.after");
            int diffs = after.LogDiff(before);
            long memAfter = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();

            Core.Log.Msg($"capture cycle {cycle}/{cycles}: done in {total.ElapsedMilliseconds} ms (load {loadMs}, clone {cloneMs}, strip {stripMs}, unload {unloadMs}); " +
                         $"global differences after unload: {diffs}; allocated memory {memBefore / 1048576} -> {memAfter / 1048576} MB; " +
                         $"source log: {_sourceErrors} errors, {_sourceExceptions} exceptions, {_sourceWarnings} warnings");
            UnityEngine.Debug.Log($"[MapExpansions] capture cycle {cycle} end");
        }

        private static void CollectNavMeshes(Scene scene)
        {
            NavMeshes.Clear();
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var s in root.GetComponentsInChildren<NavMeshSurface>(true))
                {
                    if (s.navMeshData == null)
                    {
                        Core.Log.Warning($"capture: NavMeshSurface '{s.name}' has no baked data");
                        continue;
                    }
                    // Scene assets without a scene reference are freed by UnloadUnusedAssets; the clone holds none.
                    s.navMeshData.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    var b = s.navMeshData.sourceBounds;
                    NavMeshes.Add(new NavSource
                    {
                        Data = s.navMeshData,
                        AgentTypeId = s.agentTypeID,
                        Name = s.name,
                        Position = s.navMeshData.position,
                        Rotation = s.navMeshData.rotation,
                    });
                    Core.Log.Msg($"capture: navmesh '{s.name}' agent={NavMesh.GetSettingsNameFromID(s.agentTypeID)} ({s.agentTypeID}) " +
                                 $"bounds center={b.center} size={b.size} pos={s.navMeshData.position}");
                }
            }
        }

        private static void Strip(GameObject clone)
        {
            var removed = new Dictionary<string, int>(StringComparer.Ordinal);
            int remaining = 0;
            for (int pass = 1; pass <= 5; pass++)
            {
                var all = clone.GetComponentsInChildren<Component>(true);
                remaining = 0;
                int removedThisPass = 0;
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    var c = all[i];
                    if (c == null) continue;
                    string type = c.GetIl2CppType().FullName;
                    if (Keep.Contains(type)) continue;
                    Object.DestroyImmediate(c);
                    if (c == null)
                    {
                        removed.TryGetValue(type, out int n);
                        removed[type] = n + 1;
                        removedThisPass++;
                    }
                    else remaining++;
                }
                if (remaining == 0 || removedThisPass == 0) break;
            }
            int total = removed.Values.Sum();
            Core.Log.Msg($"capture: stripped {total} components of {removed.Count} types; {remaining} could not be removed");
            foreach (var kv in removed.OrderByDescending(k => k.Value).Take(40))
                Core.Log.Msg($"capture:   removed {kv.Value,6} x {kv.Key}");
            if (remaining > 0)
                Core.Log.Warning($"capture: {remaining} non-whitelisted components remain on the clone");
        }

        private static (int renderers, int noMesh, int noMaterial, int batched) CountRenderers(GameObject root)
        {
            int renderers = 0, noMesh = 0, noMaterial = 0, batched = 0;
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderers++;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) noMesh++;
                if (mr.sharedMaterial == null) noMaterial++;
                if (mr.isPartOfStaticBatch) batched++;
            }
            return (renderers, noMesh, noMaterial, batched);
        }

        /// <summary>Renderer state of the original, so the cache check can tell inherited gaps from capture losses.</summary>
        private static (int renderers, int noMesh, int noMaterial, int batched) _sourceRenderers;

        private static void LogRendererState(string label, GameObject root)
        {
            var r = CountRenderers(root);
            _sourceRenderers = r;
            Core.Log.Msg($"capture: {label} renderers={r.renderers} (no mesh {r.noMesh}, no material {r.noMaterial}, static batch {r.batched})");
        }

        private static HashSet<string> RegisteredGuids()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var list = Il2Cpp.GUIDManager.registeredGUIDs;
            for (int i = 0; i < list.Count; i++) set.Add(list[i].ToString());
            return set;
        }

        /// <summary>
        /// Source objects register their GUIDs in Awake and do not always deregister when the scene unloads.
        /// Vanilla clears the manager only in LoadManager.CleanUp, so the leftovers would collide with the next
        /// capture and with the main world's own objects.
        /// </summary>
        private static void ReleaseSourceGuids(HashSet<string> before)
        {
            var list = Il2Cpp.GUIDManager.registeredGUIDs;
            var map = Il2Cpp.GUIDManager.guidToObject;
            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var g = list[i];
                if (before.Contains(g.ToString())) continue;
                list.RemoveAt(i);
                map.Remove(g);
                removed++;
            }
            if (removed > 0)
                Core.Log.Msg($"capture: released {removed} GUIDs the source scene left registered ({list.Count} remain)");
            else
                Core.Log.Msg($"capture: source scene left no GUIDs registered ({list.Count} registered)");
        }

        private static void VerifyCache(GameObject clone)
        {
            var (renderers, noMesh, noMaterial, batched) = CountRenderers(clone);
            int terrains = 0, noTerrainData = 0;
            foreach (var tr in clone.GetComponentsInChildren<Terrain>(true))
            {
                terrains++;
                if (tr.terrainData == null) noTerrainData++;
            }
            int meshColliders = 0, noColliderMesh = 0;
            foreach (var mc in clone.GetComponentsInChildren<MeshCollider>(true))
            {
                meshColliders++;
                if (mc.sharedMesh == null) noColliderMesh++;
            }
            int navAlive = NavMeshes.Count(n => n.Data != null);
            string line = $"capture: cache renderers={renderers} (no mesh {noMesh}, no material {noMaterial}, static batch {batched}), " +
                          $"terrains={terrains} (no data {noTerrainData}), meshColliders={meshColliders} (no mesh {noColliderMesh}), " +
                          $"navmesh data alive {navAlive}/{NavMeshes.Count}";
            var src = _sourceRenderers;
            if (renderers < src.renderers || noMesh > src.noMesh || noMaterial > src.noMaterial
                || noTerrainData + noColliderMesh > 0 || navAlive < NavMeshes.Count)
                Core.Log.Warning(line);
            else
                Core.Log.Msg(line);
        }

        private static void StartSourceLog()
        {
            SourceLog.Clear();
            _sourceErrors = _sourceWarnings = _sourceExceptions = 0;
            _logHandler ??= new Action<string, string, LogType>(OnUnityLog);
            Application.add_logMessageReceived(_logHandler);
        }

        private static void StopSourceLog()
        {
            Application.remove_logMessageReceived(_logHandler);
            foreach (var kv in SourceLog.OrderByDescending(k => k.Value).Take(40))
                Core.Log.Msg($"capture: source log x{kv.Value}: {kv.Key}");
        }

        private static void OnUnityLog(string message, string stack, LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Assert: _sourceErrors++; break;
                case LogType.Exception: _sourceExceptions++; break;
                case LogType.Warning: _sourceWarnings++; break;
                default: return;
            }
            string key = $"[{type}] " + (message.Length > 160 ? message.Substring(0, 160) : message);
            SourceLog.TryGetValue(key, out int n);
            SourceLog[key] = n + 1;
        }
    }
}
