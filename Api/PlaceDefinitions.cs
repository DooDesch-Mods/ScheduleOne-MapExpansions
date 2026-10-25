using UnityEngine;

namespace MapExpansions
{
    /// <summary>A point with a facing, in expansion-local coordinates.</summary>
    public readonly struct LocalPoint
    {
        public readonly Vector3 Position;

        /// <summary>Facing in local degrees around the vertical axis; 0 faces local +z.</summary>
        public readonly float Yaw;

        public LocalPoint(Vector3 position, float yaw = 0f)
        {
            Position = position;
            Yaw = yaw;
        }

        public LocalPoint(float x, float y, float z, float yaw = 0f) : this(new Vector3(x, y, z), yaw) { }
    }

    /// <summary>
    /// A building NPCs walk into and out of, as in the main world: they reach a door, disappear inside and come out
    /// at a door again. The building is an anchor only; walls and doors are whatever the expansion's content shows
    /// there. Schedules find it by <see cref="Name"/>.
    /// </summary>
    public sealed class BuildingDefinition
    {
        /// <summary>Stable id inside the expansion; the building's saved identity is made from it.</summary>
        public string Id { get; set; }

        /// <summary>The name the game and other mods look the building up by. Unique across the whole world.</summary>
        public string Name { get; set; }

        /// <summary>Where NPCs stand to go in, outside the door and on walkable ground, facing the door.</summary>
        public List<LocalPoint> Doors { get; } = new();

        public BuildingDefinition() { }

        public BuildingDefinition(string id, string name, params LocalPoint[] doors)
        {
            Id = id;
            Name = name;
            Doors.AddRange(doors);
        }
    }

    /// <summary>A parking lot NPC cars drive to and park in.</summary>
    public sealed class ParkingLotDefinition
    {
        public string Id { get; set; }

        /// <summary>The object name schedules find the lot by. Unique across the whole world.</summary>
        public string Name { get; set; }

        /// <summary>Where a car drives to before it parks: on the road, at most about 6 m from a road of the vehicle graph.</summary>
        public LocalPoint Entry { get; set; }

        /// <summary>One point per parking space: the car's centre and the direction its front points.</summary>
        public List<LocalPoint> Spots { get; } = new();
    }

    /// <summary>
    /// A property the player can buy, as a copy of a main-world property: its building, grids, loading docks, employee
    /// spots and sale sign come along and stand at <see cref="Root"/>. The copy is a networked object the host spawns,
    /// so ownership, hiring and building work in co-op as on the original. A business such as the laundromat is copied
    /// the same way and launders money like the original.
    /// </summary>
    public sealed class PropertyDefinition
    {
        /// <summary>Stable id inside the expansion; the GUIDs of the copy's grids and docks are made from it.</summary>
        public string Id { get; set; }

        /// <summary>
        /// The game's property code: lowercase, unique across the world. Saves store the property under it, and the
        /// realtor's dialogue choice uses it as its label.
        /// </summary>
        public string Code { get; set; }

        public string Name { get; set; }

        /// <summary>The purchase price; null keeps the template's.</summary>
        public float? Price { get; set; }

        /// <summary>How many employees work there; null keeps the template's.</summary>
        public int? EmployeeCapacity { get; set; }

        /// <summary>Code of the main-world property or business that is copied, for example "bungalow", "barn" or "laundromat".</summary>
        public string Template { get; set; } = "bungalow";

        /// <summary>Where the template's root stands, in expansion-local coordinates.</summary>
        public LocalPoint Root { get; set; }

        /// <summary>
        /// The copy is the mirror image of the template, its local x axis reversed, as if placed with scale x -1. Grids,
        /// parking spots and spawn, access and idle points keep a proper orientation, so building, deliveries and
        /// employees work as on the original.
        /// </summary>
        public bool Mirrored { get; set; }

        /// <summary>Ray the realtor offers the property among his others when this is set.</summary>
        public bool SoldByRealtor { get; set; } = true;

        /// <summary>The realtor's choice text; empty means "&lt;Name&gt; (&lt;PRICE&gt;)". The price token is replaced by the game.</summary>
        public string RealtorText { get; set; }
    }

    /// <summary>Where an expansion's police station releases arrested players and parks the cars taken from them.</summary>
    public sealed class PoliceStationDefinition
    {
        /// <summary>Where a released player stands, on walkable ground, facing away from the station.</summary>
        public LocalPoint ReleasePoint { get; set; }

        /// <summary>Parking spaces for taken cars: the car's centre and the direction its front points. At least one.</summary>
        public List<LocalPoint> VehicleSpots { get; } = new();
    }

    /// <summary>A place where a customer waits for a deal.</summary>
    public sealed class DealLocationDefinition
    {
        public string Id { get; set; }

        /// <summary>Shown to the player in the deal, as in "behind the pawn shop".</summary>
        public string Name { get; set; }

        public string Description { get; set; } = string.Empty;

        /// <summary>Where the customer stands, on walkable ground.</summary>
        public LocalPoint StandPoint { get; set; }

        /// <summary>The region whose customers use this place, by <see cref="RegionDefinition.Name"/>. Empty means the expansion's first region.</summary>
        public string Region { get; set; }
    }
}
