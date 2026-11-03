using Il2CppInterop.Runtime;
using MapExpansions.Lifecycle;
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

        internal sealed class StateBake : IDisposable
        {
            internal readonly List<NavMeshSurface> Surfaces = new();
            internal readonly List<Source.NavLinks.Lease> Links = new();
            public void Dispose()
            {
                // Each part leaves its list once it is gone, so a failed removal resumes on the next try.
                for (int i = Links.Count - 1; i >= 0; i--)
                {
                    Links[i].Dispose();
                    Links.RemoveAt(i);
                }
                for (int i = Surfaces.Count - 1; i >= 0; i--)
                {
                    var surface = Surfaces[i];
                    if (surface != null) { surface.RemoveData(); surface.gameObject.SetActive(false); Object.Destroy(surface.gameObject); }
                    Surfaces.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Keeps the player's placed items out of a state bake. Their visual colliders sit on the Default layer, so a
        /// PhysicsColliders bake over Default and Terrain treats racks and stations as walls; one standing on a doorway
        /// probe would fail the transition. Vanilla's NavMesh is the empty building with the items carving it, and this
        /// bake matches that: while it runs, the items' colliders sit on a layer outside the bake's mask. Moving the
        /// layer sends no trigger events, unlike disabling them, and the whole hold lasts one synchronous call.
        /// </summary>
        private sealed class PlacedItemsBakeHold : IDisposable
        {
            private readonly List<(GameObject go, int layer)> _moved = new();

            internal PlacedItemsBakeHold(string id, Bounds bounds)
            {
                try
                {
                    int excluded = LayerMask.NameToLayer("Ignore Raycast");
                    if (excluded < 0) throw new InvalidOperationException("layer 'Ignore Raycast' does not exist");
                    int items = 0;
                    foreach (var property in Property.Properties)
                    {
                        if (property?.BuildableItems == null) continue;
                        foreach (var item in property.BuildableItems)
                        {
                            if (item == null) continue;
                            int before = _moved.Count;
                            foreach (var c in item.GetComponentsInChildren<Collider>(true))
                            {
                                if (c == null || ((1 << c.gameObject.layer) & Mask) == 0 || !c.bounds.Intersects(bounds)) continue;
                                _moved.Add((c.gameObject, c.gameObject.layer));
                                c.gameObject.layer = excluded;
                            }
                            if (_moved.Count > before) items++;
                        }
                    }
                    // Loose physics props (dropped items, trash, boxes) move about and are no part of the floor plan either.
                    int loose = 0;
                    foreach (var c in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, Mask, QueryTriggerInteraction.Collide))
                    {
                        var body = c == null ? null : c.attachedRigidbody;
                        if (body == null || body.isKinematic) continue;
                        _moved.Add((c.gameObject, c.gameObject.layer));
                        c.gameObject.layer = excluded;
                        loose++;
                    }
                    Core.Log.Msg($"state nav: '{id}' keeps {items} placed items and {loose} loose props ({_moved.Count} collider objects) out of the bake sources");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                foreach (var (go, layer) in _moved)
                    if (go != null) go.layer = layer;
                _moved.Clear();
            }
        }

        internal static StateBake BakeState(string id, Bounds bounds, IReadOnlyList<Vector3> probes, Transform parent, IReadOnlyList<string> requiredAgents)
        {
            var result = new StateBake();
            try
            {
                var registered = NpcNavigation.AgentTypes().Select(a => new NavigationAgentSelection.Agent(a, NpcNavigation.AgentName(a))).ToArray();
                var agents = NavigationAgentSelection.Resolve(requiredAgents, registered);
                // A state's volume is property interior, as vanilla marks its buildings: civilians' masks leave the area out,
                // employees' include it. An expansion's own NPC needs the area in its mask to walk here.
                int blocked = 0;
                int area = NavMesh.GetAreaFromName(InteriorArea);
                if (area < 0) throw new InvalidOperationException($"state '{id}' needs the NavMesh area '{InteriorArea}', which this game does not define");
                using var placedItems = new PlacedItemsBakeHold(id, bounds);
                foreach (int agent in agents)
                {
                    var go = new GameObject($"MapExpansions.{id}.StateNav.{agent}");
                    go.transform.SetParent(parent, true);
                    go.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);
                    go.AddComponent(Il2CppType.Of<NavMeshSurface>());
                    var surface = go.GetComponent<NavMeshSurface>();
                    result.Surfaces.Add(surface);
                    surface.agentTypeID = agent;
                    surface.collectObjects = CollectObjects.Volume;
                    surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                    surface.layerMask = Mask;
                    surface.defaultArea = area;
                    surface.center = Vector3.zero;
                    surface.size = bounds.size;
                    var settings = NavMesh.GetSettingsByID(agent);
                    Core.Log.Msg($"state nav: '{id}' baking required agent '{NpcNavigation.AgentName(agent)}' ({agent}), radius {settings.agentRadius:0.###}, height {settings.agentHeight:0.###}, volume {bounds}");
                    surface.BuildNavMesh();
                    if (surface.navMeshData == null) throw new InvalidOperationException($"state '{id}' baked no navigation for agent {agent}");
                    // A probe can sit under whatever the player's world holds there (another mod's object, a loose prop).
                    // That costs NPCs a corner, so it is reported and the state still applies: a failed state blocks saving.
                    foreach (var point in probes)
                    {
                        if (NpcNavigation.Covered(point, agent)) continue;
                        Core.Log.Warning($"state nav: '{id}' point {point} has no navigation for required agent '{NpcNavigation.AgentName(agent)}' ({agent}, radius {settings.agentRadius:0.###}, height {settings.agentHeight:0.###}); obstacles there: {Obstacles(point, point, parent)}");
                        blocked++;
                    }
                }
                // The first two authored probes straddle the doorway. A short seam joins separate NavMesh data.
                if (probes.Count >= 2)
                {
                    foreach (int agent in agents)
                    {
                        var path = new NavMeshPath();
                        var filter = new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
                        if (!NavMesh.CalculatePath(probes[0], probes[1], filter, path) || path.status != NavMeshPathStatus.PathComplete)
                        {
                            if (Vector3.Distance(probes[0], probes[1]) > 2.5f)
                                throw new InvalidOperationException($"state '{id}' doorway probes are over 2.5 m apart; no safe seam");
                            result.Links.Add(Source.NavLinks.AddOwned(probes[0], probes[1], 0.7f, new[] { agent }));
                        }
                        for (int i = 1; i < probes.Count; i++)
                        {
                            if (NavMesh.CalculatePath(probes[0], probes[i], filter, path) && path.status == NavMeshPathStatus.PathComplete) continue;
                            Core.Log.Warning($"state nav: '{id}' has no complete path to probe {i} {probes[i]} for agent {agent}; obstacles on the line from the doorway: {Obstacles(probes[0], probes[i], parent)}");
                            blocked++;
                        }
                    }
                }
                if (blocked == 0) Core.Log.Msg($"state nav: '{id}' baked {agents.Length} agent types; {probes.Count} probes connected");
                else Core.Log.Warning($"state nav: '{id}' baked {agents.Length} agent types; {blocked} probe checks failed, the state applies and NPCs may not reach those points");
                return result;
            }
            catch (Exception original)
            {
                try { result.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("state navigation and cleanup failed", original, cleanup); }
                throw;
            }
        }

        private static readonly int Mask = LayerMask.GetMask("Default", "Terrain");
        private const string InteriorArea = "PropertyInterior";

        /// <summary>Names the bake-source colliders on a walking line at knee height, so a failed probe says what stands there.</summary>
        private static string Obstacles(Vector3 from, Vector3 to, Transform expansion)
        {
            try
            {
                var lift = Vector3.up * 0.5f;
                var names = new List<string>();
                foreach (var c in Physics.OverlapCapsule(from + lift, to + lift, 0.4f, Mask, QueryTriggerInteraction.Ignore))
                {
                    if (c == null) continue;
                    var t = c.transform;
                    string path = t.name;
                    for (var p = t.parent; p != null && path.Length < 120; p = p.parent) path = p.name + "/" + path;
                    if (expansion != null && t.IsChildOf(expansion)) path = "[expansion] " + path;
                    names.Add(path);
                }
                return names.Count == 0 ? "none found" : string.Join("; ", names.Distinct().Take(10));
            }
            catch (Exception e)
            {
                Core.Log.Warning($"state nav: obstacle lookup failed: {e.Message}");
                return "lookup failed";
            }
        }

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
