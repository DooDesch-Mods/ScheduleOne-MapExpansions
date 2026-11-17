using System.Text.Json.Nodes;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// World states in the JSON layer. The expansion's layer holds one section per state under <c>"states"</c>, keyed by
    /// the state id, with the arrays a layer has for copies, rules and boxes. A section lies over the state as the code
    /// and the state's own embedded layer give it, by the same rules as the base layer: the same id replaces, removed
    /// takes out, a new id adds. In the layer state the editor keeps, a state's copies and boxes have the owner
    /// <c>state:&lt;id&gt;</c> and its rules are kept per state.
    /// </summary>
    internal static partial class Layers
    {
        internal const string StatesKey = "states";
        internal const string StateOwnerPrefix = "state:";

        internal static string StateOwner(string stateId) => StateOwnerPrefix + stateId;

        /// <summary>The state id of a layer owner like <c>state:roof</c>, else null.</summary>
        internal static string StateOfOwner(string owner) =>
            owner != null && owner.StartsWith(StateOwnerPrefix, StringComparison.Ordinal) ? owner.Substring(StateOwnerPrefix.Length) : null;

        /// <summary>How logs name a layer owner: the expansion, a shop or a world state.</summary>
        internal static string OwnerLabel(string layerOwner)
        {
            if (layerOwner == ExpansionOwner) return "expansion";
            if (StateOfOwner(layerOwner) is string state) return $"state '{state}'";
            return layerOwner != null && layerOwner.StartsWith("shop:", StringComparison.Ordinal) ? $"shop '{layerOwner.Substring(5)}'" : $"'{layerOwner}'";
        }

        private static readonly HashSet<string> StateSectionKeys = new(StringComparer.Ordinal) { "copies", "models", "prefabs", "rules", "boxes" };

        /// <summary>
        /// What each state holds before the expansion layer's section lies over it: code plus the state's embedded layer.
        /// A section entry for one of these is a change; for anything else it is an addition and is written in full.
        /// </summary>
        private static readonly Dictionary<(string expansion, string state), CodeEntries> StateBases = new();

        /// <summary>The section of one state in a parsed layer, or null.</summary>
        internal static JsonObject StateSection(JsonNode root, string stateId) =>
            root?[StatesKey] is JsonObject states && states[stateId] is JsonObject s ? s : null;

        /// <summary>A detached copy of a section, to keep or write into another tree.</summary>
        internal static JsonObject CloneSection(JsonObject section) => (JsonObject)JsonNode.Parse(section.ToJsonString());

        /// <summary>Ids of every state section in a parsed layer.</summary>
        internal static IEnumerable<string> StateSectionIds(JsonNode root) =>
            root?[StatesKey] is JsonObject states ? states.Select(x => x.Key) : Enumerable.Empty<string>();

        /// <summary>The expansion's layer parsed, or null when it has none or it cannot be used (the reason is logged).</summary>
        internal static JsonNode ReadRoot(ExpansionDefinition def)
        {
            var (json, from) = Read(def);
            return json == null ? null : Parse(def, json, from);
        }

        /// <summary>
        /// The state as code, the state's embedded layer and <paramref name="section"/> give it. Entries the section lays
        /// over the state are recorded in <paramref name="record"/> with the state's owner.
        /// </summary>
        internal static WorldStateDefinition ResolveState(ExpansionDefinition owner, WorldStateDefinition source, JsonObject section = null, State record = null)
        {
            var def = new ExpansionDefinition { Id = owner.Id + ":state:" + source.Id };
            def.Hide.AddRange(source.Rules.Select(r => r.Clone()));
            def.Copies.AddRange(source.Copies.Select(c => c.Clone()));
            // Keys before any merge, so that a layer entry finds the code rule it names.
            var keys = LayerMerge.Unique(def.Hide.Select(r => string.IsNullOrEmpty(r.Id) ? r.MadeId() : r.Id));
            for (int i = 0; i < def.Hide.Count; i++) def.Hide[i].Key = keys[i];
            Restore(def);
            if (source.Layer != null)
            {
                using var stream = source.Layer.Assembly?.GetManifestResourceStream(source.Layer.ResourceName);
                if (stream == null) throw new InvalidOperationException($"state '{source.Id}' layer '{source.Layer.ResourceName}' is missing");
                using var reader = new StreamReader(stream);
                var node = Parse(def, reader.ReadToEnd(), source.Layer.ResourceName);
                if (node == null) throw new InvalidOperationException($"state '{source.Id}' layer could not be parsed");
                var allowed = new HashSet<string> { "format", "expansion", "copies", "models", "prefabs", "rules", "boxes" };
                if (node is not JsonObject obj || obj.Any(x => !allowed.Contains(x.Key)))
                    throw new InvalidOperationException($"state '{source.Id}' layer contains unsupported point or world sections");
                ApplyNode(def, node, new State(), source.Layer.ResourceName, true);
            }
            var bases = new CodeEntries
            {
                Rules = def.Hide.Select(r => r.Clone()).ToList(),
                Copies = def.Copies.Select(c => c.Clone()).ToList(),
                ShopCopies = new Dictionary<string, List<CopyDefinition>>(StringComparer.Ordinal),
            };
            StateBases[(owner.Id, source.Id)] = bases;
            if (section != null)
            {
                var unknown = section.Select(x => x.Key).Where(k => !StateSectionKeys.Contains(k)).ToList();
                if (unknown.Count > 0)
                    Core.Log.Warning($"layer: '{owner.Id}' state section '{source.Id}' has {string.Join(", ", unknown)}; a state section reads only copies, models, prefabs, rules and boxes, the rest is left out");
                var got = new State();
                ApplyNode(def, section, got, $"the layer's section for state '{source.Id}'", false, bases);
                if (record != null) Record(record, source.Id, got);
            }
            var state = new WorldStateDefinition { Id = source.Id, NavigationBounds = source.NavigationBounds, Layer = source.Layer };
            state.Rules.AddRange(def.Hide);
            state.Copies.AddRange(def.Copies);
            state.Moves.AddRange(source.Moves);
            state.PropertyExtensions.AddRange(source.PropertyExtensions);
            state.NavigationProbes.AddRange(source.NavigationProbes);
            state.NavigationAgents.AddRange(source.NavigationAgents);
            state.NavigationExistingFloor.AddRange(source.NavigationExistingFloor);
            state.WeatherCover.AddRange(source.WeatherCover);
            return state;
        }

        /// <summary>Copies what a section laid over a state into the expansion's layer state, under the state's owner.</summary>
        private static void Record(State into, string stateId, State got)
        {
            string o = StateOwner(stateId);
            foreach (var (_, id) in got.Copies) into.Copies.Add((o, id));
            foreach (var (_, id) in got.AddedCopies) into.AddedCopies.Add((o, id));
            foreach (var (_, id) in got.RemovedCopies) into.RemovedCopies.Add((o, id));
            foreach (var (_, copy, id) in got.Boxes) into.Boxes.Add((o, copy, id));
            foreach (var (_, copy, id) in got.RemovedBoxes) into.RemovedBoxes.Add((o, copy, id));
            foreach (var key in got.Rules) into.StateRules.Add((stateId, key));
            foreach (var key in got.RemovedRules) into.RemovedStateRules.Add((stateId, key));
        }

        /// <summary>Lays the entries of <paramref name="from"/> into <paramref name="into"/>, as kept after a resolve.</summary>
        internal static void Merge(State into, State from)
        {
            into.Copies.UnionWith(from.Copies);
            into.AddedCopies.UnionWith(from.AddedCopies);
            into.RemovedCopies.UnionWith(from.RemovedCopies);
            into.Boxes.UnionWith(from.Boxes);
            into.RemovedBoxes.UnionWith(from.RemovedBoxes);
            into.StateRules.UnionWith(from.StateRules);
            into.RemovedStateRules.UnionWith(from.RemovedStateRules);
            foreach (var (id, raw) in from.RawStates) into.RawStates[id] = raw;
        }

        /// <summary>Takes every entry of one state out of a layer state, before the state is resolved anew.</summary>
        internal static void ForgetState(State s, string stateId)
        {
            string o = StateOwner(stateId);
            s.Copies.RemoveWhere(x => x.owner == o);
            s.AddedCopies.RemoveWhere(x => x.owner == o);
            s.RemovedCopies.RemoveWhere(x => x.owner == o);
            s.Boxes.RemoveWhere(x => x.owner == o);
            s.RemovedBoxes.RemoveWhere(x => x.owner == o);
            s.StateRules.RemoveWhere(x => x.state == stateId);
            s.RemovedStateRules.RemoveWhere(x => x.state == stateId);
            s.RawStates.Remove(stateId);
        }

        /// <summary>Whether a state has a rule with this key before the layer's section: a removal is then written.</summary>
        internal static bool IsCodeStateRule(string expansionId, string stateId, string key) =>
            StateBases.TryGetValue((expansionId, stateId), out var c) && c.Rules.Any(r => r.Key == key);

        private static bool IsCodeStateBox(string expansionId, string stateId, string copy, string id) =>
            StateBases.TryGetValue((expansionId, stateId), out var c) && c.Copies.FirstOrDefault(x => x.Id == copy)?.Boxes.Any(x => x.Id == id) == true;

        /// <summary>One state's arrays while the layer is written.</summary>
        private sealed class SectionLists
        {
            public readonly List<JsonObject> Copies = new(), Models = new(), Prefabs = new(), Rules = new(), Boxes = new();

            public JsonObject ToNode()
            {
                var o = new JsonObject();
                if (Copies.Count > 0) o["copies"] = Sorted(Copies, "id");
                if (Models.Count > 0) o["models"] = Sorted(Models, "id");
                if (Prefabs.Count > 0) o["prefabs"] = Sorted(Prefabs, "id");
                if (Rules.Count > 0) o["rules"] = Sorted(Rules, "id");
                if (Boxes.Count > 0) o["boxes"] = Sorted(Boxes, "copy", "id");
                return o;
            }
        }

        /// <summary>Writes the state sections of a layer state into <paramref name="root"/>.</summary>
        private static void WriteStates(ExpansionDefinition def, State state, JsonObject root)
        {
            var sections = new SortedDictionary<string, SectionLists>(StringComparer.Ordinal);
            SectionLists Of(string id)
            {
                if (!sections.TryGetValue(id, out var s)) sections[id] = s = new SectionLists();
                return s;
            }

            foreach (var (owner, id) in state.Copies)
            {
                var st = StateOfOwner(owner);
                if (st == null) continue;
                var d = WorldStates.Definition(def.Id, st)?.Copies.FirstOrDefault(c => c.Id == id);
                if (d == null)
                {
                    Core.Log.Warning($"layer: write '{def.Id}': copy '{id}' of state '{st}' is no longer in the state; it is not written");
                    continue;
                }
                var entry = CopyEntry(owner, d, state.AddedCopies.Contains((owner, id)));
                entry.Remove("owner");
                var lists = Of(st);
                (!string.IsNullOrEmpty(d.Model) ? lists.Models : !string.IsNullOrEmpty(d.Prefab) ? lists.Prefabs : lists.Copies).Add(entry);
            }
            foreach (var (owner, id) in state.RemovedCopies)
                if (StateOfOwner(owner) is string st) Of(st).Copies.Add(new JsonObject { ["id"] = id, ["removed"] = true });

            foreach (var (st, key) in state.StateRules)
            {
                var r = WorldStates.Definition(def.Id, st)?.Rules.FirstOrDefault(x => x.Key == key);
                if (r == null)
                {
                    Core.Log.Warning($"layer: write '{def.Id}': rule '{key}' of state '{st}' is no longer in the state; it is not written");
                    continue;
                }
                Of(st).Rules.Add(RuleEntry(r));
            }
            foreach (var (st, key) in state.RemovedStateRules) Of(st).Rules.Add(new JsonObject { ["id"] = key, ["removed"] = true });

            foreach (var (owner, copy, id) in state.Boxes)
            {
                var st = StateOfOwner(owner);
                if (st == null) continue;
                var b = WorldStates.Definition(def.Id, st)?.Copies.FirstOrDefault(c => c.Id == copy)?.Boxes.FirstOrDefault(x => x.Id == id);
                if (b == null)
                {
                    Core.Log.Warning($"layer: write '{def.Id}': box '{copy}/{id}' of state '{st}' is no longer in the state; it is not written");
                    continue;
                }
                var entry = BoxEntry(owner, copy, b);
                entry.Remove("owner");
                Of(st).Boxes.Add(entry);
            }
            foreach (var (owner, copy, id) in state.RemovedBoxes)
            {
                var st = StateOfOwner(owner);
                if (st == null) continue;
                // A box the state never had needs no removal; the layer just leaves it out.
                if (!IsCodeStateBox(def.Id, st, copy, id)) continue;
                Of(st).Boxes.Add(new JsonObject { ["id"] = id, ["copy"] = copy, ["removed"] = true });
            }

            var states = new JsonObject();
            var ids = new SortedSet<string>(sections.Keys.Concat(state.RawStates.Keys), StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (sections.TryGetValue(id, out var lists)) states[id] = lists.ToNode();
                else states[id] = CloneSection(state.RawStates[id]);
            }
            if (states.Count > 0) root[StatesKey] = states;
        }
    }
}
