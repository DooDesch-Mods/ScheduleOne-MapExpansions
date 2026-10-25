using System.Collections;
using Il2CppFishNet;
using Il2CppFishNet.Managing.Object;
using Il2CppFishNet.Object;
using Il2CppFishNet.Observing;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using HarmonyLib;
using Il2CppScheduleOne.Delivery;
using Il2CppScheduleOne.Heatmap;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Misc;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Property;
using MapExpansions.Lifecycle;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using Object = UnityEngine.Object;
using Property = Il2CppScheduleOne.Property.Property;

namespace MapExpansions.World
{
    /// <summary>
    /// Builds the buyable properties of an expansion as copies of main-world properties. Every peer turns its copy of
    /// the template into a spawnable prefab under the same slot; the host spawns it before the save loads, so the game
    /// finds it when it reads the saved properties, and the clients receive it like any spawned object.
    /// </summary>
    internal static class Properties
    {
        private sealed class Copy
        {
            public BuiltExpansion Expansion;
            public PropertyDefinition Def;
            public GameObject Template;
            public Matrix4x4 Move;
            public Vector3 Position;
            public Quaternion Rotation;
            public Transform Poster;
            public Vector3 PosterPosition;
            public Quaternion PosterRotation;
            public Property Instance;
        }

        private static readonly List<Copy> Copies = new();

        private static object _spawnRoutine;

        public static void Build(BuiltExpansion b)
        {
            var defs = b.Definition.Properties;
            if (defs.Count == 0)
            {
                Core.Log.Msg($"properties: '{b.Id}' defines no properties");
                return;
            }
            var templates = new GameObject("Property Templates");
            templates.SetActive(false);
            templates.transform.SetParent(b.Root.transform, false);

            int made = 0;
            foreach (var d in defs)
            {
                try
                {
                    var c = MakeTemplate(b, templates.transform, d);
                    if (c == null) continue;
                    if (!Register(c)) continue;
                    Copies.Add(c);
                    if (d.SoldByRealtor) RealtorListings.Add(b.Id, d);
                    made++;
                }
                catch (Exception e)
                {
                    Core.Log.Error($"properties: '{b.Id}' property '{d.Id}' failed: {e}");
                }
            }
            Core.Log.Msg($"properties: '{b.Id}' prepared {made}/{defs.Count} properties");
            if (made > 0 && _spawnRoutine == null) _spawnRoutine = MelonCoroutines.Start(SpawnWhenNetworked());
        }

        private static Property FindSource(string code)
        {
            foreach (var p in Object.FindObjectsOfType<Property>(true))
            {
                if (p == null || !string.Equals(p.PropertyCode, code, StringComparison.OrdinalIgnoreCase)) continue;
                if (Copies.Any(c => c.Template == p.gameObject)) continue;
                return p;
            }
            return null;
        }

        private static Copy MakeTemplate(BuiltExpansion b, Transform templates, PropertyDefinition d)
        {
            if (string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Code) || string.IsNullOrEmpty(d.Name))
            {
                Core.Log.Error($"properties: '{b.Id}' a property has no id, code or name ('{d.Id}', '{d.Code}', '{d.Name}'); it is not built");
                return null;
            }
            if (d.Code != d.Code.ToLowerInvariant())
            {
                Core.Log.Error($"properties: '{b.Id}' property code '{d.Code}' is not lowercase; it is not built");
                return null;
            }
            if (FindSource(d.Code) != null || Copies.Any(c => c.Def.Code == d.Code))
            {
                Core.Log.Error($"properties: '{b.Id}' property code '{d.Code}' is already in the world; it is not built");
                return null;
            }
            var src = FindSource(d.Template);
            if (src == null)
            {
                Core.Log.Error($"properties: '{b.Id}' property '{d.Id}': template '{d.Template}' is not in the main world; it is not built");
                return null;
            }

            var go = Object.Instantiate(src.gameObject, templates);
            go.name = d.Name;
            var scale = d.Mirrored ? new Vector3(-1f, 1f, 1f) : Vector3.one;
            go.transform.localScale = scale;
            var c = new Copy
            {
                Expansion = b,
                Def = d,
                Template = go,
                Position = b.Root.transform.TransformPoint(d.Root.Position),
                Rotation = b.Root.transform.rotation * Quaternion.Euler(0f, d.Root.Yaw, 0f),
            };
            c.Move = Matrix4x4.TRS(c.Position, c.Rotation, scale) * Matrix4x4.TRS(src.transform.position, src.transform.rotation, Vector3.one).inverse;

            foreach (var lot in go.GetComponentsInChildren<ParkingLot>(true))
                if (lot != null && !UnderDock(lot.transform))
                {
                    Core.Log.Msg($"properties: '{d.Code}' drops parking lot '{lot.name}'; NPC cars park in the original");
                    Object.DestroyImmediate(lot.gameObject);
                }
            foreach (var sp in go.GetComponentsInChildren<SavePoint>(true))
                if (sp != null) Object.DestroyImmediate(sp.gameObject);

            var nob = go.GetComponent<NetworkObject>();
            if (nob == null)
            {
                Core.Log.Error($"properties: '{b.Id}' template '{d.Template}' has no NetworkObject; '{d.Code}' is not built");
                Object.Destroy(go);
                return null;
            }
            PrepareNetworkObjects(d, go, nob);
            OpenObserver(d, go);

            var p = go.GetComponent<Property>();
            float templatePrice = p.Price;
            int templateCapacity = p.EmployeeCapacity;
            p.propertyName = d.Name;
            p.propertyCode = d.Code;
            if (d.Price.HasValue) p.Price = d.Price.Value;
            if (d.EmployeeCapacity.HasValue) p.EmployeeCapacity = d.EmployeeCapacity.Value;
            p.OwnedByDefault = false;
            p.IsOwnedVariable = string.Empty;
            p.AvailableInDemo = true;
            // Switches and toggleables are saved by index; the copy keeps those that came along, in their order.
            p.Switches = Inside(d, "switch", go, p.Switches);
            p.Toggleables = Inside(d, "toggleable", go, p.Toggleables);
            // Awake makes its own containers; the copied ones belong to the original.
            p.Container = null;
            p.WorldspaceUIContainer = null;
            ResetPoi(d, p.PoI);

            int guids = Reguid(b, d, go, p);
            CheckInside(d, go, p);
            if (d.Mirrored) Unmirror(d, p);
            c.Poster = CopyPoster(d, src, templates, out c.PosterPosition, out c.PosterRotation);
            p.ListingPoster = c.Poster;

            Core.Log.Msg($"properties: '{b.Id}' template '{d.Code}' from '{d.Template}' ({(p.TryCast<Business>() != null ? "business" : "property")}): " +
                         $"{guids} GUIDs replaced, {p.Grids?.Count ?? 0} grids, {p.LoadingDocks?.Length ?? 0} docks, {p.EmployeeIdlePoints?.Length ?? 0} idle points, " +
                         $"price {p.Price} (template {templatePrice}), {p.EmployeeCapacity} employees (template {templateCapacity}), stands at {c.Position}");
            return c;
        }

        private static Il2CppSystem.Collections.Generic.List<T> Inside<T>(PropertyDefinition d, string what, GameObject go,
            Il2CppSystem.Collections.Generic.List<T> list) where T : Component
        {
            var kept = new Il2CppSystem.Collections.Generic.List<T>();
            if (list == null) return kept;
            for (int i = 0; i < list.Count; i++)
            {
                var x = list[i];
                if (x != null && x.transform.IsChildOf(go.transform)) kept.Add(x);
                else Core.Log.Warning($"properties: '{d.Code}' {what} {i} '{(x != null ? x.name : "null")}' is not part of the copy; it is not saved");
            }
            return kept;
        }

        private static bool UnderDock(Transform t)
        {
            for (var x = t; x != null; x = x.parent)
                if (x.GetComponent<LoadingDock>() != null) return true;
            return false;
        }

        /// <summary>
        /// The original is a scene object nested in the scene's property root. The copy becomes a root of its own; its
        /// doors, taps, switches and bins stay nested in it and spawn with it, as in a prefab.
        /// </summary>
        private static void PrepareNetworkObjects(PropertyDefinition d, GameObject go, NetworkObject root)
        {
            root.SceneId = 0UL;
            root.IsNested = false;
            root.ParentNetworkObject = null;
            root.RuntimeChildNetworkObjects?.Clear();
            int nested = 0;
            foreach (var n in go.GetComponentsInChildren<NetworkObject>(true))
            {
                if (n == null || n == root) continue;
                n.SceneId = 0UL;
                n.RuntimeChildNetworkObjects?.Clear();
                nested++;
                Core.Log.Msg($"properties: '{d.Code}' keeps networked child '{Path(go.transform, n.transform)}' (nested {n.IsNested}, " +
                             $"parent '{(n.ParentNetworkObject != null ? n.ParentNetworkObject.name : "none")}')");
            }
            Core.Log.Msg($"properties: '{d.Code}' root object holds {root.ChildNetworkObjects?.Count ?? 0} children in its list, {nested} nested in all");
        }

        private static void OpenObserver(PropertyDefinition d, GameObject go)
        {
            var obs = go.GetComponent<NetworkObserver>();
            if (obs == null)
            {
                Core.Log.Msg($"properties: '{d.Code}' has no NetworkObserver; every client observes it");
                return;
            }
            // Override 3 ignores the manager's conditions, so every client sees the property wherever it stands.
            obs._overrideType = (NetworkObserver.ConditionOverrideType)3;
            obs._observerConditions = new Il2CppSystem.Collections.Generic.List<ObserverCondition>();
            obs._updateHostVisibility = false;
        }

        private static void ResetPoi(PropertyDefinition d, POI poi)
        {
            if (poi == null)
            {
                Core.Log.Warning($"properties: '{d.Code}' has no map marker");
                return;
            }
            // The copied marker points at the original's map icon; cleared, it makes its own.
            poi._UISetup_k__BackingField = false;
            poi._UI_k__BackingField = null;
            poi._IconContainer_k__BackingField = null;
            poi.mainLabel = null;
            poi.button = null;
            poi.eventTrigger = null;
            poi.mainTextSet = false;
        }

        /// <summary>Every object of the copy that the game registers by GUID gets one made from the property's id.</summary>
        private static int Reguid(BuiltExpansion b, PropertyDefinition d, GameObject go, Property p)
        {
            int n = 0;
            string Next(string kind, int i)
            {
                n++;
                return Places.StableGuid(b.Id, "property", $"{d.Id}/{kind}/{i}").ToString();
            }

            var docks = go.GetComponentsInChildren<LoadingDock>(true);
            var dockArr = new Il2CppReferenceArray<LoadingDock>(docks.Length);
            for (int i = 0; i < docks.Length; i++)
            {
                var dock = docks[i];
                dock.BakedGUID = Next("dock", i);
                dock._GUID_k__BackingField = new Il2CppSystem.Guid(dock.BakedGUID);
                dock.ParentProperty = p;
                var lot = dock.GetComponentInChildren<ParkingLot>(true);
                if (lot != null)
                {
                    lot.BakedGUID = Next("dock-lot", i);
                    lot._GUID_k__BackingField = new Il2CppSystem.Guid(lot.BakedGUID);
                    dock.Parking = lot;
                }
                dockArr[i] = dock;
            }
            p.LoadingDocks = dockArr;

            var grids = go.GetComponentsInChildren<Grid>(true);
            var gridList = new Il2CppSystem.Collections.Generic.List<Grid>();
            for (int i = 0; i < grids.Length; i++)
            {
                grids[i]._guid = Next("grid", i);
                grids[i]._GUID_k__BackingField = new Il2CppSystem.Guid(grids[i]._guid);
                gridList.Add(grids[i]);
            }
            p.Grids = gridList;

            var storage = go.GetComponentsInChildren<Il2CppScheduleOne.Storage.WorldStorageEntity>(true);
            for (int i = 0; i < storage.Length; i++)
            {
                storage[i].BakedGUID = Next("storage", i);
                storage[i]._GUID_k__BackingField = new Il2CppSystem.Guid(storage[i].BakedGUID);
            }
            var surfaces = go.GetComponentsInChildren<Il2CppScheduleOne.Building.Surface>(true);
            for (int i = 0; i < surfaces.Length; i++)
            {
                surfaces[i].BakedGUID = Next("surface", i);
                surfaces[i]._GUID_k__BackingField = new Il2CppSystem.Guid(surfaces[i].BakedGUID);
            }
            var sprays = go.GetComponentsInChildren<Il2CppScheduleOne.Graffiti.WorldSpraySurface>(true);
            for (int i = 0; i < sprays.Length; i++)
            {
                sprays[i].BakedGUID = Next("spray", i);
                sprays[i]._GUID_k__BackingField = new Il2CppSystem.Guid(sprays[i].BakedGUID);
            }
            var drags = go.GetComponentsInChildren<Il2CppScheduleOne.Dragging.SceneDraggable>(true);
            for (int i = 0; i < drags.Length; i++) drags[i].BakedGUID = Next("draggable", i);
            var drops = go.GetComponentsInChildren<Il2CppScheduleOne.Economy.DeadDrop>(true);
            for (int i = 0; i < drops.Length; i++)
            {
                drops[i].BakedGUID = Next("deaddrop", i);
                drops[i]._GUID_k__BackingField = new Il2CppSystem.Guid(drops[i].BakedGUID);
            }
            Core.Log.Msg($"properties: '{d.Code}' GUIDs: {docks.Length} docks, {grids.Length} grids, {storage.Length} storage, {surfaces.Length} surfaces, " +
                         $"{sprays.Length} spray surfaces, {drags.Length} draggables, {drops.Length} dead drops");
            return n;
        }

        /// <summary>Points the property refers to from outside its own objects stay at the original; each is reported.</summary>
        private static void CheckInside(PropertyDefinition d, GameObject go, Property p)
        {
            void Check(string what, Transform t)
            {
                if (t == null) Core.Log.Warning($"properties: '{d.Code}' has no {what}");
                else if (!t.IsChildOf(go.transform)) Core.Log.Warning($"properties: '{d.Code}' {what} '{t.name}' lies outside the property and stays at the original");
            }
            Check("spawn point", p.SpawnPoint);
            Check("interior spawn point", p.InteriorSpawnPoint);
            Check("NPC spawn point", p.NPCSpawnPoint);
            Check("for-sale sign", p.ForSaleSign != null ? p.ForSaleSign.transform : null);
            Check("bounding box", p.BoundingBox != null ? p.BoundingBox.transform : null);
            Check("disposal area", p.DisposalArea != null ? p.DisposalArea.transform : null);
            if (p.EmployeeIdlePoints != null)
                for (int i = 0; i < p.EmployeeIdlePoints.Length; i++) Check($"idle point {i}", p.EmployeeIdlePoints[i]);
            if ((p.EmployeeIdlePoints?.Length ?? 0) < p.EmployeeCapacity)
                Core.Log.Warning($"properties: '{d.Code}' has {p.EmployeeIdlePoints?.Length ?? 0} idle points for {p.EmployeeCapacity} employees");
        }

        /// <summary>
        /// In a mirrored copy every transform is left-handed. The game reads grids as tile frames and points as
        /// position plus facing, so those get a proper frame again: the same place, facing mirrored. A grid's tiles
        /// fill the mirrored area because its frame is flipped about the middle of its tile columns.
        /// </summary>
        private static void Unmirror(PropertyDefinition d, Property p)
        {
            int grids = 0, points = 0;
            foreach (var grid in p.Grids)
            {
                if (grid == null || !Mirrored(grid.transform)) continue;
                var tiles = grid.GetComponentsInChildren<Il2CppScheduleOne.Tiles.Tile>(true);
                if (tiles.Length == 0)
                {
                    Core.Log.Warning($"properties: '{d.Code}' grid '{grid.name}' has no tiles; it stays mirrored");
                    continue;
                }
                float min = float.MaxValue, max = float.MinValue;
                foreach (var tile in tiles)
                {
                    float x = grid.transform.InverseTransformPoint(tile.transform.position).x;
                    min = Mathf.Min(min, x);
                    max = Mathf.Max(max, x);
                }
                var t = grid.transform;
                var s = t.localScale;
                t.localPosition += t.localRotation * new Vector3(s.x * (min + max), 0f, 0f);
                t.localScale = new Vector3(-s.x, s.y, s.z);
                grids++;
            }

            var list = new List<Transform> { p.SpawnPoint, p.InteriorSpawnPoint, p.NPCSpawnPoint };
            if (p.EmployeeIdlePoints != null) list.AddRange(p.EmployeeIdlePoints);
            foreach (var lot in p.GetComponentsInChildren<ParkingLot>(true))
            {
                list.Add(lot.EntryPoint);
                list.Add(lot.ExitPoint);
                list.Add(lot.HiddenVehicleAccessPoint);
                foreach (var spot in lot.ParkingSpots) if (spot != null) list.Add(spot.transform);
            }
            if (p.LoadingDocks != null)
                foreach (var dock in p.LoadingDocks)
                {
                    if (dock == null) continue;
                    list.Add(dock.uiPoint);
                    if (dock.accessPoints != null) list.AddRange(dock.accessPoints);
                }
            // Parents first, so a point under a flipped point is proper already.
            foreach (var x in list.Where(x => x != null && x.IsChildOf(p.transform)).Distinct().OrderBy(Depth))
            {
                if (!Mirrored(x)) continue;
                var s = x.localScale;
                x.localScale = new Vector3(-s.x, s.y, s.z);
                points++;
            }
            Core.Log.Msg($"properties: '{d.Code}' is mirrored; {grids} grids and {points} points keep a proper frame");
        }

        private static bool Mirrored(Transform t) => t.localToWorldMatrix.determinant < 0f;

        private static int Depth(Transform t)
        {
            int n = 0;
            for (var x = t.parent; x != null; x = x.parent) n++;
            return n;
        }

        /// <summary>The listing on the realtor's wall: a copy of the template's, hung below it once the property exists.</summary>
        private static Transform CopyPoster(PropertyDefinition d, Property src, Transform templates, out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = default;
            if (src.ListingPoster == null)
            {
                Core.Log.Msg($"properties: '{d.Code}' template has no listing poster");
                return null;
            }
            var poster = Object.Instantiate(src.ListingPoster.gameObject, templates).transform;
            poster.name = $"PropertyListing {d.Name}";
            int below = Copies.Count(c => c.Poster != null && c.Def.Template == d.Template) + 1;
            pos = src.ListingPoster.position + Vector3.down * (0.42f * below);
            rot = src.ListingPoster.rotation;
            return poster;
        }

        private static bool Register(Copy c) =>
            SpawnablePrefabs.Register("property:" + c.Def.Code, c.Template.GetComponent<NetworkObject>(), "properties");

        /// <summary>
        /// The host starts its server after the scene is initialized and before the save loads. The copies are spawned
        /// in that gap, so the saved properties find them.
        /// </summary>
        private static IEnumerator SpawnWhenNetworked()
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!InstanceFinder.IsServer && !InstanceFinder.IsClient)
            {
                if (wait.Elapsed.TotalMinutes > 3)
                {
                    Core.Log.Error("properties: the network did not start within 3 minutes; no property is spawned");
                    _spawnRoutine = null;
                    yield break;
                }
                yield return null;
            }
            _spawnRoutine = null;
            if (!InstanceFinder.IsServer)
            {
                Core.Log.Msg($"properties: client; {Copies.Count} properties arrive from the host");
                yield break;
            }
            foreach (var c in Copies.ToList())
            {
                try
                {
                    Spawn(c);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"properties: spawning '{c.Def.Code}' failed: {e}");
                }
            }
        }

        private static void Spawn(Copy c)
        {
            var go = Object.Instantiate(c.Template, c.Position, c.Rotation);
            go.name = c.Template.name;
            go.SetActive(true);
            var nob = go.GetComponent<NetworkObject>();
            InstanceFinder.ServerManager.Spawn(nob, null, default(Scene));
            var p = go.GetComponent<Property>();
            // The bungalow's script writes the quest variables of the main bungalow; a copy must not.
            if (p.TryCast<Bungalow>() != null)
            {
                p.CancelInvoke("UpdateVariables");
                Core.Log.Msg($"properties: '{c.Def.Code}' does not update the bungalow's quest variables");
            }
            var obs = go.GetComponent<NetworkObserver>();
            if (obs != null)
            {
                obs._overrideType = (NetworkObserver.ConditionOverrideType)3;
                obs._observerConditions?.Clear();
                obs._hasNormalConditions = false;
                obs.SetUpdateHostVisibility(false);
                InstanceFinder.ServerManager.Objects.RebuildObservers(nob, false);
            }
            nob.SetRenderersVisible(true, true);
            int children = 0, spawned = 0;
            foreach (var n in go.GetComponentsInChildren<NetworkObject>(true))
            {
                if (n == nob) continue;
                children++;
                if (n.IsSpawned) spawned++;
                else Core.Log.Warning($"properties: '{c.Def.Code}' child '{Path(go.transform, n.transform)}' did not spawn with the property");
            }
            Core.Log.Msg($"properties: '{c.Def.Code}' spawned at {go.transform.position}, object id {nob.ObjectId}, {spawned}/{children} children spawned, " +
                         $"registered {Property.Properties.Contains(p)}, owned {p.IsOwned}");
        }

        /// <summary>Fits each copy once it exists on this peer: on the host after the spawn, on a client when it arrives.</summary>
        public static void Tick()
        {
            TickHeatmap();
            if (Copies.Count == 0) return;
            foreach (var c in Copies)
            {
                if (c.Instance != null) continue;
                var p = FindInstance(c);
                if (p == null) continue;
                c.Instance = p;
                try
                {
                    Fit(c, p);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"properties: fitting '{c.Def.Code}' failed: {e}");
                }
            }
        }

        private static Property FindInstance(Copy c)
        {
            var all = Property.Properties;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p != null && p.PropertyCode == c.Def.Code && p.gameObject != c.Template && p.gameObject.activeInHierarchy) return p;
            }
            return null;
        }

        /// <summary>
        /// Statically batched meshes hold their vertices where the original stands. Each batch is pointed at a root moved
        /// like the property, so the copy draws where it stands.
        /// </summary>
        private static void Fit(Copy c, Property p)
        {
            var (batched, roots, own) = StaticBatches.Follow(p.transform, c.Move, c.Def.Name);
            Navigation.PropertyNavigation.Bake(c.Def.Code, p, c.Expansion.Root.transform);
            if (c.Poster != null)
            {
                c.Poster.SetParent(null, false);
                c.Poster.SetPositionAndRotation(c.PosterPosition, c.PosterRotation);
            }
            Core.Log.Msg($"properties: '{c.Def.Code}' in the world at {p.transform.position} ({(InstanceFinder.IsServer ? "host" : "client")}): " +
                         $"{batched} batched renderers moved over {roots} roots, {own} follow the property already" +
                         (c.Poster != null ? $", listing at {c.PosterPosition}" : ""));
        }

        private static string Path(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>
        /// The heatmap manager sets up the temperature view of every property that exists when it starts, and never
        /// again. Its start waits until the expansions' properties stand on this peer, or until
        /// <see cref="HeatmapWaitSeconds"/> have passed.
        /// </summary>
        private const float HeatmapWaitSeconds = 180f;

        private static HeatmapManager _deferredHeatmap;
        private static float _heatmapDeadline;
        private static bool _startingHeatmap;

        private static int ExpectedProperties() => Registry.Usable().Sum(d => d.Properties.Count);

        private static void TickHeatmap()
        {
            if (_deferredHeatmap == null) return;
            int expected = ExpectedProperties();
            int standing = Copies.Count(c => c.Instance != null);
            bool late = Time.unscaledTime >= _heatmapDeadline;
            if (standing < expected && !late) return;
            var heat = _deferredHeatmap;
            _deferredHeatmap = null;
            if (late && standing < expected)
                Core.Log.Warning($"properties: {standing}/{expected} properties stand after {HeatmapWaitSeconds:0} s; the heatmap starts without the rest, they have no temperature view");
            _startingHeatmap = true;
            try
            {
                heat.Start();
                int known = heat._propertyGridMasks?.Count ?? 0;
                var missing = Copies.Where(c => heat._propertyGridMasks == null || !heat._propertyGridMasks.ContainsKey(c.Def.Code)).Select(c => c.Def.Code).ToList();
                if (missing.Count > 0) Core.Log.Warning($"properties: the heatmap started without {string.Join(", ", missing)}; no temperature view there");
                Core.Log.Msg($"properties: heatmap started with {known} properties, {standing} of them from expansions");
            }
            catch (Exception e)
            {
                Core.Log.Error($"properties: starting the heatmap failed; temperature views are missing: {e}");
            }
            finally
            {
                _startingHeatmap = false;
            }
        }

        [HarmonyPatch(typeof(HeatmapManager), nameof(HeatmapManager.Start))]
        private static class HeatmapStartPatch
        {
            private static bool Prefix(HeatmapManager __instance)
            {
                if (_startingHeatmap) return true;
                int expected = ExpectedProperties();
                if (expected == 0) return true;
                _deferredHeatmap = __instance;
                _heatmapDeadline = Time.unscaledTime + HeatmapWaitSeconds;
                Core.Log.Msg($"properties: heatmap start waits for {expected} expansion properties");
                return false;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex SavedCode =
            new("\"PropertyCode\"\\s*:\\s*\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// A save deletes every property file the property manager did not list, and every business file the business
        /// manager did not list. A file whose property is not in the
        /// world (its expansion failed to build, is switched off or its mod is not installed) is listed anyway, so
        /// ownership, employees and everything built there come back once the property does. Every property the game
        /// knows writes its own file, so a file with an unknown code can only come from a property that is missing.
        /// </summary>
        [HarmonyPatch(typeof(PropertyManager), nameof(PropertyManager.WriteData))]
        private static class KeepOrphanedSaves
        {
            private static void Postfix(PropertyManager __instance, string parentFolderPath, Il2CppSystem.Collections.Generic.List<string> __result) =>
                KeepOrphans("property", __instance.SaveFolderName, parentFolderPath, __result);
        }

        [HarmonyPatch(typeof(BusinessManager), nameof(BusinessManager.WriteData))]
        private static class KeepOrphanedBusinessSaves
        {
            private static void Postfix(BusinessManager __instance, string parentFolderPath, Il2CppSystem.Collections.Generic.List<string> __result) =>
                KeepOrphans("business", __instance.SaveFolderName, parentFolderPath, __result);
        }

        private static void KeepOrphans(string kind, string saveFolderName, string parentFolderPath, Il2CppSystem.Collections.Generic.List<string> __result)
        {
            try
            {
                if (__result == null)
                {
                    Core.Log.Warning($"properties: the {kind} manager listed no files; orphaned expansion {kind} saves are not protected");
                    return;
                }
                var known = new HashSet<string>();
                var all = Property.Properties;
                for (int i = 0; all != null && i < all.Count; i++)
                    if (all[i] != null) known.Add(all[i].PropertyCode);
                var businesses = Business.Businesses;
                for (int i = 0; businesses != null && i < businesses.Count; i++)
                    if (businesses[i] != null) known.Add(businesses[i].PropertyCode);
                var folder = System.IO.Path.Combine(parentFolderPath, saveFolderName);
                if (!System.IO.Directory.Exists(folder)) return;
                foreach (var file in System.IO.Directory.GetFiles(folder, "*.json"))
                {
                    string text;
                    try
                    {
                        text = System.IO.File.ReadAllText(file);
                    }
                    catch (Exception e)
                    {
                        Core.Log.Warning($"properties: could not read '{file}' to check it before the save; it is kept: {e.Message}");
                        __result.Add(System.IO.Path.GetFileName(file));
                        continue;
                    }
                    var m = SavedCode.Match(text);
                    if (!m.Success || known.Contains(m.Groups[1].Value)) continue;
                    string code = m.Groups[1].Value;
                    __result.Add(System.IO.Path.GetFileName(file));
                    Core.Log.Warning($"properties: '{code}' is not in the world; its saved state '{System.IO.Path.GetFileName(file)}' is kept unchanged");
                }
            }
            catch (Exception e)
            {
                Core.Log.Error($"properties: checking for orphaned expansion {kind} saves failed; the save may delete them: {e}");
            }
        }

        public static void Clear()
        {
            if (_spawnRoutine != null) MelonCoroutines.Stop(_spawnRoutine);
            _spawnRoutine = null;
            if (_deferredHeatmap != null) Core.Log.Warning("properties: the main world closed before the heatmap started");
            _deferredHeatmap = null;
            foreach (var c in Copies)
                if (c.Poster != null) Object.Destroy(c.Poster.gameObject);
            if (Copies.Count > 0) Core.Log.Msg($"properties: {Copies.Count} properties removed with the main world");
            Copies.Clear();
            RealtorListings.Clear();
        }
    }
}
