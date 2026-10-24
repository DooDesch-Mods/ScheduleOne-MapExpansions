using System.Collections;
using Il2CppInterop.Runtime;
using MapExpansions.Lifecycle;
using MapExpansions.Source;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace MapExpansions.Navigation
{
    /// <summary>
    /// NPC navigation for one placed expansion: its own NavMesh data turned and moved with it, and baked pieces
    /// along each connection where neither world has a NavMesh. Separate NavMesh data never stitches, so code links
    /// join the pieces to each other and to both worlds.
    /// </summary>
    internal sealed class NpcNavigation
    {
        private const float PieceLength = 20f;
        private const float LinkReach = 2.5f;

        private readonly List<NavMeshDataInstance> _data = new();
        private readonly List<GameObject> _surfaces = new();

        public int Pieces => _surfaces.Count;

        public static IEnumerable<int> AgentTypes()
        {
            int n = NavMesh.GetSettingsCount();
            for (int i = 0; i < n; i++) yield return NavMesh.GetSettingsByIndex(i).agentTypeID;
        }

        public static string AgentName(int id) => NavMesh.GetSettingsNameFromID(id);

        /// <summary>Adds the source's NavMesh data. Must run before the connections are baked, so the coverage test sees it.</summary>
        public void AddSourceData(BuiltExpansion b)
        {
            int added = 0;
            foreach (var n in b.Source.NavMeshes)
            {
                if (n.Data == null)
                {
                    Core.Log.Error($"npc nav: '{b.Id}' NavMesh data '{n.Name}' was unloaded");
                    continue;
                }
                var rot = b.Frame.Rotation;
                var inst = NavMesh.AddNavMeshData(n.Data, rot * n.Position + b.Frame.Offset, rot * n.Rotation);
                if (!inst.valid)
                {
                    Core.Log.Error($"npc nav: '{b.Id}' AddNavMeshData failed for '{n.Name}' ({AgentName(n.AgentTypeId)})");
                    continue;
                }
                _data.Add(inst);
                added++;
            }
            string line = $"npc nav: '{b.Id}' {added}/{b.Source.NavMeshes.Count} NavMesh data added";
            if (added < b.Source.NavMeshes.Count || added == 0) Core.Log.Warning(line);
            else Core.Log.Msg(line);
        }

        /// <summary>True when the agent type has a NavMesh within reach of the point, at the point's height.</summary>
        private static bool Covered(Vector3 p, int agent)
        {
            var filter = new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
            return NavMesh.SamplePosition(p, out NavMeshHit hit, 0.6f, filter) && Mathf.Abs(hit.position.y - p.y) < 0.5f;
        }

        /// <summary>Bakes and links one connection, a piece per frame so the load does not stall in one block.</summary>
        public IEnumerator BakeConnection(BuiltExpansion b, BuiltConnection conn)
        {
            var curve = conn.Curve;
            float length = curve.Length;
            var agents = AgentTypes().Where(a => Covered(curve.Points[0], a) || Covered(curve.Points[curve.Count - 1], a)).ToList();
            if (agents.Count == 0)
            {
                Core.Log.Error($"npc nav: '{b.Id}' {conn.Name}: no agent type has a NavMesh at either end of the route; NPCs cannot cross");
                yield break;
            }
            int probe = agents.Contains(0) ? 0 : agents[0];

            // The gap: the first metre from the main-world end and the last from the expansion end without a NavMesh.
            float dStart = length, dEnd = 0f;
            for (float d = 0f; d <= length; d += 1f)
                if (!Covered(curve.PointAt(d), probe)) { dStart = d; break; }
            for (float d = length; d >= 0f; d -= 1f)
                if (!Covered(curve.PointAt(d), probe)) { dEnd = d; break; }
            if (dStart >= dEnd)
            {
                Core.Log.Warning($"npc nav: '{b.Id}' {conn.Name}: the route is covered by a NavMesh over its whole length ({length:0} m); " +
                                 "no pieces baked. Separate NavMesh data does not join by itself, so NPCs may still stop at a seam.");
                yield break;
            }
            float from = Mathf.Max(0f, dStart - 1f), to = Mathf.Min(length, dEnd + 1f);
            Core.Log.Msg($"npc nav: '{b.Id}' {conn.Name}: no NavMesh from {dStart:0} to {dEnd:0} m of {length:0} m; baking {from:0}..{to:0} m " +
                         $"for {string.Join(", ", agents.Select(AgentName))}");

            float half = conn.HalfInnerWidth + 0.5f;
            int pieces = Mathf.Max(1, Mathf.CeilToInt((to - from) / PieceLength));
            float step = (to - from) / pieces;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < pieces; i++)
            {
                float a = from + i * step, c = a + step;
                foreach (int agent in agents) Bake(b, conn, a, c, half, agent, i);
                yield return null;
            }
            long bakeMs = sw.ElapsedMilliseconds;

            float width = Mathf.Max(2f, conn.Definition.RoadWidth);
            // Links overlap each seam by LinkReach on both sides, so an agent always finds a polygon at both ends.
            NavLinks.Add(Ground(curve.PointAt(Mathf.Max(0f, from - 1f))), Ground(curve.PointAt(from + LinkReach + 0.5f)), width, agents);
            for (int i = 1; i < pieces; i++)
            {
                float d = from + i * step;
                NavLinks.Add(Ground(curve.PointAt(d - LinkReach)), Ground(curve.PointAt(d + LinkReach)), width, agents);
            }
            NavLinks.Add(Ground(curve.PointAt(to - LinkReach - 0.5f)), Ground(curve.PointAt(Mathf.Min(length, to + 1f))), width, agents);
            Core.Log.Msg($"npc nav: '{b.Id}' {conn.Name}: {pieces} pieces x {agents.Count} agent types baked in {bakeMs} ms, {pieces + 1} seams linked");

            var path = new NavMeshPath();
            var filter = new NavMeshQueryFilter { agentTypeID = probe, areaMask = NavMesh.AllAreas };
            Vector3 s0 = curve.PointAt(Mathf.Max(0f, from - 6f)), s1 = curve.PointAt(Mathf.Min(length, to + 6f));
            bool ok = NavMesh.CalculatePath(s0, s1, filter, path);
            var corners = path.corners;
            string end = corners.Length > 0 ? corners[corners.Length - 1].ToString() : "-";
            string check = $"npc nav: '{b.Id}' {conn.Name}: path check {s0} -> {s1} for {AgentName(probe)}: {path.status}, {corners.Length} corners, ends at {end}";
            if (ok && path.status == NavMeshPathStatus.PathComplete) Core.Log.Msg(check);
            else Core.Log.Error(check + "; NPCs will not find a complete path through the connection");
        }

        /// <summary>The route's centre line lies on the deck; links need it a little above to snap onto the polygons.</summary>
        private static Vector3 Ground(Vector3 p) => p + Vector3.up * 0.05f;

        private void Bake(BuiltExpansion b, BuiltConnection conn, float from, float to, float half, int agent, int index)
        {
            var curve = conn.Curve;
            int i0 = curve.IndexAt(from), i1 = curve.IndexAt(to);
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = i0; i <= i1; i++)
            {
                minY = Mathf.Min(minY, curve.Points[i].y);
                maxY = Mathf.Max(maxY, curve.Points[i].y);
            }
            Vector3 a = curve.Points[i0], c = curve.Points[i1];
            var flat = new Vector3(c.x - a.x, 0f, c.z - a.z);
            var rot = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat) : Quaternion.identity;
            // Up to head height over the highest point, but below the vault, whose top would bake as a floor.
            float bottom = minY - 1f, top = maxY + 2.5f;
            var center = new Vector3((a.x + c.x) * 0.5f, (bottom + top) * 0.5f, (a.z + c.z) * 0.5f);

            // The piece must also hold the curve between its ends: widen it by the curve's largest sideways offset.
            float side = 0f;
            for (int i = i0; i <= i1; i++)
                side = Mathf.Max(side, Mathf.Abs(Vector3.Dot(curve.Points[i] - center, rot * Vector3.right)));

            var go = new GameObject($"MapExpansions.{b.Id}.{conn.Name}.Nav{index}.{AgentName(agent)}");
            go.transform.SetParent(conn.Root.transform, false);
            go.transform.SetPositionAndRotation(center, rot);
            go.AddComponent(Il2CppType.Of<NavMeshSurface>());
            var s = go.GetComponent<NavMeshSurface>();
            s.agentTypeID = agent;
            s.collectObjects = CollectObjects.Volume;
            s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            s.center = Vector3.zero;
            s.size = new Vector3((half + side) * 2f, top - bottom, flat.magnitude + 1f);
            try
            {
                s.BuildNavMesh();
            }
            catch (Exception e)
            {
                Core.Log.Error($"npc nav: '{b.Id}' {conn.Name} piece {index} ({AgentName(agent)}) failed to bake: {e.Message}");
                Object.Destroy(go);
                return;
            }
            if (s.navMeshData == null)
            {
                Core.Log.Error($"npc nav: '{b.Id}' {conn.Name} piece {index} ({AgentName(agent)}) baked no data");
                Object.Destroy(go);
                return;
            }
            _surfaces.Add(go);
        }

        public void Teardown(string id)
        {
            foreach (var inst in _data) inst.Remove();
            foreach (var go in _surfaces)
                if (go != null) Object.Destroy(go);
            Core.Log.Msg($"npc nav: '{id}' removed {_data.Count} NavMesh data and {_surfaces.Count} baked pieces");
            _data.Clear();
            _surfaces.Clear();
        }
    }
}
