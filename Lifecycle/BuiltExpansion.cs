using MapExpansions.Connections;
using MapExpansions.Source;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Lifecycle
{
    /// <summary>One expansion placed in the loaded main world, with everything the build created for it.</summary>
    internal sealed class BuiltExpansion
    {
        public readonly ExpansionDefinition Definition;
        public readonly Frame Frame;
        public readonly CapturedSource Source;

        /// <summary>Root at the expansion's offset and rotation; it lives in the main scene and goes with it.</summary>
        public GameObject Root;

        /// <summary>The placed copy of the source root, under <see cref="Root"/>.</summary>
        public GameObject Content;

        public readonly List<BuiltConnection> Connections = new();

        /// <summary>Assets made for this build (meshes, materials, terrain data); destroyed on teardown.</summary>
        public readonly List<Object> Owned = new();

        public BuiltExpansion(ExpansionDefinition def, CapturedSource source)
        {
            Definition = def;
            Frame = Frame.Of(def);
            Source = source;
        }

        public string Id => Definition.Id;

        public bool IsOurs(Transform t) => Root != null && t != null && t.IsChildOf(Root.transform);

        public T Own<T>(T o) where T : Object
        {
            if (o != null) Owned.Add(o);
            return o;
        }

        public void Teardown()
        {
            int destroyed = 0;
            foreach (var o in Owned)
            {
                if (o == null) continue;
                Object.Destroy(o);
                destroyed++;
            }
            Owned.Clear();
            if (Root != null) Object.Destroy(Root);
            Root = null;
            Content = null;
            Connections.Clear();
            Core.Log.Msg($"teardown: '{Id}' released {destroyed} owned assets");
        }
    }

    /// <summary>One connection of an expansion as built in the world.</summary>
    internal sealed class BuiltConnection
    {
        public readonly ConnectionDefinition Definition;
        public readonly RouteCurve Curve;

        /// <summary>Parent of every chunk, light and collider the connection built.</summary>
        public GameObject Root;

        /// <summary>Chunks that carry a road surface, used for the NPC navigation bake.</summary>
        public readonly List<GameObject> SurfaceChunks = new();

        /// <summary>
        /// Boxes that must stay clear for the built stretches: the drivable and walkable inside, from just under the
        /// deck to the vault. Meshes in them are cut, so nothing shows through the deck or the walls.
        /// </summary>
        public readonly List<OrientedBox> Interior = new();

        /// <summary>
        /// The part of the inside that something must reach into to stand in the way. Colliders that cannot be cut
        /// are switched off only when they reach in here, so a large box under the deck keeps carrying the world
        /// around it.
        /// </summary>
        public readonly List<OrientedBox> Blocking = new();

        /// <summary>Boxes whose terrain cells become holes, so the deck lies free and the tunnel can enter a hill.</summary>
        public readonly List<OrientedBox> Footprint = new();

        public BuiltConnection(ConnectionDefinition def, RouteCurve curve)
        {
            Definition = def;
            Curve = curve;
        }

        public string Name => string.IsNullOrEmpty(Definition.Name) ? "connection" : Definition.Name;

        /// <summary>Distance range along the route that the framework built, or (0, 0) when everything already existed.</summary>
        public (float from, float to) BuiltRange
        {
            get
            {
                float from = float.MaxValue, to = float.MinValue;
                foreach (var (style, a, b) in Curve.Stretches())
                {
                    if (style == RouteStyle.Existing) continue;
                    from = Mathf.Min(from, a);
                    to = Mathf.Max(to, b);
                }
                return from > to ? (0f, 0f) : (from, to);
            }
        }

        public float HalfInnerWidth => Definition.RoadWidth * 0.5f + Definition.WalkwayWidth;
    }
}
