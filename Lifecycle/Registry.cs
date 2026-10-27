using System.Text.RegularExpressions;
using UnityEngine;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// Registered expansions and their state. Phase 1 of the load: every definition is checked once, before the
    /// menu capture. A rejected definition stays registered so its state can be read, but is never built.
    /// </summary>
    internal static class Registry
    {
        private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9_-]{1,39}$", RegexOptions.CultureInvariant);

        private sealed class Entry
        {
            public ExpansionDefinition Definition;
            public ExpansionState State;
        }

        private static readonly List<Entry> Entries = new();

        public static IReadOnlyList<string> Ids => Entries.Select(e => e.Definition.Id).ToList();

        public static bool Register(ExpansionDefinition def)
        {
            if (def == null)
            {
                Core.Log.Error("register: definition is null");
                return false;
            }
            if (string.IsNullOrEmpty(def.Id))
            {
                Core.Log.Error($"register: definition '{def.DisplayName}' has no id");
                return false;
            }
            if (Entries.Any(e => e.Definition.Id == def.Id))
            {
                Core.Log.Error($"register: id '{def.Id}' is already registered; the second definition is ignored");
                return false;
            }
            var entry = new Entry { Definition = def, State = ExpansionState.Registered };
            Entries.Add(entry);
            Core.Log.Msg($"register: '{def.Id}' ({def.DisplayName}) version {def.Version} from {System.Reflection.Assembly.GetCallingAssembly().GetName().Name}");
            Validate(entry);
            return true;
        }

        public static ExpansionState GetState(string id) =>
            Entries.FirstOrDefault(e => e.Definition.Id == id)?.State ?? ExpansionState.Rejected;

        public static ExpansionDefinition GetDefinition(string id) => Entries.FirstOrDefault(e => e.Definition.Id == id)?.Definition;

        public static void SetState(string id, ExpansionState state)
        {
            var e = Entries.FirstOrDefault(x => x.Definition.Id == id);
            if (e == null)
            {
                Core.Log.Error($"state: no expansion '{id}'");
                return;
            }
            if (e.State == state) return;
            Core.Log.Msg($"state: '{id}' {e.State} -> {state}");
            e.State = state;
        }

        /// <summary>Definitions that passed the check, in registration order.</summary>
        public static IEnumerable<ExpansionDefinition> Usable() =>
            Entries.Where(e => e.State != ExpansionState.Rejected).Select(e => e.Definition);

        private static void Validate(Entry entry)
        {
            var def = entry.Definition;
            var problems = new List<string>();
            if (!IdPattern.IsMatch(def.Id)) problems.Add($"id '{def.Id}' must be 2 to 40 characters of a-z, 0-9, '-' and '_'");
            if (def.HasSource)
            {
                if (string.IsNullOrEmpty(def.Source.RootName)) problems.Add("no source root name");
            }
            else
            {
                // Without a source the expansion's coordinates are the main world's.
                if (def.Offset != Vector3.zero || def.QuarterTurns != 0) problems.Add("without a source scene the offset is zero and there is no turn");
                if (def.Connections.Count > 0) problems.Add($"{def.Connections.Count} connections need a source scene to lead into");
                if (def.GroundPlates.Count > 0) problems.Add("ground plates need a source scene");
                if (def.SeparateViews) problems.Add("separate views need a source scene");
                foreach (var r in def.Hide.Where(r => r.Side == WorldSide.Expansion))
                    problems.Add($"rule {r} selects in the expansion, which has no source scene");
            }
            if (def.QuarterTurns < 0 || def.QuarterTurns > 3) problems.Add($"QuarterTurns {def.QuarterTurns} is outside 0..3");
            if (def.Map != null)
            {
                if (def.Map.HalfSize <= 0f) problems.Add("map sheet without a size");
                if (def.Map.Picture == MapPictureSource.Png && (def.Map.Png == null || def.Map.Png.Length == 0)) problems.Add("map sheet without its PNG");
            }

            var regionNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in def.Regions)
            {
                if (string.IsNullOrEmpty(r.Name)) problems.Add("a region has no name");
                else if (!regionNames.Add(r.Name)) problems.Add($"region name '{r.Name}' is used twice");
                if (r.Polygon.Count < 3) problems.Add($"region '{r.Name}' has {r.Polygon.Count} polygon points, at least 3 are needed");
                if (r.StandInRegion < 0 || r.StandInRegion > 5) problems.Add($"region '{r.Name}' stand-in {r.StandInRegion} is no vanilla region (0..5)");
            }

            var locationNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in def.Locations)
            {
                if (string.IsNullOrEmpty(l.Name) || l.Name.Contains(':')) problems.Add($"location name '{l.Name}' is empty or contains ':'");
                else if (!locationNames.Add(l.Name)) problems.Add($"location name '{l.Name}' is used twice");
            }

            foreach (var c in def.Connections)
            {
                string n = string.IsNullOrEmpty(c.Name) ? "(unnamed)" : c.Name;
                if (c.Route.Count < 2) problems.Add($"connection {n} has {c.Route.Count} route points, at least 2 are needed");
                for (int i = 1; i < c.Route.Count; i++)
                    if (c.Route[i].Point.Side == c.Route[i - 1].Point.Side
                        && (c.Route[i].Point.Position - c.Route[i - 1].Point.Position).sqrMagnitude < 0.25f)
                        problems.Add($"connection {n} route points {i - 1} and {i} are less than 0.5 m apart");
                if (c.RoadWidth < 3f) problems.Add($"connection {n} road width {c.RoadWidth} is below 3 m");
                if (c.CrownHeight < c.WallHeight) problems.Add($"connection {n} crown {c.CrownHeight} is below the wall height {c.WallHeight}");
                if (c.CrownHeight < 4.5f) problems.Add($"connection {n} crown {c.CrownHeight} is too low for vehicles (4.5 m)");
                if (c.LightSpacing < 4f) problems.Add($"connection {n} light spacing {c.LightSpacing} is below 4 m");
            }

            if (def.Source != null && def.Source.TestedGameVersions.Count > 0 && !def.Source.TestedGameVersions.Contains(Application.version))
                Core.Log.Warning($"validate: '{def.Id}' was checked against game {string.Join(", ", def.Source.TestedGameVersions)}, " +
                                 $"this is {Application.version}; its source content may have moved");

            var ruleKeys = LayerMerge.Unique(def.Hide.Select(r => string.IsNullOrEmpty(r.Id) ? r.MadeId() : r.Id));
            for (int i = 0; i < def.Hide.Count; i++) def.Hide[i].Key = ruleKeys[i];
            foreach (var key in ruleKeys.Where(k => k.Contains('#')))
                Core.Log.Warning($"validate: '{def.Id}': two rules select the same objects the same way; one is '{key}'");
            foreach (var c in def.Copies.Concat(def.Shops.SelectMany(s => s.Copies)))
                foreach (var id in c.Boxes.GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key))
                    problems.Add($"copy '{c.Id}' has two boxes with the id '{id}'");

            if (problems.Count > 0)
            {
                foreach (var p in problems) Core.Log.Error($"validate: '{def.Id}': {p}");
                entry.State = ExpansionState.Rejected;
                Core.Log.Error($"validate: '{def.Id}' rejected with {problems.Count} problems; it will not be built");
                return;
            }
            entry.State = ExpansionState.Validated;
            Core.Log.Msg($"validate: '{def.Id}' ok: {(def.HasSource ? "source " + def.Source.Key : "no source scene, an edit layer over the main world")}, offset {def.Offset}, {def.QuarterTurns * 90} degrees, " +
                         $"{def.Regions.Count} regions, {def.Connections.Count} connections, {def.Locations.Count} locations, {def.Hide.Count} object rules");
        }

        /// <summary>
        /// Checks that every source scene is in the game build. Runs in the main menu, where the scene list is known.
        /// </summary>
        public static void CheckScenes()
        {
            foreach (var e in Entries)
            {
                if (e.State == ExpansionState.Rejected || !e.Definition.HasSource) continue;
                string scene = e.Definition.Source.SceneName;
                if (Application.CanStreamedLevelBeLoaded(scene)) continue;
                Core.Log.Error($"validate: '{e.Definition.Id}': source scene '{scene}' is not in the game build; the expansion is rejected");
                e.State = ExpansionState.Rejected;
            }
        }
    }
}
