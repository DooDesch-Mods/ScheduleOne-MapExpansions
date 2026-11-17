using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace MapExpansions.World
{
    /// <summary>
    /// Reads and writes the detail layers of a terrain (grass and bush tufts). The interop has no
    /// TerrainData.GetDetailLayer or SetDetailLayer because the game never calls them, but the engine still registers
    /// both as internal calls, so they are resolved and called directly.
    /// </summary>
    internal static class DetailLayers
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetFn(IntPtr self, int xBase, int yBase, int width, int height, int layer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LayersFn(IntPtr self, int xBase, int yBase, int totalWidth, int totalHeight);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetFn(IntPtr self, int xBase, int yBase, int totalWidth, int totalHeight, int layer, IntPtr data);

        private static GetFn _get;
        private static SetFn _set;
        private static LayersFn _layers;
        private static bool _resolved;

        /// <summary>Element data of an IL2CPP array starts after klass, monitor, bounds and max_length.</summary>
        private static readonly int DataOffset = 4 * IntPtr.Size;

        private static readonly int LengthOffset = 3 * IntPtr.Size;

        /// <summary>Resolves both internal calls once; false with an error line when one is missing.</summary>
        private static bool Resolve()
        {
            if (_resolved) return _get != null && _set != null && _layers != null;
            _resolved = true;
            IntPtr get = IL2CPP.il2cpp_resolve_icall("UnityEngine.TerrainData::GetDetailLayer");
            IntPtr set = IL2CPP.il2cpp_resolve_icall("UnityEngine.TerrainData::Internal_SetDetailLayer");
            IntPtr layers = IL2CPP.il2cpp_resolve_icall("UnityEngine.TerrainData::GetSupportedLayers");
            if (get == IntPtr.Zero || set == IntPtr.Zero || layers == IntPtr.Zero)
            {
                Core.Log.Error($"terrain: detail layer calls not found (get {Found(get)}, set {Found(set)}, layers {Found(layers)})");
                return false;
            }
            _get = Marshal.GetDelegateForFunctionPointer<GetFn>(get);
            _set = Marshal.GetDelegateForFunctionPointer<SetFn>(set);
            _layers = Marshal.GetDelegateForFunctionPointer<LayersFn>(layers);
            return true;
        }

        private static string Found(IntPtr p) => p != IntPtr.Zero ? "found" : "missing";

        /// <summary>
        /// Indices of the detail layers that hold tufts anywhere on the terrain. The detailPrototypes getter is no way
        /// to count them: the interop cannot load its array type, and the engine's getter crashes the process while
        /// it builds the managed prototype objects. Empty layers need no turn.
        /// </summary>
        private static int[] UsedLayers(TerrainData td, int res)
        {
            IntPtr arr = _layers(td.Pointer, 0, 0, res, res);
            if (arr == IntPtr.Zero) return Array.Empty<int>();
            int count = (int)Marshal.ReadIntPtr(arr, LengthOffset).ToInt64();
            var used = new int[count];
            Marshal.Copy(arr + DataOffset, used, 0, count);
            return used;
        }

        /// <summary>The tufts a <see cref="Clear"/> took out of one rectangle of detail cells, so they can be put back.</summary>
        internal sealed class Cleared
        {
            internal TerrainData Data;
            internal string Terrain;
            internal int X, Y, Width, Height;
            internal readonly List<(int Layer, int[] Cells)> Layers = new();
            internal long Tufts;
        }

        /// <summary>
        /// Takes every grass and bush tuft of <paramref name="t"/> out of the detail cells under the world rectangle
        /// <paramref name="area"/> (x and z). Returns what it took, with no layers when the rectangle holds none, or null
        /// on failure with the cause in <paramref name="detail"/>.
        /// </summary>
        internal static Cleared Clear(Terrain t, Rect area, out string detail)
        {
            detail = "";
            if (!Resolve())
            {
                detail = "calls missing";
                return null;
            }
            var td = t.terrainData;
            int res = td != null ? td.detailResolution : 0;
            if (res <= 0)
            {
                detail = "no detail map";
                return null;
            }
            Vector3 pos = t.transform.position, size = td.size;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((area.xMin - pos.x) / size.x * res), 0, res);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((area.xMax - pos.x) / size.x * res), 0, res);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((area.yMin - pos.z) / size.z * res), 0, res);
            int y1 = Mathf.Clamp(Mathf.CeilToInt((area.yMax - pos.z) / size.z * res), 0, res);
            var cleared = new Cleared { Data = td, Terrain = t.name, X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 };
            if (cleared.Width <= 0 || cleared.Height <= 0)
            {
                detail = "area outside the terrain";
                return cleared;
            }
            IntPtr usedArr = _layers(td.Pointer, x0, y0, cleared.Width, cleared.Height);
            int[] used = Array.Empty<int>();
            if (usedArr != IntPtr.Zero)
            {
                int count = (int)Marshal.ReadIntPtr(usedArr, LengthOffset).ToInt64();
                used = new int[count];
                Marshal.Copy(usedArr + DataOffset, used, 0, count);
            }
            int cells = cleared.Width * cleared.Height;
            foreach (int layer in used)
            {
                var saved = new int[cells];
                if (!Exchange(td, cleared, layer, saved, toWorld: false, out detail)) return null;
                foreach (int v in saved) cleared.Tufts += v;
                cleared.Layers.Add((layer, saved));
            }
            detail = $"cells x {x0}..{x1} y {y0}..{y1} of {res}, layers {(used.Length == 0 ? "none" : string.Join(",", used))}";
            return cleared;
        }

        /// <summary>Puts back what <see cref="Clear"/> took; false with the cause when the terrain is gone or a call fails.</summary>
        internal static bool Restore(Cleared cleared, out string detail)
        {
            detail = "";
            if (cleared.Data == null)
            {
                detail = "terrain data is gone";
                return false;
            }
            foreach (var (layer, cells) in cleared.Layers)
                if (!Exchange(cleared.Data, cleared, layer, cells, toWorld: true, out detail)) return false;
            return true;
        }

        /// <summary>
        /// Reads one layer's cells of the rectangle and writes them back: zeros after copying them into
        /// <paramref name="cells"/>, or the contents of <paramref name="cells"/> when <paramref name="toWorld"/>.
        /// </summary>
        private static bool Exchange(TerrainData td, Cleared c, int layer, int[] cells, bool toWorld, out string detail)
        {
            detail = "";
            IntPtr arr = _get(td.Pointer, c.X, c.Y, c.Width, c.Height, layer);
            if (arr == IntPtr.Zero)
            {
                detail = $"layer {layer} read nothing";
                return false;
            }
            IntPtr handle = IL2CPP.il2cpp_gchandle_new(arr, true);
            try
            {
                long length = Marshal.ReadIntPtr(arr, LengthOffset).ToInt64();
                if (length != cells.Length)
                {
                    detail = $"layer {layer} has {length} cells, expected {cells.Length}";
                    return false;
                }
                IntPtr data = arr + DataOffset;
                if (toWorld) Marshal.Copy(cells, 0, data, cells.Length);
                else
                {
                    Marshal.Copy(data, cells, 0, cells.Length);
                    Marshal.Copy(new int[cells.Length], 0, data, cells.Length);
                }
                _set(td.Pointer, c.X, c.Y, c.Width, c.Height, layer, arr);
                return true;
            }
            finally
            {
                IL2CPP.il2cpp_gchandle_free(handle);
            }
        }

        /// <summary>
        /// Turns every detail layer of a square detail map clockwise by <paramref name="k"/> quarter turns seen from
        /// above, cell for cell, the same way as the heightmap. Returns the number of tufts moved, or -1 on failure.
        /// </summary>
        public static long Turn(TerrainData td, int k, out string detail)
        {
            detail = "";
            if (!Resolve())
            {
                detail = "calls missing";
                return -1;
            }
            int res = td.detailResolution;
            if (res <= 0)
            {
                detail = "none";
                return 0;
            }
            Core.Log.Msg($"terrain: '{td.name}' reading the used detail layers");
            int[] used = UsedLayers(td, res);
            if (used.Length == 0)
            {
                detail = "none";
                return 0;
            }
            int n = res - 1;
            long tufts = 0;
            var src = new int[res * res];
            var dst = new int[res * res];
            foreach (int layer in used)
            {
                Core.Log.Msg($"terrain: '{td.name}' reading detail layer {layer} at {res}");
                IntPtr arr = _get(td.Pointer, 0, 0, res, res, layer);
                if (arr == IntPtr.Zero)
                {
                    detail = $"layer {layer} read nothing";
                    return -1;
                }
                IntPtr handle = IL2CPP.il2cpp_gchandle_new(arr, true);
                try
                {
                    long length = Marshal.ReadIntPtr(arr, LengthOffset).ToInt64();
                    if (length != (long)res * res)
                    {
                        detail = $"layer {layer} has {length} cells, expected {res * res}";
                        return -1;
                    }
                    IntPtr data = arr + DataOffset;
                    Marshal.Copy(data, src, 0, src.Length);
                    // The map is [z, x]; the same source cell for a destination cell as the holes texture.
                    for (int y = 0; y < res; y++)
                        for (int x = 0; x < res; x++)
                        {
                            var (sx, sy) = k switch
                            {
                                1 => (n - y, x),
                                2 => (n - x, n - y),
                                _ => (y, n - x),
                            };
                            int v = src[sy * res + sx];
                            dst[y * res + x] = v;
                            tufts += v;
                        }
                    Marshal.Copy(dst, 0, data, dst.Length);
                    Core.Log.Msg($"terrain: '{td.name}' writing detail layer {layer}");
                    _set(td.Pointer, 0, 0, res, res, layer, arr);
                }
                finally
                {
                    IL2CPP.il2cpp_gchandle_free(handle);
                }
            }
            detail = $"layers {string.Join(",", used)} at {res}";
            return tufts;
        }
    }
}
