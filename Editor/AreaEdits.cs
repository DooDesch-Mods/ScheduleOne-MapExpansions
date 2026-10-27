using Il2CppInterop.Runtime;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Steps on rules and on the boxes of copies. Hiding rules apply at once. Rules that change meshes or colliders
    /// (hollow, cut and the like) wait until "Anwenden", because each one rebuilds what it touches. Hollow rules can be
    /// taken back alone; the other expensive ones stay in the world until it is built again.
    /// </summary>
    internal static class AreaEdits
    {
        /// <summary>Rules changed in the definition that are not applied yet, by expansion and key.</summary>
        private static readonly HashSet<(string exp, string key)> Pending = new();

        /// <summary>Rules whose older form still stands in the world, because it cannot be taken back alone.</summary>
        private static readonly HashSet<(string exp, string key)> Stale = new();

        internal static int PendingCount => Pending.Count;
        internal static int StaleCount => Stale.Count;
        internal static bool IsPending(string exp, string key) => Pending.Contains((exp, key));
        internal static bool IsStale(string exp, string key) => Stale.Contains((exp, key));

        /// <summary>Effects that rebuild what they touch, so they wait for "Anwenden".</summary>
        internal static bool Expensive(ObjectEffect e) => e is not (ObjectEffect.Hide or ObjectEffect.NoCollision or ObjectEffect.Remove);

        internal static void Forget()
        {
            Pending.Clear();
            Stale.Clear();
        }

        private static void SetIn<T>(HashSet<T> set, T item, bool on)
        {
            if (on) set.Add(item);
            else set.Remove(item);
        }

        // ---- rules

        internal sealed class RuleEdit : Edits.Edit
        {
            public string Expansion, Key;
            public ObjectRule Before, After;
            public bool LayerBefore, RemovedBefore, LayerAfter, RemovedAfter;

            public override bool Do() => Put(Expansion, Key, After, LayerAfter, RemovedAfter, "do");

            public override void Undo() => Put(Expansion, Key, Before, LayerBefore, RemovedBefore, "undo");
        }

        /// <summary>Puts a rule into the definition under its key, or takes it out with null, and into the world.</summary>
        private static bool Put(string exp, string key, ObjectRule value, bool inLayer, bool removed, string why)
        {
            var b = BuildPipeline.Get(exp);
            if (b == null)
            {
                Core.Log.Error($"rule ({why}): expansion '{exp}' is not built; rule '{key}' stays as it is");
                return false;
            }
            var hide = b.Definition.Hide;
            var cur = hide.FirstOrDefault(r => r.Key == key);
            int index = cur == null ? hide.Count : hide.IndexOf(cur);
            if (cur != null)
            {
                TakeBack(b, cur, why);
                hide.Remove(cur);
            }
            Pending.Remove((exp, key));
            if (value != null)
            {
                var v = value.Clone();
                v.Key = key;
                hide.Insert(Mathf.Clamp(index, 0, hide.Count), v);
                if (Expensive(v.Effect))
                {
                    Pending.Add((exp, key));
                    Core.Log.Msg($"rule ({why}): '{exp}' {v} is {v.Effect}; it waits for Anwenden ({Pending.Count} waiting)");
                }
                else ObjectRules.ApplyOne(b, BuildPipeline.Index, v);
            }
            var state = Layers.StateOf(exp);
            SetIn(state.Rules, key, inLayer);
            SetIn(state.RemovedRules, key, removed);
            Saving.MarkDirty(exp);
            Core.Log.Msg($"rule ({why}): '{exp}' rule '{key}' {(value == null ? "taken out" : "put in")}; layer entry {inLayer}, removal {removed}");
            return true;
        }

        /// <summary>Takes a rule out of the world as far as it can be.</summary>
        private static void TakeBack(BuiltExpansion b, ObjectRule cur, string why)
        {
            if (Pending.Contains((b.Id, cur.Key))) return;
            if (ObjectRules.IsApplied(b.Id, cur))
            {
                ObjectRules.Revert(b, cur);
                Stale.Remove((b.Id, cur.Key));
                return;
            }
            if (!ObjectRules.CanRevert(cur.Effect))
            {
                Stale.Add((b.Id, cur.Key));
                Core.Log.Warning($"rule ({why}): '{b.Id}' {cur} ({cur.Effect}) cannot be taken back alone; it stays in the world until the save is loaded again");
                Panel.Note($"{cur.Key}: alte Form bleibt bis zum Neuladen");
            }
        }

        private static RuleEdit EditOf(BuiltExpansion b, ObjectRule before, ObjectRule after, string key, string what)
        {
            var state = Layers.StateOf(b.Id);
            bool code = Layers.IsCodeRule(b.Id, key);
            return new RuleEdit
            {
                What = what,
                Expansion = b.Id,
                Key = key,
                Before = before?.Clone(),
                After = after?.Clone(),
                LayerBefore = state.Rules.Contains(key),
                RemovedBefore = state.RemovedRules.Contains(key),
                // A changed rule is a layer entry; a deleted code rule stays out as a removal.
                LayerAfter = after != null,
                RemovedAfter = after == null && code,
            };
        }

        /// <summary>Changes the selected rule's copy by a callback, as one step.</summary>
        internal static bool ChangeRule(AreaRef a, Action<ObjectRule> change, string what, string why)
        {
            var b = a?.Built;
            var r = a?.RuleDef;
            if (b == null || r == null)
            {
                Core.Log.Warning($"rule ({why}): {a?.ToString() ?? "nothing"} is not a rule of a built expansion; nothing changes");
                return false;
            }
            var after = r.Clone();
            change(after);
            return Edits.Run(EditOf(b, r, after, r.Key, what), why);
        }

        // ---- boxes of copies

        internal sealed class BoxEdit : Edits.Edit
        {
            public AreaRef Box;
            public BoxDefinition Before, After;
            public bool LayerBefore, RemovedBefore, LayerAfter, RemovedAfter;

            public override bool Do() => PutBox(Box, After, LayerAfter, RemovedAfter, "do");

            public override void Undo() => PutBox(Box, Before, LayerBefore, RemovedBefore, "undo");
        }

        private static string OwnerLabel(string layerOwner) =>
            layerOwner == Layers.ExpansionOwner ? "expansion" : $"shop '{layerOwner.Substring(5)}'";

        private static bool PutBox(AreaRef a, BoxDefinition value, bool inLayer, bool removed, string why)
        {
            var b = a.Built;
            var d = a.CopyDef;
            if (b == null || d == null)
            {
                Core.Log.Error($"box ({why}): copy '{a.Expansion}/{a.Owner}/{a.Copy}' is not in a built expansion; box '{a.Box}' stays as it is");
                return false;
            }
            var cur = d.Boxes.FirstOrDefault(x => x.Id == a.Box);
            int index = cur == null ? d.Boxes.Count : d.Boxes.IndexOf(cur);
            var p = a.Placed;
            if (cur != null)
            {
                d.Boxes.Remove(cur);
                var go = p?.Built?.Local == null ? null : p.Built.Local.transform.Find(MainCopies.BoxName(cur));
                if (go != null)
                {
                    go.gameObject.SetActive(false);
                    Object.Destroy(go.gameObject);
                }
                else if (p?.Built?.Local != null) Core.Log.Warning($"box ({why}): '{a}' has no object '{MainCopies.BoxName(cur)}' below its copy");
            }
            if (value != null)
            {
                var v = value.Clone();
                v.Id = a.Box;
                d.Boxes.Insert(Mathf.Clamp(index, 0, d.Boxes.Count), v);
                if (p?.Built?.Local != null)
                {
                    if (MainCopies.AddBox(b, d, OwnerLabel(a.Owner), p.Built.Local.transform, p.Built.Move, v) != null) Physics.SyncTransforms();
                }
                else Core.Log.Warning($"box ({why}): the copy of '{a}' is not built; only its definition changes");
            }
            var state = Layers.StateOf(a.Expansion);
            SetIn(state.Boxes, (a.Owner, a.Copy, a.Box), inLayer);
            SetIn(state.RemovedBoxes, (a.Owner, a.Copy, a.Box), removed);
            Saving.MarkDirty(a.Expansion, a.Owner, a.Copy);
            Core.Log.Msg($"box ({why}): '{a}' {(value == null ? "taken out" : $"put in, {value.Kind} {value.Box.min} to {value.Box.max}")}");
            return true;
        }

        private static BoxEdit BoxEditOf(AreaRef a, BoxDefinition before, BoxDefinition after, string what)
        {
            var state = Layers.StateOf(a.Expansion);
            var k = (a.Owner, a.Copy, a.Box);
            return new BoxEdit
            {
                What = what,
                Box = a,
                Before = before?.Clone(),
                After = after?.Clone(),
                LayerBefore = state.Boxes.Contains(k),
                RemovedBefore = state.RemovedBoxes.Contains(k),
                LayerAfter = after != null,
                RemovedAfter = after == null && Layers.IsCodeBox(a.Expansion, a.Owner, a.Copy, a.Box),
            };
        }

        internal static bool ChangeBox(AreaRef a, Action<BoxDefinition> change, string what, string why)
        {
            var box = a?.BoxDef;
            if (box == null)
            {
                Core.Log.Warning($"box ({why}): {a?.ToString() ?? "nothing"} is not a box of a copy; nothing changes");
                return false;
            }
            var after = box.Clone();
            change(after);
            return Edits.Run(BoxEditOf(a, box, after, what), why);
        }

        // ---- one box, whatever holds it

        /// <summary>Gives the selected box new bounds in its own coordinates, as one step.</summary>
        internal static bool SetBox(AreaRef a, Bounds local, string what, string why)
        {
            switch (a.Kind)
            {
                case AreaKind.Box:
                    return ChangeBox(a, x => x.Box = local, what, why);
                case AreaKind.Rule:
                    return ChangeRule(a, r => r.Area = local, what, why);
                case AreaKind.Opening:
                    return ChangeRule(a, r => r.Openings[a.Index] = local, what, why);
                default:
                    return ChangeRule(a, r => r.Windows[a.Index] = local, what, why);
            }
        }

        /// <summary>Deletes the selected rule, opening, window or box, as one step.</summary>
        internal static bool Delete(AreaRef a, string why)
        {
            if (a == null) return false;
            bool done;
            switch (a.Kind)
            {
                case AreaKind.Box:
                {
                    var box = a.BoxDef;
                    if (box == null)
                    {
                        Core.Log.Warning($"box ({why}): '{a}' is gone; nothing to delete");
                        return false;
                    }
                    done = Edits.Run(BoxEditOf(a, box, null, $"{a.Label} gelöscht"), why);
                    break;
                }
                case AreaKind.Rule:
                {
                    var b = a.Built;
                    var r = a.RuleDef;
                    if (r == null)
                    {
                        Core.Log.Warning($"rule ({why}): '{a}' is gone; nothing to delete");
                        return false;
                    }
                    done = Edits.Run(EditOf(b, r, null, r.Key, $"{a.Label} gelöscht"), why);
                    break;
                }
                case AreaKind.Opening:
                    done = ChangeRule(a, r => r.Openings.RemoveAt(a.Index), $"{a.Label} gelöscht", why);
                    break;
                default:
                    done = ChangeRule(a, r => r.Windows.RemoveAt(a.Index), $"{a.Label} gelöscht", why);
                    break;
            }
            if (done) Areas.Deselect("deleted");
            return done;
        }

        // ---- new rules, openings and boxes

        /// <summary>
        /// What the ray meets first, or the middle of the view without a ray. A filter skips what may not take the
        /// new thing; <paramref name="quiet"/> leaves out the log, for the preview that asks every frame.
        /// </summary>
        private static bool Aim(Ray? ray, out RaycastHit hit, string why, Func<Transform, bool> accept = null, bool quiet = false)
        {
            hit = default;
            var cam = EditorMode.Camera?.Camera;
            if (ray == null && cam == null)
            {
                Core.Log.Warning($"aim ({why}): no camera");
                return false;
            }
            var r = ray ?? cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            bool found = false;
            if (accept == null) found = Physics.Raycast(r, out hit, 300f, ~0, QueryTriggerInteraction.Ignore);
            else
            {
                float best = float.MaxValue;
                foreach (var h in Physics.RaycastAll(r, 300f, ~0, QueryTriggerInteraction.Ignore))
                    if (h.distance < best && accept(h.collider.transform))
                    {
                        best = h.distance;
                        hit = h;
                        found = true;
                    }
            }
            if (quiet) return found;
            string where = ray == null ? "in the middle of the view" : "under the cursor";
            if (!found)
            {
                Core.Log.Warning($"aim ({why}): nothing{(accept == null ? "" : " that fits")} within 300 m {where}");
                Panel.Note(ray == null ? "In der Bildmitte liegt nichts." : "Unter dem Mauszeiger liegt nichts Passendes.");
                return false;
            }
            Core.Log.Msg($"aim ({why}): '{MeshCarver.Path(hit.transform)}' at {hit.point}, {hit.distance:0.0} m {where}, normal {hit.normal}");
            return true;
        }

        /// <summary>The built expansion whose root holds the transform, or null for the main world.</summary>
        private static BuiltExpansion ExpansionOf(Transform t) =>
            BuildPipeline.All.FirstOrDefault(b => b.Root != null && t.IsChildOf(b.Root.transform));

        private static Bounds ToLocal(Matrix4x4 toWorld, Bounds world)
        {
            var inv = toWorld.inverse;
            var b = new Bounds(inv.MultiplyPoint3x4(world.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(inv.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? world.min.x : world.max.x, (i & 2) == 0 ? world.min.y : world.max.y, (i & 4) == 0 ? world.min.z : world.max.z)));
            return b;
        }

        private static string Slug(string s)
        {
            var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            var slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Length == 0 ? "object" : slug.Length > 30 ? slug.Substring(0, 30) : slug;
        }

        /// <summary>The world box a rule for the hit object covers: its collider and renderer, with a little room.</summary>
        private static Bounds RuleArea(RaycastHit hit)
        {
            var t = hit.collider.transform;
            var world = hit.collider.bounds;
            var r = t.GetComponent<Renderer>();
            if (r != null && r.enabled) world.Encapsulate(r.bounds);
            world.Expand(0.1f);
            return world;
        }

        /// <summary>A rule for the object the ray meets, or the middle of the view without a ray; its area the object's bounds.</summary>
        internal static bool NewRule(ObjectEffect effect, string why, Ray? ray = null)
        {
            if (!Aim(ray, out var hit, why)) return false;
            var t = hit.collider.transform;
            // Rules act on the world and the expansion's own pieces, never on placed copies: a rule there matches nothing.
            var copy = CopyOf(t);
            if (copy != null)
            {
                Core.Log.Warning($"rule ({why}): '{Copies.Key(copy)}' is a placed copy; rules leave copies alone");
                Panel.Note("Das ist eine eigene Kopie. Löschen mit Entf.");
                return false;
            }
            var target = ExpansionOf(t) ?? BuildPipeline.All.FirstOrDefault();
            if (target == null)
            {
                Core.Log.Error($"rule ({why}): no expansion is built; a rule has nowhere to go");
                return false;
            }
            bool expansionSide = ExpansionOf(t) != null;
            var rule = new ObjectRule { Side = expansionSide ? WorldSide.Expansion : WorldSide.Main, Name = t.name, Effect = effect };
            rule.Area = ToLocal(AreaRef.RuleMatrix(target, rule), RuleArea(hit));
            var hide = target.Definition.Hide;
            var stem = $"{effect.ToString().ToLowerInvariant()}-{Slug(t.name)}";
            var key = stem;
            for (int n = 2; hide.Any(x => x.Key == key); n++) key = $"{stem}-{n}";
            rule.Id = key;
            if (!Edits.Run(EditOf(target, null, rule, key, $"Regel {key} angelegt"), why)) return false;
            Areas.Select(AreaRef.OfRule(target.Id, key), why);
            return true;
        }

        /// <summary>The selected hollow rule, or null with the reason in the log and the status line.</summary>
        internal static ObjectRule HollowRule(string why, bool quiet = false)
        {
            var a = Areas.Current;
            var r = a?.RuleDef;
            if (r != null && r.Effect == ObjectEffect.Hollow) return r;
            if (!quiet)
            {
                Core.Log.Warning($"opening ({why}): select a hollow rule first; {(a == null ? "nothing" : a.ToString())} is selected");
                Panel.Note("Erst eine Aushöhlen-Regel auswählen.");
            }
            return null;
        }

        /// <summary>Only the hollowed object takes a doorway, so a chair in front of the wall does not.</summary>
        private static Func<Transform, bool> HollowFilter(ObjectRule r)
        {
            string shell = "MapExpansions.Shell " + r.Name;
            return t => t.name == r.Name || t.name == shell || (t.parent != null && t.parent.name == r.Name);
        }

        /// <summary>The world box of a doorway where the hit meets a wall: wide along it, deep across it, from the floor.</summary>
        private static Bounds OpeningWorld(RaycastHit hit, string why, bool quiet)
        {
            bool acrossX = Mathf.Abs(hit.normal.x) > Mathf.Abs(hit.normal.z);
            var size = acrossX ? new Vector3(1f, 2.2f, 1.2f) : new Vector3(1.2f, 2.2f, 1f);
            float bottom = hit.point.y - 0.6f;
            if (Physics.Raycast(hit.point + hit.normal * 0.3f, Vector3.down, out var floor, 3f, ~0, QueryTriggerInteraction.Ignore))
                bottom = floor.point.y - 0.05f;
            else if (!quiet) Core.Log.Warning($"opening ({why}): no floor within 3 m below {hit.point}; the opening starts 0.6 m below the aimed point");
            return new Bounds(new Vector3(hit.point.x, bottom + size.y * 0.5f, hit.point.z), size);
        }

        /// <summary>A doorway in the selected hollow rule where the ray, or the middle of the view, meets a wall.</summary>
        internal static bool NewOpening(string why, Ray? ray = null)
        {
            var r = HollowRule(why);
            if (r == null) return false;
            var a = Areas.Current;
            if (!Aim(ray, out var hit, why, HollowFilter(r))) return false;
            var local = ToLocal(AreaRef.RuleMatrix(a.Built, r), OpeningWorld(hit, why, false));
            int index = r.Openings.Count;
            var rule = new AreaRef { Kind = AreaKind.Rule, Expansion = a.Expansion, Rule = a.Rule };
            if (!ChangeRule(rule, x => x.Openings.Add(local), $"Öffnung {index + 1} in {a.Rule}", why)) return false;
            Areas.Select(new AreaRef { Kind = AreaKind.Opening, Expansion = a.Expansion, Rule = a.Rule, Index = index }, why);
            return true;
        }

        /// <summary>
        /// The copy that gets a panel or block: the copy the hit lies on, else the selected copy or the copy of the
        /// selected box. Null when there is none.
        /// </summary>
        /// <summary>The placed copy that holds the transform, or null.</summary>
        private static MainCopies.Placed CopyOf(Transform t)
        {
            if (t != null)
                foreach (var c in Copies.All())
                    if (c.Built?.Handle != null && t.IsChildOf(c.Built.Handle)) return c;
            return null;
        }

        private static MainCopies.Placed BoxCopy(Transform hit)
        {
            var hitCopy = CopyOf(hit);
            if (hitCopy != null) return hitCopy;
            var p = Selection.Current;
            if (p == null && Areas.Current?.Kind == AreaKind.Box) p = Areas.Current.Placed;
            return p?.Built?.Local == null ? null : p;
        }

        /// <summary>The box in the copy's own coordinates, in front of the hit surface rather than half inside it.</summary>
        private static Bounds BoxLocal(BoxKind kind, MainCopies.Placed p, RaycastHit hit)
        {
            var inv = p.Built.Move.inverse;
            var n = inv.MultiplyVector(hit.normal).normalized;
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            var size = kind == BoxKind.Block ? new Vector3(1f, 2f, 1f)
                : ay >= ax && ay >= az ? new Vector3(1f, 0.1f, 2f)
                : ax >= az ? new Vector3(0.1f, 2f, 1f)
                : new Vector3(1f, 2f, 0.1f);
            float half = 0.5f * (ax * size.x + ay * size.y + az * size.z) + 0.01f;
            return new Bounds(inv.MultiplyPoint3x4(hit.point) + n * half, size);
        }

        /// <summary>
        /// A panel or block where the ray, or the middle of the view, meets a surface. It belongs to the copy that
        /// surface is part of, else to the selected copy.
        /// </summary>
        internal static bool NewBox(BoxKind kind, string why, Ray? ray = null)
        {
            if (ray == null && BoxCopy(null) == null)
            {
                Core.Log.Warning($"box ({why}): select the copy that gets the {kind} first");
                Panel.Note("Erst eine Kopie auswählen.");
                return false;
            }
            if (!Aim(ray, out var hit, why)) return false;
            var p = BoxCopy(hit.transform);
            if (p == null)
            {
                Core.Log.Warning($"box ({why}): '{MeshCarver.Path(hit.transform)}' is part of no copy and no copy is selected; the {kind} has nothing to belong to");
                Panel.Note("Auf eine Kopie klicken oder erst eine Kopie auswählen.");
                return false;
            }
            var local = BoxLocal(kind, p, hit);
            string material = null;
            if (kind == BoxKind.Panel)
            {
                var hr = hit.collider.GetComponent<Renderer>();
                material = hr != null && hr.sharedMaterial != null ? hr.sharedMaterial.name : Materials("", 1).FirstOrDefault().name;
            }
            var d = p.Built.Def;
            var stem = kind.ToString().ToLowerInvariant();
            var id = stem + "-1";
            for (int k = 2; d.Boxes.Any(x => x.Id == id); k++) id = $"{stem}-{k}";
            var a = AreaRef.OfBox(p.Expansion.Id, p.LayerOwner, d.Id, id);
            var box = new BoxDefinition { Id = id, Kind = kind, Box = local, Material = material };
            if (!Edits.Run(BoxEditOf(a, null, box, $"{kind} {id} an {d.Id}"), why)) return false;
            Areas.Select(a, why);
            return true;
        }

        /// <summary>
        /// The eight world corners of what a click with the tool would make where the ray points, for the preview.
        /// False when the click would make nothing there.
        /// </summary>
        internal static bool Preview(PickTool tool, Ray ray, out Vector3[] corners)
        {
            corners = null;
            switch (tool)
            {
                case PickTool.Remove:
                {
                    if (!Aim(ray, out var hit, "preview", null, true) || CopyOf(hit.transform) != null) return false;
                    corners = Areas.Corners(RuleArea(hit), Matrix4x4.identity);
                    return true;
                }
                case PickTool.Opening:
                {
                    var r = HollowRule("preview", true);
                    if (r == null || !Aim(ray, out var hit, "preview", HollowFilter(r), true)) return false;
                    corners = Areas.Corners(OpeningWorld(hit, "preview", true), Matrix4x4.identity);
                    return true;
                }
                default:
                {
                    var kind = tool == PickTool.Block ? BoxKind.Block : BoxKind.Panel;
                    if (!Aim(ray, out var hit, "preview", null, true)) return false;
                    var p = BoxCopy(hit.transform);
                    if (p == null) return false;
                    corners = Areas.Corners(BoxLocal(kind, p, hit), p.Built.Move);
                    return true;
                }
            }
        }

        /// <summary>Makes what the tool makes where the ray points; true when a step was written.</summary>
        internal static bool Create(PickTool tool, Ray ray, string why) => tool switch
        {
            PickTool.Remove => NewRule(ObjectEffect.Remove, why, ray),
            PickTool.Block => NewBox(BoxKind.Block, why, ray),
            PickTool.Panel => NewBox(BoxKind.Panel, why, ray),
            _ => NewOpening(why, ray),
        };

        // ---- applying the expensive rules

        /// <summary>Applies every rule that waits; returns how many.</summary>
        internal static int Apply(string why)
        {
            if (Pending.Count == 0)
            {
                Core.Log.Msg($"apply ({why}): no rule waits");
                Panel.Note("Nichts anzuwenden.");
                return 0;
            }
            int done = 0;
            foreach (var (exp, key) in Pending.ToList())
            {
                Pending.Remove((exp, key));
                var b = BuildPipeline.Get(exp);
                var r = b?.Definition.Hide.FirstOrDefault(x => x.Key == key);
                if (r == null)
                {
                    Core.Log.Warning($"apply ({why}): rule '{exp}/{key}' is no longer in the definition");
                    continue;
                }
                ObjectRules.ApplyOne(b, BuildPipeline.Index, r);
                done++;
            }
            Core.Log.Msg($"apply ({why}): {done} rules applied; {Stale.Count} older forms stay until the save is loaded again");
            Panel.Note(Stale.Count > 0 ? $"{done} Regeln angewendet. {Stale.Count} alte Formen bleiben bis zum Neuladen." : $"{done} Regeln angewendet.");
            return done;
        }

        // ---- materials

        private static List<(string name, Color color)> _materials;

        /// <summary>Loaded materials whose name contains the text, with the colour they draw, at most <paramref name="max"/>.</summary>
        internal static List<(string name, Color color)> Materials(string query, int max)
        {
            if (_materials == null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                _materials = new List<(string, Color)>();
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>()))
                {
                    var m = o.TryCast<Material>();
                    if (m == null || string.IsNullOrEmpty(m.name) || m.name.StartsWith("Hidden", StringComparison.Ordinal) || !seen.Add(m.name)) continue;
                    _materials.Add((m.name, ColourOf(m, "_BaseColor") ?? ColourOf(m, "_Color") ?? Color.gray));
                }
                _materials.Sort((x, y) => string.CompareOrdinal(x.Item1, y.Item1));
                Core.Log.Msg($"materials: {_materials.Count} loaded materials listed");
            }
            var q = (query ?? "").Trim();
            return _materials.Where(x => q.Length == 0 || x.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(max).ToList();
        }

        /// <summary>The colour property of the material's shader, or null when the shader has no colour of that name.</summary>
        private static Color? ColourOf(Material m, string property)
        {
            var sh = m.shader;
            if (sh == null) return null;
            int i = sh.FindPropertyIndex(property);
            if (i < 0 || sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color) return null;
            return m.GetColor(property);
        }

        internal static void ForgetMaterials() => _materials = null;
    }
}
