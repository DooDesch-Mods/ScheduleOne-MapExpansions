using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MapExpansions.World
{
    /// <summary>
    /// The renderers, colliders and terrains of the main world and of each placed expansion, collected once per
    /// build. Inactive objects are included, so a rule still holds when the game switches an object on later.
    /// </summary>
    internal sealed class SceneIndex
    {
        public sealed class Part
        {
            public readonly List<Transform> Transforms = new();
            public readonly List<Renderer> Renderers = new();
            public readonly List<Collider> Colliders = new();
            public readonly List<Terrain> Terrains = new();
            public readonly List<Light> Lights = new();
        }

        /// <summary>
        /// Bounds of every renderer and collider of one part, read once while a state applies its rules. A rule with an
        /// area only selects objects whose own bounds touch it, so it can skip the name walk for all the others.
        /// </summary>
        public sealed class AreaSnapshot
        {
            private readonly Part _part;
            private readonly List<(Renderer Item, IntPtr Owner, Bounds Bounds)> _renderers = new();
            private readonly List<(Collider Item, IntPtr Owner, Bounds Bounds)> _colliders = new();
            private readonly List<(Light Item, IntPtr Owner, Vector3 Position)> _lights = new();

            public AreaSnapshot(Part part)
            {
                _part = part;
                foreach (var r in part.Renderers) if (r != null) _renderers.Add((r, r.transform.Pointer, r.bounds));
                foreach (var c in part.Colliders) if (c != null) _colliders.Add((c, c.transform.Pointer, c.bounds));
                foreach (var l in part.Lights) if (l != null) _lights.Add((l, l.transform.Pointer, l.transform.position));
            }

            /// <summary>The part's objects a rule with this area can select; terrains are few and stay in.</summary>
            public Part Near(Bounds area)
            {
                var owners = new HashSet<IntPtr>();
                foreach (var (_, owner, bounds) in _renderers) if (bounds.Intersects(area)) owners.Add(owner);
                foreach (var (_, owner, bounds) in _colliders) if (bounds.Intersects(area)) owners.Add(owner);
                var near = new Part();
                foreach (var (item, owner, _) in _renderers) if (owners.Contains(owner)) near.Renderers.Add(item);
                foreach (var (item, owner, _) in _colliders) if (owners.Contains(owner)) near.Colliders.Add(item);
                foreach (var (item, owner, position) in _lights) if (owners.Contains(owner) || area.Contains(position)) near.Lights.Add(item);
                near.Terrains.AddRange(_part.Terrains);
                return near;
            }
        }

        public readonly Part Main = new();
        private readonly Dictionary<string, Part> _expansions = new(StringComparer.Ordinal);

        /// <summary>How many renderers use each mesh, across every loaded scene and the capture cache.</summary>
        public readonly Dictionary<IntPtr, int> MeshUsers = new();

        public Part Expansion(string id) => _expansions.TryGetValue(id, out var p) ? p : null;

        public Part Of(WorldSide side, BuiltExpansion b) => side == WorldSide.Main ? Main : Expansion(b.Id);

        public static SceneIndex Build(Scene main, IEnumerable<BuiltExpansion> built)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var idx = new SceneIndex();
            var roots = built.Where(b => b.Root != null).ToList();
            foreach (var go in main.GetRootGameObjects())
            {
                if (roots.Any(b => b.Root.Pointer == go.Pointer)) continue;
                Fill(idx.Main, go);
            }
            foreach (var b in roots)
            {
                var part = new Part();
                Fill(part, b.Root);
                idx._expansions[b.Id] = part;
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<MeshFilter>(), true))
            {
                var mf = o.TryCast<MeshFilter>();
                var m = mf?.sharedMesh;
                if (m == null) continue;
                idx.MeshUsers.TryGetValue(m.Pointer, out int n);
                idx.MeshUsers[m.Pointer] = n + 1;
            }
            Core.Log.Msg($"index: main {idx.Main.Transforms.Count} objects, {idx.Main.Renderers.Count} renderers, {idx.Main.Colliders.Count} colliders, " +
                         $"{idx.Main.Terrains.Count} terrains; {string.Join(", ", idx._expansions.Select(kv => $"'{kv.Key}' {kv.Value.Renderers.Count} renderers"))}; " +
                         $"{idx.MeshUsers.Count} meshes in use; {sw.ElapsedMilliseconds} ms");
            return idx;
        }

        private static void Fill(Part part, GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) part.Transforms.Add(t);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) part.Renderers.Add(r);
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) part.Colliders.Add(c);
            foreach (var t in root.GetComponentsInChildren<Terrain>(true)) part.Terrains.Add(t);
            foreach (var l in root.GetComponentsInChildren<Light>(true)) part.Lights.Add(l);
        }

        public static bool HasAncestor(Transform t, string prefix)
        {
            for (var p = t.parent; p != null; p = p.parent)
                if (p.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool Matches(Transform t, string name, string parent) =>
            t.name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && (parent == null || HasAncestor(t, parent));

        /// <summary>
        /// True when the transform or one of its ancestors matches the rule by name and the transform stands in the
        /// area. For instanced world objects such as map trees, whose renderers report bounds tens of metres wide and
        /// would select every neighbour.
        /// </summary>
        public static bool MatchesByPosition(Transform t, ObjectRule rule, Bounds worldArea)
        {
            for (var p = t; p != null; p = p.parent)
                if (Matches(p, rule.Name, rule.Parent))
                    return rule.Area.size == Vector3.zero || worldArea.Contains(t.position);
            return false;
        }

        /// <summary>True when the transform or one of its ancestors matches the rule.</summary>
        public static bool MatchesSelfOrAncestor(Transform t, ObjectRule rule, Bounds worldArea)
        {
            for (var p = t; p != null; p = p.parent)
            {
                if (!Matches(p, rule.Name, rule.Parent)) continue;
                if (rule.Area.size == Vector3.zero) return true;
                var r = t.GetComponent<Renderer>();
                var c = t.GetComponent<Collider>();
                if ((r != null && r.bounds.Intersects(worldArea)) || (c != null && c.bounds.Intersects(worldArea))) return true;
                // A lamp has no bounds of its own; it belongs to the area it shines from.
                if (r == null && c == null && t.GetComponent<Light>() != null && worldArea.Contains(t.position)) return true;
            }
            return false;
        }
    }
}
