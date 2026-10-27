using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Snapping, as the toolbar sets it: a move grid, a turn step, and the faces of other copies. Each step is chosen
    /// from a short list, as in the snap menus of Unity and Unreal. The grid serves copies, points and boxes alike. The
    /// choice is kept in a file beside the bookmarks, so the next session starts with it.
    /// </summary>
    internal static class Snap
    {
        internal static readonly float[] GridSteps = { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f };
        internal static readonly float[] TurnSteps = { 1f, 5f, 10f, 15f, 30f, 45f, 90f };

        internal const float ScaleStep = 0.05f;
        /// <summary>How near a face of another copy must come before the moved selection lies against it.</summary>
        internal const float EdgeReach = 0.25f;

        private static float _grid = 0.5f;
        private static float _turn = 15f;
        private static bool _gridOn = true;
        private static bool _turnOn = true;
        private static bool _edges;
        private static bool _loaded;

        internal static float GridStep { get { Load(); return _grid; } }
        internal static float TurnStep { get { Load(); return _turn; } }
        internal static bool Grid { get { Load(); return _gridOn; } set { Load(); _gridOn = value; } }
        internal static bool Turn { get { Load(); return _turnOn; } set { Load(); _turnOn = value; } }
        internal static bool Edges { get { Load(); return _edges; } set { Load(); _edges = value; } }

        private static string FilePath => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MapExpansionsEditor", "snap.json");

        /// <summary>Switches grid, turn or edges on or off.</summary>
        internal static bool Toggle(string which, string why)
        {
            switch (which)
            {
                case "grid": Grid = !Grid; break;
                case "turn": Turn = !Turn; break;
                case "edges": Edges = !Edges; break;
                default:
                    Core.Log.Warning($"snap ({why}): '{which}' is not grid, turn or edges");
                    return false;
            }
            Core.Log.Msg($"snap ({why}): grid {Grid}, turn {Turn}, edges {Edges}");
            Write(why);
            return true;
        }

        /// <summary>Sets a step from <c>grid=1</c> or <c>turn=45</c>; the value must be one of the list.</summary>
        internal static bool SetStep(string which, string value, string why)
        {
            Load();
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            {
                Core.Log.Warning($"snap ({why}): '{value}' is no number; the step stays");
                return false;
            }
            var list = which == "grid" ? GridSteps : which == "turn" ? TurnSteps : null;
            if (list == null || Array.IndexOf(list, v) < 0)
            {
                Core.Log.Warning($"snap ({why}): {which} {v} is not one of the offered steps; the step stays");
                return false;
            }
            // Choosing a step means snapping to it.
            if (which == "grid")
            {
                _grid = v;
                _gridOn = true;
                Panel.Note($"Raster {Panel.Num(v, v < 1f ? 2 : 0)} m.");
            }
            else
            {
                _turn = v;
                _turnOn = true;
                Panel.Note($"Drehschritt {Panel.Num(v, 0)}°.");
            }
            Core.Log.Msg($"snap ({why}): grid {_grid} m ({_gridOn}), turn {_turn}° ({_turnOn})");
            Write(why);
            return true;
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            var path = FilePath;
            if (!File.Exists(path))
            {
                Core.Log.Msg($"snap: no file at {path} yet; grid {_grid} m, turn {_turn}°");
                return;
            }
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new JsonException("the file holds no object");
                float grid = (float?)o["grid"] ?? _grid;
                float turn = (float?)o["turn"] ?? _turn;
                if (Array.IndexOf(GridSteps, grid) >= 0) _grid = grid;
                else Core.Log.Warning($"snap: grid {grid} in {path} is not one of the offered steps; it stays {_grid} m");
                if (Array.IndexOf(TurnSteps, turn) >= 0) _turn = turn;
                else Core.Log.Warning($"snap: turn {turn} in {path} is not one of the offered steps; it stays {_turn}°");
                _gridOn = (bool?)o["gridOn"] ?? _gridOn;
                _turnOn = (bool?)o["turnOn"] ?? _turnOn;
                _edges = (bool?)o["edges"] ?? _edges;
                Core.Log.Msg($"snap: read from {path}: grid {_grid} m ({_gridOn}), turn {_turn}° ({_turnOn}), edges {_edges}");
            }
            catch (Exception e)
            {
                Core.Log.Error($"snap: reading {path} failed: {e.Message}; the editor starts with grid {_grid} m and turn {_turn}°");
            }
        }

        private static void Write(string why)
        {
            var path = FilePath;
            var o = new JsonObject { ["grid"] = _grid, ["turn"] = _turn, ["gridOn"] = _gridOn, ["turnOn"] = _turnOn, ["edges"] = _edges };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Core.Log.Msg($"snap ({why}): written to {path}");
            }
            catch (Exception e)
            {
                Core.Log.Error($"snap ({why}): writing {path} failed: {e.Message}; the setting lasts until the game closes");
            }
        }
    }
}
