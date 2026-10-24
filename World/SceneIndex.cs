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
        }

        public static bool HasAncestor(Transform t, string prefix)
        {
            for (var p = t.parent; p != null; p = p.parent)
                if (p.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool Matches(Transform t, string name, string parent) =>
            t.name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && (parent == null || HasAncestor(t, parent));

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
            }
            return false;
        }
    }
}
