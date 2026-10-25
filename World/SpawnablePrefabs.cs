using Il2CppFishNet;
using Il2CppFishNet.Managing.Object;
using Il2CppFishNet.Object;
using Il2CppFishNet.Observing;

namespace MapExpansions.World
{
    /// <summary>
    /// Registers copies of main-world networked objects as spawnable prefabs, so the host can spawn them and the clients
    /// receive them. Every peer registers the same keys in the same order, so a key gets the same prefab id everywhere.
    /// </summary>
    internal static class SpawnablePrefabs
    {
        /// <summary>
        /// The prefab slot of each key. The prefab list outlives the main world; a reload puts the new template into the
        /// old slot, so host and clients keep the same ids however often each of them loaded.
        /// </summary>
        private static readonly Dictionary<string, int> Slots = new();

        public static bool Register(string key, NetworkObject nob, string what)
        {
            var nm = InstanceFinder.NetworkManager;
            if (nm == null)
            {
                Core.Log.Error($"{what}: no NetworkManager; '{key}' cannot be spawned");
                return false;
            }
            var prefabs = nm.SpawnablePrefabs;
            var single = prefabs.TryCast<SinglePrefabObjects>();
            if (Slots.TryGetValue(key, out int slot) && single != null && slot < single._prefabs.Count)
            {
                single._prefabs[slot] = nob;
                single.InitializePrefabRange(slot);
                Core.Log.Msg($"{what}: '{key}' takes its prefab slot {slot} again (id {nob.PrefabId})");
                return true;
            }
            int before = prefabs.GetObjectCount();
            prefabs.AddObject(nob, true);
            int after = prefabs.GetObjectCount();
            if (after != before + 1)
            {
                Core.Log.Error($"{what}: registering '{key}' as a spawnable prefab left the list at {after} (was {before})");
                return false;
            }
            Slots[key] = before;
            if (single == null) Core.Log.Warning($"{what}: the prefab list is a {prefabs.GetIl2CppType().Name}; a reload adds '{key}' again");
            Core.Log.Msg($"{what}: '{key}' registered as spawnable prefab {before} (id {nob.PrefabId})");
            return true;
        }

        /// <summary>
        /// A scene object nested in the main scene becomes a root of its own; networked children stay nested in it and
        /// spawn with it, as in a prefab.
        /// </summary>
        public static int Detach(NetworkObject root)
        {
            root.SceneId = 0UL;
            root.IsNested = false;
            root.ParentNetworkObject = null;
            root.RuntimeChildNetworkObjects?.Clear();
            int nested = 0;
            foreach (var n in root.GetComponentsInChildren<NetworkObject>(true))
            {
                if (n == null || n == root) continue;
                n.SceneId = 0UL;
                n.RuntimeChildNetworkObjects?.Clear();
                nested++;
            }
            return nested;
        }

        /// <summary>Override 3 ignores the manager's conditions, so every client sees the object wherever it stands.</summary>
        public static bool OpenObserver(NetworkObserver obs)
        {
            if (obs == null) return false;
            obs._overrideType = (NetworkObserver.ConditionOverrideType)3;
            obs._observerConditions = new Il2CppSystem.Collections.Generic.List<ObserverCondition>();
            obs._updateHostVisibility = false;
            return true;
        }

        /// <summary>After the spawn: the observer conditions the template carried are dropped and rebuilt for every client.</summary>
        public static void RebuildObservers(NetworkObject nob)
        {
            var obs = nob.GetComponent<NetworkObserver>();
            if (obs == null) return;
            obs._overrideType = (NetworkObserver.ConditionOverrideType)3;
            obs._observerConditions?.Clear();
            obs._hasNormalConditions = false;
            obs.SetUpdateHostVisibility(false);
            InstanceFinder.ServerManager.Objects.RebuildObservers(nob, false);
        }
    }
}
