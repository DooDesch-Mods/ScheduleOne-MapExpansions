using System.Globalization;
using MapExpansions.Lifecycle;
using MapExpansions.World;
using UnityEngine;

namespace MapExpansions.Editor
{
    /// <summary>
    /// Editing the geometry of world states: which state is the edit target, rebuilding the standing state from the
    /// definition as edited, reading the layer file again, and the state's rules and boxes. A state's copies are moved,
    /// duplicated and deleted with the usual copy tools; their layer owner is <c>state:&lt;id&gt;</c>.
    /// </summary>
    internal static class StateEdits
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <summary>The expansion and state the state commands work on; null until a state is entered.</summary>
        internal static string TargetExpansion { get; private set; }
        internal static string TargetState { get; private set; }

        /// <summary>Whether a placed copy belongs to the target state, so that a bare copy id finds it first.</summary>
        internal static bool IsTarget(MainCopies.Placed p) =>
            TargetState != null && p.Expansion.Id == TargetExpansion && p.LayerOwner == Layers.StateOwner(TargetState);

        private static BuiltExpansion Built(string expansionId, string what)
        {
            var b = BuildPipeline.Get(expansionId);
            if (b == null)
                Core.Log.Warning($"[state] {what}: expansion '{expansionId}' is not built; built: {string.Join(", ", BuildPipeline.All.Select(x => x.Id))}");
            return b;
        }

        /// <summary>The expansion a command names, else the target's.</summary>
        private static string ExpansionOr(string given, string what)
        {
            if (!string.IsNullOrEmpty(given)) return given;
            if (TargetExpansion == null) Core.Log.Warning($"[state] {what}: no expansion given and no state entered; use mapedit state <expansion> <state>");
            return TargetExpansion;
        }

        /// <summary>Applies a state and makes it the edit target; "dormant" takes every state out and clears the target.</summary>
        internal static bool Enter(string expansionId, string stateId, string why)
        {
            var b = Built(expansionId, "state");
            if (b == null) return false;
            string wanted = stateId.Equals("dormant", StringComparison.OrdinalIgnoreCase) ? null : stateId;
            if (wanted != null && WorldStates.Definition(expansionId, wanted) == null)
            {
                Core.Log.Warning($"[state] state: '{expansionId}' has no state '{stateId}'; it has {StateIds(b)}");
                return false;
            }
            if (!WorldStates.TrySet(expansionId, wanted, out var error))
            {
                Core.Log.Error($"[state] state ({why}): '{expansionId}/{stateId}' could not be applied: {error}");
                return false;
            }
            TargetExpansion = wanted == null ? null : expansionId;
            TargetState = wanted;
            Core.Log.Msg($"[state] state ({why}): '{expansionId}' stands in '{wanted ?? "dormant"}'; edit target {(wanted == null ? "none" : $"'{expansionId}/{wanted}'")}");
            Describe(expansionId);
            return true;
        }

        private static string StateIds(BuiltExpansion b) => string.Join(", ", b.Definition.WorldStates.Where(s => s != null).Select(s => s.Id));

        /// <summary>Logs the states of an expansion and the copies, boxes and rules of the standing one.</summary>
        internal static void Describe(string expansionId)
        {
            var b = Built(expansionId, "state");
            if (b == null) return;
            string active = WorldStates.ActiveState(expansionId);
            Core.Log.Msg($"[state] '{expansionId}': states {StateIds(b)}; standing {active ?? "dormant"}, selected {WorldStates.Desired(expansionId) ?? "dormant"}, " +
                         $"target {(TargetExpansion == expansionId ? TargetState : "none")}, unsaved {Saving.HasChangesFor(expansionId)}");
            if (active == null) return;
            var def = WorldStates.Definition(expansionId, active);
            string owner = Layers.StateOwner(active);
            foreach (var c in def.Copies)
            {
                var placed = Copies.Find($"{expansionId}/{owner}/{c.Id}");
                Core.Log.Msg($"[state]   copy {expansionId}/{owner}/{c.Id}: {(placed == null ? "not placed" : $"at {Fmt(placed.Built.Handle.position)} yaw {placed.Built.Handle.eulerAngles.y:0.#}")}, {c.Boxes.Count} boxes");
            }
            foreach (var r in def.Rules)
                Core.Log.Msg($"[state]   rule {r.Key}: {r.Effect} area {Fmt(r.Area.min)} to {Fmt(r.Area.max)}");
        }

        private static string Fmt(Vector3 v) => string.Format(Ci, "({0:0.###}, {1:0.###}, {2:0.###})", v.x, v.y, v.z);

        /// <summary>Lays the edited state definitions as the layer would write them over the states and rebuilds the standing one.</summary>
        internal static bool Restate(string expansionId, string why)
        {
            expansionId = ExpansionOr(expansionId, "restate");
            if (expansionId == null) return false;
            var b = Built(expansionId, "restate");
            if (b == null) return false;
            string json = Layers.Write(b.Definition, Layers.StateOf(expansionId));
            bool ok = WorldStates.Restate(expansionId, json, "the editor's unsaved layer", why, out var error);
            if (!ok) Core.Log.Error($"[state] restate ({why}): '{expansionId}' failed: {error}");
            else Core.Log.Msg($"[state] restate ({why}): '{expansionId}' rebuilt in '{WorldStates.ActiveState(expansionId) ?? "dormant"}'; " +
                              (Saving.HasChangesFor(expansionId) ? "the changes are not saved yet (mapedit save)" : "nothing unsaved"));
            return ok;
        }

        /// <summary>
        /// Reads the expansion's layer file again, for edits made in the file. Its state sections apply at once; its other
        /// entries apply with the next world build.
        /// </summary>
        internal static bool Reload(string expansionId, string why)
        {
            expansionId = ExpansionOr(expansionId, "reload");
            if (expansionId == null) return false;
            if (Built(expansionId, "reload") == null) return false;
            if (Saving.HasChangesFor(expansionId))
            {
                Core.Log.Warning($"[state] reload ({why}): '{expansionId}' has unsaved changes, which the file would replace; save or undo them first");
                return false;
            }
            string path = Core.LayerFile(expansionId);
            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception e)
            {
                Core.Log.Error($"[state] reload ({why}): reading '{expansionId}' from {path} failed: {e.Message}");
                return false;
            }
            Layers.SetLive(expansionId, json);
            bool ok = WorldStates.Restate(expansionId, json, path, why, out var error);
            if (!ok) Core.Log.Error($"[state] reload ({why}): '{expansionId}' from {path} failed: {error}");
            else Core.Log.Msg($"[state] reload ({why}): '{expansionId}' states read from {path} and rebuilt; entries outside \"states\" apply with the next world build");
            return ok;
        }

        /// <summary>Logs the boxes of a placed copy with the key <c>mapedit area</c> selects each by.</summary>
        internal static void Boxes(string copyKey)
        {
            var p = Copies.Find(copyKey);
            if (p == null)
            {
                Core.Log.Warning($"[state] boxes: no placed copy '{copyKey}'");
                return;
            }
            var d = p.Built.Def;
            Core.Log.Msg($"[state] boxes of {Copies.Key(p)}: {d.Boxes.Count}");
            foreach (var x in d.Boxes)
                Core.Log.Msg($"[state]   {p.Expansion.Id}/{p.LayerOwner}/{d.Id}/box/{x.Id}: {x.Kind} {Fmt(x.Box.min)} to {Fmt(x.Box.max)}");
        }

        /// <summary>Moves the selected box or rule area by a delta in its own coordinates, as one undo step.</summary>
        internal static bool BoxMove(Vector3 delta, string why)
        {
            var a = Areas.Current;
            if (a == null)
            {
                Core.Log.Warning("[state] boxmove: no area selected; mapedit area <key> selects one, mapedit boxes <copy> lists the keys");
                return false;
            }
            Bounds from;
            if (a.Kind == AreaKind.Box && a.BoxDef != null) from = a.BoxDef.Box;
            else if (a.Kind == AreaKind.Rule && a.RuleDef != null) from = a.RuleDef.Area;
            else
            {
                Core.Log.Warning($"[state] boxmove: '{a}' is no box or rule area that is still defined");
                return false;
            }
            var to = from;
            to.center += delta;
            bool ok = AreaEdits.SetBox(a, to, $"{a} um {Fmt(delta)} verschoben", why);
            if (ok) Core.Log.Msg($"[state] boxmove ({why}): '{a}' from {Fmt(from.center)} to {Fmt(to.center)}");
            else Core.Log.Error($"[state] boxmove ({why}): '{a}' could not be moved");
            return ok;
        }

        /// <summary>
        /// <c>staterule &lt;key&gt; area x0 y0 z0 x1 y1 z1</c> or <c>staterule &lt;key&gt; del</c> on the target state.
        /// The change goes into the state's definition and its layer section; <c>restate</c> builds it.
        /// </summary>
        internal static bool Rule(string key, string[] args, string why)
        {
            if (TargetState == null)
            {
                Core.Log.Warning("[state] staterule: no state entered; use mapedit state <expansion> <state>");
                return false;
            }
            var def = WorldStates.Definition(TargetExpansion, TargetState);
            var rule = def?.Rules.FirstOrDefault(r => r.Key == key);
            if (rule == null)
            {
                Core.Log.Warning($"[state] staterule: '{TargetExpansion}/{TargetState}' has no rule '{key}'; it has {string.Join(", ", def?.Rules.Select(r => r.Key) ?? Enumerable.Empty<string>())}");
                return false;
            }
            var layer = Layers.StateOf(TargetExpansion);
            string op = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (op == "area" && args.Length > 6)
            {
                var area = new Bounds();
                area.SetMinMax(V(args, 1), V(args, 4));
                rule.Area = area;
                layer.StateRules.Add((TargetState, key));
                layer.RemovedStateRules.Remove((TargetState, key));
            }
            else if (op == "del")
            {
                def.Rules.Remove(rule);
                layer.StateRules.Remove((TargetState, key));
                if (Layers.IsCodeStateRule(TargetExpansion, TargetState, key)) layer.RemovedStateRules.Add((TargetState, key));
            }
            else
            {
                Core.Log.Warning($"[state] staterule: '{string.Join(" ", args)}' is not 'area x0 y0 z0 x1 y1 z1' or 'del'");
                return false;
            }
            Saving.MarkDirty(TargetExpansion);
            Core.Log.Msg($"[state] staterule ({why}): '{TargetExpansion}/{TargetState}' rule '{key}' {(op == "del" ? "taken out" : $"area {Fmt(rule.Area.min)} to {Fmt(rule.Area.max)}")}; not an undo step, mapedit restate builds it");
            return true;
        }

        private static Vector3 V(string[] a, int i) =>
            new(float.Parse(a[i], Ci), float.Parse(a[i + 1], Ci), float.Parse(a[i + 2], Ci));

        /// <summary>The world was rebuilt; the target names a state that may no longer stand.</summary>
        internal static void Forget(string why)
        {
            if (TargetState != null) Core.Log.Msg($"[state] {why}: edit target '{TargetExpansion}/{TargetState}' cleared");
            TargetExpansion = null;
            TargetState = null;
        }
    }
}
