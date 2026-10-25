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
