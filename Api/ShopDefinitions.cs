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
        public List<string> Exclude { get; } = new();

        /// <summary>
        /// Solid boxes added to the copy, in the main world's coordinates like the template, each with the name of a
        /// loaded material. For a side the original leaves open because it stands against a wall the expansion lacks.
        /// </summary>
        public List<(Bounds Box, string Material)> Panels { get; } = new();

        public CopyDefinition() { }

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

        /// <summary>Adds a box to <see cref="Panels"/>.</summary>
        public CopyDefinition Panel(Vector3 min, Vector3 max, string material)
        {
            var b = new Bounds();
            b.SetMinMax(Vector3.Min(min, max), Vector3.Max(min, max));
            Panels.Add((b, material));
            return this;
        }
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
