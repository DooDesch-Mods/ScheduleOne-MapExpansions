using System.Security.Cryptography;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.Doors;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Vehicles;
using MapExpansions.Lifecycle;
using MapExpansions.Regions;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Creates the buildings, parking lots and deal locations of an expansion. Each object gets a GUID made from the
    /// expansion id and its own id, so every peer and every load agree on it; the game sends these GUIDs between peers
    /// and stores them in saves. The objects are built inactive and switched on once their GUID is set, because their
    /// Awake reads it.
    /// </summary>
    internal static class Places
    {
        public static void Build(BuiltExpansion b)
        {
            var def = b.Definition;
            if (def.Buildings.Count == 0 && def.ParkingLots.Count == 0 && def.DealLocations.Count == 0)
            {
                Core.Log.Msg($"places: '{b.Id}' defines no buildings, parking lots or deal locations");
                return;
            }
            var root = new GameObject("Places");
            root.transform.SetParent(b.Root.transform, false);

            int buildings = 0, lots = 0, deals = 0;
            foreach (var d in def.Buildings)
                if (Try(b, "building", d.Id, () => AddBuilding(b, root.transform, d))) buildings++;
            foreach (var d in def.ParkingLots)
                if (Try(b, "parking lot", d.Id, () => AddParkingLot(b, root.transform, d))) lots++;
            foreach (var d in def.DealLocations)
                if (Try(b, "deal location", d.Id, () => AddDealLocation(b, root.transform, d))) deals++;
            Core.Log.Msg($"places: '{b.Id}' built {buildings}/{def.Buildings.Count} buildings, {lots}/{def.ParkingLots.Count} parking lots, " +
                         $"{deals}/{def.DealLocations.Count} deal locations");
        }

        private static bool Try(BuiltExpansion b, string kind, string id, Func<bool> add)
        {
            try
            {
                return add();
            }
            catch (Exception e)
            {
                Core.Log.Error($"places: '{b.Id}' {kind} '{id}' failed: {e}");
                return false;
            }
        }

        /// <summary>A GUID that depends only on the expansion, the kind of object and its id.</summary>
        public static Guid StableGuid(string expansionId, string kind, string id)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes($"mapexpansions/{expansionId}/{kind}/{id}"));
            return new Guid(bytes);
        }

        private static bool Taken(BuiltExpansion b, string kind, string id, Guid guid)
        {
            if (!Il2Cpp.GUIDManager.IsGUIDAlreadyRegistered(new Il2CppSystem.Guid(guid.ToString()))) return false;
            Core.Log.Error($"places: '{b.Id}' {kind} '{id}' GUID {guid} is already registered; it is not built");
            return true;
        }

        private static Transform Point(Transform parent, string name, LocalPoint p)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = p.Position;
            t.localRotation = Quaternion.Euler(0f, p.Yaw, 0f);
            return t;
        }

        private static bool AddBuilding(BuiltExpansion b, Transform parent, BuildingDefinition d)
        {
            if (string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Name))
            {
                Core.Log.Error($"places: '{b.Id}' a building has no id or name ('{d.Id}', '{d.Name}'); it is not built");
                return false;
            }
            if (d.Doors.Count == 0)
            {
                Core.Log.Error($"places: '{b.Id}' building '{d.Id}' has no doors; it is not built");
                return false;
            }
            var guid = StableGuid(b.Id, "building", d.Id);
            if (Taken(b, "building", d.Id, guid)) return false;

            var go = new GameObject($"Building {d.Name}");
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = d.Doors[0].Position;

            var building = go.AddComponent<NPCEnterableBuilding>();
            building.BuildingName = d.Name;
            building.BakedGUID = guid.ToString();
            var doors = new Il2CppReferenceArray<StaticDoor>(d.Doors.Count);
            for (int i = 0; i < d.Doors.Count; i++)
            {
                var doorGo = new GameObject($"Door {i}");
                doorGo.transform.SetParent(go.transform, false);
                doorGo.transform.position = b.Root.transform.TransformPoint(d.Doors[i].Position);
                doorGo.transform.rotation = b.Root.transform.rotation * Quaternion.Euler(0f, d.Doors[i].Yaw, 0f);
                var access = new GameObject("AccessPoint").transform;
                access.SetParent(doorGo.transform, false);
                // Without a knock surface the door has no collider, and the player never points at it.
                var intObj = doorGo.AddComponent<InteractableObject>();
                if (d.Knock) AddKnockSurface(b, d, i, doorGo.transform, intObj);
                var door = doorGo.AddComponent<StaticDoor>();
                door.AccessPoint = access;
                door.IntObj = intObj;
                door.Building = building;
                door.Usable = true;
                door.CanKnock = d.Knock;
                if (d.Knock) door.KnockSound = KnockSound(doorGo.transform);
                doors[i] = door;
            }
            building.Doors = doors;
            go.SetActive(true);

            var registered = Il2Cpp.GUIDManager.GetObject<NPCEnterableBuilding>(new Il2CppSystem.Guid(guid.ToString()));
            if (registered == null)
            {
                Core.Log.Error($"places: '{b.Id}' building '{d.Name}' woke but its GUID {guid} is not registered");
                return false;
            }
            Core.Log.Msg($"places: '{b.Id}' building '{d.Name}' with {d.Doors.Count} {(d.Knock ? "knock doors" : "doors for NPCs")} at {go.transform.position}, GUID {guid}");
            return true;
        }

        private const float KnockWidth = 1.1f, KnockHeight = 2.1f, KnockDepth = 0.6f, KnockOut = 0.45f, KnockSearch = 4f, KnockBack = 0.5f;

        /// <summary>
        /// A trigger box the player points at to knock. It covers the door model where the definition names one, else it
        /// stands on the first wall in front of the door point. The interaction cast stops at the first solid collider,
        /// so the box reaches past the door's own collider on both sides. The game draws the knock prompt at the
        /// interactable's display point, else at its origin, which here lies on the ground below the player's view;
        /// the display point sits in the middle of the box.
        /// </summary>
        private static void AddKnockSurface(BuiltExpansion b, BuildingDefinition d, int index, Transform door, InteractableObject intObj)
        {
            var surface = new GameObject("Knock").transform;
            surface.SetParent(door, false);
            float height = KnockHeight;
            string model = index < d.DoorModels.Count ? d.DoorModels[index] : null;
            var bounds = string.IsNullOrEmpty(model) ? null : ModelBounds(b, d, index, model);
            if (bounds is { } m)
            {
                height = Mathf.Max(m.size.y, 1.8f);
                surface.position = new Vector3(m.center.x, m.min.y, m.center.z);
            }
            else
            {
                // The expansion was just placed; casts see its colliders only after a sync. The cast starts a little
                // behind the point, which may sit on the wall's face.
                Physics.SyncTransforms();
                var from = door.position + Vector3.up * 1.2f - door.forward * KnockBack;
                if (Physics.Raycast(from, door.forward, out var hit, KnockSearch + KnockBack, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    surface.position = new Vector3(hit.point.x, door.position.y, hit.point.z);
                else
                {
                    surface.position = door.position + door.forward * 0.8f;
                    Core.Log.Warning($"places: '{b.Id}' building '{d.Id}' door {index}: no wall within {KnockSearch} m in front of {door.position}; " +
                                     "its knock surface stands 0.8 m ahead");
                }
            }
            surface.gameObject.layer = KnockLayer();
            var box = surface.gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(KnockWidth, height, KnockDepth);
            // Reaches out in front of the door, past facade and frame colliders that stand a little proud of it.
            box.center = new Vector3(0f, height * 0.5f, KnockDepth * 0.5f - KnockOut);
            var prompt = new GameObject("Prompt").transform;
            prompt.SetParent(surface, false);
            prompt.localPosition = box.center;
            intObj.displayLocationPoint = prompt;
            Core.Log.Msg($"places: '{b.Id}' building '{d.Id}' door {index}: knock surface at {surface.position}{(bounds != null ? $" on '{model}'" : " on a wall")}");
        }

        /// <summary>World bounds of the renderers of a door model of the expansion's source.</summary>
        private static Bounds? ModelBounds(BuiltExpansion b, BuildingDefinition d, int index, string path)
        {
            var t = MainCopies.FindInExpansion(b, path);
            if (t == null)
            {
                Core.Log.Error($"places: '{b.Id}' building '{d.Id}' door {index}: door model '{path}' is not in the expansion; the knock surface looks for a wall instead");
                return null;
            }
            Bounds? all = null;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (all is { } a) { a.Encapsulate(r.bounds); all = a; }
                else all = r.bounds;
            }
            if (all == null)
                Core.Log.Error($"places: '{b.Id}' building '{d.Id}' door {index}: door model '{path}' has no renderer; the knock surface looks for a wall instead");
            // The prologue's door script is gone, but its trigger stayed; the interaction cast ends on it.
            int dead = 0;
            foreach (var c in t.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || !c.isTrigger || !c.enabled || c.GetComponentInParent<InteractableObject>() != null) continue;
                c.enabled = false;
                dead++;
            }
            if (dead > 0)
                Core.Log.Msg($"places: '{b.Id}' building '{d.Id}' door {index}: {dead} trigger(s) without a script on '{path}' switched off");
            return all;
        }

        private static int _knockLayer = -1;
        private static StaticDoor _knockTemplate;

        /// <summary>A main-world door the player can knock at; its collider layer and knock sound are reused.</summary>
        private static StaticDoor KnockTemplate()
        {
            if (_knockTemplate != null) return _knockTemplate;
            foreach (var s in Object.FindObjectsOfType<StaticDoor>(true))
            {
                if (s == null || !s.CanKnock || s.IntObj == null || s.KnockSound == null) continue;
                if (s.transform.root.name.StartsWith("MapExpansions.", StringComparison.Ordinal)) continue;
                if (s.IntObj.GetComponentInChildren<Collider>(true) == null) continue;
                _knockTemplate = s;
                Core.Log.Msg($"places: knock doors take layer and sound from '{s.name}' in '{s.Building?.BuildingName}'");
                return s;
            }
            Core.Log.Warning("places: the main world has no knock door with a collider and a sound; knock doors use layer Default and stay silent");
            return null;
        }

        private static int KnockLayer()
        {
            if (_knockLayer >= 0) return _knockLayer;
            var c = KnockTemplate()?.IntObj.GetComponentInChildren<Collider>(true);
            _knockLayer = c != null ? c.gameObject.layer : 0;
            return _knockLayer;
        }

        private static Il2CppScheduleOne.Audio.AudioSourceController KnockSound(Transform door)
        {
            var src = KnockTemplate()?.KnockSound;
            if (src == null) return null;
            var copy = Object.Instantiate(src.gameObject, door, false);
            copy.name = "KnockSound";
            copy.transform.localPosition = Vector3.up * 1.2f;
            return copy.GetComponent<Il2CppScheduleOne.Audio.AudioSourceController>();
        }

        /// <summary>
        /// The game parks a car with its rear on the spot's point. A definition names the car's centre, so the point
        /// moves back by half the length of a common car.
        /// </summary>
        internal const float HalfCarLength = 2.3f;

        /// <summary>The ground below a point, or the point itself where nothing is below it.</summary>
        internal static Vector3 Ground(Vector3 p)
        {
            if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var hit, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return hit.point;
            Core.Log.Warning($"places: no ground below parking spot {p}; it stays at that height");
            return p;
        }

        private static bool AddParkingLot(BuiltExpansion b, Transform parent, ParkingLotDefinition d)
        {
            if (string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Name))
            {
                Core.Log.Error($"places: '{b.Id}' a parking lot has no id or name ('{d.Id}', '{d.Name}'); it is not built");
                return false;
            }
            if (d.Spots.Count == 0)
            {
                Core.Log.Error($"places: '{b.Id}' parking lot '{d.Id}' has no spots; it is not built");
                return false;
            }
            var guid = StableGuid(b.Id, "parking", d.Id);
            if (Taken(b, "parking lot", d.Id, guid)) return false;

            var go = new GameObject(d.Name);
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = d.Entry.Position;
            var lot = go.AddComponent<ParkingLot>();
            lot.BakedGUID = guid.ToString();
            lot.UseExitPoint = false;
            lot.EntryPoint = Point(parent, d.Name + " Entry", d.Entry);
            lot.EntryPoint.SetParent(go.transform, true);
            for (int i = 0; i < d.Spots.Count; i++)
            {
                var spotGo = new GameObject($"Spot {i}");
                spotGo.transform.SetParent(go.transform, false);
                var align = Point(parent, "AlignmentPoint", d.Spots[i]);
                align.position = Ground(align.position - align.forward * HalfCarLength);
                spotGo.transform.position = align.position;
                spotGo.transform.rotation = align.rotation;
                align.SetParent(spotGo.transform, true);
                var spot = spotGo.AddComponent<ParkingSpot>();
                spot.AlignmentPoint = align;
                spot.Alignment = EParkingAlignment.RearToKerb;
            }
            go.SetActive(true);

            int spots = lot.ParkingSpots?.Count ?? -1;
            if (spots != d.Spots.Count)
                Core.Log.Warning($"places: '{b.Id}' parking lot '{d.Name}' holds {spots} of {d.Spots.Count} spots");
            Core.Log.Msg($"places: '{b.Id}' parking lot '{d.Name}' with {spots} spots, entry {lot.EntryPoint.position}, GUID {guid}");
            return true;
        }

        private static bool AddDealLocation(BuiltExpansion b, Transform parent, DealLocationDefinition d)
        {
            if (string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Name))
            {
                Core.Log.Error($"places: '{b.Id}' a deal location has no id or name ('{d.Id}', '{d.Name}'); it is not built");
                return false;
            }
            var region = RegionRegistry.All.FirstOrDefault(e => e.ExpansionId == b.Id &&
                                                                 (string.IsNullOrEmpty(d.Region) || string.Equals(e.Definition.Name, d.Region, StringComparison.Ordinal)));
            if (region == null)
            {
                Core.Log.Error($"places: '{b.Id}' deal location '{d.Id}' names region '{d.Region}', which the expansion does not have; it is not built");
                return false;
            }
            var map = Il2CppScheduleOne.DevUtilities.Singleton<Map>.Instance;
            var data = map?.GetRegionData(region.Region);
            if (data == null)
            {
                Core.Log.Error($"places: '{b.Id}' deal location '{d.Id}': region {(int)region.Region} is not on the map; it is not built");
                return false;
            }
            var guid = StableGuid(b.Id, "deal", d.Id);
            if (Taken(b, "deal location", d.Id, guid)) return false;

            var go = new GameObject(d.Name);
            go.SetActive(false);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = d.StandPoint.Position;
            go.transform.localRotation = Quaternion.Euler(0f, d.StandPoint.Yaw, 0f);
            var loc = go.AddComponent<DeliveryLocation>();
            loc.LocationName = d.Name;
            loc.LocationDescription = d.Description ?? string.Empty;
            loc.StaticGUID = guid.ToString();
            var stand = new GameObject("CustomerStandPoint").transform;
            stand.SetParent(go.transform, false);
            var teleport = new GameObject("TeleportPoint").transform;
            teleport.SetParent(go.transform, false);
            loc.CustomerStandPoint = stand;
            loc.TeleportPoint = teleport;
            go.SetActive(true);

            if (Il2Cpp.GUIDManager.GetObject<DeliveryLocation>(new Il2CppSystem.Guid(guid.ToString())) == null)
            {
                Core.Log.Error($"places: '{b.Id}' deal location '{d.Name}' woke but its GUID {guid} is not registered");
                return false;
            }
            var old = data.RegionDeliveryLocations ?? new Il2CppReferenceArray<DeliveryLocation>(0);
            var arr = new Il2CppReferenceArray<DeliveryLocation>(old.Length + 1);
            for (int i = 0; i < old.Length; i++) arr[i] = old[i];
            arr[old.Length] = loc;
            data.RegionDeliveryLocations = arr;
            Core.Log.Msg($"places: '{b.Id}' deal location '{d.Name}' in region {(int)region.Region} at {go.transform.position}, GUID {guid} ({arr.Length} in the region)");
            return true;
        }
    }
}
