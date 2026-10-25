using System.Collections;
using System.Diagnostics;
using Il2CppInterop.Runtime;
using Il2CppPathfinding;
using MapExpansions.Lifecycle;
using MelonLoader;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MapExpansions.Source
{
    /// <summary>The content of one source scene, kept for the whole session.</summary>
    internal sealed class CapturedSource
    {
        public string Key;
        public string SceneName;

        /// <summary>Clone of the source root, inactive inside the cache.</summary>
        public GameObject Root;

        public readonly List<NavSource> NavMeshes = new();

        /// <summary>Road node positions in source coordinates, in scene order.</summary>
        public readonly List<Vector3> RoadNodes = new();

        /// <summary>Road links between two entries of <see cref="RoadNodes"/>, by index, as the source scene sets them.</summary>
        public readonly List<(int From, int To, bool OneWay)> RoadLinks = new();

        /// <summary>The source scene's characters, by object name.</summary>
        public readonly List<SourceCharacter> Characters = new();
    }

    internal sealed class NavSource
    {
        public NavMeshData Data;
        public int AgentTypeId;
        public string Name;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    /// <summary>
    /// Phase 2 of the load: takes the original world geometry out of each source scene while the main menu is open.
    ///
    /// A source scene is a complete second game world with its own managers, network objects and a Map singleton.
    /// In the menu none of the main-world singletons exist yet, so its duplicates either destroy themselves or become
    /// short-lived instances that are cleaned up when the scene unloads. The capture loads the scene additively,
    /// switches every root off in the sceneLoaded callback (before Start runs), clones the root into an inactive
    /// container that survives scene changes, strips every component that is not a plain Unity rendering, collision
    /// or light component from the clone, and unloads the scene again. Each distinct scene and root is captured once,
    /// however many expansions use it.
    ///
    /// Clones live under an inactive parent, so none of their components ever run Awake or OnEnable.
    /// </summary>
    internal static class SourceCapture
    {
        /// <summary>True from the start of a capture until its scene is unloaded; save loading waits for it.</summary>
        public static bool Busy { get; private set; }

        /// <summary>The inactive container in DontDestroyOnLoad that holds every captured root.</summary>
        public static GameObject Cache { get; private set; }

        private static readonly Dictionary<string, CapturedSource> Captured = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Failed = new(StringComparer.Ordinal);

        public static CapturedSource Get(string key) => Captured.TryGetValue(key, out var s) ? s : null;

        public static IEnumerable<CapturedSource> All => Captured.Values;

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
        private static string _loading;

        public static void OnMenuReady()
        {
            if (Busy)
            {
                Core.Log.Msg("capture: menu reloaded while a capture runs; the running capture continues");
                return;
            }
            Registry.CheckScenes();
            var pending = new List<WorldSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var def in Registry.Usable())
            {
                string key = def.Source.Key;
                if (Captured.ContainsKey(key))
                {
                    Registry.SetState(def.Id, ExpansionState.Captured);
                    continue;
                }
                if (Failed.Contains(key))
                {
                    Registry.SetState(def.Id, ExpansionState.CaptureFailed);
                    continue;
                }
                if (seen.Add(key)) pending.Add(def.Source);
            }
            if (pending.Count == 0)
            {
                Core.Log.Msg($"capture: nothing to capture ({Captured.Count} sources cached, {Registry.Ids.Count} expansions registered)");
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
            Busy = true;
            MelonCoroutines.Start(Run(pending, Math.Max(1, cycles)));
        }

        /// <summary>
        /// Called from Unity's sceneLoaded callback. Unity runs Awake and OnEnable before this callback and Start
        /// after it, so switching the roots off here keeps every Start and Update of the source scene from running.
        /// </summary>
        public static void OnSceneLoaded(string sceneName)
        {
            if (!Busy || sceneName != _loading) return;
            var scene = SceneManager.GetSceneByName(sceneName);
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!root.activeSelf) continue;
                root.SetActive(false);
                n++;
            }
            _rootsSwitchedOffInCallback = true;
            Core.Log.Msg($"capture: switched off {n} roots of '{sceneName}' in sceneLoaded");
        }

        private static IEnumerator Run(List<WorldSource> sources, int cycles)
        {
            Busy = true;
            if (Cache == null)
            {
                Cache = new GameObject("MapExpansions.SourceCache");
                Cache.SetActive(false);
                Object.DontDestroyOnLoad(Cache);
            }
            foreach (var src in sources)
            {
                for (int cycle = 1; cycle <= cycles; cycle++)
                {
                    Exception failure = null;
                    IEnumerator inner = Cycle(src, cycle, cycles);
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
                    if (failure != null)
                    {
                        Core.Log.Error($"capture: '{src.Key}' cycle {cycle}/{cycles} failed: {failure}");
                        StopSourceLog();
                        Failed.Add(src.Key);
                        if (Captured.TryGetValue(src.Key, out var partial))
                        {
                            if (partial.Root != null) Object.Destroy(partial.Root);
                            Captured.Remove(src.Key);
                        }
                        break;
                    }
                    if (cycle < cycles && Captured.TryGetValue(src.Key, out var done))
                    {
                        Object.Destroy(done.Root);
                        Captured.Remove(src.Key);
                        yield return null;
                    }
                }
                _loading = null;
            }
            foreach (var def in Registry.Usable())
                Registry.SetState(def.Id, Captured.ContainsKey(def.Source.Key) ? ExpansionState.Captured : ExpansionState.CaptureFailed);
            Busy = false;
            LoadGate.Release();
        }

        private static IEnumerator Cycle(WorldSource src, int cycle, int cycles)
        {
            var total = Stopwatch.StartNew();
            string sceneName = src.SceneName;
            Core.Log.Msg($"capture '{src.Key}' cycle {cycle}/{cycles}: begin");
            var before = WorldSnapshot.Take($"c{cycle}.before");
            if (cycle == 1) before.LogAll();
            long memBefore = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();

            StartSourceLog();
            var guidsBefore = RegisteredGuids();
            _rootsSwitchedOffInCallback = false;
            _loading = sceneName;
            var t = Stopwatch.StartNew();
            var load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            if (load == null)
                throw new InvalidOperationException($"LoadSceneAsync('{sceneName}') returned null; the scene is not in the build");
            while (!load.isDone) yield return null;
            long loadMs = t.ElapsedMilliseconds;

            var scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"source scene '{sceneName}' is not loaded after LoadSceneAsync");
            if (!_rootsSwitchedOffInCallback)
                Core.Log.Warning("capture: sceneLoaded callback did not switch the roots off; Start may have run on source objects");

            var loaded = WorldSnapshot.Take($"c{cycle}.loaded");
            loaded.LogDiff(before);

            GameObject sourceRoot = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                root.SetActive(false);
                Core.Log.Msg($"capture: source root '{root.name}' children={root.transform.childCount}");
                if (root.name == src.RootName) sourceRoot = root;
            }
            if (sourceRoot == null)
                throw new InvalidOperationException($"source root '{src.RootName}' missing in scene '{sceneName}'");

            var captured = new CapturedSource { Key = src.Key, SceneName = sceneName };
            CollectNavMeshes(scene, captured);
            CollectRoadNodes(scene, src, captured);
            CollectCharacters(scene, captured);
            var sourceRenderers = CountRenderers(sourceRoot);
            Core.Log.Msg($"capture: source renderers={sourceRenderers.renderers} (no mesh {sourceRenderers.noMesh}, " +
                         $"no material {sourceRenderers.noMaterial}, static batch {sourceRenderers.batched})");

            t.Restart();
            var clone = Object.Instantiate(sourceRoot, Cache.transform);
            clone.name = src.RootName;
            clone.SetActive(true);
            captured.Root = clone;
            Captured[src.Key] = captured;
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

            VerifyCache(captured, sourceRenderers);
            var after = WorldSnapshot.Take($"c{cycle}.after");
            int diffs = after.LogDiff(before);
            long memAfter = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();

            Core.Log.Msg($"capture '{src.Key}' cycle {cycle}/{cycles}: done in {total.ElapsedMilliseconds} ms (load {loadMs}, clone {cloneMs}, strip {stripMs}, unload {unloadMs}); " +
                         $"global differences after unload: {diffs}; allocated memory {memBefore / 1048576} -> {memAfter / 1048576} MB; " +
                         $"source log: {_sourceErrors} errors, {_sourceExceptions} exceptions, {_sourceWarnings} warnings");
        }

        private static void CollectNavMeshes(Scene scene, CapturedSource captured)
        {
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
                    captured.NavMeshes.Add(new NavSource
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
            if (captured.NavMeshes.Count == 0)
                Core.Log.Warning($"capture: '{captured.Key}' has no baked NavMesh; NPCs cannot walk in the expansion");
        }

        private static void CollectRoadNodes(Scene scene, WorldSource src, CapturedSource captured)
        {
            if (string.IsNullOrEmpty(src.RoadNodesName))
            {
                Core.Log.Msg($"capture: '{src.Key}' takes no road nodes");
                return;
            }
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != src.RoadNodesName) continue;
                    for (int i = 0; i < t.childCount; i++) captured.RoadNodes.Add(t.GetChild(i).position);
                    int open = 0;
                    for (int i = 0; i < t.childCount; i++)
                        foreach (var link in t.GetChild(i).GetComponents<NodeLink>())
                        {
                            int to = link.end != null && link.end.parent == t ? link.end.GetSiblingIndex() : -1;
                            if (to < 0 || to == i) { open++; continue; }
                            captured.RoadLinks.Add((i, to, link.oneWay));
                        }
                    Core.Log.Msg($"capture: {t.childCount} road nodes and {captured.RoadLinks.Count} road links under '{t.name}' (root '{root.name}')");
                    if (open > 0) Core.Log.Warning($"capture: {open} road links under '{t.name}' end outside it and are left out");
                    return;
                }
            }
            Core.Log.Warning($"capture: no object '{src.RoadNodesName}' in '{src.SceneName}'; vehicles in the expansion find no road node nearby");
        }

        /// <summary>
        /// Keeps each character's data and the assets it points to past the scene unload. The scene's NPCs never wake,
        /// so the data is read from their data assets directly.
        /// </summary>
        private static void CollectCharacters(Scene scene, CapturedSource captured)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var npc in root.GetComponentsInChildren<Il2CppScheduleOne.NPCs.NPC>(true))
                {
                    var dataObject = npc._defaultNPCData;
                    var data = dataObject?.GetOriginalData();
                    if (data?.BasicInfo == null)
                    {
                        Core.Log.Warning($"capture: character '{npc.name}' in '{captured.SceneName}' has no data asset; it is left out");
                        continue;
                    }
                    KeepLoaded(dataObject);
                    var look = data.Appearance;
                    if (look != null)
                    {
                        KeepLoaded(look.DefaultAppearance);
                        KeepLoaded(look.DefaultOutfit);
                        KeepLoaded(look.Impostor);
                        KeepLoaded(look.Mugshot);
                        if (look.Mugshot != null) KeepLoaded(look.Mugshot.texture);
                    }
                    KeepLoaded(data.Voice?.VoiceDatabase);
                    captured.Characters.Add(new SourceCharacter
                    {
                        ObjectName = npc.name,
                        Id = data.BasicInfo.ID,
                        FirstName = data.BasicInfo.FirstName,
                        LastName = data.BasicInfo.HasLastName ? data.BasicInfo.LastName : string.Empty,
                        Mugshot = look?.Mugshot,
                        Data = dataObject,
                    });
                }
            }
            Core.Log.Msg($"capture: {captured.Characters.Count} characters in '{captured.SceneName}': " +
                         string.Join(", ", captured.Characters.Select(c => $"{c.ObjectName} ({c.Id}, look {(c.Data.GetOriginalData().Appearance?.DefaultAppearance != null ? "yes" : "no")})")));
        }

        private static void KeepLoaded(Object o)
        {
            if (o != null) o.hideFlags |= HideFlags.DontUnloadUnusedAsset;
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

        private static void VerifyCache(CapturedSource captured, (int renderers, int noMesh, int noMaterial, int batched) src)
        {
            var clone = captured.Root;
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
            int navAlive = captured.NavMeshes.Count(n => n.Data != null);
            string line = $"capture: cache '{captured.Key}' renderers={renderers} (no mesh {noMesh}, no material {noMaterial}, static batch {batched}), " +
                          $"terrains={terrains} (no data {noTerrainData}), meshColliders={meshColliders} (no mesh {noColliderMesh}), " +
                          $"navmesh data alive {navAlive}/{captured.NavMeshes.Count}, road nodes {captured.RoadNodes.Count}";
            if (renderers < src.renderers || noMesh > src.noMesh || noMaterial > src.noMaterial
                || noTerrainData + noColliderMesh > 0 || navAlive < captured.NavMeshes.Count)
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
            if (_logHandler == null) return;
            Application.remove_logMessageReceived(_logHandler);
            foreach (var kv in SourceLog.OrderByDescending(k => k.Value).Take(40))
                Core.Log.Msg($"capture: source log x{kv.Value}: {kv.Key}");
            SourceLog.Clear();
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
