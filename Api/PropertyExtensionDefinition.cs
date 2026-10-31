using UnityEngine;

namespace MapExpansions
{
    /// <summary>A coordinate on an existing property's grid. Its original GUID and origin stay unchanged.</summary>
    public readonly struct GridCoordinate
    {
        public readonly int X;
        public readonly int Y;
        public GridCoordinate(int x, int y) { X = x; Y = y; }
    }

    /// <summary>Adds build tiles, surfaces, bounds and loading docks to an existing property, without copying it.</summary>
    public sealed class PropertyExtensionDefinition
    {
        public string Id { get; set; }
        public string PropertyCode { get; set; }
        public int GridIndex { get; set; }
        public List<GridCoordinate> Tiles { get; } = new();
        public List<PropertySurfaceDefinition> Surfaces { get; } = new();
        /// <summary>Permanent additional delivery slots. Keep completed dock effects active when later upgrades begin.</summary>
        public List<PropertyDockDefinition> Docks { get; } = new();
        /// <summary>Additional world-space boxes. Existing property bounds are retained.</summary>
        public List<Bounds> Bounds { get; } = new();
    }
}
