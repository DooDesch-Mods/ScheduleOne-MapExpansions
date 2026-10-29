using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Camera bookmarks: Ctrl and a digit keeps the camera pose under that digit, the digit alone flies back to it.
    /// The poses are world positions, so one file serves every save; it lies beside the drafts.
    /// </summary>
    internal static class Bookmarks
    {
        private static readonly SortedDictionary<int, (Vector3 pos, float yaw, float pitch)> Saved = new();
        private static bool _loaded;

        private static string FilePath => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MapExpansionsEditor", "bookmarks.json");

        internal static void Store(int slot, string why)
        {
            Load();
            if (FlyCamera.Top)
            {
                Core.Log.Msg($"bookmark ({why}): {slot} is not kept in the top view");
                Panel.Note("In der Draufsicht wird keine Kamera gemerkt.");
                return;
            }
            Saved[slot] = (FlyCamera.Position, FlyCamera.Yaw, FlyCamera.Pitch);
            Core.Log.Msg($"bookmark ({why}): {slot} at {FlyCamera.Position}, yaw {FlyCamera.Yaw:0.#}, pitch {FlyCamera.Pitch:0.#}");
            Panel.Note($"Kamera {slot} gemerkt.");
            Write(why);
        }

        internal static void Jump(int slot, string why)
        {
            Load();
            if (!Saved.TryGetValue(slot, out var pose))
            {
                Core.Log.Msg($"bookmark ({why}): {slot} is empty");
                Panel.Note($"Kamera {slot} ist nicht belegt.");
                return;
            }
            FlyCamera.SetTop(false, EditorMode.Camera?.Camera, why);
            FlyCamera.Place(pose.pos, pose.yaw, pose.pitch);
            Core.Log.Msg($"bookmark ({why}): camera to {slot} at {pose.pos}");
            Panel.Note($"Kamera {slot}.");
        }

        internal static JsonArray State()
        {
            Load();
            return new JsonArray(Saved.Keys.Select(k => (JsonNode)k).ToArray());
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            var path = FilePath;
            if (!File.Exists(path))
            {
                Core.Log.Msg($"bookmarks: no file at {path} yet");
                return;
            }
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (root == null) throw new JsonException("the file holds no object");
                foreach (var (key, value) in root)
                {
                    var a = value?.AsArray();
                    if (!int.TryParse(key, out int slot) || a == null || a.Count != 5)
                    {
                        Core.Log.Warning($"bookmarks: entry '{key}' in {path} is unreadable; it is skipped");
                        continue;
                    }
                    Saved[slot] = (new Vector3((float)a[0], (float)a[1], (float)a[2]), (float)a[3], (float)a[4]);
                }
                Core.Log.Msg($"bookmarks: {Saved.Count} read from {path}");
            }
            catch (Exception e)
            {
                Core.Log.Error($"bookmarks: reading {path} failed: {e.Message}; the editor starts without bookmarks");
            }
        }

        private static void Write(string why)
        {
            var path = FilePath;
            var root = new JsonObject();
            foreach (var (slot, (pos, yaw, pitch)) in Saved)
                root[slot.ToString()] = new JsonArray(Math.Round(pos.x, 2), Math.Round(pos.y, 2), Math.Round(pos.z, 2), Math.Round(yaw, 1), Math.Round(pitch, 1));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Core.Log.Msg($"bookmarks ({why}): {Saved.Count} written to {path}");
            }
            catch (Exception e)
            {
                Core.Log.Error($"bookmarks ({why}): writing {path} failed: {e.Message}; the bookmark lasts until the game closes");
                Panel.Note("Kamera gemerkt, aber nicht gespeichert, siehe Log.");
            }
        }
    }
}
