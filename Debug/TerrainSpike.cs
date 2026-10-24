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
    /// mxterrain cut x0 z0 x1 z1 [main]  punch holes into the placed terrains (or the Main ones) inside a world rectangle
    /// mxterrain height x z              terrain heights and the first collider hit from above at x/z
    /// mxterrain cutmain [margin]        punch holes into the placed terrains wherever a Main terrain lies
    /// mxterrain clip name zmax         shrink a placed object along world z so its bounds end at zmax
    /// mxterrain hide copy|main name    disable the renderers whose name starts with name, in the copy or in Main
    /// mxterrain show copy|main name    enable them again
    /// mxterrain ground name zmax       unbatched quad with the material of a placed renderer, its extent cut at zmax
    /// mxterrain objects z               placed objects whose bounds reach north of z, by size
    /// mxterrain collider copy|main name on|off   enable or disable the colliders whose object name starts with name
    /// mxterrain road ax ay az bx by bz w  a flat drivable box whose top runs from a to b, w metres wide
    /// mxterrain road clear              remove all stand-in roads
    /// mxterrain overlap ax ay az bx by bz w h [off]  colliders and renderers in the box from a to b (w wide, h high
    ///                                   above the line); off disables them
    /// mxterrain rotate                  turn the data of the placed terrains by the placement yaw (multiples of 90
    ///                                   degrees); mxplace runs it on its own when yaw is set
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
                        Mathf.Max(F(parts[2]), F(parts[4])), Mathf.Max(F(parts[3]), F(parts[5]))) }, parts.Length > 6 && parts[6] == "main");
                    break;
                case "height" when parts.Length > 3: Height(F(parts[2]), F(parts[3])); break;
                case "cutmain": CutMain(parts.Length > 2 ? F(parts[2]) : 0f); break;
                case "objects" when parts.Length > 2: Objects(F(parts[2])); break;
                case "clip" when parts.Length > 3: Clip(parts[2], F(parts[3])); break;
                case "hide" when parts.Length > 3: Hide(parts[2] == "copy", string.Join(" ", parts.Skip(3)), true); break;
                case "show" when parts.Length > 3: Hide(parts[2] == "copy", string.Join(" ", parts.Skip(3)), false); break;
                case "ground" when parts.Length > 3: Ground(parts[2], F(parts[3])); break;
                case "rotate": Rotate(); break;
                case "collider" when parts.Length > 4: Colliders(parts[2] == "copy", parts[3], parts[4] == "on"); break;
                case "road" when parts.Length > 2 && parts[2] == "clear": ClearRoads(); break;
                case "overlap" when parts.Length > 9:
                    Overlap(new Vector3(F(parts[2]), F(parts[3]), F(parts[4])), new Vector3(F(parts[5]), F(parts[6]), F(parts[7])),
                        F(parts[8]), F(parts[9]), parts.Length > 10 && parts[10] == "off");
                    break;
                case "road" when parts.Length > 8:
                    Road(new Vector3(F(parts[2]), F(parts[3]), F(parts[4])), new Vector3(F(parts[5]), F(parts[6]), F(parts[7])), F(parts[8]));
                    break;
                default:
                    Core.Log.Warning("[dev] mxterrain list|cut|cutmain|objects (see TerrainSpike docs)");
                    break;
            }
        }

        private static Transform PlacedRoot => DevConsole.PlacedRoot;

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

        private static readonly HashSet<int> Rotated = new();
        private static Material _blit;

        /// <summary>
        /// A terrain renders axis-aligned at its transform position and ignores rotation, so a turned placement needs
        /// the data itself turned: heightmap, splat maps and holes on the GPU, tree instances on the CPU, and the
        /// terrain moved to the minimum corner of its turned rectangle. Detail layers have no read or write path in
        /// the interop; their density is set to 0 instead of drawing grass in the unturned pattern.
        /// </summary>
        internal static void Rotate()
        {
            var root = PlacedRoot;
            if (root == null)
            {
                Core.Log.Warning("[dev] mxterrain rotate: nothing placed");
                return;
            }
            float yaw = root.eulerAngles.y;
            int k = Mathf.RoundToInt(yaw / 90f) & 3;
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, k * 90f)) > 0.5f)
            {
                Core.Log.Error($"[dev] mxterrain rotate: yaw {yaw:0.##} is not a multiple of 90, terrains stay unturned");
                return;
            }
            if (_blit == null)
            {
                var shader = Shader.Find("Hidden/BlitCopy");
                if (shader == null)
                {
                    Core.Log.Error("[dev] mxterrain rotate: shader Hidden/BlitCopy not found");
                    return;
                }
                _blit = new Material(shader);
            }
            int done = 0;
            foreach (var t in root.GetComponentsInChildren<Terrain>(true))
            {
                if (k == 0 || Rotated.Contains(t.GetInstanceID()))
                {
                    Core.Log.Msg($"[dev] mxterrain rotate: '{t.name}' skipped, {(k == 0 ? "yaw 0" : "already turned")}");
                    continue;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var td = EnsureClone(t);
                Vector3 size = td.size;
                Vector3 pos = t.transform.position;
                var rot = root.rotation;
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

                string holes = Step(t, "holes", () =>
                {
                    var src = td.holesTexture;
                    if (src == null) return "none";
                    var hlrt = Turn(src, k, RenderTextureFormat.R8);
                    td.CopyActiveRenderTextureToTexture(TerrainData.HolesTextureName, 0, new RectInt(0, 0, src.width, src.height), new Vector2Int(0, 0), false);
                    Release(hlrt);
                    return $"{src.width}";
                });

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

                if ((k & 1) == 1) td.size = new Vector3(size.z, size.y, size.x);
                t.transform.position = min;
                t.transform.rotation = Quaternion.identity;
                t.detailObjectDensity = 0f;
                t.Flush();
                Rotated.Add(t.GetInstanceID());
                done++;
                Core.Log.Msg($"[dev] mxterrain rotate: '{t.name}' turned {k * 90} degrees in {sw.ElapsedMilliseconds} ms, heightmap {heights}, " +
                             $"splat maps {splats}, holes {holes}, trees {trees}, now x {min.x:0}..{min.x + td.size.x:0} " +
                             $"z {min.z:0}..{min.z + td.size.z:0}; detail density set to 0");
            }
            Core.Log.Msg($"[dev] mxterrain rotate: {done} placed terrains turned by {k * 90} degrees");
        }

        /// <summary>Runs one part of the turn; a failed part is logged and the remaining parts still run.</summary>
        private static string Step(Terrain t, string what, Func<string> body)
        {
            try
            {
                return body();
            }
            catch (Exception e)
            {
                RenderTexture.active = null;
                Core.Log.Error($"[dev] mxterrain rotate: '{t.name}' {what} failed: {e.GetType().Name}: {e.Message}");
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

        private static TerrainData EnsureClone(Terrain t)
        {
            if (!t.terrainData.name.EndsWith("(MapExpansions)", StringComparison.Ordinal))
            {
                var clone = Object.Instantiate(t.terrainData);
                clone.name = t.terrainData.name + " (MapExpansions)";
                t.terrainData = clone;
                var col = t.GetComponent<TerrainCollider>();
                if (col != null) col.terrainData = clone;
                else Core.Log.Warning($"[dev] mxterrain: '{t.name}' has no TerrainCollider");
            }
            return t.terrainData;
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
        private static void Height(float x, float z)
        {
            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                if (t.terrainData == null) continue;
                Vector3 p = t.transform.position;
                Vector3 s = t.terrainData.size;
                if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
                float h = t.SampleHeight(new Vector3(x, 0f, z)) + p.y;
                Core.Log.Msg($"[dev] mxterrain height {x:0.#} {z:0.#}: {(IsPlaced(t) ? "copy" : "main")} terrain '{t.name}' y {h:0.00}");
            }
            var hits = Physics.RaycastAll(new Vector3(x, 400f, z), Vector3.down, 800f, ~0, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits.OrderBy(hh => hh.distance).Take(4))
                Core.Log.Msg($"[dev] mxterrain height {x:0.#} {z:0.#}: hit '{hit.collider.name}' ({(IsPlaced(hit.collider) ? "copy" : "main")}) at y {hit.point.y:0.00}");
            if (hits.Length == 0) Core.Log.Msg($"[dev] mxterrain height {x:0.#} {z:0.#}: no collider below");
        }

        private static void Cut(Rect[] worldRects, bool main = false)
        {
            int done = 0;
            foreach (var t in Object.FindObjectsOfType<Terrain>())
            {
                if (IsPlaced(t) == main || t.terrainData == null) continue;
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var td = EnsureClone(t);
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
            Core.Log.Msg($"[dev] mxterrain cut: {done} {(main ? "Main" : "placed")} terrains changed");
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

        private static void Hide(bool copy, string prefix, bool hide)
        {
            int n = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>(true))
            {
                // Inactive renderers belong to the capture cache; a change there would carry into every later placement.
                if (!r.gameObject.activeInHierarchy) continue;
                if (IsPlaced(r) != copy || !r.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                r.enabled = !hide;
                n++;
                Core.Log.Msg($"[dev] mxterrain {(hide ? "hide" : "show")} {(copy ? "copy" : "main")}: '{r.name}' under '{r.transform.root.name}' x {r.bounds.min.x:0}..{r.bounds.max.x:0} z {r.bounds.min.z:0}..{r.bounds.max.z:0}");
            }
            // A terrain is no Renderer; a disabled one also drops out of Terrain.activeTerrains, which the recast scan reads.
            foreach (var t in Object.FindObjectsOfType<Terrain>(true))
            {
                if (!t.gameObject.activeInHierarchy) continue;
                if (IsPlaced(t) != copy || !t.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                t.enabled = !hide;
                n++;
                Core.Log.Msg($"[dev] mxterrain {(hide ? "hide" : "show")} {(copy ? "copy" : "main")}: terrain '{t.name}' enabled {t.enabled}");
            }
            if (n == 0) Core.Log.Warning($"[dev] mxterrain {(hide ? "hide" : "show")}: no {(copy ? "copy" : "main")} renderer starts with '{prefix}'");
        }

        private static void Colliders(bool copy, string prefix, bool on)
        {
            int n = 0;
            foreach (var c in Object.FindObjectsOfType<Collider>(true))
            {
                if (!c.gameObject.activeInHierarchy) continue;
                if (IsPlaced(c) != copy || !c.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                c.enabled = on;
                n++;
                Core.Log.Msg($"[dev] mxterrain collider {(copy ? "copy" : "main")}: '{c.name}' {c.GetIl2CppType().Name} {(on ? "on" : "off")}, " +
                             $"x {c.bounds.min.x:0}..{c.bounds.max.x:0} z {c.bounds.min.z:0}..{c.bounds.max.z:0}");
            }
            if (n == 0) Core.Log.Warning($"[dev] mxterrain collider: no {(copy ? "copy" : "main")} collider starts with '{prefix}'");
        }

        private static readonly List<GameObject> Roads = new();

        /// <summary>A stand-in tunnel floor: a box whose top face runs along a to b, so wheels and the grid scan see a road.</summary>
        private static void Road(Vector3 a, Vector3 b, float width)
        {
            const float thickness = 1f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "MapExpansions.Road";
            var dir = b - a;
            go.transform.rotation = Quaternion.LookRotation(dir);
            go.transform.position = (a + b) * 0.5f - go.transform.up * (thickness * 0.5f);
            go.transform.localScale = new Vector3(width, thickness, dir.magnitude);
            var root = PlacedRoot;
            if (root != null) go.transform.SetParent(root, true);
            Roads.Add(go);
            Core.Log.Msg($"[dev] mxterrain road: {a} -> {b}, {dir.magnitude:0.0} m long, {width} m wide, slope {Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg:0.0} degrees");
        }

        /// <summary>
        /// Lists what fills a corridor: colliders by physics overlap, renderers by bounds. Stand-in roads are skipped.
        /// Renderers only count when their bounds are smaller than 300 m, so the world plates stay out of the list.
        /// </summary>
        private static void Overlap(Vector3 a, Vector3 b, float width, float height, bool off)
        {
            var dir = b - a;
            var rot = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z));
            var center = (a + b) * 0.5f + Vector3.up * (height * 0.5f + 0.3f);
            var half = new Vector3(width * 0.5f, height * 0.5f + Mathf.Abs(dir.y) * 0.5f, new Vector2(dir.x, dir.z).magnitude * 0.5f);
            int n = 0;
            var seen = new HashSet<IntPtr>();
            foreach (var c in Physics.OverlapBox(center, half, rot, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null || c.name == "MapExpansions.Road" || !seen.Add(c.gameObject.Pointer)) continue;
                n++;
                Core.Log.Msg($"[dev] mxterrain overlap: collider '{c.name}' ({(IsPlaced(c) ? "copy" : "main")}) {c.GetIl2CppType().Name} under '{c.transform.parent?.name}' " +
                             $"x {c.bounds.min.x:0}..{c.bounds.max.x:0} y {c.bounds.min.y:0}..{c.bounds.max.y:0} z {c.bounds.min.z:0}..{c.bounds.max.z:0}{(off ? ", off" : "")}");
                // A terrain collider covers the whole map; switching it off would drop everything on it.
                if (off && c.GetIl2CppType().Name == "TerrainCollider")
                {
                    Core.Log.Warning($"[dev] mxterrain overlap: terrain collider '{c.name}' stays on");
                    continue;
                }
                if (off)
                {
                    c.enabled = false;
                    var r = c.GetComponent<Renderer>();
                    if (r != null) r.enabled = false;
                }
            }
            var box = new Bounds(center, Vector3.zero);
            foreach (var corner in new[] { new Vector3(-1, -1, -1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1), new Vector3(1, -1, -1),
                         new Vector3(1, 1, -1), new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(-1, 1, -1) })
                box.Encapsulate(center + rot * Vector3.Scale(half, corner));
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || r.name == "MapExpansions.Road" || seen.Contains(r.gameObject.Pointer)) continue;
                var rb = r.bounds;
                if (rb.size.x > 300f || rb.size.z > 300f || !rb.Intersects(box)) continue;
                n++;
                Core.Log.Msg($"[dev] mxterrain overlap: renderer '{r.name}' ({(IsPlaced(r) ? "copy" : "main")}) under '{r.transform.parent?.name}' " +
                             $"x {rb.min.x:0}..{rb.max.x:0} y {rb.min.y:0}..{rb.max.y:0} z {rb.min.z:0}..{rb.max.z:0}{(off ? ", off" : "")}");
                if (off) r.enabled = false;
            }
            Core.Log.Msg($"[dev] mxterrain overlap: {n} objects in the box {a} -> {b}, {width} m wide, {height} m high{(off ? ", all switched off" : "")}");
        }

        private static void ClearRoads()
        {
            int n = 0;
            foreach (var go in Roads)
                if (go != null) { Object.Destroy(go); n++; }
            Roads.Clear();
            Core.Log.Msg($"[dev] mxterrain road clear: {n} roads removed");
        }

        private static GameObject _ground;

        /// <summary>
        /// A statically batched plate cannot be rescaled, so the replacement is a plain quad over the same x range and
        /// height, ending at <paramref name="zMax"/>.
        /// </summary>
        private static void Ground(string name, float zMax)
        {
            var root = PlacedRoot;
            var r = root == null ? null : root.GetComponentsInChildren<Renderer>(true).FirstOrDefault(x => x.name == name);
            if (r == null)
            {
                Core.Log.Warning($"[dev] mxterrain ground: no placed renderer '{name}'");
                return;
            }
            Bounds b = r.bounds;
            if (_ground != null) Object.Destroy(_ground);
            _ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _ground.name = "MapExpansions.Ground";
            var col = _ground.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            _ground.transform.SetParent(root, true);
            float zMin = b.min.z;
            _ground.transform.position = new Vector3(b.center.x, b.center.y, (zMin + zMax) * 0.5f);
            _ground.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            _ground.transform.localScale = new Vector3(b.size.x, zMax - zMin, 1f);
            _ground.GetComponent<MeshRenderer>().sharedMaterial = r.sharedMaterial;
            r.enabled = false;
            var gb = _ground.GetComponent<Renderer>().bounds;
            Core.Log.Msg($"[dev] mxterrain ground: '{name}' hidden, quad x {gb.min.x:0}..{gb.max.x:0} z {gb.min.z:0}..{gb.max.z:0} y {gb.center.y:0} " +
                         $"material {(r.sharedMaterial != null ? r.sharedMaterial.name : "none")}");
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
