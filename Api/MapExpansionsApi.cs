using MapExpansions.Lifecycle;
using UnityEngine;

namespace MapExpansions
{
    /// <summary>Build state of one registered expansion.</summary>
    public enum ExpansionState
    {
        /// <summary>Registered, definition not checked yet.</summary>
        Registered,

        /// <summary>The definition failed its check; the expansion is never built. The log names the reason.</summary>
        Rejected,

        /// <summary>Definition checked, waiting for the source capture in the main menu.</summary>
        Validated,

        /// <summary>Source content is in the cache.</summary>
        Captured,

        /// <summary>The capture failed; the expansion is not built this session.</summary>
        CaptureFailed,

        /// <summary>Placed in the main world, navigation still being joined.</summary>
        Placed,

        /// <summary>Placed and joined; players, vehicles and NPCs can use it.</summary>
        Ready,

        /// <summary>The build failed part-way; the log names the step.</summary>
        BuildFailed,
    }

    /// <summary>Entry point for mods that add or use map expansions.</summary>
    public static class MapExpansionsApi
    {
        /// <summary>Version of this API. Raised when a public member changes.</summary>
        public const string ApiVersion = "0.3.0";

        /// <summary>
        /// Raised when an expansion is placed and joined in the main world, once per world load. The argument is the
        /// expansion id.
        /// </summary>
        public static event Action<string> ExpansionReady;

        /// <summary>
        /// Registers an expansion. Call it from <c>OnInitializeMelon</c>; definitions registered after the main menu
        /// opened are checked but only built from the next game start on.
        /// </summary>
        /// <returns>False when the id is taken or the definition is null; the log names the reason.</returns>
        public static bool Register(ExpansionDefinition definition) => Registry.Register(definition);

        /// <summary>Ids of all registered expansions, in registration order.</summary>
        public static IReadOnlyList<string> Expansions => Registry.Ids;

        public static ExpansionState GetState(string expansionId) => Registry.GetState(expansionId);

        /// <summary>True while the expansion is placed and joined in the loaded world.</summary>
        public static bool IsReady(string expansionId) => Registry.GetState(expansionId) == ExpansionState.Ready;

        /// <summary>World position of an expansion-local point. False while the expansion is not placed.</summary>
        public static bool TryToWorld(string expansionId, Vector3 local, out Vector3 world)
        {
            world = default;
            var built = BuildPipeline.Get(expansionId);
            if (built == null) return false;
            world = built.Frame.ToWorld(local);
            return true;
        }

        /// <summary>
        /// World position of an expansion-local point, from the registered definition. It holds before the expansion is
        /// placed, for setup code such as NPC spawn points; it says nothing about whether the expansion gets built.
        /// </summary>
        public static bool TryToWorldFromDefinition(string expansionId, Vector3 local, out Vector3 world)
        {
            world = default;
            var def = Registry.GetDefinition(expansionId);
            if (def == null) return false;
            world = Frame.Of(def).ToWorld(local);
            return true;
        }

        /// <summary>World rotation of an expansion-local facing in degrees, from the registered definition.</summary>
        public static bool TryToWorldRotationFromDefinition(string expansionId, float localYaw, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            var def = Registry.GetDefinition(expansionId);
            if (def == null) return false;
            rotation = Frame.Of(def).Rotation * Quaternion.Euler(0f, localYaw, 0f);
            return true;
        }

        /// <summary>Expansion-local position of a world point. False while the expansion is not placed.</summary>
        public static bool TryToLocal(string expansionId, Vector3 world, out Vector3 local)
        {
            local = default;
            var built = BuildPipeline.Get(expansionId);
            if (built == null) return false;
            local = built.Frame.ToLocal(world);
            return true;
        }

        /// <summary>World position and facing of a named place. False while it is unknown or not placed.</summary>
        public static bool TryResolve(LocationRef location, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            var def = Registry.GetDefinition(location.ExpansionId);
            var built = BuildPipeline.Get(location.ExpansionId);
            if (def == null || built == null) return false;
            foreach (var l in def.Locations)
            {
                if (!string.Equals(l.Name, location.Name, StringComparison.Ordinal)) continue;
                position = built.Frame.ToWorld(l.LocalPosition);
                rotation = built.Frame.Rotation * Quaternion.Euler(0f, l.LocalYaw, 0f);
                return true;
            }
            return false;
        }

        /// <summary>
        /// A character of the expansion's source scene by its object name, such as "Pam". Known from the main menu on,
        /// once the source is captured.
        /// </summary>
        public static bool TryGetSourceCharacter(string expansionId, string objectName, out SourceCharacter character)
        {
            character = null;
            var def = Registry.GetDefinition(expansionId);
            var src = def != null ? Source.SourceCapture.Get(def.Source.Key) : null;
            character = src?.Characters.FirstOrDefault(c => string.Equals(c.ObjectName, objectName, StringComparison.Ordinal));
            return character != null;
        }

        /// <summary>Id of the expansion whose region contains the world point, or null.</summary>
        public static string ExpansionAt(Vector3 world) => Regions.RegionRegistry.ExpansionAt(world);

        internal static void RaiseReady(string expansionId)
        {
            var handlers = ExpansionReady;
            if (handlers == null)
            {
                Core.Log.Msg($"api: '{expansionId}' ready, no ExpansionReady subscriber");
                return;
            }
            foreach (Action<string> h in handlers.GetInvocationList())
            {
                try
                {
                    h(expansionId);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"api: ExpansionReady handler {h.Method.DeclaringType?.FullName}.{h.Method.Name} failed for '{expansionId}': {e}");
                }
            }
            Core.Log.Msg($"api: '{expansionId}' ready, {handlers.GetInvocationList().Length} subscribers notified");
        }
    }
}
