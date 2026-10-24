#if DEBUG
using System.Globalization;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// Terrain overlap commands, all under <c>mxterrain</c>:
    ///
    /// mxterrain list                    every terrain with position, size, resolution and whether it belongs to the copy
    /// mxterrain cut x0 z0 x1 z1         punch holes into the placed terrains inside a world rectangle
    /// mxterrain cutmain [margin]        punch holes into the placed terrains wherever a Main terrain lies
    /// mxterrain clip name zmax         shrink a placed object along world z so its bounds end at zmax
    /// mxterrain objects z               placed objects whose bounds reach north of z, by size
    /// </summary>
    internal static class TerrainSpike
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static float F(string s) => float.Parse(s, Ci);

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            switch (sub)
            {
                case "list": List(); break;
                case "cut" when parts.Length > 5:
                    Cut(new[] { Rect.MinMaxRect(Mathf.Min(F(parts[2]), F(parts[4])), Mathf.Min(F(parts[3]), F(parts[5])),
                        Mathf.Max(F(parts[2]), F(parts[4])), Mathf.Max(F(parts[3]), F(parts[5]))) });
                    break;
                case "cutmain": CutMain(parts.Length > 2 ? F(parts[2]) : 0f); break;
                case "objects" when parts.Length > 2: Objects(F(parts[2])); break;
                case "clip" when parts.Length > 3: Clip(parts[2], F(parts[3])); break;
                default:
                    Core.Log.Warning("[dev] mxterrain list|cut|cutmain|objects (see TerrainSpike docs)");
                    break;
            }
        }

        private static Transform PlacedRoot => GameObject.Find("MapExpansions.Placed")?.transform;

        private static bool IsPlaced(Component c)
        {
            var root = PlacedRoot;
            return root != null && c.transform.IsChildOf(root);
        }

        private static void List()
        {
            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                var td = t.terrainData;
                Vector3 p = t.transform.position;
                Vector3 s = td != null ? td.size : Vector3.zero;
                Core.Log.Msg($"[dev] mxterrain {(IsPlaced(t) ? "copy" : "main")} '{t.name}': x {p.x:0}..{p.x + s.x:0} z {p.z:0}..{p.z + s.z:0} " +
                             $"y {p.y:0}..{p.y + s.y:0}, heightmap {td?.heightmapResolution}, holes {td?.holesResolution}, " +
                             $"material {(t.materialTemplate != null ? t.materialTemplate.shader.name : "none")}");
            }
        }

        private static void CutMain(float margin)
        {
            var rects = new List<Rect>();
            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                if (IsPlaced(t) || t.terrainData == null) continue;
                Vector3 p = t.transform.position;
                Vector3 s = t.terrainData.size;
                rects.Add(Rect.MinMaxRect(p.x - margin, p.z - margin, p.x + s.x + margin, p.z + s.z + margin));
            }
            Core.Log.Msg($"[dev] mxterrain cutmain: {rects.Count} Main terrain rectangles, margin {margin}");
            Cut(rects.ToArray());
        }

        /// <summary>
        /// Writes a holes mask into each placed terrain through the GPU path, the only terrain write the interop keeps.
        /// The terrain data is cloned first, so the cached source stays intact.
        /// </summary>
        private static void Cut(Rect[] worldRects)
        {
            int done = 0;
            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                if (!IsPlaced(t) || t.terrainData == null) continue;
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                if (!t.terrainData.name.EndsWith("(MapExpansions)", StringComparison.Ordinal))
                {
                    var clone = Object.Instantiate(t.terrainData);
                    clone.name = t.terrainData.name + " (MapExpansions)";
                    t.terrainData = clone;
                    var col = t.GetComponent<TerrainCollider>();
                    if (col != null) col.terrainData = clone;
                    else Core.Log.Warning($"[dev] mxterrain cut: '{t.name}' has no TerrainCollider");
                }
                var td = t.terrainData;
                int res = td.holesResolution;
                Vector3 p = t.transform.position;
                Vector3 s = td.size;
                var pixels = new Il2CppStructArray<Color32>(res * res);
                int holes = 0;
                for (int y = 0; y < res; y++)
                {
                    float wz = p.z + (y + 0.5f) / res * s.z;
                    for (int x = 0; x < res; x++)
                    {
                        float wx = p.x + (x + 0.5f) / res * s.x;
                        bool hole = false;
                        foreach (var r in worldRects)
                            if (r.Contains(new Vector2(wx, wz))) { hole = true; break; }
                        if (hole) holes++;
                        byte v = hole ? (byte)0 : (byte)255;
                        pixels[y * res + x] = new Color32(v, v, v, v);
                    }
                }
                if (holes == 0)
                {
                    Core.Log.Msg($"[dev] mxterrain cut: '{t.name}' does not touch the rectangles");
                    continue;
                }

                var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
                tex.SetPixels32(pixels);
                tex.Apply(false);
                var rt = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                td.CopyActiveRenderTextureToTexture(TerrainData.HolesTextureName, 0, new RectInt(0, 0, res, res), new Vector2Int(0, 0), false);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(tex);
                done++;
                Core.Log.Msg($"[dev] mxterrain cut: '{t.name}' {holes}/{res * res} hole cells in {t0.ElapsedMilliseconds} ms");
            }
            Core.Log.Msg($"[dev] mxterrain cut: {done} placed terrains changed");
        }

        /// <summary>
        /// Scales a placed object along the local axis that points along world z and shifts it back, so its north
        /// edge ends at <paramref name="zMax"/> and the south edge stays where it was.
        /// </summary>
        private static void Clip(string name, float zMax)
        {
            var root = PlacedRoot;
            var r = root == null ? null : root.GetComponentsInChildren<Renderer>(true).FirstOrDefault(x => x.name == name);
            if (r == null)
            {
                Core.Log.Warning($"[dev] mxterrain clip: no placed renderer '{name}'");
                return;
            }
            var t = r.transform;
            Bounds before = r.bounds;
            if (before.max.z <= zMax)
            {
                Core.Log.Msg($"[dev] mxterrain clip: '{name}' already ends at z {before.max.z:0}");
                return;
            }
            Vector3 local = t.InverseTransformDirection(Vector3.forward);
            int axis = Mathf.Abs(local.x) >= Mathf.Abs(local.y) && Mathf.Abs(local.x) >= Mathf.Abs(local.z) ? 0
                : Mathf.Abs(local.y) >= Mathf.Abs(local.z) ? 1 : 2;
            float factor = (zMax - before.min.z) / (before.max.z - before.min.z);
            var scale = t.localScale;
            scale[axis] *= factor;
            t.localScale = scale;
            float shift = zMax - r.bounds.max.z;
            t.position += new Vector3(0f, 0f, shift);
            Bounds after = r.bounds;
            Core.Log.Msg($"[dev] mxterrain clip: '{name}' local axis {axis} x{factor:0.000}, z {before.min.z:0}..{before.max.z:0} -> {after.min.z:0}..{after.max.z:0}, " +
                         $"collider {(r.GetComponent<Collider>() != null ? r.GetComponent<Collider>().bounds.max.z.ToString("0", Ci) : "none")}");
        }

        private static void Objects(float z)
        {
            var root = PlacedRoot;
            if (root == null)
            {
                Core.Log.Warning("[dev] mxterrain objects: nothing placed");
                return;
            }
            var hits = new List<(string name, Bounds b)>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                if (r.bounds.max.z > z) hits.Add((r.name, r.bounds));
            foreach (var h in hits.OrderByDescending(h => h.b.size.x * h.b.size.z).Take(15))
                Core.Log.Msg($"[dev] mxterrain objects: '{h.name}' x {h.b.min.x:0}..{h.b.max.x:0} z {h.b.min.z:0}..{h.b.max.z:0} y {h.b.min.y:0}..{h.b.max.y:0}");
            Core.Log.Msg($"[dev] mxterrain objects: {hits.Count} placed renderers reach north of z {z}");
        }
    }
}
#endif
