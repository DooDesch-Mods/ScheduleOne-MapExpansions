using UnityEngine;

namespace MapExpansions.Navigation
{
    /// <summary>
    /// Streets built from the game's road pieces (a straight piece, a T or a crossing; each has a box collider named
    /// "Road" under a "StraightRoad..." or "...Intersection..." object). A source's road nodes often follow only its
    /// main road, so a town's side streets and junctions have none. Each straight piece becomes a link between its two
    /// ends, each junction piece a node where the middle lines of its arms meet. Source nodes on a piece give way to
    /// the street's nodes, and a source road that came from outside joins the nearest open street end.
    /// </summary>
    internal static class Streets
    {
        /// <summary>Ends closer than this are one node.</summary>
        private const float SameEnd = 1f;

        /// <summary>A straight piece's end this close to a junction piece is one of its arms.</summary>
        private const float ArmReach = 0.75f;

        /// <summary>A source node this close to a piece lies on the street.</summary>
        private const float OnStreet = 1f;

        private struct Piece
        {
            public Vector3 Centre, Along, Across;
            public float HalfLength, HalfWidth;
            public bool Junction;
            public string Name;

            public bool Contains(Vector3 p, float margin)
            {
                Vector3 d = p - Centre;
                d.y = 0f;
                return Mathf.Abs(Vector3.Dot(d, Along)) <= HalfLength + margin && Mathf.Abs(Vector3.Dot(d, Across)) <= HalfWidth + margin;
            }
        }

        public static void Add(string id, Transform content, List<Vector3> nodes, List<(int From, int To, bool OneWay)> links, ISet<int> joints,
                               HashSet<int> streetNodes, HashSet<int> replaced)
        {
            if (content == null)
            {
                Core.Log.Warning($"vehicle nav: '{id}' has no placed content; no streets from road pieces");
                return;
            }
            var pieces = Find(content);
            if (pieces.Count == 0)
            {
                Core.Log.Msg($"vehicle nav: '{id}' has no road pieces; its roads come from the source's road nodes only");
                return;
            }

            int sourceCount = nodes.Count;
            int NodeAt(Vector3 p)
            {
                for (int i = sourceCount; i < nodes.Count; i++)
                    if (Flat(nodes[i] - p).magnitude < SameEnd) return i;
                nodes.Add(p);
                streetNodes.Add(nodes.Count - 1);
                return nodes.Count - 1;
            }

            var arms = new List<(Vector3 At, Vector3 Dir, int Node)>[pieces.Count];
            for (int j = 0; j < pieces.Count; j++) arms[j] = new();
            int straights = 0;
            foreach (var s in pieces)
            {
                if (s.Junction) continue;
                straights++;
                Vector3 a = s.Centre - s.Along * s.HalfLength, b = s.Centre + s.Along * s.HalfLength;
                int na = NodeAt(a), nb = NodeAt(b);
                links.Add((na, nb, false));
                for (int j = 0; j < pieces.Count; j++)
                {
                    if (!pieces[j].Junction) continue;
                    if (pieces[j].Contains(a, ArmReach)) arms[j].Add((a, s.Along, na));
                    if (pieces[j].Contains(b, ArmReach)) arms[j].Add((b, s.Along, nb));
                }
            }
            int junctions = 0;
            for (int j = 0; j < pieces.Count; j++)
            {
                if (!pieces[j].Junction) continue;
                if (arms[j].Count == 0)
                {
                    Core.Log.Warning($"vehicle nav: '{id}' junction piece '{pieces[j].Name}' at {pieces[j].Centre} has no straight piece at its sides; no street there");
                    continue;
                }
                junctions++;
                int nj = NodeAt(Meet(arms[j], pieces[j].Centre));
                foreach (var arm in arms[j]) links.Add((arm.Node, nj, false));
            }

            if (nodes.Count == sourceCount)
            {
                Core.Log.Warning($"vehicle nav: '{id}' {pieces.Count} road pieces but no straight one; no streets from them");
                return;
            }
            var degree = new int[nodes.Count];
            foreach (var l in links)
                if (l.From >= sourceCount && l.To >= sourceCount) { degree[l.From]++; degree[l.To]++; }
            var open = new List<int>();
            for (int i = sourceCount; i < nodes.Count; i++)
                if (degree[i] == 1) open.Add(i);

            for (int i = 0; i < sourceCount; i++)
                if (!joints.Contains(i) && pieces.Exists(p => p.Contains(nodes[i], OnStreet))) replaced.Add(i);
            int joined = 0, dropped = 0;
            for (int li = links.Count - 1; li >= 0; li--)
            {
                var (from, to, oneWay) = links[li];
                if (from >= sourceCount || to >= sourceCount) continue;
                bool f = replaced.Contains(from), t = replaced.Contains(to);
                if (!f && !t) continue;
                if (f && t) { links.RemoveAt(li); dropped++; continue; }
                int kept = f ? to : from;
                int end = Nearest(nodes, open.Count > 0 ? open : new List<int>(streetNodes), nodes[kept]);
                links[li] = f ? (end, to, oneWay) : (from, end, oneWay);
                joined++;
                Core.Log.Msg($"vehicle nav: '{id}' source road {from}-{to} joins the street end {nodes[end]}");
            }
            Core.Log.Msg($"vehicle nav: '{id}' streets from {straights} road pieces and {junctions} junctions: {nodes.Count - sourceCount} street nodes, " +
                         $"{open.Count} open ends; {replaced.Count} source road nodes lie on them, {dropped} source roads gave way, {joined} joined a street end");
        }

        private static List<Piece> Find(Transform content)
        {
            var list = new List<Piece>();
            foreach (var box in content.GetComponentsInChildren<BoxCollider>(false))
            {
                if (box == null || !box.enabled || box.isTrigger) continue;
                string n = box.name;
                if (n != "Road" && !n.StartsWith("Road.", StringComparison.Ordinal)) continue;
                bool piece = false, junction = false;
                for (Transform t = box.transform.parent; t != null && t != content; t = t.parent)
                {
                    if (t.name.IndexOf("Intersection", StringComparison.OrdinalIgnoreCase) >= 0) { piece = junction = true; break; }
                    if (t.name.IndexOf("StraightRoad", StringComparison.OrdinalIgnoreCase) >= 0) { piece = true; break; }
                }
                if (!piece) continue;

                // The two horizontal axes of the box, whichever of its local axes they are.
                var tr = box.transform;
                Vector3 size = box.size;
                var axes = new[] { tr.TransformVector(new Vector3(size.x, 0f, 0f)), tr.TransformVector(new Vector3(0f, size.y, 0f)), tr.TransformVector(new Vector3(0f, 0f, size.z)) };
                int up = 0;
                for (int i = 1; i < 3; i++)
                    if (Mathf.Abs(axes[i].normalized.y) > Mathf.Abs(axes[up].normalized.y)) up = i;
                Vector3 a = Flat(axes[(up + 1) % 3]), b = Flat(axes[(up + 2) % 3]);
                if (a.magnitude < b.magnitude) (a, b) = (b, a);
                list.Add(new Piece
                {
                    Centre = tr.TransformPoint(box.center),
                    Along = a.normalized,
                    Across = b.normalized,
                    HalfLength = a.magnitude * 0.5f,
                    HalfWidth = b.magnitude * 0.5f,
                    Junction = junction,
                    Name = tr.parent != null ? tr.parent.name : n,
                });
            }
            return list;
        }

        /// <summary>Where the middle lines of a junction's arms meet; with all arms in one line, the piece's middle on it.</summary>
        private static Vector3 Meet(List<(Vector3 At, Vector3 Dir, int Node)> arms, Vector3 centre)
        {
            float a11 = 0f, a12 = 0f, a22 = 0f, b1 = 0f, b2 = 0f;
            foreach (var (at, dir, _) in arms)
            {
                float dx = dir.x, dz = dir.z;
                float m11 = 1f - dx * dx, m12 = -dx * dz, m22 = 1f - dz * dz;
                a11 += m11; a12 += m12; a22 += m22;
                b1 += m11 * at.x + m12 * at.z;
                b2 += m12 * at.x + m22 * at.z;
            }
            float det = a11 * a22 - a12 * a12;
            if (Mathf.Abs(det) < 1e-3f)
            {
                var (at, dir, _) = arms[0];
                Vector3 on = at + dir * Vector3.Dot(centre - at, dir);
                return new Vector3(on.x, centre.y, on.z);
            }
            return new Vector3((b1 * a22 - b2 * a12) / det, centre.y, (a11 * b2 - a12 * b1) / det);
        }

        private static int Nearest(List<Vector3> nodes, IEnumerable<int> among, Vector3 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            foreach (int i in among)
            {
                float d = Flat(nodes[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        private static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
    }
}
