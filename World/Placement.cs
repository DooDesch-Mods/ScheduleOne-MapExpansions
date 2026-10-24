using MapExpansions.Lifecycle;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>Puts a copy of the captured source into the main scene at the expansion's offset and rotation.</summary>
    internal static class Placement
    {
        public static bool Place(BuiltExpansion b, Scene main)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var src = b.Source?.Root;
            if (src == null)
            {
                Core.Log.Error($"place: '{b.Id}' has no captured root");
                return false;
            }

            var root = new GameObject("MapExpansions." + b.Id);
            SceneManager.MoveGameObjectToScene(root, main);
            root.transform.SetPositionAndRotation(b.Frame.Offset, b.Frame.Rotation);
            b.Root = root;

            // The captured root sits at the cache's origin, so its local pose is its pose in the source scene.
            var copy = Object.Instantiate(src, root.transform, false);
            copy.name = src.name;
            b.Content = copy;
            long cloneMs = sw.ElapsedMilliseconds;

            // Statically batched meshes hold their vertices in source-scene world space. Pointing the batch at the
            // placed root moves them with it.
            int batched = 0, rerooted = 0, failed = 0, lightmapped = 0;
            string firstError = null;
            foreach (var mr in copy.GetComponentsInChildren<MeshRenderer>(true))
            {
                // The source scene's lightmaps are not loaded; an index would point into the main world's set.
                if (mr.lightmapIndex >= 0 && mr.lightmapIndex < 65534)
                {
                    mr.lightmapIndex = -1;
                    lightmapped++;
                }
                if (!mr.isPartOfStaticBatch) continue;
                batched++;
                try
                {
                    mr.staticBatchRootTransform = root.transform;
                    if (mr.staticBatchRootTransform == root.transform) rerooted++;
                    else failed++;
                }
                catch (Exception e)
                {
                    failed++;
                    firstError ??= e.Message;
                }
            }

            // A terrain ignores its rotation and renders axis-aligned, so only its data turns.
            int terrains = 0, turned = 0;
            foreach (var t in copy.GetComponentsInChildren<Terrain>(true))
            {
                terrains++;
                t.transform.rotation = Quaternion.identity;
                if (TerrainOps.Rotate(t, b.Frame, b)) turned++;
            }

            string line = $"place: '{b.Id}' at {b.Frame} in {sw.ElapsedMilliseconds} ms (copy {cloneMs} ms): {batched} batched renderers, " +
                          $"{rerooted} follow the root, {failed} failed; {lightmapped} lightmap indices cleared; {turned}/{terrains} terrains turned";
            bool ok = failed == 0 && turned == terrains;
            if (ok) Core.Log.Msg(line);
            else Core.Log.Error(line + (firstError != null ? $"; first error: {firstError}" : ""));
            return ok;
        }
    }
}
