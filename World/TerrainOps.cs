using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Changes to terrains: turning a placed terrain's data, punching holes and removing trees. Every change works on
    /// a clone of the terrain data, so the captured source and the main world's asset stay untouched.
    /// </summary>
    internal static class TerrainOps
    {
        private const string CloneSuffix = " (MapExpansions)";
        private static Material _blit;

        public static TerrainData EnsureClone(Terrain t, BuiltExpansion owner)
        {
            if (t.terrainData.name.EndsWith(CloneSuffix, StringComparison.Ordinal)) return t.terrainData;
            var clone = Object.Instantiate(t.terrainData);
            clone.name = t.terrainData.name + CloneSuffix;
            t.terrainData = clone;
            owner.Own(clone);
            var col = t.GetComponent<TerrainCollider>();
            if (col != null) col.terrainData = clone;
            else Core.Log.Warning($"terrain: '{t.name}' has no TerrainCollider");
            return clone;
        }

        /// <summary>
        /// A terrain renders axis-aligned at its transform position and ignores rotation, so a turned placement needs
        /// the data itself turned: heightmap, splat maps and holes on the GPU, tree instances on the CPU, and the
        /// terrain moved to the minimum corner of its turned rectangle. Detail layers (grass and bush tufts) turn cell
        /// for cell through <see cref="DetailLayers"/>; when that fails, no details are drawn on the terrain rather than
        /// in the unturned pattern.
        /// </summary>
        public static bool Rotate(Terrain t, Frame frame, BuiltExpansion owner)
        {
            int k = frame.QuarterTurns;
            if (k == 0) return true;
            if (_blit == null)
            {
                var shader = Shader.Find("Hidden/BlitCopy");
                if (shader == null)
                {
                    Core.Log.Error("terrain: shader Hidden/BlitCopy not found; terrains stay unturned");
                    return false;
                }
                _blit = new Material(shader);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var td = EnsureClone(t, owner);
            Vector3 size = td.size;
            Vector3 pos = t.transform.position;
            var rot = frame.Rotation;
            Vector3 c1 = pos + rot * new Vector3(size.x, 0f, 0f);
            Vector3 c2 = pos + rot * new Vector3(0f, 0f, size.z);
            Vector3 c3 = pos + rot * new Vector3(size.x, 0f, size.z);
            var min = new Vector3(Mathf.Min(Mathf.Min(pos.x, c1.x), Mathf.Min(c2.x, c3.x)), pos.y,
                                  Mathf.Min(Mathf.Min(pos.z, c1.z), Mathf.Min(c2.z, c3.z)));

            int hres = td.heightmapResolution;
            string heights = Step(t, "heightmap", () =>
            {
                var hrt = Turn(td.heightmapTexture, k, td.heightmapTexture.format);
                td.CopyActiveRenderTextureToHeightmap(new RectInt(0, 0, hres, hres), new Vector2Int(0, 0), TerrainHeightmapSyncControl.HeightAndLod);
                Release(hrt);
                return $"{hres}";
            });

            // TerrainData.alphamapTextures is stripped from the game; the indexed getter is the remaining path.
            string splats = Step(t, "splat maps", () =>
            {
                int count = td.alphamapTextureCount;
                for (int i = 0; i < count; i++)
                {
                    var src = td.GetAlphamapTexture(i);
                    var art = Turn(src, k, RenderTextureFormat.ARGB32);
                    td.CopyActiveRenderTextureToTexture(TerrainData.AlphamapTextureName, i, new RectInt(0, 0, src.width, src.height), new Vector2Int(0, 0), false);
                    Release(art);
                }
                if (count > 0) td.SyncTexture(TerrainData.AlphamapTextureName);
                return $"{count}";
            });

            // The holes texture is read back and turned on the CPU; a terrain without open holes needs nothing.
            string holes = Step(t, "holes", () =>
            {
                int res = td.holesResolution;
                var solid = ReadHoles(td, res, t.name);
                int open = 0;
                foreach (bool s in solid) if (!s) open++;
                if (open == 0) return "none";
                int n = res - 1;
                var turned = new bool[res * res];
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                    {
                        var (sx, sy) = k switch
                        {
                            1 => (n - y, x),
                            2 => (n - x, n - y),
                            _ => (y, n - x),
                        };
                        turned[y * res + x] = solid[sy * res + sx];
                    }
                WriteHoles(td, res, turned);
                return $"{open} open";
            }, warnOnly: true);

            TreeInstance Turned(TreeInstance tree)
            {
                var p = tree.position;
                tree.position = k switch
                {
                    1 => new Vector3(p.z, p.y, 1f - p.x),
                    2 => new Vector3(1f - p.x, p.y, 1f - p.z),
                    _ => new Vector3(1f - p.z, p.y, p.x),
                };
                tree.rotation += k * Mathf.PI * 0.5f;
                return tree;
            }
            string trees = Step(t, "trees (array)", () =>
            {
                var all = td.treeInstances;
                for (int i = 0; i < all.Length; i++) all[i] = Turned(all[i]);
                td.treeInstances = all;
                return $"{all.Length}";
            });
            if (trees == "FAILED")
                trees = Step(t, "trees (indexed)", () =>
                {
                    int count = td.treeInstanceCount;
                    for (int i = 0; i < count; i++) td.SetTreeInstance(i, Turned(td.GetTreeInstance(i)));
                    return $"{count}";
                });

            string details = Step(t, "detail layers", () =>
            {
                long tufts = DetailLayers.Turn(td, k, out string what);
                if (tufts < 0) throw new InvalidOperationException(what);
                return what == "none" ? what : $"{what}, {tufts} tufts";
            });
            // Quality settings override the detail density, so a failed turn switches the details off by distance.
            if (details == "FAILED") t.detailObjectDistance = 0f;

            if ((k & 1) == 1) td.size = new Vector3(size.z, size.y, size.x);
            t.transform.position = min;
            t.transform.rotation = Quaternion.identity;
            t.Flush();
            // Unturned holes leave a few open cells in the wrong place; the terrain is still usable.
            // Details failing leaves the terrain without grass but usable, so the expansion is still placed.
            bool ok = heights != "FAILED" && splats != "FAILED" && trees != "FAILED";
            string line = $"terrain: '{t.name}' turned {k * 90} degrees in {sw.ElapsedMilliseconds} ms, heightmap {heights}, " +
                          $"splat maps {splats}, holes {holes}, trees {trees}, details {details}, now x {min.x:0}..{min.x + td.size.x:0} " +
                          $"z {min.z:0}..{min.z + td.size.z:0}{(details == "FAILED" ? "; details switched off" : "")}";
            if (!ok) Core.Log.Error(line);
            else if (details == "FAILED") Core.Log.Warning(line);
            else Core.Log.Msg(line);
            return ok;
        }

        /// <summary>Runs one part of the turn; a failed part is logged and the remaining parts still run.</summary>
        private static string Step(Terrain t, string what, Func<string> body, bool warnOnly = false)
        {
            try
            {
                return body();
            }
            catch (Exception e)
            {
                RenderTexture.active = null;
                string line = $"terrain: '{t.name}' {what} failed: {e.GetType().Name}: {e.Message}";
                if (warnOnly) Core.Log.Warning(line + "; it stays unturned"); else Core.Log.Error(line);
                return "FAILED";
            }
        }

        /// <summary>Source uv for a destination uv when the data turns clockwise by k quarter turns seen from above.</summary>
        private static Vector2 SourceUv(float u, float v, int k) => k switch
        {
            1 => new Vector2(1f - v, u),
            2 => new Vector2(1f - u, 1f - v),
            3 => new Vector2(v, 1f - u),
            _ => new Vector2(u, v),
        };

        /// <summary>Draws <paramref name="src"/> turned into a temporary render texture and leaves it active.</summary>
        private static RenderTexture Turn(Texture src, int k, RenderTextureFormat format)
        {
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, format, RenderTextureReadWrite.Linear);
            var filter = src.filterMode;
            src.filterMode = FilterMode.Point;
            RenderTexture.active = rt;
            _blit.mainTexture = src;
            GL.PushMatrix();
            GL.LoadOrtho();
            _blit.SetPass(0);
            GL.Begin(GL.QUADS);
            foreach (var (u, v) in new[] { (0f, 0f), (0f, 1f), (1f, 1f), (1f, 0f) })
            {
                var s = SourceUv(u, v, k);
                GL.TexCoord2(s.x, s.y);
                GL.Vertex3(u, v, 0f);
            }
            GL.End();
            GL.PopMatrix();
            src.filterMode = filter;
            return rt;
        }

        private static void Release(RenderTexture rt)
        {
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
        }

        /// <summary>
        /// Collects holes for several terrains and writes each terrain once. A terrain's existing holes are read back
        /// first, so holes the world already had stay.
        /// </summary>
        public sealed class HoleMask
        {
            private sealed class Entry
            {
                public Terrain Terrain;
                public bool[] Solid;
                public int Res;
                public int Added;
                public List<string> Reasons = new();
            }

            private readonly Dictionary<IntPtr, Entry> _entries = new();
            private readonly BuiltExpansion _owner;

            public HoleMask(BuiltExpansion owner) => _owner = owner;

            /// <summary>
            /// Marks every hole cell of the terrain that passes the test. The test gets the cell centre's world x/z and
            /// a terrain height. A cell on a steep slope spans many metres between its corners and is drawn as a
            /// sliver across that span, so the test runs over the whole span in steps of one metre. <paramref name="slack"/>
            /// widens the span up and down: a coarse level of detail draws the surface metres off the heightmap, and the
            /// holes texture clips it only where the cell is open. <paramref name="slackBelow"/>, when given, replaces it
            /// downwards, for a test whose holes would show where nothing lies under them.
            /// </summary>
            public int Cut(Terrain t, Rect worldArea, Func<float, float, float, bool> isHole, string reason, float slack = 0f, float? slackBelow = null)
            {
                var td = t.terrainData;
                if (td == null) return 0;
                Vector3 p = t.transform.position;
                Vector3 s = td.size;
                var terrainRect = Rect.MinMaxRect(p.x, p.z, p.x + s.x, p.z + s.z);
                if (!terrainRect.Overlaps(worldArea)) return 0;
                var e = Get(t);
                int res = e.Res;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((worldArea.xMin - p.x) / s.x * res), 0, res - 1);
                int x1 = Mathf.Clamp(Mathf.CeilToInt((worldArea.xMax - p.x) / s.x * res), 0, res - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((worldArea.yMin - p.z) / s.z * res), 0, res - 1);
                int z1 = Mathf.Clamp(Mathf.CeilToInt((worldArea.yMax - p.z) / s.z * res), 0, res - 1);
                int added = 0;
                for (int y = z0; y <= z1; y++)
                {
                    float wz = p.z + (y + 0.5f) / res * s.z;
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = y * res + x;
                        if (!e.Solid[i]) continue;
                        float wx = p.x + (x + 0.5f) / res * s.x;
                        float cx = s.x / res * 0.5f, cz = s.z / res * 0.5f;
                        float lo = float.MaxValue, hi = float.MinValue;
                        foreach (var (dx, dz) in Corners)
                        {
                            float ch = t.SampleHeight(new Vector3(wx + dx * cx, 0f, wz + dz * cz)) + p.y;
                            lo = Mathf.Min(lo, ch);
                            hi = Mathf.Max(hi, ch);
                        }
                        lo -= slackBelow ?? slack;
                        hi += slack;
                        bool hole = false;
                        for (float h = lo; !hole; h = Mathf.Min(h + 1f, hi))
                        {
                            hole = isHole(wx, wz, h);
                            if (h >= hi) break;
                        }
                        if (!hole) continue;
                        e.Solid[i] = false;
                        added++;
                    }
                }
                if (added > 0)
                {
                    e.Added += added;
                    e.Reasons.Add($"{reason} {added}");
                }
                return added;
            }

            private static readonly (float, float)[] Corners = { (0f, 0f), (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) };

            private Entry Get(Terrain t)
            {
                if (_entries.TryGetValue(t.Pointer, out var e)) return e;
                var td = t.terrainData;
                int res = td.holesResolution;
                e = new Entry { Terrain = t, Res = res, Solid = ReadHoles(td, res, t.name) };
                _entries[t.Pointer] = e;
                return e;
            }

            /// <summary>Writes every changed terrain; returns the number of terrains written.</summary>
            public int Apply()
            {
                int written = 0;
                foreach (var e in _entries.Values)
                {
                    if (e.Added == 0 || e.Terrain == null) continue;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var td = EnsureClone(e.Terrain, _owner);
                        WriteHoles(td, e.Res, e.Solid);
                        written++;
                        Core.Log.Msg($"terrain: '{e.Terrain.name}' {e.Added} hole cells added ({string.Join(", ", e.Reasons)}) in {sw.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        RenderTexture.active = null;
                        Core.Log.Error($"terrain: writing the holes of '{e.Terrain.name}' failed; it stays closed: {ex}");
                    }
                }
                return written;
            }
        }

        /// <summary>Writes solid flags as the terrain's holes texture.</summary>
        private static void WriteHoles(TerrainData td, int res, bool[] solid)
        {
            var pixels = new Il2CppStructArray<Color32>(res * res);
            for (int i = 0; i < res * res; i++)
            {
                byte v = solid[i] ? (byte)255 : (byte)0;
                pixels[i] = new Color32(v, v, v, v);
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
        }

        /// <summary>Current holes of a terrain as solid flags; a terrain without a holes texture is solid everywhere.</summary>
        private static bool[] ReadHoles(TerrainData td, int res, string name)
        {
            var solid = new bool[res * res];
            for (int i = 0; i < solid.Length; i++) solid[i] = true;
            Texture src;
            try { src = td.holesTexture; }
            catch (Exception e)
            {
                Core.Log.Warning($"terrain: holes of '{name}' not readable ({e.Message}); existing holes are treated as closed");
                return solid;
            }
            if (src == null) return solid;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                rt = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
                tex.Apply(false);
                RenderTexture.active = prev;
                var px = tex.GetPixels32();
                int open = 0;
                for (int i = 0; i < solid.Length && i < px.Length; i++)
                {
                    solid[i] = px[i].r > 127;
                    if (!solid[i]) open++;
                }
                if (open > 0) Core.Log.Msg($"terrain: '{name}' already has {open} hole cells; they stay");
            }
            catch (Exception e)
            {
                RenderTexture.active = null;
                Core.Log.Warning($"terrain: reading the holes of '{name}' failed ({e.Message}); existing holes are treated as closed");
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tex != null) Object.Destroy(tex);
            }
            return solid;
        }

        /// <summary>Removes the tree instances whose base passes the test (world position).</summary>
        public static int RemoveTrees(Terrain t, Func<Vector3, bool> remove, BuiltExpansion owner, string reason)
        {
            var td = t.terrainData;
            if (td == null) return 0;
            Vector3 p = t.transform.position;
            Vector3 s = td.size;
            try
            {
                var all = td.treeInstances;
                var keep = new List<TreeInstance>(all.Length);
                for (int i = 0; i < all.Length; i++)
                {
                    var w = p + Vector3.Scale(all[i].position, s);
                    if (!remove(w)) keep.Add(all[i]);
                }
                int removed = all.Length - keep.Count;
                if (removed == 0) return 0;
                var data = EnsureClone(t, owner);
                var arr = new Il2CppStructArray<TreeInstance>(keep.Count);
                for (int i = 0; i < keep.Count; i++) arr[i] = keep[i];
                data.treeInstances = arr;
                t.Flush();
                Core.Log.Msg($"terrain: '{t.name}' {removed} trees removed ({reason})");
                return removed;
            }
            catch (Exception e)
            {
                Core.Log.Error($"terrain: removing trees from '{t.name}' failed ({reason}): {e}");
                return 0;
            }
        }
    }
}
