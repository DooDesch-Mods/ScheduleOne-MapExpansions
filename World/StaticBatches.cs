using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Statically batched meshes hold their vertices where the original stands, so a copy of them draws at the
    /// original. Each batch of a copy is pointed at a root moved like the copy, so the copy draws where it stands.
    /// </summary>
    internal static class StaticBatches
    {
        /// <param name="copy">The copy's root; the new batch roots hang under it.</param>
        /// <param name="move">Takes a point of the original to the same point of the copy, in world space.</param>
        /// <returns>Batched renderers moved, batch roots made, and renderers whose batch root is part of the copy already.</returns>
        public static (int batched, int roots, int own) Follow(Transform copy, Matrix4x4 move, string name)
        {
            var roots = new Dictionary<IntPtr, Transform>();
            int batched = 0, own = 0;
            foreach (var mr in copy.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!mr.isPartOfStaticBatch) continue;
                var orig = mr.staticBatchRootTransform;
                if (orig != null && orig.IsChildOf(copy))
                {
                    own++;
                    continue;
                }
                var key = orig == null ? IntPtr.Zero : orig.Pointer;
                if (!roots.TryGetValue(key, out var root))
                {
                    var m = move * (orig == null ? Matrix4x4.identity : orig.localToWorldMatrix);
                    root = new GameObject($"{name} batch root {roots.Count}").transform;
                    root.SetParent(copy, false);
                    // Relative to the copy the root is a proper frame, also when the copy is mirrored.
                    var local = copy.worldToLocalMatrix * m;
                    root.localPosition = local.GetColumn(3);
                    root.localRotation = local.rotation;
                    root.localScale = local.lossyScale;
                    roots[key] = root;
                }
                mr.staticBatchRootTransform = root;
                batched++;
            }
            return (batched, roots.Count, own);
        }
    }
}
