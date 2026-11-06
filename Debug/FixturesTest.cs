#if DEBUG
using System.Globalization;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Tiles;
using MapExpansions.World;
using UnityEngine;
using Grid = Il2CppScheduleOne.Tiles.Grid;
using Object = UnityEngine.Object;

namespace MapExpansions.Debug
{
    /// <summary>
    /// mxfixtures                  every fixture: verdict with its first failing reason, pivot, and per tile whether it is in the
    ///                             grid, its occupants and the grid's answer for a real footprint tile
    /// mxfixtures hide|show id     SetActive(false|true) on the fixture's object
    /// mxfixtures release|unrelease id  the API's SetFixtureReleased on the fixture's object
    /// mxfixtures aim id           turns the player to the fixture's tiles and equips the first buildable item in the hotbar,
    ///                             so the game's own placement ghost asks the grid about those tiles every frame
    /// mxfixtures count            the patch counters alone, without asking the grid itself
    /// </summary>
    internal static class FixturesTest
    {
        private static FootprintTile _footprint;
        private static string _footprintSource;

        internal static void Handle(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            if (sub == "") { List(); return; }
            if (sub == "count")
            {
                Core.Log.Msg($"[dev] mxfixtures count: patch reached {PropertyFixtures.Reached} fixture-tile checks, turned {PropertyFixtures.Blocked} invalid");
                Ghost();
                return;
            }
            if (sub is not ("aim" or "hide" or "show" or "release" or "unrelease"))
            {
                Core.Log.Warning($"[dev] mxfixtures: unknown '{sub}'; use count, or aim, hide, show, release or unrelease followed by a fixture id");
                return;
            }
            if (parts.Length < 3)
            {
                Core.Log.Warning($"[dev] mxfixtures {sub}: needs a fixture id; mxfixtures lists them");
                return;
            }
            var fixture = PropertyFixtures.FindById(parts[2]);
            if (fixture == null)
            {
                Core.Log.Warning($"[dev] mxfixtures {sub}: no fixture '{parts[2]}'; {PropertyFixtures.Registered.Count} registered");
                return;
            }
            if (sub == "aim")
            {
                float? pitch = null;
                if (parts.Length > 3)
                {
                    if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float p))
                    {
                        Core.Log.Warning($"[dev] mxfixtures aim: pitch '{parts[3]}' is not a number");
                        return;
                    }
                    pitch = p;
                }
                Aim(fixture, pitch);
                return;
            }
            if (fixture.Target == null)
            {
                Core.Log.Warning($"[dev] mxfixtures {sub}: fixture '{fixture.Label}' has no object ({PropertyFixtures.Describe(PropertyFixtures.Evaluate(fixture))}), nothing to change");
                return;
            }
            switch (sub)
            {
                case "hide":
                case "show":
                    fixture.Target.gameObject.SetActive(sub == "show");
                    break;
                default:
                    if (!MapExpansionsApi.SetFixtureReleased(fixture.Target.gameObject, sub == "release"))
                    {
                        Core.Log.Warning($"[dev] mxfixtures {sub}: the API does not know the object of '{fixture.Label}' as a fixture");
                        return;
                    }
                    break;
            }
            fixture.CachedFrame = -1;
            Core.Log.Msg($"[dev] mxfixtures {sub}: '{fixture.Label}' now {PropertyFixtures.Describe(PropertyFixtures.Evaluate(fixture))}");
        }

        // The ghost of an equipped buildable is placed by BuildUpdate_Grid, which asks Grid.IsTileValidAtCoordinate for every
        // footprint tile each frame. Looking at the fixture's tiles with one equipped makes the game's own loop reach the patch.
        // The ghost stands holdDistance * (1 + flatness) in front of the camera, so a steep look puts it at the player's
        // feet. An explicit pitch (degrees below the horizon) turns the player toward the tiles and tilts only the camera.
        private static void Aim(PropertyFixtures.Fixture fixture, float? pitch)
        {
            if (fixture.Grid == null)
            {
                Core.Log.Warning($"[dev] mxfixtures aim: the grid of '{fixture.Label}' is gone");
                return;
            }
            if (!PlayerSingleton<PlayerCamera>.InstanceExists || PlayerSingleton<PlayerCamera>.Instance == null)
            {
                Core.Log.Warning("[dev] mxfixtures aim: no player camera");
                return;
            }
            if (!PlayerSingleton<PlayerInventory>.InstanceExists || PlayerSingleton<PlayerInventory>.Instance == null)
            {
                Core.Log.Warning("[dev] mxfixtures aim: no player inventory");
                return;
            }
            var centre = Vector3.zero;
            foreach (var (x, y) in fixture.Tiles)
                centre += fixture.Grid.transform.TransformPoint(new Vector3(x * Grid.TileSize, 0f, y * Grid.TileSize));
            centre /= fixture.Tiles.Count;
            var camera = PlayerSingleton<PlayerCamera>.Instance;
            if (pitch == null) camera.LookAt(centre, 0.05f);
            else if (Player.Local == null)
            {
                Core.Log.Warning("[dev] mxfixtures aim: no local player to turn");
                return;
            }
            else
            {
                var flat = centre - camera.transform.position;
                flat.y = 0f;
                float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                Player.Local.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                camera.transform.localRotation = Quaternion.Euler(pitch.Value, 0f, 0f);
            }

            var inventory = PlayerSingleton<PlayerInventory>.Instance;
            string equipped = null;
            for (int i = 0; i < inventory.hotbarSlots.Count && equipped == null; i++)
            {
                var slot = inventory.hotbarSlots[i];
                var def = slot?.ItemInstance?.Definition;
                if (def == null || def.TryCast<BuildableItemDefinition>() == null) continue;
                if (!slot.IsSelected) inventory.Equip(slot);
                equipped = $"'{def.ID}' in hotbar slot {i}";
            }
            if (equipped == null)
            {
                Core.Log.Warning($"[dev] mxfixtures aim: looking at {centre.ToString("F2")}, but the hotbar holds no buildable item; give one first");
                return;
            }
            Core.Log.Msg($"[dev] mxfixtures aim: '{fixture.Label}' looking at {centre.ToString("F2")} from {camera.transform.position.ToString("F2")} (pitch {(pitch?.ToString("F0", CultureInfo.InvariantCulture) ?? "LookAt")}), equipped {equipped}");
        }

        // Where the game's placement ghost stands right now, on the grid it snapped to, and whether the game accepts it.
        private static void Ghost()
        {
            var build = Object.FindObjectOfType<Il2CppScheduleOne.Building.BuildUpdate_Grid>();
            if (build == null)
            {
                Core.Log.Msg("[dev] mxfixtures count: no grid placement ghost is active");
                return;
            }
            var tile = build.closestIntersection?.tile;
            string where = "no tile under the ghost";
            if (tile != null && tile.OwnerGrid != null)
            {
                var local = tile.OwnerGrid.transform.InverseTransformPoint(tile.transform.position) / Grid.TileSize;
                where = $"snapped to tile {Mathf.RoundToInt(local.x)},{Mathf.RoundToInt(local.z)} of grid '{tile.OwnerGrid.name}' at {tile.transform.position.ToString("F2")}";
            }
            string ghost = build.GhostModel != null ? build.GhostModel.transform.position.ToString("F2") : "none";
            Core.Log.Msg($"[dev] mxfixtures count: ghost at {ghost}, {where}, valid {build._validPosition}");
        }

        private static void List()
        {
            var all = PropertyFixtures.Registered;
            Core.Log.Msg($"[dev] mxfixtures: {all.Count} fixtures; patch reached {PropertyFixtures.Reached} fixture-tile checks, turned {PropertyFixtures.Blocked} invalid");
            if (all.Count == 0) return;
            var footprint = Footprint();
            Core.Log.Msg($"[dev] mxfixtures: footprint tile for the grid query: {_footprintSource}");
            foreach (var f in all)
            {
                var verdict = PropertyFixtures.Evaluate(f);
                string pivot = f.Target == null ? "none" : f.Target.position.ToString("F2");
                string state = verdict == PropertyFixtures.Verdict.Standing ? "standing" : "gone (" + PropertyFixtures.Describe(verdict) + ")";
                Core.Log.Msg($"[dev] mxfixtures: {f.Label} path '{f.Path}' property '{f.PropertyCode}' {state} pivot {pivot} " +
                    $"rect x {f.MinX.ToString("F2", CultureInfo.InvariantCulture)}..{f.MaxX.ToString("F2", CultureInfo.InvariantCulture)} z {f.MinZ.ToString("F2", CultureInfo.InvariantCulture)}..{f.MaxZ.ToString("F2", CultureInfo.InvariantCulture)}");
                foreach (var (x, y) in f.Tiles)
                {
                    if (f.Grid == null)
                    {
                        Core.Log.Msg($"[dev] mxfixtures:   tile {x},{y}: grid destroyed");
                        continue;
                    }
                    var coord = new Coordinate(x, y);
                    var tile = f.Grid.GetTile(coord);
                    int occupants = tile == null ? 0 : tile.BuildableOccupants?.Count ?? 0;
                    string valid;
                    if (footprint == null) valid = "unknown (no footprint tile found)";
                    else
                    {
                        // The grid's own answer first, then the answer with the postfix, so a fixture that blocks shows as true -> false.
                        PropertyFixtures.Bypass = true;
                        bool raw;
                        try { raw = f.Grid.IsTileValidAtCoordinate(coord, footprint); }
                        finally { PropertyFixtures.Bypass = false; }
                        bool patched = f.Grid.IsTileValidAtCoordinate(coord, footprint);
                        valid = $"grid {raw}, with fixtures {patched}";
                    }
                    Core.Log.Msg($"[dev] mxfixtures:   tile {x},{y}: in grid {(tile != null ? "yes" : "no")}, occupants {occupants}, IsTileValidAtCoordinate {valid}");
                }
            }
        }

        // IsTileValidAtCoordinate wants a FootprintTile for the required offset only; any placed or prefab item has one.
        private static FootprintTile Footprint()
        {
            if (_footprint != null) return _footprint;
            foreach (var item in Resources.FindObjectsOfTypeAll<GridItem>())
            {
                var pairs = item?.CoordinateFootprintTilePairs;
                if (pairs == null || pairs.Count == 0) continue;
                var tile = pairs[0].footprintTile;
                if (tile == null) continue;
                _footprint = tile;
                _footprintSource = $"first footprint tile of '{item.name}'";
                return _footprint;
            }
            Core.Log.Warning("[dev] mxfixtures: no loaded GridItem has a footprint tile, so the grid query is skipped");
            _footprintSource = "none";
            return null;
        }
    }
}
#endif
