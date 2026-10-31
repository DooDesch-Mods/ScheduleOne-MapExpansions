namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// What a transition between two world states keeps standing. An extension stays when the next state has it with
    /// the same id and the same definition, so its used tiles, surfaces and docks never have to be removed. A surface
    /// on a copy's block also needs that copy unchanged in the next state; the copy then stays with the extension.
    /// </summary>
    internal static class WorldStatePlan
    {
        internal readonly struct Extension
        {
            public readonly string Id, Signature;
            public readonly IReadOnlyCollection<string> CarrierCopies;
            public readonly bool Damaged;
            public Extension(string id, string signature, IReadOnlyCollection<string> carrierCopies, bool damaged = false)
            {
                Id = id;
                Signature = signature;
                CarrierCopies = carrierCopies;
                Damaged = damaged;
            }
        }

        internal readonly struct Copy
        {
            public readonly string Id, Signature;
            public Copy(string id, string signature) { Id = id; Signature = signature; }
        }

        internal sealed class Result
        {
            public readonly HashSet<string> Extensions = new(StringComparer.Ordinal);
            public readonly HashSet<string> Copies = new(StringComparer.Ordinal);
        }

        internal static Result Plan(IEnumerable<Extension> previous, IEnumerable<Copy> previousCopies, IEnumerable<Extension> next, IEnumerable<Copy> nextCopies)
        {
            var result = new Result();
            var before = previousCopies.ToDictionary(c => c.Id, c => c.Signature, StringComparer.Ordinal);
            var after = nextCopies.ToDictionary(c => c.Id, c => c.Signature, StringComparer.Ordinal);
            var wanted = next.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Signature, StringComparer.Ordinal);
            foreach (var extension in previous)
            {
                if (extension.Damaged || !wanted.TryGetValue(extension.Id, out var signature) || signature != extension.Signature) continue;
                if (!extension.CarrierCopies.All(copy => CarrierStays(copy, before, after))) continue;
                result.Extensions.Add(extension.Id);
                foreach (var copy in extension.CarrierCopies)
                    if (before.ContainsKey(copy)) result.Copies.Add(copy);
            }
            return result;
        }

        // A carrier in neither state is a copy of the expansion itself, which no transition touches.
        private static bool CarrierStays(string copy, Dictionary<string, string> before, Dictionary<string, string> after)
        {
            bool had = before.TryGetValue(copy, out var old), has = after.TryGetValue(copy, out var now);
            return had == has && (!had || old == now);
        }

        /// <summary>Why a surface cannot stand on its copy's box, or null. Values of a copy's map: box id to "is a block".</summary>
        internal static string SurfaceBindingError(string surfaceId, string copyId, string boxId, IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> copies)
        {
            if (string.IsNullOrEmpty(copyId)) return null;
            if (string.IsNullOrEmpty(boxId)) return $"surface '{surfaceId}' needs a box id";
            if (!copies.TryGetValue(copyId, out var boxes)) return $"surface '{surfaceId}' names copy '{copyId}', which the state and the expansion do not have";
            if (!boxes.TryGetValue(boxId, out var block)) return $"surface '{surfaceId}' names box '{boxId}', which copy '{copyId}' does not have";
            // Only a block has a collider; a surface needs one to mount on.
            return block ? null : $"surface '{surfaceId}' names panel '{copyId}/{boxId}'; a surface needs a block";
        }
    }
}
