using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;

namespace MapExpansions.Connections
{
    /// <summary>Finds the materials a connection borrows from the worlds, and makes the plain ones it needs itself.</summary>
    internal static class MaterialResolver
    {
        private const string LitShader = "Universal Render Pipeline/Lit";

        public readonly struct Resolved
        {
            public readonly Material Material;

            /// <summary>Layer of the renderer the material came from, or -1 for a fallback.</summary>
            public readonly int Layer;

            public readonly float Tiling;

            public Resolved(Material material, int layer, float tiling)
            {
                Material = material;
                Layer = layer;
                Tiling = tiling;
            }
        }

        public static Resolved Resolve(MaterialRef r, string role, BuiltExpansion b, SceneIndex index)
        {
            if (r == null)
            {
                Core.Log.Msg($"material: '{b.Id}' {role}: none given, plain grey");
                return new Resolved(Plain(new Color(0.45f, 0.45f, 0.45f), b), -1, 4f);
            }
            var part = index.Of(r.Side, b);
            if (part != null)
            {
                foreach (var renderer in part.Renderers)
                {
                    if (renderer == null || !SceneIndex.Matches(renderer.transform, r.Renderer, r.Parent)) continue;
                    var mats = renderer.sharedMaterials;
                    if (r.Slot < 0 || r.Slot >= mats.Length || mats[r.Slot] == null)
                    {
                        Core.Log.Warning($"material: '{b.Id}' {role}: {r} has {mats.Length} slots; slot {r.Slot} is empty");
                        continue;
                    }
                    Core.Log.Msg($"material: '{b.Id}' {role}: '{mats[r.Slot].name}' from {MeshCarver.Path(renderer.transform)}");
                    return new Resolved(mats[r.Slot], renderer.gameObject.layer, Mathf.Max(0.1f, r.Tiling));
                }
            }
            Core.Log.Warning($"material: '{b.Id}' {role}: {r} not found; plain colour {r.Fallback}");
            return new Resolved(Plain(r.Fallback, b), -1, Mathf.Max(0.1f, r.Tiling));
        }

        public static Material Plain(Color color, BuiltExpansion owner)
        {
            var shader = Shader.Find(LitShader);
            if (shader == null)
            {
                Core.Log.Error($"material: shader '{LitShader}' not found; the default material is used");
                return null;
            }
            var m = owner.Own(new Material(shader) { name = "MapExpansions.Plain" });
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", 0.15f);
            return m;
        }

        /// <summary>A material that glows in the given colour, for light fixtures.</summary>
        public static Material Emissive(Color color, float strength, BuiltExpansion owner)
        {
            var m = Plain(color, owner);
            if (m == null) return null;
            m.name = "MapExpansions.Lamp";
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", color * strength);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return m;
        }
    }
}
