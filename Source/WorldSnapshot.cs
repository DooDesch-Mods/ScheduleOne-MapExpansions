using Il2CppFishNet;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.NPCs;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Property = Il2CppScheduleOne.Property.Property;

namespace MapExpansions.Source
{
    /// <summary>
    /// Counts of global game state that loading a foreign scene could change: loaded scenes, objects kept across
    /// scene changes, network objects, cameras, audio, and the static registries the game fills in Awake.
    /// Two snapshots taken before and after a capture show what the capture left behind.
    /// </summary>
    internal sealed class WorldSnapshot
    {
        public readonly string Label;
        public readonly SortedDictionary<string, string> Values = new();

        private WorldSnapshot(string label) => Label = label;

        public static WorldSnapshot Take(string label)
        {
            var s = new WorldSnapshot(label);
            s.Put("scenes", SceneNames());
            s.Put("ddol.roots", DontDestroyOnLoadRoots());
            s.Put("networkObjects", Count<Il2CppFishNet.Object.NetworkObject>());
            s.Put("networkManagers", Count<Il2CppFishNet.Managing.NetworkManager>());
            s.Put("cameras", Count<Camera>());
            s.Put("audioListeners", Count<AudioListener>());
            s.Put("audioSources.playing", PlayingAudio());
            s.Put("map.instance", Safe(() => Singleton<Il2CppScheduleOne.Map.Map>.InstanceExists.ToString()));
            s.Put("npcManager.instance", Safe(() => NetworkSingleton<NPCManager>.InstanceExists.ToString()));
            s.Put("npcRegistry", Safe(() => NPCManager.NPCRegistry.Count.ToString()));
            s.Put("properties", Safe(() => Property.Properties.Count.ToString()));
            s.Put("guids", Safe(() => Il2Cpp.GUIDManager.registeredGUIDs.Count.ToString()));
            s.Put("net.server", Safe(() => InstanceFinder.IsServer.ToString()));
            s.Put("net.client", Safe(() => InstanceFinder.IsClient.ToString()));
            return s;
        }

        /// <summary>Writes every value that differs from <paramref name="before"/>; returns the number of differences.</summary>
        public int LogDiff(WorldSnapshot before)
        {
            int diffs = 0;
            foreach (var kv in Values)
            {
                before.Values.TryGetValue(kv.Key, out var old);
                if (old == kv.Value) continue;
                diffs++;
                Core.Log.Warning($"[snapshot] {before.Label} -> {Label}: {kv.Key} changed: '{old}' -> '{kv.Value}'");
            }
            if (diffs == 0)
                Core.Log.Msg($"[snapshot] {before.Label} -> {Label}: no difference in {Values.Count} values");
            return diffs;
        }

        public void LogAll()
        {
            foreach (var kv in Values)
                Core.Log.Msg($"[snapshot] {Label}: {kv.Key} = {kv.Value}");
        }

        private void Put(string key, string value) => Values[key] = value;

        private static string Safe(Func<string> read)
        {
            try { return read(); }
            catch (Exception e) { return "error: " + e.GetType().Name + ": " + e.Message; }
        }

        private static string SceneNames()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                names.Add(sc.name + (sc.isLoaded ? "" : "(loading)"));
            }
            return string.Join(",", names);
        }

        /// <summary>The DontDestroyOnLoad scene is not reachable by name; a probe object moved there exposes it.</summary>
        private static string DontDestroyOnLoadRoots()
        {
            var probe = new GameObject("MapExpansions.DdolProbe");
            try
            {
                Object.DontDestroyOnLoad(probe);
                var names = new List<string>();
                foreach (var go in probe.scene.GetRootGameObjects())
                    if (go != probe) names.Add(go.name);
                names.Sort(StringComparer.Ordinal);
                return names.Count + ": " + string.Join(",", names);
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        private static string Count<T>() where T : Object
        {
            try { return Object.FindObjectsOfType(Il2CppType.Of<T>(), true).Length.ToString(); }
            catch (Exception e) { return "error: " + e.Message; }
        }

        private static string PlayingAudio()
        {
            try
            {
                int n = 0;
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<AudioSource>(), false))
                {
                    var a = o.TryCast<AudioSource>();
                    if (a != null && a.isPlaying) n++;
                }
                return n.ToString();
            }
            catch (Exception e) { return "error: " + e.Message; }
        }
    }
}
