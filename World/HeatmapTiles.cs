using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Heatmap;
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

        private static int RegionOf(Property property, Grid grid)
        {
            for (int i = 0; i < property.Grids.Count; i++)
                if (property.Grids[i]?.Pointer == grid.Pointer) return i;
            return -1;
        }
    }
}
