using UnityEngine;

namespace MapExpansions
{
    /// <summary>A named, reversible local world state. The consumer saves and replicates its id.</summary>
    public sealed class WorldStateDefinition
    {
        public string Id { get; set; }
        public List<ObjectRule> Rules { get; } = new();
        public List<CopyDefinition> Copies { get; } = new();
        public List<PropertyExtensionDefinition> PropertyExtensions { get; } = new();
        /// <summary>World-space volume baked by the host for the required NPC agent profiles when this state is applied.</summary>
        public Bounds? NavigationBounds { get; set; }
        /// <summary>Ordered world-space points which must have a complete walking path after baking.</summary>
        public List<Vector3> NavigationProbes { get; } = new();
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
}
