using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// What the phone map shows where no sheet lies: the ground below, seen from above. A grid over the map is cast
    /// down onto the world; each cell takes the colour of what it hits, in the palette of the world it belongs to. The
    /// sea, and ground below the sea's surface, stay sea. The rays run a few milliseconds per frame, so building the
    /// map does not stall the game.
    /// </summary>
    internal sealed class MapFill
    {
        /// <summary>Side of one cell, in metres.</summary>
        public const float Cell = 2f;

        private const float RayTop = 400f, RayLength = 600f;

        /// <summary>The main map's colours: the outskirts' grass and the highway.</summary>
        public static readonly Color32 MainLand = new(71, 91, 61, 255), Road = new(127, 133, 136, 255);

        private static readonly Color32 Sea = new(0, 0, 0, 0);

        private readonly int _n;
        private readonly Vector2 _low;
        private readonly Func<Vector2, bool> _covered;
        private readonly List<BuiltExpansion> _built;
        private readonly Color32[] _cells;
        private readonly Dictionary<IntPtr, Color32> _byCollider = new();
        private readonly int _mask, _water;
        private readonly float _seaLevel;
        private int _next;
        private int _rays, _hitSea, _missed;
        private double _spent;

        /// <param name="low">World x/z of the map's lower left corner.</param>
        /// <param name="covered">Whether a sheet shows the point; such cells are not cast.</param>
        public MapFill(Vector2 low, float side, Func<Vector2, bool> covered, List<BuiltExpansion> built)
        {
            _low = low;
            _n = Mathf.CeilToInt(side / Cell);
            _covered = covered;
            _built = built;
            _cells = new Color32[_n * _n];

            _water = LayerMask.NameToLayer("Water");
            int terrain = LayerMask.NameToLayer("Terrain");
            _mask = 1 << 0;
            if (terrain >= 0) _mask |= 1 << terrain;
            else Core.Log.Warning("map: the game has no Terrain layer; the map between the sheets shows only what lies on the Default layer");
            if (_water >= 0) _mask |= 1 << _water;
            else Core.Log.Warning("map: the game has no Water layer; the map between the sheets takes its sea from the sea level only");

            _seaLevel = float.NegativeInfinity;
            if (_water >= 0)
                foreach (var c in Object.FindObjectsOfType<Collider>())
                    if (c != null && c.gameObject.layer == _water) _seaLevel = Mathf.Max(_seaLevel, c.bounds.max.y);
            if (float.IsNegativeInfinity(_seaLevel))
                Core.Log.Warning("map: no water collider found; ground below the sea is drawn as land between the sheets");
        }

        public bool Done => _next >= _cells.Length;

        /// <summary>Casts cells until the budget is spent. True once every cell is known.</summary>
        public bool Step(double budgetMs)
        {
            if (Done) return true;
            var clock = Stopwatch.StartNew();
            while (_next < _cells.Length)
            {
                int i = _next++;
                var p = _low + new Vector2(i % _n + 0.5f, i / _n + 0.5f) * Cell;
                _cells[i] = _covered(p) ? Sea : Sample(p);
                if ((i & 127) == 0 && clock.Elapsed.TotalMilliseconds > budgetMs) break;
            }
            _spent += clock.Elapsed.TotalMilliseconds;
            if (!Done) return false;
            Core.Log.Msg($"map: the ground between the sheets was sampled: {_n}x{_n} cells of {Cell} m, {_rays} rays in {_spent:0} ms, " +
                         $"{_hitSea} on the sea, {_missed} hit nothing, {_byCollider.Count} colliders seen, sea level {_seaLevel:0.0}");
            return true;
        }

        private Color32 Sample(Vector2 p)
        {
            _rays++;
            if (!Physics.Raycast(new Vector3(p.x, RayTop, p.y), Vector3.down, out var hit, RayLength, _mask, QueryTriggerInteraction.Ignore))
            {
                _missed++;
                return Sea;
            }
            var collider = hit.collider;
            if (collider == null || hit.point.y < _seaLevel || collider.gameObject.layer == _water)
            {
                _hitSea++;
                return Sea;
            }
            if (_byCollider.TryGetValue(collider.Pointer, out var known)) return known;
            var colour = ColourOf(collider);
            _byCollider[collider.Pointer] = colour;
            return colour;
        }

        /// <summary>
        /// An expansion's terrain takes its land colour and everything else it built (rocks, cliffs, tunnel shells) its
        /// rock colour. The main world is grass, except its highway.
        /// </summary>
        private Color32 ColourOf(Collider c)
        {
            var t = c.transform;
            foreach (var b in _built)
            {
                bool ours = b.IsOurs(t);
                if (!ours)
                    foreach (var conn in b.Connections)
                        if (conn.Root != null && t.IsChildOf(conn.Root.transform)) { ours = true; break; }
                if (!ours) continue;
                var sheet = b.Definition.Map;
                return c.TryCast<TerrainCollider>() != null ? sheet.Land : sheet.Rock;
            }
            for (var a = t; a != null; a = a.parent)
                if (a.name == "Overpass") return Road;
            return MainLand;
        }

        /// <summary>Pixels per cell side in <see cref="Picture"/>.</summary>
        private const int Smooth = 4;

        /// <summary>
        /// The cells as a picture over the whole map, the sea where nothing was found. Each pixel weighs the sixteen
        /// nearest cells with a cubic B-spline and takes the colour that weighs most, so the borders between colours run
        /// as rounded curves instead of the cells' steps. Near a border the two heaviest colours are blended over about
        /// a pixel, which keeps the curve's edge soft at full zoom. Colours are RGBA packed little end first, so the
        /// result is the texture's raw data; nothing here touches the engine, which lets it run off the main thread.
        /// </summary>
        private static int[] Pixels(int[] packed, int n)
        {
            int m = n * Smooth;
            var pixels = new int[m * m];
            var keys = new int[16];
            var sums = new float[16];
            var wx = new float[4];
            var wy = new float[4];
            var cols = new int[4];
            var rows = new int[4];
            for (int y = 0; y < m; y++)
            {
                float cy = (y + 0.5f) / Smooth - 0.5f;
                int by = (int)Math.Floor(cy);
                Spline(cy - by, wy);
                for (int k = 0; k < 4; k++) rows[k] = Math.Clamp(by - 1 + k, 0, n - 1) * n;
                for (int x = 0; x < m; x++)
                {
                    float cx = (x + 0.5f) / Smooth - 0.5f;
                    int bx = (int)Math.Floor(cx);
                    for (int k = 0; k < 4; k++) cols[k] = Math.Clamp(bx - 1 + k, 0, n - 1);
                    int first = packed[rows[1] + cols[1]];
                    bool uniform = true;
                    for (int j = 0; j < 4 && uniform; j++)
                        for (int k = 0; k < 4; k++)
                            if (packed[rows[j] + cols[k]] != first) { uniform = false; break; }
                    if (uniform)
                    {
                        pixels[y * m + x] = first;
                        continue;
                    }

                    Spline(cx - bx, wx);
                    int distinct = 0;
                    for (int j = 0; j < 4; j++)
                        for (int k = 0; k < 4; k++)
                        {
                            int key = packed[rows[j] + cols[k]];
                            int d = 0;
                            while (d < distinct && keys[d] != key) d++;
                            if (d == distinct) { keys[d] = key; sums[d] = 0f; distinct++; }
                            sums[d] += wy[j] * wx[k];
                        }
                    int top = 0, second = -1;
                    for (int d = 1; d < distinct; d++)
                    {
                        if (sums[d] > sums[top]) { second = top; top = d; }
                        else if (second < 0 || sums[d] > sums[second]) second = d;
                    }
                    if (second < 0) { pixels[y * m + x] = keys[top]; continue; }
                    float t = Math.Clamp(0.5f + (sums[top] - sums[second]) * Soft, 0f, 1f);
                    pixels[y * m + x] = Blend(keys[second], keys[top], t);
                }
            }
            return pixels;
        }

        /// <summary>
        /// How fast a border pixel turns from the blend into the heavier colour as the weights part. Across a border the
        /// weight gap grows by about a quarter per pixel, so the blend spans about a pixel and a half.
        /// </summary>
        private const float Soft = 1.4f;

        /// <summary>Cubic B-spline weights of the four cells around a point at fraction <paramref name="t"/> past the second.</summary>
        private static void Spline(float t, float[] w)
        {
            float t2 = t * t, t3 = t2 * t, u = 1f - t;
            w[0] = u * u * u / 6f;
            w[1] = (3f * t3 - 6f * t2 + 4f) / 6f;
            w[2] = (-3f * t3 + 3f * t2 + 3f * t + 1f) / 6f;
            w[3] = t3 / 6f;
        }

        /// <summary>Two packed colours mixed channel by channel, <paramref name="t"/> of the way from a to b.</summary>
        private static int Blend(int a, int b, float t)
        {
            int result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int ca = (a >> shift) & 0xFF, cb = (b >> shift) & 0xFF;
                result |= ((int)(ca + (cb - ca) * t + 0.5f) & 0xFF) << shift;
            }
            return result;
        }

        private static int Pack(Color32 c) => c.r | (c.g << 8) | (c.b << 16) | (c.a << 24);

        private Task<int[]> _picture;

        /// <summary>
        /// Starts working out <see cref="Picture"/> on a worker thread; it takes seconds, which on the main thread would
        /// freeze the game right after loading.
        /// </summary>
        public void BeginPicture(Color32 sea)
        {
            var packed = new int[_cells.Length];
            int seaKey = Pack(sea);
            for (int i = 0; i < _cells.Length; i++) packed[i] = _cells[i].a == 0 ? seaKey : Pack(_cells[i]);
            int n = _n;
            _picture = Task.Run(() => Pixels(packed, n));
            _pictureClock = Stopwatch.StartNew();
        }

        private Stopwatch _pictureClock;

        /// <summary>Whether <see cref="BeginPicture"/> has finished, with or without success.</summary>
        public bool PictureReady => _picture != null && _picture.IsCompleted;

        /// <summary>The picture <see cref="BeginPicture"/> worked out. Throws what the worker threw.</summary>
        public Texture2D Picture()
        {
            var pixels = _picture.GetAwaiter().GetResult();
            int m = _n * Smooth;
            Core.Log.Msg($"map: the ground between the sheets was smoothed into {m}x{m} pixels off the main thread, ready after {_pictureClock.ElapsedMilliseconds} ms");
            var bytes = new byte[pixels.Length * 4];
            Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
            var tex = new Texture2D(m, m, TextureFormat.RGBA32, false) { name = "MapExpansions.MapFill", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.LoadRawTextureData(bytes);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Metres of map the picture covers, a little more than the map because of the whole cells.</summary>
        public float Covers => _n * Cell;
    }
}
