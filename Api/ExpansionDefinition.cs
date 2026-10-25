using UnityEngine;

namespace MapExpansions
{
    /// <summary>
    /// Everything the framework needs to build one expansion: where its content comes from, where it goes, which
    /// regions and named places it brings, and how it joins the main world. A consumer mod fills one and passes it to
    /// <see cref="MapExpansionsApi.Register"/> before the main menu opens.
    /// </summary>
    /// <remarks>
    /// Expansion-local coordinates are the coordinates of the source scene. The framework turns them into world
    /// coordinates with <see cref="Offset"/> and <see cref="QuarterTurns"/>; main-world coordinates stay as they are.
    /// </remarks>
    public sealed class ExpansionDefinition
    {
        /// <summary>Stable id; part of every saved identity the expansion produces. Lowercase, no spaces.</summary>
        public string Id { get; set; }

        public string DisplayName { get; set; }

        /// <summary>Content version of the consumer, compared between host and clients.</summary>
        public string Version { get; set; } = "1.0.0";

        public WorldSource Source { get; set; }

        /// <summary>World position of the source scene's origin.</summary>
        public Vector3 Offset { get; set; }

        /// <summary>Clockwise quarter turns seen from above. Terrains turn only in whole quarter turns.</summary>
        public int QuarterTurns { get; set; }

        public List<RegionDefinition> Regions { get; } = new();

        public List<ConnectionDefinition> Connections { get; } = new();

        /// <summary>Objects switched off when the expansion is built, in the expansion or in the main world.</summary>
        public List<ObjectRule> Hide { get; } = new();

        /// <summary>Large flat plates of the source that are replaced by a plate over a smaller local area.</summary>
        public List<GroundPlate> GroundPlates { get; } = new();

        /// <summary>Named places inside the expansion, resolved through <see cref="LocationRef"/>.</summary>
        public List<LocationDefinition> Locations { get; } = new();

        /// <summary>
        /// Local rectangle the vehicle graph covers, in addition to the connections. Empty means the bounds of the
        /// source terrains.
        /// </summary>
        public Rect VehicleArea { get; set; }

        /// <summary>The lowest height the expansion may reach in the world; lower terrain is reported on build.</summary>
        public float MinimumWorldHeight { get; set; } = -5f;

        /// <summary>
        /// Each world is drawn only while the camera is on its side, as if the other were not there: in the expansion the
        /// main world's map is not drawn, in the main world the expansion is not drawn. The side changes in the middle of
        /// each connection's built stretch, where neither end can be seen; the connections are always drawn. For an
        /// expansion whose source had its own backdrop, such as a ring of distant mountains, that would stand in the
        /// other world. Colliders, characters and vehicles stay as they are.
        /// </summary>
        public bool SeparateViews { get; set; }

        /// <summary>
        /// Root objects of the main scene that are not drawn in the expansion with <see cref="SeparateViews"/>. Nothing
        /// that can move between the worlds may lie under them; objects under a rigidbody are always drawn.
        /// </summary>
        public List<string> MainViewRoots { get; } = new() { "Map", "@Properties" };

        /// <summary>Main-world objects drawn on both sides with everything under them, by name prefix; the sea lies under both worlds.</summary>
        public List<string> SharedViewObjects { get; } = new() { "StylizedWater2_Ocean" };
    }

    /// <summary>Where the expansion's original content comes from.</summary>
    public sealed class WorldSource
    {
        /// <summary>Scene in the game build, loaded additively in the main menu.</summary>
        public string SceneName { get; set; }

        /// <summary>Root object of that scene whose hierarchy is taken over.</summary>
        public string RootName { get; set; } = "Map";

        /// <summary>
        /// Game versions the content was checked against. A different running version is reported as a warning;
        /// an empty list skips the check.
        /// </summary>
        public List<string> TestedGameVersions { get; } = new();

        /// <summary>
        /// Object of the source scene whose children are the road nodes of its vehicle road graph. They join the
        /// main world's road node graph, which vehicles use to find the nearest road. Empty skips them.
        /// </summary>
        public string RoadNodesName { get; set; } = "RoadNodes";

        internal string Key => SceneName + "/" + RootName;
    }

    /// <summary>A map region of its own, appended after the vanilla regions.</summary>
    public sealed class RegionDefinition
    {
        public string Name { get; set; }

        /// <summary>Outline in expansion-local x/z, in order around the area.</summary>
        public List<Vector2> Polygon { get; } = new();

        /// <summary>Unlocked in every save without a quest or rank.</summary>
        public bool UnlockedByDefault { get; set; } = true;

        /// <summary>
        /// The vanilla region that answers for this one wherever the game needs a vanilla region (cartel activities,
        /// starting influence). Vanilla regions are numbered 0 Northtown to 5 Uptown.
        /// </summary>
        public int StandInRegion { get; set; }
    }

    /// <summary>Which world an object, a point or a material belongs to.</summary>
    public enum WorldSide
    {
        Main,
        Expansion,
    }

    /// <summary>A point in the main world or in expansion-local coordinates.</summary>
    public readonly struct PathPoint
    {
        public readonly WorldSide Side;
        public readonly Vector3 Position;

        public PathPoint(WorldSide side, Vector3 position)
        {
            Side = side;
            Position = position;
        }

        public static PathPoint Main(float x, float y, float z) => new(WorldSide.Main, new Vector3(x, y, z));

        public static PathPoint Expansion(float x, float y, float z) => new(WorldSide.Expansion, new Vector3(x, y, z));
    }

    public enum ObjectEffect
    {
        /// <summary>Renderers off, colliders stay.</summary>
        Hide,

        /// <summary>Colliders off, renderers stay.</summary>
        NoCollision,

        /// <summary>Renderers and colliders off.</summary>
        Remove,

        /// <summary>
        /// Renderers draw both faces. For one-sided outer walls whose back faces the main world and would otherwise
        /// show as gaps in the sky.
        /// </summary>
        DoubleSided,

        /// <summary>
        /// Hangs a wall of the same material from the open lower edges of each selected mesh down to
        /// <see cref="ObjectRule.Bottom"/>. Only edges inside the rule's area count. For a cliff whose lower edge
        /// floats over the ground, so the view passes under it. The wall has no collider.
        /// </summary>
        Skirt,

        /// <summary>
        /// Convex mesh colliders collide with the mesh's own triangles instead of their hull. For a ramp whose hull
        /// stands as a step over the road at its foot. Colliders on a moving body stay convex.
        /// </summary>
        ExactCollision,

        /// <summary>
        /// The top face of box colliders moves by <see cref="ObjectRule.TopShift"/>, the bottom face stays. For a deck
        /// whose boxes reach up to its kerbs, above the road drawn on it.
        /// </summary>
        ColliderTop,

        /// <summary>
        /// Cuts the triangles of each selected mesh, renderers and mesh colliders, out of the rule's area; the rest of
        /// the mesh stays. For a backdrop that runs through the other world, such as a ring of distant mountains.
        /// </summary>
        Cut,
    }

    /// <summary>Selects objects by name in one world, optionally limited to an area.</summary>
    public sealed class ObjectRule
    {
        public WorldSide Side { get; set; }

        /// <summary>The object's name starts with this (case-insensitive).</summary>
        public string Name { get; set; }

        /// <summary>When set, some ancestor's name starts with this.</summary>
        public string Parent { get; set; }

        /// <summary>
        /// When its size is not zero, the object's bounds must intersect this box. Main-world coordinates for
        /// <see cref="WorldSide.Main"/>, expansion-local ones for <see cref="WorldSide.Expansion"/>.
        /// </summary>
        public Bounds Area { get; set; }

        public ObjectEffect Effect { get; set; } = ObjectEffect.Hide;

        /// <summary>For <see cref="ObjectEffect.Skirt"/>: the height the wall reaches down to, in the same coordinates as <see cref="Area"/>.</summary>
        public float Bottom { get; set; }

        /// <summary>For <see cref="ObjectEffect.ColliderTop"/>: metres the top face moves up, negative for down.</summary>
        public float TopShift { get; set; }

        public static ObjectRule In(WorldSide side, string name, ObjectEffect effect, string parent = null) =>
            new() { Side = side, Name = name, Parent = parent, Effect = effect };

        public ObjectRule Within(Vector3 min, Vector3 max)
        {
            var b = new Bounds();
            b.SetMinMax(Vector3.Min(min, max), Vector3.Max(min, max));
            Area = b;
            return this;
        }

        /// <summary>Sets <see cref="Bottom"/>.</summary>
        public ObjectRule DownTo(float height)
        {
            Bottom = height;
            return this;
        }

        /// <summary>Sets <see cref="TopShift"/>.</summary>
        public ObjectRule ShiftTop(float metres)
        {
            TopShift = metres;
            return this;
        }

        public override string ToString() =>
            $"{Side} '{Name}'{(Parent != null ? $" under '{Parent}'" : "")}{(Area.size != Vector3.zero ? $" in {Area.min}..{Area.max}" : "")} -> {Effect}";
    }

    /// <summary>
    /// Replaces a large flat plate of the source (a ground plane that would reach into the main world) with one
    /// over a local rectangle, using the same material and height.
    /// </summary>
    public sealed class GroundPlate
    {
        public string Name { get; set; }

        public Rect LocalArea { get; set; }
    }

    /// <summary>A named place in an expansion.</summary>
    public sealed class LocationDefinition
    {
        public string Name { get; set; }

        public Vector3 LocalPosition { get; set; }

        /// <summary>Facing in local degrees around the vertical axis.</summary>
        public float LocalYaw { get; set; }

        public LocationDefinition() { }

        public LocationDefinition(string name, Vector3 localPosition, float localYaw = 0f)
        {
            Name = name;
            LocalPosition = localPosition;
            LocalYaw = localYaw;
        }
    }

    /// <summary>A stable reference to a named place: expansion id plus location name.</summary>
    public readonly struct LocationRef : IEquatable<LocationRef>
    {
        public readonly string ExpansionId;
        public readonly string Name;

        public LocationRef(string expansionId, string name)
        {
            ExpansionId = expansionId;
            Name = name;
        }

        /// <summary>Parses "expansion:name".</summary>
        public static bool TryParse(string text, out LocationRef value)
        {
            value = default;
            if (string.IsNullOrEmpty(text)) return false;
            int i = text.IndexOf(':');
            if (i <= 0 || i == text.Length - 1) return false;
            value = new LocationRef(text.Substring(0, i), text.Substring(i + 1));
            return true;
        }

        public bool Equals(LocationRef other) =>
            string.Equals(ExpansionId, other.ExpansionId, StringComparison.Ordinal) && string.Equals(Name, other.Name, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is LocationRef other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(ExpansionId, Name);

        public override string ToString() => ExpansionId + ":" + Name;
    }
}
