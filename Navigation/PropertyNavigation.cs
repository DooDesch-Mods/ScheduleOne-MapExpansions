using Il2CppInterop.Runtime;
using Il2CppScheduleOne.Tools;
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

#if DEBUG
        /// <summary>Dev switch for `mxreach keepcache`: leaves the cache alone to compare against the old behaviour.</summary>
        internal static bool KeepSamplesForTest;
#endif

        /// <summary>
        /// Drops the game's NavMesh sample cache. Employees ask it before every reach check (NavMeshUtility.SamplePosition),
        /// it keeps a hit for every point within 0.5 m of an earlier query, and only a save load clears it. After a state
        /// adds or removes NavMesh, a hit from the old mesh can sit far enough from a station's access point that the
        /// station counts as unreachable (the spawn station allows 0.6 m).
        /// </summary>
        internal static void ForgetSamples(string why)
        {
#if DEBUG
            if (KeepSamplesForTest)
            {
                Core.Log.Warning($"[dev] nav: kept the game's NavMesh sample cache after {why} (mxreach keepcache on)");
                return;
            }
#endif
            try
            {
                int entries = Il2CppScheduleOne.DevUtilities.NavMeshUtility.sampleCacheKeys?.Count ?? 0;
                Il2CppScheduleOne.DevUtilities.NavMeshUtility.ClearCache();
                Core.Log.Msg($"nav: cleared the game's NavMesh sample cache ({entries} entries) after {why}");
            }
            catch (Exception e)
            {
                Core.Log.Warning($"nav: could not clear the game's NavMesh sample cache after {why}; employees may misjudge reach until the next load: {e.Message}");
            }
        }

        internal sealed class StateBake : IDisposable
        {
            internal readonly List<NavMeshSurface> Surfaces = new();
            internal readonly List<Source.NavLinks.Lease> Links = new();
            public void Dispose()
            {
                bool had = Surfaces.Count > 0 || Links.Count > 0;
                try { RemoveParts(); }
                finally { if (had) ForgetSamples("a state's navigation was removed"); }
            }

            private void RemoveParts()
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

        /// <summary>
        /// The NavMesh builder reads a mesh collider's vertices on the CPU and skips an unreadable mesh with a console
        /// error. Most of the game's building meshes are unreadable, copied walls included, so a storey's walls would be
        /// missing from its bake and NPCs would path through them. For one synchronous bake each such collider gets a
        /// readable twin read back from the GPU, and the original object sits on a layer outside the bake's mask.
        /// </summary>
        private sealed class ReadableTwinsBakeHold : IDisposable
        {
            private readonly List<(GameObject go, int layer)> _moved = new();
            private readonly List<GameObject> _twins = new();
            private readonly List<Mesh> _meshes = new();

            internal ReadableTwinsBakeHold(string id, Bounds bounds)
            {
                try
                {
                    int excluded = LayerMask.NameToLayer("Ignore Raycast");
                    if (excluded < 0) throw new InvalidOperationException("layer 'Ignore Raycast' does not exist");
                    var copies = new Dictionary<IntPtr, Mesh>();
                    var names = new List<string>();
                    int kept = 0;
                    foreach (var c in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore))
                    {
                        var mc = c != null && c.enabled ? c.TryCast<MeshCollider>() : null;
                        var mesh = mc != null ? mc.sharedMesh : null;
                        if (mesh == null || mesh.isReadable) continue;
                        var go = mc.gameObject;
                        // Moving the object's layer takes every collider on it out of the bake; another one there would go missing.
                        if (go.GetComponents<Collider>().Any(o => o != null && o.Pointer != mc.Pointer && o.enabled && !o.isTrigger))
                        {
                            kept++;
                            Core.Log.Warning($"state nav: '{id}' leaves the unreadable collider '{World.MeshCarver.Path(mc.transform)}' to the builder, which skips it: its object holds more colliders");
                            continue;
                        }
                        if (!copies.TryGetValue(mesh.Pointer, out var copy))
                        {
                            try { copy = World.MeshCarver.ReadableCopy(mesh, mesh.name + " (MapExpansions nav bake)"); }
                            catch (Exception e)
                            {
                                Core.Log.Warning($"state nav: '{id}' could not read '{mesh.name}' back from the GPU; the builder skips '{World.MeshCarver.Path(mc.transform)}': {e.Message}");
                                copy = null;
                            }
                            copies[mesh.Pointer] = copy;
                            if (copy != null) _meshes.Add(copy);
                        }
                        if (copy == null)
                        {
                            kept++;
                            continue;
                        }
                        var twin = new GameObject($"MapExpansions nav twin {go.name}") { layer = go.layer };
                        twin.transform.SetParent(go.transform, false);
                        var tc = twin.AddComponent<MeshCollider>();
                        tc.convex = mc.convex;
                        tc.sharedMesh = copy;
                        _twins.Add(twin);
                        _moved.Add((go, go.layer));
                        go.layer = excluded;
                        names.Add(go.name);
                    }
                    if (_twins.Count > 0 || kept > 0)
                        Core.Log.Msg($"state nav: '{id}' bakes {_twins.Count} unreadable mesh colliders through readable twins ({_meshes.Count} meshes read back), {kept} stay unreadable: " +
                                     string.Join(", ", names.GroupBy(x => x).Select(x => x.Count() > 1 ? $"{x.Key} x{x.Count()}" : x.Key)));
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
                foreach (var t in _twins)
                    if (t != null) Object.DestroyImmediate(t);
                _twins.Clear();
                foreach (var m in _meshes)
                    if (m != null) Object.Destroy(m);
                _meshes.Clear();
            }
        }

        /// <summary>
        /// The main world switches its buildings' colliders off while the local player is more than 30 m away
        /// (OptimizedColliderGroup). A day change runs where the player sleeps, often at another property, and a bake
        /// then finds no floor under a copied storey. For one synchronous bake the groups' switched-off colliders in the
        /// volume stand on; afterwards they follow their group again.
        /// </summary>
        private sealed class ColliderGroupsAwakeHold : IDisposable
        {
            private readonly List<Collider> _woken = new();

            internal ColliderGroupsAwakeHold(string id, Bounds bounds)
            {
                var reach = bounds;
                reach.Expand(1f);
                int groups = 0;
                foreach (var g in Object.FindObjectsOfType<OptimizedColliderGroup>(true))
                {
                    var cols = g?.Colliders;
                    if (cols == null || g.collidersEnabled) continue;
                    int before = _woken.Count;
                    for (int i = 0; i < cols.Length; i++)
                    {
                        var c = cols[i];
                        if (c == null || c.enabled || !c.gameObject.activeInHierarchy || !reach.Contains(c.transform.position)) continue;
                        c.enabled = true;
                        _woken.Add(c);
                    }
                    if (_woken.Count > before) groups++;
                }
                Core.Log.Msg($"state nav: '{id}' bake switches on {_woken.Count} distance-culled colliders of {groups} collider groups");
            }

            public void Dispose()
            {
                foreach (var c in _woken)
                    if (c != null) c.enabled = false;
                _woken.Clear();
            }
        }

        /// <summary>
        /// Marks floor the main world already walks on as not walkable for one state bake, so the state's mesh leaves it
        /// to the world's mesh. The volumes reach half a metre below each box so the floor surface lies inside them.
        /// </summary>
        private sealed class ExistingFloorHold : IDisposable
        {
            private readonly List<GameObject> _volumes = new();

            internal ExistingFloorHold(string id, IReadOnlyList<Bounds> floors, Transform parent)
            {
                try
                {
                    int notWalkable = NavMesh.GetAreaFromName("Not Walkable");
                    if (notWalkable < 0) throw new InvalidOperationException("the NavMesh area 'Not Walkable' does not exist");
                    for (int i = 0; i < floors.Count; i++)
                    {
                        var f = floors[i];
                        var go = new GameObject($"MapExpansions.{id}.ExistingFloor.{i}") { layer = 0 };
                        _volumes.Add(go);
                        go.transform.SetParent(parent, true);
                        go.transform.SetPositionAndRotation(f.center + Vector3.down * 0.25f, Quaternion.identity);
                        go.AddComponent(Il2CppType.Of<NavMeshModifierVolume>());
                        var volume = go.GetComponent<NavMeshModifierVolume>();
                        if (volume == null) throw new InvalidOperationException($"state '{id}' could not add a NavMeshModifierVolume");
                        volume.center = Vector3.zero;
                        volume.size = f.size + Vector3.up * 0.5f;
                        volume.area = notWalkable;
                    }
                    if (floors.Count > 0) Core.Log.Msg($"state nav: '{id}' leaves {floors.Count} existing floor box(es) to the world's NavMesh");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                foreach (var go in _volumes)
                    if (go != null) { go.SetActive(false); UnityEngine.Object.Destroy(go); }
                _volumes.Clear();
            }
        }

        private const float StitchStep = 0.75f;
        private const float StitchReach = 0.6f;

        /// <summary>Points just inside and just outside each edge of an existing floor box, one pair every <see cref="StitchStep"/>.</summary>
        private static IEnumerable<(Vector3 inner, Vector3 outer)> EdgePairs(Bounds f)
        {
            float y = f.min.y;
            foreach (var (from, to, outward) in new[]
            {
                (new Vector2(f.min.x, f.min.z), new Vector2(f.max.x, f.min.z), Vector2.down),
                (new Vector2(f.min.x, f.max.z), new Vector2(f.max.x, f.max.z), Vector2.up),
                (new Vector2(f.min.x, f.min.z), new Vector2(f.min.x, f.max.z), Vector2.left),
                (new Vector2(f.max.x, f.min.z), new Vector2(f.max.x, f.max.z), Vector2.right),
            })
            {
                float length = Vector2.Distance(from, to);
                int count = Mathf.Max(1, Mathf.FloorToInt(length / StitchStep));
                for (int i = 0; i < count; i++)
                {
                    var p = Vector2.Lerp(from, to, (i + 0.5f) / count);
                    var a = p - outward * StitchReach;
                    var b = p + outward * StitchReach;
                    yield return (new Vector3(a.x, y, a.y), new Vector3(b.x, y, b.y));
                }
            }
        }

        private static bool InsideXZ(Bounds b, Vector3 p) => p.x >= b.min.x && p.x <= b.max.x && p.z >= b.min.z && p.z <= b.max.z;

        /// <summary>
        /// True when the NavMesh already runs from a to b about as straight as the seam would. A detour through the next seam
        /// along the edge is longer and still gets its own. NavMesh.Raycast with a query filter throws ObjectCollectedException
        /// under IL2CPP interop, so this uses CalculatePath.
        /// </summary>
        private static bool AlreadyJoined(Vector3 a, Vector3 b, NavMeshQueryFilter filter)
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(a, b, filter, path) || path.status != NavMeshPathStatus.PathComplete) return false;
            var corners = path.corners;
            float length = 0f;
            for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
            return length <= Vector3.Distance(a, b) * 1.5f + 0.2f;
        }

        /// <summary>
        /// Joins one existing floor box to the state's new floor for one agent. A pair is joined only where the inner end lies
        /// on the box, the outer end on new floor inside the volume, no wall stands between them and the NavMesh does not
        /// already run straight across.
        /// </summary>
        private static (int joined, int open) Stitch(string id, Bounds volume, IReadOnlyList<Bounds> floors, Bounds f, int agent, StateBake result)
        {
            // The interop filter wraps a boxed IL2CPP value the native GC may take between calls, so each query gets its own.
            NavMeshQueryFilter Filter() => new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
            int joined = 0, open = 0;
            foreach (var (inner, outer) in EdgePairs(f))
            {
                if (!InsideXZ(volume, outer) || floors.Any(other => InsideXZ(other, outer))) continue;
                open++;
                if (!NavMesh.SamplePosition(inner, out NavMeshHit a, StitchReach, Filter()) || Mathf.Abs(a.position.y - inner.y) > 0.5f || !InsideXZ(f, a.position)) continue;
                if (!NavMesh.SamplePosition(outer, out NavMeshHit b, StitchReach, Filter()) || Mathf.Abs(b.position.y - outer.y) > 0.5f || InsideXZ(f, b.position)) continue;
                if (Physics.Linecast(a.position + Vector3.up * 0.4f, b.position + Vector3.up * 0.4f, Mask, QueryTriggerInteraction.Ignore) ||
                    Physics.Linecast(a.position + Vector3.up * 1.2f, b.position + Vector3.up * 1.2f, Mask, QueryTriggerInteraction.Ignore)) continue;
                // A floor the bake already runs onto needs no seam there, and it is joined all the same.
                joined++;
                if (AlreadyJoined(a.position, b.position, Filter())) continue;
                result.Links.Add(Source.NavLinks.AddOwned(a.position, b.position, 0.7f, new[] { agent }));
            }
            return (joined, open);
        }

        internal static StateBake BakeState(string id, Bounds bounds, IReadOnlyList<Vector3> probes, Transform parent, IReadOnlyList<string> requiredAgents,
            IReadOnlyList<Bounds> existingFloor)
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
                using var awake = new ColliderGroupsAwakeHold(id, bounds);
                using var placedItems = new PlacedItemsBakeHold(id, bounds);
                using var twins = new ReadableTwinsBakeHold(id, bounds);
                using var existing = new ExistingFloorHold(id, existingFloor, parent);
#if DEBUG
                if (Debug.NavTest.BakeWatch is Vector3 watch)
                {
                    var r = Debug.NavTest.BakeWatchRadius;
                    Core.Log.Msg($"[dev] mxnav watch: '{id}' bake sees at {watch} within {r} m: {Obstacles(watch, watch, parent, r)}");
                }
#endif
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
                existing.Dispose();
                twins.Dispose();
                // Each existing floor box meets the new floor along its open edges; one seam per step keeps the join when a
                // placed item later carves some of them away.
                for (int i = 0; i < existingFloor.Count; i++)
                    foreach (int agent in agents)
                    {
                        var (joined, open) = Stitch(id, bounds, existingFloor, existingFloor[i], agent, result);
                        string line = $"state nav: '{id}' existing floor {i} {existingFloor[i]} joined to the new floor at {joined} of {open} edge points for agent '{NpcNavigation.AgentName(agent)}' ({agent})";
                        if (joined > 0) Core.Log.Msg(line);
                        else if (open == 0) Core.Log.Msg(line + "; it has no open edge inside the volume and keeps only the world's walking map");
                        else
                        {
                            Core.Log.Warning(line + "; NPCs on that floor cannot reach the new rooms through it");
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
                            var corners = path.corners;
                            string stop = corners == null || corners.Length == 0 ? "no path at all"
                                : $"the path stops at {corners[corners.Length - 1]}, bake colliders there: {Obstacles(corners[corners.Length - 1], corners[corners.Length - 1], parent, 0.8f)}";
                            Core.Log.Warning($"state nav: '{id}' has no complete path to probe {i} {probes[i]} for agent {agent}; {stop}; obstacles on the line from the doorway: {Obstacles(probes[0], probes[i], parent)}");
                            blocked++;
                        }
                    }
                }
                if (blocked == 0) Core.Log.Msg($"state nav: '{id}' baked {agents.Length} agent types; {probes.Count} probes connected");
                else Core.Log.Warning($"state nav: '{id}' baked {agents.Length} agent types; {blocked} probe checks failed, the state applies and NPCs may not reach those points");
                ForgetSamples($"state '{id}' baked its navigation");
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
        private static string Obstacles(Vector3 from, Vector3 to, Transform expansion, float radius = 0.4f)
        {
            try
            {
                var lift = Vector3.up * 0.5f;
                var names = new List<string>();
                foreach (var c in Physics.OverlapCapsule(from + lift, to + lift, radius, Mask, QueryTriggerInteraction.Ignore))
                {
                    if (c == null) continue;
                    var t = c.transform;
                    string path = t.name;
                    for (var p = t.parent; p != null && path.Length < 120; p = p.parent) path = p.name + "/" + path;
                    if (expansion != null && t.IsChildOf(expansion)) path = "[expansion] " + path;
                    names.Add(path + " " + c.bounds.min.ToString("F2") + ".." + c.bounds.max.ToString("F2"));
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
            ForgetSamples($"property '{code}' baked its navigation");
        }
    }
}
