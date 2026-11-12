using UnityEngine;

namespace MapExpansions
{
    /// <summary>A named, reversible local world state. The consumer saves and replicates its id.</summary>
    public sealed class WorldStateDefinition
    {
        public string Id { get; set; }
        public List<ObjectRule> Rules { get; } = new();
        public List<CopyDefinition> Copies { get; } = new();
        /// <summary>Main-world objects that stand elsewhere in this state, for fixtures that must keep working.</summary>
        public List<ObjectMoveDefinition> Moves { get; } = new();
        public List<PropertyExtensionDefinition> PropertyExtensions { get; } = new();
        /// <summary>World-space volume baked by the host for the required NPC agent profiles when this state is applied.</summary>
        public Bounds? NavigationBounds { get; set; }
        /// <summary>Ordered world-space points which must have a complete walking path after baking.</summary>
        public List<Vector3> NavigationProbes { get; } = new();
        /// <summary>
        /// Floor inside <see cref="NavigationBounds"/> that the main world's NavMesh already covers, such as an old room whose
        /// walls came out. The bake leaves these boxes out, so two walking maps never lie on top of each other, and joins
        /// each box's edges to the new floor around it every 0.75 m. A box's bottom face is the floor height. Without
        /// these, an NPC standing there can end up on the state's copy of the floor with no way back to the world.
        /// </summary>
        public List<Bounds> NavigationExistingFloor { get; } = new();
        /// <summary>
        /// World-space volumes under a roof of this state that has no floor or navigation of its own, such as a carport.
        /// The game's rain mask knows nothing of the roof; inside these volumes the roof's colliders decide instead.
        /// <see cref="NavigationBounds"/> and the property extensions' bounds count as cover without this list.
        /// </summary>
        public List<Bounds> WeatherCover { get; } = new();
        /// <summary>Exact registered agent names this state must support. Empty requires every registered profile.</summary>
        public List<string> NavigationAgents { get; } = new();
        /// <summary>An embedded editor layer containing copies, rules and boxes for this state.</summary>
        public LayerSource Layer { get; set; }
        public WorldStateDefinition UseLayer(System.Reflection.Assembly assembly, string resourceName)
        {
            Layer = new LayerSource { Assembly = assembly, ResourceName = resourceName };
            return this;
        }
    }

    /// <summary>
    /// Moves one main-world object itself for as long as a state stands. Unlike a copy it keeps its interactions and
    /// network behaviours. The object goes back when the state leaves.
    /// </summary>
    public sealed class ObjectMoveDefinition
    {
        /// <summary>Unique inside the state.</summary>
        public string Id { get; set; }

        /// <summary>Path of the object in the main world, as in <see cref="CopyDefinition.Template"/>.</summary>
        public string Path { get; set; }

        /// <summary>World-space shift of the object's pivot.</summary>
        public Vector3 Offset { get; set; }

        /// <summary>
        /// The object may be missing, for example because another mod removed it. An optional move is then logged at info
        /// level and skipped; otherwise a missing object fails the whole state.
        /// </summary>
        public bool Optional { get; set; }
    }
}
