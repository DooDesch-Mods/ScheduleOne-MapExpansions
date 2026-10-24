using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using UnityEngine;
using UnityEngine.AI;

namespace MapExpansions.Source
{
    /// <summary>
    /// Code links that join an expansion's navigation data to the main world, and the walk across them.
    /// </summary>
    /// <remarks>
    /// Separate NavMesh data never stitches together, so an expansion needs links at its seams. A link added with
    /// <c>NavMesh.AddLink</c> has no OffMeshLink component behind it, and <c>NPCMovement.Update</c> treats every
    /// off-mesh link as a ladder: it calls <c>offMeshLink.GetComponent&lt;Ladder&gt;()</c> on a null link and throws
    /// every frame. The prefix walks an NPC across one of these links instead and completes it. Links of other mods
    /// and ladders run vanilla.
    /// </remarks>
    internal static class NavLinks
    {
        private const float FallbackSpeed = 1.5f;
        private const float SnapRadius = 1.5f;

        private static readonly List<NavMeshLinkInstance> Instances = new();
        private static readonly List<(Vector3 a, Vector3 b, float width)> Ends = new();
        private static int _crossings;

        public static int Count => Ends.Count;

        public static void Add(Vector3 a, Vector3 b, float width, IEnumerable<int> agentTypes)
        {
            int added = 0;
            var unsnapped = new List<string>();
            foreach (int agent in agentTypes)
            {
                // A link only joins polygons its ends touch; route points can sit a little above or below the mesh.
                var filter = new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
                Vector3 Snap(Vector3 p, string end)
                {
                    if (NavMesh.SamplePosition(p, out NavMeshHit hit, SnapRadius, filter)) return hit.position;
                    unsnapped.Add($"{end} for {NavMesh.GetSettingsNameFromID(agent)}");
                    return p;
                }
                var data = new NavMeshLinkData
                {
                    startPosition = Snap(a, "start"),
                    endPosition = Snap(b, "end"),
                    width = width,
                    costModifier = -1f,
                    bidirectional = true,
                    area = 0,
                    agentTypeID = agent,
                };
                var inst = NavMesh.AddLink(data);
                if (!inst.valid)
                {
                    Core.Log.Error($"nav link {a} -> {b}: AddLink failed for agent {NavMesh.GetSettingsNameFromID(agent)} ({agent})");
                    continue;
                }
                Instances.Add(inst);
                added++;
            }
            Ends.Add((a, b, width));
            Core.Log.Msg($"nav link {a} -> {b} width {width}: added for {added} agent types");
            if (unsnapped.Count > 0)
                Core.Log.Warning($"nav link {a} -> {b}: no NavMesh within {SnapRadius} m of the {string.Join(", ", unsnapped)}; that end may not connect");
        }

        public static void Clear()
        {
            foreach (var inst in Instances) inst.Remove();
            Core.Log.Msg($"nav links: removed {Instances.Count} link instances ({Ends.Count} seams)");
            Instances.Clear();
            Ends.Clear();
        }

        /// <summary>True when the link an agent stands on starts and ends at one of our seams, in either direction.</summary>
        private static bool IsOurs(Vector3 start, Vector3 end)
        {
            foreach (var (a, b, width) in Ends)
            {
                // A wide link reports the ends where the agent crosses, anywhere across its width.
                float tol = width * 0.5f + 1f;
                if ((Near(start, a, tol) && Near(end, b, tol)) || (Near(start, b, tol) && Near(end, a, tol))) return true;
            }
            return false;
        }

        private static bool Near(Vector3 p, Vector3 q, float tol)
        {
            var d = p - q;
            d.y = 0f;
            return d.magnitude < tol;
        }

        [HarmonyPatch(typeof(NPCMovement), "Update")]
        private static class CrossPatch
        {
            private static bool Prefix(NPCMovement __instance, bool __runOriginal)
            {
                if (!__runOriginal || Ends.Count == 0) return true;
                try
                {
                    NavMeshAgent agent = __instance._agent;
                    if (agent == null || !agent.enabled || !agent.isOnOffMeshLink) return true;
                    OffMeshLinkData link = agent.currentOffMeshLinkData;
                    if (link.offMeshLink != null) return true;
                    if (!IsOurs(link.startPos, link.endPos)) return true;

                    Transform t = agent.transform;
                    Vector3 end = link.endPos + Vector3.up * agent.baseOffset;
                    float speed = agent.speed > 0.1f ? agent.speed : FallbackSpeed;
                    t.position = Vector3.MoveTowards(t.position, end, speed * Time.deltaTime);
                    var flat = new Vector3(end.x - t.position.x, 0f, end.z - t.position.z);
                    if (flat.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(flat);
                    if ((t.position - end).sqrMagnitude < 0.0025f)
                    {
                        agent.CompleteOffMeshLink();
                        _crossings++;
                        Core.Log.Msg($"nav link: {__instance.gameObject.name} crossed a seam ({_crossings} crossings)");
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Core.Log.Warning($"nav link: could not walk {__instance.gameObject.name} across a seam, vanilla handles it as a ladder: {e.Message}");
                    return true;
                }
            }
        }
    }
}
