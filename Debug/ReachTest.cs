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
                case "at" when parts.Length > 5: PlaceAt(parts[2], parts[3], int.Parse(parts[4]), int.Parse(parts[5]), parts.Length > 6 ? int.Parse(parts[6]) : 0, parts.Length > 7 ? int.Parse(parts[7]) : 0); break;
                case "plant" when parts.Length > 3: PlantAll(parts[2], parts[3], parts.Length > 4 && parts[4] == "grown"); break;
                case "capacity" when parts.Length > 3:
                {
                    var p = Find(parts[2]);
                    if (p == null) break;
                    int before = p.EmployeeCapacity;
                    p.EmployeeCapacity = int.Parse(parts[3]);
                    Core.Log.Msg($"[dev] mxreach capacity: '{p.PropertyCode}' employee capacity {before} -> {p.EmployeeCapacity}, {p.Employees.Count} employed");
                    break;
                }
                case "route" when parts.Length > 3: Route(parts[2], parts[3]); break;
                case "trace" when parts.Length > 2: RepathTrace.Toggle(parts[2] == "on"); break;
                case "navmap" when parts.Length > 2: NavMap(parts[2], parts.Length > 3 ? parts[3] : "Employee", parts.Length > 4 ? int.Parse(parts[4]) : 0); break;
                case "tiles" when parts.Length > 2:Tiles(parts[2], parts.Length > 3 ? int.Parse(parts[3]) : 0); break;
                case "clear" when parts.Length > 6: Clear(parts[2], int.Parse(parts[3]), int.Parse(parts[4]), int.Parse(parts[5]), int.Parse(parts[6])); break;
                case "setup" when parts.Length > 2: Setup(parts[2], parts.Length > 3 && parts[3] == "wing"); break;
                case "stock" when parts.Length > 4: Stock(parts[2], parts[3], int.Parse(parts[4])); break;
                case "work" when parts.Length > 2: Work(parts[2]); break;
                case "pots" when parts.Length > 2: Pots(parts[2]); break;
                case "ripen" when parts.Length > 2: Ripen(parts[2]); break;
                case "path" when parts.Length > 3: PathOf(parts[2], parts[3]); break;
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

        /// <summary>
        /// The input slots that refuse items: a slot locked by a worker counts as full for every other worker
        /// (ITransitEntity.GetInputCapacityForItem), so a lock that outlives its carry trip hides the slot.
        /// </summary>
        internal static string InputLocks(ITransitEntity t)
        {
            if (t == null) return "no transit entity";
            var slots = t.InputSlots;
            if (slots == null) return "no input slots";
            var parts = new List<string>();
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (s == null) continue;
                if (s.ActiveLock != null)
                    parts.Add($"slot {i} locked by {s.ActiveLock.LockOwner?.gameObject.name ?? "nobody"} ('{s.ActiveLock.LockReason}')");
                if (s.IsAddLocked) parts.Add($"slot {i} add-locked");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "no locks";
        }

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

        /// <summary>Builds an item with its origin on one given tile, as a player would, without the walkway rule of place.</summary>
        private static void PlaceAt(string code, string itemId, int x, int y, int rotation, int gridIndex)
        {
            var p = Find(code);
            if (p == null) return;
            var def = Il2CppScheduleOne.Registry.GetItem(itemId);
            var built0 = def?.TryCast<BuildableItemDefinition>()?.BuiltItem?.TryCast<GridItem>();
            if (built0 == null) { Core.Log.Warning($"[dev] mxreach at: '{itemId}' is not a grid item"); return; }
            var grid = gridIndex >= 0 && gridIndex < p.Grids.Count ? p.Grids[gridIndex] : null;
            if (grid == null) { Core.Log.Warning($"[dev] mxreach at: '{code}' has no grid {gridIndex}"); return; }
            foreach (var c in Coordinate.BuildCoordinateMatches(new Coordinate(x, y), built0.FootprintX, built0.FootprintY, rotation))
            {
                var t = grid.GetTile(c.coord2);
                if (t == null) { Core.Log.Warning($"[dev] mxreach at: footprint tile {c.coord2.x},{c.coord2.y} is not on grid {gridIndex}"); return; }
                if ((t.BuildableOccupants?.Count ?? 0) > 0 || (t.OccupantTiles?.Count ?? 0) > 0) { Core.Log.Warning($"[dev] mxreach at: footprint tile {t.x},{t.y} is occupied by {Occupants(t)}"); return; }
            }
            var built = NetworkSingleton<BuildManager>.Instance.CreateGridItem(def.GetDefaultInstance(1), grid, new Vector2(x, y), rotation);
            if (built == null) { Core.Log.Error($"[dev] mxreach at: the game did not build '{itemId}' at {x},{y}"); return; }
            Core.Log.Msg($"[dev] mxreach at: built {itemId} at tile {x},{y} rot {rotation} of grid {gridIndex} at {built.transform.position}, guid {built.GUID}");
        }

        /// <summary>Removes every grid item with a footprint tile inside the tile rectangle x1..x2, y1..y2, as picking it up would.</summary>
        /// <summary>The items a tile holds: its grid occupants and the items whose footprint tiles sit on it.</summary>
        private static List<BuildableItem> OccupantItems(Tile t)
        {
            var items = new List<BuildableItem>();
            foreach (var occupant in t.BuildableOccupants?.ToArray() ?? Array.Empty<GridItem>())
                if (occupant != null) items.Add(occupant);
            foreach (var ft in t.OccupantTiles?.ToArray() ?? Array.Empty<FootprintTile>())
            {
                var owner = ft?.GetComponentInParent<BuildableItem>();
                if (owner != null && !items.Any(i => i.Pointer == owner.Pointer)) items.Add(owner);
            }
            return items;
        }

        private static string Occupants(Tile t)
        {
            var items = OccupantItems(t);
            return items.Count == 0 ? $"{t.OccupantTiles?.Count ?? 0} footprint tiles without an item"
                : string.Join(", ", items.Select(i => $"{Name(i)} at {i.transform.position}"));
        }

        private static void Clear(string code, int x1, int y1, int x2, int y2)
        {
            var p = Find(code);
            if (p == null) return;
            int removed = 0;
            bool InRect(int x, int y) => x >= x1 && x <= x2 && y >= y1 && y <= y2;
            var doomed = new List<BuildableItem>();
            foreach (var item in p.BuildableItems.ToArray())
            {
                var gi = item?.TryCast<GridItem>();
                if (gi?.CoordinatePairs != null && gi.CoordinatePairs.ToArray().Any(c => InRect(c.coord2.x, c.coord2.y))) doomed.Add(item);
            }
            // Items the tiles list as occupants, such as closets whose footprint pairs lie on another grid.
            foreach (var grid in p.Grids)
                foreach (var t in grid?.Tiles?.ToArray() ?? Array.Empty<Tile>())
                {
                    if (t == null || !InRect(t.x, t.y)) continue;
                    foreach (var occupant in OccupantItems(t))
                        if (!doomed.Any(d => d.Pointer == occupant.Pointer)) doomed.Add(occupant);
                }
            foreach (var item in doomed)
            {
                Core.Log.Msg($"[dev] mxreach clear: removing {Name(item)} at {item.transform.position}");
                try
                {
                    item.Destroy_Server();
                    removed++;
                }
                catch (Exception e)
                {
                    // Vanilla UI callbacks of the destroy can throw (a locker refreshes a closed management panel); the item may be gone anyway.
                    Core.Log.Warning($"[dev] mxreach clear: destroying {Name(item)} threw, continuing: {e.Message.Split('\n')[0]}");
                }
            }
            Core.Log.Msg($"[dev] mxreach clear: removed {removed} items from tiles {x1},{y1}..{x2},{y2} of '{code}'");
        }

        /// <summary>Prints the tile coordinate range of a grid, the expansion tiles' range, and a map row per y (. old, + expansion, # occupied).</summary>
        private static void Tiles(string code, int gridIndex)
        {
            var p = Find(code);
            if (p == null) return;
            var grid = gridIndex >= 0 && gridIndex < p.Grids.Count ? p.Grids[gridIndex] : null;
            if (grid == null) { Core.Log.Warning($"[dev] mxreach tiles: '{code}' has no grid {gridIndex}"); return; }
            var tiles = grid.Tiles.ToArray().Where(t => t != null).ToList();
            var byCoord = new Dictionary<(int, int), Tile>();
            foreach (var t in tiles) byCoord[(t.x, t.y)] = t;
            int minX = tiles.Min(t => t.x), maxX = tiles.Max(t => t.x), minY = tiles.Min(t => t.y), maxY = tiles.Max(t => t.y);
            var o = grid.GetTile(new Coordinate(minX, minY));
            Core.Log.Msg($"[dev] mxreach tiles: grid {gridIndex} ({grid.name}) x {minX}..{maxX}, y {minY}..{maxY}, {tiles.Count} tiles, " +
                         $"{tiles.Count(IsExpansionTile)} expansion; tile {minX},{minY} at {o?.transform.position}, grid rotation {grid.transform.eulerAngles}");
            for (int y = maxY; y >= minY; y--)
            {
                var row = new System.Text.StringBuilder();
                for (int x = minX; x <= maxX; x++)
                    row.Append(!byCoord.TryGetValue((x, y), out var t) ? ' ' :
                        (t.BuildableOccupants?.Count ?? 0) > 0 || (t.OccupantTiles?.Count ?? 0) > 0 ? '#' : IsExpansionTile(t) ? '+' : '.');
                Core.Log.Msg($"[dev] mxreach tiles: y {y,4} {row}");
            }
        }

        /// <summary>Fills every empty pot of a property with soil and water and sows a seed; <c>grown</c> also grows it fully.</summary>
        private static void PlantAll(string code, string seedId, bool grown)
        {
            var p = Find(code);
            if (p == null) return;
            var soil = Il2CppScheduleOne.Registry.GetItem("soil")?.TryCast<SoilDefinition>();
            if (soil == null) { Core.Log.Warning("[dev] mxreach plant: no soil definition 'soil'"); return; }
            if (Il2CppScheduleOne.Registry.GetItem(seedId) == null) { Core.Log.Warning($"[dev] mxreach plant: no item '{seedId}'"); return; }
            int sown = 0, skipped = 0;
            foreach (var item in p.BuildableItems.ToArray())
            {
                var pot = item?.TryCast<Pot>();
                if (pot == null) continue;
                if (pot.Plant != null) { skipped++; continue; }
                pot.SetSoilData_Server(soil.ID, pot.SoilCapacity, soil.Uses);
                pot.SetMoistureData_Server(pot.MoistureCapacity);
                // The second argument is the plant's starting growth. A plant sown within a few seconds of building its
                // pot is gone a moment later; sowing again afterwards keeps it.
                pot.PlantSeed_Server(seedId, 0f);
                if (grown) pot.SetGrowthProgress_Server(1f);
                sown++;
            }
            Core.Log.Msg($"[dev] mxreach plant: '{code}' sowed {seedId} in {sown} pots{(grown ? ", fully grown" : "")}; {skipped} pots already had a plant");
        }

        /// <summary>
        /// One character per grid tile for one agent type: '.' NavMesh at the tile centre that the property's first idle
        /// point reaches, 'x' NavMesh the idle point does not reach, '-' a tile without NavMesh within 0.12 m.
        /// </summary>
        private static void NavMap(string code, string agentName, int gridIndex)
        {
            var p = Find(code);
            if (p == null) return;
            var grid = gridIndex >= 0 && gridIndex < p.Grids.Count ? p.Grids[gridIndex] : null;
            if (grid == null) { Core.Log.Warning($"[dev] mxreach navmap: '{code}' has no grid {gridIndex}"); return; }
            int? agent = null;
            var names = new List<string>();
            foreach (int a in MapExpansions.Navigation.NpcNavigation.AgentTypes())
            {
                string name = MapExpansions.Navigation.NpcNavigation.AgentName(a)?.Trim() ?? "";
                names.Add($"{name} ({a})");
                if (string.Equals(name, agentName.Trim(), StringComparison.OrdinalIgnoreCase)) agent = a;
            }
            if (agent == null) { Core.Log.Warning($"[dev] mxreach navmap: no agent type named '{agentName}'; known: {string.Join(", ", names)}"); return; }
            var idle = p.EmployeeIdlePoints?.FirstOrDefault(t => t != null);
            if (idle == null) { Core.Log.Warning($"[dev] mxreach navmap: '{code}' has no employee idle point to path from"); return; }
            int agentId = agent.Value;
            NavMeshQueryFilter Filter() => new NavMeshQueryFilter { agentTypeID = agentId, areaMask = NavMesh.AllAreas };
            if (!NavMesh.SamplePosition(idle.position, out NavMeshHit from, 1f, Filter()))
            {
                Core.Log.Warning($"[dev] mxreach navmap: idle point {idle.position} has no NavMesh for '{agentName}' within 1 m");
                return;
            }
            var tiles = grid.Tiles.ToArray().Where(t => t != null).ToList();
            var byCoord = new Dictionary<(int, int), Tile>();
            foreach (var t in tiles) byCoord[(t.x, t.y)] = t;
            int minX = tiles.Min(t => t.x), maxX = tiles.Max(t => t.x), minY = tiles.Min(t => t.y), maxY = tiles.Max(t => t.y);
            int none = 0, island = 0;
            Core.Log.Msg($"[dev] mxreach navmap: '{code}' grid {gridIndex} for '{agentName}' ({agent}), paths from idle point {from.position}");
            for (int y = maxY; y >= minY; y--)
            {
                var row = new System.Text.StringBuilder();
                for (int x = minX; x <= maxX; x++)
                {
                    if (!byCoord.TryGetValue((x, y), out var t)) { row.Append(' '); continue; }
                    if (!NavMesh.SamplePosition(t.transform.position, out NavMeshHit hit, 0.12f, Filter())) { row.Append('-'); none++; continue; }
                    var path = new NavMeshPath();
                    bool reached = NavMesh.CalculatePath(from.position, hit.position, Filter(), path) && path.status == NavMeshPathStatus.PathComplete;
                    if (!reached) island++;
                    row.Append(reached ? '.' : 'x');
                }
                Core.Log.Msg($"[dev] mxreach navmap: y {y,4} {row}");
            }
            Core.Log.Msg($"[dev] mxreach navmap: {tiles.Count} tiles, {none} without NavMesh, {island} not reached from the idle point");
        }

        private static bool OnExpansion(BuildableItem item) =>
            item?.TryCast<GridItem>() is GridItem gi && gi.CoordinatePairs != null &&
            gi.CoordinatePairs.ToArray().Any(c => IsExpansionTile(gi.OwnerGrid?.GetTile(c.coord2)));

        // Every pot sends its harvest to the nearest drying rack and is sown with one seed, as a player sets it up with
        // the management clipboard; without a destination a botanist leaves ripe plants standing.
        private static void Route(string code, string seedId)
        {
            var p = Find(code);
            if (p == null) return;
            var seed = Il2CppScheduleOne.Registry.GetItem(seedId);
            if (seed == null) { Core.Log.Warning($"[dev] mxreach route: no item '{seedId}'"); return; }
            var racks = p.BuildableItems.ToArray().Where(i => i?.TryCast<DryingRack>() != null).ToList();
            if (racks.Count == 0) { Core.Log.Warning($"[dev] mxreach route: '{code}' has no drying rack"); return; }
            int pots = 0, failed = 0;
            foreach (var item in p.BuildableItems)
            {
                var pot = item?.TryCast<Pot>();
                if (pot == null) continue;
                var config = pot.potConfiguration;
                if (config == null) { failed++; continue; }
                var rack = racks.OrderBy(r => Vector3.Distance(r.transform.position, pot.transform.position)).First();
                config.Destination.SetObject(rack, true);
                config.Seed.SetItem(seed, true);
                pots++;
            }
            if (failed > 0) Core.Log.Warning($"[dev] mxreach route: {failed} pots have no configuration yet");
            Core.Log.Msg($"[dev] mxreach route: '{code}' {pots} pots send their harvest to the nearest of {racks.Count} drying racks and are sown with {seedId}");
        }

        private static void Setup(string code, bool wingSupplies)
        {
            var p = Find(code);
            if (p == null) return;
            var racks = p.BuildableItems.ToArray().Where(i => i?.TryCast<PlaceableStorageEntity>() != null && (i.ItemInstance?.ID?.Contains("storagerack") ?? false)).ToList();
            var anyStorage = p.BuildableItems.ToArray().Where(i => i?.TryCast<PlaceableStorageEntity>() != null).ToList();
            var storage = wingSupplies ? racks.FirstOrDefault(OnExpansion) : racks.FirstOrDefault() ?? anyStorage.FirstOrDefault();
            if (wingSupplies && storage == null) Core.Log.Warning($"[dev] mxreach setup: '{code}' has no storage rack on the expansion tiles; supplies stay unset");
            int botanists = 0;
            // A botanist takes a limited number of objects. The northmost go first and each to one botanist only, so
            // the botanists together cover the far end of the wing instead of the same few objects near the door.
            var work = p.BuildableItems.ToArray()
                .Where(i => i != null && (i.TryCast<GrowContainer>() != null || i.TryCast<MushroomSpawnStation>() != null || i.TryCast<DryingRack>() != null))
                .OrderByDescending(i => i.transform.position.z).ToList();
            var taken = new HashSet<IntPtr>();
            foreach (var e in p.Employees)
            {
                var b = e?.TryCast<Botanist>();
                if (b == null) continue;
                botanists++;
                var config = b.configuration;
                if (config == null) { Core.Log.Warning($"[dev] mxreach setup: botanist {b.FirstName} has no configuration yet"); continue; }
                foreach (var item in config.Assigns.SelectedObjects.ToArray()) config.Assigns.RemoveItem(item);
                int added = 0;
                // A botanist takes only as many objects as it may have pots. With wing, the racks on the expansion go in
                // first, and assigned objects elsewhere make room for them.
                var wingRacks = wingSupplies ? p.BuildableItems.ToArray().Where(i => i?.TryCast<DryingRack>() != null && OnExpansion(i)).ToList() : new();
                foreach (var rack in wingRacks)
                {
                    if (config.Assigns.SelectedObjects.Contains(rack)) continue;
                    var spare = config.Assigns.SelectedObjects.ToArray().FirstOrDefault(i => i != null && !OnExpansion(i));
                    if (config.Assigns.SelectedObjects.Count >= config.Assigns.MaxItems && spare != null)
                    {
                        config.Assigns.RemoveItem(spare);
                        Core.Log.Msg($"[dev] mxreach setup: {b.FirstName} gave up {Name(spare)} at {spare.transform.position} to make room for the expansion's drying rack");
                    }
                    config.Assigns.AddItem(rack);
                    added++;
                }
                foreach (var item in work)
                {
                    if (config.Assigns.SelectedObjects.Count >= config.Assigns.MaxItems) break;
                    if (taken.Contains(item.Pointer) || config.Assigns.SelectedObjects.Contains(item)) continue;
                    int before = config.Assigns.SelectedObjects.Count;
                    config.Assigns.AddItem(item);
                    if (config.Assigns.SelectedObjects.Count == before) break;
                    taken.Add(item.Pointer);
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

        /// <summary>One line per pot: position, ready or the reason it is not, the botanist it is assigned to, and its destination.</summary>
        private static void Pots(string code)
        {
            var p = Find(code);
            if (p == null) return;
            var owners = new Dictionary<IntPtr, string>();
            foreach (var e in p.Employees)
            {
                var b = e?.TryCast<Botanist>();
                var config = b?.configuration;
                if (config == null) continue;
                foreach (var assigned in config.AssignedPots)
                    if (assigned != null) owners[assigned.Pointer] = b.FirstName;
            }
            foreach (var item in p.BuildableItems.ToArray())
            {
                var pot = item?.TryCast<Pot>();
                if (pot == null) continue;
                bool ready = pot.IsReadyForHarvest(out var reason);
                var dest = pot.potConfiguration?.Destination?.SelectedObject;
                owners.TryGetValue(pot.Pointer, out var owner);
                float light = pot.GetAverageLightExposure(out float lightSpeed);
                Core.Log.Msg($"[dev] mxreach pots: {pot.transform.position} {(ready ? "ready" : reason)}, botanist {owner ?? "none"}, " +
                             $"destination {(dest != null ? Name(dest) + " at " + dest.transform.position : "none")}; spawned {pot.IsSpawned}, " +
                             $"growth {(pot.Plant != null ? pot.Plant.NormalizedGrowthProgress.ToString("0.00") : "-")}, harvestables {pot.Plant?.ActiveHarvestables?.Count ?? -1}, " +
                             $"light {light:0.00} x{lightSpeed:0.00}, moisture {pot.NormalizedMoistureAmount:0.00}, temperature x{pot.GetTemperatureGrowthMultiplier():0.00}");
            }
        }

        /// <summary>
        /// Grows every plant of a property fully on the host. Calls the plant directly; the growth RPC behind
        /// <c>growplants</c> does not reach pots built in the same session.
        /// </summary>
        private static void Ripen(string code)
        {
            var p = Find(code);
            if (p == null) return;
            int ripened = 0, ready = 0, failed = 0;
            foreach (var item in p.BuildableItems.ToArray())
            {
                var pot = item?.TryCast<Pot>();
                if (pot?.Plant == null) continue;
                if (!pot.Plant.IsFullyGrown)
                {
                    try
                    {
                        pot.Plant.SetNormalizedGrowthProgress(1f);
                        ripened++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Core.Log.Warning($"[dev] mxreach ripen: pot at {pot.transform.position} threw: {e.Message.Split('\n')[0]}");
                    }
                }
                if (pot.IsReadyForHarvest(out _)) ready++;
            }
            Core.Log.Msg($"[dev] mxreach ripen: '{code}' grew {ripened} plants fully ({failed} failed); {ready} pots ready for harvest");
        }

        /// <summary>
        /// Why one employee does or does not walk to its current goal: the agent state, the goal (the walk's destination,
        /// else the idle point), what GetClosestReachablePoint answers, and the raw path from the agent to the goal.
        /// </summary>
        private static void PathOf(string code, string firstName)
        {
            var p = Find(code);
            if (p == null) return;
            var e = p.Employees.ToArray().FirstOrDefault(x => x != null && string.Equals(x.FirstName, firstName, StringComparison.OrdinalIgnoreCase));
            if (e == null) { Core.Log.Warning($"[dev] mxreach path: no employee '{firstName}' on '{p.PropertyCode}'"); return; }
            var m = e.Movement;
            var agent = m._agent;
            var idle = e.WaitOutside?.IdlePoint;
            var goal = m.CurrentDestination != Vector3.zero ? m.CurrentDestination : idle != null ? idle.position : Vector3.zero;
            string head = $"[dev] mxreach path: {e.FirstName} foot {m.FootPosition}, agent at {agent.transform.position}, on mesh {agent.isOnNavMesh}, " +
                          $"on link {agent.isOnOffMeshLink}, hasPath {agent.hasPath}, pending {agent.pathPending}, status {agent.pathStatus}, " +
                          $"remaining {agent.remainingDistance:0.00}, stopping {agent.stoppingDistance:0.00}, stopped {agent.isStopped}, " +
                          $"velocity {agent.velocity.magnitude:0.00}, moving {m.IsMoving}, can move {m.CanMove}, has destination {m._hasDestination}, " +
                          $"active {e.Behaviour?.activeBehaviour?.Name ?? "none"}";
            Core.Log.Msg(head);
            Core.Log.Msg($"[dev] mxreach path: {e.FirstName} goal {goal} ({Vector3.Distance(m.FootPosition, goal):0.00} m), idle point {(idle != null ? idle.position.ToString() : "none")}, " +
                         $"as close as possible {m.IsAsCloseAsPossible(goal)}");
            bool reach = m.GetClosestReachablePoint(goal, out var closest);
            Core.Log.Msg($"[dev] mxreach path: {e.FirstName} closest reachable {(reach ? closest + $" ({Vector3.Distance(m.FootPosition, closest):0.00} m from foot)" : "none")}");
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            if (!NavMeshUtility.SamplePosition(goal, out var hit, 3f, -1, true)) { Core.Log.Msg($"[dev] mxreach path: {e.FirstName} no NavMesh within 3 m of the goal"); return; }
            var path = new NavMeshPath();
            NavMesh.CalculatePath(agent.transform.position, hit.position, filter, path);
            var corners = path.corners;
            var list = new List<string>();
            for (int i = 0; i < corners.Length; i++) list.Add(corners[i].ToString());
            Core.Log.Msg($"[dev] mxreach path: {e.FirstName} raw path to {hit.position}: {path.status}, {corners.Length} corners: {string.Join(" ", list)}");
            bool startHit = NavMesh.SamplePosition(agent.transform.position, out var start, 1f, filter);
            Core.Log.Msg($"[dev] mxreach path: {e.FirstName} NavMesh under the agent: {(startHit ? start.position + $" ({Vector3.Distance(start.position, agent.transform.position):0.00} m)" : "none within 1 m")}");
        }

        private static void Work(string code)
        {
            var p = Find(code);
            if (p == null) return;
            int pots = 0, soil = 0, planted = 0, ready = 0;
            foreach (var item in p.BuildableItems)
            {
                var pot = item?.TryCast<Pot>();
                if (pot == null) continue;
                pots++;
                if (pot.IsFullyFilledWithSoil) soil++;
                if (pot.Plant != null) planted++;
                if (pot.IsReadyForHarvest(out _)) ready++;
            }
            Core.Log.Msg($"[dev] mxreach work: pots {pots}, full of soil {soil}, planted {planted}, ready for harvest {ready}");
            foreach (var e in p.Employees)
            {
                if (e == null) continue;
                var active = e.Behaviour?.activeBehaviour;
                var issues = e.WorkIssues?.ToArray().Select(w => $"'{w.Reason}' ({w.Fix})").ToList() ?? new List<string>();
                var agent = e.Movement?._agent;
                Core.Log.Msg($"[dev] mxreach work: {e.FirstName} at {e.transform.position}, active {(active != null ? active.Name : "none")}, " +
                             $"moving {e.Movement?.IsMoving}, idle point {(e.WaitOutside?.IdlePoint != null ? Vector3.Distance(e.transform.position, e.WaitOutside.IdlePoint.position).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-")} m away, " +
                             $"destination {e.Movement?.CurrentDestination}, " +
                             $"agent {(agent != null && agent.enabled ? $"path {agent.pathStatus}, on link {agent.isOnOffMeshLink}, on mesh {agent.isOnNavMesh}" : "off")}, " +
                             $"issues: {(issues.Count > 0 ? string.Join("; ", issues) : "none")}");
                foreach (var item in p.BuildableItems)
                {
                    var rack = item?.TryCast<DryingRack>();
                    if (rack == null) continue;
                    Core.Log.Msg($"[dev] mxreach work:   drying rack at {item.transform.position}{(OnExpansion(item) ? " on expansion tiles" : "")}: " +
                                 $"input {rack.InputSlot.Quantity}, drying {rack.GetTotalDryingItems()}, output {rack.OutputSlot.Quantity}, {InputLocks(Transit(item))}");
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
