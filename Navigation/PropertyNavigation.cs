using Il2CppInterop.Runtime;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;
using Property = Il2CppScheduleOne.Property.Property;

namespace MapExpansions.Navigation
{
    /// <summary>
    /// NavMesh for the agent types an expansion's content has none for around a copied property. Employees walk on
    /// their own agent type, which the main world bakes everywhere and an expansion's source usually lacks; without it
    /// they stand at their idle point and never reach a station or the loading dock.
    /// </summary>
    internal static class PropertyNavigation
    {
        /// <summary>Metres the baked volume reaches past the property's walls, docks and idle points.</summary>
        private const float Margin = 4f;

        private static readonly int Mask = LayerMask.GetMask("Default", "Terrain");

        /// <summary>
        /// Bakes the copy's surroundings for every agent type without a NavMesh at its idle points. The pieces hang under
        /// <paramref name="parent"/>, an unmirrored frame, since a mirrored property would turn the baked volume inside
        /// out. Run it before the save places objects on the copy's grids, so the NavMesh is the empty building, as the
        /// main world's is.
        /// </summary>
        public static void Bake(string code, Property p, Transform parent)
        {
            var probes = new List<Vector3>();
            if (p.EmployeeIdlePoints != null)
                foreach (var t in p.EmployeeIdlePoints)
                    if (t != null) probes.Add(t.position);
            if (probes.Count == 0)
            {
                Core.Log.Msg($"property nav: '{code}' has no idle points; nothing to bake");
                return;
            }
            var missing = NpcNavigation.AgentTypes().Where(a => !probes.All(x => NpcNavigation.Covered(x, a))).ToList();
            if (missing.Count == 0)
            {
                Core.Log.Msg($"property nav: '{code}' every agent type has a NavMesh at its idle points; nothing to bake");
                return;
            }

            var bounds = new Bounds(probes[0], Vector3.zero);
            foreach (var x in probes) bounds.Encapsulate(x);
            foreach (var c in p.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger || ((1 << c.gameObject.layer) & Mask) == 0) continue;
                bounds.Encapsulate(c.bounds);
            }
            if (p.LoadingDocks != null)
                foreach (var d in p.LoadingDocks)
                    if (d != null) bounds.Encapsulate(d.transform.position);
            bounds.Expand(new Vector3(Margin * 2f, 2f, Margin * 2f));

            foreach (int agent in missing)
            {
                string name = NpcNavigation.AgentName(agent);
                var go = new GameObject($"MapExpansions.{code}.Nav.{name}");
                go.transform.SetParent(parent, true);
                go.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
                go.AddComponent(Il2CppType.Of<NavMeshSurface>());
                var s = go.GetComponent<NavMeshSurface>();
                s.agentTypeID = agent;
                s.collectObjects = CollectObjects.Volume;
                s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                s.layerMask = Mask;
                s.center = Vector3.zero;
                s.size = bounds.size;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    s.BuildNavMesh();
                }
                catch (Exception e)
                {
                    Core.Log.Error($"property nav: '{code}' ({name}) failed to bake; its employees cannot walk: {e.Message}");
                    Object.Destroy(go);
                    continue;
                }
                if (s.navMeshData == null)
                {
                    Core.Log.Error($"property nav: '{code}' ({name}) baked no data; its employees cannot walk");
                    Object.Destroy(go);
                    continue;
                }
                int reached = probes.Count(x => NpcNavigation.Covered(x, agent));
                string line = $"property nav: '{code}' baked {name} over {bounds.size.x:0} x {bounds.size.z:0} m at {bounds.center} in {sw.ElapsedMilliseconds} ms, " +
                              $"{reached}/{probes.Count} idle points on it";
                if (reached < probes.Count) Core.Log.Warning(line);
                else Core.Log.Msg(line);
            }
        }
    }
}
