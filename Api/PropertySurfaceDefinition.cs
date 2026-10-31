using UnityEngine;

namespace MapExpansions
{
    public enum PropertySurfaceKind { Wall, Roof }
    public enum PropertySurfaceFace { Front, Back, Top, Bottom, Left, Right }

    /// <summary>A vanilla wall or roof placement surface with a stable saved identity.</summary>
    public sealed class PropertySurfaceDefinition
    {
        public string Id { get; set; }
        /// <summary>Copy and block ids whose collider carries the surface. Empty creates a standalone world-space box.</summary>
        public string CopyId { get; set; }
        public string BoxId { get; set; }
        public Bounds Bounds { get; set; }
        public PropertySurfaceKind SurfaceType { get; set; } = PropertySurfaceKind.Wall;
        public List<PropertySurfaceFace> ValidFaces { get; } = new() { PropertySurfaceFace.Front };
    }
}
