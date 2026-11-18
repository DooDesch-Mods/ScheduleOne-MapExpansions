using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Terrain layers a world state paints onto the main terrain, such as a trodden path or a gravel patch. The splat
    /// map is changed in place on the GPU and the painted texels are kept, so the state's removal and the world's
    /// unload put the original weights back.
    /// </summary>
    internal static class StatePaint
    {
        private static readonly List<Lease> Active = new();

        internal sealed class Painted
        {
            internal Terrain Terrain;
            internal string TerrainName;
            internal int Texture;
            internal RectInt Region;
            internal Color32[] Original;
            internal bool[] Changed;
            internal int Texels;
        }

        internal sealed class Lease : IDisposable
        {
            internal string Owner;
            internal readonly List<Painted> Painted = new();

            /// <summary>
            /// Writes the kept texels back, last painted first. Only the texels this paint changed go back: the rest of
            /// the region may carry another state's paint since, which stays.
            /// </summary>
            public void Dispose()
            {
                var errors = new List<string>();
                for (int i = Painted.Count - 1; i >= 0; i--)
                {
                    var p = Painted[i];
                    try
                    {
                        if (p.Terrain == null || p.Terrain.terrainData == null)
                            Core.Log.Msg($"terrain paint: {Owner}: '{p.TerrainName}' is gone; nothing to put back");
                        else
                        {
                            var now = Read(p.Terrain.terrainData, p.Texture, p.Region);
                            for (int k = 0; k < now.Length && k < p.Changed.Length; k++)
                                if (p.Changed[k]) now[k] = p.Original[k];
                            Write(p.Terrain.terrainData, p.Texture, p.Region, now);
                            p.Terrain.terrainData.SyncTexture(TerrainData.AlphamapTextureName);
                            Core.Log.Msg($"terrain paint: {Owner}: {p.Texels} texels back on '{p.TerrainName}'");
                        }
                    }
                    catch (Exception e)
                    {
                        RenderTexture.active = null;
                        errors.Add($"'{p.TerrainName}': {e.Message}");
                    }
                    Painted.RemoveAt(i);
                }
                Active.Remove(this);
                if (errors.Count > 0) throw new InvalidOperationException($"terrain paint: {Owner}: not all texels came back ({string.Join("; ", errors)})");
            }
        }

        /// <summary>
        /// Paints the rule's <see cref="ObjectRule.PaintLayer"/> on every main terrain the rule's name selects: along
        /// <see cref="ObjectRule.PaintPath"/> when it has points, otherwise over the ellipse that fills the area.
        /// </summary>
        internal static Lease Apply(BuiltExpansion b, SceneIndex index, ObjectRule rule, string owner)
        {
            if (index == null) throw new InvalidOperationException($"{owner}: painting needs a scene index");
            if (string.IsNullOrEmpty(rule.PaintLayer)) throw new InvalidOperationException($"{owner}: painting needs a terrain layer name");
            var part = index.Of(WorldSide.Main, b) ?? throw new InvalidOperationException($"{owner}: painting has no main-world index");
            Rect rect = PaintRect(rule);
            if (rect.width <= 0f || rect.height <= 0f) throw new InvalidOperationException($"{owner}: painting needs an area or a path");
            var lease = new Lease { Owner = owner };
            int matched = 0;
            try
            {
                foreach (var t in part.Terrains)
                {
                    if (t == null || t.terrainData == null) continue;
                    if (!SceneIndex.Matches(t.transform, rule.Name ?? "", rule.Parent)) continue;
                    var tp = t.transform.position;
                    var ts = t.terrainData.size;
                    if (!rect.Overlaps(Rect.MinMaxRect(tp.x, tp.z, tp.x + ts.x, tp.z + ts.z))) continue;
                    matched++;
                    var painted = Paint(t, rule, rect, owner);
                    lease.Painted.Add(painted);
                    Core.Log.Msg($"terrain paint: {owner}: {painted.Texels} texels of '{rule.PaintLayer}' on '{t.name}' " +
                                 $"(x {rect.xMin:0.0}..{rect.xMax:0.0}, z {rect.yMin:0.0}..{rect.yMax:0.0})");
                }
            }
            catch
            {
                lease.Dispose();
                throw;
            }
            if (matched == 0) throw new InvalidOperationException($"{owner}: no main terrain named '{rule.Name}' lies under the paint");
            Active.Add(lease);
            return lease;
        }

        /// <summary>Before the world goes: the terrain data outlives the scene in memory, so the weights go back.</summary>
        internal static void Reset()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var lease = Active[i];
                try { lease.Dispose(); }
                catch (Exception e) { Core.Log.Warning(e.Message); }
            }
            Active.Clear();
        }

        private static Rect PaintRect(ObjectRule rule)
        {
            if (rule.PaintPath.Count == 0)
                return Rect.MinMaxRect(rule.Area.min.x, rule.Area.min.z, rule.Area.max.x, rule.Area.max.z);
            float r = rule.PaintWidth * 0.5f + 0.5f;
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            foreach (var p in rule.PaintPath)
            {
                x0 = Mathf.Min(x0, p.x - r); x1 = Mathf.Max(x1, p.x + r);
                z0 = Mathf.Min(z0, p.y - r); z1 = Mathf.Max(z1, p.y + r);
            }
            return Rect.MinMaxRect(x0, z0, x1, z1);
        }

        /// <summary>
        /// Cover of the paint at a world point, 0 to 1: full inside, falling off over the outer quarter of the half
        /// width, with Perlin noise on the edge so it does not read as a stamped shape.
        /// </summary>
        private static float Cover(ObjectRule rule, float x, float z)
        {
            float edge;
            if (rule.PaintPath.Count == 0)
            {
                var c = rule.Area.center;
                var e = rule.Area.extents;
                float dx = (x - c.x) / Mathf.Max(e.x, 0.01f), dz = (z - c.z) / Mathf.Max(e.z, 0.01f);
                edge = 1f - Mathf.Sqrt(dx * dx + dz * dz);
                edge *= Mathf.Min(e.x, e.z);
            }
            else
            {
                float best = float.MaxValue;
                var q = new Vector2(x, z);
                if (rule.PaintPath.Count == 1) best = Vector2.Distance(q, rule.PaintPath[0]);
                for (int i = 0; i + 1 < rule.PaintPath.Count; i++)
                {
                    Vector2 a = rule.PaintPath[i], d = rule.PaintPath[i + 1] - a;
                    float t = d.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(q - a, d) / d.sqrMagnitude);
                    best = Mathf.Min(best, Vector2.Distance(q, a + d * t));
                }
                edge = rule.PaintWidth * 0.5f - best;
            }
            float noise = (Mathf.PerlinNoise(x * 1.7f + 31.3f, z * 1.7f + 17.9f) - 0.5f) * 0.5f;
            float soft = Mathf.Max(rule.PaintWidth > 0f ? rule.PaintWidth * 0.25f : 0.4f, 0.2f);
            float cover = Mathf.Clamp01((edge + noise * soft) / soft);
            float patches = 0.8f + 0.2f * Mathf.PerlinNoise(x * 3.1f + 5.1f, z * 3.1f + 9.7f);
            return cover * patches * Mathf.Clamp01(rule.PaintStrength);
        }

        private static Painted Paint(Terrain t, ObjectRule rule, Rect rect, string owner)
        {
            var td = t.terrainData;
            int layer = LayerIndex(td, rule.PaintLayer, out string names);
            if (layer < 0) throw new InvalidOperationException($"{owner}: terrain '{t.name}' has no layer '{rule.PaintLayer}' (layers: {names})");
            int res = td.alphamapResolution;
            var tp = t.transform.position;
            var ts = td.size;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((rect.xMin - tp.x) / ts.x * res), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((rect.xMax - tp.x) / ts.x * res), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((rect.yMin - tp.z) / ts.z * res), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((rect.yMax - tp.z) / ts.z * res), 0, res - 1);
            var region = new RectInt(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
            int texture = layer / 4, channel = layer % 4;
            if (td.alphamapTextureCount > 1)
                throw new InvalidOperationException($"{owner}: terrain '{t.name}' has {td.alphamapTextureCount} splat textures; painting handles one");

            var original = Read(td, texture, region);
            var pixels = new Color32[original.Length];
            var changed = new bool[original.Length];
            int texels = 0;
            for (int y = 0; y < region.height; y++)
            {
                float wz = tp.z + (region.y + y + 0.5f) / res * ts.z;
                for (int x = 0; x < region.width; x++)
                {
                    float wx = tp.x + (region.x + x + 0.5f) / res * ts.x;
                    int i = y * region.width + x;
                    var src = original[i];
                    float a = Cover(rule, wx, wz);
                    if (a <= 0.004f) { pixels[i] = src; continue; }
                    texels++;
                    changed[i] = true;
                    float[] w = { src.r / 255f, src.g / 255f, src.b / 255f, src.a / 255f };
                    for (int k = 0; k < 4; k++) w[k] = Mathf.Lerp(w[k], k == channel ? 1f : 0f, a);
                    byte[] bytes = new byte[4];
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        if (k == channel) continue;
                        bytes[k] = (byte)Mathf.RoundToInt(w[k] * 255f);
                        sum += bytes[k];
                    }
                    bytes[channel] = (byte)Mathf.Clamp(255 - sum, 0, 255);
                    pixels[i] = new Color32(bytes[0], bytes[1], bytes[2], bytes[3]);
                }
            }
            Write(td, texture, region, pixels);
            td.SyncTexture(TerrainData.AlphamapTextureName);
            return new Painted { Terrain = t, TerrainName = t.name, Texture = texture, Region = region, Original = original, Changed = changed, Texels = texels };
        }

        private static int LayerIndex(TerrainData td, string wanted, out string names)
        {
            var layers = td.terrainLayers;
            var list = new List<string>();
            int found = -1;
            for (int i = 0; layers != null && i < layers.Length; i++)
            {
                string n = layers[i] != null ? layers[i].name : "";
                list.Add(n);
                if (found < 0 && n.Equals(wanted, StringComparison.OrdinalIgnoreCase)) found = i;
            }
            names = string.Join(", ", list);
            return found;
        }

        /// <summary>Reads a region of one splat texture through a full-size render texture.</summary>
        private static Color32[] Read(TerrainData td, int texture, RectInt region)
        {
            var src = td.GetAlphamapTexture(texture);
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tex = new Texture2D(region.width, region.height, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(region.x, region.y, region.width, region.height), 0, 0, false);
                tex.Apply(false);
                var px = tex.GetPixels32();
                var result = new Color32[px.Length];
                for (int i = 0; i < px.Length; i++) result[i] = px[i];
                return result;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (tex != null) Object.Destroy(tex);
            }
        }

        private static void Write(TerrainData td, int texture, RectInt region, Color32[] pixels)
        {
            var arr = new Il2CppStructArray<Color32>(pixels.Length);
            for (int i = 0; i < pixels.Length; i++) arr[i] = pixels[i];
            var tex = new Texture2D(region.width, region.height, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(arr);
            tex.Apply(false);
            var rt = RenderTexture.GetTemporary(region.width, region.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                td.CopyActiveRenderTextureToTexture(TerrainData.AlphamapTextureName, texture, new RectInt(0, 0, region.width, region.height),
                    new Vector2Int(region.x, region.y), false);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(tex);
            }
        }
    }
}
