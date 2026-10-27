using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Every change the editor makes, as a step that can be done, undone and done again. A step names its copies by
    /// key (expansion/owner/id), not by the built object, because turning or scaling a copy builds it anew. The
    /// history lasts while the world is loaded.
    /// </summary>
    internal static class Edits
    {
        internal abstract class Edit
        {
            public string What;
            public abstract bool Do();
            public abstract void Undo();
        }

        private static readonly List<Edit> Done = new();
        private static readonly List<Edit> Undone = new();

        internal static int UndoCount => Done.Count;
        internal static int RedoCount => Undone.Count;

        internal static bool Run(Edit e, string why)
        {
            if (!e.Do())
            {
                Core.Log.Warning($"edit ({why}): {e.What} changed nothing; it is not in the history");
                return false;
            }
            Done.Add(e);
            Undone.Clear();
            Core.Log.Msg($"edit ({why}): {e.What}; {Done.Count} steps to undo");
            Panel.Note(e.What);
            return true;
        }

        internal static void Undo(string why)
        {
            if (Done.Count == 0)
            {
                Core.Log.Msg($"undo ({why}): nothing to undo");
                Panel.Note("Nichts rückgängig zu machen.");
                return;
            }
            var e = Done[^1];
            Done.RemoveAt(Done.Count - 1);
            e.Undo();
            Undone.Add(e);
            Core.Log.Msg($"undo ({why}): {e.What}; {Done.Count} steps left to undo, {Undone.Count} to redo");
            Panel.Note("Rückgängig: " + e.What);
        }

        internal static void Redo(string why)
        {
            if (Undone.Count == 0)
            {
                Core.Log.Msg($"redo ({why}): nothing to redo");
                Panel.Note("Nichts wiederherzustellen.");
                return;
            }
            var e = Undone[^1];
            Undone.RemoveAt(Undone.Count - 1);
            if (!e.Do()) Core.Log.Warning($"redo ({why}): {e.What} changed nothing");
            Done.Add(e);
            Core.Log.Msg($"redo ({why}): {e.What}; {Done.Count} steps to undo, {Undone.Count} to redo");
            Panel.Note("Wiederhergestellt: " + e.What);
        }

        internal static void Forget(string why)
        {
            if (Done.Count + Undone.Count > 0) Core.Log.Msg($"history ({why}): {Done.Count} steps to undo and {Undone.Count} to redo are dropped");
            Done.Clear();
            Undone.Clear();
        }

        // ---- copies by key

        internal readonly struct CopyRef
        {
            public readonly string Expansion, Owner, Id;

            public CopyRef(string expansion, string owner, string id)
            {
                Expansion = expansion;
                Owner = owner;
                Id = id;
            }

            public static CopyRef Of(MainCopies.Placed p) => new(p.Expansion.Id, p.LayerOwner, p.Built.Def.Id);

            public string Key => $"{Expansion}/{Owner}/{Id}";

            public BuiltExpansion Built => BuildPipeline.Get(Expansion);

            public List<CopyDefinition> List => Built == null ? null : Layers.CopyList(Built.Definition, Owner);

            public CopyDefinition Def
            {
                get
                {
                    var id = Id;
                    return List?.FirstOrDefault(c => c.Id == id);
                }
            }

            public MainCopies.Placed Placed => Copies.Find(Key);

            public override string ToString() => Key;
        }

        /// <summary>The fields of a copy that place it.</summary>
        internal readonly struct Pose
        {
            public readonly LocalPoint At;
            public readonly Vector3 OntoOffset, OntoRotation;
            public readonly float Scale;

            public Pose(CopyDefinition d)
            {
                At = d.At;
                OntoOffset = d.OntoOffset;
                OntoRotation = d.OntoRotation;
                Scale = d.Scale;
            }

            public void Write(CopyDefinition d)
            {
                d.At = At;
                d.OntoOffset = OntoOffset;
                d.OntoRotation = OntoRotation;
                d.Scale = Scale;
            }
        }

        /// <summary>
        /// Puts a copy at a pose: a pure move shifts the built copy, a turn or a new scale builds it anew. The layer
        /// takes the copy's entry from now on.
        /// </summary>
        private static bool Place(CopyRef r, Pose pose, string why)
        {
            var b = r.Built;
            var d = r.Def;
            if (b == null || d == null)
            {
                Core.Log.Error($"place ({why}): copy {r} is not in a built expansion; it stays as it is");
                return false;
            }
            var p = r.Placed;
            var old = new Pose(d);
            if (!MainCopies.FramePose(b, d, out var oldPos, out var oldRot))
            {
                Core.Log.Error($"place ({why}): copy {r} stands on '{d.Onto}', which is gone; it stays as it is");
                return false;
            }
            pose.Write(d);
            MainCopies.FramePose(b, d, out var newPos, out var newRot);
            bool turned = Quaternion.Angle(oldRot, newRot) > 0.01f || Mathf.Abs(old.Scale - pose.Scale) > 1e-5f;
            if (p == null)
                Core.Log.Warning($"place ({why}): copy {r} is not built; only its definition changes");
            else if (turned)
            {
                p = MainCopies.Rebuild(p, why);
                if (p != null) Selection.Replace(r.Key);
            }
            else
            {
                // Shift writes the move into the definition itself, from the old values.
                old.Write(d);
                MainCopies.Shift(p, newPos - oldPos);
                pose.Write(d);
            }
            Saving.MarkDirty(r.Expansion, r.Owner, r.Id);
            return true;
        }

        /// <summary>A world change of the selection: turn around the pivot, scale from it, then move.</summary>
        internal static Pose Moved(CopyRef r, Vector3 pivot, Quaternion turn, float scale, Vector3 move)
        {
            var d = r.Def;
            var tmp = d.Clone();
            if (!MainCopies.FramePose(r.Built, d, out var pos, out var rot)) return new Pose(d);
            var newPos = pivot + turn * ((pos - pivot) * scale) + move;
            MainCopies.SetFramePose(r.Built, tmp, newPos, turn * rot, d.Scale * scale);
            return new Pose(tmp);
        }

        internal sealed class PoseEdit : Edit
        {
            public readonly List<(CopyRef copy, Pose before, Pose after)> Items = new();

            public override bool Do()
            {
                bool any = false;
                foreach (var (c, _, after) in Items) any |= Place(c, after, "do");
                return any;
            }

            public override void Undo()
            {
                foreach (var (c, before, _) in Items) Place(c, before, "undo");
            }
        }

        /// <summary>Applies one change to every selected copy as one step.</summary>
        internal static bool Transform(IReadOnlyList<MainCopies.Placed> sel, Vector3 pivot, Quaternion turn, float scale, Vector3 move, string what, string why)
        {
            if (sel.Count == 0)
            {
                Core.Log.Msg($"edit ({why}): nothing is selected");
                return false;
            }
            var e = new PoseEdit { What = what };
            foreach (var p in sel)
            {
                var r = CopyRef.Of(p);
                var d = r.Def;
                if (d == null) continue;
                e.Items.Add((r, new Pose(d), Moved(r, pivot, turn, scale, move)));
            }
            return Run(e, why);
        }

        internal static bool MoveBy(IReadOnlyList<MainCopies.Placed> sel, Vector3 delta, string why)
        {
            if (delta.sqrMagnitude < 1e-8f)
            {
                Core.Log.Msg($"move ({why}): the selection did not move");
                return false;
            }
            return Transform(sel, Vector3.zero, Quaternion.identity, 1f, delta,
                $"{Name(sel)} um {delta.magnitude:0.00} m verschoben", why);
        }

        /// <summary>Lets every selected copy fall onto what lies below its bounds.</summary>
        internal static bool Drop(IReadOnlyList<MainCopies.Placed> sel, string why)
        {
            if (sel.Count == 0)
            {
                Core.Log.Msg($"drop ({why}): nothing is selected");
                return false;
            }
            var e = new PoseEdit { What = $"{Name(sel)} auf die Oberfläche gesetzt" };
            foreach (var p in sel)
            {
                var b = Copies.Bounds(p);
                var from = new Vector3(b.center.x, b.min.y + 0.05f, b.center.z);
                float best = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(from, Vector3.down, 500f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (p.Built.Local != null && hit.transform.IsChildOf(p.Built.Local.transform)) continue;
                    if (hit.distance < best) best = hit.distance;
                }
                if (best == float.MaxValue)
                {
                    Core.Log.Warning($"drop ({why}): nothing below {Copies.Key(p)} within 500 m; it stays");
                    continue;
                }
                float dy = -(best - 0.05f);
                var r = CopyRef.Of(p);
                Core.Log.Msg($"drop ({why}): {r} falls {-dy:0.###} m onto the surface below");
                e.Items.Add((r, new Pose(r.Def), Moved(r, Vector3.zero, Quaternion.identity, 1f, new Vector3(0f, dy, 0f))));
            }
            if (e.Items.Count == 0) return false;
            return Run(e, why);
        }

        private static string Name(IReadOnlyList<MainCopies.Placed> sel) =>
            sel.Count == 1 ? sel[0].Built.Def.Id : $"{sel.Count} Kopien";

        // ---- duplicate and delete

        /// <summary>What the layer state holds of one copy, to put back after a delete is undone.</summary>
        internal sealed class Membership
        {
            public bool Copy, Added, Removed;
            public List<(string owner, string copy, string id)> Boxes = new();

            public static Membership Of(Layers.State s, CopyRef r) => new()
            {
                Copy = s.Copies.Contains((r.Owner, r.Id)),
                Added = s.AddedCopies.Contains((r.Owner, r.Id)),
                Removed = s.RemovedCopies.Contains((r.Owner, r.Id)),
                Boxes = s.Boxes.Where(x => x.owner == r.Owner && x.copy == r.Id).ToList(),
            };

            public void Write(Layers.State s, CopyRef r)
            {
                Set(s.Copies, (r.Owner, r.Id), Copy);
                Set(s.AddedCopies, (r.Owner, r.Id), Added);
                Set(s.RemovedCopies, (r.Owner, r.Id), Removed);
                s.Boxes.RemoveWhere(x => x.owner == r.Owner && x.copy == r.Id);
                foreach (var x in Boxes) s.Boxes.Add(x);
            }

            private static void Set<T>(HashSet<T> set, T item, bool on)
            {
                if (on) set.Add(item);
                else set.Remove(item);
            }
        }

        private static string OwnerLabel(string layerOwner) =>
            layerOwner == Layers.ExpansionOwner ? "expansion" : $"shop '{layerOwner.Substring(5)}'";

        /// <summary>Puts a definition into its list and builds it; the layer writes it in full.</summary>
        private static bool AddCopy(CopyRef r, CopyDefinition d, int index, Membership m, string why)
        {
            var list = r.List;
            if (list == null)
            {
                Core.Log.Error($"copies ({why}): {r} has no list to go into; it is not built");
                return false;
            }
            if (list.Any(c => c.Id == d.Id))
            {
                Core.Log.Warning($"copies ({why}): {r} is in the definition already");
                return false;
            }
            list.Insert(Mathf.Clamp(index, 0, list.Count), d);
            m.Write(Layers.StateOf(r.Expansion), r);
            Saving.MarkDirty(r.Expansion);
            return MainCopies.ApplyOne(r.Built, d, OwnerLabel(r.Owner), r.Owner) != null;
        }

        private static (CopyDefinition def, int index) TakeCopy(CopyRef r, Membership after, string why)
        {
            var list = r.List;
            var d = r.Def;
            if (list == null || d == null)
            {
                Core.Log.Warning($"copies ({why}): {r} is not in the definition");
                return (null, -1);
            }
            int index = list.IndexOf(d);
            var p = r.Placed;
            if (p != null)
            {
                Selection.Remove(r.Key);
                MainCopies.Revert(p);
            }
            list.Remove(d);
            after.Write(Layers.StateOf(r.Expansion), r);
            Saving.MarkDirty(r.Expansion);
            return (d, index);
        }

        internal sealed class DuplicateEdit : Edit
        {
            public readonly List<(CopyRef source, CopyRef made, CopyDefinition def, int index)> Items = new();

            public override bool Do()
            {
                bool any = false;
                foreach (var (_, made, def, index) in Items)
                {
                    var m = new Membership { Copy = true, Added = true, Boxes = def.Boxes.Select(x => (made.Owner, made.Id, x.Id)).ToList() };
                    if (AddCopy(made, def, index, m, "duplicate")) any = true;
                }
                Selection.Set(Items.Select(x => x.made.Key), "duplicate");
                return any;
            }

            public override void Undo()
            {
                foreach (var (_, made, _, _) in Items)
                    TakeCopy(made, new Membership(), "undo duplicate");
                Selection.Set(Items.Select(x => x.source.Key), "undo duplicate");
            }
        }

        internal static bool Duplicate(IReadOnlyList<MainCopies.Placed> sel, string why)
        {
            if (sel.Count == 0)
            {
                Core.Log.Msg($"duplicate ({why}): nothing is selected");
                return false;
            }
            var e = new DuplicateEdit { What = $"{Name(sel)} dupliziert" };
            var taken = new HashSet<string>();
            foreach (var p in sel)
            {
                var src = CopyRef.Of(p);
                var list = src.List;
                if (list == null) continue;
                // The copy stands where its original stands, selected, so the next drag moves the new one.
                var d = src.Def.Clone();
                d.Id = FreeId(list, src.Id, taken);
                taken.Add(d.Id);
                e.Items.Add((src, new CopyRef(src.Expansion, src.Owner, d.Id), d, list.IndexOf(src.Def) + 1));
            }
            return Run(e, why);
        }

        private static string FreeId(List<CopyDefinition> list, string id, HashSet<string> taken)
        {
            var stem = System.Text.RegularExpressions.Regex.Replace(id, @"-\d+$", "");
            for (int n = 2; ; n++)
            {
                var candidate = $"{stem}-{n}";
                if (!taken.Contains(candidate) && list.All(c => c.Id != candidate)) return candidate;
            }
        }

        internal sealed class DeleteEdit : Edit
        {
            public readonly List<(CopyRef copy, Membership before, CopyDefinition def, int index)> Items = new();

            public override bool Do()
            {
                bool any = false;
                for (int i = 0; i < Items.Count; i++)
                {
                    var (r, before, _, _) = Items[i];
                    // A copy of the code stays out as a removal in the layer; one the layer added just goes.
                    var after = before.Added ? new Membership() : new Membership { Removed = true };
                    var (d, index) = TakeCopy(r, after, "delete");
                    if (d == null) continue;
                    Items[i] = (r, before, d, index);
                    any = true;
                }
                return any;
            }

            public override void Undo()
            {
                foreach (var (r, before, def, index) in Items)
                    if (def != null) AddCopy(r, def, index, before, "undo delete");
                Selection.Set(Items.Select(x => x.copy.Key), "undo delete");
            }
        }

        internal static bool Delete(IReadOnlyList<MainCopies.Placed> sel, string why)
        {
            if (sel.Count == 0)
            {
                Core.Log.Msg($"delete ({why}): nothing is selected");
                return false;
            }
            var e = new DeleteEdit { What = $"{Name(sel)} gelöscht" };
            foreach (var p in sel)
            {
                var r = CopyRef.Of(p);
                e.Items.Add((r, Membership.Of(Layers.StateOf(r.Expansion), r), null, -1));
            }
            return Run(e, why);
        }
    }
}
