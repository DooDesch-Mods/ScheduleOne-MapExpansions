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

        /// <summary>Takes the instanced copies at the given world positions out of the draw; returns how many.</summary>
        public static int Hide(List<Vector3> positions, BuiltExpansion owner, string reason)
        {
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
            int hidden = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var data = list[i];
                if (data == null || data.PositionData == null) continue;
                try
                {
                    hidden += HideIn(data, positions, owner, reason);
                }
                catch (Exception e)
                {
                    Core.Log.Error($"instancing: '{data.Id}' could not be changed ({reason}): {e}");
                }
            }
            string line = $"instancing: {hidden} of {positions.Count} instanced objects taken out of the draw ({reason})";
            if (hidden < positions.Count) Core.Log.Warning(line + "; the rest stay drawn");
            else Core.Log.Msg(line);
            return hidden;
        }

        private static int HideIn(InstanceObjectData data, List<Vector3> positions, BuiltExpansion owner, string reason)
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
