using HarmonyLib;
using Il2CppScheduleOne.Tiles;
using UnityEngine;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using Property = Il2CppScheduleOne.Property.Property;

namespace MapExpansions.World
{
    /// <summary>
    /// Main-world objects that occupy grid tiles. Vanilla placement asks the grid only, never the props standing on it, so
    /// a tile under a sink would accept an item once the sink is gone and refuse nothing while it stands. A fixture closes
    /// its tiles through a postfix on <see cref="Grid.IsTileValidAtCoordinate"/> exactly as long as the object really
    /// stands where it did; when another mod removes, hides or moves it, the tiles are free again with no call.
    /// </summary>
    internal static class PropertyFixtures
    {
        internal enum Verdict
        {
            Standing,
            Missing,
            Inactive,
            NoRenderer,
            MovedAway,
            Released,
        }

        internal sealed class Fixture
        {
            internal string ExtensionId;
            internal string Id;
            internal string Path;
            internal string PropertyCode;
            internal Grid Grid;
            // Kept apart because a destroyed grid can no longer tell its pointer.
            internal IntPtr GridPointer;
            internal Transform Target;
            internal bool Released;
            internal readonly List<(int x, int y)> Tiles = new();
            // Horizontal world rectangle of the tiles' positions, grown by one tile, inside which the pivot must stay.
            internal float MinX, MaxX, MinZ, MaxZ;
            internal int CachedFrame = -1;
            internal Verdict Cached;

            internal string Label => $"{ExtensionId}/{Id}";
        }

        // Grid pointer, then packed coordinate. Placement asks for every footprint tile each frame, so a miss must be two lookups.
        private static readonly Dictionary<IntPtr, Dictionary<long, List<Fixture>>> ByGrid = new();
        private static readonly List<Fixture> All = new();
        private static bool _postfixFailureLogged;

        /// <summary>Calls of the postfix that reached a fixture tile with a valid result, and how many of them it turned invalid.</summary>
        internal static long Reached, Blocked;

        /// <summary>Lets the dev command read the grid's own answer.</summary>
        internal static bool Bypass;

        internal static IReadOnlyList<Fixture> Registered => All;

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        /// <summary>Resolves the object, registers the fixture's tiles and adds it to <paramref name="owner"/>. A missing object leaves the tiles free.</summary>
        internal static void Register(string expansion, PropertyExtensionDefinition ext, PropertyFixtureDefinition def, Property prop, Grid grid, List<Fixture> owner)
        {
            var fixture = new Fixture
            {
                ExtensionId = ext.Id,
                Id = def.Id,
                Path = def.Path,
                PropertyCode = prop.PropertyCode,
                Grid = grid,
                GridPointer = grid.Pointer,
                Target = MainCopies.FindMain(def.Path),
            };
            float tile = Grid.TileSize;
            fixture.MinX = fixture.MinZ = float.MaxValue;
            fixture.MaxX = fixture.MaxZ = float.MinValue;
            foreach (var c in def.Tiles)
            {
                fixture.Tiles.Add((c.X, c.Y));
                var at = grid.transform.TransformPoint(new Vector3(c.X * tile, 0f, c.Y * tile));
                fixture.MinX = Mathf.Min(fixture.MinX, at.x - tile);
                fixture.MaxX = Mathf.Max(fixture.MaxX, at.x + tile);
                fixture.MinZ = Mathf.Min(fixture.MinZ, at.z - tile);
                fixture.MaxZ = Mathf.Max(fixture.MaxZ, at.z + tile);
            }

            if (!ByGrid.TryGetValue(grid.Pointer, out var coords)) ByGrid[grid.Pointer] = coords = new Dictionary<long, List<Fixture>>();
            foreach (var (x, y) in fixture.Tiles)
            {
                if (!coords.TryGetValue(Key(x, y), out var list)) coords[Key(x, y)] = list = new List<Fixture>();
                list.Add(fixture);
            }
            All.Add(fixture);
            owner.Add(fixture);

            if (fixture.Target == null)
            {
                Core.Log.Msg($"fixture '{def.Id}': no object at '{def.Path}', its tiles are free");
                return;
            }
            var reason = Evaluate(fixture);
            if (reason != Verdict.Standing)
            {
                Core.Log.Msg($"fixture '{def.Id}': '{def.Path}' is gone at apply ({Describe(reason)}), its tiles are free");
                return;
            }
            foreach (var (x, y) in fixture.Tiles)
            {
                var t = grid.GetTile(new Coordinate(x, y));
                if (t != null && ((t.BuildableOccupants?.Count ?? 0) > 0 || (t.OccupantTiles?.Count ?? 0) > 0))
                    Core.Log.Warning($"fixture '{def.Id}': tile {x},{y} already has {t.BuildableOccupants?.Count ?? 0} occupants while '{def.Path}' stands on it");
            }
            Core.Log.Msg($"fixture '{def.Id}': '{def.Path}' stands, {fixture.Tiles.Count} tiles closed for building ({expansion}/{ext.Id})");
        }

        internal static void Unregister(Fixture fixture)
        {
            if (ByGrid.TryGetValue(fixture.GridPointer, out var coords))
            {
                foreach (var (x, y) in fixture.Tiles)
                    if (coords.TryGetValue(Key(x, y), out var list))
                    {
                        list.Remove(fixture);
                        if (list.Count == 0) coords.Remove(Key(x, y));
                    }
                if (coords.Count == 0) ByGrid.Remove(fixture.GridPointer);
            }
            All.Remove(fixture);
        }

        internal static string Describe(Verdict v) => v switch
        {
            Verdict.Standing => "standing",
            Verdict.Missing => "missing",
            Verdict.Inactive => "inactive",
            Verdict.NoRenderer => "no visible renderer",
            Verdict.MovedAway => "moved away",
            Verdict.Released => "released",
            _ => v.ToString(),
        };

        /// <summary>Why the fixture does not stand, in the order the checks run, or <see cref="Verdict.Standing"/>.</summary>
        internal static Verdict Evaluate(Fixture f)
        {
            if (f.Target == null) return Verdict.Missing;
            var go = f.Target.gameObject;
            if (!go.activeInHierarchy) return Verdict.Inactive;
            bool visible = false;
            foreach (var r in f.Target.GetComponentsInChildren<Renderer>(false))
                if (r != null && r.enabled) { visible = true; break; }
            if (!visible) return Verdict.NoRenderer;
            var p = f.Target.position;
            if (p.x < f.MinX || p.x > f.MaxX || p.z < f.MinZ || p.z > f.MaxZ) return Verdict.MovedAway;
            if (f.Released) return Verdict.Released;
            return Verdict.Standing;
        }

        // The component walk is too much for every placement query, and one frame asks the same fixture many times.
        private static bool Stands(Fixture f)
        {
            int frame = Time.frameCount;
            if (f.CachedFrame != frame)
            {
                f.Cached = Evaluate(f);
                f.CachedFrame = frame;
            }
            return f.Cached == Verdict.Standing;
        }

        internal static Fixture Find(GameObject obj)
        {
            if (obj == null) return null;
            foreach (var f in All)
                if (f.Target != null && f.Target.gameObject.Pointer == obj.Pointer) return f;
            return null;
        }

        /// <summary>Sets the released flag of every fixture on this object. False when the object is no fixture.</summary>
        internal static bool SetReleased(GameObject obj, bool released)
        {
            bool any = false;
            foreach (var f in All)
                if (f.Target != null && obj != null && f.Target.gameObject.Pointer == obj.Pointer)
                {
                    f.Released = released;
                    f.CachedFrame = -1;
                    any = true;
                    Core.Log.Msg($"fixture '{f.Label}': {(released ? "released" : "taken back")}");
                }
            return any;
        }

        internal static Fixture FindById(string id)
        {
            foreach (var f in All)
                if (f.Id == id || f.Label == id) return f;
            return null;
        }

        /// <summary>True when the grid and coordinate belong to a standing fixture.</summary>
        internal static bool Closes(Grid grid, int x, int y)
        {
            if (!ByGrid.TryGetValue(grid.Pointer, out var coords) || !coords.TryGetValue(Key(x, y), out var list)) return false;
            foreach (var f in list)
                if (Stands(f)) return true;
            return false;
        }

        [HarmonyPatch(typeof(Grid), nameof(Grid.IsTileValidAtCoordinate))]
        internal static class IsTileValidPatch
        {
            private static void Postfix(Grid __instance, Coordinate gridCoord, ref bool __result)
            {
                if (!__result || Bypass || ByGrid.Count == 0 || __instance == null || gridCoord == null) return;
                try
                {
                    if (!ByGrid.TryGetValue(__instance.Pointer, out var coords) || !coords.ContainsKey(Key(gridCoord.x, gridCoord.y))) return;
                    Reached++;
                    if (!Closes(__instance, gridCoord.x, gridCoord.y)) return;
                    Blocked++;
                    __result = false;
                }
                catch (Exception e)
                {
                    // A throwing postfix would break placement; the result stays as the game computed it.
                    if (_postfixFailureLogged) return;
                    _postfixFailureLogged = true;
                    Core.Log.Error($"fixtures: the tile check failed, fixtures do not block this call: {e}");
                }
            }
        }
    }
}
