using System.Text.Json.Nodes;

namespace MapExpansions.Lifecycle
{
    internal static partial class Layers
    {
        internal static WorldStateDefinition ResolveState(ExpansionDefinition owner, WorldStateDefinition source)
        {
            var def = new ExpansionDefinition { Id = owner.Id + ":state:" + source.Id };
            def.Hide.AddRange(source.Rules.Select(r => r.Clone()));
            def.Copies.AddRange(source.Copies.Select(c => c.Clone()));
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
                Restore(def);
                ApplyNode(def, node, new State(), source.Layer.ResourceName, true);
            }
            var state = new WorldStateDefinition { Id = source.Id, NavigationBounds = source.NavigationBounds, Layer = source.Layer };
            state.Rules.AddRange(def.Hide);
            state.Copies.AddRange(def.Copies);
            state.Moves.AddRange(source.Moves);
            state.PropertyExtensions.AddRange(source.PropertyExtensions);
            state.NavigationProbes.AddRange(source.NavigationProbes);
            state.NavigationAgents.AddRange(source.NavigationAgents);
            return state;
        }
    }
}
