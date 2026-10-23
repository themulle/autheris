using Autheris.Tests.Parity.Infrastructure;

namespace Autheris.Tests.Parity.Pairwise;

/// <summary>Block B against the in-process SQLite host. Container dialect cases run in <c>Dialects/DialectPairwiseTests</c>.</summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed class PairwiseCaseTests(ParityHostFixture host) : ParityTestBase
{
    public static IEnumerable<object[]> SqliteCases() =>
        PairwiseModel.Cases().Where(c => c.Dialect == PairwiseModel.Sqlite).SelectMany(c => new[] { "WebSql", "GraphQl" }, (c, ch) => new object[] { ch, c.Id });

    [ParityTheory]
    [MemberData(nameof(SqliteCases))]
    public Task Pairwise_Case(string channel, string caseId)
    {
        var c = PairwiseModel.Cases().Single(x => x.Id == caseId);
        return ParityCell.RunAsync(
            new ParityCellInfo("pairwise", channel, PairwiseExecutor.CellCapability, caseId, c.Dialect, PairwiseKnownGaps.For(c, channel)),
            () => new PairwiseExecutor(host).RunAsync(c, channel));
    }
}
