using HarmonyLib;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.Persistence.Loaders;
using MapExpansions.Navigation;
using MapExpansions.World;
using UnityEngine;

namespace MapExpansions.Lifecycle
{
    internal static class WorldStates
    {
        private sealed class Applied
        {
            internal BuiltExpansion Expansion;
            internal WorldStateDefinition State;
            internal readonly List<ObjectRule> Rules = new();
            internal readonly List<StateCuts.Lease> Cuts = new();
            internal readonly List<MainCopies.Placed> Copies = new();
            internal readonly List<PropertyExtensionLease> Properties = new();
            internal PropertyNavigation.StateBake Navigation;
            internal void CheckRemovable()
            {
                foreach (var p in Properties) p.CheckRemovable();
                PropertyDocks.CheckRemovalOrder(Properties.SelectMany(p => p.Docks));
            }
            internal void Remove()
            {
                CheckRemovable();
                Navigation?.Dispose();
                Navigation = null;
                foreach (var p in Properties.AsEnumerable().Reverse()) p.Dispose();
                Properties.Clear();
                foreach (var c in Copies) MainCopies.Revert(c);
                Copies.Clear();
                foreach (var cut in Cuts.AsEnumerable().Reverse()) cut.Dispose();
                Cuts.Clear();
                foreach (var r in Rules.AsEnumerable().Reverse())
                    if (ObjectRules.Revert(Expansion, r) < 0) throw new InvalidOperationException($"state '{State.Id}' could not revert rule '{r.Key}'");
                Rules.Clear();
                Physics.SyncTransforms();
            }
        }

        private static readonly Dictionary<string, string> Selections = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Applied> Active = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Failures = new(StringComparer.Ordinal);
        private static readonly Dictionary<(string expansion, string state), WorldStateDefinition> Definitions = new();

        internal static string Desired(string id) => Selections.TryGetValue(id, out var value) ? value : null;
        internal static bool IsApplied(string id) => BuildPipeline.Get(id) != null && !Failures.ContainsKey(id) &&
            (Desired(id) == null ? !Active.ContainsKey(id) : Active.TryGetValue(id, out var a) && a.State.Id == Desired(id));

        internal static IEnumerable<string> Validate(ExpansionDefinition def)
        {
            var errors = new List<string>();
            if (def.WorldStates.Count > 0 && def.HasSource) errors.Add("switchable world states require Source=null");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var permanentDocks = new Dictionary<(string extension, string dock), (string property, int index)>();
            foreach (var source in def.WorldStates)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.Id) || !ids.Add(source.Id))
                { errors.Add("world state has a missing or repeated id"); continue; }
                try
                {
                    var state = Layers.ResolveState(def, source);
                    var ruleIds = new HashSet<string>();
                    foreach (var rule in state.Rules)
                    {
                        if (!ObjectRules.CanRevert(rule.Effect) && rule.Effect is not (ObjectEffect.Cut or ObjectEffect.CutCollision)) errors.Add($"state '{state.Id}' uses irreversible rule {rule.Effect}");
                        if (rule.Side != WorldSide.Main) errors.Add($"state '{state.Id}' rule must select the main world");
                        rule.Key = string.IsNullOrEmpty(rule.Id) ? rule.MadeId() : rule.Id;
                        if (!ruleIds.Add(rule.Key)) errors.Add($"state '{state.Id}' repeats rule '{rule.Key}'");
                    }
                    var copyIds = new HashSet<string>();
                    foreach (var copy in state.Copies)
                    {
                        if (string.IsNullOrEmpty(copy.Id) || !copyIds.Add(copy.Id)) errors.Add($"state '{state.Id}' has a missing or repeated copy id");
                        if (!string.IsNullOrEmpty(copy.Resource) && !copy.VisualOnly) errors.Add($"state '{state.Id}' resource copies require VisualOnly=true");
                        if (!string.IsNullOrEmpty(copy.Prefab)) errors.Add($"state '{state.Id}' prefabs require a network lifecycle and cannot be switched locally");
                        if (copy.TemplateSide != WorldSide.Main) errors.Add($"state '{state.Id}' copy must use the main world");
                    }
                    var extensions = new HashSet<string>();
                    var dockSlots = new HashSet<(string property, int index)>();
                    foreach (var p in state.PropertyExtensions)
                    {
                        if (string.IsNullOrEmpty(p.Id) || !extensions.Add(p.Id) || string.IsNullOrEmpty(p.PropertyCode))
                            errors.Add($"state '{state.Id}' property extension needs unique id and property code");
                        if (p.GridIndex < 0) errors.Add($"state '{state.Id}' grid index is negative");
                        if (p.Tiles.Select(x => (x.X, x.Y)).Distinct().Count() != p.Tiles.Count)
                            errors.Add($"state '{state.Id}' repeats tile coordinates");
                        var surfaces = new HashSet<string>();
                        foreach (var surface in p.Surfaces)
                        {
                            if (string.IsNullOrEmpty(surface.Id) || !surfaces.Add(surface.Id) || surface.ValidFaces.Count == 0)
                                errors.Add($"state '{state.Id}' surface needs unique id and valid faces");
                            if (string.IsNullOrEmpty(surface.CopyId) && !ValidBounds(surface.Bounds))
                                errors.Add($"state '{state.Id}' standalone surface '{surface.Id}' has invalid bounds");
                            if (!string.IsNullOrEmpty(surface.CopyId) && string.IsNullOrEmpty(surface.BoxId))
                                errors.Add($"state '{state.Id}' surface '{surface.Id}' needs a box id");
                        }
                        foreach (var bounds in p.Bounds)
                            if (!ValidBounds(bounds)) errors.Add($"state '{state.Id}' has invalid property bounds");
                        var dockIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var dock in p.Docks)
                        {
                            foreach (var cause in PropertyDocks.Validate(dock)) errors.Add($"state '{state.Id}' property '{p.PropertyCode}': {cause}");
                            if (dock != null && (!dockIds.Add(dock.Id ?? "") || !dockSlots.Add((p.PropertyCode, dock.Index))))
                                errors.Add($"state '{state.Id}' repeats a dock ID or permanent property slot");
                            if (dock != null && !string.IsNullOrEmpty(dock.Id))
                            {
                                var key = (p.Id, dock.Id);
                                var slot = (p.PropertyCode, dock.Index);
                                if (permanentDocks.TryGetValue(key, out var prior) && prior != slot)
                                    errors.Add($"state '{state.Id}' changes the permanent slot of dock '{p.Id}/{dock.Id}'");
                                else permanentDocks[key] = slot;
                            }
                        }
                    }
                    if (state.NavigationAgents.Any(string.IsNullOrWhiteSpace) || state.NavigationAgents.Distinct(StringComparer.Ordinal).Count() != state.NavigationAgents.Count)
                        errors.Add($"state '{state.Id}' navigation agent names must be non-empty and unique");
                    if (state.NavigationAgents.Count > 0 && !state.NavigationBounds.HasValue)
                        errors.Add($"state '{state.Id}' declares navigation agents without a navigation volume");
                    if (state.NavigationBounds.HasValue && (!ValidBounds(state.NavigationBounds.Value) || state.NavigationProbes.Count < 2))
                        errors.Add($"state '{state.Id}' navigation needs valid bounds and at least two doorway probes");
                    Definitions[(def.Id, state.Id)] = state;
                }
                catch (Exception e) { errors.Add($"state '{source.Id}' cannot be resolved: {e.Message}"); }
            }
            return errors;
        }

        private static bool ValidBounds(Bounds b) => b.size.x > 0f && b.size.y > 0f && b.size.z > 0f &&
            float.IsFinite(b.center.x) && float.IsFinite(b.center.y) && float.IsFinite(b.center.z) &&
            float.IsFinite(b.size.x) && float.IsFinite(b.size.y) && float.IsFinite(b.size.z);

        internal static bool TrySet(string id, string stateId, out string error)
        {
            error = null;
            var def = Registry.GetDefinition(id);
            if (def == null || Registry.GetState(id) == ExpansionState.Rejected)
            { error = $"expansion '{id}' is unknown or rejected"; Core.Log.Error(error); return false; }
            if (string.IsNullOrEmpty(stateId)) stateId = null;
            if (stateId != null && !Definitions.ContainsKey((id, stateId)))
            { error = $"expansion '{id}' has no state '{stateId}'"; Core.Log.Error(error); return false; }
            if (BuildPipeline.Get(id) == null)
            {
                Selections[id] = stateId;
                Core.Log.Msg($"world state: '{id}' selected '{stateId ?? "dormant"}', waiting for world placement");
                return true;
            }
            return Apply(id, stateId, out error);
        }

        private static Applied Build(BuiltExpansion b, WorldStateDefinition state)
        {
            var result = new Applied { Expansion = b, State = state };
            try
            {
                // Copy the original source state before rules hide or cut it. Rules use the existing main-world index.
                foreach (var c in state.Copies)
                {
                    var placed = MainCopies.ApplyOne(b, c, $"state '{state.Id}'", "state:" + state.Id);
                    if (placed == null) throw new InvalidOperationException($"state '{state.Id}' copy '{c.Id}' was not built; see copy error above");
                    result.Copies.Add(placed);
                    if (placed.Built.Parts.Count > 0)
                        throw new InvalidOperationException($"state '{state.Id}' copy '{c.Id}' contains network parts; use visual-only source parts");
                }
                foreach (var r in state.Rules)
                {
                    if (r.Effect is ObjectEffect.Cut or ObjectEffect.CutCollision)
                    {
                        result.Cuts.Add(StateCuts.Apply(b, BuildPipeline.Index, r));
                        continue;
                    }
                    result.Rules.Add(r);
                    ObjectRules.ApplyOne(b, BuildPipeline.Index, r);
                    if (ObjectRules.AppliedTargets(b.Id, r) == 0)
                        throw new InvalidOperationException($"state '{state.Id}' rule '{r.Key}' matched no applicable targets");
                }
                Physics.SyncTransforms();
                foreach (var p in state.PropertyExtensions) result.Properties.Add(PropertyExtensions.Apply(b.Id, p));
                if (state.NavigationBounds.HasValue)
                {
                    // Native NPCMovement disables agents on pure clients; the host owns routes and movement.
                    // Every peer still completes the same geometry and property-effect checks before its ACK.
                    if (Il2CppFishNet.InstanceFinder.IsClient && !Il2CppFishNet.InstanceFinder.IsServer)
                        Core.Log.Msg($"world state: '{b.Id}/{state.Id}' geometry and property effects applied; NPC navigation is server-owned on this client");
                    else
                        result.Navigation = PropertyNavigation.BakeState(b.Id + "/" + state.Id, state.NavigationBounds.Value, state.NavigationProbes, b.Root.transform, state.NavigationAgents);
                }
                return result;
            }
            catch (Exception original)
            {
                try { result.Remove(); }
                catch (Exception cleanup) { throw new AggregateException("state build and cleanup both failed", original, cleanup); }
                throw;
            }
        }

        private static bool Apply(string id, string stateId, out string error)
        {
            error = null;
            Active.TryGetValue(id, out var previous);
            if (previous?.State.Id == stateId)
            {
                Selections[id] = stateId;
                Failures.Remove(id);
                if (stateId == null) MapExpansionsApi.RaiseWorldStateApplied(id, null);
                return true;
            }
            try
            {
                previous?.CheckRemovable();
                previous?.Remove();
                Active.Remove(id);
                if (stateId != null) Active[id] = Build(BuildPipeline.Get(id), Definitions[(id, stateId)]);
                Selections[id] = stateId;
                Failures.Remove(id);
                Core.Log.Msg($"world state: '{id}' applied '{stateId ?? "dormant"}'");
                MapExpansionsApi.RaiseWorldStateApplied(id, stateId);
                return true;
            }
            catch (Exception e)
            {
                error = $"world state '{id}/{stateId ?? "dormant"}' failed: {e}";
                Failures[id] = error;
                Core.Log.Error(error);
                if (previous != null && !Active.ContainsKey(id))
                {
                    try { Active[id] = Build(previous.Expansion, previous.State); }
                    catch (Exception rollback) { error += $"; previous state restoration failed: {rollback}"; Failures[id] = error; Core.Log.Error(error); }
                }
                return false;
            }
        }

        internal static bool ApplyPending()
        {
            bool applied = true;
            foreach (var entry in Selections.ToArray())
                if (!Apply(entry.Key, entry.Value, out _)) applied = false;
            return applied;
        }

        private static void RequireApplied()
        {
            if (Selections.Count > 0 && Selections.Any(x => BuildPipeline.Get(x.Key) == null)) BuildPipeline.OnMainInitialized();
            foreach (var entry in Selections.ToArray())
            {
                if (IsApplied(entry.Key)) continue;
                if (BuildPipeline.Get(entry.Key) == null)
                {
                    var cause = $"world state '{entry.Key}/{entry.Value}' has no placed expansion before property objects load";
                    Failures[entry.Key] = cause;
                    Core.Log.Error(cause);
                    throw new InvalidOperationException(cause);
                }
                if (!Apply(entry.Key, entry.Value, out var error)) throw new InvalidOperationException(error);
            }
            if (Failures.Count > 0) throw new InvalidOperationException(string.Join("; ", Failures.Values));
        }

        internal static void ResetSelections()
        {
            Selections.Clear();
            Failures.Clear();
            WorldStateReplication.Reset();
            Core.Log.Msg("world state: save selections reset in menu");
        }

        internal static void Teardown()
        {
            foreach (var item in Active.Values)
                try { item.Navigation?.Dispose(); }
                catch (Exception e) { Core.Log.Error($"world state '{item.Expansion.Id}' navigation teardown failed: {e}"); }
            Active.Clear();
            StateCuts.Reset();
        }

        // Both legacy folder saves and current dynamic saves must see the state before any object loader runs.
        [HarmonyPatch(typeof(PropertyLoader), nameof(PropertyLoader.Load), new[] { typeof(string) })]
        private static class BeforeLegacyPropertyObjects { private static void Prefix() => RequireApplied(); }

        [HarmonyPatch(typeof(PropertyLoader), nameof(PropertyLoader.Load), new[] { typeof(PropertyData), typeof(string) })]
        private static class BeforePropertyObjects { private static void Prefix() => RequireApplied(); }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), new[] { typeof(string) })]
        private static class KeepFailedLoadSave
        {
            private static bool Prefix()
            {
                if (Failures.Count == 0) return true;
                Core.Log.Error("save blocked: required world state failed, preserving existing property object data: " + string.Join("; ", Failures.Values));
                return false;
            }
        }
    }
}
