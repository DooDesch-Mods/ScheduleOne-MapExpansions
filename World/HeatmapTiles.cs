using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Heatmap;
using Il2CppScheduleOne.Temperature;
using Il2CppScheduleOne.Tiles;
using UnityEngine;
using Object = UnityEngine.Object;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using Property = Il2CppScheduleOne.Property.Property;

namespace MapExpansions.World
{
    /// <summary>
    /// The heatmap manager reads which tiles a grid has once, when it starts, and colours only those. Tiles a property
    /// extension adds or removes later have to reach its mask here, or the floor view keeps the old outline.
    /// </summary>
    internal static class HeatmapTiles
    {
        // The sizes HeatmapManager lays its mask out with.
        private const int TextureSize = 128;
        private const int MaxRegions = 16;

        internal static void Set(Property property, Grid grid, IReadOnlyCollection<(int x, int y)> tiles, bool present)
        {
            if (tiles.Count == 0) return;
            try
            {
                var manager = Singleton<HeatmapManager>.Instance;
                if (manager == null || manager._propertyGridMasks == null || !manager._propertyGridMasks.ContainsKey(property.PropertyCode))
                {
                    Core.Log.Msg($"property extension: heatmap of '{property.PropertyCode}' not set up yet; it reads the tiles when it starts");
                    return;
                }
                int region = RegionOf(property, grid);
                if (region < 0 || region >= MaxRegions)
                {
                    Core.Log.Warning($"property extension: grid '{grid.name}' has no heatmap region on '{property.PropertyCode}' (index {region})");
                    return;
                }
                var data = manager._propertyGridMasks[property.PropertyCode];
                // The manager lays out its mask, regions and matrices once for the grids the property has when it
                // starts. A grid an extension builds later gets its slot here; the new layout reads its tiles.
                if (region >= (data.Regions?.Count ?? 0) || data.MaskData == null || data.MaskData.Length < TextureSize * TextureSize * (region + 1))
                {
                    Relayout(property, $"grid '{grid.name}' came after the heatmap was laid out");
                    return;
                }
                int written = 0;
                foreach (var (x, y) in tiles)
                {
                    if (x < 0 || y < 0 || x >= TextureSize || y >= TextureSize)
                    {
                        Core.Log.Warning($"property extension: tile {x},{y} lies outside the heatmap of '{property.PropertyCode}' and stays uncoloured");
                        continue;
                    }
                    data.MaskData[x + TextureSize * (y + TextureSize * region)] = present ? 1 : 0;
                    written++;
                }
                // The first time the view opens it draws from the mask anyway.
                if (data.InitialDispatched) manager.DispatchHeatmap(property.PropertyCode, grid.TemperatureEmitterInfos);
                Core.Log.Msg($"property extension: heatmap of '{property.PropertyCode}' {(present ? "gained" : "lost")} {written} tiles, redrawn {data.InitialDispatched}");
            }
            catch (Exception e)
            {
                Core.Log.Warning($"property extension: heatmap of '{property.PropertyCode}' keeps its old outline: {e}");
            }
        }

        /// <summary>
        /// Lays the property's heatmap out again for the grids it has now, the way the manager does at start: one
        /// mask slice, one matrix and one region quad per grid. Quads whose grid kept its index stay; the rest are
        /// rebuilt, so a grid that left takes its quad along and later grids keep matching texture slices.
        /// </summary>
        internal static void Relayout(Property property, string why)
        {
            try
            {
                var manager = Singleton<HeatmapManager>.Instance;
                if (manager == null || manager._propertyGridMasks == null || !manager._propertyGridMasks.ContainsKey(property.PropertyCode))
                {
                    Core.Log.Msg($"property extension: heatmap of '{property.PropertyCode}' not set up yet; it lays out the grids when it starts");
                    return;
                }
                var refs = manager._propertyRegionReferences;
                int refIndex = -1;
                for (int i = 0; i < refs.Count; i++) if (refs[i].PropertyCode == property.PropertyCode) { refIndex = i; break; }
                if (refIndex < 0)
                {
                    Core.Log.Warning($"property extension: heatmap of '{property.PropertyCode}' has no region reference; it keeps its old layout");
                    return;
                }
                var data = manager._propertyGridMasks[property.PropertyCode];
                var grids = property.Grids;
                int count = Math.Min(grids.Count, MaxRegions);
                var old = data.Regions;
                bool active = old != null && old.Count > 0 && old[0] != null && old[0].gameObject.activeSelf;

                var regions = new Il2CppSystem.Collections.Generic.List<HeatmapRegion>();
                var mask = new Il2CppStructArray<int>(TextureSize * TextureSize * grids.Count);
                var matrices = new Il2CppStructArray<Matrix4x4>(grids.Count);
                int kept = 0, built = 0, dropped = 0;
                for (int i = 0; i < count; i++)
                {
                    var grid = grids[i];
                    var region = old != null && i < old.Count ? old[i] : null;
                    if (region != null && region.transform.parent != null && region.transform.parent.Pointer == grid.transform.Pointer)
                        kept++;
                    else
                    {
                        if (region != null) { Object.Destroy(region.gameObject); dropped++; }
                        region = Object.Instantiate(manager._heatmapRegionPrefab, grid.transform);
                        region.Create(grid, refIndex * MaxRegions + i, manager._heatmapMat);
                        region.gameObject.SetActive(active);
                        Subscribe(manager, grid);
                        built++;
                    }
                    regions.Add(region);
                    matrices[i] = grid.transform.localToWorldMatrix;
                    for (int x = 0; x < grid.Width && x < TextureSize; x++)
                        for (int y = 0; y < grid.Height && y < TextureSize; y++)
                            mask[x + TextureSize * (y + TextureSize * i)] = grid.GetTile(new Coordinate(x, y)) != null ? 1 : 0;
                }
                for (int i = count; old != null && i < old.Count; i++)
                    if (old[i] != null) { Object.Destroy(old[i].gameObject); dropped++; }

                data.Regions = regions;
                data.MaskData = mask;
                data.Matrices = matrices;
                // The reference is a value type; the list hands out a copy, so the changed copy goes back in.
                var reference = refs[refIndex];
                reference.RegionAmount = grids.Count;
                refs[refIndex] = reference;
                if (data.InitialDispatched) manager.DispatchHeatmap(property.PropertyCode, null);
                Core.Log.Msg($"property extension: heatmap of '{property.PropertyCode}' laid out again for {grids.Count} grids ({why}): {kept} regions kept, {built} built, {dropped} dropped, redrawn {data.InitialDispatched}");
            }
            catch (Exception e)
            {
                Core.Log.Warning($"property extension: heatmap of '{property.PropertyCode}' keeps its old layout ({why}): {e}");
            }
        }

        // The manager redraws a property when one of its grids reports changed heat sources; a grid built later
        // has to report the same way.
        private static void Subscribe(HeatmapManager manager, Grid grid)
        {
            Il2CppSystem.Action<string, Il2CppStructArray<TemperatureEmitterInfo>> handler =
                (Action<string, Il2CppStructArray<TemperatureEmitterInfo>>)((code, infos) => manager.DispatchHeatmap(code, infos));
            var current = grid.OnCosmeticTemperatureEmittersChanged;
            grid.OnCosmeticTemperatureEmittersChanged = current == null
                ? handler
                : Il2CppSystem.Delegate.Combine(current, handler).Cast<Il2CppSystem.Action<string, Il2CppStructArray<TemperatureEmitterInfo>>>();
        }

        private static int RegionOf(Property property, Grid grid)
        {
            for (int i = 0; i < property.Grids.Count; i++)
                if (property.Grids[i]?.Pointer == grid.Pointer) return i;
            return -1;
        }
    }
}
