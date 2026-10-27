using MapExpansions.World;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>The copies MapExpansions placed in the world, with the key the editor names each one by.</summary>
    internal static class Copies
    {
        /// <summary>expansion/owner/id - unique across all expansions, and the same key the layer file uses per expansion.</summary>
        internal static string Key(MainCopies.Placed p) => $"{p.Expansion.Id}/{p.LayerOwner}/{p.Built.Def.Id}";

        internal static int Count() => MainCopies.All.Count;

        internal static IEnumerable<MainCopies.Placed> All() => MainCopies.All.Where(p => p?.Built?.Handle != null);

        internal static MainCopies.Placed Find(string keyOrId)
        {
            if (string.IsNullOrEmpty(keyOrId)) return null;
            return All().FirstOrDefault(p => Key(p) == keyOrId)
                   ?? All().FirstOrDefault(p => p.Built.Def.Id == keyOrId);
        }

        /// <summary>World bounds of the copy's renderers; a 1 m box at its root when it draws nothing.</summary>
        internal static Bounds Bounds(MainCopies.Placed p)
        {
            var root = p.Built.Handle;
            bool any = false;
            var b = new Bounds(root.position, Vector3.one);
            foreach (var r in p.Built.Handle.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }
    }
}
