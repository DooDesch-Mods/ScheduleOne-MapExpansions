using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Instancing;
using MapExpansions.Lifecycle;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.World
{
    /// <summary>
    /// Objects the game draws twice: a world object with its own renderer near the camera, and a GPU-instanced copy
    /// that <see cref="InstancingManager"/> draws from a baked position texture. Switching the renderer off leaves the
    /// instanced copy. An object is taken out of the instanced draw by moving its texel far below the world, where the
    /// manager's distance culling drops it. The original texture comes back on teardown.
    /// </summary>
    internal static class InstancedObjects
    {
        private const float FarBelow = -100000f;
        private const float MatchHorizontal = 0.75f;
        private const float MatchVertical = 2f;

        /// <summary>True when the renderer belongs to a world object that also has an instanced copy.</summary>
        public static bool IsInstanced(Renderer r) => r != null && r.GetComponent<InstanceWorldObject>() != null;

        /// <summary>
        /// Takes the instanced copies at the given world positions out of the draw; returns how many positions had a
        /// copy. One object can have copies in several baked sets (one per LOD), and its renderers can repeat a position.
        /// </summary>
        public static int Hide(List<Vector3> found, BuiltExpansion owner, string reason)
        {
            var positions = new List<Vector3>();
            foreach (var p in found)
                if (!positions.Any(q => (q - p).sqrMagnitude < 0.01f)) positions.Add(p);
            if (positions.Count == 0) return 0;
            var manager = Singleton<InstancingManager>.Instance;
            if (manager == null)
            {
                Core.Log.Warning($"instancing: no InstancingManager; {positions.Count} instanced objects ({reason}) stay drawn");
                return 0;
            }
            var list = manager.BackedInstanceObjects;
            if (list == null || list.Count == 0)
            {
                Core.Log.Warning($"instancing: InstancingManager has no baked objects; {positions.Count} instanced objects ({reason}) stay drawn");
                return 0;
            }
            var matched = new bool[positions.Count];
            int texels = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var data = list[i];
                if (data == null || data.PositionData == null) continue;
                try
                {
                    texels += HideIn(data, positions, matched, owner, reason);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"instancing: '{data.Id}' could not be changed ({reason}): {e}");
                }
            }
            int hidden = matched.Count(x => x);
            string line = $"instancing: {hidden} of {positions.Count} instanced objects taken out of the draw, {texels} copies ({reason})";
            if (hidden < positions.Count) Core.Log.Warning(line + "; the rest stay drawn");
            else Core.Log.Msg(line);
            return hidden;
        }

        private static int HideIn(InstanceObjectData data, List<Vector3> positions, bool[] matched, BuiltExpansion owner, string reason)
        {
            var src = data.PositionData;
            int w = src.width, h = src.height;
            var pixels = Read(src);
            var offset = data.PositionOffset;
            var done = new bool[positions.Count];
            int hidden = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                var p = new Vector3(c.r, c.g, c.b) + offset;
                for (int k = 0; k < positions.Count; k++)
                {
                    if (done[k]) continue;
                    var d = positions[k] - p;
                    if (Mathf.Abs(d.y) > MatchVertical || new Vector2(d.x, d.z).magnitude > MatchHorizontal) continue;
                    pixels[i] = new Color(c.r, FarBelow - offset.y, c.b, c.a);
                    done[k] = true;
                    matched[k] = true;
                    hidden++;
                    break;
                }
            }
            if (hidden == 0) return 0;

            var copy = owner.Own(new Texture2D(w, h, TextureFormat.RGBAFloat, false, true));
            copy.name = src.name + " (MapExpansions)";
            copy.filterMode = src.filterMode;
            copy.wrapMode = src.wrapMode;
            copy.SetPixels(pixels);
            copy.Apply(false, false);
            var original = src;
            data.PositionData = copy;
            owner.OnTeardown.Add(() =>
            {
                if (data != null && data.PositionData == copy) data.PositionData = original;
                Core.Log.Msg($"instancing: '{data?.Id}' position data restored");
            });
            Core.Log.Msg($"instancing: '{data.Id}' {hidden} instances moved out of the draw ({reason}), texture {w}x{h} {src.format}");
            return hidden;
        }

        /// <summary>One baked set whose draw object rules changed: the texture before them and the positions they hold out.</summary>
        internal sealed class HeldSet
        {
            public InstanceObjectData Data;
            public Texture2D Base;
            public Color[] BasePixels;
            public Texture2D Copy;
            /// <summary>One entry per holding rule; a position two rules hold appears twice.</summary>
            public readonly List<Vector3> Positions = new();
        }

        /// <summary>What one object rule holds out of the instanced draw, so the rule can be taken back alone.</summary>
        internal sealed class Holding
        {
            public readonly List<(HeldSet Set, Vector3 Position)> Entries = new();
            public string Reason;
        }

        private static readonly List<HeldSet> HeldSets = new();

        /// <summary>
        /// Takes the instanced copies at the given world positions out of the draw for an object rule, until
        /// <see cref="Release"/> gives them back. Every set is rebuilt from the texture it had before the first rule, so
        /// rules taken back in any order leave exactly the copies the remaining rules hold. Returns null when nothing
        /// was held.
        /// </summary>
        public static Holding Hold(List<Vector3> found, string reason)
        {
            var positions = new List<Vector3>();
            foreach (var p in found)
                if (!positions.Any(q => (q - p).sqrMagnitude < 0.01f)) positions.Add(p);
            if (positions.Count == 0) return null;
            var manager = Singleton<InstancingManager>.Instance;
            if (manager == null)
            {
                Core.Log.Warning($"instancing: no InstancingManager; {positions.Count} instanced objects ({reason}) stay drawn from afar");
                return null;
            }
            var list = manager.BackedInstanceObjects;
            if (list == null || list.Count == 0)
            {
                Core.Log.Warning($"instancing: InstancingManager has no baked objects; {positions.Count} instanced objects ({reason}) stay drawn from afar");
                return null;
            }
            var holding = new Holding { Reason = reason };
            var matched = new bool[positions.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var data = list[i];
                if (data == null || data.PositionData == null) continue;
                try
                {
                    var set = HeldSets.FirstOrDefault(s => s.Data != null && s.Data.Pointer == data.Pointer);
                    var pixels = set?.BasePixels ?? Read(data.PositionData);
                    var mine = new List<Vector3>();
                    for (int k = 0; k < positions.Count; k++)
                        if (FirstTexel(pixels, data.PositionOffset, positions[k]) >= 0)
                        {
                            mine.Add(positions[k]);
                            matched[k] = true;
                        }
                    if (mine.Count == 0) continue;
                    if (set == null)
                    {
                        set = new HeldSet { Data = data, Base = data.PositionData, BasePixels = pixels };
                        HeldSets.Add(set);
                    }
                    foreach (var p in mine)
                    {
                        set.Positions.Add(p);
                        holding.Entries.Add((set, p));
                    }
                    Rebuild(set, reason);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"instancing: '{data.Id}' could not be changed ({reason}): {e}");
                }
            }
            int hidden = matched.Count(x => x);
            string line = $"instancing: {hidden} of {positions.Count} instanced objects held out of the draw, {holding.Entries.Count} copies ({reason})";
            if (hidden < positions.Count) Core.Log.Warning(line + "; the rest stay drawn from afar");
            else Core.Log.Msg(line);
            return holding.Entries.Count > 0 ? holding : null;
        }

        /// <summary>Gives back what one rule held; copies another rule still holds stay out of the draw.</summary>
        public static void Release(Holding holding)
        {
            if (holding == null) return;
            var touched = new List<HeldSet>();
            foreach (var (set, position) in holding.Entries)
            {
                int i = set.Positions.FindIndex(p => (p - position).sqrMagnitude < 0.01f);
                if (i < 0)
                {
                    Core.Log.Warning($"instancing: release ({holding.Reason}): '{set.Data?.Id}' no longer holds {position}");
                    continue;
                }
                set.Positions.RemoveAt(i);
                if (!touched.Contains(set)) touched.Add(set);
            }
            foreach (var set in touched)
            {
                if (set.Positions.Count > 0)
                {
                    Rebuild(set, holding.Reason);
                    continue;
                }
                Restore(set, holding.Reason);
                HeldSets.Remove(set);
            }
            holding.Entries.Clear();
        }

        /// <summary>Puts every held set back to its texture before the first rule, as the world is torn down.</summary>
        public static void ReleaseAll()
        {
            foreach (var set in HeldSets) Restore(set, "teardown");
            HeldSets.Clear();
        }

        private static void Restore(HeldSet set, string reason)
        {
            if (set.Data == null)
            {
                Core.Log.Warning($"instancing: a held set is gone ({reason}); nothing to restore");
            }
            else if (set.Copy != null && set.Data.PositionData != set.Copy)
            {
                Core.Log.Warning($"instancing: '{set.Data.Id}' position data was replaced by someone else ({reason}); it stays as it is");
            }
            else
            {
                set.Data.PositionData = set.Base;
                Core.Log.Msg($"instancing: '{set.Data.Id}' position data restored ({reason})");
            }
            if (set.Copy != null) Object.Destroy(set.Copy);
            set.Copy = null;
        }

        private static void Rebuild(HeldSet set, string reason)
        {
            var src = set.Base;
            var pixels = (Color[])set.BasePixels.Clone();
            var offset = set.Data.PositionOffset;
            int moved = 0;
            foreach (var p in set.Positions.Distinct())
            {
                int i = FirstTexel(pixels, offset, p);
                if (i < 0) continue;
                var c = pixels[i];
                pixels[i] = new Color(c.r, FarBelow - offset.y, c.b, c.a);
                moved++;
            }
            var copy = new Texture2D(src.width, src.height, TextureFormat.RGBAFloat, false, true)
            {
                name = src.name + " (MapExpansions rules)",
                filterMode = src.filterMode,
                wrapMode = src.wrapMode
            };
            copy.SetPixels(pixels);
            copy.Apply(false, false);
            var old = set.Copy;
            set.Copy = copy;
            set.Data.PositionData = copy;
            if (old != null) Object.Destroy(old);
            Core.Log.Msg($"instancing: '{set.Data.Id}' {moved} instances held out of the draw ({reason}), texture {src.width}x{src.height}");
        }

        /// <summary>The first texel that has not been moved away and stands at the position, or -1.</summary>
        private static int FirstTexel(Color[] pixels, Vector3 offset, Vector3 position)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                var d = position - (new Vector3(c.r, c.g, c.b) + offset);
                if (Mathf.Abs(d.y) > MatchVertical || new Vector2(d.x, d.z).magnitude > MatchHorizontal) continue;
                return i;
            }
            return -1;
        }

        /// <summary>The texture's texels as floats; an unreadable texture goes through a float render texture.</summary>
        private static Color[] Read(Texture2D src)
        {
            if (src.isReadable) return src.GetPixels();
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                tex = new Texture2D(src.width, src.height, TextureFormat.RGBAFloat, false, true);
                tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0, false);
                tex.Apply(false);
                return tex.GetPixels();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (tex != null) Object.Destroy(tex);
            }
        }
    }
}
