using MapExpansions.Connections;
using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>Reversible cuts use independent selected geometry; shared original meshes are never changed.</summary>
    internal static class StateCuts
    {
        private static readonly HashSet<IntPtr> RenderersInUse = new();
        private static readonly HashSet<IntPtr> CollidersInUse = new();
        internal sealed class Lease : IDisposable
        {
            internal BuiltExpansion Owner;
            internal readonly List<(Renderer renderer, bool enabled)> Renderers = new();
            internal readonly List<(Collider collider, bool enabled, Mesh mesh, bool convex)> Colliders = new();
            internal readonly List<GameObject> Objects = new();
            internal readonly List<Object> Assets = new();
            /// <summary>Takes the replacements out of the LOD groups they joined, before they are destroyed.</summary>
            internal readonly List<Action> Undo = new();
            public void Dispose()
            {
                foreach (var undo in Undo)
                {
                    try { undo(); }
                    catch (Exception e) { Core.Log.Warning($"state cut: taking a replacement out of its LOD group failed: {e.Message}"); }
                }
                Undo.Clear();
                foreach (var go in Objects) if (go != null) { go.SetActive(false); Object.Destroy(go); }
                Objects.Clear();
                foreach (var (r, enabled) in Renderers)
                    if (r != null) { r.enabled = enabled; RenderersInUse.Remove(r.Pointer); }
                Renderers.Clear();
                foreach (var (c, enabled, mesh, convex) in Colliders)
                {
                    if (c == null) continue;
                    if (c.TryCast<MeshCollider>() is MeshCollider mc) { mc.sharedMesh = mesh; mc.convex = convex; }
                    c.enabled = enabled;
                    CollidersInUse.Remove(c.Pointer);
                }
                Colliders.Clear();
                foreach (var asset in Assets)
                    if (asset != null) { Owner.Owned.Remove(asset); Object.Destroy(asset); }
                Assets.Clear();
                Physics.SyncTransforms();
            }
        }

        /// <param name="offColliders">Gets the source colliders the cut switched off; the caller takes them out of their groups once per transition.</param>
        internal static Lease Apply(BuiltExpansion b, SceneIndex index, ObjectRule rule, HashSet<IntPtr> offColliders)
        {
            if (index == null || rule.Area.size == Vector3.zero) throw new InvalidOperationException("state cut needs a scene index and area");
            var part = index.Of(WorldSide.Main, b);
            if (part == null) throw new InvalidOperationException("state cut has no main-world index");
            var lease = new Lease { Owner = b };
            int ownedBefore = b.Owned.Count, changed = 0, hidden = 0;
            // A renderer is cut once per state, so further areas of the same rule go into the same pass.
            var areas = new List<Bounds> { rule.Area };
            areas.AddRange(rule.Openings.Where(o => o.size != Vector3.zero));
            var boxes = areas.Select(a => new OrientedBox(a.center, Quaternion.identity, a.extents)).ToArray();
            // Visibility and runtime bounds are not scene geometry readiness. The independent cut tests the actual
            // selected mesh or local collider shape against the area, including inactive client scene objects.
            var selector = rule.Clone();
            selector.Area = default;
            try
            {
                foreach (var r in rule.Effect == ObjectEffect.Cut ? part.Renderers : Enumerable.Empty<Renderer>())
                {
                    if (r == null || !SceneIndex.MatchesSelfOrAncestor(r.transform, selector, default)) continue;
                    // A switched-off renderer draws nothing, often a collision helper with a default material.
                    // Its replacement would show up as a plain wall in front of the real one.
                    if (!r.enabled) { hidden++; continue; }
                    if (RenderersInUse.Contains(r.Pointer)) throw new InvalidOperationException($"another active state already cuts '{MeshCarver.Path(r.transform)}'");
                    var mr = r.TryCast<MeshRenderer>();
                    if (mr == null) throw new InvalidOperationException($"state cut renderer '{r.name}' is not a MeshRenderer");
                    var mesh = b.Own(MeshCarver.BuildForward(mr, $"MapExpansions.{b.Id}.{r.name}.StateCut"));
                    if (mesh == null) throw new InvalidOperationException($"state cut renderer '{r.name}' has no selected mesh");
                    var replacement = IndependentCopies.Renderer(mr, b.Root.transform, mesh, "MapExpansions state cut " + r.name);
                    replacement.transform.SetPositionAndRotation(mr.transform.position, mr.transform.rotation);
                    replacement.transform.localScale = mr.transform.lossyScale;
                    lease.Objects.Add(replacement.gameObject);
                    replacement.enabled = true;
                    var outcome = MeshCarver.CarveRenderer(replacement, boxes, index, b, out int triangles, replacement.transform);
                    if (outcome == MeshCarver.Outcome.Failed) throw new InvalidOperationException($"state cut renderer '{r.name}' failed; see carver cause above");
                    if (outcome == MeshCarver.Outcome.Untouched)
                    { replacement.gameObject.SetActive(false); Object.Destroy(replacement.gameObject); continue; }
                    // The copy and its patch stand in for the original, so they are drawn only at the original's LOD
                    // distances; a far-distance proxy box would otherwise cover the building from close up.
                    int levels = MeshPatch.JoinLod(mr, replacement, b, lease.Undo);
                    foreach (var piece in replacement.GetComponentsInChildren<MeshRenderer>(true))
                        if (piece.Pointer != replacement.Pointer) MeshPatch.JoinLod(mr, piece, b, lease.Undo);
                    lease.Renderers.Add((r, r.enabled));
                    RenderersInUse.Add(r.Pointer);
                    r.enabled = false;
                    changed++;
                    Core.Log.Msg($"state cut: '{b.Id}' '{r.name}' cut {triangles} triangles on independent geometry, original retained" +
                                 (levels > 0 ? $", drawn in {levels} LOD levels of its group" : ", no LOD group"));
                }
                foreach (var c in part.Colliders)
                {
                    if (c == null || c.isTrigger || !SceneIndex.MatchesSelfOrAncestor(c.transform, selector, default)) continue;
                    if (CollidersInUse.Contains(c.Pointer)) throw new InvalidOperationException($"another active state already cuts collider '{MeshCarver.Path(c.transform)}'");
                    if (c.TryCast<BoxCollider>() is BoxCollider box)
                    {
                        // A box minus one box stays a set of boxes; minus two overlapping ones it would need a second pass.
                        var touched = areas.Where(a => Overlap(box, a, out _, out _)).ToList();
                        if (touched.Count > 1)
                            throw new InvalidOperationException($"state cut box '{MeshCarver.Path(box.transform)}' lies in {touched.Count} areas of '{rule.Key}'; a box collider can be cut by one area");
                        if (touched.Count == 1 && SubtractBox(box, touched[0], lease)) changed++;
                    }
                    else if (c.TryCast<MeshCollider>() is MeshCollider mc)
                    {
                        if (mc.sharedMesh == null) continue;
                        if (mc.attachedRigidbody != null && !mc.attachedRigidbody.isKinematic)
                            throw new InvalidOperationException($"state cut collider '{c.name}' is attached to a moving body");
                        var go = IndependentFrame(b, mc.transform, "MapExpansions state cut collider");
                        lease.Objects.Add(go);
                        var replacement = go.AddComponent<MeshCollider>();
                        replacement.sharedMesh = mc.sharedMesh;
                        replacement.convex = mc.convex;
                        replacement.sharedMaterial = mc.sharedMaterial;
                        replacement.contactOffset = mc.contactOffset;
                        var outcome = MeshCarver.CarveCollider(replacement, boxes, b, out _);
                        if (outcome == MeshCarver.Outcome.Failed) throw new InvalidOperationException($"state cut collider '{c.name}' failed; see carver cause above");
                        if (outcome == MeshCarver.Outcome.Untouched)
                        { go.SetActive(false); Object.Destroy(go); continue; }
                        lease.Colliders.Add((c, c.enabled, mc.sharedMesh, mc.convex));
                        CollidersInUse.Add(c.Pointer);
                        c.enabled = false;
                        changed++;
                    }
                    else throw new InvalidOperationException($"state cut collider '{c.name}' is neither box nor mesh");
                }
                if (changed == 0) throw new InvalidOperationException($"state cut '{rule.Key}' changed no geometry in its area");
                lease.Assets.AddRange(b.Owned.Skip(ownedBefore));
                foreach (var (source, _, _, _) in lease.Colliders) offColliders.Add(source.Pointer);
                Physics.SyncTransforms();
                Core.Log.Msg($"state cut: '{b.Id}' '{rule.Key}' applied {changed} geometry changes from selected source data; visibility-independent replacements active, {hidden} switched-off renderers stay off");
                return lease;
            }
            catch (Exception original)
            {
                lease.Assets.AddRange(b.Owned.Skip(ownedBefore));
                try { lease.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException("state cut and cleanup failed", original, cleanup); }
                throw;
            }
        }

        private static GameObject IndependentFrame(BuiltExpansion owner, Transform source, string name)
        {
            var go = new GameObject(name) { layer = source.gameObject.layer };
            go.transform.SetParent(owner.Root.transform, false);
            go.transform.SetPositionAndRotation(source.position, source.rotation);
            go.transform.localScale = source.lossyScale;
            return go;
        }

        // The part of the box inside the area, in the box frame.
        private static bool Overlap(BoxCollider source, Bounds area, out Vector3 lo, out Vector3 hi)
        {
            var t = source.transform;
            var local = new Bounds(t.InverseTransformPoint(area.min), Vector3.zero);
            for (int i = 0; i < 8; i++) local.Encapsulate(t.InverseTransformPoint(new Vector3(
                (i & 1) == 0 ? area.min.x : area.max.x, (i & 2) == 0 ? area.min.y : area.max.y, (i & 4) == 0 ? area.min.z : area.max.z)));
            lo = Vector3.Max(source.center - source.size * 0.5f, local.min);
            hi = Vector3.Min(source.center + source.size * 0.5f, local.max);
            // The area's corners in the box frame enclose the area for any rotation, so a box outside them is outside the
            // area. Such a box stays untouched, whatever its rotation; a selector by name also reaches boxes far away.
            return hi.x - lo.x > 0.0001f && hi.y - lo.y > 0.0001f && hi.z - lo.z > 0.0001f;
        }

        private static bool SubtractBox(BoxCollider source, Bounds area, Lease lease)
        {
            var t = source.transform;
            var min = source.center - source.size * 0.5f;
            var max = source.center + source.size * 0.5f;
            if (!Overlap(source, area, out var lo, out var hi)) return false;
            // An axis-aligned cut remains rectangular in the box frame only for cardinal rotations.
            foreach (var axis in new[] { t.right, t.up, t.forward })
                if (Mathf.Max(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)) < 0.9999f)
                    throw new InvalidOperationException($"state cut box '{MeshCarver.Path(t)}' is not cardinally rotated and lies in the cut area");
            lease.Colliders.Add((source, source.enabled, null, false));
            CollidersInUse.Add(source.Pointer);
            void Slab(Vector3 a, Vector3 z)
            {
                var size = z - a;
                if (size.x <= 0.0001f || size.y <= 0.0001f || size.z <= 0.0001f) return;
                var go = IndependentFrame(lease.Owner, t, "MapExpansions state cut slab");
                lease.Objects.Add(go);
                var slab = go.AddComponent<BoxCollider>();
                slab.center = (a + z) * 0.5f;
                slab.size = size;
                slab.sharedMaterial = source.sharedMaterial;
                slab.contactOffset = source.contactOffset;
            }
            foreach (var slab in BoxDifference.Subtract(
                new(min.x, min.y, min.z, max.x, max.y, max.z),
                new(lo.x, lo.y, lo.z, hi.x, hi.y, hi.z)))
                Slab(new Vector3(slab.MinX, slab.MinY, slab.MinZ), new Vector3(slab.MaxX, slab.MaxY, slab.MaxZ));
            source.enabled = false;
            return true;
        }

        internal static void Reset() { RenderersInUse.Clear(); CollidersInUse.Clear(); }
    }
}
