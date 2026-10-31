namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// Which collider groups a collider left while world states hold it out of them. Several states can hold one
    /// collider, so it goes back on the last release. A permanent detach of the main world or the map editor wins:
    /// such a collider never returns to a group.
    /// </summary>
    internal sealed class ColliderDetachLedger<TCollider, TGroup> where TCollider : class where TGroup : class
    {
        internal sealed class Entry
        {
            public TCollider Collider;
            public readonly List<TGroup> Groups = new();
            public int Holders;
            public bool Permanent;
        }

        private readonly Dictionary<IntPtr, Entry> _entries = new();

        internal int Count => _entries.Count;

        /// <summary>Adds a holder. True for the first one: the caller still has to find the collider's groups.</summary>
        internal bool Retain(IntPtr key)
        {
            bool first = !_entries.TryGetValue(key, out var entry);
            if (first) _entries[key] = entry = new Entry();
            entry.Holders++;
            return first;
        }

        internal void Record(IntPtr key, TCollider collider, TGroup group)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            entry.Collider = collider;
            entry.Groups.Add(group);
        }

        internal void MarkPermanent(IntPtr key)
        {
            if (_entries.TryGetValue(key, out var entry)) entry.Permanent = true;
        }

        /// <summary>Drops a holder. Returns the entry when its collider now goes back to its groups, otherwise null.</summary>
        internal Entry Release(IntPtr key)
        {
            if (!_entries.TryGetValue(key, out var entry) || --entry.Holders > 0) return null;
            _entries.Remove(key);
            return entry.Permanent || entry.Groups.Count == 0 ? null : entry;
        }

        internal void Clear() => _entries.Clear();
    }
}
