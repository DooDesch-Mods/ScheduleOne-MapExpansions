using System.Diagnostics;
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

        /// <summary>The cells as a picture over the whole map, the sea where nothing was found.</summary>
        public Texture2D Picture(Color32 sea)
        {
            var pixels = new Color32[_cells.Length];
            for (int i = 0; i < _cells.Length; i++) pixels[i] = _cells[i].a == 0 ? sea : _cells[i];
            var tex = new Texture2D(_n, _n, TextureFormat.RGBA32, false) { name = "MapExpansions.MapFill", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Metres of map the picture covers, a little more than the map because of the whole cells.</summary>
        public float Covers => _n * Cell;
    }
}
