using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Matrix;
using Autheris.Tests.Parity.Pairwise;
using Autheris.Tests.Parity.Robin;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Dialects;

/// <summary>
/// Container dialect job (D-15): the matrix subset that does not use SQLite-only functions, the pairwise cases of the PostgreSQL dialect and
/// Robin item 2 (#280) against a PostgreSQL Testcontainers host. Selected only by <c>Dialect=Container</c> in <c>parity-dialects</c>.
/// SQL Server cells are registered Pending (no host yet). Unknown PostgreSQL behaviour is not pre-registered as a gap: the first nightly
/// run decides, and a 5xx or leak is a hard failure (SEC-PAR-05).
/// </summary>
[Trait("Category", "Parity")]
[Trait("Dialect", "Container")]
[Collection(ParityPostgresCollection.Name)]
public sealed class DialectMatrixTests(ParityPostgresHostFixture host) : ParityTestBase
{
    /// <summary>Capabilities whose WebSQL request text is written with the SQLite function <c>strftime</c>; the text has no PostgreSQL spelling, so the cell cannot be stated here.</summary>
    private static readonly HashSet<string> SqliteOnlyText = ["group_date_year", "group_date_month", "group_date_day"];

    /// <summary>Pending reason of those cells; validated by the <c>PendingReasons</c> rule in <c>DialectStructureTests</c>. This is not a product gap (PARITY-GAP-013 is resolved).</summary>
    public const string SqliteOnlyReason = "inexpressible: the request text uses SQLite strftime and has no PostgreSQL spelling";

    public static IEnumerable<object[]> Cells() =>
        ParityMatrix.Capabilities.SelectMany(_ => ParityMatrix.Channels, (c, ch) => new object[] { c, ch.ToString() });

    [ParityTheory]
    [MemberData(nameof(Cells))]
    public Task Matrix_Cell_Postgres(string capability, string channel) => ParityCell.RunAsync(
        new ParityCellInfo("matrixpg", channel, capability, "cell", "postgres", GapId: MatrixKnownGaps.For(capability, Enum.Parse<MatrixChannel>(channel), "postgres")),
        () => SqliteOnlyText.Contains(capability) && channel == "WebSql"
            ? throw new PendingCellException(channel, capability, SqliteOnlyReason)
            : new MatrixExecutor(host, ParityPostgresHostFixture.Schema).RunCellAsync(capability, Enum.Parse<MatrixChannel>(channel)));
}

[Trait("Category", "Parity")]
[Trait("Dialect", "Container")]
[Collection(ParityPostgresCollection.Name)]
public sealed class DialectPairwiseTests(ParityPostgresHostFixture host, ParitySqlServerHostFixture sqlServerHost) : ParityTestBase, IClassFixture<ParitySqlServerHostFixture>
{
    public static IEnumerable<object[]> ContainerCases() =>
        PairwiseModel.Cases().Where(c => c.Dialect != PairwiseModel.Sqlite).SelectMany(c => new[] { "WebSql", "GraphQl" }, (c, ch) => new object[] { ch, c.Id });

    [ParityTheory]
    [MemberData(nameof(ContainerCases))]
    public Task Pairwise_Case_Container(string channel, string caseId)
    {
        var c = PairwiseModel.Cases().Single(x => x.Id == caseId);
        var activeHost = c.Dialect == PairwiseModel.SqlServer ? (IParityHost)sqlServerHost : host;
        var activeSchema = c.Dialect == PairwiseModel.SqlServer ? ParitySqlServerHostFixture.Schema : ParityPostgresHostFixture.Schema;
        return ParityCell.RunAsync(
            new ParityCellInfo("pairwise", channel, PairwiseExecutor.CellCapability, caseId, c.Dialect),
            () => new PairwiseExecutor(activeHost, activeSchema).RunAsync(c, channel));
    }
}

/// <summary>Robin item 2 (#280): aggregation with <c>where column = value</c> on PostgreSQL and SQL Server.</summary>
[Trait("Category", "Parity")]
[Trait("Dialect", "Container")]
[Collection(ParityPostgresCollection.Name)]
public sealed class DialectRobinTests(ParityPostgresHostFixture host, ParitySqlServerHostFixture sqlServerHost) : ParityTestBase, IClassFixture<ParitySqlServerHostFixture>
{
    private const string Field = "default_public_" + ParitySeed.SalesTable + "_aggregate";

    [ParityTheory]
    [InlineData("postgres", "GraphQl")]
    [InlineData("postgres", "WebSql")]
    [InlineData("sqlserver", "GraphQl")]
    [InlineData("sqlserver", "WebSql")]
    public Task Robin_280_AggregationWhereEqualsValue_Dialect(string dialect, string channel) => ParityCell.RunAsync(
        new ParityCellInfo("robin", channel, "aggregate_where_eq", $"Robin_280_AggregationWhereEqualsValue.{dialect}", dialect),
        async () =>
        {
            var activeHost = dialect == "sqlserver" ? (IParityHost)sqlServerHost : host;
            var field = dialect == "sqlserver"
                ? $"default_{ParitySqlServerHostFixture.Schema}_{ParitySeed.SalesTable}_aggregate"
                : Field;
            var robin = new RobinClient(activeHost);
            var result = channel == "GraphQl"
                ? await robin.GraphQlAsync(
                    "{ " + field + "(where: { region: { eq: \"North\" } }, aggregates: [{ fn: COUNT, alias: \"n\" }, { fn: SUM, column: amount, alias: \"total\" }]) { groups { rowCount aggregates { " + RobinClient.AggregateSelection + " } } } }",
                    field)
                : await robin.WebSqlAsync("SELECT COUNT(*) AS n, SUM(amount) AS total FROM parity_sales WHERE region = 'North'");
            result.RowsShouldBe(["n", "total"], [[ParityExpected.RowsByRegion["North"], ParityExpected.SumAmountByRegion["North"]]]);
        });
}
