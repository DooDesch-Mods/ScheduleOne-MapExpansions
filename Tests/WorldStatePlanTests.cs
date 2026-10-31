using MapExpansions.Lifecycle;
using Plan = MapExpansions.Lifecycle.WorldStatePlan;

namespace MapExpansions.Tests
{
    public static class WorldStatePlanTests
    {
        private static readonly string[] NoCopies = Array.Empty<string>();

        private static Plan.Extension Ext(string id, string signature, params string[] carriers) => new(id, signature, carriers);

        public static void AnUnchangedExtensionStaysAndAChangedOneDoesNot()
        {
            var plan = Plan.Plan(
                new[] { Ext("floor", "a"), Ext("dock", "b"), Ext("gone", "c") },
                Array.Empty<Plan.Copy>(),
                new[] { Ext("floor", "a"), Ext("dock", "changed"), Ext("new", "d") },
                Array.Empty<Plan.Copy>());
            Check.True(plan.Extensions.SetEquals(new[] { "floor" }));
            Check.Eq(0, plan.Copies.Count);
        }

        public static void ADamagedLeaseIsNeverKept()
        {
            var plan = Plan.Plan(
                new[] { new Plan.Extension("floor", "a", NoCopies, damaged: true) },
                Array.Empty<Plan.Copy>(),
                new[] { Ext("floor", "a") },
                Array.Empty<Plan.Copy>());
            Check.Eq(0, plan.Extensions.Count);
        }

        public static void AKeptExtensionKeepsTheCopyItsSurfacesStandOn()
        {
            var copies = new[] { new Plan.Copy("wing", "w"), new Plan.Copy("props", "p") };
            var plan = Plan.Plan(new[] { Ext("floor", "a", "wing") }, copies, new[] { Ext("floor", "a", "wing") }, copies);
            Check.True(plan.Extensions.SetEquals(new[] { "floor" }));
            Check.True(plan.Copies.SetEquals(new[] { "wing" }), "only the carrier stays");
        }

        public static void AChangedCarrierRebuildsTheExtension()
        {
            var plan = Plan.Plan(
                new[] { Ext("floor", "a", "wing") },
                new[] { new Plan.Copy("wing", "w") },
                new[] { Ext("floor", "a", "wing") },
                new[] { new Plan.Copy("wing", "w2") });
            Check.Eq(0, plan.Extensions.Count);
            Check.Eq(0, plan.Copies.Count);
        }

        public static void ACarrierThatLeavesTheStateRebuildsTheExtension()
        {
            var plan = Plan.Plan(
                new[] { Ext("floor", "a", "wing") },
                new[] { new Plan.Copy("wing", "w") },
                new[] { Ext("floor", "a", "wing") },
                Array.Empty<Plan.Copy>());
            Check.Eq(0, plan.Extensions.Count);
        }

        public static void ACarrierOfTheExpansionItselfDoesNotBlockKeeping()
        {
            var plan = Plan.Plan(new[] { Ext("floor", "a", "base") }, Array.Empty<Plan.Copy>(), new[] { Ext("floor", "a", "base") }, Array.Empty<Plan.Copy>());
            Check.True(plan.Extensions.SetEquals(new[] { "floor" }));
            Check.Eq(0, plan.Copies.Count, "a transition never owns the expansion's copy");
        }

        private static readonly Dictionary<string, IReadOnlyDictionary<string, bool>> Bindable = new()
        {
            ["wing"] = new Dictionary<string, bool> { ["wall-collision"] = true, ["wall"] = false },
        };

        public static void ASurfaceMustStandOnAnExistingBlock()
        {
            Check.Eq(null, Plan.SurfaceBindingError("s", "wing", "wall-collision", Bindable));
            Check.Eq(null, Plan.SurfaceBindingError("s", null, null, Bindable), "a standalone surface has no binding");
            Check.Contains(Plan.SurfaceBindingError("s", "wing", null, Bindable), "needs a box id");
            Check.Contains(Plan.SurfaceBindingError("s", "other", "wall", Bindable), "copy 'other'");
            Check.Contains(Plan.SurfaceBindingError("s", "wing", "missing", Bindable), "box 'missing'");
            Check.Contains(Plan.SurfaceBindingError("s", "wing", "wall", Bindable), "needs a block");
        }
    }

    public static class ColliderDetachLedgerTests
    {
        private static readonly IntPtr Key = new(1);

        public static void TheLastHolderGivesTheColliderBack()
        {
            var ledger = new ColliderDetachLedger<string, string>();
            Check.True(ledger.Retain(Key), "the first holder scans for the groups");
            Check.False(ledger.Retain(Key), "the second holder needs no scan");
            ledger.Record(Key, "collider", "group-a");
            ledger.Record(Key, "collider", "group-b");
            Check.Eq(null, ledger.Release(Key));
            var entry = ledger.Release(Key);
            Check.Eq("collider", entry.Collider);
            Check.Eq(2, entry.Groups.Count);
            Check.Eq(0, ledger.Count);
        }

        public static void APermanentDetachNeverReturnsToAGroup()
        {
            var ledger = new ColliderDetachLedger<string, string>();
            ledger.Retain(Key);
            ledger.Record(Key, "collider", "group");
            ledger.MarkPermanent(Key);
            Check.Eq(null, ledger.Release(Key));
            Check.Eq(0, ledger.Count);
        }

        public static void AColliderInNoGroupHasNothingToRestore()
        {
            var ledger = new ColliderDetachLedger<string, string>();
            ledger.Retain(Key);
            Check.Eq(null, ledger.Release(Key));
            Check.Eq(null, ledger.Release(Key), "a release without a holder is harmless");
        }
    }

    public static class PeerWorldStateAckRetryTests
    {
        public static void AJoinWithoutAcknowledgementIsKicked()
        {
            var gate = new PeerWorldStateAck(100);
            Check.True(gate.Require(1, 101));
            Check.False(gate.Expire(124.9));
            Check.True(gate.Expire(125));
        }

        public static void AMidSessionRevisionIsNeverKicked()
        {
            var gate = new PeerWorldStateAck(100);
            gate.Require(1, 101);
            gate.Confirm(1);
            Check.True(gate.Require(2, 500));
            Check.False(gate.Confirmed, "native replication is held for the new revision");
            Check.False(gate.Expire(10000));
            Check.False(gate.TimedOut);
            Check.True(gate.Confirm(2));
        }

        public static void TheRequestRepeatsOnAnIntervalUntilAcknowledged()
        {
            var gate = new PeerWorldStateAck(100);
            Check.True(gate.ShouldRequest(100), "a new gate requests at once");
            Check.False(gate.ShouldRequest(100.1), "once per interval");
            Check.False(gate.ShouldRequest(104.9));
            Check.True(gate.ShouldRequest(105));
            Check.True(gate.Require(1, 105.5));
            Check.False(gate.ShouldRequest(110), "a new revision was just sent");
            Check.True(gate.ShouldRequest(110.5));
            Check.True(gate.Confirm(1));
            Check.False(gate.ShouldRequest(200), "a confirmed peer needs no request");
        }

        public static void ATimedOutPeerGetsNoFurtherRequest()
        {
            var gate = new PeerWorldStateAck(100);
            Check.True(gate.Expire(125));
            Check.False(gate.ShouldRequest(200));
        }

        public static void AMidSessionBumpWaitsOneIntervalBeforeItsFirstRetry()
        {
            var gate = new PeerWorldStateAck(0);
            gate.Require(1, 1);
            gate.Confirm(1);
            gate.Require(2, 50);
            Check.False(gate.ShouldRequest(54.9));
            Check.True(gate.ShouldRequest(55));
        }
    }
}
