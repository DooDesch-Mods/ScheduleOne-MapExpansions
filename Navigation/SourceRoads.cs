using UnityEngine;

namespace MapExpansions.Navigation
{
    /// <summary>
    /// The expansion's own roads as lanes for the "Road Nodes" graph. A source's road nodes can lie a hundred metres
    /// apart with links that cut straight across bends and dunes, and a two-way link has no side to keep to: vehicles
    /// on it leave the road, drive down its middle and float over hollows, and road drawings built from the links run
    /// across the sand. The expansion's road pieces add the streets and junctions the source has no nodes for
    /// (<see cref="Streets"/>). Each run of links between junctions is traced along the road surface under it, smoothed,
    /// and gets a lane each way, traffic on the right. Lanes stop at a junction's mouth; there every lane that arrives
    /// turns into every lane that leaves on another run. A dead end turns back.
    /// </summary>
    internal sealed class SourceRoads
    {
        /// <summary>Distance between trace samples along the road.</summary>
        private const float TraceStep = 4f;

        /// <summary>
        /// A lane node every this many metres, or after <see cref="MinLink"/> where the road bends. Road drawings take a
        /// link under 8 m for a turn inside a junction, so no link along a road is shorter.
        /// </summary>
        private const float NodeSpacing = 10f;
        private const float MinLink = 8.5f;
        private const float NodeBend = 12f;

        /// <summary>How far to each side of the trace the road surface is searched, and in which steps.</summary>
        private const float ScanHalf = 14f;
        private const float ScanStep = 0.5f;

        /// <summary>The centre line is averaged over this many samples to each side (8 m), this many times.</summary>
        private const int SmoothReach = 2, SmoothPasses = 3;

        /// <summary>
        /// Lanes end this far from a junction's middle. The turns across it stay under the 8 m that road drawings read
        /// as a junction, also straight across a 10 m crossing.
        /// </summary>
        private const float JunctionSetback = 3.5f;

        /// <summary>
        /// A street end is walked along the road surface beyond it for at most this far, with a node this often. A
        /// walk ends where the surface ends, widens into a forecourt or junction, turns sharper than
        /// <see cref="WalkTurn"/> in one step, or comes within <see cref="WalkMeets"/> of another road's node.
        /// </summary>
        private const float MaxWalk = 400f, WalkNodeSpacing = 40f, WalkTurn = 45f, WalkMeets = 8f;

        private const float LaneWidth = 3.5f;

        /// <summary>
        /// A cross-section this much wider than the road so far runs into a junction or a forecourt; its middle is not the
        /// road's, and the trace goes straight on instead.
        /// </summary>
        private const float WideSection = 1.4f;

        /// <summary>Distance of each lane from the middle of a two-lane road, as the main world's lanes lie 3 m apart.</summary>
        private const float TwoLaneOffset = 1.5f;
        private const float MinRoadWidth = 5f, MaxRoadWidth = 14f, FallbackRoadWidth = 7f;
        private const float RoadNodeHeight = 0.05f;

        public readonly List<Vector3> Points = new();
        public readonly List<(int From, int To)> Edges = new();

        /// <summary>Per source node, the lane ends that arrive there and the lane starts that leave, with their run.</summary>
        public List<(int Point, int Run)>[] Arrive, Leave;

        public int Runs, Traced, Fallbacks;

        private readonly string _id;
        private readonly int _mask = LayerMask.GetMask("Default", "Terrain");
        private readonly HashSet<int> _roadColliders = new();
        private readonly Dictionary<int, bool> _isRoad = new();

        /// <summary>Street nodes: their place comes from a road piece, not from a trace.</summary>
        private readonly HashSet<int> _fixed;

        /// <summary>Source nodes that lie on a street; the street's own nodes stand in for them.</summary>
        private readonly HashSet<int> _replaced;

        private int[] _degree;

        private SourceRoads(string id, HashSet<int> streetNodes, HashSet<int> replaced)
        {
            _id = id;
            _fixed = streetNodes;
            _replaced = replaced;
        }

        /// <param name="nodes">The source's road nodes in world space.</param>
        /// <param name="links">The source's road links between them.</param>
        /// <param name="joints">Nodes where a connection joins; a run ends there.</param>
        /// <param name="content">The placed expansion; its road pieces become streets with their junctions.</param>
        /// <remarks><see cref="Arrive"/> and <see cref="Leave"/> start with the source's nodes in their order; street nodes follow.</remarks>
        public static SourceRoads Build(string id, IReadOnlyList<Vector3> nodes, IReadOnlyList<(int From, int To, bool OneWay)> links, ISet<int> joints,
                                        Transform content)
        {
            var all = new List<Vector3>(nodes);
            var allLinks = new List<(int From, int To, bool OneWay)>(links);
            var streetNodes = new HashSet<int>();
            var replaced = new HashSet<int>();
            Streets.Add(id, content, all, allLinks, joints, streetNodes, replaced);
            var r = new SourceRoads(id, streetNodes, replaced);
            r.ExtendStreets(all, allLinks, joints);
            r.Make(all, allLinks, joints);
            return r;
        }

        private void Make(IReadOnlyList<Vector3> nodes, IReadOnlyList<(int From, int To, bool OneWay)> raw, ISet<int> joints)
        {
            Arrive = new List<(int, int)>[nodes.Count];
            Leave = new List<(int, int)>[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) { Arrive[i] = new(); Leave[i] = new(); }

            // One entry per pair: a link and its way back are one two-way road.
            var links = new List<(int From, int To, bool OneWay)>();
            foreach (var l in raw)
            {
                if (l.From == l.To || l.From < 0 || l.To < 0 || l.From >= nodes.Count || l.To >= nodes.Count) continue;
                int back = links.FindIndex(x => x.From == l.To && x.To == l.From);
                if (back >= 0) { links[back] = (links[back].From, links[back].To, false); continue; }
                if (links.Exists(x => x.From == l.From && x.To == l.To)) continue;
                links.Add(l);
            }

            foreach (var p in nodes)
                if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out var hit, 4f, _mask, QueryTriggerInteraction.Ignore)
                    && hit.collider.gameObject.layer != LayerMask.NameToLayer("Terrain"))
                    _roadColliders.Add(hit.collider.GetInstanceID());

            var at = new List<int>[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) at[i] = new();
            for (int i = 0; i < links.Count; i++) { at[links[i].From].Add(i); at[links[i].To].Add(i); }
            _degree = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) _degree[i] = at[i].Count;

            bool Through(int n) => !joints.Contains(n) && at[n].Count == 2 && !links[at[n][0]].OneWay && !links[at[n][1]].OneWay;

            var used = new bool[links.Count];
            for (int i = 0; i < links.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var seq = new List<int> { links[i].From, links[i].To };
                if (!links[i].OneWay)
                {
                    Extend(seq, at, links, used, Through, forward: true);
                    Extend(seq, at, links, used, Through, forward: false);
                }
                AddRun(seq, nodes, links[i].OneWay);
            }

            for (int n = 0; n < nodes.Count; n++)
            {
                if (at[n].Count > 0 || _replaced.Contains(n)) continue;
                // A node without links is still a place a route can reach the road network.
                Points.Add(nodes[n]);
                Arrive[n].Add((Points.Count - 1, -1));
                Leave[n].Add((Points.Count - 1, -1));
            }

            // A lane turns into every lane of another run; it turns back only at a dead end. Where a connection joins, the
            // connection's lanes go on, so nothing turns back there.
            int turns = 0, uturns = 0;
            for (int n = 0; n < nodes.Count; n++)
                foreach (var (a, runA) in Arrive[n])
                {
                    bool other = Leave[n].Exists(x => x.Run != runA);
                    foreach (var (b, runB) in Leave[n])
                    {
                        if (a == b) continue;
                        if (runA == runB && (other || joints.Contains(n))) continue;
                        Edges.Add((a, b));
                        if (runA == runB) uturns++;
                        else turns++;
                    }
                }
            if (uturns > 0) Core.Log.Msg($"vehicle nav: '{_id}' {uturns} dead ends turn back");
            Core.Log.Msg($"vehicle nav: '{_id}' {nodes.Count} road nodes and {links.Count} roads make {Runs} runs, " +
                         $"{Traced} traced along the road, {Fallbacks} straight; {Points.Count} lane nodes, {turns} turns at junctions");
        }

        /// <summary>
        /// Walks on from each street end where the road surface goes on: a road the source has no nodes for, such as
        /// the lane from a town's side street to its houses. Without it, cars leave the road network at the street end
        /// and cross open ground to their destination.
        /// </summary>
        private void ExtendStreets(List<Vector3> nodes, List<(int From, int To, bool OneWay)> links, ISet<int> joints)
        {
            var degree = new int[nodes.Count];
            var neighbour = new int[nodes.Count];
            foreach (var (from, to, _) in links)
            {
                degree[from]++; degree[to]++;
                neighbour[from] = to; neighbour[to] = from;
            }
            var ends = new List<int>();
            foreach (int n in _fixed)
                if (degree[n] == 1 && !joints.Contains(n)) ends.Add(n);
            ends.Sort();
            foreach (int end in ends)
            {
                Vector3 heading = Flat(nodes[end] - nodes[neighbour[end]]);
                var walk = Walk(nodes[end], heading, out string stop);
                if (walk.Count == 0)
                {
                    Core.Log.Msg($"vehicle nav: '{_id}' street end {nodes[end]}: {stop}; it stays a dead end");
                    continue;
                }
                // Another road's node near the walk's end: the walk joins it there.
                int meets = -1;
                for (int i = 0; i < nodes.Count && meets < 0; i++)
                    if (i != end && !_replaced.Contains(i) && (i >= degree.Length || degree[i] > 0)
                        && FlatDistance(nodes[i], walk[^1]) < WalkMeets && FlatDistance(nodes[i], nodes[end]) > WalkMeets)
                        meets = i;
                int prev = end, added = 0;
                float since = 0f;
                for (int i = 0; i < walk.Count; i++)
                {
                    since += FlatDistance(i == 0 ? nodes[end] : walk[i - 1], walk[i]);
                    bool last = i == walk.Count - 1;
                    if (since < WalkNodeSpacing && !last) continue;
                    if (last && meets >= 0) break;
                    nodes.Add(walk[i]);
                    links.Add((prev, nodes.Count - 1, false));
                    prev = nodes.Count - 1;
                    since = 0f;
                    added++;
                }
                if (meets >= 0) links.Add((prev, meets, false));
                Core.Log.Msg($"vehicle nav: '{_id}' street end {nodes[end]} goes on along the road for {Length(walk):0} m to {walk[^1]} ({stop})" +
                             (meets >= 0 ? $", joining the road node {nodes[meets]}" : "") + $"; {added} nodes added");
            }
        }

        /// <summary>The road surface's middle line beyond <paramref name="start"/>, in steps of <see cref="TraceStep"/>.</summary>
        private List<Vector3> Walk(Vector3 start, Vector3 heading, out string stop)
        {
            var line = new List<Vector3>();
            var widths = new List<float>();
            Vector3 c = start;
            stop = "the road reaches the walk's length";
            for (int step = 0; step < MaxWalk / TraceStep; step++)
            {
                Vector3 guess = c + heading * TraceStep;
                if (!Across(guess, heading, out Vector3 next, out float w)) { stop = "the road surface ends"; break; }
                if (Wide(w, widths)) { stop = "the road widens into a forecourt or junction"; break; }
                Vector3 h = Flat(next - c);
                if (h.sqrMagnitude < 0.5f || Vector3.Angle(h, heading) > WalkTurn) { stop = "the surface beside it is not the road ahead"; break; }
                line.Add(next);
                widths.Add(w);
                heading = Flat(heading * 2f + h);
                c = next;
            }
            // Fewer than three steps is the edge of a piece or a kerb, not a road.
            if (line.Count < 3)
            {
                if (line.Count > 0) stop = $"only {line.Count * TraceStep:0} m of road surface follows, then {stop}";
                line.Clear();
            }
            return line;
        }

        private static void Extend(List<int> seq, List<int>[] at, List<(int From, int To, bool OneWay)> links, bool[] used, Func<int, bool> through, bool forward)
        {
            while (true)
            {
                int end = forward ? seq[^1] : seq[0];
                if (!through(end)) return;
                int next = -1;
                foreach (int l in at[end]) if (!used[l]) { next = l; break; }
                if (next < 0) return;
                used[next] = true;
                int other = links[next].From == end ? links[next].To : links[next].From;
                if (forward) seq.Add(other); else seq.Insert(0, other);
                if (other == (forward ? seq[0] : seq[^1])) return;
            }
        }

        private void AddRun(List<int> seq, IReadOnlyList<Vector3> nodes, bool oneWay)
        {
            int run = Runs++;
            var path = new List<Vector3>();
            foreach (int n in seq) path.Add(nodes[n]);
            List<Vector3> centre;
            float width;
            bool traced = Trace(seq, path, out centre, out width, out string lost);
            if (traced) Traced++;
            else
            {
                Fallbacks++;
                Core.Log.Warning($"vehicle nav: '{_id}' road run {run} (nodes {string.Join(",", seq)}) {lost}; its lanes follow the straight links");
                centre = Straight(path);
                width = FallbackRoadWidth;
            }
            if (traced) centre = Smooth(centre);
            // Lanes stop at a junction's mouth; the turns across it are links of their own.
            if (_degree[seq[0]] >= 3) centre = Trim(centre, JunctionSetback);
            if (_degree[seq[^1]] >= 3)
            {
                centre.Reverse();
                centre = Trim(centre, JunctionSetback);
                centre.Reverse();
            }
            if (traced) centre = Thin(centre);

            if (oneWay)
            {
                int first = Points.Count;
                AddLane(centre, 0f, reverse: false);
                Leave[seq[0]].Add((first, run));
                Arrive[seq[^1]].Add((Points.Count - 1, run));
                return;
            }
            float carriageway = width * 0.5f;
            int lanes = Mathf.Max(1, Mathf.RoundToInt(carriageway / LaneWidth));
            float offset = lanes == 1 ? TwoLaneOffset : carriageway - carriageway / lanes * 0.5f;

            int there = Points.Count;
            AddLane(centre, offset, reverse: false);
            Leave[seq[0]].Add((there, run));
            Arrive[seq[^1]].Add((Points.Count - 1, run));
            int back = Points.Count;
            AddLane(centre, offset, reverse: true);
            Leave[seq[^1]].Add((back, run));
            Arrive[seq[0]].Add((Points.Count - 1, run));
            Core.Log.Msg($"vehicle nav: '{_id}' road run {run}: nodes {string.Join(",", seq)}, {Length(centre):0} m, road {width:0.0} m wide, " +
                         $"lanes {offset:0.00} m off its middle, {back - there} nodes each way");
        }

        /// <summary>A chain of lane nodes beside the centre line, on the right of the direction it runs.</summary>
        private void AddLane(List<Vector3> centre, float offset, bool reverse)
        {
            int n = centre.Count;
            for (int k = 0; k < n; k++)
            {
                int i = reverse ? n - 1 - k : k;
                Vector3 ahead = centre[Mathf.Min(i + 1, n - 1)] - centre[Mathf.Max(i - 1, 0)];
                if (reverse) ahead = -ahead;
                ahead.y = 0f;
                Vector3 right = ahead.sqrMagnitude > 1e-4f ? Vector3.Cross(Vector3.up, ahead.normalized) : Vector3.zero;
                Points.Add(Ground(centre[i] + right * offset));
                if (k > 0) Edges.Add((Points.Count - 2, Points.Count - 1));
            }
        }

        /// <summary>
        /// Walks the road from node to node: a step ahead along the road's own heading, then across it to the middle
        /// of the road surface found there. Between two street nodes the road piece already gives the line, and the
        /// walk only measures the width. Fails when the road is lost or the walk stops getting closer to the node.
        /// </summary>
        private bool Trace(List<int> seq, List<Vector3> path, out List<Vector3> samples, out float width, out string lost)
        {
            samples = new List<Vector3>();
            var widths = new List<float>();
            int wide = 0;
            width = 0f;
            lost = null;
            Vector3 heading = Flat(path[1] - path[0]);
            Vector3 c;
            float w;
            if (_fixed.Contains(seq[0])) { c = Ground(path[0]); w = 0f; }
            else if (!Across(path[0], heading, out c, out w)) { lost = $"has no road surface under its first node {path[0]}"; return false; }
            samples.Add(c);
            widths.Add(w);
            for (int k = 1; k < path.Count; k++)
            {
                Vector3 target = path[k];
                if (_fixed.Contains(seq[k - 1]) && _fixed.Contains(seq[k]))
                {
                    Vector3 from = c, along = Flat(target - from);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(FlatDistance(from, target) / TraceStep));
                    for (int i = 1; i < steps; i++)
                    {
                        Vector3 p = Ground(Vector3.Lerp(from, target, (float)i / steps));
                        samples.Add(p);
                        widths.Add(Across(p, along, out _, out w) && !Wide(w, widths) ? w : 0f);
                    }
                    c = Ground(target);
                    samples.Add(c);
                    widths.Add(0f);
                    continue;
                }
                int limit = Mathf.CeilToInt(FlatDistance(c, target) * 3f / TraceStep) + 10;
                int worse = 0;
                while (FlatDistance(c, target) > TraceStep * 1.5f)
                {
                    if (--limit < 0) { lost = $"never reaches node {target}"; return false; }
                    Vector3 toTarget = Flat(target - c);
                    // The road's own heading, turned a little toward the node so the walk takes the right branch.
                    Vector3 ahead = samples.Count > 1 ? Flat(samples[^1] - samples[^2]) : toTarget;
                    ahead = Flat(ahead * 3f + toTarget);
                    Vector3 guess = c + ahead * TraceStep;
                    if (!Across(guess, ahead, out Vector3 next, out w)) { lost = $"loses the road near {guess}"; return false; }
                    if (Wide(w, widths)) { next = Ground(guess); w = 0f; wide++; }
                    worse = FlatDistance(next, target) >= FlatDistance(c, target) ? worse + 1 : 0;
                    if (worse >= 3) { lost = $"turns away from node {target} near {next}"; return false; }
                    c = next;
                    samples.Add(c);
                    widths.Add(w);
                }
                if (_fixed.Contains(seq[k])) { c = Ground(target); w = 0f; }
                else
                {
                    Vector3 h = Flat(target - samples[^1]);
                    if (!Across(target, h.sqrMagnitude > 0f ? h : heading, out c, out w)) { lost = $"has no road surface under node {target}"; return false; }
                    if (Wide(w, widths)) { c = Ground(target); w = 0f; wide++; }
                }
                samples.Add(c);
                widths.Add(w);
            }
            widths.RemoveAll(x => x <= 0f);
            widths.Sort();
            width = Mathf.Clamp(widths.Count > 0 ? widths[widths.Count / 2] : FallbackRoadWidth, MinRoadWidth, MaxRoadWidth);
            if (wide > 0) Core.Log.Msg($"vehicle nav: '{_id}' {wide} cross-sections past a junction or forecourt kept the road's line");
            return true;
        }

        /// <summary>The line without its first <paramref name="metres"/>; a line not much longer stays whole.</summary>
        private List<Vector3> Trim(List<Vector3> line, float metres)
        {
            if (Length(line) < metres + MinLink) return line;
            float left = metres;
            for (int i = 0; i < line.Count - 1; i++)
            {
                float seg = FlatDistance(line[i], line[i + 1]);
                if (seg > left)
                {
                    var cut = new List<Vector3> { Ground(Vector3.Lerp(line[i], line[i + 1], left / seg)) };
                    cut.AddRange(line.GetRange(i + 1, line.Count - i - 1));
                    return cut;
                }
                left -= seg;
            }
            return line;
        }

        private static bool Wide(float w, List<float> widths)
        {
            var known = widths.FindAll(x => x > 0f);
            if (known.Count < 3) return false;
            known.Sort();
            return w > known[known.Count / 2] * WideSection;
        }

        /// <summary>The middle and width of the road surface on a line across <paramref name="heading"/> through <paramref name="p"/>.</summary>
        private bool Across(Vector3 p, Vector3 heading, out Vector3 middle, out float width)
        {
            middle = p;
            width = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, heading.sqrMagnitude > 0f ? heading.normalized : Vector3.forward);
            int n = Mathf.RoundToInt(ScanHalf / ScanStep);
            var road = new bool[2 * n + 1];
            var height = new float[2 * n + 1];
            for (int i = -n; i <= n; i++)
            {
                Vector3 q = p + side * (i * ScanStep);
                // From just above the road: a tunnel roof or a rock overhead is not the surface.
                if (!Physics.Raycast(q + Vector3.up * 3f, Vector3.down, out var hit, 9f, _mask, QueryTriggerInteraction.Ignore)) continue;
                road[i + n] = IsRoad(hit.collider);
                height[i + n] = hit.point.y;
            }
            int best = -1;
            for (int d = 0; d <= n && best < 0; d++)
            {
                if (road[n + d]) best = n + d;
                else if (road[n - d]) best = n - d;
            }
            if (best < 0) return false;
            int lo = best, hi = best;
            while (lo > 0 && road[lo - 1]) lo--;
            while (hi < 2 * n && road[hi + 1]) hi++;
            float mid = ((lo + hi) * 0.5f - n) * ScanStep;
            width = (hi - lo + 1) * ScanStep;
            middle = Ground(p + side * mid);
            return true;
        }

        /// <summary>
        /// A road collider: one the source's road nodes stand on, or one named as a road along its parents (the
        /// prologue's spline road and its town's road pieces). Terrain, rocks, sidewalks, barriers and signs are not.
        /// </summary>
        private bool IsRoad(Collider c)
        {
            int id = c.GetInstanceID();
            if (_roadColliders.Contains(id)) return true;
            if (_isRoad.TryGetValue(id, out bool known)) return known;
            bool road = false;
            if (c.gameObject.layer != LayerMask.NameToLayer("Terrain"))
                for (Transform t = c.transform; t != null; t = t.parent)
                {
                    // The nearest name that says what it is decides: a road piece's sidewalk or a road sign is not the road.
                    if (Has(t.name, "sidewalk") || Has(t.name, "barrier") || Has(t.name, "sign")) break;
                    if (Has(t.name, "road")) { road = true; break; }
                }
            _isRoad[id] = road;
            return road;
        }

        private static bool Has(string name, string part) => name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private Vector3 Ground(Vector3 p)
        {
            float best = float.MaxValue;
            Vector3? ground = null;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 2.5f, Vector3.down, 6f, _mask, QueryTriggerInteraction.Ignore))
            {
                if (h.distance >= best) continue;
                best = h.distance;
                ground = h.point;
            }
            return ground.HasValue ? ground.Value + Vector3.up * RoadNodeHeight : p;
        }

        /// <summary>
        /// Averages each sample with its neighbours, <see cref="SmoothPasses"/> times over. A cross-section that
        /// catches a kerb or a road piece's edge moves the middle by a metre or two; unsmoothed, the lanes zigzag.
        /// The ends stay put, since other roads join there.
        /// </summary>
        private List<Vector3> Smooth(List<Vector3> s)
        {
            var cur = new List<Vector3>(s);
            for (int pass = 0; pass < SmoothPasses; pass++)
            {
                var next = new List<Vector3>(cur);
                for (int i = 1; i < cur.Count - 1; i++)
                {
                    int r = Mathf.Min(SmoothReach, Mathf.Min(i, cur.Count - 1 - i));
                    float x = 0f, z = 0f;
                    for (int j = i - r; j <= i + r; j++) { x += cur[j].x; z += cur[j].z; }
                    next[i] = new Vector3(x / (2 * r + 1), cur[i].y, z / (2 * r + 1));
                }
                cur = next;
            }
            for (int i = 1; i < cur.Count - 1; i++) cur[i] = Ground(cur[i]);
            return cur;
        }

        /// <summary>Keeps a sample every <see cref="NodeSpacing"/> metres, and where the road has turned by more than <see cref="NodeBend"/> degrees.</summary>
        private static List<Vector3> Thin(List<Vector3> s)
        {
            var kept = new List<Vector3> { s[0] };
            for (int i = 1; i < s.Count - 1; i++)
            {
                Vector3 last = kept[^1];
                float along = FlatDistance(last, s[i]);
                float bend = kept.Count > 1 ? Vector3.Angle(Flat(last - kept[^2]), Flat(s[i] - last)) : 0f;
                if (along >= NodeSpacing || (along >= MinLink && bend > NodeBend)) kept.Add(s[i]);
            }
            if (FlatDistance(kept[^1], s[^1]) < MinLink && kept.Count > 1) kept[^1] = s[^1];
            else kept.Add(s[^1]);
            return kept;
        }

        private List<Vector3> Straight(List<Vector3> path)
        {
            var points = new List<Vector3> { Ground(path[0]) };
            for (int k = 1; k < path.Count; k++)
            {
                int steps = Mathf.Max(1, Mathf.CeilToInt(FlatDistance(path[k - 1], path[k]) / NodeSpacing));
                for (int i = 1; i <= steps; i++) points.Add(Ground(Vector3.Lerp(path[k - 1], path[k], (float)i / steps)));
            }
            return points;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static float Length(List<Vector3> s)
        {
            float l = 0f;
            for (int i = 1; i < s.Count; i++) l += Vector3.Distance(s[i - 1], s[i]);
            return l;
        }
    }
}
