using Il2CppInterop.Runtime;
using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>Applies the object rules and ground plates of a definition.</summary>
    internal static class ObjectRules
    {
        /// <summary>Switches off what each rule selects. Colliders that were switched off are added to <paramref name="offColliders"/>.</summary>
        public static void Apply(BuiltExpansion b, SceneIndex index, IEnumerable<ObjectRule> rules, HashSet<IntPtr> offColliders,
            TerrainOps.HoleMask holes)
        {
            foreach (var rule in rules)
            {
                var part = index.Of(rule.Side, b);
                if (part == null)
                {
                    Core.Log.Error($"rules: '{b.Id}' {rule}: the {rule.Side} side is not indexed");
                    continue;
                }
                var area = rule.Area.size == Vector3.zero ? default : (rule.Side == WorldSide.Main ? rule.Area : b.Frame.ToWorld(rule.Area));
                if (rule.Effect == ObjectEffect.DoubleSided)
                {
                    DoubleSided(b, part, rule, area);
                    continue;
                }
                if (rule.Effect == ObjectEffect.Skirt)
                {
                    if (area.size == Vector3.zero)
                    {
                        Core.Log.Error($"rules: '{b.Id}' {rule}: a skirt needs an area; nothing is built");
                        continue;
                    }
                    float bottom = rule.Side == WorldSide.Main ? rule.Bottom : b.Frame.ToWorld(Vector3.up * rule.Bottom).y;
                    Skirts.Apply(b, part, rule, area, bottom);
                    continue;
                }
                if (rule.Effect == ObjectEffect.ColliderTop)
                {
                    ColliderTop(b, part, rule, area, rule.TopShift);
                    continue;
                }
                if (rule.Effect == ObjectEffect.ExactCollision)
                {
                    ExactCollision(b, part, rule, area);
                    continue;
                }
                if (rule.Effect == ObjectEffect.Hollow)
                {
                    var openings = rule.Openings.Select(o => rule.Side == WorldSide.Main ? o : b.Frame.ToWorld(o)).ToList();
                    var windows = rule.Windows.Select(o => rule.Side == WorldSide.Main ? o : b.Frame.ToWorld(o)).ToList();
                    Hollow(b, part, rule, area, openings, windows, offColliders);
                    continue;
                }
                if (rule.Effect == ObjectEffect.Cut || rule.Effect == ObjectEffect.CutCollision)
                {
                    if (area.size == Vector3.zero)
                    {
                        Core.Log.Error($"rules: '{b.Id}' {rule}: a cut needs an area; nothing is cut");
                        continue;
                    }
                    Cut(b, index, part, rule, area, offColliders, rule.Effect == ObjectEffect.Cut);
                    continue;
                }
                if (rule.Effect == ObjectEffect.CloseHoles)
                {
                    if (area.size == Vector3.zero)
                    {
                        Core.Log.Error($"rules: '{b.Id}' {rule}: closing holes needs an area; nothing is closed");
                        continue;
                    }
                    var rect = Rect.MinMaxRect(area.min.x, area.min.z, area.max.x, area.max.z);
                    int matched = 0, closed = 0;
                    foreach (var t in part.Terrains)
                    {
                        if (t == null || !SceneIndex.MatchesSelfOrAncestor(t.transform, rule, area)) continue;
                        matched++;
                        closed += holes.Close(t, rect, rule.ToString());
                    }
                    string holesLine = $"rules: '{b.Id}' {rule}: {closed} hole cells closed in {matched} terrains";
                    if (closed == 0) Core.Log.Warning(holesLine + "; nothing was open there");
                    else Core.Log.Msg(holesLine);
                    continue;
                }
                int renderers = 0, colliders = 0, terrains = 0, lights = 0;
                bool hide = rule.Effect != ObjectEffect.NoCollision;
                bool noCollision = rule.Effect != ObjectEffect.Hide;
                var rec = new Record { ExpansionId = b.Id, Rule = rule };
                Records.Add(rec);
                if (hide)
                    foreach (var r in part.Renderers)
                    {
                        if (r == null || !SceneIndex.MatchesSelfOrAncestor(r.transform, rule, area)) continue;
                        if (Take(rec, r, r.enabled, v => r.enabled = v)) renderers++;
                    }
                // A removed car's headlights would still light up the walls it stood in front of.
                if (hide)
                    foreach (var l in part.Lights)
                    {
                        if (l == null || !SceneIndex.MatchesSelfOrAncestor(l.transform, rule, area)) continue;
                        if (Take(rec, l, l.enabled, v => l.enabled = v)) lights++;
                    }
                if (noCollision)
                    foreach (var c in part.Colliders)
                    {
                        if (c == null || !SceneIndex.MatchesSelfOrAncestor(c.transform, rule, area)) continue;
                        if (!Take(rec, c, c.enabled, v => c.enabled = v)) continue;
                        offColliders.Add(c.Pointer);
                        colliders++;
                    }
                foreach (var t in part.Terrains)
                {
                    if (t == null || !SceneIndex.MatchesSelfOrAncestor(t.transform, rule, area)) continue;
                    // A switched-off terrain also drops out of Terrain.activeTerrains, which the vehicle graph scan reads.
                    if (hide) Take(rec, t, t.enabled, v => t.enabled = v);
                    if (noCollision)
                    {
                        var tc = t.GetComponent<TerrainCollider>();
                        if (tc != null && Take(rec, tc, tc.enabled, v => tc.enabled = v)) offColliders.Add(tc.Pointer);
                    }
                    terrains++;
                }
                string line = $"rules: '{b.Id}' {rule}: {renderers} renderers, {colliders} colliders, {terrains} terrains, {lights} lamps";
                if (renderers + colliders + terrains == 0) Core.Log.Warning(line + "; the rule matched nothing");
                else Core.Log.Msg(line);
            }
        }

        /// <summary>A component a hiding rule switched off, and how many rules want it off.</summary>
        private sealed class Hold
        {
            public Object Target;
            public Action<bool> Set;
            public int Count;
            /// <summary>A rule switched it off; it was on before. Only these are switched on again.</summary>
            public bool Ours;
        }

        /// <summary>What one hiding rule holds off, so the rule can be taken back alone.</summary>
        private sealed class Record
        {
            public string ExpansionId;
            public ObjectRule Rule;
            public readonly List<IntPtr> Keys = new();
        }

        private static readonly Dictionary<IntPtr, Hold> Holds = new();
        private static readonly List<Record> Records = new();

        /// <summary>The effects a rule can be taken back from alone; the others change meshes and colliders for good.</summary>
        internal static bool CanRevert(ObjectEffect e) => e is ObjectEffect.Hide or ObjectEffect.NoCollision or ObjectEffect.Remove;

        /// <summary>Counts the rule as holding the component off and switches it off. True when it was on.</summary>
        private static bool Take(Record rec, Component target, bool enabled, Action<bool> set)
        {
            var key = target.Pointer;
            if (!Holds.TryGetValue(key, out var h)) Holds[key] = h = new Hold { Target = target, Set = set };
            h.Count++;
            rec.Keys.Add(key);
            if (!enabled) return false;
            set(false);
            h.Ours = true;
            return true;
        }

        /// <summary>
        /// Takes back one hiding rule: what no other rule still holds off and a rule switched off comes back on.
        /// Returns the components switched on, or -1 when the rule cannot be taken back alone.
        /// </summary>
        internal static int Revert(BuiltExpansion b, ObjectRule rule)
        {
            var rec = Records.FirstOrDefault(r => r.ExpansionId == b.Id && ReferenceEquals(r.Rule, rule));
            if (rec == null)
            {
                if (CanRevert(rule.Effect)) Core.Log.Warning($"rules: revert '{b.Id}' {rule}: the rule is not applied");
                else Core.Log.Warning($"rules: revert '{b.Id}' {rule}: {rule.Effect} changes meshes or colliders; only rebuilding the world takes it back");
                return -1;
            }
            int back = 0;
            foreach (var key in rec.Keys)
            {
                if (!Holds.TryGetValue(key, out var h)) continue;
                if (--h.Count > 0) continue;
                Holds.Remove(key);
                if (!h.Ours || h.Target == null) continue;
                h.Set(true);
                back++;
            }
            Records.Remove(rec);
            Core.Log.Msg($"rules: revert '{b.Id}' {rule}: {back} components on again, {rec.Keys.Count - back} stay as other rules or the world keep them");
            return back;
        }

        /// <summary>Applies one rule to a world that is already built, as a map editor adds or changes it.</summary>
        internal static void ApplyOne(BuiltExpansion b, SceneIndex index, ObjectRule rule)
        {
            if (index == null)
            {
                Core.Log.Error($"rules: apply '{b.Id}' {rule}: the world has no scene index; nothing is applied");
                return;
            }
            var off = new HashSet<IntPtr>();
            var holes = new TerrainOps.HoleMask(b);
            Apply(b, index, new[] { rule }, off, holes);
            int cells = holes.Apply();
            int detached = ColliderKeepAlive.Detach(off);
            Physics.SyncTransforms();
            Core.Log.Msg($"rules: apply '{b.Id}' {rule}: {off.Count} colliders off ({detached} taken out of collider groups), {cells} terrain cells changed");
        }

        /// <summary>How many hiding rules are applied and how many components they hold off, for checks.</summary>
        internal static (int rules, int held) Counts(string expansionId) =>
            (Records.Count(r => r.ExpansionId == expansionId), Holds.Count);

        internal static void Clear()
        {
            Holds.Clear();
            Records.Clear();
        }

        /// <summary>
        /// Gives every renderer the rule selects copies of its materials with back-face culling off. One copy per
        /// source material, shared by all renderers that used it, owned by the build.
        /// </summary>
        private static void DoubleSided(BuiltExpansion b, SceneIndex.Part part, ObjectRule rule, Bounds area)
        {
            var copies = new Dictionary<IntPtr, Material>();
            var noCull = new HashSet<IntPtr>();
            int renderers = 0, backs = 0, lowerLods = 0, failed = 0, vertices = 0;
            var shaders = new HashSet<string>();
            var added = new List<Renderer>();
            foreach (var r in part.Renderers)
            {
                if (r == null || !r.enabled || r.name.StartsWith(BackName, StringComparison.Ordinal)) continue;
                if (!SceneIndex.MatchesSelfOrAncestor(r.transform, rule, area)) continue;
                var mats = r.sharedMaterials;
                bool changed = false, needsBack = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || m.name.EndsWith(TwoSidedSuffix, StringComparison.Ordinal)) continue;
                    if (noCull.Contains(m.Pointer))
                    {
                        needsBack = true;
                        continue;
                    }
                    if (!copies.TryGetValue(m.Pointer, out var copy))
                    {
                        if (!m.HasProperty(CullId))
                        {
                            // Without a culling property the shader decides on its own; a reversed copy of the mesh draws the back.
                            shaders.Add(m.shader != null ? m.shader.name : "?");
                            noCull.Add(m.Pointer);
                            needsBack = true;
                            continue;
                        }
                        copy = b.Own(new Material(m) { name = m.name + TwoSidedSuffix });
                        copy.SetFloat(CullId, 0f);
                        copy.doubleSidedGI = true;
                        copies[m.Pointer] = copy;
                    }
                    mats[i] = copy;
                    changed = true;
                }
                if (changed)
                {
                    r.sharedMaterials = mats;
                    renderers++;
                }
                if (!needsBack) continue;
                var mr = r.TryCast<MeshRenderer>();
                if (mr == null) continue;
                if (!IsCoarsestLod(r))
                {
                    lowerLods++;
                    continue;
                }
                try
                {
                    var back = AddBack(b, mr);
                    if (back == null) continue;
                    added.Add(back);
                    backs++;
                    vertices += back.GetComponent<MeshFilter>().sharedMesh.vertexCount;
                }
                catch (Exception e)
                {
                    failed++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: the back of '{MeshCarver.Path(r.transform)}' could not be built, it stays one-sided: {e.Message}");
                }
            }
            // The backs join the index, so later rules and the clearing of the connections treat them like their originals.
            part.Renderers.AddRange(added);
            string line = $"rules: '{b.Id}' {rule}: {renderers} renderers, {copies.Count} materials made two-sided";
            if (noCull.Count > 0)
                line += $"; {noCull.Count} materials without _Cull ({string.Join(", ", shaders)}): {backs} reversed backs ({vertices} vertices), " +
                        $"{lowerLods} finer LODs without a back";
            if (failed > 0) Core.Log.Warning($"{line}; {failed} backs failed");
            else if (renderers + backs == 0) Core.Log.Warning(line + "; the rule matched nothing");
            else Core.Log.Msg(line);
        }

        /// <summary>
        /// Moves the top face of every box collider the rule selects by <paramref name="shift"/> metres up (down when
        /// negative); the bottom face stays. Only boxes with one local axis upright are changed.
        /// </summary>
        private static void ColliderTop(BuiltExpansion b, SceneIndex.Part part, ObjectRule rule, Bounds area, float shift)
        {
            int changed = 0, tilted = 0, tooThin = 0;
            float before = float.NaN, after = float.NaN;
            var axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
            foreach (var c in part.Colliders)
            {
                if (c == null || !c.enabled || c.isTrigger || !SceneIndex.MatchesSelfOrAncestor(c.transform, rule, area)) continue;
                var box = c.TryCast<BoxCollider>();
                if (box == null) continue;
                var t = box.transform;
                var path = MeshCarver.Path(t);
                int i = -1;
                float dot = 0f;
                for (int k = 0; k < 3; k++)
                {
                    float d = Vector3.Dot(t.TransformDirection(axes[k]), Vector3.up);
                    if (Mathf.Abs(d) > Mathf.Abs(dot)) (i, dot) = (k, d);
                }
                if (Mathf.Abs(dot) < 0.999f)
                {
                    tilted++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: box '{path}' has no upright axis and keeps its top");
                    continue;
                }
                var upLocal = axes[i] * Mathf.Sign(dot);
                float scale = Mathf.Abs(t.lossyScale[i]);
                var size = box.size;
                float delta = scale < 1e-4f ? 0f : shift / scale;
                if (scale < 1e-4f || size[i] + delta < 0.01f)
                {
                    tooThin++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: box '{path}' is {size[i] * scale:0.00} m high; moving its top by {shift:0.00} m leaves nothing, it stays");
                    continue;
                }
                if (float.IsNaN(before)) before = t.TransformPoint(box.center + upLocal * (size[i] * 0.5f)).y;
                size[i] += delta;
                box.size = size;
                box.center += upLocal * (delta * 0.5f);
                if (float.IsNaN(after)) after = t.TransformPoint(box.center + upLocal * (size[i] * 0.5f)).y;
                changed++;
            }
            string line = $"rules: '{b.Id}' {rule}: {changed} boxes moved their top by {shift:0.00} m (the first from {before:0.00} to {after:0.00}), {tilted} tilted, {tooThin} too thin";
            if (changed == 0) Core.Log.Warning(line + "; no box changed");
            else if (tilted + tooThin > 0) Core.Log.Warning(line);
            else Core.Log.Msg(line);
        }

        /// <summary>
        /// Turns the convex mesh colliders the rule selects into plain ones. Physics cooks a plain collider from the
        /// mesh's CPU data, so an unreadable mesh is replaced by a readable copy first.
        /// </summary>
        private static void ExactCollision(BuiltExpansion b, SceneIndex.Part part, ObjectRule rule, Bounds area)
        {
            int changed = 0, copied = 0, moving = 0, failed = 0;
            var readable = new Dictionary<IntPtr, Mesh>();
            Mesh Readable(Mesh mesh)
            {
                if (mesh.isReadable) return mesh;
                if (readable.TryGetValue(mesh.Pointer, out var known)) return known;
                var copy = MeshCarver.ReadableCopy(mesh, mesh.name + " (MapExpansions exact)");
                if (copy != null)
                {
                    b.Own(copy);
                    copied++;
                }
                readable[mesh.Pointer] = copy;
                return copy;
            }
            foreach (var c in part.Colliders)
            {
                if (c == null || !c.enabled || c.isTrigger || !SceneIndex.MatchesSelfOrAncestor(c.transform, rule, area)) continue;
                var mc = c.TryCast<MeshCollider>();
                if (mc == null || !mc.convex) continue;
                var path = MeshCarver.Path(mc.transform);
                if (mc.attachedRigidbody != null && !mc.attachedRigidbody.isKinematic)
                {
                    moving++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: collider '{path}' sits on a moving body and stays convex");
                    continue;
                }
                var mesh = mc.sharedMesh;
                if (mesh == null)
                {
                    failed++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: collider '{path}' has no mesh and stays convex");
                    continue;
                }
                try
                {
                    var shape = Readable(mesh);
                    if (shape == null)
                    {
                        failed++;
                        Core.Log.Warning($"rules: '{b.Id}' {rule}: collider '{path}' has no readable triangles and stays convex");
                        continue;
                    }
                    if (shape != mesh) mc.sharedMesh = shape;
                    mc.convex = false;
                    changed++;
                    Core.Log.Msg($"rules: '{b.Id}' {rule}: collider '{path}' now uses its {mc.sharedMesh.vertexCount} vertices as a plain mesh");
                }
                catch (Exception e)
                {
                    failed++;
                    Core.Log.Error($"rules: '{b.Id}' {rule}: collider '{path}' could not be made plain, it stays convex: {e.Message}");
                }
            }
            string line = $"rules: '{b.Id}' {rule}: {changed} convex colliders made plain, {copied} readable copies, {moving} on moving bodies";
            if (failed > 0) Core.Log.Warning($"{line}; {failed} failed");
            else if (changed == 0) Core.Log.Warning(line + "; no convex mesh collider matched");
            else Core.Log.Msg(line);
        }

        private const float ShellThickness = 0.2f;

        /// <summary>
        /// Replaces every solid box collider the rule selects by a shell of thin boxes outside its faces, so the inside
        /// matches the faces drawn and the outside is <see cref="ShellThickness"/> larger. Faces are split around the
        /// openings that cross them. Boxes too thin to hold a room stay solid. A rule with a lining also gets inside walls
        /// and a ceiling, open at the openings and the windows.
        /// </summary>
        private static void Hollow(BuiltExpansion b, SceneIndex.Part part, ObjectRule rule, Bounds area, List<Bounds> openings, List<Bounds> windows,
            HashSet<IntPtr> offColliders)
        {
            int hollowed = 0, thin = 0, pieces = 0;
            var used = new HashSet<int>();
            var added = new List<Collider>();
            var liners = new List<Renderer>();
            bool lined = rule.Bands.Count > 0 || rule.CeilingMaterial != null || rule.FloorMaterial != null;
            foreach (var c in part.Colliders)
            {
                if (c == null || !c.enabled || c.isTrigger || !SceneIndex.MatchesSelfOrAncestor(c.transform, rule, area)) continue;
                var box = c.TryCast<BoxCollider>();
                if (box == null) continue;
                var t = box.transform;
                var path = MeshCarver.Path(t);
                var scale = new Vector3(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y), Mathf.Abs(t.lossyScale.z));
                var size = Vector3.Scale(box.size, scale);
                if (Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < ShellThickness * 4f || scale.x < 1e-4f || scale.y < 1e-4f || scale.z < 1e-4f)
                {
                    thin++;
                    Core.Log.Msg($"rules: '{b.Id}' {rule}: box '{path}' is {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m, too thin for a room; it stays solid");
                    continue;
                }
                var shell = new GameObject("MapExpansions.Shell " + box.name);
                shell.layer = box.gameObject.layer;
                shell.transform.SetParent(t, false);
                var holes = openings.Select(o => LocalBox(t, o)).ToList();
                var th = new Vector3(ShellThickness / scale.x, ShellThickness / scale.y, ShellThickness / scale.z);
                int before = pieces;
                for (int axis = 0; axis < 3; axis++)
                    for (int side = -1; side <= 1; side += 2)
                        pieces += ShellFace(shell, box, th, axis, side, holes, used, added);
                box.enabled = false;
                offColliders.Add(box.Pointer);
                hollowed++;
                if (lined)
                {
                    var liner = Line(b, rule, shell, box, scale, holes.Concat(windows.Select(w => LocalBox(t, w))).ToList(), path);
                    if (liner != null) liners.Add(liner);
                }
                Core.Log.Msg($"rules: '{b.Id}' {rule}: box '{path}' ({size.x:0.00} x {size.y:0.00} x {size.z:0.00} m) is now a shell of {pieces - before} boxes");
            }
            // The shell joins the index, so later rules and the clearing of the connections treat it like the box it replaces.
            part.Colliders.AddRange(added);
            part.Renderers.AddRange(liners);
            string line = $"rules: '{b.Id}' {rule}: {hollowed} boxes hollowed into {pieces} pieces, {thin} too thin, {used.Count} of {openings.Count} openings cut";
            if (lined) line += $", {liners.Count} lined";
            if (hollowed == 0) Core.Log.Warning(line + "; no box changed");
            else if (lined && liners.Count < hollowed) Core.Log.Warning(line + "; a lining is missing");
            else if (used.Count < openings.Count) Core.Log.Warning(line + "; an opening crosses no face");
            else Core.Log.Msg(line);
        }

        /// <summary>Adds the boxes of one face, along <paramref name="axis"/> on <paramref name="side"/>, in the box's local space.</summary>
        private static int ShellFace(GameObject shell, BoxCollider box, Vector3 th, int axis, int side, List<Bounds> holes, HashSet<int> used, List<Collider> added)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            var c = box.center;
            var s = box.size;
            float inner = c[axis] + side * s[axis] * 0.5f;
            float outer = inner + side * th[axis];
            float a0 = Mathf.Min(inner, outer), a1 = Mathf.Max(inner, outer);
            // The faces reach over the edges by the thickness, so the corners close.
            float u0 = c[u] - s[u] * 0.5f - th[u], u1 = c[u] + s[u] * 0.5f + th[u];
            float v0 = c[v] - s[v] * 0.5f - th[v], v1 = c[v] + s[v] * 0.5f + th[v];

            var cuts = new List<(float u0, float u1, float v0, float v1)>();
            for (int i = 0; i < holes.Count; i++)
            {
                var h = holes[i];
                if (h.max[axis] < a0 || h.min[axis] > a1) continue;
                float hu0 = Mathf.Max(h.min[u], u0), hu1 = Mathf.Min(h.max[u], u1);
                float hv0 = Mathf.Max(h.min[v], v0), hv1 = Mathf.Min(h.max[v], v1);
                if (hu1 <= hu0 || hv1 <= hv0) continue;
                cuts.Add((hu0, hu1, hv0, hv1));
                used.Add(i);
            }

            var stops = new List<float> { u0, u1 };
            foreach (var h in cuts)
            {
                stops.Add(h.u0);
                stops.Add(h.u1);
            }
            stops.Sort();
            int count = 0;
            for (int i = 0; i + 1 < stops.Count; i++)
            {
                float s0 = stops[i], s1 = stops[i + 1];
                if (s1 - s0 < 1e-4f) continue;
                float mid = (s0 + s1) * 0.5f;
                var gaps = cuts.Where(h => h.u0 <= mid && h.u1 >= mid).Select(h => (h.v0, h.v1)).OrderBy(g => g.v0).ToList();
                float from = v0;
                foreach (var (g0, g1) in gaps)
                {
                    if (g0 > from) count += ShellBox(shell, axis, u, v, a0, a1, s0, s1, from, g0, added);
                    from = Mathf.Max(from, g1);
                }
                if (v1 > from) count += ShellBox(shell, axis, u, v, a0, a1, s0, s1, from, v1, added);
            }
            return count;
        }

        private static int ShellBox(GameObject shell, int axis, int u, int v, float a0, float a1, float u0, float u1, float v0, float v1, List<Collider> added)
        {
            if (v1 - v0 < 1e-4f) return 0;
            var center = Vector3.zero;
            var size = Vector3.zero;
            center[axis] = (a0 + a1) * 0.5f;
            size[axis] = a1 - a0;
            center[u] = (u0 + u1) * 0.5f;
            size[u] = u1 - u0;
            center[v] = (v0 + v1) * 0.5f;
            size[v] = v1 - v0;
            var piece = shell.AddComponent<BoxCollider>();
            piece.center = center;
            piece.size = size;
            added.Add(piece);
            return 1;
        }

        // Metres a hole may stand off a face and still cut it, for a window box drawn around the glass.
        private const float HoleReach = 0.3f;

        /// <summary>Metres between the model's floor and the lining's floor.</summary>
        private const float FloorInset = 0.005f;

        private static readonly Dictionary<string, Material> LiningMaterials = new();

        /// <summary>
        /// Lines the inside of <paramref name="box"/> with walls in <see cref="ObjectRule.Bands"/> and a ceiling, one mesh
        /// with a submesh per material, cut around <paramref name="holes"/> (box-local). Returns its renderer, or null.
        /// </summary>
        private static Renderer Line(BuiltExpansion b, ObjectRule rule, GameObject shell, BoxCollider box, Vector3 scale, List<Bounds> holes, string path)
        {
            var t = box.transform;
            int up = 0;
            float upSign = 1f, best = -1f;
            for (int i = 0; i < 3; i++)
            {
                var axis = Vector3.zero;
                axis[i] = 1f;
                float d = Vector3.Dot(t.TransformDirection(axis).normalized, Vector3.up);
                if (Mathf.Abs(d) <= best) continue;
                best = Mathf.Abs(d);
                up = i;
                upSign = Mathf.Sign(d);
            }
            if (best < 0.9f)
            {
                Core.Log.Warning($"rules: '{b.Id}' {rule}: box '{path}' has no upright axis; it gets no lining");
                return null;
            }

            var c = box.center;
            var s = box.size;
            // The inset keeps the lining off the box faces, so the two never draw at the same depth.
            float inset = Mathf.Max(0.001f, rule.LiningInset);
            var ins = new Vector3(inset / scale.x, inset / scale.y, inset / scale.z);
            float floor = c[up] - upSign * s[up] * 0.5f;
            var bands = rule.Bands.OrderBy(x => x.From).ToList();

            var mats = new List<Material>();
            var slots = new Dictionary<string, int>();
            var tris = new List<List<int>>();
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            int Slot(string name)
            {
                if (name == null) return -1;
                if (slots.TryGetValue(name, out var i)) return i;
                var m = FindMaterial(b.Id, rule, name);
                slots[name] = i = m == null ? -1 : mats.Count;
                if (m == null) return -1;
                mats.Add(m);
                tris.Add(new List<int>());
                return i;
            }
            int BandAt(float local)
            {
                if (bands.Count == 0) return -1;
                float height = (local - floor) * upSign * scale[up];
                var band = bands[0];
                foreach (var x in bands)
                    if (x.From <= height) band = x;
                return Slot(band.Material);
            }

            int quads = 0;
            // One quad from four box-local corners, wound so it faces along the box-local direction 'facing'.
            void Quad(int slot, Vector3[] corners, Vector2[] uv, Vector3 facing)
            {
                int first = verts.Count;
                verts.AddRange(corners);
                uvs.AddRange(uv);
                var w0 = t.TransformPoint(corners[0]);
                bool front = Vector3.Dot(Vector3.Cross(t.TransformPoint(corners[1]) - w0, t.TransformPoint(corners[2]) - w0), t.TransformDirection(facing)) > 0f;
                tris[slot].AddRange(front
                    ? new[] { first, first + 1, first + 2, first, first + 2, first + 3 }
                    : new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
                quads++;
            }
            Vector3 At(int axis, float pa, int u, float pu, int v, float pv)
            {
                var p = Vector3.zero;
                p[axis] = pa;
                p[u] = pu;
                p[v] = pv;
                return p;
            }

            int frameSlot = Slot(rule.FrameMaterial);
            void Face(int axis, float plane, float inward, int u, int v, List<float> extraV, Func<float, int> slotAt, bool framed, bool cut = true)
            {
                float reach = HoleReach / scale[axis];
                var cross = cut ? holes.Where(h => h.min[axis] <= plane + reach && h.max[axis] >= plane - reach).ToList() : new List<Bounds>();
                float u0 = c[u] - s[u] * 0.5f + ins[u], u1 = c[u] + s[u] * 0.5f - ins[u];
                float v0 = c[v] - s[v] * 0.5f + ins[v], v1 = c[v] + s[v] * 0.5f - ins[v];
                framed &= frameSlot >= 0;
                float fu = framed ? rule.FrameWidth / scale[u] : 0f, fv = framed ? rule.FrameWidth / scale[v] : 0f;
                var us = new List<float> { u0, u1 };
                var vs = new List<float> { v0, v1 };
                foreach (var h in cross)
                {
                    us.Add(Mathf.Clamp(h.min[u], u0, u1));
                    us.Add(Mathf.Clamp(h.max[u], u0, u1));
                    vs.Add(Mathf.Clamp(h.min[v], v0, v1));
                    vs.Add(Mathf.Clamp(h.max[v], v0, v1));
                    if (!framed) continue;
                    us.Add(Mathf.Clamp(h.min[u] - fu, u0, u1));
                    us.Add(Mathf.Clamp(h.max[u] + fu, u0, u1));
                    vs.Add(Mathf.Clamp(h.min[v] - fv, v0, v1));
                    vs.Add(Mathf.Clamp(h.max[v] + fv, v0, v1));
                }
                vs.AddRange(extraV.Select(x => Mathf.Clamp(x, v0, v1)));
                us = us.Distinct().OrderBy(x => x).ToList();
                vs = vs.Distinct().OrderBy(x => x).ToList();
                bool InHole(float pu, float pv) => cross.Any(h => pu > h.min[u] && pu < h.max[u] && pv > h.min[v] && pv < h.max[v]);
                var normal = Vector3.zero;
                normal[axis] = inward;
                for (int i = 0; i + 1 < us.Count; i++)
                for (int j = 0; j + 1 < vs.Count; j++)
                {
                    float a0 = us[i], a1 = us[i + 1], b0 = vs[j], b1 = vs[j + 1];
                    if (a1 - a0 < 1e-4f || b1 - b0 < 1e-4f) continue;
                    float um = (a0 + a1) * 0.5f, vm = (b0 + b1) * 0.5f;
                    if (InHole(um, vm)) continue;
                    // A cell in the band around a hole is its frame, cut out of the wall rather than laid over it.
                    bool frame = framed && cross.Any(h => um > h.min[u] - fu && um < h.max[u] + fu && vm > h.min[v] - fv && vm < h.max[v] + fv);
                    int slot = frame ? frameSlot : slotAt(vm);
                    if (slot < 0) continue;
                    Quad(slot,
                        new[] { At(axis, plane, u, a0, v, b0), At(axis, plane, u, a1, v, b0), At(axis, plane, u, a1, v, b1), At(axis, plane, u, a0, v, b1) },
                        new[] { new Vector2(a0 * scale[u], b0 * scale[v]), new Vector2(a1 * scale[u], b0 * scale[v]), new Vector2(a1 * scale[u], b1 * scale[v]), new Vector2(a0 * scale[u], b1 * scale[v]) },
                        normal);
                }
                if (!framed) return;

                // The reveal closes the gap between the lining and the box face along each hole edge that lies on the wall,
                // so the edge of the outer model behind it does not show through.
                float face = plane - inward * ins[axis];
                foreach (var h in cross)
                {
                    float hu0 = Mathf.Clamp(h.min[u], u0, u1), hu1 = Mathf.Clamp(h.max[u], u0, u1);
                    float hv0 = Mathf.Clamp(h.min[v], v0, v1), hv1 = Mathf.Clamp(h.max[v], v0, v1);
                    if (hu1 - hu0 < 1e-4f || hv1 - hv0 < 1e-4f) continue;
                    foreach (var (onU, at, toward) in new[] { (true, hu0, 1f), (true, hu1, -1f), (false, hv0, 1f), (false, hv1, -1f) })
                    {
                        if (onU ? at <= u0 + 1e-4f || at >= u1 - 1e-4f : at <= v0 + 1e-4f || at >= v1 - 1e-4f) continue;
                        float e0 = onU ? hv0 : hu0, e1 = onU ? hv1 : hu1;
                        // An edge that runs inside another hole is open on both sides.
                        float outside = at - toward * 1e-3f;
                        if (onU ? InHole(outside, (e0 + e1) * 0.5f) : InHole((e0 + e1) * 0.5f, outside)) continue;
                        var facing = Vector3.zero;
                        facing[onU ? u : v] = toward;
                        Vector3 P(float depth, float along) => onU ? At(axis, depth, u, at, v, along) : At(axis, depth, u, along, v, at);
                        float sa = scale[axis], se = scale[onU ? v : u];
                        Quad(frameSlot,
                            new[] { P(plane, e0), P(face, e0), P(face, e1), P(plane, e1) },
                            new[] { new Vector2(plane * sa, e0 * se), new Vector2(face * sa, e0 * se), new Vector2(face * sa, e1 * se), new Vector2(plane * sa, e1 * se) },
                            facing);
                    }
                }
            }

            var bandStops = bands.Select(x => floor + upSign * x.From / scale[up]).ToList();
            var none = new List<float>();
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == up) continue;
                int along = 3 - up - axis;
                for (int side = -1; side <= 1; side += 2)
                    Face(axis, c[axis] + side * (s[axis] * 0.5f - ins[axis]), -side, along, up, bandStops, BandAt, true);
            }
            int ceiling = Slot(rule.CeilingMaterial);
            Face(up, c[up] + upSign * (s[up] * 0.5f - ins[up]), -upSign, (up + 1) % 3, (up + 2) % 3, none, _ => ceiling, false);
            // Doorways reach down to the floor, but the floor runs on through them up to the outer wall. It lies just
            // over the model's floor whatever the walls' inset, so flat things on that floor, such as door mats, stay on top.
            int floorSlot = Slot(rule.FloorMaterial);
            Face(up, floor + upSign * FloorInset / scale[up], upSign, (up + 1) % 3, (up + 2) % 3, none, _ => floorSlot, false, cut: false);

            if (quads == 0)
            {
                Core.Log.Warning($"rules: '{b.Id}' {rule}: box '{path}' got no lining; no material of it was found");
                return null;
            }
            var name = "MapExpansions.Lining " + box.name;
            var mesh = b.Own(new Mesh { name = name });
            mesh.vertices = verts.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.subMeshCount = tris.Count;
            for (int i = 0; i < tris.Count; i++)
                mesh.SetTriangles(tris[i].ToArray(), i);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.layer = box.gameObject.layer;
            go.transform.SetParent(shell.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats.ToArray();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            Core.Log.Msg($"rules: '{b.Id}' {rule}: box '{path}' lined with {quads} quads in {string.Join(", ", mats.Select(m => m.name))}");
            return mr;
        }

        /// <summary>A loaded material by name; the first found is kept for later builds while it lives.</summary>
        private static Material FindMaterial(string id, ObjectRule rule, string name)
        {
            var m = LoadedMaterial(name);
            if (m == null) Core.Log.Error($"rules: '{id}' {rule}: no loaded material '{name}'; the lining leaves its part out");
            return m;
        }

        /// <summary>A loaded material by name, or null; the first found is kept for later calls while it lives.</summary>
        internal static Material LoadedMaterial(string name)
        {
            if (LiningMaterials.TryGetValue(name, out var known) && known != null) return known;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>()))
            {
                var m = o.TryCast<Material>();
                if (m == null || m.name != name) continue;
                LiningMaterials[name] = m;
                return m;
            }
            return null;
        }

        /// <summary>A world box in the local space of <paramref name="t"/>, as the box around its eight corners there.</summary>
        private static Bounds LocalBox(Transform t, Bounds world)
        {
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? world.min.x : world.max.x, (i & 2) == 0 ? world.min.y : world.max.y, (i & 4) == 0 ? world.min.z : world.max.z);
                var p = t.InverseTransformPoint(corner);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        /// <summary>
        /// Cuts the triangles of the selected renderers and mesh colliders out of <paramref name="area"/>. Large cut
        /// triangles are drawn again outside it. A renderer whose cut fails is switched off, a collider whose cut fails
        /// stays whole. Without <paramref name="renderers"/> only the colliders are cut.
        /// </summary>
        private static void Cut(BuiltExpansion b, SceneIndex index, SceneIndex.Part part, ObjectRule rule, Bounds area, HashSet<IntPtr> offColliders, bool renderers)
        {
            var boxes = new List<Connections.OrientedBox> { new(area.center, Quaternion.identity, area.extents) };
            int carved = 0, triangles = 0, untouched = 0, failed = 0, inactive = 0, colCut = 0, colTris = 0, colFailed = 0;
            foreach (var r in renderers ? part.Renderers : Enumerable.Empty<Renderer>())
            {
                if (r == null || !r.enabled || !SceneIndex.MatchesSelfOrAncestor(r.transform, rule, area)) continue;
                // The patch of a switched-off object would be visible on its own.
                if (!r.gameObject.activeInHierarchy) { inactive++; continue; }
                var mr = r.TryCast<MeshRenderer>();
                if (mr == null)
                {
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: '{MeshCarver.Path(r.transform)}' is no mesh renderer and stays whole");
                    continue;
                }
                var outcome = MeshCarver.CarveRenderer(mr, boxes, index, b, out int cut);
                if (outcome == MeshCarver.Outcome.Carved)
                {
                    carved++;
                    triangles += cut;
                    Core.Log.Msg($"rules: '{b.Id}' {rule}: '{MeshCarver.Path(r.transform)}' {cut} triangles cut");
                }
                else if (outcome == MeshCarver.Outcome.Failed)
                {
                    r.enabled = false;
                    failed++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: '{MeshCarver.Path(r.transform)}' could not be cut and is switched off");
                }
                else untouched++;
            }
            foreach (var c in part.Colliders)
            {
                if (c == null || !c.enabled || c.isTrigger || !SceneIndex.MatchesSelfOrAncestor(c.transform, rule, area)) continue;
                var mc = c.TryCast<MeshCollider>();
                if (mc == null) continue;
                var outcome = MeshCarver.CarveCollider(mc, boxes, b, out int cut);
                if (outcome == MeshCarver.Outcome.Carved)
                {
                    colCut++;
                    colTris += cut;
                }
                else if (outcome == MeshCarver.Outcome.Untouched && !renderers)
                    Core.Log.Msg($"rules: '{b.Id}' {rule}: collider '{MeshCarver.Path(c.transform)}' has no triangle in the area");
                else if (outcome == MeshCarver.Outcome.Failed)
                {
                    colFailed++;
                    Core.Log.Warning($"rules: '{b.Id}' {rule}: collider '{MeshCarver.Path(c.transform)}' could not be cut and stays whole");
                }
            }
            string line = $"rules: '{b.Id}' {rule}: {carved} renderers cut ({triangles} triangles), {untouched} without a triangle in the area, " +
                          $"{failed} off after a failed cut, {inactive} inactive left as they are; {colCut} colliders cut ({colTris} triangles), {colFailed} failed";
            if (failed + colFailed > 0) Core.Log.Warning(line);
            else if (carved + colCut == 0) Core.Log.Warning(line + "; nothing was cut");
            else Core.Log.Msg(line);
        }

        /// <summary>
        /// True when the renderer is in no LOD group or in the coarsest level of its group. A back is mostly seen from
        /// far away, and backs of every level would draw on top of each other.
        /// </summary>
        private static bool IsCoarsestLod(Renderer r)
        {
            var group = r.GetComponentInParent<LODGroup>();
            if (group == null) return true;
            var lods = group.GetLODs();
            int level = -1, coarsest = -1;
            for (int i = 0; i < lods.Length; i++)
            {
                var rs = lods[i].renderers;
                if (rs == null || rs.Length == 0) continue;
                coarsest = i;
                foreach (var x in rs)
                    if (x != null && x.Pointer == r.Pointer) level = i;
            }
            // A renderer under a group but in none of its levels draws on its own.
            return level < 0 || level == coarsest;
        }

        private static Renderer AddBack(BuiltExpansion b, MeshRenderer mr)
        {
            var mesh = MeshCarver.BuildBack(mr, BackName + mr.name);
            if (mesh == null) return null;
            b.Own(mesh);
            var go = new GameObject(BackName + mr.name);
            go.layer = mr.gameObject.layer;
            go.transform.SetParent(mr.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var back = go.AddComponent<MeshRenderer>();
            back.sharedMaterials = mr.sharedMaterials;
            back.shadowCastingMode = ShadowCastingMode.Off;
            back.receiveShadows = mr.receiveShadows;
            return back;
        }

        internal const string BackName = "TwoSidedBack ";

        private static readonly int CullId = Shader.PropertyToID("_Cull");
        private const string TwoSidedSuffix = " (two-sided)";

        /// <summary>
        /// A large plate of the source (often statically batched, so it cannot be rescaled) is replaced by a plain quad
        /// over the plate's local area, with the same material and height. Its collider becomes a thin box over the
        /// same area.
        /// </summary>
        public static void GroundPlates(BuiltExpansion b, SceneIndex index, HashSet<IntPtr> offColliders)
        {
            var part = index.Expansion(b.Id);
            foreach (var plate in b.Definition.GroundPlates)
            {
                var r = part?.Renderers.FirstOrDefault(x => x != null && x.name == plate.Name);
                if (r == null)
                {
                    Core.Log.Error($"ground: '{b.Id}' has no renderer '{plate.Name}'; the plate stays as it is");
                    continue;
                }
                float y = r.bounds.center.y;
                var a = plate.LocalArea;
                Vector3[] local =
                {
                    new(a.xMin, 0f, a.yMin), new(a.xMin, 0f, a.yMax), new(a.xMax, 0f, a.yMax), new(a.xMax, 0f, a.yMin),
                };
                var go = new GameObject("MapExpansions.Ground." + plate.Name);
                go.transform.SetParent(b.Root.transform, false);
                go.layer = r.gameObject.layer;
                var mesh = b.Own(new Mesh { name = "MapExpansions.Ground." + plate.Name });
                var verts = new Vector3[4];
                var uvs = new Vector2[4];
                for (int i = 0; i < 4; i++)
                {
                    var w = b.Frame.ToWorld(local[i]);
                    w.y = y;
                    verts[i] = go.transform.InverseTransformPoint(w);
                    uvs[i] = new Vector2(local[i].x, local[i].z) / 10f;
                }
                mesh.vertices = verts;
                mesh.uv = uvs;
                // Clockwise seen from above, so the face points up.
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = r.sharedMaterials;
                r.enabled = false;

                int colliders = 0;
                foreach (var c in r.GetComponents<Collider>())
                {
                    if (!c.enabled) continue;
                    c.enabled = false;
                    offColliders.Add(c.Pointer);
                    colliders++;
                }
                if (colliders > 0)
                {
                    var box = go.AddComponent<BoxCollider>();
                    var lb = mesh.bounds;
                    box.center = lb.center - new Vector3(0f, 0.25f, 0f);
                    box.size = new Vector3(lb.size.x, 0.5f, lb.size.z);
                }
                var gb = mr.bounds;
                Core.Log.Msg($"ground: '{b.Id}' '{plate.Name}' replaced by a plate over x {gb.min.x:0}..{gb.max.x:0} z {gb.min.z:0}..{gb.max.z:0} at y {y:0.00}, " +
                             $"material {(r.sharedMaterial != null ? r.sharedMaterial.name : "none")}, {colliders} colliders replaced");
            }
        }

        /// <summary>Expansion terrain cells under a main-world terrain become holes, so the main world wins where both lie.</summary>
        public static void YieldToMainTerrains(BuiltExpansion b, SceneIndex index, TerrainOps.HoleMask holes)
        {
            var mainRects = new List<Rect>();
            foreach (var t in index.Main.Terrains)
            {
                if (t == null || t.terrainData == null || !t.enabled) continue;
                Vector3 p = t.transform.position;
                Vector3 s = t.terrainData.size;
                mainRects.Add(Rect.MinMaxRect(p.x, p.z, p.x + s.x, p.z + s.z));
            }
            var part = index.Expansion(b.Id);
            int cells = 0;
            foreach (var t in part.Terrains)
            {
                if (t == null) continue;
                foreach (var rect in mainRects)
                    cells += holes.Cut(t, rect, (x, z, h) => rect.Contains(new Vector2(x, z)), "under a main-world terrain");
            }
            Core.Log.Msg($"terrain: '{b.Id}' {cells} cells give way to {mainRects.Count} main-world terrains");
        }

        /// <summary>Reports terrain parts that end up lower than the definition allows.</summary>
        public static void CheckHeights(BuiltExpansion b, SceneIndex index)
        {
            var part = index.Expansion(b.Id);
            foreach (var t in part.Terrains)
            {
                if (t == null || t.terrainData == null) continue;
                float min = t.transform.position.y;
                if (min < b.Definition.MinimumWorldHeight)
                    Core.Log.Warning($"place: '{b.Id}' terrain '{t.name}' base lies at y {min:0.00}, below the allowed {b.Definition.MinimumWorldHeight}");
            }
        }

        internal static void Destroy(Object o)
        {
            if (o != null) Object.Destroy(o);
        }
    }
}
