using System.Collections;
using Il2CppFishNet;
using Il2CppFishNet.Object;
using Il2CppFishNet.Observing;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Misc;
using MapExpansions.Lifecycle;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Copies main-world objects into expansions. Each peer builds the plain part of a copy itself. Networked objects
    /// below a template, such as doors, are taken out of it and registered as spawnable prefabs; the host spawns them
    /// where they belong, so their state is shared like the original's.
    /// </summary>
    internal static class MainCopies
    {
        /// <summary>A networked object of a copy: registered on every peer, spawned by the host, found on each peer once it exists.</summary>
        internal sealed class Part
        {
            public string Key;
            public GameObject Template;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public NetworkObject Instance;
            /// <summary>From where the part stands to where its original stands in the main world.</summary>
            public Matrix4x4 ToMain;
            /// <summary>Holder of the expansion's probe anchors, see <see cref="AnchorProbes"/>.</summary>
            public Transform Anchors;
        }

        /// <summary>What one definition became on this peer.</summary>
        internal sealed class Built
        {
            public CopyDefinition Def;
            public GameObject Local;
            public readonly List<Part> Parts = new();
            /// <summary>
            /// Lamps whose original the access zone of its main-world building switches with the opening hours. The
            /// zone is not part of the copy, so the owner of the copy switches these.
            /// </summary>
            public readonly List<ToggleableLight> ZoneLights = new();
        }

        /// <summary>A built copy with what a map editor needs to find it and write it back.</summary>
        internal sealed class Placed
        {
            public BuiltExpansion Expansion;
            /// <summary>Owner name in the expansion's layer: "expansion" or "shop:&lt;id&gt;".</summary>
            public string LayerOwner;
            public Built Built;
        }

        /// <summary>Every copy built in the loaded world, in build order.</summary>
        internal static readonly List<Placed> All = new();

        private static readonly List<Part> Parts = new();
        private static object _spawnRoutine;
        private static float _nextFind;

        /// <summary>Builds the copies under the expansion's root. Failed copies are logged and left out.</summary>
        public static List<Built> Build(BuiltExpansion b, IReadOnlyList<CopyDefinition> defs, string owner, string layerOwner)
        {
            var result = new List<Built>();
            if (defs.Count == 0) return result;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in defs)
            {
                names.Add(FirstSegment(d.Template));
                if (!string.IsNullOrEmpty(d.Frame)) names.Add(FirstSegment(d.Frame));
            }
            var found = FindInMain(names);
            var zoneLights = ZoneLightsOfMain();

            var templates = b.Root.transform.Find("Copy Templates")?.gameObject;
            if (templates == null)
            {
                templates = new GameObject("Copy Templates");
                templates.SetActive(false);
                templates.transform.SetParent(b.Root.transform, false);
            }
            var holder = b.Root.transform.Find("Copies")?.gameObject;
            if (holder == null)
            {
                holder = new GameObject("Copies");
                holder.transform.SetParent(b.Root.transform, false);
            }

            foreach (var d in defs)
            {
                try
                {
                    var built = BuildOne(b, d, owner, found, zoneLights, templates.transform, holder.transform);
                    if (built != null)
                    {
                        result.Add(built);
                        All.Add(new Placed { Expansion = b, LayerOwner = layerOwner, Built = built });
                    }
                }
                catch (Exception e)
                {
                    Core.Log.Error($"copies: '{b.Id}' {owner} copy '{d.Id}' of '{d.Template}' failed: {e}");
                }
            }
            Core.Log.Msg($"copies: '{b.Id}' {owner}: {result.Count}/{defs.Count} copies built, {result.Sum(x => x.Parts.Count)} networked parts");
            if (Parts.Count > 0 && _spawnRoutine == null) _spawnRoutine = MelonCoroutines.Start(SpawnWhenNetworked());
            return result;
        }

        private static Built BuildOne(BuiltExpansion b, CopyDefinition d, string owner, Dictionary<string, Transform> found,
            HashSet<int> zoneLights, Transform templates, Transform holder)
        {
            if (string.IsNullOrEmpty(d.Id) || string.IsNullOrEmpty(d.Template))
            {
                Core.Log.Error($"copies: '{b.Id}' {owner} has a copy without id or template ('{d.Id}', '{d.Template}'); it is not built");
                return null;
            }
            var src = Resolve(found, d.Template);
            if (src == null)
            {
                Core.Log.Error($"copies: '{b.Id}' {owner} copy '{d.Id}': '{d.Template}' is not in the main world; it is not built");
                return null;
            }
            var frame = string.IsNullOrEmpty(d.Frame) ? src : Resolve(found, d.Frame);
            if (frame == null)
            {
                Core.Log.Error($"copies: '{b.Id}' {owner} copy '{d.Id}': frame '{d.Frame}' is not in the main world; it is not built");
                return null;
            }

            Matrix4x4 at;
            if (string.IsNullOrEmpty(d.Onto))
                at = Matrix4x4.TRS(b.Root.transform.TransformPoint(d.At.Position),
                    b.Root.transform.rotation * Quaternion.Euler(0f, d.At.Yaw, 0f), Vector3.one * d.Scale);
            else
            {
                var onto = FindInExpansion(b, d.Onto);
                if (onto == null)
                {
                    Core.Log.Error($"copies: '{b.Id}' {owner} copy '{d.Id}': '{d.Onto}' is not in the expansion; it is not built");
                    return null;
                }
                at = Matrix4x4.TRS(onto.position, onto.rotation, Vector3.one)
                     * Matrix4x4.TRS(d.OntoOffset, Quaternion.Euler(d.OntoRotation), Vector3.one * d.Scale);
            }
            var frameM = Matrix4x4.TRS(frame.position, frame.rotation, Vector3.one);
            var move = at * frameM.inverse;
            var pose = move * src.localToWorldMatrix;
            var toMain = move.inverse;
            var anchors = AnchorHolder(b);
            if (!string.IsNullOrEmpty(d.Onto))
            {
                // The building the copy goes into is the counterpart of the original's, so its walls light like those.
                var onto = FindInExpansion(b, d.Onto);
                var (shell, shellAnchors) = AnchorProbes(anchors, onto.gameObject, toMain);
                if (shell > 0) Core.Log.Msg($"copies: '{b.Id}' {owner} '{d.Id}': {shell} renderers of '{d.Onto}' take their light probes in the main world at {shellAnchors} points");
            }

            // Built inactive, so nothing of the copy wakes before its networked parts are out and it stands in place.
            var go = Object.Instantiate(src.gameObject, templates);
            go.name = src.name;
            go.transform.SetPositionAndRotation(pose.GetColumn(3), pose.rotation);
            go.transform.localScale = pose.lossyScale;
            var built = new Built { Def = d };
            // A fresh copy has the original's hierarchy, so its lamps pair up with the original's in order.
            var srcLights = src.GetComponentsInChildren<ToggleableLight>(true);
            var copyLights = go.GetComponentsInChildren<ToggleableLight>(true);
            if (srcLights.Length == copyLights.Length)
            {
                for (int i = 0; i < srcLights.Length; i++)
                    if (srcLights[i] != null && zoneLights.Contains(srcLights[i].GetInstanceID())) built.ZoneLights.Add(copyLights[i]);
            }
            else
                Core.Log.Error($"copies: '{b.Id}' {owner} '{d.Id}': {copyLights.Length} lamps in the copy against {srcLights.Length} in '{d.Template}'; " +
                               "no lamp of it follows opening hours");
            Trim(b, d, owner, go);
            AddPanels(b, d, owner, go, move);
            built.ZoneLights.RemoveAll(l => l == null);
            var nobs = go.GetComponentsInChildren<NetworkObject>(true);
            // Outermost first; a networked object inside another one goes along with it.
            var roots = nobs.Where(n => n != null && !HasNetworkedAncestor(n, go.transform)).ToList();
            for (int i = 0; i < roots.Count; i++)
            {
                var nob = roots[i];
                // Copies of different owners may share an id ("door"); the owner keeps their prefab slots apart.
                var key = $"copy:{b.Id}/{KeyOf(owner)}/{d.Id}/{i}";
                var t = nob.transform;
                var part = new Part { Key = key, Template = nob.gameObject, Position = t.position, Rotation = t.rotation, Scale = t.lossyScale, ToMain = toMain, Anchors = anchors };
                t.SetParent(templates, true);
                int nested = SpawnablePrefabs.Detach(nob);
                SpawnablePrefabs.OpenObserver(nob.GetComponent<NetworkObserver>());
                if (!SpawnablePrefabs.Register(key, nob, "copies"))
                {
                    Object.Destroy(nob.gameObject);
                    continue;
                }
                Core.Log.Msg($"copies: '{b.Id}' {owner} '{d.Id}' networked part '{nob.name}' ({nested} nested) stands at {part.Position}");
                built.Parts.Add(part);
                Parts.Add(part);
            }

            if (nobs.Length == 1 && roots.Count == 1 && roots[0].gameObject == go)
            {
                // The template is a networked object itself; nothing is left to build locally.
                Core.Log.Msg($"copies: '{b.Id}' {owner} '{d.Id}' is networked as a whole");
                return built;
            }

            go.transform.SetParent(holder, true);
            go.SetActive(true);
            var (batched, batchRoots, _) = StaticBatches.Follow(go.transform, move, $"{owner} {d.Id}");
            var (anchored, points) = AnchorProbes(anchors, go, toMain);
            built.Local = go;
            Core.Log.Msg($"copies: '{b.Id}' {owner} '{d.Id}' from '{d.Template}' stands at {go.transform.position}, " +
                         $"{go.GetComponentsInChildren<Renderer>(true).Length} renderers, {batched} batched over {batchRoots} roots, " +
                         $"{anchored} take their light probes at {points} points of the main world, {built.ZoneLights.Count} lamps follow opening hours");
            return built;
        }

        /// <summary>The definition's panels and blocks as boxes below the copy, moved from the main world like it.</summary>
        private static void AddPanels(BuiltExpansion b, CopyDefinition d, string owner, GameObject go, Matrix4x4 move)
        {
            foreach (var (box, material) in d.Panels)
            {
                var mat = ObjectRules.LoadedMaterial(material);
                if (mat == null)
                {
                    Core.Log.Error($"copies: '{b.Id}' {owner} '{d.Id}': no loaded material '{material}'; the panel at {box.center} is left out");
                    continue;
                }
                var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
                panel.name = "MapExpansions.Panel";
                // The copy's own colliders already stand around the panel.
                Object.Destroy(panel.GetComponent<Collider>());
                panel.layer = go.layer;
                panel.transform.SetPositionAndRotation(move.MultiplyPoint3x4(box.center), move.rotation);
                panel.transform.localScale = Vector3.Scale(box.size, move.lossyScale);
                panel.GetComponent<MeshRenderer>().sharedMaterial = mat;
                panel.transform.SetParent(go.transform, true);
                Core.Log.Msg($"copies: '{b.Id}' {owner} '{d.Id}': panel of {box.size} in '{material}' stands at {panel.transform.position}");
            }
            foreach (var box in d.Blocks)
            {
                var block = new GameObject("MapExpansions.Block");
                block.transform.SetPositionAndRotation(move.MultiplyPoint3x4(box.center), move.rotation);
                block.transform.localScale = Vector3.Scale(box.size, move.lossyScale);
                block.AddComponent<BoxCollider>();
                block.transform.SetParent(go.transform, true);
                Core.Log.Msg($"copies: '{b.Id}' {owner} '{d.Id}': block of {box.size} stands at {block.transform.position}");
            }
        }

        /// <summary>Side of the cells in which renderers share one probe anchor, in metres.</summary>
        private const float AnchorCell = 0.5f;

        private static readonly Dictionary<IntPtr, Dictionary<Vector3Int, Transform>> AnchorCells = new();

        private static Transform AnchorHolder(BuiltExpansion b)
        {
            var holder = b.Root.transform.Find("Probe Anchors");
            if (holder != null) return holder;
            holder = new GameObject("Probe Anchors").transform;
            holder.SetParent(b.Root.transform, false);
            return holder;
        }

        /// <summary>
        /// The expansion has no baked light probes, so its renderers light like the open air, inside a building too.
        /// Each renderer below <paramref name="go"/> takes its probes where its counterpart stands in the main world
        /// instead; renderers in one cell share an anchor. Renderers that already have an anchor keep it. Returns the
        /// renderers anchored and the anchors they use.
        /// </summary>
        private static (int renderers, int anchors) AnchorProbes(Transform holder, GameObject go, Matrix4x4 toMain)
        {
            if (!AnchorCells.TryGetValue(holder.Pointer, out var cells)) AnchorCells[holder.Pointer] = cells = new Dictionary<Vector3Int, Transform>();
            int anchored = 0, inactive = 0;
            var used = new HashSet<Vector3Int>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.lightProbeUsage == UnityEngine.Rendering.LightProbeUsage.Off || r.probeAnchor != null) continue;
                Vector3 here;
                if (r.gameObject.activeInHierarchy) here = r.bounds.center;
                else { here = r.transform.position; inactive++; }
                var at = toMain.MultiplyPoint3x4(here);
                var cell = Vector3Int.FloorToInt(at / AnchorCell);
                if (!cells.TryGetValue(cell, out var anchor) || anchor == null)
                {
                    anchor = new GameObject("Probe Anchor").transform;
                    anchor.SetParent(holder, false);
                    anchor.position = ((Vector3)cell + Vector3.one * 0.5f) * AnchorCell;
                    cells[cell] = anchor;
                }
                r.probeAnchor = anchor;
                used.Add(cell);
                anchored++;
            }
            if (inactive > 0)
                Core.Log.Msg($"copies: {inactive} inactive renderers below '{go.name}' take their light probes from their pivot, not their bounds");
            return (anchored, used.Count);
        }

        /// <summary>Instance ids of the main world's lamps that an access zone switches.</summary>
        private static HashSet<int> ZoneLightsOfMain()
        {
            var ids = new HashSet<int>();
            int zones = 0;
            foreach (var z in Object.FindObjectsOfType<AccessZone>(true))
            {
                if (z == null || z.Lights == null) continue;
                zones++;
                foreach (var l in z.Lights)
                    if (l != null) ids.Add(l.GetInstanceID());
            }
            if (zones == 0) Core.Log.Warning("copies: the main world has no access zone; no copied lamp follows opening hours");
            return ids;
        }

        private static void Trim(BuiltExpansion b, CopyDefinition d, string owner, GameObject go)
        {
            foreach (var path in d.Exclude)
            {
                var t = string.IsNullOrEmpty(path) ? null : go.transform.Find(path);
                if (t == null)
                {
                    Core.Log.Warning($"copies: '{b.Id}' {owner} copy '{d.Id}': '{path}' to leave out is not below '{d.Template}'");
                    continue;
                }
                Object.DestroyImmediate(t.gameObject);
            }
            if (d.OwnMesh) return;
            int removed = 0;
            foreach (var c in go.GetComponents<Collider>()) { Object.DestroyImmediate(c); removed++; }
            var r = go.GetComponent<Renderer>();
            if (r != null) { Object.DestroyImmediate(r); removed++; }
            var f = go.GetComponent<MeshFilter>();
            if (f != null) { Object.DestroyImmediate(f); removed++; }
            Core.Log.Msg($"copies: '{b.Id}' {owner} copy '{d.Id}' leaves the template's own mesh behind ({removed} components)");
        }

        private static string KeyOf(string owner) =>
            new string(owner.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');

        private static bool HasNetworkedAncestor(NetworkObject n, Transform top)
        {
            for (var p = n.transform.parent; p != null && p != top.parent; p = p.parent)
                if (p.GetComponent<NetworkObject>() != null) return true;
            return false;
        }

        private static string FirstSegment(string path)
        {
            int i = path.IndexOf('/');
            return i < 0 ? path : path.Substring(0, i);
        }

        /// <summary>A main-world object by path, as in <see cref="CopyDefinition.Template"/>.</summary>
        public static Transform FindMain(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return Resolve(FindInMain(new HashSet<string>(StringComparer.Ordinal) { FirstSegment(path) }), path);
        }

        /// <summary>
        /// An object of the expansion's source by path: the first object named like the first segment, depth first,
        /// then its children along the rest. The framework's own holders of copies are skipped.
        /// </summary>
        public static Transform FindInExpansion(BuiltExpansion b, string path)
        {
            if (b.Root == null || string.IsNullOrEmpty(path)) return null;
            string first = FirstSegment(path);
            var stack = new Stack<Transform>();
            stack.Push(b.Root.transform);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                if (t != b.Root.transform && t.name == first)
                {
                    int i = path.IndexOf('/');
                    var hit = i < 0 ? t : t.Find(path.Substring(i + 1));
                    if (hit != null) return hit;
                }
                for (int c = t.childCount - 1; c >= 0; c--)
                {
                    var child = t.GetChild(c);
                    if (t == b.Root.transform && (child.name == "Copies" || child.name == "Copy Templates")) continue;
                    stack.Push(child);
                }
            }
            return null;
        }

        private static Transform Resolve(Dictionary<string, Transform> found, string path)
        {
            if (!found.TryGetValue(FirstSegment(path), out var t) || t == null) return null;
            int i = path.IndexOf('/');
            return i < 0 ? t : t.Find(path.Substring(i + 1));
        }

        /// <summary>
        /// The first object of each name in the main scene, depth first in hierarchy order. Roots of expansions and the
        /// copies made from main-world objects are skipped.
        /// </summary>
        private static Dictionary<string, Transform> FindInMain(HashSet<string> names)
        {
            var found = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var scene = SceneManager.GetSceneByName("Main");
            if (!scene.IsValid())
            {
                Core.Log.Error("copies: the main scene is not loaded; no main-world object can be copied");
                return found;
            }
            var stack = new Stack<Transform>();
            foreach (var root in scene.GetRootGameObjects().Reverse())
            {
                if (root == null || root.name.StartsWith("MapExpansions.", StringComparison.Ordinal)) continue;
                stack.Push(root.transform);
            }
            int visited = 0;
            while (stack.Count > 0 && found.Count < names.Count)
            {
                var t = stack.Pop();
                visited++;
                string n = t.name;
                if (names.Contains(n) && !found.ContainsKey(n)) found[n] = t;
                for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
            }
            foreach (var n in names.Where(n => !found.ContainsKey(n)))
                Core.Log.Error($"copies: no object '{n}' in the main scene ({visited} objects searched)");
            return found;
        }

        private static IEnumerator SpawnWhenNetworked()
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!InstanceFinder.IsServer && !InstanceFinder.IsClient)
            {
                if (wait.Elapsed.TotalMinutes > 3)
                {
                    Core.Log.Error("copies: the network did not start within 3 minutes; no networked part is spawned");
                    _spawnRoutine = null;
                    yield break;
                }
                yield return null;
            }
            _spawnRoutine = null;
            if (!InstanceFinder.IsServer)
            {
                Core.Log.Msg($"copies: client; {Parts.Count} networked parts arrive from the host");
                yield break;
            }
            int spawned = 0, standing = 0;
            foreach (var p in Parts.ToList())
            {
                // Every Build call starts this routine; parts of earlier calls already stand.
                if (p.Instance != null) { standing++; continue; }
                try
                {
                    var go = Object.Instantiate(p.Template, p.Position, p.Rotation);
                    go.name = p.Template.name;
                    go.transform.localScale = p.Scale;
                    go.SetActive(true);
                    var nob = go.GetComponent<NetworkObject>();
                    InstanceFinder.ServerManager.Spawn(nob, null, default(Scene));
                    SpawnablePrefabs.RebuildObservers(nob);
                    p.Instance = nob;
                    SettlePart(p);
                    spawned++;
                }
                catch (Exception e)
                {
                    Core.Log.Error($"copies: spawning '{p.Key}' failed: {e}");
                }
            }
            Core.Log.Msg($"copies: host spawned {spawned}/{Parts.Count - standing} new networked parts, {standing} already stand");
        }

        /// <summary>On a client the parts arrive from the host; each is matched to its definition by prefab id and place.</summary>
        public static void Tick()
        {
            if (Parts.Count == 0 || Time.unscaledTime < _nextFind) return;
            _nextFind = Time.unscaledTime + 1f;
            List<Part> open = null;
            foreach (var p in Parts)
                if (p.Instance == null) (open ??= new List<Part>()).Add(p);
            if (open == null) return;
            var spawned = InstanceFinder.ClientManager?.Objects?.Spawned;
            if (spawned == null) return;
            foreach (var kv in spawned)
            {
                var nob = kv.Value;
                if (nob == null) continue;
                foreach (var p in open)
                {
                    if (p.Instance != null) continue;
                    var tn = p.Template.GetComponent<NetworkObject>();
                    if (tn == null || nob.PrefabId != tn.PrefabId) continue;
                    if ((nob.transform.position - p.Position).sqrMagnitude > 0.25f) continue;
                    p.Instance = nob;
                    Core.Log.Msg($"copies: networked part '{p.Key}' arrived at {nob.transform.position}");
                    SettlePart(p);
                }
            }
        }

        /// <summary>
        /// A spawned part is an instance of its template, so its batched meshes still draw at the original and its
        /// renderers still sample the expansion's empty light probes. Both are pointed at the main world here.
        /// </summary>
        private static void SettlePart(Part p)
        {
            var (batched, roots, own) = StaticBatches.Follow(p.Instance.transform, p.ToMain.inverse, p.Key);
            if (batched + own > 0)
                Core.Log.Msg($"copies: networked part '{p.Key}': {batched} batched renderers draw where the part stands, over {roots} roots; {own} had their root in the part");
            if (p.Anchors == null)
            {
                Core.Log.Warning($"copies: networked part '{p.Key}' has no probe anchors; it lights like the open air");
                return;
            }
            var (anchored, points) = AnchorProbes(p.Anchors, p.Instance.gameObject, p.ToMain);
            Core.Log.Msg($"copies: networked part '{p.Key}': {anchored} renderers take their light probes at {points} points of the main world");
        }

        /// <summary>
        /// Moves a built copy by a world offset and writes the move into its definition, so the next build puts it
        /// there too. Networked parts that exist on this peer move along.
        /// </summary>
        internal static void Shift(Placed p, Vector3 delta)
        {
            var d = p.Built.Def;
            var b = p.Expansion;
            if (string.IsNullOrEmpty(d.Onto))
                d.At = new LocalPoint(d.At.Position + b.Root.transform.InverseTransformVector(delta), d.At.Yaw);
            else
            {
                var onto = FindInExpansion(b, d.Onto);
                if (onto == null)
                {
                    Core.Log.Error($"copies: '{b.Id}' {p.LayerOwner} '{d.Id}': '{d.Onto}' is gone; the move is not written into the definition");
                    return;
                }
                // The frame stands on the onto object's position and rotation, without its scale.
                d.OntoOffset += Quaternion.Inverse(onto.rotation) * delta;
            }
            if (p.Built.Local != null) p.Built.Local.transform.position += delta;
            foreach (var part in p.Built.Parts)
            {
                part.Position += delta;
                if (part.Instance != null) part.Instance.transform.position += delta;
            }
        }

        public static void Clear()
        {
            All.Clear();
            Parts.Clear();
            AnchorCells.Clear();
            _nextFind = 0f;
        }
    }
}
