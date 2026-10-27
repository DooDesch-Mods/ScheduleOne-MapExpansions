using MapExpansions.Lifecycle;
using MapExpansions.World;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Writes each changed expansion's layer: every entry the layer already held plus every entry changed since, as
    /// the definition holds it now. The written text also becomes the live layer, so the next world build uses it
    /// without a rebuild of the expansion.
    /// </summary>
    internal static class Saving
    {
        /// <summary>Expansions with changes that are not written yet.</summary>
        private static readonly HashSet<string> Dirty = new(StringComparer.Ordinal);

        /// <summary>Changed copies by expansion, as (owner, id), for the panel's marks.</summary>
        private static readonly Dictionary<string, HashSet<(string owner, string id)>> DirtyCopies = new(StringComparer.Ordinal);

        internal static int DirtyCount => Dirty.Count == 0 ? 0 : Math.Max(Dirty.Count, DirtyCopies.Values.Sum(s => s.Count));

        internal static bool IsDirty(MainCopies.Placed p) =>
            DirtyCopies.TryGetValue(p.Expansion.Id, out var set) && set.Contains((p.LayerOwner, p.Built.Def.Id));

        /// <summary>A copy moved or changed: its entry goes into the layer.</summary>
        internal static void MarkDirty(MainCopies.Placed p) => MarkDirty(p.Expansion.Id, p.LayerOwner, p.Built.Def.Id);

        internal static void MarkDirty(string expansionId, string owner, string copyId)
        {
            Layers.StateOf(expansionId).Copies.Add((owner, copyId));
            if (!DirtyCopies.TryGetValue(expansionId, out var set)) DirtyCopies[expansionId] = set = new HashSet<(string, string)>();
            set.Add((owner, copyId));
            Dirty.Add(expansionId);
        }

        /// <summary>Something else of the expansion's layer state changed (a rule, a box, a removal).</summary>
        internal static void MarkDirty(string expansionId) => Dirty.Add(expansionId);

        /// <summary>Saves every expansion with changes; returns how many layers were written.</summary>
        internal static int SaveAll(string why)
        {
            if (Dirty.Count == 0)
            {
                Core.Log.Msg($"save ({why}): nothing changed since the last save");
                Panel.Note("Nichts zu speichern.");
                return 0;
            }
            int written = 0;
            foreach (var expansionId in Dirty.ToList())
                if (Save(expansionId, why)) written++;
            return written;
        }

        private static bool Save(string expansionId, string why)
        {
            var def = BuildPipeline.Get(expansionId)?.Definition ?? Registry.GetDefinition(expansionId);
            if (def == null)
            {
                Core.Log.Error($"save ({why}): expansion '{expansionId}' is not registered; its changes are not written");
                Panel.Note($"Fehler: {expansionId} ist nicht gebaut.");
                return false;
            }
            var state = Layers.StateOf(expansionId);
            string json = Layers.Write(def, state);
            string path = Core.LayerFile(expansionId);
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, json);
            }
            catch (Exception e)
            {
                Core.Log.Error($"save ({why}): writing '{expansionId}' to {path} failed: {e.Message}");
                Panel.Note($"Fehler beim Speichern: {e.Message}");
                return false;
            }
            Layers.SetLive(expansionId, json);
            Dirty.Remove(expansionId);
            DirtyCopies.Remove(expansionId);
            Core.Log.Msg($"saved ({why}): '{expansionId}' layer with {state.Count} entries to {path}, {json.Length} characters");
            Panel.Note($"Gespeichert: {expansionId}, {state.Count} Einträge.");
            return true;
        }

        internal static bool HasChanges => Dirty.Count > 0;

        /// <summary>Folder of the drafts: unsaved layers, written in the background so a crash loses little.</summary>
        internal static string DraftFolder => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MapExpansionsEditor", "drafts");

        /// <summary>Writes the layer of every expansion with unsaved changes as a draft; the layer files stay as they are.</summary>
        internal static int Draft(string why)
        {
            int written = 0;
            foreach (var expansionId in Dirty.ToList())
            {
                var def = BuildPipeline.Get(expansionId)?.Definition ?? Registry.GetDefinition(expansionId);
                if (def == null)
                {
                    Core.Log.Error($"draft ({why}): expansion '{expansionId}' is not registered; no draft is written");
                    continue;
                }
                string path = Path.Combine(DraftFolder, expansionId + ".json");
                try
                {
                    Directory.CreateDirectory(DraftFolder);
                    File.WriteAllText(path, Layers.Write(def, Layers.StateOf(expansionId)));
                    written++;
                    Core.Log.Msg($"draft ({why}): '{expansionId}' written to {path}");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"draft ({why}): writing '{expansionId}' to {path} failed: {e.Message}");
                }
            }
            return written;
        }

        /// <summary>The world was rebuilt; changes that were not saved are gone with it.</summary>
        internal static void Forget(string why)
        {
            if (Dirty.Count > 0) Core.Log.Warning($"{why}: unsaved changes of {string.Join(", ", Dirty)} are dropped");
            Dirty.Clear();
            DirtyCopies.Clear();
        }
    }
}
