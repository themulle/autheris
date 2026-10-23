using Autheris.Tests.Parity.Infrastructure;
using Shouldly;

namespace Autheris.Tests.Parity.Matrix;

/// <summary>Block A: structure tests of the matrix (no host) and one theory row per capability x channel cell against the in-process SQLite host.</summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed class MatrixTests(ParityHostFixture host) : ParityTestBase
{
    private static readonly string[] RequiredCapabilities =
    [
        "agg_sum", "agg_avg", "agg_count", "agg_min", "agg_max", "agg_count_distinct",
        "group_column", "group_date_year", "group_date_month", "group_date_day", "group_expression",
        "filter_eq", "filter_range", "filter_like", "filter_in_list", "filter_in_empty", "filter_is_null", "filter_empty_object", "filter_none",
        "order_desc_then_id", "paging_offset_limit", "null_count_column", "param_aggregate_eq", "param_in_list",
    ];

    public static IEnumerable<object[]> AllCells() =>
        ParityMatrix.Capabilities.SelectMany(_ => ParityMatrix.Channels, (c, ch) => new object[] { c, ch.ToString() });

    [Fact]
    public void Matrix_IsComplete()
    {
        ParityMatrix.Capabilities.Distinct().Count().ShouldBe(ParityMatrix.Capabilities.Count);
        ParityMatrix.Missing(ParityMatrix.Capabilities, ParityMatrix.Channels, ParityMatrix.Cells).ShouldBeEmpty();
        ParityMatrix.Cells.Count.ShouldBe(ParityMatrix.Capabilities.Count * ParityMatrix.Channels.Count);
        RequiredCapabilities.ShouldBeSubsetOf(ParityMatrix.Capabilities);
    }

    [Fact]
    public void Matrix_IsCompleteDetectsMissingCell()
    {
        var cells = ParityMatrix.Cells.Where(kv => kv.Key != ("agg_sum", MatrixChannel.Rest)).ToDictionary(kv => kv.Key, kv => kv.Value);
        ParityMatrix.Missing(ParityMatrix.Capabilities, ParityMatrix.Channels, cells).ShouldBe([("agg_sum", MatrixChannel.Rest)]);
    }

    [Fact]
    public void Matrix_UnsupportedHasCode()
    {
        foreach (var ((capability, channel), cell) in ParityMatrix.Cells)
        {
            if (cell is Unsupported u)
            {
                // a null code (any stable catalogue code) is legal only while a registered gap tracks the missing rejection
                (u.Code is not null || MatrixKnownGaps.For(capability, channel) is not null).ShouldBeTrue($"{capability}/{channel}");
            }
        }

        ParityMatrix.Cells.Values.OfType<Unsupported>().ShouldNotBeEmpty();
    }

    [Fact]
    public void Matrix_PendingHasReason()
    {
        var pending = ParityMatrix.Cells.Where(kv => kv.Value is Pending).ToList();
        pending.ShouldNotBeEmpty();
        pending.ShouldAllBe(kv => ((Pending)kv.Value).Reason.Length > 10);
        ParityMatrix.Capabilities.All(cap => ParityMatrix.Cells[(cap, MatrixChannel.Mcp)] is Pending).ShouldBeTrue();
    }

    [Fact]
    public void Matrix_PendingReasonsAreInexpressibleOrFollowUp()
    {
        var followUps = PendingReasons.FollowUpIds(PendingReasons.ReadPlan());
        followUps.ShouldContain("PARITY-FU-001");
        foreach (var ((capability, channel), cell) in ParityMatrix.Cells)
        {
            if (cell is Pending p)
            {
                PendingReasons.IsValid(p.Reason, followUps).ShouldBeTrue($"{capability}/{channel}: '{p.Reason}'");
            }
        }

        ParityMatrix.Cells[("agg_sum", MatrixChannel.Mcp)].ShouldBeOfType<Pending>().Reason.ShouldContain("PARITY-FU-001");
    }

    [Fact]
    public void Matrix_PendingReasonCheckFailsClosed()
    {
        string[] known = ["PARITY-FU-001"];
        PendingReasons.IsValid("inexpressible: no such operator", known).ShouldBeTrue();
        PendingReasons.IsValid("deferred (PARITY-FU-001)", known).ShouldBeTrue();
        PendingReasons.IsValid("deferred (PARITY-FU-009)", known).ShouldBeFalse();
        PendingReasons.IsValid("inexpressible:", known).ShouldBeFalse();
        PendingReasons.IsValid("not driven by the host yet", known).ShouldBeFalse();
        PendingReasons.IsValid("deferred (PARITY-FU-001) and PARITY-FU-009", known).ShouldBeFalse();
    }

    [Fact]
    public void Matrix_GapsPointAtExistingNonPendingCells()
    {
        foreach (var key in MatrixKnownGaps.All.Keys)
        {
            var parts = key.Split('|');
            var cell = ParityMatrix.Cells[(parts[0], Enum.Parse<MatrixChannel>(parts[1]))];
            cell.ShouldNotBeOfType<Pending>(key);
        }
    }

    [Fact]
    public void Matrix_SupportedCellsHaveExpectedRows()
    {
        foreach (var ((capability, channel), cell) in ParityMatrix.Cells)
        {
            if (cell is Supported s)
            {
                s.Expected.Columns.ShouldNotBeEmpty($"{capability}/{channel}");
                s.Expected.Rows.ShouldAllBe(r => r.Length == s.Expected.Columns.Length, $"{capability}/{channel}");
                (s.ActualColumns ?? s.Expected.Columns).Length.ShouldBe(s.Expected.Columns.Length);
            }
        }
    }

    [ParityTheory]
    [MemberData(nameof(AllCells))]
    public Task Matrix_Cell(string capability, string channel)
    {
        var ch = Enum.Parse<MatrixChannel>(channel);
        return ParityCell.RunAsync(
            new ParityCellInfo("matrix", channel, capability, "cell", GapId: MatrixKnownGaps.For(capability, ch)),
            () => new MatrixExecutor(host).RunCellAsync(capability, ch));
    }
}
