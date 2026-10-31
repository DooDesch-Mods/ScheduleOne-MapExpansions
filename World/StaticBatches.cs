using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Statically batched meshes hold their vertices where the original stands, so a copy of them draws at the
    /// original. Each batch of a copy is pointed at a root moved like the copy, so the copy draws where it stands.
    /// </summary>
    internal static class StaticBatches
    {
        /// <summary>
        /// Which copy draws which part of a batch, by mesh and first submesh. Unity draws one part of a batch once a
        /// frame, so two copies of the same original never both show while both are in view; the second one drawn is
        /// missing, and which one that is changes with the view.
        /// </summary>
        private static readonly Dictionary<(IntPtr Mesh, int Start), (Transform Copy, string Name)> Drawn = new();

        /// <param name="copy">The copy's root; the new batch roots hang under it.</param>
        /// <param name="move">Takes a point of the original to the same point of the copy, in world space.</param>
        /// <returns>Batched renderers moved, batch roots made, and renderers whose batch root is part of the copy already.</returns>
        public static (int batched, int roots, int own) Follow(Transform copy, Matrix4x4 move, string name)
        {
            var roots = new Dictionary<IntPtr, Transform>();
            int batched = 0, own = 0;
            foreach (var mr in copy.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!mr.enabled || !mr.isPartOfStaticBatch) continue;
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
                Claim(copy, name, mr);
            }
            return (batched, roots.Count, own);
        }

        /// <summary>
        /// Gives up the parts a copy draws. A copy that is taken out is destroyed at the end of the frame, so without
        /// this a copy built anew in the same frame would find its own parts still held.
        /// </summary>
        internal static int Release(Transform copy)
        {
            if (copy == null) return 0;
            var mine = Drawn.Where(x => x.Value.Copy != null && x.Value.Copy.Pointer == copy.Pointer).Select(x => x.Key).ToList();
            foreach (var k in mine) Drawn.Remove(k);
            return mine.Count;
        }

        private static void Claim(Transform copy, string name, MeshRenderer mr)
        {
            var mesh = mr.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null)
            {
                Core.Log.Warning($"copies: batched renderer '{mr.name}' of '{name}' has no mesh; whether another copy shares its part is not checked");
                return;
            }
            var key = (mesh.Pointer, mr.subMeshStartIndex);
            if (Drawn.TryGetValue(key, out var first) && first.Copy != null && first.Copy != copy)
            {
                Core.Log.Warning($"copies: '{name}' and '{first.Name}' draw the same part of '{mesh.name}' (renderer '{mr.name}', " +
                                 $"submesh {mr.subMeshStartIndex}); while both are in view one of them is missing. Copy them from different originals.");
                return;
            }
            Drawn[key] = (copy, name);
        }
    }
}
