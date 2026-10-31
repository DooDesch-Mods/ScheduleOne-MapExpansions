namespace MapExpansions.Lifecycle
{
    /// <summary>Resolves an authored navigation contract without substituting or dropping required profiles.</summary>
    internal static class NavigationAgentSelection
    {
        internal readonly record struct Agent(int Id, string Name);
        internal static int[] Resolve(IReadOnlyList<string> required, IReadOnlyList<Agent> registered)
        {
            if (registered.Count == 0) throw new InvalidOperationException("no registered navigation agent types");
            if (required.Count == 0) return registered.Select(a => a.Id).ToArray();
            var result = new List<int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in required)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    throw new InvalidOperationException($"required navigation agent name '{name}' is empty or repeated");
                var matches = registered.Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException($"required navigation agent '{name}' has {matches.Length} exact registered matches; available: {string.Join(", ", registered.Select(a => a.Name))}");
                result.Add(matches[0].Id);
            }
            return result.ToArray();
        }
    }
}
