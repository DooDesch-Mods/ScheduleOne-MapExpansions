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
        /// <summary>What stands in the world for one expansion's state. <see cref="Active"/> always holds it, also half built.</summary>
        private sealed class Applied
        {
            internal BuiltExpansion Expansion;
            internal WorldStateDefinition State;
            internal readonly List<ObjectRule> Rules = new();
            internal readonly List<StateCuts.Lease> Cuts = new();
            internal readonly List<ObjectMoves.Lease> Moves = new();
            internal readonly List<MainCopies.Placed> Copies = new();
            internal readonly List<PropertyExtensionLease> Properties = new();
            internal readonly HashSet<IntPtr> DetachedColliders = new();
            internal PropertyNavigation.StateBake Navigation;
            /// <summary>
            /// The state is not whole: its build has not finished or its removal has started. It never counts as applied,
            /// and the next apply of any id first removes what is left of it.
            /// </summary>
            internal bool Broken;
            internal string Timing = "";

            internal void CheckRemovable(Carried carry)
            {
                var leaving = Properties.Where(p => !carry.Properties.Contains(p)).ToList();
                foreach (var p in leaving) p.CheckRemovable();
                PropertyDocks.CheckRemovalOrder(leaving.SelectMany(p => p.Docks));
            }

            /// <summary>
            /// Removes everything except what <paramref name="carry"/> keeps. Each part leaves its list once it is gone,
            /// so a removal that fails can be repeated and resumes where it stopped.
            /// </summary>
            internal void Remove(Carried carry)
            {
                var errors = new List<Exception>();
                try { World.WeatherCover.RemoveState(CoverOwner(Expansion, State)); }
                catch (Exception e) { errors.Add(e); }
                try
                {
                    Navigation?.Dispose();
                    Navigation = null;
                }
                catch (Exception e) { errors.Add(e); }
                for (int i = Properties.Count - 1; i >= 0; i--)
                {
                    if (carry.Properties.Contains(Properties[i])) continue;
                    try
                    {
                        Properties[i].Dispose();
                        Properties.RemoveAt(i);
                    }
                    catch (Exception e) { errors.Add(e); }
                }
                // A surface of a lease that could not go still stands on its copy, so the geometry waits for the retry.
                if (errors.Count > 0) throw new AggregateException($"state '{State.Id}' could not remove its property effects", errors);
                for (int i = Copies.Count - 1; i >= 0; i--)
                {
                    if (carry.Copies.Contains(Copies[i])) continue;
                    try
                    {
                        MainCopies.Revert(Copies[i]);
                        Copies.RemoveAt(i);
                    }
                    catch (Exception e) { errors.Add(e); }
                }
                for (int i = Moves.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        Moves[i].Dispose();
                        Moves.RemoveAt(i);
                    }
                    catch (Exception e) { errors.Add(e); }
                }
                for (int i = Cuts.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        Cuts[i].Dispose();
                        Cuts.RemoveAt(i);
                    }
                    catch (Exception e) { errors.Add(e); }
                }
                for (int i = Rules.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        // Only reversible rules pass validation, so -1 means this rule has nothing left to take back.
                        if (ObjectRules.Revert(Expansion, Rules[i]) < 0)
                            Core.Log.Warning($"world state: '{Expansion.Id}/{State.Id}' rule '{Rules[i].Key}' was not applied; nothing to revert");
                        Rules.RemoveAt(i);
                    }
                    catch (Exception e) { errors.Add(e); }
                }
                try { ColliderKeepAlive.Restore(DetachedColliders); }
                catch (Exception e) { errors.Add(e); }
                Physics.SyncTransforms();
                if (errors.Count > 0) throw new AggregateException($"state '{State.Id}' could not remove all its effects", errors);
            }
        }

        /// <summary>What a transition keeps standing, in the order of the state it comes from.</summary>
        private sealed class Carried
        {
            internal static readonly Carried Nothing = new();
            internal readonly List<PropertyExtensionLease> Properties = new();
            internal readonly List<MainCopies.Placed> Copies = new();
        }

        private static readonly Dictionary<string, string> Selections = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Applied> Active = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Failures = new(StringComparer.Ordinal);
        private static readonly Dictionary<(string expansion, string state), WorldStateDefinition> Definitions = new();
        private static readonly HashSet<string> UnplacedDormantLogged = new(StringComparer.Ordinal);

        internal static string Desired(string id) => Selections.TryGetValue(id, out var value) ? value : null;
        internal static bool IsApplied(string id) => BuildPipeline.Get(id) != null && !Failures.ContainsKey(id) &&
            (Desired(id) == null ? !Active.ContainsKey(id) : Active.TryGetValue(id, out var a) && !a.Broken && a.State.Id == Desired(id));

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
                    var moveIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var move in state.Moves)
                    {
                        if (move == null || string.IsNullOrEmpty(move.Id) || !moveIds.Add(move.Id)) errors.Add($"state '{state.Id}' has a missing or repeated move id");
                        else if (string.IsNullOrEmpty(move.Path)) errors.Add($"state '{state.Id}' move '{move.Id}' needs a path");
                    }
                    // Box id to "is a block", per copy a surface can stand on. A state's copy goes before the expansion's.
                    var bindable = new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.Ordinal);
                    foreach (var copy in state.Copies.Concat(def.Copies))
                        if (!string.IsNullOrEmpty(copy.Id) && !bindable.ContainsKey(copy.Id))
                            bindable[copy.Id] = copy.Boxes.GroupBy(x => x.Id ?? "").ToDictionary(g => g.Key, g => g.First().Kind == BoxKind.Block);
                    var extensions = new HashSet<string>();
                    var dockSlots = new HashSet<(string property, int index)>();
                    foreach (var p in state.PropertyExtensions)
                    {
                        if (string.IsNullOrEmpty(p.Id) || !extensions.Add(p.Id) || string.IsNullOrEmpty(p.PropertyCode))
                            errors.Add($"state '{state.Id}' property extension needs unique id and property code");
                        if (p.GridIndex < 0) errors.Add($"state '{state.Id}' grid index is negative");
                        if (p.GridOrigin is Vector3 origin && !(float.IsFinite(origin.x) && float.IsFinite(origin.y) && float.IsFinite(origin.z)))
                            errors.Add($"state '{state.Id}' extension '{p.Id}' grid origin is not a finite point");
                        if (p.Tiles.Select(x => (x.X, x.Y)).Distinct().Count() != p.Tiles.Count)
                            errors.Add($"state '{state.Id}' repeats tile coordinates");
                        var fixtureIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var fixture in p.Fixtures)
                        {
                            if (fixture == null || string.IsNullOrEmpty(fixture.Id) || !fixtureIds.Add(fixture.Id))
                                errors.Add($"state '{state.Id}' extension '{p.Id}' has a missing or repeated fixture id");
                            else if (string.IsNullOrEmpty(fixture.Path)) errors.Add($"state '{state.Id}' fixture '{fixture.Id}' needs a path");
                            else if (fixture.Tiles.Count == 0 || fixture.Tiles.Select(x => (x.X, x.Y)).Distinct().Count() != fixture.Tiles.Count)
                                errors.Add($"state '{state.Id}' fixture '{fixture.Id}' needs at least one tile and no repeated coordinate");
                        }
                        var surfaces = new HashSet<string>();
                        foreach (var surface in p.Surfaces)
                        {
                            if (string.IsNullOrEmpty(surface.Id) || !surfaces.Add(surface.Id) || surface.ValidFaces.Count == 0)
                                errors.Add($"state '{state.Id}' surface needs unique id and valid faces");
                            if (string.IsNullOrEmpty(surface.CopyId) && !ValidBounds(surface.Bounds))
                                errors.Add($"state '{state.Id}' standalone surface '{surface.Id}' has invalid bounds");
                            var binding = WorldStatePlan.SurfaceBindingError(surface.Id, surface.CopyId, surface.BoxId, bindable);
                            if (binding != null) errors.Add($"state '{state.Id}' {binding}");
                        }
                        foreach (var bounds in p.Bounds)
                            if (!ValidBounds(bounds)) errors.Add($"state '{state.Id}' has invalid property bounds");
                        var dockIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var dock in p.Docks)
                        {
                            foreach (var cause in PropertyDocks.Validate(dock)) errors.Add($"state '{state.Id}' property '{p.PropertyCode}': {cause}");
                            if (dock != null && !PropertyDocks.HasPlacement(dock))
                                Core.Log.Warning($"validate: '{def.Id}' state '{state.Id}' dock '{dock.Id}' has no At yet; applying the state fails until it is set");
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
                    if (state.NavigationExistingFloor.Count > 0 && !state.NavigationBounds.HasValue)
                        errors.Add($"state '{state.Id}' declares existing floor without a navigation volume");
                    else if (state.NavigationExistingFloor.Any(f => !ValidBounds(f) || !f.Intersects(state.NavigationBounds.Value)))
                        errors.Add($"state '{state.Id}' existing floor boxes must be valid and lie in the navigation volume");
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

        /// <summary>Which parts of <paramref name="previous"/> stand unchanged in <paramref name="next"/>.</summary>
        private static Carried PlanCarry(Applied previous, WorldStateDefinition next)
        {
            if (previous == null || next == null) return Carried.Nothing;
            var plan = WorldStatePlan.Plan(
                previous.Properties.Select(p => new WorldStatePlan.Extension(p.ExtensionId, p.Signature, p.CarrierCopies, p.Damaged)),
                previous.Copies.Where(c => c.Built.Local != null).Select(c => new WorldStatePlan.Copy(c.Built.Def.Id, c.StateSignature)),
                next.PropertyExtensions.Select(p => new WorldStatePlan.Extension(p.Id, WorldStateSignatures.Of(p), WorldStateSignatures.Carriers(p))),
                next.Copies.Select(c => new WorldStatePlan.Copy(c.Id, WorldStateSignatures.Of(c))));
            var carry = new Carried();
            carry.Properties.AddRange(previous.Properties.Where(p => plan.Extensions.Contains(p.ExtensionId)));
            carry.Copies.AddRange(previous.Copies.Where(c => plan.Copies.Contains(c.Built.Def.Id)));
            return carry;
        }

        /// <summary>Fills a new state around what stands already. On an exception the caller removes what got built.</summary>
        private static void Populate(Applied result, Carried carry)
        {
            var b = result.Expansion;
            var state = result.State;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string owner = "state:" + state.Id;
            foreach (var kept in carry.Copies)
            {
                kept.LayerOwner = owner;
                kept.Built.Def = state.Copies.First(c => c.Id == kept.Built.Def.Id);
                result.Copies.Add(kept);
            }
            // Kept extensions stay ahead of new ones: removal runs backwards, which frees appended dock slots first.
            result.Properties.AddRange(carry.Properties);

            // Copy the original source state before rules hide or cut it. Rules use the existing main-world index.
            var fresh = state.Copies.Where(c => carry.Copies.All(k => k.Built.Def.Id != c.Id)).ToList();
            var lookup = fresh.Count > 0 ? MainCopies.LookupFor(fresh) : null;
            long lookupMs = clock.ElapsedMilliseconds;
            foreach (var c in fresh)
            {
                var placed = MainCopies.ApplyOne(b, c, $"state '{state.Id}'", owner, lookup);
                if (placed == null) throw new InvalidOperationException($"state '{state.Id}' copy '{c.Id}' was not built; see copy error above");
                placed.StateSignature = WorldStateSignatures.Of(c);
                result.Copies.Add(placed);
                if (placed.Built.Parts.Count > 0)
                    throw new InvalidOperationException($"state '{state.Id}' copy '{c.Id}' contains network parts; use visual-only source parts");
            }
            long copiesMs = clock.ElapsedMilliseconds;

            var offColliders = new HashSet<IntPtr>();
            // A full pass over the world for every rule would stall the game for seconds on each state change.
            var mainNearby = state.Rules.Count > 0 && BuildPipeline.Index != null ? new SceneIndex.AreaSnapshot(BuildPipeline.Index.Main) : null;
            foreach (var r in state.Rules)
            {
                if (r.Effect is ObjectEffect.Cut or ObjectEffect.CutCollision)
                {
                    result.Cuts.Add(StateCuts.Apply(b, BuildPipeline.Index, r, offColliders));
                    continue;
                }
                result.Rules.Add(r);
                ObjectRules.ApplyState(b, BuildPipeline.Index, r, offColliders, mainNearby);
                if (ObjectRules.AppliedTargets(b.Id, r) == 0)
                    throw new InvalidOperationException($"state '{state.Id}' rule '{r.Key}' matched no applicable targets");
            }
            ColliderKeepAlive.DetachForState(offColliders, result.DetachedColliders);
            // Before the bake, so that navigation sees a moved object where it now stands.
            foreach (var move in state.Moves)
            {
                var moved = ObjectMoves.Apply(b.Id + "/" + state.Id, move);
                if (moved != null) result.Moves.Add(moved);
            }
            Physics.SyncTransforms();
            long rulesMs = clock.ElapsedMilliseconds;

            foreach (var p in state.PropertyExtensions)
                if (carry.Properties.All(k => k.ExtensionId != p.Id)) result.Properties.Add(PropertyExtensions.Apply(b.Id, p));
            long propertiesMs = clock.ElapsedMilliseconds;

            if (state.NavigationBounds.HasValue)
            {
                // Native NPCMovement disables agents on pure clients; the host owns routes and movement.
                // Every peer still completes the same geometry and property-effect checks before its ACK.
                if (Il2CppFishNet.InstanceFinder.IsClient && !Il2CppFishNet.InstanceFinder.IsServer)
                    Core.Log.Msg($"world state: '{b.Id}/{state.Id}' geometry and property effects applied; NPC navigation is server-owned on this client");
                else
                    result.Navigation = PropertyNavigation.BakeState(b.Id + "/" + state.Id, state.NavigationBounds.Value, state.NavigationProbes, b.Root.transform, state.NavigationAgents, state.NavigationExistingFloor);
            }
            // The game's rain and its cover queries read a mask baked without this state's roofs; the world's own
            // colliders answer over the state's volume instead.
            Bounds? cover = state.NavigationBounds;
            foreach (var p in state.PropertyExtensions)
                foreach (var box in p.Bounds)
                {
                    var grown = cover ?? box;
                    grown.Encapsulate(box);
                    cover = grown;
                }
            if (cover.HasValue) World.WeatherCover.AddState(CoverOwner(b, state), cover.Value);
            result.Timing = $"lookup {lookupMs} ms, {fresh.Count} copies {copiesMs - lookupMs} ms, {state.Rules.Count} rules {rulesMs - copiesMs} ms, " +
                            $"properties {propertiesMs - rulesMs} ms, navigation {clock.ElapsedMilliseconds - propertiesMs} ms; kept {carry.Copies.Count} copies, {carry.Properties.Count} extensions";
        }

        private static string CoverOwner(BuiltExpansion b, WorldStateDefinition state) => $"'{b.Id}' world state {state.Id}";

        private static bool Fail(string id, string stateId, Exception cause, out string error)
        {
            error = $"world state '{id}/{stateId ?? "dormant"}' failed: {cause}";
            Failures[id] = error;
            Core.Log.Error(error);
            return false;
        }

        private static bool Apply(string id, string stateId, out string error)
        {
            error = null;
            Active.TryGetValue(id, out var previous);
            if (previous == null ? stateId == null : !previous.Broken && previous.State.Id == stateId)
            {
                Selections[id] = stateId;
                Failures.Remove(id);
                if (stateId == null) MapExpansionsApi.RaiseWorldStateApplied(id, null);
                return true;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var b = BuildPipeline.Get(id);
            var next = stateId == null ? null : Definitions[(id, stateId)];
            if (next != null && b == null)
                return Fail(id, stateId, new InvalidOperationException("the expansion has no placed world"), out error);
            var carry = PlanCarry(previous, next);
            try
            {
                if (previous != null)
                {
                    // A refusal here leaves the old state whole. From the next line on it counts as broken until it is gone.
                    if (!previous.Broken) previous.CheckRemovable(carry);
                    previous.Broken = true;
                    previous.Remove(carry);
                    Active.Remove(id);
                }
            }
            catch (Exception e) { return Fail(id, stateId, e, out error); }
            long removeMs = clock.ElapsedMilliseconds;

            Applied building = null;
            try
            {
                if (next != null)
                {
                    building = new Applied { Expansion = b, State = next, Broken = true };
                    Active[id] = building;
                    Populate(building, carry);
                    building.Broken = false;
                }
            }
            catch (Exception e)
            {
                Fail(id, stateId, e, out error);
                Recover(id, building, previous, carry, ref error);
                return false;
            }
            Selections[id] = stateId;
            Failures.Remove(id);
            Core.Log.Msg($"world state: '{id}' applied '{stateId ?? "dormant"}' in {clock.ElapsedMilliseconds} ms (remove {removeMs} ms{(building == null ? "" : ", " + building.Timing)})");
            MapExpansionsApi.RaiseWorldStateApplied(id, stateId);
            return true;
        }

        /// <summary>
        /// Takes a failed build out and puts the previous state back around what the transition kept. Whatever cannot be
        /// removed stays in <see cref="Active"/> as broken, so the next apply removes it first. The failure stays recorded.
        /// </summary>
        private static void Recover(string id, Applied building, Applied previous, Carried carry, ref string error)
        {
            try
            {
                building.Remove(carry);
                Active.Remove(id);
            }
            catch (Exception cleanup)
            {
                error += $"; cleanup failed, the state stays broken: {cleanup}";
                Failures[id] = error;
                Core.Log.Error(error);
                return;
            }
            if (previous == null) return;
            var rollback = new Applied { Expansion = previous.Expansion, State = previous.State, Broken = true };
            Active[id] = rollback;
            try
            {
                Populate(rollback, carry);
                rollback.Broken = false;
                Core.Log.Warning($"world state: '{id}' restored previous state '{previous.State.Id}' after the failed transition");
            }
            catch (Exception failed)
            {
                error += $"; previous state restoration failed, the state stays broken: {failed}";
                Failures[id] = error;
                Core.Log.Error(error);
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
                    // Dormant asks for nothing, so an expansion that failed to place cannot break property loading.
                    if (entry.Value == null)
                    {
                        if (UnplacedDormantLogged.Add(entry.Key))
                            Core.Log.Warning($"world state: '{entry.Key}' is dormant and has no placed expansion; property objects load without it");
                        continue;
                    }
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
            {
                try { item.Navigation?.Dispose(); }
                catch (Exception e) { Core.Log.Error($"world state '{item.Expansion.Id}' navigation teardown failed: {e}"); }
                World.WeatherCover.RemoveState(CoverOwner(item.Expansion, item.State));
                // The objects go with the scene; their entries in the static GUIDManager do not.
                foreach (var property in item.Properties) property.ReleaseGuids();
            }
            Active.Clear();
            UnplacedDormantLogged.Clear();
            StateCuts.Reset();
            ColliderKeepAlive.Reset();
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
