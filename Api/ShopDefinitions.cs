using UnityEngine;

namespace MapExpansions
{
    /// <summary>
    /// A main-world object copied into the expansion, such as a shop's interior or its doors. The copy keeps the
    /// template's place relative to <see cref="Frame"/>, and the frame stands at <see cref="At"/>: when the expansion
    /// holds the same building as the main world, the frame is the main building and <see cref="At"/> the expansion's,
    /// so an interior lands in the expansion's walls as it stood in the main world's. Networked parts below the
    /// template (doors) are spawned by the host, so they open and close for everyone.
    /// </summary>
    public sealed class CopyDefinition
    {
        /// <summary>Stable id inside the expansion; networked parts of the copy are registered under it.</summary>
        public string Id { get; set; }

        /// <summary>
        /// Path of the template in the main world, from a scene root or from any object: "Pawn shop/Interior" is the
        /// child "Interior" of the first object called "Pawn shop". Objects of expansions never match.
        /// </summary>
        public string Template { get; set; }

        /// <summary>The main-world object whose place <see cref="At"/> gives. Empty means the template itself.</summary>
        public string Frame { get; set; }

        /// <summary>Where the frame stands, in expansion-local coordinates. Not used when <see cref="Onto"/> is set.</summary>
        public LocalPoint At { get; set; }

        /// <summary>
        /// Path of an object of the expansion's source the frame stands on instead of <see cref="At"/>, such as the
        /// expansion's own static door a working door replaces: "Pawn shop/Metal Glass Door (Static)". The frame takes
        /// its place and its full rotation, tilt included, moved by <see cref="OntoOffset"/> and turned by
        /// <see cref="OntoRotation"/> in that object's own axes.
        /// </summary>
        public string Onto { get; set; }

        /// <summary>Offset of the frame from <see cref="Onto"/>, in that object's axes.</summary>
        public Vector3 OntoOffset { get; set; }

        /// <summary>Euler angles that turn the frame against <see cref="Onto"/>, in that object's axes.</summary>
        public Vector3 OntoRotation { get; set; }

        /// <summary>Scale of the copy against the main world, for an expansion building built smaller.</summary>
        public float Scale { get; set; } = 1f;

        /// <summary>
        /// The template's own renderer and collider come along. False takes only what lies below it, for a shell whose
        /// contents are fitted into the expansion's own shell.
        /// </summary>
        public bool OwnMesh { get; set; } = true;

        /// <summary>Paths below the template that are left out of the copy, such as a door to a room the expansion lacks.</summary>
        public List<string> Exclude { get; private set; } = new();

        /// <summary>
        /// Boxes added to the copy, in the main world's coordinates like the template: panels with a loaded material for
        /// a side the original leaves open, and invisible blocks for a collider that would reach through the
        /// expansion's walls.
        /// </summary>
        public List<BoxDefinition> Boxes { get; private set; } = new();

        /// <summary>The panels among <see cref="Boxes"/>.</summary>
        public IEnumerable<BoxDefinition> Panels => Boxes.Where(b => b.Kind == BoxKind.Panel);

        /// <summary>The blocks among <see cref="Boxes"/>.</summary>
        public IEnumerable<BoxDefinition> Blocks => Boxes.Where(b => b.Kind == BoxKind.Block);

        /// <summary>Which world <see cref="Template"/> and <see cref="Frame"/> are found in.</summary>
        public WorldSide TemplateSide { get; set; } = WorldSide.Main;

        public CopyDefinition() { }

        /// <summary>A copy of this definition with its own lists and boxes, for a layer to change.</summary>
        internal CopyDefinition Clone()
        {
            var c = (CopyDefinition)MemberwiseClone();
            c.Exclude = new List<string>(Exclude);
            c.Boxes = Boxes.Select(b => b.Clone()).ToList();
            return c;
        }

        public CopyDefinition(string id, string template, string frame, LocalPoint at)
        {
            Id = id;
            Template = template;
            Frame = frame;
            At = at;
        }

        /// <summary>A copy whose frame stands on an object of the expansion's source.</summary>
        public static CopyDefinition OnObject(string id, string template, string frame, string onto,
            Vector3 offset = default, Vector3 rotation = default) =>
            new() { Id = id, Template = template, Frame = frame, Onto = onto, OntoOffset = offset, OntoRotation = rotation };

        /// <summary>Adds a panel to <see cref="Boxes"/>. Without an id it is "panel-1", "panel-2" in order.</summary>
        public CopyDefinition Panel(Vector3 min, Vector3 max, string material, string id = null) =>
            AddBox(BoxKind.Panel, min, max, material, id);

        /// <summary>Adds a block to <see cref="Boxes"/>. Without an id it is "block-1", "block-2" in order.</summary>
        public CopyDefinition Block(Vector3 min, Vector3 max, string id = null) => AddBox(BoxKind.Block, min, max, null, id);

        private CopyDefinition AddBox(BoxKind kind, Vector3 min, Vector3 max, string material, string id)
        {
            var b = new Bounds();
            b.SetMinMax(Vector3.Min(min, max), Vector3.Max(min, max));
            Boxes.Add(new BoxDefinition
            {
                Id = string.IsNullOrEmpty(id) ? Lifecycle.LayerMerge.BoxId(kind.ToString(), Boxes.Count(x => x.Kind == kind)) : id,
                Kind = kind,
                Box = b,
                Material = material,
            });
            return this;
        }
    }

    public enum BoxKind
    {
        /// <summary>A solid box drawn with a loaded material; the copy's own colliders stand around it.</summary>
        Panel,

        /// <summary>An invisible box collider that blocks the player.</summary>
        Block,
    }

    /// <summary>A box of a copy, in the main world's coordinates like the copy's template.</summary>
    public sealed class BoxDefinition
    {
        /// <summary>Stable id inside its copy, which a map layer uses to replace or remove the box.</summary>
        public string Id { get; set; }

        public BoxKind Kind { get; set; }

        public Bounds Box { get; set; }

        /// <summary>For <see cref="BoxKind.Panel"/>: the name of a loaded material.</summary>
        public string Material { get; set; }

        internal BoxDefinition Clone() => (BoxDefinition)MemberwiseClone();
    }

    /// <summary>What a shop's counter opens.</summary>
    public enum ShopKind
    {
        /// <summary>The main world's shop screen with <see cref="ShopDefinition.ShopCode"/>: its goods, prices and stock.</summary>
        Store,

        /// <summary>The pawn shop screen, where the player sells items.</summary>
        PawnShop,

        /// <summary>The barbershop screen, where the player changes hair and beard.</summary>
        Barbershop,
    }

    /// <summary>
    /// A shop with a seller at its counter, as in the main world: while the seller stands at the counter the player can
    /// talk to them and buy, and the shop's doors let the player in; otherwise the doors only let the player out.
    /// The seller's day, including when they walk to the counter, is the expansion's own schedule.
    /// </summary>
    public sealed class ShopDefinition
    {
        public string Id { get; set; }

        public ShopKind Kind { get; set; }

        /// <summary>For <see cref="ShopKind.Store"/>: the code of the main-world shop whose goods are sold, such as "gas_mart_west".</summary>
        public string ShopCode { get; set; }

        /// <summary>The NPC id of the seller.</summary>
        public string Seller { get; set; }

        /// <summary>Where the seller stands behind the counter, facing the customers.</summary>
        public LocalPoint Counter { get; set; }

        /// <summary>The seller counts as at the counter within this many metres of it.</summary>
        public float CounterReach { get; set; } = 1.5f;

        /// <summary>What the player says to buy. Empty uses the main world's line for the kind.</summary>
        public string ChoiceText { get; set; }

        /// <summary>The shop's interior, doors and props. Doors among them follow the seller.</summary>
        public List<CopyDefinition> Copies { get; } = new();

        /// <summary>
        /// For <see cref="ShopKind.Barbershop"/>: the main-world barbershop whose screen opens, and the object of the
        /// expansion's source that stands for it. The screen shows the player in the main barbershop's chair through
        /// its camera; while it is open, chair and camera stand in the expansion's shop as they stood in the main one.
        /// </summary>
        public string MainFrame { get; set; }

        /// <summary>See <see cref="MainFrame"/>.</summary>
        public string Onto { get; set; }
    }
}
