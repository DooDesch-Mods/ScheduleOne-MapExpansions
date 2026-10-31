using MapExpansions.Lifecycle;

namespace MapExpansions.Tests
{
    public static class WorldStateSafetyTests
    {
        public static void OpeningPreservesExactlyTheRemainingSolidVolume()
        {
            var source = new BoxDifference.Box(0, 0, 0, 4, 3, 2);
            var opening = new BoxDifference.Box(1, 0, -1, 3, 2, 3);
            var slabs = BoxDifference.Subtract(source, opening);
            Check.Eq(16f, slabs.Sum(x => x.Volume));
            for (int i = 0; i < slabs.Count; i++)
                for (int j = i + 1; j < slabs.Count; j++)
                {
                    var a = slabs[i]; var b = slabs[j];
                    Check.False(MathF.Min(a.MaxX, b.MaxX) > MathF.Max(a.MinX, b.MinX) &&
                        MathF.Min(a.MaxY, b.MaxY) > MathF.Max(a.MinY, b.MinY) &&
                        MathF.Min(a.MaxZ, b.MaxZ) > MathF.Max(a.MinZ, b.MinZ), "solid slabs overlap");
                }
        }
        public static void AContainedOpeningLeavesSixSlabsAndNoSolidCenter()
        {
            var slabs = BoxDifference.Subtract(new(0, 0, 0, 3, 3, 3), new(1, 1, 1, 2, 2, 2));
            Check.Eq(6, slabs.Count);
            Check.Eq(26f, slabs.Sum(x => x.Volume));
            Check.False(slabs.Any(b => 1.5f > b.MinX && 1.5f < b.MaxX && 1.5f > b.MinY && 1.5f < b.MaxY && 1.5f > b.MinZ && 1.5f < b.MaxZ));
        }
        public static void ADisjointOpeningRetainsTheOriginalAndAFullOpeningLeavesNothing()
        {
            var source = new BoxDifference.Box(0, 0, 0, 2, 2, 2);
            Check.Eq(source, BoxDifference.Subtract(source, new(3, 0, 0, 4, 1, 1)).Single());
            Check.Eq(source, BoxDifference.Subtract(source, new(2, 0, 0, 3, 1, 1)).Single());
            Check.Eq(0, BoxDifference.Subtract(source, new(-1, -1, -1, 3, 3, 3)).Count);
        }
        public static void PendingReplicationNeedsAnExplicitMatchingRevision()
        {
            var gate = new PeerWorldStateAck(10);
            Check.False(gate.Confirmed);
            Check.False(gate.Confirm(0));
            Check.True(gate.Require(5, 11));
            Check.False(gate.Confirm(4));
            Check.False(gate.Confirm(6));
            Check.True(gate.Confirm(5));
            Check.True(gate.Confirmed);
            Check.True(gate.Require(5, 12));
            Check.True(gate.Confirmed);
            Check.True(gate.Require(6, 13));
            Check.False(gate.Confirmed);
            Check.False(gate.Require(5, 14));
        }
        public static void RepeatedRequestsCannotExtendTheTimeoutOrReleaseAfterIt()
        {
            var gate = new PeerWorldStateAck(100);
            Check.True(gate.Require(7, 101));
            Check.True(gate.Require(7, 124));
            Check.False(gate.Expire(124.9));
            Check.True(gate.Expire(125));
            Check.True(gate.TimedOut);
            Check.False(gate.Confirmed);
            Check.False(gate.Confirm(7));
            Check.False(gate.Require(8, 126));
            Check.False(gate.Expire(130));
        }
    }
}
