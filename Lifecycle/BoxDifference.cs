namespace MapExpansions.Lifecycle
{
    /// <summary>Partitions the part of a box outside a rectangular opening into non-overlapping solid boxes.</summary>
    internal static class BoxDifference
    {
        internal readonly record struct Box(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)
        {
            internal float Volume => (MaxX - MinX) * (MaxY - MinY) * (MaxZ - MinZ);
        }
        internal static IReadOnlyList<Box> Subtract(Box source, Box opening)
        {
            float x0 = MathF.Max(source.MinX, opening.MinX), y0 = MathF.Max(source.MinY, opening.MinY), z0 = MathF.Max(source.MinZ, opening.MinZ);
            float x1 = MathF.Min(source.MaxX, opening.MaxX), y1 = MathF.Min(source.MaxY, opening.MaxY), z1 = MathF.Min(source.MaxZ, opening.MaxZ);
            var result = new List<Box>();
            if (x1 <= x0 || y1 <= y0 || z1 <= z0) { result.Add(source); return result; }
            void Add(Box b) { if (b.MaxX > b.MinX && b.MaxY > b.MinY && b.MaxZ > b.MinZ) result.Add(b); }
            Add(new(source.MinX, source.MinY, source.MinZ, x0, source.MaxY, source.MaxZ));
            Add(new(x1, source.MinY, source.MinZ, source.MaxX, source.MaxY, source.MaxZ));
            Add(new(x0, source.MinY, source.MinZ, x1, y0, source.MaxZ));
            Add(new(x0, y1, source.MinZ, x1, source.MaxY, source.MaxZ));
            Add(new(x0, y0, source.MinZ, x1, y1, z0));
            Add(new(x0, y0, z1, x1, y1, source.MaxZ));
            return result;
        }
    }
}
