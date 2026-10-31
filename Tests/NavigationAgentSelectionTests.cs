using MapExpansions.Lifecycle;

namespace MapExpansions.Tests
{
    public static class NavigationAgentSelectionTests
    {
        private static readonly NavigationAgentSelection.Agent[] Registered = {
            new(0, "Humanoid"), new(-334000983, "Ignore Costs"), new(1479372276, "Big Humanoid"), new(-1923039037, "Employee")
        };
        public static void AnIndoorContractRetainsEveryRequiredHumanProfileWithoutRequiringTheLargeProfile()
        {
            var actual = NavigationAgentSelection.Resolve(new[] { "Humanoid", "Ignore Costs", "Employee" }, Registered);
            Check.Eq("0,-334000983,-1923039037", string.Join(",", actual));
            Check.False(actual.Contains(1479372276));
        }
        public static void AnUnspecifiedContractStillRequiresAllRegisteredProfiles()
        {
            var actual = NavigationAgentSelection.Resolve(Array.Empty<string>(), Registered);
            Check.Eq("0,-334000983,1479372276,-1923039037", string.Join(",", actual));
        }
        public static void MissingOrRepeatedProfilesFailInsteadOfFallingBack()
        {
            ExpectFailure(() => NavigationAgentSelection.Resolve(new[] { "Humanoid", "Missing" }, Registered), "Missing");
            ExpectFailure(() => NavigationAgentSelection.Resolve(new[] { "Humanoid", "Humanoid" }, Registered), "repeated");
            ExpectFailure(() => NavigationAgentSelection.Resolve(new[] { "humanoid" }, Registered), "humanoid");
            ExpectFailure(() => NavigationAgentSelection.Resolve(new[] { "Employee" }, Array.Empty<NavigationAgentSelection.Agent>()), "no registered");
        }
        private static void ExpectFailure(Action action, string cause)
        {
            try { action(); }
            catch (InvalidOperationException error) { Check.Contains(error.Message, cause); return; }
            Check.Fail("required-profile resolution unexpectedly succeeded");
        }
    }
}
