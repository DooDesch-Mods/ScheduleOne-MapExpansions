using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    internal static class IndependentCopies
    {
        internal static MeshRenderer Renderer(MeshRenderer source, Transform parent, Mesh mesh, string name)
        {
            var go = new GameObject(name) { layer = source.gameObject.layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = source.sharedMaterials;
            mr.shadowCastingMode = source.shadowCastingMode;
            mr.receiveShadows = source.receiveShadows;
            mr.renderingLayerMask = source.renderingLayerMask;
            mr.lightProbeUsage = source.lightProbeUsage;
            mr.reflectionProbeUsage = source.reflectionProbeUsage;
            mr.enabled = source.enabled;
            return mr;
        }

        internal static void Extract(BuiltExpansion b, Transform source, GameObject copy, bool ownMesh)
        {
            var original = source.GetComponentsInChildren<MeshRenderer>(true);
            var cloned = copy.GetComponentsInChildren<MeshRenderer>(true);
            if (original.Length != cloned.Length) throw new InvalidOperationException("independent copy renderer hierarchy does not match its source");
            for (int i = 0; i < original.Length; i++)
            {
                var src = original[i];
                var dst = cloned[i];
                if (!src.isPartOfStaticBatch || (!ownMesh && dst.transform == copy.transform)) continue;
                var mesh = MeshCarver.BuildForward(src, $"MapExpansions.{b.Id}.{src.name}.Independent");
                if (mesh == null) throw new InvalidOperationException($"renderer '{MeshCarver.Path(src.transform)}' has no extractable selected submeshes");
                b.Own(mesh);
                var replacement = Renderer(src, dst.transform, mesh, "MapExpansions independent visual");
                foreach (var group in copy.GetComponentsInChildren<LODGroup>(true))
                {
                    var levels = group.GetLODs();
                    for (int level = 0; level < levels.Length; level++)
                    {
                        var lod = levels[level];
                        var renderers = lod.renderers;
                        for (int j = 0; j < renderers.Length; j++)
                            if (renderers[j] != null && renderers[j].Pointer == dst.Pointer) renderers[j] = replacement;
                        lod.renderers = renderers;
                        levels[level] = lod;
                    }
                    group.SetLODs(levels);
                }
                dst.enabled = false;
                Core.Log.Msg($"independent copy: '{src.name}' extracted {mesh.subMeshCount} selected submeshes, local bounds {mesh.bounds.size}");
            }
        }
    }
}
