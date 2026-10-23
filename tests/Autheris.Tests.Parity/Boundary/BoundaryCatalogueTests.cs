using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;
using Shouldly;

namespace Autheris.Tests.Parity.Boundary;

/// <summary>Pure unit tests of the shared boundary catalogue (D-10). No host needed.</summary>
[Trait("Category", "Parity")]
public sealed class BoundaryCatalogueTests : ParityTestBase
{
    [ParityFact]
    public void Boundary_IdsUniqueAndOrdered()
    {
        var ids = BoundaryValues.All.Select(v => v.Id).ToList();
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(ids.Count);
        // ordered: grouped by family prefix in declaration order, no family reappears later
        var families = ids.Select(i => i[..i.IndexOf('.', StringComparison.Ordinal)]).ToList();
        var seen = new List<string>();
        foreach (var f in families)
        {
            if (seen.LastOrDefault() != f)
            {
                seen.ShouldNotContain(f, $"family '{f}' is not contiguous");
                seen.Add(f);
            }
        }
    }

    [ParityFact]
    public void Boundary_CoversEveryRequiredFamily()
    {
        string[] required =
        [
            "str.empty", "null.any", "in.one", "str.quote", "str.emoji", "str.sql_drop", "str.len64k", "date.leap_2024_02_29",
            "date.invalid_2100_02_29", "ts.exact_dst_day", "ts.dst_ambiguous_hour", "ts.tz_invalid_zone", "in.empty", "in.duplicates",
            "int.max", "dec.nan_string", "page.size_neg_1", "page.size_max_plus_1",
        ];
        var ids = BoundaryValues.All.Select(v => v.Id).ToHashSet();
        required.Where(r => !ids.Contains(r)).ShouldBeEmpty();
    }

    [ParityFact]
    public void Boundary_EveryValueHasDecidedExpectation()
    {
        foreach (var v in BoundaryValues.All)
        {
            Enum.IsDefined(v.Expected).ShouldBeTrue(v.Id);
            if (v.Type == ParityType.List)
            {
                v.Value.ShouldBeOfType<ListKind>(v.Id);
            }
        }

        // every slot of every channel yields an oracle expectation without throwing
        foreach (var channel in BoundaryCases.ChannelNames)
        {
            foreach (var c in BoundaryCases.For(channel))
            {
                Should.NotThrow(() => BoundaryOracle.Expected(channel, c), $"{channel}|{c.Id}");
            }
        }
    }

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public void Boundary_CaseCountWithinBudget(string channel)
    {
        var total = BoundaryCases.For(channel).Count + BoundaryCases.CrossType(channel).Count + BoundaryCases.ObservedServerErrors(channel).Count;
        total.ShouldBeLessThanOrEqualTo(BoundaryCases.Budget);
        BoundaryCases.For(channel).Select(c => c.Id).Distinct().Count().ShouldBe(BoundaryCases.For(channel).Count);
    }

    [ParityFact]
    public void Boundary_CasesAppliedToEveryFilterSortAndPagingParameter()
    {
        foreach (var channel in BoundaryCases.ChannelNames)
        {
            var slots = BoundaryCases.For(channel).Select(c => c.Slot).Distinct().ToHashSet();
            foreach (var slot in Enum.GetValues<BoundarySlot>().Where(s => BoundaryCases.Supports(channel, s)))
            {
                slots.ShouldContain(slot, $"{channel} has no case for {slot}");
            }
        }
    }

    [ParityFact]
    public void Boundary_OracleMatchesHandComputedSeedValues()
    {
        var eq = (string column, string valueId) =>
            BoundaryOracle.Expected("WebSql", new BoundaryCase(BoundarySlot.Eq, BoundaryValues.Column(column), BoundaryValues.All.Single(v => v.Id == valueId))).Ids;
        eq("region", "str.exact_north")!.Count.ShouldBe(ParityExpected.RowsByRegion["North"]);
        eq("region", "str.case_lower")!.ShouldBeEmpty();
        eq("region", "null.any")!.ShouldBeEmpty();
        eq("note", "str.quote")!.ShouldBeEmpty();
        eq("note", "str.emoji")!.ShouldBe([8]);
        eq("note", "str.select_keyword")!.ShouldBe([6]);
        eq("sale_date", "date.leap_2024_02_29")!.ShouldBe([5]);
        eq("sold_at", "ts.exact_dst_day")!.ShouldBe([6]);
        eq("amount", "dec.exact")!.ShouldBe([2]);
        eq("amount", "dec.trailing_scale")!.ShouldBe([1]);
        eq("qty", "int.exact_qty")!.ShouldBe([1]);
        eq("is_return", "bool.true")!.ShouldBe([5, 9, 17]);

        BoundaryOracle.Expected("WebSql", new BoundaryCase(BoundarySlot.Limit, null, BoundaryValues.All.Single(v => v.Id == "page.size_17"))).Ids!.Count.ShouldBe(17);
        BoundaryOracle.Expected("WebSql", new BoundaryCase(BoundarySlot.Offset, null, BoundaryValues.All.Single(v => v.Id == "page.offset_17"))).Ids.ShouldBe([18]);
        BoundaryOracle.Expected("WebSql", new BoundaryCase(BoundarySlot.Offset, null, BoundaryValues.All.Single(v => v.Id == "page.offset_19"))).Ids!.ShouldBeEmpty();
        BoundaryOracle.Expected("WebSql", new BoundaryCase(BoundarySlot.Limit, null, BoundaryValues.All.Single(v => v.Id == "page.size_neg_1"))).Kind.ShouldBe(Expectation.Rejected);
        BoundaryOracle.Expected("WebSql", new BoundaryCase(BoundarySlot.Sort, null, BoundaryValues.All.Single(v => v.Id == "str.quote"))).Kind.ShouldBe(Expectation.Rejected);
        BoundaryOracle.Expected("WebSql", new BoundaryCase(BoundarySlot.Sort, null, BoundaryValues.All.Single(v => v.Id == "str.col_note"))).Kind.ShouldBe(Expectation.Ok);
    }

    [ParityFact]
    public void Boundary_RenderersExpressEveryGeneratedCase()
    {
        foreach (var channel in BoundaryCases.ChannelNames)
        {
            foreach (var c in BoundaryCases.For(channel).Concat(BoundaryCases.CrossType(channel)))
            {
                Channels.BoundaryChannels.For(channel).Render(c).ShouldNotBeNull($"{channel}|{c.Id}");
            }
        }
    }

    [ParityFact]
    public void BoundaryKnownGaps_UnmatchedCaseRunsNormally() =>
        BoundaryKnownGaps.Classify("WebSql", "eq.region.str.empty").Kind.ShouldBe(BoundaryClass.None);
}
