using MapExpansions.Lifecycle;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Widens the physics world to the expansions. The game uses multibox pruning with world bounds of 250 m around the
    /// origin and 50 m up and down. A body outside them gets no contacts: a car there stands on its wheel rays and drives
    /// through walls and falls through floors, while queries such as raycasts still hit. After the build the bounds cover
    /// the main map and every collider of the expansions; they go back to the game's value on teardown.
    /// </summary>
    internal static class PhysicsBounds
    {
        /// <summary>The game's world bounds (ProjectSettings/DynamicsManager: center 0, extent 250 x 50 x 250, 16 subdivisions).</summary>
        private static readonly Bounds Vanilla = new(Vector3.zero, new Vector3(500f, 100f, 500f));

        private const int Subdivisions = 16;
        private const float Margin = 50f;

        private static bool _extended;

        public static void Extend(IReadOnlyList<BuiltExpansion> built)
        {
            var bounds = Vanilla;
            int counted = 0;
            foreach (var b in built)
            {
                if (b.Root == null)
                {
                    Core.Log.Warning($"physics bounds: '{b.Id}' has no root; its colliders do not count");
                    continue;
                }
                foreach (var c in b.Root.GetComponentsInChildren<Collider>(true))
                {
                    // A switched-off collider reports empty bounds.
                    if (c == null || !c.enabled || c.bounds.size == Vector3.zero) continue;
                    bounds.Encapsulate(c.bounds);
                    counted++;
                }
            }
            bounds.Expand(Margin * 2f);
            try
            {
                Physics.RebuildBroadphaseRegions(bounds, Subdivisions);
            }
            catch (Exception e)
            {
                Core.Log.Error($"physics bounds: widening to {bounds.min}..{bounds.max} failed; vehicles outside the game's 250 m pass through walls: {e}");
                return;
            }
            _extended = true;
            Core.Log.Msg($"physics bounds: {Vanilla.min}..{Vanilla.max} -> {bounds.min}..{bounds.max} from {counted} colliders, {Subdivisions} subdivisions");
        }

        public static void Restore(string why)
        {
            if (!_extended) return;
            _extended = false;
            try
            {
                Physics.RebuildBroadphaseRegions(Vanilla, Subdivisions);
                Core.Log.Msg($"physics bounds: {why}, back to {Vanilla.min}..{Vanilla.max}");
            }
            catch (Exception e)
            {
                Core.Log.Error($"physics bounds: {why}, restoring the game's bounds failed: {e}");
            }
        }
    }
}
