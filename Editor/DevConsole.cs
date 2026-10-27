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
    /// fly dx dy dz seconds|hit x y|uiclick x y [frames]|batch &lt;name&gt;|viewmodel|hold &lt;slot&gt;</c>.
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
                        var keys = new List<string>();
                        foreach (var id in p.Skip(2))
                        {
                            var c = Copies.Find(id);
                            if (c == null) Core.Log.Warning($"[dev] select: no placed copy '{id}'; placed: {string.Join(", ", Copies.All().Select(Copies.Key))}");
                            else keys.Add(Copies.Key(c));
                        }
                        Selection.Set(keys, "console");
                        break;
                    case "pick" when p.Length > 3:
                        Selection.Pick(EditorMode.Camera?.Camera, Screen(p[2], p[3]), p.Length > 4 && p[4] == "add", "console");
                        break;
                    case "frame" when p.Length > 5:
                        var a = Screen(p[2], p[3]);
                        var b = Screen(p[4], p[5]);
                        Selection.Frame(EditorMode.Camera?.Camera, Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)), false, "console");
                        break;
                    case "move" when p.Length > 4:
                        Edits.MoveBy(Selection.All, new Vector3(F(p[2]), F(p[3]), F(p[4])), "console");
                        break;
                    case "turn" when p.Length > 2:
                        Edits.Transform(Selection.All, Selection.CurrentBounds.center, Quaternion.Euler(0f, F(p[2]), 0f), 1f, Vector3.zero, $"um {p[2]}° gedreht", "console");
                        break;
                    case "scale" when p.Length > 2:
                        Edits.Transform(Selection.All, Selection.CurrentBounds.center, Quaternion.identity, F(p[2]), Vector3.zero, $"auf {p[2]} skaliert", "console");
                        break;
                    case "set" when p.Length > 3:
                        Edits.SetValue(Selection.Current, p[2], F(p[3]), "console");
                        break;
                    case "obstacle" when p.Length > 2:
                        Edits.Obstacle(Selection.All, p[2] == "on" ? true : p[2] == "off" ? false : null, "console");
                        break;
                    case "undo": Edits.Undo("console"); break;
                    case "redo": Edits.Redo("console"); break;
                    case "dup": Edits.Duplicate(Selection.All, "console"); break;
                    case "del": Edits.Delete(Selection.All, "console"); break;
                    case "drop": Edits.Drop(Selection.All, "console"); break;
                    case "focus": EditorMode.FrameSelection("console"); break;
                    case "play": EditorMode.Play("console"); break;
                    case "resume": EditorMode.Resume("console"); break;
                    case "draft": Core.Log.Msg($"[dev] draft: {Saving.Draft("console")} layers written to {Saving.DraftFolder}"); break;
                    case "mode" when p.Length > 2:
                        if (Enum.TryParse(p[2], true, out GizmoMode mode)) Gizmo.SetMode(mode, "console");
                        else Core.Log.Warning($"[dev] mode: '{p[2]}' is not move, rotate or scale");
                        break;
                    case "snap" when p.Length > 3:
                        bool on = p[3] == "on";
                        if (p[2] == "grid") Snap.Grid = on;
                        else if (p[2] == "turn") Snap.Turn = on;
                        else if (p[2] == "edges") Snap.Edges = on;
                        Core.Log.Msg($"[dev] snap: grid {Snap.Grid}, turn {Snap.Turn}, edges {Snap.Edges}");
                        break;
                    case "key" when p.Length > 2: Key(p[2]); break;
                    case "hover" when p.Length > 3:
                    {
                        // The cursor rests on a point for some frames without a button, as a hover.
                        var at = Screen(p[2], p[3]);
                        int frames = p.Length > 4 ? int.Parse(p[4], Ci) : 120;
                        EditorInput.Queue(Enumerable.Repeat(new EditorInput.Step(at, false, null), frames));
                        Core.Log.Msg($"[dev] hover: {at} for {frames} frames");
                        break;
                    }
                    case "wheel" when p.Length > 4:
                    {
                        var at = Screen(p[3], p[4]);
                        float turn = F(p[2]);
                        EditorInput.Queue(new[] { new EditorInput.Step(at, false, null), new EditorInput.Step(at, false, null, KeyCode.None, turn), new EditorInput.Step(at, false, null) });
                        Core.Log.Msg($"[dev] wheel: {turn} at {at}");
                        break;
                    }
                    case "placing" when p.Length > 2: Catalog.BeginPlace(int.Parse(p[2], Ci), "console"); break;
                    case "click" when p.Length > 3:
                        Script(Screen(p[2], p[3]), Screen(p[2], p[3]), 0, p.Length > 4 ? p[4] : null);
                        break;
                    case "drag" when p.Length > 5:
                        Script(Screen(p[2], p[3]), Screen(p[4], p[5]), p.Length > 6 ? int.Parse(p[6], Ci) : 20, p.Length > 7 ? p[7] : null);
                        break;
                    case "handles": Core.Log.Msg($"[dev] handles: {Gizmo.Describe(EditorMode.Camera?.Camera)}"); break;
                    case "sel":
                        Core.Log.Msg($"[dev] sel: {Selection.Count} selected: {string.Join(", ", Selection.All.Select(x => $"{Copies.Key(x)} at {x.Built.Handle.position} yaw {x.Built.Handle.eulerAngles.y:0.#} scale {x.Built.Def.Scale:0.###}"))}; undo {Edits.UndoCount}, redo {Edits.RedoCount}, playing {EditorMode.Playing}");
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
                    case "uiclick" when p.Length > 3: MelonLoader.MelonCoroutines.Start(UiClick(Screen(p[2], p[3]), p.Length > 4 ? int.Parse(p[4], Ci) : 30)); break;
                    case "batch" when p.Length > 2: Batch(string.Join(" ", p.Skip(2))); break;
                    case "viewmodel": Viewmodel(); break;
                    case "hold" when p.Length > 2: Hold(int.Parse(p[2], Ci)); break;
                    case "count": Count(); break;
                    case "rules": Rules(p.Length > 2 ? p[2] : null); break;
                    case "rule" when p.Length > 3: Rule(p[2].ToLowerInvariant(), string.Join(" ", p.Skip(3))); break;
                    case "copy" when p.Length > 3: Copy(p[2].ToLowerInvariant(), p[3]); break;
                    case "area" when p.Length > 2:
                        var areaKey = string.Join(" ", p.Skip(2));
                        var area = AreaRef.Parse(areaKey);
                        if (area == null) Core.Log.Warning($"[dev] area: '{areaKey}' is no key like exp/rule/key, exp/rule/key/opening/0 or exp/owner/copy/box/id");
                        else Areas.Select(area, "console");
                        break;
                    case "areahandles": Core.Log.Msg($"[dev] areahandles: {Areas.Describe(EditorMode.Camera?.Camera)}"); break;
                    case "areaset" when p.Length > 7 && Areas.Current != null:
                        var nb = new Bounds();
                        nb.SetMinMax(new Vector3(F(p[2]), F(p[3]), F(p[4])), new Vector3(F(p[5]), F(p[6]), F(p[7])));
                        AreaEdits.SetBox(Areas.Current, nb, $"{Areas.Current.Label} gesetzt", "console");
                        break;
                    case "newrule":
                        var eff = MapExpansions.ObjectEffect.Remove;
                        if (p.Length > 2 && !Enum.TryParse(p[2], true, out eff)) Core.Log.Warning($"[dev] newrule: '{p[2]}' is no effect");
                        else AreaEdits.NewRule(eff, "console");
                        break;
                    case "newbox" when p.Length > 2:
                        if (Enum.TryParse(p[2], true, out MapExpansions.BoxKind bk)) AreaEdits.NewBox(bk, "console");
                        else Core.Log.Warning($"[dev] newbox: '{p[2]}' is neither panel nor block");
                        break;
                    case "newopening": AreaEdits.NewOpening("console"); break;
                    case "point" when p.Length > 2: Point(p); break;
                    case "effect" when p.Length > 2:
                        if (Enum.TryParse(p[2], true, out MapExpansions.ObjectEffect fx))
                            AreaEdits.ChangeRule(Areas.Current, r => r.Effect = fx, $"Wirkung {fx}", "console");
                        else Core.Log.Warning($"[dev] effect: '{p[2]}' is no effect");
                        break;
                    case "material" when p.Length > 2:
                        AreaEdits.ChangeBox(Areas.Current, x => x.Material = string.Join(" ", p.Skip(2)), $"Material {string.Join(" ", p.Skip(2))}", "console");
                        break;
                    case "materials":
                        var found = AreaEdits.Materials(p.Length > 2 ? string.Join(" ", p.Skip(2)) : "", 30);
                        Core.Log.Msg($"[dev] materials: {string.Join(", ", found.Select(m => m.name))}");
                        break;
                    case "apply": AreaEdits.Apply("console"); break;
                    case "catalog" when p.Length > 2 && Enum.TryParse(p[2], true, out Catalog.Tab tab):
                        Catalog.SetTab(tab, "console", false);
                        Catalog.Search(string.Join(" ", p.Skip(3)), "console");
                        foreach (var (x, i) in Catalog.Items.Take(15).Select((x, i) => (x, i)))
                            Core.Log.Msg($"[dev] catalog {i}: {x.Label} | {x.Detail}");
                        break;
                    case "place" when p.Length > 2: Catalog.Place(int.Parse(p[2], Ci), "console"); break;
                    case "walk" when p.Length > 4: MelonLoader.MelonCoroutines.Start(Walk(F(p[2]), F(p[3]), F(p[4]))); break;
                    case "areas":
                        Core.Log.Msg($"[dev] areas: selected {Areas.Current?.ToString() ?? "none"}; {AreaEdits.PendingCount} rules wait, {AreaEdits.StaleCount} stale");
                        break;
                    default:
                        Core.Log.Warning($"[dev] unknown or incomplete: '{sig}'. Use mapedit open|close|state|select <id>...|pick x y [add]|frame x0 y0 x1 y1|move dx dy dz|turn deg|scale s|undo|redo|dup|del|drop|focus|play|resume|draft|mode m|snap grid|turn|edges on|off|sel|save|target <expansion> <file>|cam x y z yaw pitch|look <id> [m]|fly dx dy dz s|hit x y|uiclick x y [frames]|batch <name>|viewmodel|hold <slot>|count|rules [text]|rule apply|revert <key>|copy apply|revert <expansion/owner/id>|area <key>|areahandles|areaset x0 y0 z0 x1 y1 z1|newrule [effect]|newbox panel|block|newopening|effect <effect>|material <name>|materials [text]|apply|walk dx dz metres|areas");
                        break;
                }
            }
            catch (Exception e)
            {
                Core.Log.Error($"[dev] '{sig}' failed: {e}");
            }
            return true;
        }

        private static KeyCode KeyName(string name) => name.ToLowerInvariant() switch
        {
            "ctrl" => KeyCode.LeftControl,
            "alt" => KeyCode.LeftAlt,
            "shift" => KeyCode.LeftShift,
            "del" => KeyCode.Delete,
            // A bare digit is the number row, not the key code of that value.
            _ when name.Length == 1 && char.IsDigit(name[0]) => KeyCode.Alpha0 + (name[0] - '0'),
            _ => Enum.TryParse(name, true, out KeyCode k) ? k : KeyCode.None,
        };

        private static KeyCode[] Mods(string chord) =>
            string.IsNullOrEmpty(chord) ? Array.Empty<KeyCode>() : chord.Split('+').Select(KeyName).Where(k => k != KeyCode.None).ToArray();

        /// <summary>Queues one press of a key, with modifiers held: "ctrl+z", "tab", "f4".</summary>
        private static void Key(string chord)
        {
            var parts = chord.Split('+');
            var down = KeyName(parts[^1]);
            if (down == KeyCode.None)
            {
                Core.Log.Warning($"[dev] key: '{parts[^1]}' is no key name");
                return;
            }
            var held = parts.Take(parts.Length - 1).Select(KeyName).ToArray();
            EditorInput.Queue(new[] { new EditorInput.Step(null, false, held, down) });
            Core.Log.Msg($"[dev] key: {chord} queued as {down} with {string.Join("+", held)} held");
        }

        /// <summary>
        /// Queues a left-button gesture: hover at the start, press, move to the end over the given frames, release.
        /// Zero frames is a click.
        /// </summary>
        private static void Script(Vector2 from, Vector2 to, int frames, string mods)
        {
            var held = Mods(mods);
            var steps = new List<EditorInput.Step>();
            for (int i = 0; i < 3; i++) steps.Add(new EditorInput.Step(from, false, held));
            steps.Add(new EditorInput.Step(from, true, held));
            for (int i = 1; i <= frames; i++) steps.Add(new EditorInput.Step(Vector2.Lerp(from, to, i / (float)frames), true, held));
            steps.Add(new EditorInput.Step(to, true, held));
            steps.Add(new EditorInput.Step(to, false, held));
            EditorInput.Queue(steps);
            Core.Log.Msg($"[dev] {(frames == 0 ? "click" : "drag")}: {from} to {to} over {steps.Count} frames, held {string.Join("+", held)}");
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

        /// <summary>
        /// A mouse click on the panel the way uGUI's input module raises it: the pointer rests on the point for
        /// <paramref name="hoverFrames"/> frames, and each frame a target that differs from the last entered one gets
        /// PointerEnter. Then press, two frames, release; the click goes out only when press and release found the
        /// same live target. A page that rebuilds under a resting pointer shows up as many enters and no click.
        /// </summary>
        private static System.Collections.IEnumerator UiClick(Vector2 screen, int hoverFrames)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null)
            {
                Core.Log.Warning("[dev] uiclick: there is no event system");
                yield break;
            }
            UnityEngine.EventSystems.EventTrigger TriggerAt()
            {
                var hits = Panel.HitsAt(screen);
                if (hits.Count == 0) return null;
                for (var t = hits[0].transform; t != null; t = t.parent)
                {
                    var tr = t.GetComponent<UnityEngine.EventSystems.EventTrigger>();
                    if (tr != null) return tr;
                }
                return null;
            }
            UnityEngine.EventSystems.PointerEventData Data() => new(es)
            {
                position = screen,
                button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                clickCount = 1,
            };
            UnityEngine.EventSystems.EventTrigger entered = null;
            int enters = 0;
            void Move()
            {
                var now = TriggerAt();
                if (now == null || (entered != null && now.GetInstanceID() == entered.GetInstanceID())) return;
                entered = now;
                enters++;
                now.OnPointerEnter(Data());
            }
            for (int i = 0; i < hoverFrames; i++)
            {
                Move();
                yield return null;
            }
            int hoverEnters = enters;
            var pressed = TriggerAt();
            if (pressed == null)
            {
                Core.Log.Warning($"[dev] uiclick {screen}: nothing that takes clicks under the point");
                yield break;
            }
            string pressedName = Path(pressed.gameObject);
            pressed.OnPointerDown(Data());
            for (int i = 0; i < 2; i++)
            {
                yield return null;
                Move();
            }
            var released = TriggerAt();
            bool same = released != null && pressed.GetInstanceID() == released.GetInstanceID();
            pressed.OnPointerUp(Data());
            if (same) released.OnPointerClick(Data());
            Core.Log.Msg($"[dev] uiclick {screen}: {hoverEnters} enters while resting {hoverFrames} frames, {enters - hoverEnters} more while pressed; " +
                         $"pressed '{pressedName}', released on {(released == null ? "nothing" : "'" + Path(released.gameObject) + "'")}, " +
                         (same ? "click sent" : "no click (press and release on different targets)"));
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
                ? copy.Built.Handle.GetComponentsInChildren<MeshRenderer>(true).ToList()
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

        /// <summary>mapedit point list|markers|new kind [id]|select key|here|add|remove|delete|set field value</summary>
        private static void Point(string[] p)
        {
            switch (p[2].ToLowerInvariant())
            {
                case "list":
                    foreach (var (b, kind, id) in Points.Entries())
                    {
                        var e = MapExpansions.Lifecycle.Layers.PointEntry(b.Definition, kind, id);
                        Core.Log.Msg($"[dev] point {b.Id}/{kind}/{id}: {e?.ToJsonString()}");
                    }
                    break;
                case "markers": Core.Log.Msg($"[dev] point markers: {Points.Describe(EditorMode.Camera?.Camera)}"); break;
                case "new" when p.Length > 3: Points.New(p[3], p.Length > 4 ? p[4] : null, "console"); break;
                case "select" when p.Length > 3:
                    var r = PointRef.Parse(p[3]);
                    if (r == null) Core.Log.Warning($"[dev] point select: '{p[3]}' is no key like exp/kind/id/index");
                    else Points.Select(r, "console");
                    break;
                case "here": Points.Here("console"); break;
                case "play": Points.PlayHere("console"); break;
                case "add": Points.AddSub("console"); break;
                case "remove": Points.RemoveSub("console"); break;
                case "delete": Points.Delete("console"); break;
                case "set" when p.Length > 4: Points.SetField(p[3], string.Join(" ", p.Skip(4)), "console"); break;
                default:
                    Core.Log.Warning($"[dev] point: '{string.Join(" ", p.Skip(2))}' is not list, markers, new <kind> [id], select <key>, here, play, add, remove, delete or set <field> <value>");
                    break;
            }
        }

        private static void Rules(string text)
        {
            foreach (var b in Lifecycle.BuildPipeline.All)
                foreach (var r in b.Definition.Hide)
                    if (text == null || r.Key.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                        Core.Log.Msg($"[dev] rule {b.Id}: '{r.Key}' {r.Effect}{(World.ObjectRules.CanRevert(r.Effect) ? "" : " (not revertable alone)")}");
        }

        /// <summary>
        /// Walks the player through the character controller towards a direction, as walking keys would, and logs
        /// where the walk ended and what stopped it. Runs while the editor is closed or playing.
        /// </summary>
        private static System.Collections.IEnumerator Walk(float dx, float dz, float metres)
        {
            if (EditorMode.IsOpen && !EditorMode.Playing)
            {
                Core.Log.Warning("[dev] walk: the editor holds the player; play (Tab) or close it first");
                yield break;
            }
            var pm = Il2CppScheduleOne.DevUtilities.PlayerSingleton<Il2CppScheduleOne.PlayerScripts.PlayerMovement>.Instance;
            var cc = pm != null ? pm.Controller : null;
            if (cc == null)
            {
                Core.Log.Warning("[dev] walk: the player has no character controller");
                yield break;
            }
            var dir = new Vector3(dx, 0f, dz).normalized;
            var start = cc.transform.position;
            const float speed = 3f;
            float moved = 0f, stuck = 0f;
            var last = start;
            Collider blocker = null;
            while (moved < metres && stuck < 0.5f)
            {
                var flags = cc.Move(dir * speed * Time.deltaTime + Vector3.down * 2f * Time.deltaTime);
                var now = cc.transform.position;
                var step = Vector3.Dot(now - last, dir);
                moved += Mathf.Max(0f, step);
                stuck = step < speed * Time.deltaTime * 0.2f ? stuck + Time.deltaTime : 0f;
                if ((flags & CollisionFlags.Sides) != 0 && Physics.Raycast(now + Vector3.up * 0.9f, dir, out var hit, cc.radius + 0.3f, ~0, QueryTriggerInteraction.Ignore))
                    blocker = hit.collider;
                last = now;
                yield return null;
            }
            var end = cc.transform.position;
            if (moved >= metres) Core.Log.Msg($"[dev] walk: walked {moved:0.00} m of {metres:0.00} from {start} to {end}, nothing stopped the player");
            else Core.Log.Msg($"[dev] walk: stopped after {moved:0.00} m of {metres:0.00} at {end}, against '{(blocker == null ? "?" : World.MeshCarver.Path(blocker.transform))}'");
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
                Selection.Remove(Copies.Key(placed));
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
