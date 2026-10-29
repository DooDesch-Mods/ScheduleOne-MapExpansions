using MapExpansions.Lifecycle;

namespace MapExpansions.Tests
{
    /// <summary>Laying layer entries over code entries: replace in place, remove, add, and what is reported.</summary>
    public static class LayerMergeTests
    {
        private sealed class Item
        {
            public string Id;
            public string Value;
        }

        private static List<Item> Code() => new()
        {
            new Item { Id = "a", Value = "code-a" },
            new Item { Id = "b", Value = "code-b" },
            new Item { Id = "c", Value = "code-c" },
        };

        private static LayerMerge.Entry<string> E(string key, string value, bool removed = false) => new(key, removed, value);

        private static LayerMerge.Result<Item> Run(List<Item> code, params LayerMerge.Entry<string>[] entries) =>
            LayerMerge.Merge(code, i => i.Id, entries,
                (i, v) => new Item { Id = i.Id, Value = v },
                v => v == null ? null : new Item { Id = v.Split('=')[0], Value = v.Split('=')[1] });

        private static string Ids(LayerMerge.Result<Item> r) => string.Join(",", r.Items.Select(i => i.Id + ":" + i.Value));

        public static void AnEmptyLayerLeavesTheCodeAsItIs()
        {
            var r = Run(Code());
            Check.Eq("a:code-a,b:code-b,c:code-c", Ids(r));
            Check.Eq(0, r.Layered.Count);
        }

        public static void AnEntryWithACodeKeyReplacesItInPlace()
        {
            var r = Run(Code(), E("b", "layer-b"));
            Check.Eq("a:code-a,b:layer-b,c:code-c", Ids(r));
            Check.Eq(1, r.Replaced);
            Check.Eq("b", string.Join(",", r.Layered));
        }

        public static void ARemovedEntryTakesTheCodeEntryOut()
        {
            var r = Run(Code(), E("a", null, removed: true));
            Check.Eq("b:code-b,c:code-c", Ids(r));
            Check.Eq(1, r.Removed);
            Check.Eq("a", string.Join(",", r.Layered));
        }

        public static void ANewEntryIsAddedAtTheEndInLayerOrder()
        {
            var r = Run(Code(), E("z", "z=new-z"), E("y", "y=new-y"));
            Check.Eq("a:code-a,b:code-b,c:code-c,z:new-z,y:new-y", Ids(r));
            Check.Eq(2, r.Added);
        }

        public static void RemovingWhatTheCodeLacksIsAnOrphanAndChangesNothing()
        {
            var r = Run(Code(), E("q", null, removed: true));
            Check.Eq("a:code-a,b:code-b,c:code-c", Ids(r));
            Check.Eq("q", string.Join(",", r.Orphans));
            Check.Eq(0, r.Layered.Count);
        }

        public static void TheLastOfTwoEntriesWithOneKeyCounts()
        {
            var r = Run(Code(), E("b", "first"), E("b", "second"));
            Check.Eq("a:code-a,b:second,c:code-c", Ids(r));
            Check.Eq("b", string.Join(",", r.Twice));
        }

        public static void ALaterRemovalBeatsAnEarlierReplacement()
        {
            var r = Run(Code(), E("b", "first"), E("b", null, removed: true));
            Check.Eq("a:code-a,c:code-c", Ids(r));
        }

        public static void ANewEntryThatCannotBeBuiltIsReportedAndLeftOut()
        {
            var r = Run(Code(), E("n", null));
            Check.Eq("a:code-a,b:code-b,c:code-c", Ids(r));
            Check.Eq("n", string.Join(",", r.Failed));
            Check.Eq(0, r.Layered.Count);
        }

        public static void EntriesWithoutAKeyAreIgnored()
        {
            var r = Run(Code(), E(null, "x"), E("", "y"));
            Check.Eq("a:code-a,b:code-b,c:code-c", Ids(r));
        }

        public static void TheCodeListIsNotChanged()
        {
            var code = Code();
            Run(code, E("a", "layer"), E("b", null, removed: true), E("z", "z=new"));
            Check.Eq(3, code.Count);
            Check.Eq("code-a", code[0].Value);
        }

        public static void MergingTwiceFromTheSameCodeGivesTheSameResult()
        {
            var code = Code();
            var first = Ids(Run(code, E("a", "layer"), E("z", "z=new")));
            var second = Ids(Run(code, E("a", "layer"), E("z", "z=new")));
            Check.Eq(first, second, "a layer must not stack on its own earlier result");
        }
    }

    /// <summary>Ids of rules and boxes the code wrote without one.</summary>
    public static class IdTests
    {
        public static void ARuleIdHoldsSideNamesAndEffect()
        {
            Check.Eq("main:Pawn shop@Map:hide", LayerMerge.RuleId("Main", "Pawn shop", "Map", null, null, "Hide"));
            Check.Eq("expansion:Wall:nocollision", LayerMerge.RuleId("Expansion", "Wall", null, null, null, "NoCollision"));
        }

        public static void ARuleIdHoldsTheAreaInMillimetres()
        {
            var id = LayerMerge.RuleId("Main", "Tree", null, new[] { 1.23456f, 0f, -2.5f }, new[] { 3f, 4.1f, 5f }, "Remove");
            Check.Eq("main:Tree[1.235,0,-2.5..3,4.1,5]:remove", id);
        }

        public static void AnEmptyAreaIsLeftOutOfTheId()
        {
            Check.Eq("main:Tree:hide", LayerMerge.RuleId("Main", "Tree", null, new[] { 1f, 2f, 3f }, new[] { 1f, 2f, 3f }, "Hide"));
        }

        public static void RepeatedIdsAreNumberedInOrder()
        {
            var ids = LayerMerge.Unique(new[] { "x", "y", "x", "x" });
            Check.Eq("x,y,x#2,x#3", string.Join(",", ids));
        }

        public static void BoxIdsCountPerKindFromOne()
        {
            Check.Eq("panel-1", LayerMerge.BoxId("Panel", 0));
            Check.Eq("block-3", LayerMerge.BoxId("Block", 2));
        }

        public static void NumbersAreInvariant()
        {
            var old = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Check.Eq("1.5", LayerMerge.Number(1.5f));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = old;
            }
        }
    }
}
