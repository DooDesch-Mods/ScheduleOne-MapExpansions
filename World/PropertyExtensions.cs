using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.Building;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Tiles;
using UnityEngine;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using Property = Il2CppScheduleOne.Property.Property;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    internal sealed class PropertyExtensionLease : IDisposable
    {
        internal Property Property;
        internal string ExtensionId;
        /// <summary>The definition this lease was built from, see <see cref="Lifecycle.WorldStateSignatures"/>.</summary>
        internal string Signature;
        internal IReadOnlyCollection<string> CarrierCopies = Array.Empty<string>();
        /// <summary>Removal has started, so the lease no longer stands for its definition and a transition cannot keep it.</summary>
        internal bool Damaged;
        internal readonly List<(Grid grid, Tile tile)> Tiles = new();
        /// <summary>The grid this extension built for itself, see <see cref="PropertyExtensionDefinition.GridOrigin"/>.</summary>
        internal Grid OwnGrid;
        internal readonly List<BoxCollider> Boxes = new();
        internal readonly List<(Surface surface, bool ownObject)> Surfaces = new();
        internal readonly List<PropertyDockLease> Docks = new();
        internal readonly List<PropertyFixtures.Fixture> Fixtures = new();

        internal void CheckRemovable()
        {
            foreach (var dock in Docks) dock.CheckRemovable();
            if (Property?.BuildableItems != null)
                foreach (var item in Property.BuildableItems)
                    if (item?.TryCast<SurfaceItem>() is SurfaceItem mounted && mounted.ParentSurface != null &&
                        Surfaces.Any(x => x.surface != null && x.surface.Pointer == mounted.ParentSurface.Pointer))
                        throw new InvalidOperationException($"property '{Property.PropertyCode}' added surface '{mounted.ParentSurface.name}' has a mounted item");
            foreach (var (_, tile) in Tiles)
                if (tile != null && ((tile.BuildableOccupants?.Count ?? 0) > 0 || (tile.OccupantTiles?.Count ?? 0) > 0))
                    throw new InvalidOperationException($"property '{Property.PropertyCode}' added tile {tile.x},{tile.y} is occupied");
        }

        public void Dispose()
        {
            CheckRemovable();
            PropertyDocks.CheckRemovalOrder(Docks);
            Damaged = true;
            for (int i = Fixtures.Count - 1; i >= 0; i--)
            {
                PropertyFixtures.Unregister(Fixtures[i]);
                Fixtures.RemoveAt(i);
            }
            // Each part leaves its list once it is gone, so a removal that fails half way resumes on the next try.
            for (int i = Docks.Count - 1; i >= 0; i--)
            {
                Docks[i].Remove();
                Docks.RemoveAt(i);
            }
            for (int i = Surfaces.Count - 1; i >= 0; i--)
            {
                var (surface, ownObject) = Surfaces[i];
                GuidLeases.Release(surface, $"surface of '{ExtensionId}'");
                if (surface != null)
                {
                    if (ownObject) { surface.gameObject.SetActive(false); Object.Destroy(surface.gameObject); }
                    else Object.DestroyImmediate(surface);
                }
                Surfaces.RemoveAt(i);
            }
            if ((object)OwnGrid != null)
            {
                // The tiles go with their grid; the grid leaves the property's list, its GUID and its heatmap region.
                var own = OwnGrid;
                if (own != null && Property != null)
                {
                    Property.Grids?.Remove(own);
                    HeatmapTiles.Relayout(Property, $"'{ExtensionId}' removed its own grid");
                }
                Tiles.Clear();
                GuidLeases.Release(own, $"grid of '{ExtensionId}'");
                if (own != null) { own.gameObject.SetActive(false); Object.Destroy(own.gameObject); }
                OwnGrid = null;
                Core.Log.Msg($"property extension: '{ExtensionId}' removed its own grid");
            }
            Grid tilesGrid = null;
            var removedTiles = new List<(int x, int y)>();
            for (int i = Tiles.Count - 1; i >= 0; i--)
            {
                var (grid, tile) = Tiles[i];
                if (grid != null && tile != null)
                {
                    tilesGrid = grid;
                    removedTiles.Add((tile.x, tile.y));
                    grid._coordinateToTile.Remove(new Coordinate(tile.x, tile.y));
                    grid.DeregisterTile(tile);
                    tile.gameObject.SetActive(false);
                    Object.Destroy(tile.gameObject);
                    grid.SetGridSize();
                }
                Tiles.RemoveAt(i);
            }
            if (Property != null && tilesGrid != null) HeatmapTiles.Set(Property, tilesGrid, removedTiles, false);
            if (Property != null && Property.propertyBoundsColliders != null)
            {
                var keep = Property.propertyBoundsColliders.Where(x => x != null && !Boxes.Any(b => b != null && b.Pointer == x.Pointer)).ToArray();
                Property.propertyBoundsColliders = new Il2CppReferenceArray<BoxCollider>(keep);
            }
            foreach (var box in Boxes)
                if (box != null) { box.gameObject.SetActive(false); Object.Destroy(box.gameObject); }
            Boxes.Clear();
        }

        /// <summary>Deregisters the lease's GUIDs when the scene is gone and its objects cannot be removed one by one.</summary>
        internal void ReleaseGuids()
        {
            foreach (var (surface, _) in Surfaces) GuidLeases.Release(surface, $"surface of '{ExtensionId}'");
            foreach (var dock in Docks) dock.ReleaseGuids();
            if ((object)OwnGrid != null) GuidLeases.Release(OwnGrid, $"grid of '{ExtensionId}'");
            foreach (var fixture in Fixtures) PropertyFixtures.Unregister(fixture);
            Fixtures.Clear();
        }
    }

    internal static class PropertyExtensions
    {
        private static void AddSurface(string expansion, string extension, Property prop, PropertySurfaceDefinition def, PropertyExtensionLease lease)
        {
            var guid = new Il2CppSystem.Guid(Places.StableGuid(expansion, "surface", extension + "/" + def.Id).ToString());
            GuidLeases.RequireFree(guid, $"surface '{def.Id}'");
            GameObject go;
            bool ownObject = string.IsNullOrEmpty(def.CopyId);
            if (ownObject)
            {
                go = new GameObject($"MapExpansions.{expansion}.{extension}.Surface.{def.Id}");
                go.SetActive(false);
                go.transform.SetParent(prop.transform, true);
                go.transform.SetPositionAndRotation(def.Bounds.center, Quaternion.identity);
                var scale = go.transform.parent.lossyScale;
                go.transform.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
                go.layer = LayerMask.NameToLayer("Default");
                go.AddComponent<BoxCollider>().size = def.Bounds.size;
            }
            else
            {
                var copy = MainCopies.All.LastOrDefault(x => x.Expansion.Id == expansion && x.Built.Def.Id == def.CopyId);
                var box = copy?.Built.Def.Boxes.FirstOrDefault(x => x.Id == def.BoxId);
                var target = box == null ? null : copy.Built.Local?.transform.Find(MainCopies.BoxName(box));
                if (target == null || target.GetComponent<Collider>() == null)
                    throw new InvalidOperationException($"surface '{def.Id}' has no copy block '{def.CopyId}/{def.BoxId}' collider");
                go = target.gameObject;
                if (go.GetComponent<Surface>() != null) throw new InvalidOperationException($"surface '{def.Id}' target already has a Surface");
                go.SetActive(false);
            }
            try
            {
                go.AddComponent(Il2CppType.Of<Surface>());
                var surface = go.GetComponent<Surface>();
                lease.Surfaces.Add((surface, ownObject));
                surface.BakedGUID = guid.ToString();
                surface._ParentProperty_k__BackingField = prop;
                surface.SurfaceType = def.SurfaceType == PropertySurfaceKind.Roof ? Surface.ESurfaceType.Roof : Surface.ESurfaceType.Wall;
                surface.ValidFaces = new Il2CppSystem.Collections.Generic.List<Surface.EFace>();
                foreach (var face in def.ValidFaces) surface.ValidFaces.Add((Surface.EFace)(int)face);
                // Surface registers itself only in Awake, and a joining client builds this before the game switches the
                // property root on. An own surface object wakes at the scene root and then goes back under the property.
                if (ownObject && !prop.gameObject.activeInHierarchy)
                {
                    go.transform.SetParent(null, true);
                    go.SetActive(true);
                    go.transform.SetParent(prop.transform, true);
                    Core.Log.Msg($"property surface: '{expansion}/{extension}/{def.Id}' woke at the scene root; property '{prop.PropertyCode}' is inactive now");
                }
                else go.SetActive(true);
                var registered = Il2Cpp.GUIDManager.GetObject<Surface>(guid);
                if (registered == null || registered.Pointer != surface.Pointer || surface.ParentProperty?.Pointer != prop.Pointer)
                    throw new InvalidOperationException($"surface '{def.Id}' registration readback failed");
                Core.Log.Msg($"property surface: '{expansion}/{extension}/{def.Id}' registered GUID {guid}");
            }
            catch
            {
                if (!ownObject) go.SetActive(true);
                else if (lease.Surfaces.All(x => x.surface == null || x.surface.gameObject.Pointer != go.Pointer)) Object.Destroy(go);
                throw;
            }
        }

        /// <summary>
        /// Builds the extension's own grid, still inactive: vanilla's Grid.Awake adds it to the property's grids,
        /// registers its GUID and reads its tiles, so it runs only once the tiles are registered (ActivateGrid).
        /// </summary>
        private static Grid CreateGrid(string expansion, PropertyExtensionDefinition def, Property prop, Grid baseGrid)
        {
            var guid = new Il2CppSystem.Guid(Places.StableGuid(expansion, "grid", def.Id).ToString());
            GuidLeases.RequireFree(guid, $"grid of '{def.Id}'");
            var go = new GameObject($"MapExpansions.{expansion}.{def.Id}.Grid");
            go.SetActive(false);
            go.transform.SetParent(baseGrid.transform.parent, false);
            go.transform.localScale = baseGrid.transform.localScale;
            go.transform.SetPositionAndRotation(def.GridOrigin.Value, baseGrid.transform.rotation);
            go.layer = baseGrid.gameObject.layer;
            var grid = go.AddComponent<Grid>();
            grid._guid = guid.ToString();
            grid._parentProperty = prop;
            return grid;
        }

        private static void ActivateGrid(string expansion, PropertyExtensionDefinition def, Property prop, Grid grid, PropertyExtensionLease lease)
        {
            int before = prop.Grids.Count;
            // The property can be inactive here: a joining client gets the host's world states before the game switches
            // the property root on, and vanilla culling switches the base grid's parent off while the player is far away.
            // Under an inactive parent Grid.Awake would not run. Neither Grid.Awake nor Tile.Awake looks up the
            // hierarchy (the parent property is set), so the grid wakes at the scene root and then goes back under the
            // base grid's parent, where it is switched on and off with the base grid.
            var home = grid.transform.parent;
            bool asleep = home != null && !home.gameObject.activeInHierarchy;
            grid.transform.SetParent(null, true);
            grid.gameObject.SetActive(true);
            if (!grid.gameObject.activeInHierarchy)
                throw new InvalidOperationException($"grid of '{def.Id}' did not wake at the scene root");
            grid.transform.SetParent(home, true);
            if (asleep)
                Core.Log.Msg($"property extension: grid of '{expansion}/{def.Id}' woke at the scene root; its parent '{home.name}' is inactive now and wakes with the base grid");
            var guid = new Il2CppSystem.Guid(grid._guid);
            var registered = Il2Cpp.GUIDManager.GetObject<Grid>(guid);
            if (registered == null || registered.Pointer != grid.Pointer)
                throw new InvalidOperationException($"grid of '{def.Id}' GUID registration readback failed");
            if (prop.Grids.Count != before + 1 || prop.Grids[prop.Grids.Count - 1]?.Pointer != grid.Pointer)
                throw new InvalidOperationException($"grid of '{def.Id}' is not in the grids of '{def.PropertyCode}'");
            foreach (var (_, tile) in lease.Tiles)
                if (grid.GetTile(new Coordinate(tile.x, tile.y))?.Pointer != tile.Pointer)
                    throw new InvalidOperationException($"tile {tile.x},{tile.y} of the grid of '{def.Id}' registration readback failed");
            Core.Log.Msg($"property extension: '{expansion}/{def.Id}' built grid {prop.Grids.Count - 1} of '{def.PropertyCode}' at {def.GridOrigin.Value} " +
                         $"with {lease.Tiles.Count} tiles, GUID {guid}");
        }

        internal static PropertyExtensionLease Apply(string expansion, PropertyExtensionDefinition def)
        {
            Property prop = null;
            foreach (var candidate in Property.Properties)
                if (candidate != null && candidate.PropertyCode == def.PropertyCode) { prop = candidate; break; }
            if (prop == null) throw new InvalidOperationException($"property '{def.PropertyCode}' is not in the loaded scene");
            Grid grid = null;
            Tile template = null;
            // A fixture's cells are tiles of the extension even when Tiles does not list them.
            var tiles = new List<GridCoordinate>(def.Tiles);
            var listed = new HashSet<(int, int)>(tiles.Select(c => (c.X, c.Y)));
            foreach (var fixture in def.Fixtures)
                foreach (var c in fixture.Tiles)
                    if (listed.Add((c.X, c.Y))) tiles.Add(c);
            Grid baseGrid = null;
            if (tiles.Count > 0 || def.GridOrigin.HasValue)
            {
                if (prop.Grids == null || def.GridIndex < 0 || def.GridIndex >= prop.Grids.Count)
                    throw new InvalidOperationException($"property '{def.PropertyCode}' has no grid {def.GridIndex}");
                baseGrid = grid = prop.Grids[def.GridIndex];
                if (grid?.Tiles != null) foreach (var t in grid.Tiles) if (t != null) { template = t; break; }
                if (template == null || grid._coordinateToTile == null)
                    throw new InvalidOperationException($"property '{def.PropertyCode}' grid {def.GridIndex} has no initialized tile template");
            }
            var lease = new PropertyExtensionLease
            {
                Property = prop,
                ExtensionId = def.Id,
                Signature = Lifecycle.WorldStateSignatures.Of(def),
                CarrierCopies = Lifecycle.WorldStateSignatures.Carriers(def),
            };
            int reusedTiles = 0;
            try
            {
                if (def.GridOrigin.HasValue) grid = lease.OwnGrid = CreateGrid(expansion, def, prop, baseGrid);
                bool own = (object)lease.OwnGrid != null;
                foreach (var coord in tiles)
                {
                    var key = new Coordinate(coord.X, coord.Y);
                    // The grid or another extension already has this tile, and it stays when this extension goes.
                    if (grid.GetTile(key) != null) { reusedTiles++; continue; }
                    var go = Object.Instantiate(template.gameObject, grid.transform);
                    go.name = $"MapExpansions.{expansion}.{def.Id}.Tile.{coord.X}.{coord.Y}";
                    go.transform.localPosition = new Vector3(coord.X * Grid.TileSize, 0f, coord.Y * Grid.TileSize);
                    var tile = go.GetComponent<Tile>();
                    if (tile == null) { Object.Destroy(go); throw new InvalidOperationException("cloned tile has no Tile component"); }
                    lease.Tiles.Add((grid, tile));
                    tile.InitializePropertyTile(coord.X, coord.Y, template.AvailableOffset, grid);
                    tile.BuildableOccupants.Clear();
                    tile.OccupantTiles.Clear();
                    grid.RegisterTile(tile);
                    // An own grid is still inactive; its Awake fills the lookup from the registered pairs.
                    if (!own) grid._coordinateToTile[key] = tile;
                    go.SetActive(true);
                    if (!own && grid.GetTile(key)?.Pointer != tile.Pointer)
                        throw new InvalidOperationException($"tile {coord.X},{coord.Y} registration readback failed");
                }
                if (own) ActivateGrid(expansion, def, prop, grid, lease);
                grid?.SetGridSize();
                if (grid != null) HeatmapTiles.Set(prop, grid, lease.Tiles.Select(t => (t.tile.x, t.tile.y)).ToList(), true);
                foreach (var bounds in def.Bounds)
                {
                    var go = new GameObject($"MapExpansions.{expansion}.{def.Id}.Bounds");
                    go.transform.SetParent(prop.BoundingBox.transform, true);
                    go.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
                    go.transform.localScale = new Vector3(1f / go.transform.parent.lossyScale.x, 1f / go.transform.parent.lossyScale.y, 1f / go.transform.parent.lossyScale.z);
                    go.layer = LayerMask.NameToLayer("Invisible");
                    var box = go.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = bounds.size;
                    lease.Boxes.Add(box);
                }
                foreach (var fixture in def.Fixtures) PropertyFixtures.Register(expansion, def, fixture, prop, grid, lease.Fixtures);
                foreach (var surface in def.Surfaces) AddSurface(expansion, def.Id, prop, surface, lease);
                foreach (var dock in def.Docks) lease.Docks.Add(PropertyDocks.Apply(expansion, def.Id, prop, dock));
                var boxes = prop.propertyBoundsColliders?.Where(x => x != null).ToList() ?? new List<BoxCollider>();
                boxes.AddRange(lease.Boxes);
                prop.propertyBoundsColliders = new Il2CppReferenceArray<BoxCollider>(boxes.ToArray());
                Physics.SyncTransforms();
                foreach (var bounds in def.Bounds)
                    if (!prop.DoBoundsContainPoint(bounds.center)) throw new InvalidOperationException("property bounds readback failed");
                Core.Log.Msg($"property extension: '{expansion}/{def.Id}' on '{def.PropertyCode}': {lease.Tiles.Count} added tiles, {reusedTiles} existing tiles reused, {lease.Boxes.Count} bounds, {lease.Docks.Count} docks, grid GUID {grid?.GUID}");
                return lease;
            }
            catch (Exception original)
            {
                try { lease.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("property extension and cleanup failed", original, cleanup); }
                throw;
            }
        }
    }
}
