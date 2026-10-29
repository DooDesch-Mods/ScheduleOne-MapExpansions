using UnityEngine;
using UnityEngine.AI;

namespace MapExpansions.World
{
    /// <summary>
    /// NavMesh obstacles on placed copies, so NPCs walk around what a layer or the map editor put into their way. Each
    /// obstacle is a box around the copy that carves its outline out of the walkable ground once the copy stands still.
    /// </summary>
    internal static class Obstacles
    {
        /// <summary>Below this height a copy is walked over, like a rug or a floor plate, and carves nothing.</summary>
        private const float MinHeight = 0.25f;

        /// <summary>
        /// Whether the copy gets an obstacle: as its definition says, else for models, game prefabs and copies a layer
        /// adds. The copies of the code definition stand where the expansion's NavMesh was made around them.
        /// </summary>
        internal static bool Wanted(CopyDefinition d) =>
            d.Obstacle ?? (d.Added || !string.IsNullOrEmpty(d.Model) || !string.IsNullOrEmpty(d.Prefab));

        /// <summary>Puts the obstacle on the copy or takes it off, as <see cref="Wanted"/> says; returns whether it has one now.</summary>
        internal static bool Apply(MainCopies.Built built, string what)
        {
            var handle = built.Handle;
            if (handle == null)
            {
                Core.Log.Msg($"obstacles: {what} has nothing in the world yet; its obstacle follows when it stands");
                return false;
            }
            return Wanted(built.Def) ? Fit(handle.gameObject, what) : Remove(handle.gameObject, what);
        }

        /// <summary>Puts a carving box around the object's colliders, or its renderers when it has no collider.</summary>
        internal static bool Fit(GameObject go, string what)
        {
            if (!LocalBounds(go.transform, out var box, out string from))
            {
                Core.Log.Warning($"obstacles: {what} has neither collider nor renderer; NPCs walk through it");
                return Remove(go, what);
            }
            if (box.size.y * go.transform.lossyScale.y < MinHeight)
            {
                Core.Log.Msg($"obstacles: {what} is {box.size.y * go.transform.lossyScale.y:0.00} m high and is walked over; no obstacle");
                return Remove(go, what);
            }
            var ob = go.GetComponent<NavMeshObstacle>();
            if (ob == null) ob = go.AddComponent<NavMeshObstacle>();
            ob.shape = NavMeshObstacleShape.Box;
            ob.center = box.center;
            ob.size = box.size;
            ob.carving = true;
            ob.carveOnlyStationary = true;
            ob.enabled = true;
            Core.Log.Msg($"obstacles: {what} carves a box of {Vector3.Scale(box.size, go.transform.lossyScale)} m around its {from} at {go.transform.TransformPoint(box.center)}");
            return true;
        }

        /// <summary>Takes the obstacle off the object; returns false, as the object then has none.</summary>
        internal static bool Remove(GameObject go, string what)
        {
            var ob = go.GetComponent<NavMeshObstacle>();
            if (ob == null) return false;
            UnityEngine.Object.Destroy(ob);
            Core.Log.Msg($"obstacles: {what} no longer carves the NavMesh");
            return false;
        }

        /// <summary>The bounds of the object's solid colliders, else of its renderers, in its own space.</summary>
        private static bool LocalBounds(Transform root, out Bounds box, out string from)
        {
            box = default;
            bool any = false;
            var b = new Bounds();
            void Add(Vector3 world)
            {
                var p = root.InverseTransformPoint(world);
                if (!any) b = new Bounds(p, Vector3.zero);
                else b.Encapsulate(p);
                any = true;
            }
            void Corners(Transform t, Bounds local)
            {
                var c = local.center;
                var e = local.extents;
                for (int i = 0; i < 8; i++)
                    Add(t.TransformPoint(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
            }
            void WorldCorners(Bounds world)
            {
                var c = world.center;
                var e = world.extents;
                for (int i = 0; i < 8; i++)
                    Add(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
            }

            from = "colliders";
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (c.TryCast<TerrainCollider>() != null) continue;
                var bc = c.TryCast<BoxCollider>();
                var mc = bc == null ? c.TryCast<MeshCollider>() : null;
                if (bc != null) Corners(c.transform, new Bounds(bc.center, bc.size));
                else if (mc != null && mc.sharedMesh != null) Corners(c.transform, mc.sharedMesh.bounds);
                else WorldCorners(c.bounds);
            }
            if (!any)
            {
                from = "renderers";
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    var mf = r.isPartOfStaticBatch ? null : r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) Corners(r.transform, mf.sharedMesh.bounds);
                    else WorldCorners(r.bounds);
                }
            }
            box = b;
            return any;
        }
    }
}
