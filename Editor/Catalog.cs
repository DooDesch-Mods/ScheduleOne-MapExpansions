using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    /// <summary>
    /// What can be placed: main-world objects, objects of the expansion's source scene, buildable game items and the
    /// expansion's glTF models. A search fills a short result list. Choosing a result shows its outline under the
    /// cursor; the wheel turns it, a click puts it there as one undoable step, Shift and a click keeps placing, Escape
    /// stops. The console places in the middle of the view instead.
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

        /// <summary>The result being placed with the mouse, or null.</summary>
        private static Item _placing;
        private static Tab _placingTab;
        private static LineRenderer _ghost;

        // The ghost's box at the placing turn, relative to the bottom middle; computed again only when the turn or
        // the target expansion changes.
        private static string _ghostKey;
        private static Bounds _ghostBox;
        private static bool _ghostOk;

        internal static bool Placing => _placing != null;
        internal static string PlacingLabel => _placing?.Label;

        /// <summary>Extra turn of the placed object about the vertical axis, in degrees; the wheel changes it.</summary>
        internal static float PlaceYaw { get; private set; }

        /// <summary>Starts placing result <paramref name="index"/> with the mouse.</summary>
        internal static void BeginPlace(int index, string why)
        {
            if (index < 0 || index >= Results.Count)
            {
                Core.Log.Warning($"catalog place ({why}): no result {index} (there are {Results.Count})");
                return;
            }
            Picking.Cancel(why);
            _placing = Results[index];
            _placingTab = Current;
            PlaceYaw = 0f;
            _ghostKey = null;
            _toldNoExpansion = false;
            Core.Log.Msg($"catalog place ({why}): placing {Current} '{_placing.Address}' with the mouse");
            Panel.Note($"{_placing.Label}: Klick setzt, Mausrad dreht.");
        }

        internal static void CancelPlace(string why)
        {
            if (_placing == null) return;
            Core.Log.Msg($"catalog place ({why}): placing '{_placing.Address}' stopped");
            _placing = null;
            HideGhost();
        }

        private static void HideGhost()
        {
            if (_ghost != null && _ghost.gameObject.activeSelf) _ghost.gameObject.SetActive(false);
            if (_ghostModel != null && _ghostModel.activeSelf) _ghostModel.SetActive(false);
        }

        /// <summary>Moves the ghost under the cursor and places on a click; true when placing owns the mouse.</summary>
        internal static bool TickPlace(Camera cam, bool overPanel)
        {
            if (_placing == null)
            {
                HideGhost();
                return false;
            }
            if (cam == null)
            {
                CancelPlace("the camera is gone");
                return false;
            }
            if (overPanel)
            {
                HideGhost();
                return false;
            }
            float wheel = EditorInput.Wheel;
            if (wheel != 0f)
            {
                PlaceYaw = Mathf.Repeat(PlaceYaw + Mathf.Sign(wheel) * Snap.TurnStep, 360f);
                Core.Log.Msg($"catalog place: turn {PlaceYaw:0}°");
            }
            var ray = cam.ScreenPointToRay(EditorInput.MousePosition);
            if (!Physics.Raycast(ray, out var hit, 2000f, ~0, QueryTriggerInteraction.Ignore))
            {
                HideGhost();
                return true;
            }
            DrawGhost(hit.point);
            if (EditorInput.LeftDown(0))
            {
                var item = _placing;
                bool more = EditorInput.Key(KeyCode.LeftShift) || EditorInput.Key(KeyCode.RightShift);
                PlaceAt(item, _placingTab, hit, PlaceYaw, more ? "click, shift" : "click");
                if (!more) CancelPlace("placed");
            }
            return true;
        }

        private static void DrawGhost(Vector3 point)
        {
            var b = _placingTab == Tab.Source || _placingTab == Tab.Models ? BuildPipeline.Get(_placing.Expansion) : Target(point);
            bool fits = b?.Root != null;
            string key = $"{b?.Id}|{PlaceYaw}";
            if (key != _ghostKey)
            {
                _ghostKey = key;
                _ghostOk = false;
                ClearGhostModel();
                // Without an expansion nothing can be placed, but the ghost still shows what was picked, in red and
                // turned about its own position.
                if ((fits || _placingTab != Tab.Models) && Resolve(_placing, _placingTab, b, "ghost", out _, out var src, out var frame))
                {
                    var move = fits
                        ? Turn(b, frame, PlaceYaw, out _)
                        : Matrix4x4.TRS(frame.position, Quaternion.Euler(0f, PlaceYaw, 0f), Vector3.one) * Matrix4x4.Translate(-frame.position);
                    if (MovedBounds(src, move, out var mb))
                    {
                        var bottom = new Vector3(mb.center.x, mb.min.y, mb.center.z);
                        mb.center -= bottom;
                        _ghostBox = mb;
                        _ghostOk = true;
                        BuildGhostModel(src, Matrix4x4.Translate(-bottom) * move, fits);
                    }
                }
                if (!_ghostOk) Core.Log.Msg($"catalog place: '{_placing.Address}' shows no ghost ({(b == null ? "no expansion and a model" : "no mesh")})");
                else if (!fits && !_toldNoExpansion)
                {
                    _toldNoExpansion = true;
                    Panel.Note("Keine Erweiterung gebaut, hier lässt sich nichts setzen.");
                }
            }
            if (!_ghostOk)
            {
                HideGhost();
                return;
            }
            var color = fits ? new Color(0.4f, 0.95f, 0.6f) : new Color(1f, 0.35f, 0.3f);
            if (_ghost == null) _ghost = Selection.NewLine("MapExpansionsEditor_Ghost", color);
            if (_ghost.startColor != color)
            {
                _ghost.material = Gizmo.Material(color);
                _ghost.startColor = _ghost.endColor = color;
            }
            var box = _ghostBox;
            box.center += point;
            Selection.DrawBox(_ghost, box, 0.002f);
            if (_ghostModel != null)
            {
                if (!_ghostModel.activeSelf) _ghostModel.SetActive(true);
                _ghostModel.transform.position = point;
            }
        }

        /// <summary>The panel said once per placing that no expansion is built.</summary>
        private static bool _toldNoExpansion;

        /// <summary>The meshes of the item being placed, drawn in the game's own build-ghost material.</summary>
        private static GameObject _ghostModel;

        /// <summary>
        /// Whether a part is switched on up to the template's root. A prefab asset is in no scene, so Unity calls all of
        /// it inactive in the hierarchy; its own switches still say what an instance would show.
        /// </summary>
        private static bool On(Transform t, Transform root)
        {
            for (; t != null; t = t.parent)
            {
                // The root's own switch is left out: some templates are kept switched off and turned on when placed.
                if (t == root) break;
                if (!t.gameObject.activeSelf) return false;
            }
            return true;
        }

        private static void ClearGhostModel()
        {
            if (_ghostModel == null) return;
            Object.Destroy(_ghostModel);
            _ghostModel = null;
        }

        /// <summary>
        /// Copies the template's meshes, without any of its components, under one root at the cursor. The vanilla
        /// build ghost materials come from the BuildManager; without them only the box is drawn. Statically batched
        /// renderers hold the mesh of the whole batch and are left out.
        /// </summary>
        private static void BuildGhostModel(Transform src, Matrix4x4 toCursor, bool fits)
        {
            var bm = Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Building.BuildManager>.InstanceExists
                ? Il2CppScheduleOne.DevUtilities.NetworkSingleton<Il2CppScheduleOne.Building.BuildManager>.Instance : null;
            var mat = bm == null ? null : fits ? bm.ghostMaterial_White : bm.ghostMaterial_Red;
            if (mat == null)
            {
                Core.Log.Warning($"catalog place: no build ghost material ({(bm == null ? "no BuildManager" : "field empty")}); '{src.name}' shows its box only");
                return;
            }
            _ghostModel = new GameObject("MapExpansionsEditor_GhostModel");
            Object.DontDestroyOnLoad(_ghostModel);
            int meshes = 0, batched = 0;
            foreach (var f in src.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = f.GetComponent<MeshRenderer>();
                if (f.sharedMesh == null || r == null || !r.enabled || !On(f.transform, src)) continue;
                if (r.isPartOfStaticBatch)
                {
                    batched++;
                    continue;
                }
                var m = toCursor * f.transform.localToWorldMatrix;
                var go = new GameObject(f.name);
                go.transform.SetParent(_ghostModel.transform, false);
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
                var gr = go.AddComponent<MeshRenderer>();
                var mats = new Il2CppReferenceArray<Material>(Math.Max(1, f.sharedMesh.subMeshCount));
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                gr.sharedMaterials = mats;
                gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                gr.receiveShadows = false;
                meshes++;
            }
            foreach (var s in src.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (s.sharedMesh == null || !s.enabled || !On(s.transform, src)) continue;
                var m = toCursor * s.transform.localToWorldMatrix;
                var go = new GameObject(s.name);
                go.transform.SetParent(_ghostModel.transform, false);
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = s.sharedMesh;
                var gr = go.AddComponent<MeshRenderer>();
                var mats = new Il2CppReferenceArray<Material>(Math.Max(1, s.sharedMesh.subMeshCount));
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                gr.sharedMaterials = mats;
                gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshes++;
            }
            _ghostModel.SetActive(false);
            Core.Log.Msg($"catalog place: ghost of '{src.name}' from {meshes} meshes in '{mat.name}'" + (batched > 0 ? $", {batched} statically batched left out" : ""));
            if (meshes == 0) ClearGhostModel();
        }

        /// <summary>Places result <paramref name="index"/> on the surface in the middle of the view.</summary>
        internal static bool Place(int index, string why)
        {
            if (index < 0 || index >= Results.Count)
            {
                Core.Log.Warning($"catalog place ({why}): no result {index} (there are {Results.Count})");
                return false;
            }
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
            return PlaceAt(Results[index], Current, hit, 0f, why);
        }

        /// <summary>Places an item with its bottom middle on a hit point, turned by an extra yaw.</summary>
        private static bool PlaceAt(Item item, Tab tab, RaycastHit hit, float extraYaw, string why)
        {
            var b = tab == Tab.Source || tab == Tab.Models ? BuildPipeline.Get(item.Expansion) : Target(hit.point);
            if (b?.Root == null)
            {
                Core.Log.Warning($"catalog place ({why}): {hit.point} lies in no expansion");
                Panel.Note("Der Punkt liegt in keiner Erweiterung.");
                return false;
            }
            if ((tab == Tab.Source || tab == Tab.Models) && MapExpansionsApi.ExpansionAt(hit.point) != b.Id)
                Core.Log.Warning($"catalog place ({why}): '{item.Label}' belongs to '{b.Id}', the aimed point {hit.point} does not; it is placed there all the same");
            if (!Resolve(item, tab, b, why, out var d, out var src, out var frame)) return false;

            var root = b.Root.transform;
            var move = Turn(b, frame, extraYaw, out float yaw);
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
            Core.Log.Msg($"catalog place ({why}): {tab} '{item.Address}' as '{b.Id}/{d.Id}' at {atPos} (aimed at {hit.point} on '{hit.collider.name}'), yaw {yaw:0.#}");
            return Edits.PlaceNew(b, d, $"{item.Label} gesetzt", why);
        }

        /// <summary>
        /// The move from the source object's pose to the placed pose: turned about the vertical axis to the expansion
        /// root's frame plus the extra yaw, about the frame's own position.
        /// </summary>
        private static Matrix4x4 Turn(BuiltExpansion b, Transform frame, float extraYaw, out float yaw)
        {
            var root = b.Root.transform;
            yaw = Mathf.Repeat((Quaternion.Inverse(root.rotation) * frame.rotation).eulerAngles.y + extraYaw, 360f);
            var atRot = root.rotation * Quaternion.Euler(0f, yaw, 0f);
            return Matrix4x4.TRS(frame.position, atRot, Vector3.one) * Matrix4x4.TRS(frame.position, frame.rotation, Vector3.one).inverse;
        }

        /// <summary>The copy definition of an item and the objects its shape and turn come from.</summary>
        private static bool Resolve(Item item, Tab tab, BuiltExpansion b, string why, out CopyDefinition d, out Transform src, out Transform frame)
        {
            d = new CopyDefinition { Added = true };
            src = frame = null;
            switch (tab)
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
                        Core.Log.Warning($"catalog place ({why}): model '{item.Address}' gives no template");
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
                    d.TemplateSide = tab == Tab.Main ? WorldSide.Main : WorldSide.Expansion;
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
            return true;
        }

        /// <summary>
        /// The bottom middle of the template's meshes once moved, or null when it has none. A statically batched
        /// renderer's mesh filter holds the whole combined batch, so its world bounds stand in for the mesh.
        /// </summary>
        private static Vector3? BottomCentre(Transform src, Matrix4x4 move) =>
            MovedBounds(src, move, out var b) ? new Vector3(b.center.x, b.min.y, b.center.z) : null;

        /// <summary>World bounds of the template's meshes once moved; false when it has none.</summary>
        private static bool MovedBounds(Transform src, Matrix4x4 move, out Bounds b)
        {
            bool any = false;
            var box = new Bounds();
            void Add(Matrix4x4 m, Bounds mb)
            {
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(c);
                    if (!any) { box = new Bounds(p, Vector3.zero); any = true; }
                    else box.Encapsulate(p);
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
            b = box;
            return any;
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
