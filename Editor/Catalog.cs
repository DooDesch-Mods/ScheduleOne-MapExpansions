using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MapExpansions.Editor
{
    /// <summary>
    /// What can be placed: main-world objects, objects of the expansion's source scene, buildable game items and the
    /// expansion's glTF models. A search fills a short result list; placing one puts it on the surface in the middle
    /// of the view, in the expansion that surface belongs to, as one undoable step.
    /// </summary>
    internal static class Catalog
    {
        internal enum Tab { Main, Source, Prefabs, Models }

        private const int MaxResults = 100;

        /// <summary>One search result: what the list shows and what placing it needs.</summary>
        internal sealed class Item
        {
            public string Label;
            public string Detail;
            public string Address;
            public Transform Object;
            public string Expansion;
        }

        internal static Tab Current { get; private set; } = Tab.Main;
        internal static string Query { get; private set; } = "";
        internal static string Hint { get; private set; } = "";
        private static readonly List<Item> Results = new();

        internal static IReadOnlyList<Item> Items => Results;

        internal static void SetTab(Tab tab, string why, bool search = true)
        {
            Current = tab;
            if (search) Search(Query, why);
        }

        /// <summary>Fills the result list for the current tab; main-world and source searches need two letters.</summary>
        internal static void Search(string query, string why)
        {
            Query = (query ?? "").Trim();
            Results.Clear();
            Hint = "";
            try
            {
                switch (Current)
                {
                    case Tab.Main: SearchScene(MainRoots(), null, null); break;
                    case Tab.Source: SearchSource(); break;
                    case Tab.Prefabs: SearchPrefabs(); break;
                    case Tab.Models: SearchModels(); break;
                }
            }
            catch (Exception e)
            {
                Core.Log.Error($"catalog ({why}): searching {Current} for '{Query}' failed: {e}");
                Hint = "Suche fehlgeschlagen, siehe Log.";
            }
            Core.Log.Msg($"catalog ({why}): {Current} '{Query}': {Results.Count} results{(Hint.Length > 0 ? $" ({Hint})" : "")}");
        }

        private static bool Matches(string text) => Query.Length == 0 || (text ?? "").IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static IEnumerable<Transform> MainRoots()
        {
            var scene = SceneManager.GetSceneByName("Main");
            if (!scene.IsValid()) yield break;
            foreach (var root in scene.GetRootGameObjects())
                if (root != null && !root.name.StartsWith("MapExpansions.", StringComparison.Ordinal)) yield return root.transform;
        }

        /// <summary>
        /// Walks the roots depth first in hierarchy order, as a template path is resolved, and lists objects with a
        /// renderer whose name matches. Each result gets the shortest path that leads back to it.
        /// </summary>
        private static void SearchScene(IEnumerable<Transform> roots, Transform skipRoot, string expansion)
        {
            if (Query.Length < 2)
            {
                Hint = "Mindestens zwei Buchstaben.";
                return;
            }
            var first = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var stack = new Stack<Transform>();
            foreach (var r in roots.Reverse()) stack.Push(r);
            int unaddressable = 0, more = 0;
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (!first.ContainsKey(t.name)) first[t.name] = t;
                if (Matches(t.name) && t.GetComponentInChildren<Renderer>(true) != null)
                {
                    if (Results.Count >= MaxResults) more++;
                    else if (Address(t, first) is string address)
                        Results.Add(new Item { Label = t.name, Detail = address, Address = address, Object = t, Expansion = expansion });
                    else unaddressable++;
                }
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    var c = t.GetChild(i);
                    if (t == skipRoot && (c.name == "Copies" || c.name == "Copy Templates" || c.name == "Probe Anchors")) continue;
                    stack.Push(c);
                }
            }
            if (more > 0) Hint = $"{more} weitere, Suche genauer fassen.";
            if (unaddressable > 0) Core.Log.Msg($"catalog: {unaddressable} matches have no path that leads back to them (a sibling of the same name comes first); left out");
        }

        /// <summary>The shortest "Name/Child/Child" path that finds the object again, or null.</summary>
        internal static string Address(Transform t, Dictionary<string, Transform> first)
        {
            var rest = new List<string>();
            for (var a = t; a != null; a = a.parent)
            {
                if (first.TryGetValue(a.name, out var f) && f == a)
                {
                    var rel = string.Join("/", rest);
                    var back = rel.Length == 0 ? a : a.Find(rel);
                    if (back == t) return rel.Length == 0 ? a.name : a.name + "/" + rel;
                }
                rest.Insert(0, a.name);
            }
            return null;
        }

        private static void SearchSource()
        {
            var b = Target(null);
            if (b?.Root == null)
            {
                Hint = "Keine Erweiterung gebaut.";
                return;
            }
            // The source content lies below the root; the framework's own holders are skipped.
            SearchScene(new[] { b.Root.transform }, b.Root.transform, b.Id);
            Results.RemoveAll(x => x.Object == b.Root.transform);
        }

        private static void SearchPrefabs()
        {
            var list = GamePrefabs.List();
            foreach (var p in list.Where(p => Matches(p.Name) || Matches(p.Id)))
            {
                if (Results.Count >= MaxResults) break;
                Results.Add(new Item { Label = p.Name, Detail = p.Id, Address = p.Id, Object = p.Prefab.transform });
            }
            if (list.Count == 0) Hint = "Keine Spiel-Prefabs geladen.";
        }

        private static void SearchModels()
        {
            var b = Target(null);
            if (b == null)
            {
                Hint = "Keine Erweiterung gebaut.";
                return;
            }
            var files = Models.Files(b);
            foreach (var f in files.Where(Matches).Take(MaxResults))
                Results.Add(new Item { Label = f, Detail = b.Id, Address = f, Expansion = b.Id });
            if (files.Count == 0) Hint = $"Keine Modelle in Mods/{b.Id}/models.";
        }

        /// <summary>
        /// The expansion a point lies in, else the one the camera is in, else the built one whose root is nearest to
        /// the camera.
        /// </summary>
        private static BuiltExpansion Target(Vector3? point)
        {
            string id = point is Vector3 p ? MapExpansionsApi.ExpansionAt(p) : null;
            var cam = EditorMode.Camera?.Camera;
            if (id == null && cam != null) id = MapExpansionsApi.ExpansionAt(cam.transform.position);
            if (id != null) return BuildPipeline.Get(id);
            var built = BuildPipeline.All.Where(x => x?.Root != null).ToList();
            if (built.Count <= 1 || cam == null) return built.FirstOrDefault();
            var from = cam.transform.position;
            return built.OrderBy(x => (x.Root.transform.position - from).sqrMagnitude).First();
        }

        /// <summary>Places result <paramref name="index"/> on the surface in the middle of the view.</summary>
        internal static bool Place(int index, string why)
        {
            if (index < 0 || index >= Results.Count)
            {
                Core.Log.Warning($"catalog place ({why}): no result {index} (there are {Results.Count})");
                return false;
            }
            var item = Results[index];
            var cam = EditorMode.Camera?.Camera;
            if (cam == null)
            {
                Core.Log.Warning($"catalog place ({why}): no editor camera");
                return false;
            }
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (!Physics.Raycast(ray, out var hit, 300f, ~0, QueryTriggerInteraction.Ignore))
            {
                Core.Log.Warning($"catalog place ({why}): nothing within 300 m in the middle of the view");
                Panel.Note("In der Bildmitte liegt nichts.");
                return false;
            }
            var b = Current == Tab.Source || Current == Tab.Models ? BuildPipeline.Get(item.Expansion) : Target(hit.point);
            if (b?.Root == null)
            {
                Core.Log.Warning($"catalog place ({why}): {hit.point} lies in no expansion");
                Panel.Note("Die Bildmitte liegt in keiner Erweiterung.");
                return false;
            }
            if ((Current == Tab.Source || Current == Tab.Models) && MapExpansionsApi.ExpansionAt(hit.point) != b.Id)
                Core.Log.Warning($"catalog place ({why}): '{item.Label}' belongs to '{b.Id}', the aimed point {hit.point} does not; it is placed there all the same");

            var d = new CopyDefinition { Added = true };
            Transform src, frame;
            switch (Current)
            {
                case Tab.Prefabs:
                    d.Prefab = item.Address;
                    src = frame = item.Object;
                    break;
                case Tab.Models:
                    d.Model = item.Address;
                    d.Collider = ModelCollider.Mesh;
                    src = frame = Models.Template(b, item.Address, d.Collider, MainCopies.TemplateHolder(b));
                    if (src == null)
                    {
                        Panel.Note($"Modell {item.Address} ist nicht lesbar, siehe Log.");
                        return false;
                    }
                    break;
                default:
                    if (item.Object == null)
                    {
                        Core.Log.Warning($"catalog place ({why}): '{item.Address}' is gone since the search");
                        return false;
                    }
                    d.Template = item.Address;
                    d.TemplateSide = Current == Tab.Main ? WorldSide.Main : WorldSide.Expansion;
                    src = frame = item.Object;
                    // A tilted object keeps its tilt only against a frame; At holds a turn about the vertical axis alone.
                    if (Vector3.Angle(src.up, Vector3.up) > 1f && src.parent != null)
                    {
                        var parentPath = item.Address.Contains('/') ? item.Address.Substring(0, item.Address.LastIndexOf('/')) : null;
                        if (parentPath != null)
                        {
                            d.Frame = parentPath;
                            frame = src.parent;
                        }
                        else Core.Log.Warning($"catalog place ({why}): '{item.Address}' is tilted and its parent has no path; the copy stands upright");
                    }
                    break;
            }

            var root = b.Root.transform;
            float yaw = Mathf.Repeat((Quaternion.Inverse(root.rotation) * frame.rotation).eulerAngles.y, 360f);
            var atRot = root.rotation * Quaternion.Euler(0f, yaw, 0f);
            var move = Matrix4x4.TRS(frame.position, atRot, Vector3.one) * Matrix4x4.TRS(frame.position, frame.rotation, Vector3.one).inverse;
            var bottom = BottomCentre(src, move) ?? frame.position;
            var atPos = frame.position + (hit.point - bottom);
            d.At = new LocalPoint(root.InverseTransformPoint(atPos), yaw);

            var list = Layers.CopyList(b.Definition, Layers.ExpansionOwner);
            if (list == null)
            {
                Core.Log.Error($"catalog place ({why}): '{b.Id}' has no list of its own copies");
                return false;
            }
            d.Id = FreeId(list, Slug(item.Label));
            Core.Log.Msg($"catalog place ({why}): {Current} '{item.Address}' as '{b.Id}/{d.Id}' at {atPos} (aimed at {hit.point} on '{hit.collider.name}'), yaw {yaw:0.#}");
            return Edits.PlaceNew(b, d, $"{item.Label} gesetzt", why);
        }

        /// <summary>
        /// The bottom middle of the template's meshes once moved, or null when it has none. A statically batched
        /// renderer's mesh filter holds the whole combined batch, so its world bounds stand in for the mesh.
        /// </summary>
        private static Vector3? BottomCentre(Transform src, Matrix4x4 move)
        {
            bool any = false;
            var b = new Bounds();
            void Add(Matrix4x4 m, Bounds mb)
            {
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            int batched = 0;
            foreach (var f in src.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = f.GetComponent<Renderer>();
                if (f.sharedMesh == null || r == null) continue;
                if (r.isPartOfStaticBatch)
                {
                    batched++;
                    if (r.bounds.size != Vector3.zero) Add(move, r.bounds);
                }
                else Add(move * f.transform.localToWorldMatrix, f.sharedMesh.bounds);
            }
            if (batched > 0) Core.Log.Msg($"catalog: '{src.name}' has {batched} statically batched renderers; their world bounds were used");
            return any ? new Vector3(b.center.x, b.min.y, b.center.z) : null;
        }

        private static string Slug(string name)
        {
            var s = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (s.Length > 32) s = s.Substring(0, 32).Trim('-');
            return s.Length == 0 ? "object" : s;
        }

        private static string FreeId(List<CopyDefinition> list, string stem)
        {
            if (list.All(c => c.Id != stem)) return stem;
            for (int n = 2; ; n++)
                if (list.All(c => c.Id != $"{stem}-{n}")) return $"{stem}-{n}";
        }

        internal static JsonObject State() => new()
        {
            ["tab"] = Current.ToString(),
            ["query"] = Query,
            ["hint"] = Hint,
            ["results"] = new JsonArray(Results.Select((x, i) => (JsonNode)new JsonObject
            {
                ["i"] = i,
                ["label"] = x.Label,
                ["detail"] = x.Detail,
            }).ToArray()),
        };
    }
}
