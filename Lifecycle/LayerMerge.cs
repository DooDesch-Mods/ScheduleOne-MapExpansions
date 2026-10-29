using System.Globalization;

namespace MapExpansions.Lifecycle
{
    /// <summary>
    /// The part of the JSON layer that needs no game: ids for entries the code wrote without one, and laying layer
    /// entries over a list of code entries. Kept free of Unity types so the headless tests compile it alone.
    /// </summary>
    internal static class LayerMerge
    {
        /// <summary>One layer entry: its key, whether it takes the code entry out, and the parsed entry itself.</summary>
        internal readonly struct Entry<TData>
        {
            public readonly string Key;
            public readonly bool Removed;
            public readonly TData Data;

            public Entry(string key, bool removed, TData data)
            {
                Key = key;
                Removed = removed;
                Data = data;
            }
        }

        internal sealed class Result<T>
        {
            public readonly List<T> Items = new();
            /// <summary>Keys of the entries that replaced, added or removed something, in layer order.</summary>
            public readonly List<string> Layered = new();
            public int Replaced, Added, Removed;
            /// <summary>Keys of removals whose code entry does not exist; they change nothing.</summary>
            public readonly List<string> Orphans = new();
            /// <summary>Keys of entries that appear more than once in the layer; the last one counts.</summary>
            public readonly List<string> Twice = new();
            /// <summary>Keys of new entries that could not be created; they are left out.</summary>
            public readonly List<string> Failed = new();
        }

        /// <summary>
        /// Lays <paramref name="entries"/> over <paramref name="code"/>. An entry with the key of a code entry replaces
        /// it in place, a removed one takes it out, any other is added at the end. The code list is not changed.
        /// </summary>
        /// <param name="replace">Builds the replacement from the code entry and the layer entry.</param>
        /// <param name="create">Builds a new entry; null means it cannot be built from the layer alone.</param>
        public static Result<T> Merge<T, TData>(IReadOnlyList<T> code, Func<T, string> keyOf, IEnumerable<Entry<TData>> entries,
            Func<T, TData, T> replace, Func<TData, T> create)
        {
            var result = new Result<T>();
            var last = new Dictionary<string, Entry<TData>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.Key)) continue;
                if (last.ContainsKey(e.Key)) result.Twice.Add(e.Key);
                else order.Add(e.Key);
                last[e.Key] = e;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in code)
            {
                string key = keyOf(c);
                if (key == null || !last.TryGetValue(key, out var e))
                {
                    result.Items.Add(c);
                    continue;
                }
                used.Add(key);
                result.Layered.Add(key);
                if (e.Removed)
                {
                    result.Removed++;
                    continue;
                }
                result.Items.Add(replace(c, e.Data));
                result.Replaced++;
            }
            foreach (var key in order)
            {
                if (used.Contains(key)) continue;
                var e = last[key];
                if (e.Removed)
                {
                    result.Orphans.Add(key);
                    continue;
                }
                var made = create(e.Data);
                if (made == null)
                {
                    result.Failed.Add(key);
                    continue;
                }
                result.Items.Add(made);
                result.Layered.Add(key);
                result.Added++;
            }
            return result;
        }

        /// <summary>
        /// The id of an object rule the code wrote without one: side, names, area in millimetres and effect. Two rules
        /// that select the same objects the same way get the same id; <see cref="Unique"/> numbers them.
        /// </summary>
        public static string RuleId(string side, string name, string parent, float[] areaMin, float[] areaMax, string effect)
        {
            var s = new System.Text.StringBuilder();
            s.Append(Lower(side)).Append(':').Append(name ?? "");
            if (!string.IsNullOrEmpty(parent)) s.Append('@').Append(parent);
            if (areaMin != null && areaMax != null && (Differs(areaMin, areaMax)))
                s.Append('[').Append(Point(areaMin)).Append("..").Append(Point(areaMax)).Append(']');
            s.Append(':').Append(Lower(effect));
            return s.ToString();
        }

        /// <summary>Numbers repeated ids in order: the second "x" becomes "x#2", the third "x#3".</summary>
        public static List<string> Unique(IEnumerable<string> ids)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var id in ids)
            {
                seen.TryGetValue(id, out int n);
                seen[id] = ++n;
                result.Add(n == 1 ? id : id + "#" + n.ToString(CultureInfo.InvariantCulture));
            }
            return result;
        }

        /// <summary>A box id inside one copy: "panel-1", "block-2", counted per kind from 1.</summary>
        public static string BoxId(string kind, int indexOfKind) => Lower(kind) + "-" + (indexOfKind + 1).ToString(CultureInfo.InvariantCulture);

        private static bool Differs(float[] a, float[] b)
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return true;
            return false;
        }

        private static string Point(float[] p) => string.Join(",", p.Select(Number));

        /// <summary>Millimetres, invariant, without trailing zeros.</summary>
        public static string Number(float f) => Math.Round(f, 3).ToString("0.###", CultureInfo.InvariantCulture);

        private static string Lower(string s) => (s ?? "").ToLowerInvariant();
    }
}
