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
        /// <summary>
        /// Main-world objects whose tiles stay closed for building while the object stands. Tiles of a fixture that
        /// <see cref="Tiles"/> lacks are built along with them, so a fixture needs no second list. A repeated extension
        /// must repeat its fixtures too, because they are part of what makes it identical.
        /// </summary>
        public List<PropertyFixtureDefinition> Fixtures { get; } = new();
    }

    /// <summary>
    /// A main-world object that occupies grid tiles, such as a sink standing in a room. The tiles take part in the grid like
    /// any other, but nothing can be placed on them while the object stands there. Removing, disabling, hiding or moving
    /// the object frees them without a call; <see cref="MapExpansionsApi.SetFixtureReleased"/> covers the other cases.
    /// An object that is missing when the extension applies is not an error: its tiles are free.
    /// </summary>
    public sealed class PropertyFixtureDefinition
    {
        /// <summary>Unique inside the extension.</summary>
        public string Id { get; set; }

        /// <summary>Path of the object in the main world, as in <see cref="ObjectMoveDefinition.Path"/>. It is resolved after the state's moves.</summary>
        public string Path { get; set; }

        /// <summary>The cells under the object, on the extension's grid.</summary>
        public List<GridCoordinate> Tiles { get; } = new();
    }
}
