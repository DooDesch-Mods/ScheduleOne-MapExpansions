using UnityEngine;

namespace MapExpansions
{
    /// <summary>How the framework builds the stretch from one route point to the next.</summary>
    public enum RouteStyle
    {
        /// <summary>Nothing is built; the stretch already exists in one of the worlds. Only navigation joins it.</summary>
        Existing,

        /// <summary>Road deck, curbs and walkways, no walls; for a stretch inside an existing tunnel liner.</summary>
        Deck,

        /// <summary>Road deck, walkways and low guard walls under the open sky.</summary>
        Road,

        /// <summary>Road deck, walkways, walls, vault, outer shell and lights.</summary>
        Tunnel,
    }

    /// <summary>Sides of a tunnel portal, seen from outside looking into the tunnel.</summary>
    [Flags]
    public enum WingWalls
    {
        None = 0,
        Left = 1,
        Right = 2,
        Both = Left | Right,
    }

    /// <summary>One point on the centre line of a connection's road surface.</summary>
    public readonly struct RoutePoint
    {
        public readonly PathPoint Point;

        /// <summary>Style of the stretch from this point to the next one.</summary>
        public readonly RouteStyle Style;

        /// <summary>Road width at this point; 0 takes <see cref="ConnectionDefinition.RoadWidth"/>. Changes ease in along the leg.</summary>
        public readonly float RoadWidth;

        /// <summary>
        /// Width of the barrier between the two directions at this point, 0 for none. Where it narrows to nothing, the
        /// barrier ramps down into the road.
        /// </summary>
        public readonly float MedianWidth;

        public RoutePoint(PathPoint point, RouteStyle style, float roadWidth = 0f, float medianWidth = 0f)
        {
            Point = point;
            Style = style;
            RoadWidth = roadWidth;
            MedianWidth = medianWidth;
        }
    }

    /// <summary>A material taken from a renderer of one of the worlds, matched by name.</summary>
    public sealed class MaterialRef
    {
        public WorldSide Side { get; set; }

        /// <summary>Renderer name, compared case-insensitively from the start.</summary>
        public string Renderer { get; set; }

        /// <summary>When set, some ancestor's name starts with this.</summary>
        public string Parent { get; set; }

        /// <summary>Material slot of the renderer.</summary>
        public int Slot { get; set; }

        /// <summary>Colour of the plain material used when the renderer is not found.</summary>
        public Color Fallback { get; set; } = new(0.45f, 0.45f, 0.45f);

        /// <summary>Metres of surface one texture repeat covers along and across the road.</summary>
        public float Tiling { get; set; } = 4f;

        public static MaterialRef From(WorldSide side, string renderer, string parent = null, int slot = 0) =>
            new() { Side = side, Renderer = renderer, Parent = parent, Slot = slot };

        public MaterialRef Tiled(float metres)
        {
            Tiling = metres;
            return this;
        }

        public MaterialRef Or(Color fallback)
        {
            Fallback = fallback;
            return this;
        }

        public override string ToString() => $"{Side} '{Renderer}'{(Parent != null ? $" under '{Parent}'" : "")} slot {Slot}";
    }

    /// <summary>
    /// A drivable and walkable link between the main world and an expansion. The route runs along the road surface's
    /// centre line, from the main-world end to the expansion end. The framework builds the stretches, clears what
    /// stands in their way, joins the vehicle graphs and the NPC navigation at both ends, and keeps every collider the
    /// route depends on active.
    /// </summary>
    public sealed class ConnectionDefinition
    {
        public string Name { get; set; }

        public List<RoutePoint> Route { get; } = new();

        /// <summary>Width of the drivable deck between the curbs.</summary>
        public float RoadWidth { get; set; } = 8f;

        /// <summary>Width of each walkway beside the deck.</summary>
        public float WalkwayWidth { get; set; } = 1.4f;

        public float CurbHeight { get; set; } = 0.18f;

        /// <summary>Height of the straight tunnel wall above the walkway.</summary>
        public float WallHeight { get; set; } = 4.6f;

        /// <summary>Height of the vault's crown above the road.</summary>
        public float CrownHeight { get; set; } = 7f;

        /// <summary>Height of the guard walls on <see cref="RouteStyle.Road"/> stretches.</summary>
        public float GuardHeight { get; set; } = 0.9f;

        /// <summary>Height of the barrier between the two directions where a route point gives it a width.</summary>
        public float MedianHeight { get; set; } = 0.8f;

        /// <summary>Thickness of the rock shell around a tunnel, which closes holes cut into hills and rock.</summary>
        public float ShellThickness { get; set; } = 1.5f;

        /// <summary>
        /// How far the facade around a tunnel mouth reaches beyond the shell, to the sides and above. The facade closes
        /// what the clearing cut out of the slope or a portal around the mouth. 0 builds none.
        /// </summary>
        public float PortalMargin { get; set; } = 4f;

        /// <summary>Metres between two tunnel lights.</summary>
        public float LightSpacing { get; set; } = 16f;

        public float LightRange { get; set; } = 13f;

        public float LightIntensity { get; set; } = 2.2f;

        public Color LightColor { get; set; } = new(1f, 0.86f, 0.66f);

        public MaterialRef RoadMaterial { get; set; }

        public MaterialRef WalkwayMaterial { get; set; }

        public MaterialRef WallMaterial { get; set; }

        public MaterialRef ShellMaterial { get; set; }

        /// <summary>
        /// Main-world point where the vanilla vehicle graph and the expansion's graph overlap. The framework joins
        /// the two there across the road's width.
        /// </summary>
        public PathPoint? VehicleJoin { get; set; }

        /// <summary>
        /// Objects that stay although they stand in the route's way. Everything else that intersects the inside of a
        /// built stretch is switched off, or cut where it is too large to switch off as a whole.
        /// </summary>
        public List<ObjectRule> Keep { get; } = new();

        /// <summary>
        /// Objects of the existing world that reach over the road in front of the built range, such as a rock face over
        /// the lanes before a portal. Over the last <see cref="ApproachLength"/> metres of road at both ends they are cut
        /// across the road's width, from its surface to the crown; the rest of them stays. Other objects there stay whole.
        /// The rules select by name, parent and area; their effect is not read.
        /// </summary>
        public List<ObjectRule> ClearApproach { get; } = new();

        /// <summary>
        /// Metres of existing road before and after the built range that count as its approach: terrain above the road
        /// becomes a hole there, and <see cref="ClearApproach"/> objects are cut.
        /// </summary>
        public float ApproachLength { get; set; } = 6f;

        /// <summary>
        /// Sides on which the tunnel's walls run on in front of the portal at the start of the built range, over
        /// <see cref="ApproachLength"/> metres, as thick as the shell and as high as the portal frame. For a rock face
        /// beside the approach: it ends behind the wall instead of in a cut edge, and <see cref="ClearApproach"/>
        /// objects are cut up to the wall on that side.
        /// </summary>
        public WingWalls StartWingWalls { get; set; }

        /// <summary>As <see cref="StartWingWalls"/>, at the portal at the end of the built range.</summary>
        public WingWalls EndWingWalls { get; set; }

        public ConnectionDefinition Add(PathPoint point, RouteStyle style)
        {
            Route.Add(new RoutePoint(point, style));
            return this;
        }

        /// <summary>Adds a point with its own road width and median, see <see cref="RoutePoint.RoadWidth"/>.</summary>
        public ConnectionDefinition Add(PathPoint point, RouteStyle style, float roadWidth, float medianWidth = 0f)
        {
            Route.Add(new RoutePoint(point, style, roadWidth, medianWidth));
            return this;
        }
    }
}
