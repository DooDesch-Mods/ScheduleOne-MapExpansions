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

        /// <summary>
        /// The scene the expansion's own world comes from. Null makes the expansion an edit layer over the main world:
        /// nothing is captured, it has no terrain, connections or separate views, and its offset is zero.
        /// </summary>
        public WorldSource Source { get; set; }

        internal bool HasSource => Source != null && !string.IsNullOrEmpty(Source.SceneName);

        /// <summary>World position of the source scene's origin.</summary>
        public Vector3 Offset { get; set; }

        /// <summary>Clockwise quarter turns seen from above. Terrains turn only in whole quarter turns.</summary>
        public int QuarterTurns { get; set; }

        public List<RegionDefinition> Regions { get; } = new();

        /// <summary>The expansion's part of the phone map; without it the expansion is not on the map.</summary>
        public MapSheetDefinition Map { get; set; }

        public List<ConnectionDefinition> Connections { get; } = new();

        /// <summary>Objects switched off when the expansion is built, in the expansion or in the main world.</summary>
        public List<ObjectRule> Hide { get; } = new();

        /// <summary>Large flat plates of the source that are replaced by a plate over a smaller local area.</summary>
        public List<GroundPlate> GroundPlates { get; } = new();

        /// <summary>Named places inside the expansion, resolved through <see cref="LocationRef"/>.</summary>
        public List<LocationDefinition> Locations { get; } = new();

        /// <summary>Buildings NPCs can enter, created on every peer when the expansion is built.</summary>
        public List<BuildingDefinition> Buildings { get; } = new();

        /// <summary>Parking lots for NPC cars, created on every peer when the expansion is built.</summary>
        public List<ParkingLotDefinition> ParkingLots { get; } = new();

        /// <summary>Places where customers of the expansion's regions wait for deals.</summary>
        public List<DealLocationDefinition> DealLocations { get; } = new();

        /// <summary>Properties the player can buy, copied from main-world properties.</summary>
        public List<PropertyDefinition> Properties { get; } = new();

        /// <summary>Named local states, initially dormant. One expansion per independent upgrade retains completed effects.</summary>
        public List<WorldStateDefinition> WorldStates { get; } = new();

        /// <summary>
        /// The expansion's own police station. A player arrested in the expansion is set free there instead of at the
        /// main world's station, and the car taken from them is parked there. Null leaves arrests to the main world.
        /// </summary>
        public PoliceStationDefinition PoliceStation { get; set; }

        /// <summary>Shops with a seller at the counter, each with the main-world interior and doors it copies.</summary>
        public List<ShopDefinition> Shops { get; } = new();

        /// <summary>Main-world objects copied into the expansion outside any shop, such as furniture.</summary>
        public List<CopyDefinition> Copies { get; } = new();

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

        /// <summary>
        /// The JSON layer a map editor wrote for this expansion, laid over this definition each time the world is built.
        /// A file <c>Mods/&lt;Id&gt;/map.json</c> wins over it. Null means no layer.
        /// </summary>
        public LayerSource Layer { get; set; }

        /// <summary>Sets <see cref="Layer"/> to an embedded resource of the given assembly.</summary>
        public ExpansionDefinition UseLayer(System.Reflection.Assembly assembly, string resourceName)
        {
            Layer = new LayerSource { Assembly = assembly, ResourceName = resourceName };
            return this;
        }
    }

    /// <summary>Where the JSON layer of an expansion is embedded.</summary>
    public sealed class LayerSource
    {
        public System.Reflection.Assembly Assembly { get; set; }

        /// <summary>The manifest resource name, such as "SerenaFlats.map.json".</summary>
        public string ResourceName { get; set; }
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

        /// <summary>
        /// Solid box colliders become shells: thin boxes on the outside of their six faces, open wherever one of
        /// <see cref="ObjectRule.Openings"/> crosses a face. For a building that was a closed block and gets an interior.
        /// With <see cref="ObjectRule.Bands"/>, <see cref="ObjectRule.CeilingMaterial"/> or <see cref="ObjectRule.FloorMaterial"/>
        /// the inside also gets walls, a ceiling and a floor of main-world materials, open at the openings and at <see cref="ObjectRule.Windows"/>;
        /// without them, pair it with <see cref="DoubleSided"/> so the outer walls show from inside.
        /// </summary>
        Hollow,

        /// <summary>
        /// Closes the hole cells of each selected terrain inside the rule's area. For a cellar entrance cut into the
        /// ground under a building the rules removed.
        /// </summary>
        CloseHoles,

        /// <summary>
        /// Like <see cref="Cut"/>, but only for mesh colliders; renderers stay whole. For a doorway whose drawn lintel
        /// stays, while its collider stops the player, who tests every step a little above their head height.
        /// </summary>
        CutCollision,
    }

    /// <summary>Selects objects by name in one world, optionally limited to an area.</summary>
    public sealed class ObjectRule
    {
        /// <summary>
        /// Stable id inside the expansion, which a map layer uses to replace or remove the rule. Empty takes one made of
        /// side, names, area and effect.
        /// </summary>
        public string Id { get; set; }

        /// <summary>The id the framework uses: <see cref="Id"/>, or the made one, numbered when two rules make the same.</summary>
        internal string Key { get; set; }

        internal string MadeId() => Lifecycle.LayerMerge.RuleId(Side.ToString(), Name, Parent,
            Area.size == Vector3.zero ? null : new[] { Area.min.x, Area.min.y, Area.min.z },
            Area.size == Vector3.zero ? null : new[] { Area.max.x, Area.max.y, Area.max.z }, Effect.ToString());

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

        /// <summary>For <see cref="ObjectEffect.Hollow"/>: doorways left open in the shell, in the same coordinates as <see cref="Area"/>.</summary>
        public List<Bounds> Openings { get; private set; } = new();

        /// <summary>
        /// For <see cref="ObjectEffect.Hollow"/>: wall bands of the inside lining, each from a height above the floor
        /// in metres up to the next band or the ceiling, with the name of a loaded material.
        /// </summary>
        public List<(float From, string Material)> Bands { get; private set; } = new();

        /// <summary>For <see cref="ObjectEffect.Hollow"/>: the name of a loaded material for the lining's ceiling.</summary>
        public string CeilingMaterial { get; set; }

        /// <summary>
        /// For <see cref="ObjectEffect.Hollow"/>: the name of a loaded material for the lining's floor, laid 5 mm above
        /// the box's bottom face over whatever floor the model draws there.
        /// </summary>
        public string FloorMaterial { get; set; }

        /// <summary>For <see cref="ObjectEffect.Hollow"/>: boxes left open in the lining only, such as windows, in the same coordinates as <see cref="Area"/>.</summary>
        public List<Bounds> Windows { get; private set; } = new();

        /// <summary>
        /// For <see cref="ObjectEffect.Hollow"/>: the name of a loaded material for a frame around every hole in the lining's
        /// walls, <see cref="FrameWidth"/> wide on the wall and as deep as the gap between lining and outer wall.
        /// Edges on the floor, the ceiling or a corner, and edges inside another hole, get none.
        /// </summary>
        public string FrameMaterial { get; set; }

        /// <summary>For <see cref="ObjectEffect.Hollow"/>: width of <see cref="FrameMaterial"/> on the wall in metres.</summary>
        public float FrameWidth { get; set; } = 0.06f;

        /// <summary>
        /// For <see cref="ObjectEffect.Hollow"/>: metres between the outer walls and the lining's walls. More than the
        /// default keeps sills and frames the outer model draws inward behind the lining.
        /// </summary>
        public float LiningInset { get; set; } = 0.01f;

        /// <summary>A copy of this rule with its own lists, for a layer to change.</summary>
        internal ObjectRule Clone()
        {
            var r = (ObjectRule)MemberwiseClone();
            r.Openings = new List<Bounds>(Openings);
            r.Bands = new List<(float From, string Material)>(Bands);
            r.Windows = new List<Bounds>(Windows);
            return r;
        }

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

        /// <summary>Adds a box to <see cref="Openings"/>.</summary>
        public ObjectRule Opening(Vector3 min, Vector3 max)
        {
            var b = new Bounds();
            b.SetMinMax(Vector3.Min(min, max), Vector3.Max(min, max));
            Openings.Add(b);
            return this;
        }

        /// <summary>Adds a wall band to <see cref="Bands"/>.</summary>
        public ObjectRule Band(float from, string material)
        {
            Bands.Add((from, material));
            return this;
        }

        /// <summary>Sets <see cref="CeilingMaterial"/>.</summary>
        public ObjectRule Ceiling(string material)
        {
            CeilingMaterial = material;
            return this;
        }

        /// <summary>Sets <see cref="FloorMaterial"/>.</summary>
        public ObjectRule Floor(string material)
        {
            FloorMaterial = material;
            return this;
        }

        /// <summary>Sets <see cref="FrameMaterial"/> and <see cref="FrameWidth"/>.</summary>
        public ObjectRule Frame(string material, float width = 0.06f)
        {
            FrameMaterial = material;
            FrameWidth = width;
            return this;
        }

        /// <summary>Sets <see cref="LiningInset"/>.</summary>
        public ObjectRule Inset(float metres)
        {
            LiningInset = metres;
            return this;
        }

        /// <summary>Adds a box to <see cref="Windows"/>.</summary>
        public ObjectRule Window(Vector3 min, Vector3 max)
        {
            var b = new Bounds();
            b.SetMinMax(Vector3.Min(min, max), Vector3.Max(min, max));
            Windows.Add(b);
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
