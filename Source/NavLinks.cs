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

        // Crossings per NPC. A worker crosses the same seams all day on its way between stations, so the count alone
        // says nothing. An NPC that walks back over the seam it just crossed while it still heads for the same spot
        // is pacing; that is warned once per NPC and destination.
        private const int PacingReversals = 4;
        private const float SameSpot = 1f;
        // A link is as wide as its seam, so the way back over it starts and ends up to its width away from the way there.
        private const float SameEnd = 1f;
        private sealed class Tally
        {
            internal int Total, Reversals;
            internal Vector3 LastFrom, LastTo, LastDestination;
            internal readonly List<Vector3> WarnedDestinations = new();
        }
        private static readonly Dictionary<string, Tally> Tallies = new();

        private static void Record(string npc, Vector3 from, Vector3 to, Vector3 destination)
        {
            _crossings++;
            if (!Tallies.TryGetValue(npc, out var tally))
            {
                Tallies[npc] = tally = new Tally { LastFrom = from, LastTo = to, LastDestination = destination, Total = 1 };
                Core.Log.Msg($"nav link: {npc} crossed a seam for the first time ({from} -> {to}); further crossings are counted, not logged");
                return;
            }
            tally.Total++;
            bool back = (from - tally.LastTo).sqrMagnitude < SameEnd * SameEnd && (to - tally.LastFrom).sqrMagnitude < SameEnd * SameEnd;
            bool sameGoal = (destination - tally.LastDestination).sqrMagnitude < SameSpot * SameSpot;
            tally.Reversals = back && sameGoal ? tally.Reversals + 1 : 0;
            tally.LastFrom = from;
            tally.LastTo = to;
            tally.LastDestination = destination;
            if (tally.Reversals < PacingReversals) return;
            foreach (var warned in tally.WarnedDestinations)
                if ((warned - destination).sqrMagnitude < SameSpot * SameSpot) return;
            tally.WarnedDestinations.Add(destination);
            Core.Log.Warning($"nav link: {npc} crossed the seam {from} -> {to} back and forth {tally.Reversals + 1} times in a row toward {destination} " +
                             $"({tally.Total} crossings in total); it may be stuck walking back and forth");
        }

#if DEBUG
        /// <summary>
        /// Feeds two made-up NPCs through the pacing warning: a worker walking back and forth over one seam between a
        /// pot and a table (the crossings of a Sweatshop Botanist), and an NPC crossing the same seam back and forth
        /// toward one spot. Only the second must warn, and only once.
        /// </summary>
        internal static void PacingSelfTest()
        {
            const string worker = "mxpacing worker", pacer = "mxpacing pacer";
            var there = (from: new Vector3(-58.55f, -.25f, 140.5f), to: new Vector3(-57.92f, -.2f, 141.7f));
            var back = (from: new Vector3(-57.92f, -.2f, 141.7f), to: new Vector3(-58.18f, -.25f, 140.5f));
            var pot = new Vector3(-57.55f, -.2f, 141.98f);
            var table = new Vector3(-59.75f, -.24f, 139.92f);
            int crossings = _crossings;
            for (int i = 0; i < 20; i++)
            {
                var leg = i % 2 == 0 ? there : back;
                Record(worker, leg.from, leg.to, i % 2 == 0 ? pot : table);
                Record(pacer, leg.from, leg.to, pot);
            }
            int workerWarned = Tallies[worker].WarnedDestinations.Count, pacerWarned = Tallies[pacer].WarnedDestinations.Count;
            Tallies.Remove(worker);
            Tallies.Remove(pacer);
            _crossings = crossings;
            Core.Log.Msg($"[dev] mxpacing: 20 crossings each; worker warned {workerWarned} times, pacer warned {pacerWarned} times");
        }
#endif

        /// <summary>Seam crossings so far, and how many NPCs made them.</summary>
        internal static string Describe() => $"{_crossings} seam crossings by {Tallies.Count} NPCs";

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

        internal sealed class Lease : IDisposable
        {
            internal readonly List<NavMeshLinkInstance> Links = new();
            internal (Vector3 a, Vector3 b, float width) End;
            public void Dispose()
            {
                foreach (var link in Links) { link.Remove(); Instances.Remove(link); }
                Links.Clear();
                Ends.Remove(End);
            }
        }

        internal static Lease AddOwned(Vector3 a, Vector3 b, float width, IEnumerable<int> agents)
        {
            var lease = new Lease { End = (a, b, width) };
            try
            {
                foreach (int agent in agents)
                {
                    var filter = new NavMeshQueryFilter { agentTypeID = agent, areaMask = NavMesh.AllAreas };
                    if (!NavMesh.SamplePosition(a, out NavMeshHit start, 0.6f, filter) ||
                        !NavMesh.SamplePosition(b, out NavMeshHit end, 0.6f, filter))
                        throw new InvalidOperationException($"state nav seam {a} -> {b}: missing endpoint for agent {agent}");
                    var link = NavMesh.AddLink(new NavMeshLinkData { startPosition = start.position, endPosition = end.position,
                        width = width, costModifier = -1f, bidirectional = true, area = 0, agentTypeID = agent });
                    if (!link.valid) throw new InvalidOperationException($"state nav seam {a} -> {b}: AddLink failed for agent {agent}");
                    lease.Links.Add(link);
                    Instances.Add(link);
                }
                Ends.Add(lease.End);
                return lease;
            }
            catch (Exception original)
            {
                try { lease.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("state seam and cleanup failed", original, cleanup); }
                throw;
            }
        }

        public static void Clear()
        {
            foreach (var inst in Instances) inst.Remove();
            Core.Log.Msg($"nav links: removed {Instances.Count} link instances ({Ends.Count} seams), {Describe()}");
            Instances.Clear();
            Ends.Clear();
            Tallies.Clear();
            _crossings = 0;
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
                        Record(__instance.gameObject.name, link.startPos, link.endPos, agent.destination);
#if DEBUG
                        Core.Log.Msg($"nav link: {__instance.gameObject.name} crossed a seam {link.startPos} -> {link.endPos} toward {agent.destination}, path {agent.pathStatus}, {agent.path?.corners?.Length ?? 0} corners ({_crossings} crossings)");
#endif
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
