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

    /// <summary>
    /// Adds build tiles, surfaces, bounds and loading docks to an existing property, without copying it. A later state
    /// that has an extension with the same id and an identical definition keeps it standing, together with the copies
    /// its surfaces are bound to when those are identical too, so its used tiles, surfaces and docks need not be free.
    /// Any difference rebuilds the extension, which fails while it is in use.
    /// </summary>
    public sealed class PropertyExtensionDefinition
    {
        public string Id { get; set; }
        public string PropertyCode { get; set; }
        public int GridIndex { get; set; }
        public List<GridCoordinate> Tiles { get; } = new();
        public List<PropertySurfaceDefinition> Surfaces { get; } = new();
        /// <summary>Permanent additional delivery slots. A later state keeps them by repeating this extension unchanged.</summary>
        public List<PropertyDockDefinition> Docks { get; } = new();
        /// <summary>Additional world-space boxes. Existing property bounds are retained.</summary>
        public List<Bounds> Bounds { get; } = new();
    }
}
