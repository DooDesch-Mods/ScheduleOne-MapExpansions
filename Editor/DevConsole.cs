#if DEBUG
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Console commands that drive the editor without a mouse, for automated checks. Debug builds only.
    /// <c>mapedit open|close|state|select &lt;id&gt;|pick x y|move dx dy dz|save|target &lt;expansion&gt; &lt;file&gt;|cam x y z yaw pitch|look &lt;id&gt; [m]|
    /// fly dx dy dz seconds|hit x y|batch &lt;name&gt;|viewmodel|hold &lt;slot&gt;</c>.
    /// </summary>
    internal static class DevConsole
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static float F(string s) => float.Parse(s, Ci);

        private static int _lastFrame = -1;
        private static string _lastSig = "";

        internal static bool TryHandle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return Dispatch(raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static bool TryHandle(Il2CppSystem.Collections.Generic.List<string> args)
        {
            if (args == null || args.Count == 0) return false;
            var parts = new string[args.Count];
            for (int i = 0; i < args.Count; i++) parts[i] = args[i];
            return Dispatch(parts);
        }

        private static bool Dispatch(string[] p)
        {
            if (p.Length == 0 || !p[0].Equals("mapedit", StringComparison.OrdinalIgnoreCase)) return false;
            // The string overload calls the list overload, so one submission can reach both prefixes.
            string sig = string.Join(" ", p);
            if (Time.frameCount == _lastFrame && sig == _lastSig) return true;
            _lastFrame = Time.frameCount;
            _lastSig = sig;

            string sub = p.Length > 1 ? p[1].ToLowerInvariant() : "state";
            try
            {
                switch (sub)
                {
                    case "open": EditorMode.Open("console"); break;
                    case "close": EditorMode.Close("console"); break;
                    case "state": Core.Log.Msg($"[dev] state: {Panel.State()}"); break;
                    case "select" when p.Length > 2:
                        var c = Copies.Find(p[2]);
                        if (c == null) Core.Log.Warning($"[dev] select: no placed copy '{p[2]}'; placed: {string.Join(", ", Copies.All().Select(Copies.Key))}");
                        else Selection.Select(c, "console");
                        break;
                    case "pick" when p.Length > 3:
                        Selection.Pick(EditorMode.Camera?.Camera, Screen(p[2], p[3]), "console");
                        break;
                    case "move" when p.Length > 4:
                        if (Selection.Current == null) Core.Log.Warning("[dev] move: nothing is selected");
                        else Gizmo.Move(Selection.Current, new Vector3(F(p[2]), F(p[3]), F(p[4])), "console");
                        break;
                    case "save": Saving.SaveAll("console"); break;
                    case "target" when p.Length > 3: Core.SetLayerFile(p[2], string.Join(" ", p.Skip(3))); break;
                    case "cam" when p.Length > 6:
                        FlyCamera.Place(new Vector3(F(p[2]), F(p[3]), F(p[4])), F(p[5]), F(p[6]));
                        Core.Log.Msg($"[dev] cam: {FlyCamera.Position} yaw {FlyCamera.Yaw} pitch {FlyCamera.Pitch}");
                        break;
                    case "look" when p.Length > 2: Look(p[2], p.Length > 3 ? F(p[3]) : 0f); break;
                    case "fly" when p.Length > 5: MelonLoader.MelonCoroutines.Start(Fly(new Vector3(F(p[2]), F(p[3]), F(p[4])), F(p[5]))); break;
                    case "hit" when p.Length > 3: Hit(Screen(p[2], p[3])); break;
                    case "batch" when p.Length > 2: Batch(string.Join(" ", p.Skip(2))); break;
                    case "viewmodel": Viewmodel(); break;
                    case "hold" when p.Length > 2: Hold(int.Parse(p[2], Ci)); break;
                    case "count": Count(); break;
                    case "rules": Rules(p.Length > 2 ? p[2] : null); break;
                    case "rule" when p.Length > 3: Rule(p[2].ToLowerInvariant(), string.Join(" ", p.Skip(3))); break;
                    case "copy" when p.Length > 3: Copy(p[2].ToLowerInvariant(), p[3]); break;
                    default:
                        Core.Log.Warning($"[dev] unknown or incomplete: '{sig}'. Use mapedit open|close|state|select <id>|pick x y|move dx dy dz|save|target <expansion> <file>|cam x y z yaw pitch|look <id> [m]|fly dx dy dz s|hit x y|batch <name>|viewmodel|hold <slot>|count|rules [text]|rule apply|revert <key>|copy apply|revert <expansion/owner/id>");
                        break;
                }
            }
            catch (Exception e)
            {
                Core.Log.Error($"[dev] '{sig}' failed: {e}");
            }
            return true;
        }

        /// <summary>Pixels, or fractions of the screen when both values are at most 1.</summary>
        private static Vector2 Screen(string xs, string ys)
        {
            float x = F(xs), y = F(ys);
            if (x <= 1f && y <= 1f) return new Vector2(x * UnityEngine.Screen.width, y * UnityEngine.Screen.height);
            return new Vector2(x, y);
        }

        private static void Look(string id, float dist)
        {
            var c = Copies.Find(id);
            if (c == null)
            {
                Core.Log.Warning($"[dev] look: no placed copy '{id}'");
                return;
            }
            var b = Copies.Bounds(c);
            if (dist <= 0f) dist = Mathf.Max(4f, b.extents.magnitude * 3f);
            // From the south-west and a little above, so the gizmo's three axes all read.
            var dir = Quaternion.Euler(25f, 45f, 0f) * Vector3.forward;
            var pos = b.center - dir * dist;
            FlyCamera.Place(pos, 45f, 25f);
            Selection.Select(c, "console look");
            Core.Log.Msg($"[dev] look: camera at {pos} looking at {Copies.Key(c)} (centre {b.center}, {dist:0.0} m)");
        }

        /// <summary>
        /// Flies the camera in a camera-space direction for a while and measures two things per frame: whether
        /// anything else moved the camera between our frames, and the frame time.
        /// </summary>
        private static System.Collections.IEnumerator Fly(Vector3 dir, float seconds)
        {
            if (!EditorMode.IsOpen)
            {
                Core.Log.Warning("[dev] fly: the editor is not open");
                yield break;
            }
            FlyCamera.Scripted = dir;
            var times = new List<float>();
            float maxDrift = 0f;
            var start = FlyCamera.Position;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < seconds && EditorMode.IsOpen)
            {
                yield return null;
                var cam = EditorMode.Camera;
                if (cam == null) break;
                // Runs after the editor's own Update set the pose; a gap means another script moved the camera.
                maxDrift = Mathf.Max(maxDrift, Vector3.Distance(cam.transform.position, FlyCamera.Position));
                times.Add(Time.unscaledDeltaTime * 1000f);
            }
            FlyCamera.Scripted = Vector3.zero;
            if (times.Count == 0)
            {
                Core.Log.Warning("[dev] fly: no frame was measured");
                yield break;
            }
            times.Sort();
            float avg = times.Average();
            float p95 = times[(int)(times.Count * 0.95f)];
            float max = times[^1];
            int spikes = times.Count(t => t > avg * 2f);
            Core.Log.Msg($"[dev] fly: {times.Count} frames, {Vector3.Distance(start, FlyCamera.Position):0.0} m, frame ms avg {avg:0.0} p95 {p95:0.0} max {max:0.0}, " +
                         $"{spikes} frames over twice the average, camera drift from the set pose max {maxDrift:0.0000} m");
        }

        private static void Hit(Vector2 screen)
        {
            var hits = Panel.HitsAt(screen);
            Core.Log.Msg($"[dev] hit {screen}: over the docks {Panel.IsPointerOver(screen)}, {hits.Count} UI hits: " +
                         string.Join(", ", hits.Take(8).Select(Path)));
        }

        private static string Path(GameObject go)
        {
            var names = new List<string>();
            for (var t = go.transform; t != null && names.Count < 5; t = t.parent) names.Insert(0, t.name);
            return string.Join("/", names);
        }

        /// <summary>Static batching of the renderers whose object name contains the text, and of a placed copy by id.</summary>
        private static void Batch(string name)
        {
            var copy = Copies.Find(name);
            var rs = copy != null
                ? copy.Built.Local.GetComponentsInChildren<MeshRenderer>(true).ToList()
                : Object.FindObjectsOfType<MeshRenderer>().Where(r => r.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            int batched = rs.Count(r => r.isPartOfStaticBatch);
            Core.Log.Msg($"[dev] batch '{name}' ({(copy != null ? "placed copy" : "main world by name")}): {rs.Count} mesh renderers, {batched} part of a static batch");
            foreach (var r in rs.Take(12))
                Core.Log.Msg($"[dev]   {Path(r.gameObject)}: static batch {r.isPartOfStaticBatch}, static {r.gameObject.isStatic}, enabled {r.enabled}, at {r.transform.position}");
        }

        /// <summary>What is drawn and solid in the loaded scenes, to compare before and after an apply or revert.</summary>
        private static void Count()
        {
            var all = Object.FindObjectsOfType<Renderer>(true);
            int renderers = all.Count(r => r.enabled && r.gameObject.activeInHierarchy);
            // Copies stand below the expansions' roots, which the separate views switch off as a whole.
            int ours = all.Count(r => Root(r.transform)?.name.StartsWith("MapExpansions.", StringComparison.Ordinal) == true);
            int colliders = Object.FindObjectsOfType<Collider>().Count(c => c.enabled);
            int lights = Object.FindObjectsOfType<Light>().Count(l => l.enabled);
            var held = string.Join(", ", Lifecycle.BuildPipeline.All.Select(b => $"{b.Id} {World.ObjectRules.Counts(b.Id).rules} revertable rules"));
            Core.Log.Msg($"[dev] count: {renderers} renderers, {colliders} colliders, {lights} lights on; {ours} renderers below expansion roots; {World.MainCopies.All.Count} copies placed; " +
                         $"{World.ObjectRules.Counts("").held} components held off; {held}");
        }

        private static Transform Root(Transform t)
        {
            while (t != null && t.parent != null) t = t.parent;
            return t;
        }

        private static void Rules(string text)
        {
            foreach (var b in Lifecycle.BuildPipeline.All)
                foreach (var r in b.Definition.Hide)
                    if (text == null || r.Key.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                        Core.Log.Msg($"[dev] rule {b.Id}: '{r.Key}' {r.Effect}{(World.ObjectRules.CanRevert(r.Effect) ? "" : " (not revertable alone)")}");
        }

        private static void Rule(string op, string key)
        {
            foreach (var b in Lifecycle.BuildPipeline.All)
            {
                var rule = b.Definition.Hide.FirstOrDefault(r => r.Key == key);
                if (rule == null) continue;
                if (op == "revert") World.ObjectRules.Revert(b, rule);
                else if (op == "apply") World.ObjectRules.ApplyOne(b, Lifecycle.BuildPipeline.Index, rule);
                else Core.Log.Warning($"[dev] rule: '{op}' is neither apply nor revert");
                return;
            }
            Core.Log.Warning($"[dev] rule: no built expansion has a rule '{key}'; 'mapedit rules' lists them");
        }

        private static void Copy(string op, string key)
        {
            if (op == "revert")
            {
                var placed = Copies.Find(key);
                if (placed == null)
                {
                    Core.Log.Warning($"[dev] copy revert: no placed copy '{key}'");
                    return;
                }
                if (Selection.Current == placed) Selection.Clear();
                World.MainCopies.Revert(placed);
                return;
            }
            if (op != "apply")
            {
                Core.Log.Warning($"[dev] copy: '{op}' is neither apply nor revert");
                return;
            }
            foreach (var b in Lifecycle.BuildPipeline.All)
                foreach (var (owner, shop, d) in Lifecycle.Layers.AllCopies(b.Definition))
                {
                    if ($"{b.Id}/{owner}/{d.Id}" != key && d.Id != key) continue;
                    if (Copies.Find($"{b.Id}/{owner}/{d.Id}") != null)
                    {
                        Core.Log.Warning($"[dev] copy apply: '{b.Id}/{owner}/{d.Id}' is placed already; revert it first");
                        return;
                    }
                    World.MainCopies.ApplyOne(b, d, shop == null ? "expansion" : $"shop '{shop}'", owner);
                    return;
                }
            Core.Log.Warning($"[dev] copy apply: no definition has a copy '{key}'");
        }

        /// <summary>Equips a hotbar slot (1 based), as the number key does.</summary>
        private static void Hold(int slot)
        {
            var inv = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerInventory>.Instance;
            if (inv == null)
            {
                Core.Log.Warning("[dev] hold: no player inventory");
                return;
            }
            var hs = inv.IndexAllSlots(slot - 1);
            if (hs == null)
            {
                Core.Log.Warning($"[dev] hold: no hotbar slot {slot}");
                return;
            }
            inv.Equip(hs);
            Core.Log.Msg($"[dev] hold: slot {slot} equipped, index now {inv.EquippedSlotIndex}, item {(hs.ItemInstance != null ? hs.ItemInstance.Name : "none")}");
        }

        private static void Viewmodel()
        {
            var cam = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerCamera>.Instance;
            int layer = LayerMask.NameToLayer("Viewmodel");
            if (cam == null || layer < 0)
            {
                Core.Log.Warning($"[dev] viewmodel: camera {cam != null}, layer {layer}");
                return;
            }
            bool drawn = (cam.Camera.cullingMask & (1 << layer)) != 0;
            int active = Object.FindObjectsOfType<Renderer>().Count(r => r.gameObject.layer == layer && r.enabled && r.gameObject.activeInHierarchy);
            Core.Log.Msg($"[dev] viewmodel: the camera draws the Viewmodel layer {drawn}, {active} active renderers on it, editor open {EditorMode.IsOpen}");
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new[] { typeof(string) })]
    internal static class SubmitCommandStringPatch
    {
        private static bool Prefix(string args) => !DevConsole.TryHandle(args);
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class SubmitCommandListPatch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args) => !DevConsole.TryHandle(args);
    }
}
#endif
