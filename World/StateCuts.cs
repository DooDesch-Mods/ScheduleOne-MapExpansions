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
            public void Dispose()
            {
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
            var boxes = new[] { new OrientedBox(rule.Area.center, Quaternion.identity, rule.Area.extents) };
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
                    lease.Renderers.Add((r, r.enabled));
                    RenderersInUse.Add(r.Pointer);
                    r.enabled = false;
                    changed++;
                    Core.Log.Msg($"state cut: '{b.Id}' '{r.name}' cut {triangles} triangles on independent geometry, original retained");
                }
                foreach (var c in part.Colliders)
                {
                    if (c == null || c.isTrigger || !SceneIndex.MatchesSelfOrAncestor(c.transform, selector, default)) continue;
                    if (CollidersInUse.Contains(c.Pointer)) throw new InvalidOperationException($"another active state already cuts collider '{MeshCarver.Path(c.transform)}'");
                    if (c.TryCast<BoxCollider>() is BoxCollider box)
                    {
                        if (SubtractBox(box, rule.Area, lease)) changed++;
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

        private static bool SubtractBox(BoxCollider source, Bounds area, Lease lease)
        {
            var t = source.transform;
            // An axis-aligned cut remains rectangular in the box frame only for cardinal rotations.
            foreach (var axis in new[] { t.right, t.up, t.forward })
                if (Mathf.Max(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)) < 0.9999f)
                    throw new InvalidOperationException($"state cut box '{source.name}' is not cardinally rotated");
            var local = new Bounds(t.InverseTransformPoint(area.min), Vector3.zero);
            for (int i = 0; i < 8; i++) local.Encapsulate(t.InverseTransformPoint(new Vector3(
                (i & 1) == 0 ? area.min.x : area.max.x, (i & 2) == 0 ? area.min.y : area.max.y, (i & 4) == 0 ? area.min.z : area.max.z)));
            var min = source.center - source.size * 0.5f;
            var max = source.center + source.size * 0.5f;
            var lo = Vector3.Max(min, local.min);
            var hi = Vector3.Min(max, local.max);
            if (hi.x - lo.x <= 0.0001f || hi.y - lo.y <= 0.0001f || hi.z - lo.z <= 0.0001f) return false;
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
