namespace MapExpansions.Lifecycle
{
    internal static class DockIndexPolicy
    {
        internal static void RequireAppend(int existingCount, int permanentIndex)
        {
            if (existingCount < 0 || permanentIndex != existingCount)
                throw new InvalidOperationException($"dock index {permanentIndex} must append at {existingCount}; existing delivery indices cannot move");
        }

        internal static void RequireTailRemoval(int existingCount, IEnumerable<int> removing)
        {
            var indices = removing.OrderBy(x => x).ToArray();
            if (indices.Length == 0) return;
            if (existingCount < indices.Length || indices.Distinct().Count() != indices.Length ||
                !indices.SequenceEqual(Enumerable.Range(existingCount - indices.Length, indices.Length)))
                throw new InvalidOperationException("loading docks can only be removed as a trailing group; retained delivery indices cannot move");
        }
    }
}
