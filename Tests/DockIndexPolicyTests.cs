using MapExpansions.Lifecycle;

namespace MapExpansions.Tests
{
    public static class DockIndexPolicyTests
    {
        private static void Refused(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            Check.Fail("expected a delivery-index safety refusal");
        }

        public static void CumulativeSlotsKeepOriginalAndEarlierUpgradeIndices()
        {
            DockIndexPolicy.RequireAppend(2, 2);
            DockIndexPolicy.RequireAppend(3, 3);
            DockIndexPolicy.RequireTailRemoval(4, new[] { 3 });
            DockIndexPolicy.RequireAppend(3, 3);
            Refused(() => DockIndexPolicy.RequireAppend(4, 2));
            Refused(() => DockIndexPolicy.RequireAppend(4, 5));
        }
        public static void RemovingAnEarlierEffectCannotShiftRetainedDeliveries()
        {
            Refused(() => DockIndexPolicy.RequireTailRemoval(4, new[] { 2 }));
            Refused(() => DockIndexPolicy.RequireTailRemoval(5, new[] { 2, 4 }));
            Refused(() => DockIndexPolicy.RequireTailRemoval(4, new[] { 3, 3 }));
            DockIndexPolicy.RequireTailRemoval(4, new[] { 3, 2 });
            DockIndexPolicy.RequireTailRemoval(4, Array.Empty<int>());
        }
    }
}
