#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.Building;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Growing;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.StationFramework;
using Il2CppScheduleOne.Tiles;
using UnityEngine;
using UnityEngine.AI;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using Property = Il2CppScheduleOne.Property.Property;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Whether employees can work at objects on a property's expansion tiles, all under <c>mxreach</c>. An employee
    /// takes a task only when <c>NavMeshUtility.GetReachableAccessPoint</c> finds an access point inside the property
    /// bounds that its NavMesh path reaches; that check reads a static NavMesh sample cache.
    ///
    /// mxreach code [clear]                       per employee: vanilla reach of every object, expansion tiles summed up
    /// mxreach place code itemId wing|old [rot] [grid]   builds an item on a free tile of the expansion or of the old
    ///                                            tiles, on the property's grid with that index (default 0)
    /// mxreach setup code [wing]                  assigns every bed, pot, drying rack and spawn station to each botanist, a
    ///                                            storage rack as supplies (with wing: one on the expansion tiles, and only
    ///                                            the drying racks on the expansion tiles stay assigned)
    /// mxreach stock code itemId qty              puts items into the botanist's supplies, else the first storage rack
    /// mxreach work code                          each employee's active work and reported issues
    /// </summary>
    internal static class ReachTest
    {
        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "place" when parts.Length > 4: Place(parts[2], parts[3], parts[4], parts.Length > 5 ? int.Parse(parts[5]) : 0, parts.Length > 6 ? int.Parse(parts[6]) : 0); break;
                case "setup" when parts.Length > 2: Setup(parts[2], parts.Length > 3 && parts[3] == "wing"); break;
                case "stock" when parts.Length > 4: Stock(parts[2], parts[3], int.Parse(parts[4])); break;
                case "work" when parts.Length > 2: Work(parts[2]); break;
                case "keepcache" when parts.Length > 2:
                    MapExpansions.Navigation.PropertyNavigation.KeepSamplesForTest = parts[2] == "on";
                    Core.Log.Msg($"[dev] mxreach keepcache: {(MapExpansions.Navigation.PropertyNavigation.KeepSamplesForTest ? "the sample cache survives bakes" : "bakes clear the sample cache")}");
                    break;
                case "sample" when parts.Length > 4:
                {
                    var at = new Vector3(float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture));
                    bool hit = NavMeshUtility.SamplePosition(at, out var h, 2f, -1, true);
                    Core.Log.Msg($"[dev] mxreach sample: {at} -> {(hit ? h.position.ToString() + $" ({Vector3.Distance(h.position, at):0.00} m)" : "no NavMesh within 2 m")}, cache {NavMeshUtility.sampleCacheKeys?.Count ?? -1} entries");
                    break;
                }
                case "fill" when parts.Length > 2: Fill(parts[2], parts.Length > 3 ? int.Parse(parts[3]) : 2); break;
                case "" or "help":
                    Core.Log.Warning("[dev] mxreach code [clear] | place code itemId wing|old [rot] | setup code [wing] | stock code itemId qty | work code");
                    break;
                default: Report(parts[1], parts.Length > 2 && parts[2] == "clear"); break;
            }
        }

        private static Property Find(string code)
        {
            foreach (var p in Property.Properties)
                if (p != null && string.Equals(p.PropertyCode, code, StringComparison.OrdinalIgnoreCase)) return p;
            Core.Log.Warning($"[dev] mxreach: no property has the code '{code}'");
            return null;
        }

        private static bool IsExpansionTile(Tile t) => t != null && t.name.StartsWith("MapExpansions.", StringComparison.Ordinal);

        private static ITransitEntity Transit(BuildableItem item)
        {
            if (item == null || !Il2CppType.Of<ITransitEntity>().IsAssignableFrom(item.GetIl2CppType())) return null;
            return new ITransitEntity(item.Pointer);
        }

        private static string Name(BuildableItem item) => item.ItemInstance?.Definition?.ID ?? item.name;

        // The same steps as NPCMovement.CanGetTo(position, 1f, out path), with the cached and the fresh sample apart.
        private static string Probe(Property p, Employee e, Vector3 at, out bool inBounds, out bool reach, out bool stale)
        {
            inBounds = p.DoBoundsContainPoint(at);
            bool cached = NavMeshUtility.SamplePosition(at, out var cachedHit, 2f, -1, true);
            bool fresh = NavMesh.SamplePosition(at, out var freshHit, 2f, -1);
            // The cache answers any query within 0.5 m of a stored one with that one's hit, and an item standing on the
            // tile pushes a fresh sample aside, so points apart on the same floor are expected. Stale is a hit cached
            // on another floor, such as the ground under a loft built later.
            stale = cached && fresh && Mathf.Abs(cachedHit.position.y - freshHit.position.y) > 0.3f;
            reach = e.Movement.CanGetTo(at, 1f, out NavMeshPath _);
            return $"bounds {(inBounds ? "in" : "OUT")}, cached sample {(cached ? cachedHit.position.ToString() : "none")}, " +
                   $"fresh sample {(fresh ? freshHit.position.ToString() : "none")}{(stale ? " (STALE)" : "")}, path {(reach ? "yes" : "NO")}";
        }

        private static void Report(string code, bool clear)
        {
            var p = Find(code);
            if (p == null) return;
            if (clear)
            {
                int before = NavMeshUtility.sampleCacheKeys?.Count ?? -1;
                NavMeshUtility.ClearCache();
                Core.Log.Msg($"[dev] mxreach: cleared the NavMesh sample cache ({before} entries)");
            }
            Core.Log.Msg($"[dev] mxreach: '{p.PropertyCode}', {p.Employees.Count} employees, {p.BuildableItems.Count} objects, " +
                         $"sample cache {NavMeshUtility.sampleCacheKeys?.Count ?? -1} entries");
            if (p.Employees.Count == 0) { Core.Log.Warning("[dev] mxreach: the property has no employee to measure with"); return; }
            var tiles = p.Grids.ToArray().SelectMany(g => g.Tiles.ToArray()).Where(IsExpansionTile).ToList();
            foreach (var e in p.Employees)
            {
                if (e == null) continue;
                Core.Log.Msg($"[dev] mxreach: employee {e.FirstName} ({e.GetIl2CppType().Name}) at {e.transform.position}, on NavMesh {e.Movement._agent.isOnNavMesh}");
                foreach (var item in p.BuildableItems)
                {
                    var entity = Transit(item);
                    if (entity == null) continue;
                    var points = entity.AccessPoints;
                    var reachable = NavMeshUtility.GetReachableAccessPoint(entity, e);
                    bool onExpansion = item.TryCast<GridItem>() is GridItem gi && gi.CoordinatePairs != null &&
                                       gi.CoordinatePairs.ToArray().Any(c => IsExpansionTile(gi.OwnerGrid?.GetTile(c.coord2)));
                    Core.Log.Msg($"[dev] mxreach:   {Name(item)} at {item.transform.position}{(onExpansion ? " on expansion tiles" : "")}: " +
                                 $"vanilla reach {(reachable != null ? "yes" : "NO")}, {points?.Length ?? 0} access points");
                    if (points == null) continue;
                    for (int i = 0; i < points.Length; i++)
                        if (points[i] != null)
                            Core.Log.Msg($"[dev] mxreach:     point {i} at {points[i].position}: {Probe(p, e, points[i].position, out _, out _, out _)}");
                }
                int outside = 0, unreachable = 0, stale = 0;
                string firstBad = null;
                foreach (var t in tiles)
                {
                    // A tile's cell runs along its grid's axes, which a turned grid points elsewhere than world x and z.
                    var at = t.transform.position + t.transform.rotation * new Vector3(Grid.TileSize / 2f, 0f, Grid.TileSize / 2f);
                    string line = Probe(p, e, at, out bool inBounds, out bool reach, out bool isStale);
                    if (!inBounds) outside++;
                    if (!reach) unreachable++;
                    if (isStale) stale++;
                    if ((!inBounds || !reach || isStale) && firstBad == null) firstBad = $"tile {t.x},{t.y} at {at}: {line}";
                }
                Core.Log.Msg($"[dev] mxreach:   {tiles.Count} expansion tiles: {outside} outside the bounds, {unreachable} without a path, {stale} cached on another floor" +
                             (firstBad != null ? $"; first: {firstBad}" : ""));
            }
        }

        private static void Place(string code, string itemId, string where, int rotation, int gridIndex)
        {
            var p = Find(code);
            if (p == null) return;
            var def = Il2CppScheduleOne.Registry.GetItem(itemId);
            if (def == null) { Core.Log.Warning($"[dev] mxreach place: no item '{itemId}'"); return; }
            var grid = gridIndex >= 0 && gridIndex < p.Grids.Count ? p.Grids[gridIndex] : null;
            if (grid == null) { Core.Log.Warning($"[dev] mxreach place: '{code}' has no grid {gridIndex} ({p.Grids.Count} grids)"); return; }
            bool wing = where == "wing";
            var candidates = grid.Tiles.ToArray().Where(t => t != null && IsExpansionTile(t) == wing).ToList();
            if (candidates.Count == 0) { Core.Log.Warning($"[dev] mxreach place: no {(wing ? "expansion" : "old")} tiles on grid {gridIndex} ({grid.name})"); return; }
            // The game destroys a grid item whose footprint leaves the grid, so every footprint tile, plus a one-tile
            // walkway around it, has to be a free tile of the chosen kind.
            var built0 = def.TryCast<BuildableItemDefinition>()?.BuiltItem?.TryCast<GridItem>();
            if (built0 == null) { Core.Log.Warning($"[dev] mxreach place: '{itemId}' is not a grid item"); return; }
            var footprint = Coordinate.BuildCoordinateMatches(new Coordinate(0, 0), built0.FootprintX, built0.FootprintY, rotation).ToArray().Select(c => (c.coord2.x, c.coord2.y)).ToList();
            var byCoord = candidates.ToDictionary(t => (t.x, t.y));
            bool Free(int x, int y) => byCoord.TryGetValue((x, y), out var t) && (t.BuildableOccupants?.Count ?? 0) == 0 && (t.OccupantTiles?.Count ?? 0) == 0;
            bool Fits(Tile t) => footprint.All(f => Free(t.x + f.Item1, t.y + f.Item2)) &&
                                 footprint.All(f => Enumerable.Range(-1, 3).All(dx => Enumerable.Range(-1, 3).All(dy =>
                                     byCoord.ContainsKey((t.x + f.Item1 + dx, t.y + f.Item2 + dy)) ? Free(t.x + f.Item1 + dx, t.y + f.Item2 + dy) : true)));
            var cx = candidates.Average(t => t.x);
            var cy = candidates.Average(t => t.y);
            var origin = candidates.Where(Fits).OrderBy(t => (t.x - cx) * (t.x - cx) + (t.y - cy) * (t.y - cy)).FirstOrDefault();
            if (origin == null) { Core.Log.Warning($"[dev] mxreach place: no free {built0.FootprintX} by {built0.FootprintY} spot with a walkway on the {(wing ? "expansion" : "old")} tiles"); return; }
            var built = NetworkSingleton<BuildManager>.Instance.CreateGridItem(def.GetDefaultInstance(1), grid, new Vector2(origin.x, origin.y), rotation);
            if (built == null) { Core.Log.Error($"[dev] mxreach place: the game did not build '{itemId}'"); return; }
            Core.Log.Msg($"[dev] mxreach place: built {itemId} on {(wing ? "expansion" : "old")} tile {origin.x},{origin.y} of grid {gridIndex} ({grid.name}, guid {grid.GUID}) at {built.transform.position}, guid {built.GUID}");
        }

        private static bool OnExpansion(BuildableItem item) =>
            item?.TryCast<GridItem>() is GridItem gi && gi.CoordinatePairs != null &&
            gi.CoordinatePairs.ToArray().Any(c => IsExpansionTile(gi.OwnerGrid?.GetTile(c.coord2)));

        private static void Setup(string code, bool wingSupplies)
        {
            var p = Find(code);
            if (p == null) return;
            var racks = p.BuildableItems.ToArray().Where(i => i?.TryCast<PlaceableStorageEntity>() != null && (i.ItemInstance?.ID?.Contains("storagerack") ?? false)).ToList();
            var storage = wingSupplies ? racks.FirstOrDefault(OnExpansion) : racks.FirstOrDefault();
            if (wingSupplies && storage == null) Core.Log.Warning($"[dev] mxreach setup: '{code}' has no storage rack on the expansion tiles; supplies stay unset");
            int botanists = 0;
            foreach (var e in p.Employees)
            {
                var b = e?.TryCast<Botanist>();
                if (b == null) continue;
                botanists++;
                var config = b.configuration;
                if (config == null) { Core.Log.Warning($"[dev] mxreach setup: botanist {b.FirstName} has no configuration yet"); continue; }
                int added = 0;
                foreach (var item in p.BuildableItems)
                {
                    if (item == null || item.TryCast<GrowContainer>() == null && item.TryCast<MushroomSpawnStation>() == null && item.TryCast<DryingRack>() == null) continue;
                    if (config.Assigns.SelectedObjects.Contains(item)) continue;
                    config.Assigns.AddItem(item);
                    added++;
                }
                // With wing, only the drying racks on the expansion stay assigned, so the botanist has to use one of them.
                if (wingSupplies)
                    foreach (var item in config.Assigns.SelectedObjects.ToArray())
                        if (item?.TryCast<DryingRack>() != null && !OnExpansion(item)) config.Assigns.RemoveItem(item);
                if (storage != null) config.Supplies.SetObject(storage, true);
                // Without a locker and the day's pay the botanist never reaches its task checks.
                var locker = p.BuildableItems.ToArray().FirstOrDefault(i => i != null && i.GetComponent<EmployeeHome>() != null);
                if (locker != null) config.Home.SetObject(locker, true);
                b.SetIsPaid();
                Core.Log.Msg($"[dev] mxreach setup: {b.FirstName} got {added} more objects ({config.Assigns.SelectedObjects.Count} assigned, {config.AssignedRacks.Count} drying racks), " +
                             $"supplies {(storage != null ? Name(storage) + " at " + storage.transform.position + (OnExpansion(storage) ? " on expansion tiles" : "") : "none on the property")}, locker {(locker != null ? Name(locker) : "none on the property")}, paid for today");
            }
            if (botanists == 0) Core.Log.Warning($"[dev] mxreach setup: '{code}' has no botanist; hire one with 'addemployee botanist {code}'");
            // Handlers need the same locker and pay before they look at their routes.
            var home = p.BuildableItems.ToArray().FirstOrDefault(i => i != null && i.GetComponent<EmployeeHome>() != null);
            foreach (var e in p.Employees)
            {
                var h = e?.TryCast<Packager>();
                if (h == null) continue;
                var config = h.Configuration?.TryCast<PackagerConfiguration>();
                if (config == null) { Core.Log.Warning($"[dev] mxreach setup: handler {h.FirstName} has no configuration yet"); continue; }
                if (home != null) config.Home.SetObject(home, true);
                h.SetIsPaid();
                Core.Log.Msg($"[dev] mxreach setup: handler {h.FirstName} locker {(home != null ? Name(home) : "none on the property")}, paid for today");
            }
        }

        private static void Stock(string code, string itemId, int qty)
        {
            var p = Find(code);
            if (p == null) return;
            PlaceableStorageEntity storage = null;
            foreach (var e in p.Employees)
            {
                var supplies = e?.TryCast<Botanist>()?.configuration?.Supplies?.SelectedObject;
                storage = supplies?.TryCast<PlaceableStorageEntity>();
                if (storage != null) break;
            }
            storage ??= p.BuildableItems.ToArray().Where(i => i?.ItemInstance?.ID?.Contains("storagerack") ?? false).Select(i => i.TryCast<PlaceableStorageEntity>()).FirstOrDefault(s => s != null);
            if (storage == null) { Core.Log.Warning($"[dev] mxreach stock: '{code}' has no storage"); return; }
            var def = Il2CppScheduleOne.Registry.GetItem(itemId);
            if (def == null) { Core.Log.Warning($"[dev] mxreach stock: no item '{itemId}'"); return; }
            storage.StorageEntity.InsertItem(def.GetDefaultInstance(qty), true);
            Core.Log.Msg($"[dev] mxreach stock: put {qty} {itemId} into {Name(storage)} at {storage.transform.position}{(OnExpansion(storage) ? " on expansion tiles" : "")}");
        }

        private static void Work(string code)
        {
            var p = Find(code);
            if (p == null) return;
            foreach (var e in p.Employees)
            {
                if (e == null) continue;
                var active = e.Behaviour?.activeBehaviour;
                var issues = e.WorkIssues?.ToArray().Select(w => $"'{w.Reason}' ({w.Fix})").ToList() ?? new List<string>();
                Core.Log.Msg($"[dev] mxreach work: {e.FirstName} at {e.transform.position}, active {(active != null ? active.Name : "none")}, " +
                             $"issues: {(issues.Count > 0 ? string.Join("; ", issues) : "none")}");
                foreach (var item in p.BuildableItems)
                {
                    var rack = item?.TryCast<DryingRack>();
                    if (rack == null) continue;
                    Core.Log.Msg($"[dev] mxreach work:   drying rack at {item.transform.position}{(OnExpansion(item) ? " on expansion tiles" : "")}: " +
                                 $"input {rack.InputSlot.Quantity}, drying {rack.GetTotalDryingItems()}, output {rack.OutputSlot.Quantity}");
                }
                foreach (var item in p.BuildableItems)
                {
                    var station = item?.TryCast<MushroomSpawnStation>();
                    if (station == null) continue;
                    // UseSpawnStationBehaviour.IsStationReady asks CanGetTo(station, 0.6f): an access point within 0.6 m of the NavMesh.
                    var entity = Transit(item);
                    bool reach06 = entity != null && e.Movement.CanGetTo(entity, 0.6f);
                    var gaps = new List<string>();
                    if (entity?.AccessPoints != null)
                        foreach (var ap in entity.AccessPoints)
                            if (ap != null)
                                gaps.Add(NavMesh.SamplePosition(ap.position, out var hit, 2f, -1) ? $"{Vector3.Distance(hit.position, ap.position):0.00} m" : "no NavMesh");
                    Core.Log.Msg($"[dev] mxreach work:   station {Name(item)} at {item.transform.position}: grain {station.GrainBagSlot.Quantity}, syringe {station.SyringeSlot.Quantity}, " +
                                 $"output {station.OutputSlot.Quantity}, in use {station.NPCUserObject != null}, reach at 0.6 m {(reach06 ? "yes" : "NO")}, access point to NavMesh {string.Join(", ", gaps)}");
                }
            }
        }

        // Puts grain bags and spore syringes straight into every spawn station, as a player does by hand.
        private static void Fill(string code, int qty)
        {
            var p = Find(code);
            if (p == null) return;
            var grain = Il2CppScheduleOne.Registry.GetItem("grainbag");
            var syringe = Il2CppScheduleOne.Registry.GetItem("sporesyringe");
            if (grain == null || syringe == null) { Core.Log.Warning("[dev] mxreach fill: the item 'grainbag' or 'sporesyringe' is missing from the registry"); return; }
            int filled = 0;
            foreach (var item in p.BuildableItems)
            {
                var station = item?.TryCast<MushroomSpawnStation>();
                if (station == null) continue;
                station.GrainBagSlot.InsertItem(grain.GetDefaultInstance(qty));
                station.SyringeSlot.InsertItem(syringe.GetDefaultInstance(qty));
                Core.Log.Msg($"[dev] mxreach fill: station at {item.transform.position} now holds grain {station.GrainBagSlot.Quantity}, syringe {station.SyringeSlot.Quantity}");
                filled++;
            }
            if (filled == 0) Core.Log.Warning($"[dev] mxreach fill: '{code}' has no spawn station");
        }
    }
}
#endif
