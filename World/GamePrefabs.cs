using HarmonyLib;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ItemFramework;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Buildable game items placed by a layer: the item's built prefab, spawned bare by the host. A bare buildable never
    /// joins a property's item list, so the save does not hold it and a reload does not double it. Stations, pots and
    /// storage wait for a property and a configuration they never get, so the catalog leaves them out.
    /// </summary>
    internal static class GamePrefabs
    {
        /// <summary>Type names of buildables that need a property, an owner or a configuration to work.</summary>
        private static readonly HashSet<string> Inert = new(StringComparer.Ordinal)
        {
            "GrowContainer", "Pot", "PlaceableStorageEntity", "MixingStation", "MixingStationMk2", "ChemistryStation",
            "PackagingStation", "PackagingStationMk2", "BrickPress", "LabOven", "Cauldron", "DryingRack", "MushroomBed",
            "MushroomSpawnStation", "StorageEntity",
        };

        /// <summary>One entry of the catalog's prefab tab.</summary>
        internal sealed class Entry
        {
            public string Id;
            public string Name;
            public GameObject Prefab;
        }

        private static List<Entry> _list;

        /// <summary>The built prefab of a buildable item, or null with the reason logged.</summary>
        internal static GameObject Template(string itemId)
        {
            ItemDefinition item;
            try
            {
                item = Il2CppScheduleOne.Registry.GetItem(itemId);
            }
            catch (Exception e)
            {
                Core.Log.Error($"prefabs: looking up item '{itemId}' failed: {e.Message}");
                return null;
            }
            if (item == null)
            {
                Core.Log.Error($"prefabs: no item '{itemId}' in the game's registry");
                return null;
            }
            var buildable = item.TryCast<BuildableItemDefinition>();
            if (buildable == null)
            {
                Core.Log.Error($"prefabs: item '{itemId}' is a {item.GetIl2CppType().Name}, not a buildable item");
                return null;
            }
            var built = buildable.BuiltItem;
            if (built == null)
            {
                Core.Log.Error($"prefabs: buildable item '{itemId}' has no built prefab");
                return null;
            }
            if (InertType(built) is string type)
                Core.Log.Warning($"prefabs: '{itemId}' is a {type}; placed outside a property it stays inert");
            return built.gameObject;
        }

        /// <summary>The buildable items the catalog offers, by name; the inert ones left out.</summary>
        internal static IReadOnlyList<Entry> List()
        {
            if (_list != null) return _list;
            var list = new List<Entry>();
            int inert = 0;
            var registry = Il2CppScheduleOne.Registry.Instance;
            if (registry == null)
            {
                Core.Log.Error("prefabs: the game's registry is not loaded; the catalog has no prefabs");
                return list;
            }
            foreach (var item in registry.GetAllItems())
            {
                var b = item?.TryCast<BuildableItemDefinition>();
                if (b?.BuiltItem == null) continue;
                if (InertType(b.BuiltItem) != null) { inert++; continue; }
                list.Add(new Entry { Id = b.ID, Name = b.Name, Prefab = b.BuiltItem.gameObject });
            }
            list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
            Core.Log.Msg($"prefabs: {list.Count} buildable items for the catalog, {inert} inert ones left out");
            return _list = list;
        }

        private static string InertType(BuildableItem built)
        {
            for (var t = built.GetIl2CppType(); t != null; t = t.BaseType)
                if (Inert.Contains(t.Name)) return t.Name;
            foreach (var c in built.GetComponentsInChildren<Component>(true))
            {
                var n = c?.GetIl2CppType().Name;
                if (n != null && Inert.Contains(n)) return n;
            }
            return null;
        }

        /// <summary>A placed prefab has no item behind it; picking it up would hand the player nothing and throw.</summary>
        [HarmonyPatch(typeof(BuildableItem), nameof(BuildableItem.CanBePickedUp))]
        internal static class NoPickup
        {
            private static bool Prefix(BuildableItem __instance, ref bool __result, ref string reason)
            {
                if (__instance == null || __instance.ItemInstance != null) return true;
                __result = false;
                reason = string.Empty;
                return false;
            }
        }
    }
}
