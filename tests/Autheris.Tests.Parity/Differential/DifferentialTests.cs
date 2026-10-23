using Autheris.Tests.Parity.Infrastructure;

namespace Autheris.Tests.Parity.Differential;

/// <summary>
/// Block E against the in-process SQLite host (D-16): the same question through the product and directly on the seed database.
/// The host principal has no row filter or masking on <c>parity_sales</c> and the k-anonymity minimum group size is lifted
/// (<see cref="ParityHostSettings"/>), so the reference needs no policy of its own.
/// </summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed class DifferentialTests(ParityHostFixture host) : ParityTestBase
{
    public static IEnumerable<object[]> Cases() => DifferentialCorpus.Cases().Select(c => new object[] { c.Channel, c.Id });

    public static IEnumerable<object[]> TlpPredicates() => Tlp.Predicates.Select(p => new object[] { p.Id });

    [ParityTheory]
    [MemberData(nameof(Cases))]
    public Task Differential_Case(string channel, string caseId)
    {
        var c = DifferentialCorpus.Cases().Single(x => x.Channel == channel && x.Id == caseId);
        return ParityCell.RunAsync(
            new ParityCellInfo("differential", channel, c.Capability, c.CellCaseId, GapId: c.GapId),
            () => new DifferentialExecutor(host).RunAsync(c));
    }

    [ParityTheory]
    [MemberData(nameof(TlpPredicates))]
    public Task Differential_Tlp(string predicateId) => ParityCell.RunAsync(
        new ParityCellInfo("differential", "WebSql", "tlp", predicateId, GapId: TlpGaps.For(predicateId)),
        () => Tlp.RunProductAsync(host, new SqliteReference(host.DataPath), predicateId));
}

/// <summary>Known gaps of the TLP cells (strict xfail); empty until a nightly run registers one.</summary>
public static class TlpGaps
{
    private static readonly IReadOnlyDictionary<string, string> Gaps = new Dictionary<string, string>(StringComparer.Ordinal);

    public static string? For(string predicateId) => Gaps.TryGetValue(predicateId, out var id) ? id : null;
}
