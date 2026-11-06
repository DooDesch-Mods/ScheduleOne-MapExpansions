using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Moves a main-world object itself while a state stands, and puts it back when the state leaves. A copy would draw
    /// the object but lose what it does; the moved object keeps its components, interactions and network behaviours.
    /// </summary>
    internal static class ObjectMoves
    {
        internal sealed class Lease : IDisposable
        {
            private readonly Transform _target;
            private readonly Vector3 _position;
            private readonly Quaternion _rotation;
            private readonly List<(MeshRenderer Renderer, Transform Root)> _batches = new();
            private readonly List<GameObject> _roots = new();
            private bool _disposed;

            internal Lease(Transform target, Matrix4x4 move, string name)
            {
                _target = target;
                _position = target.position;
                _rotation = target.rotation;
                foreach (var renderer in target.GetComponentsInChildren<MeshRenderer>(true))
                    if (renderer.isPartOfStaticBatch) _batches.Add((renderer, renderer.staticBatchRootTransform));
                var before = new HashSet<IntPtr>();
                for (int i = 0; i < target.childCount; i++) before.Add(target.GetChild(i).Pointer);

                target.SetPositionAndRotation(move.MultiplyPoint3x4(_position), move.rotation * _rotation);
                // A batched mesh keeps its vertices where it was built, so its batch root has to move along.
                var (batched, roots, own) = StaticBatches.Follow(target, move, name);
                for (int i = 0; i < target.childCount; i++)
                    if (!before.Contains(target.GetChild(i).Pointer)) _roots.Add(target.GetChild(i).gameObject);
                Core.Log.Msg($"moves: '{name}' to {target.position}: {batched} batched renderers on {roots} new batch roots, {own} on their own");
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_target == null)
                {
                    Core.Log.Warning("moves: an object moved by a state is gone; nothing to put back");
                    return;
                }
                StaticBatches.Release(_target);
                foreach (var (renderer, root) in _batches)
                    if (renderer != null) renderer.staticBatchRootTransform = root;
                foreach (var root in _roots)
                    if (root != null) UnityEngine.Object.Destroy(root);
                _target.SetPositionAndRotation(_position, _rotation);
            }
        }

        /// <summary>The lease of the move, or null when an optional move found no object.</summary>
        internal static Lease Apply(string owner, ObjectMoveDefinition move)
        {
            var target = MainCopies.FindMain(move.Path);
            if (target == null)
            {
                if (move.Optional)
                {
                    Core.Log.Msg($"moves: '{owner}/{move.Id}': no object at '{move.Path}', the optional move is skipped");
                    return null;
                }
                throw new InvalidOperationException($"move '{move.Id}': no main-world object at '{move.Path}'");
            }
            return new Lease(target, Matrix4x4.Translate(move.Offset), owner + "/" + move.Id);
        }
    }
}
