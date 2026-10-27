using System.Text.Json.Nodes;
using MapExpansions.Lifecycle;
using MapExpansions.World;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Writes each changed expansion's layer: every entry the layer already held plus every copy moved since, as
    /// the definition holds it now. The written text also becomes the live layer, so the next world build uses it
    /// without a rebuild of the expansion.
    /// </summary>
    internal static class Saving
    {
        /// <summary>Changed copies by expansion, as (owner, id).</summary>
        private static readonly Dictionary<string, HashSet<(string owner, string id)>> Dirty = new(StringComparer.Ordinal);

        internal static int DirtyCount => Dirty.Values.Sum(s => s.Count);

        internal static bool IsDirty(MainCopies.Placed p) =>
            Dirty.TryGetValue(p.Expansion.Id, out var set) && set.Contains((p.LayerOwner, p.Built.Def.Id));

        internal static void MarkDirty(MainCopies.Placed p)
        {
            if (!Dirty.TryGetValue(p.Expansion.Id, out var set)) Dirty[p.Expansion.Id] = set = new HashSet<(string, string)>();
            set.Add((p.LayerOwner, p.Built.Def.Id));
        }

        /// <summary>Saves every expansion with changes; returns how many layers were written.</summary>
        internal static int SaveAll(string why)
        {
            if (DirtyCount == 0)
            {
                Core.Log.Msg($"save ({why}): nothing changed since the last save");
                Panel.Note("Nichts zu speichern.");
                return 0;
            }
            int written = 0;
            foreach (var expansionId in Dirty.Keys.ToList())
                if (Save(expansionId, why)) written++;
            return written;
        }

        private static bool Save(string expansionId, string why)
        {
            var def = BuildPipeline.Get(expansionId)?.Definition;
            if (def == null)
            {
                Core.Log.Error($"save ({why}): expansion '{expansionId}' is not built; its changes are not written");
                Panel.Note($"Fehler: {expansionId} ist nicht gebaut.");
                return false;
            }
            var keys = new HashSet<(string owner, string id)>(Dirty[expansionId]);
            if (Layers.Layered.TryGetValue(expansionId, out var held)) keys.UnionWith(held);

            var copies = Layers.AllCopies(def);
            var entries = new List<JsonObject>();
            foreach (var (owner, id) in keys)
            {
                var match = copies.FirstOrDefault(c => c.owner == owner && c.def.Id == id);
                if (match.def == null)
                {
                    Core.Log.Warning($"save ({why}): '{expansionId}' copy '{owner}/{id}' is no longer in the code; its entry is dropped");
                    continue;
                }
                entries.Add(Layers.CopyEntry(owner, match.def));
            }

            string json = Layers.Serialize(expansionId, entries);
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
            Layers.Layered[expansionId] = keys;
            Dirty.Remove(expansionId);
            Core.Log.Msg($"saved ({why}): '{expansionId}' layer with {entries.Count} copies to {path}, {json.Length} characters");
            Panel.Note($"Gespeichert: {expansionId}, {entries.Count} Einträge.");
            return true;
        }

        /// <summary>The world was rebuilt; moves that were not saved are gone with it.</summary>
        internal static void Forget(string why)
        {
            if (DirtyCount > 0) Core.Log.Warning($"{why}: {DirtyCount} unsaved moves are dropped");
            Dirty.Clear();
        }
    }
}
