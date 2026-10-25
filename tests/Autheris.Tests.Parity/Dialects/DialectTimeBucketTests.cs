using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Robin;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Dialects;

/// <summary>One GraphQL time-bucket cell (REQ-LB-11): shared by the PostgreSQL and SQL Server classes so both assert the same expected values.</summary>
public sealed record TimeBucketCell(string Id, string Where, string Column, string Bucket, string? TimeZone, object?[][] Expected)
{
    public string Arguments =>
        (Where.Length > 0 ? $"where: {{ {Where} }}, " : string.Empty) +
        $"timeBuckets: [{{ column: {Column}, bucket: {Bucket}{(TimeZone is null ? string.Empty : $", timeZone: \"{TimeZone}\"")} }}], " +
        $"aggregates: [{{ fn: COUNT, alias: \"n\" }}], orderBy: [{{ column: {Column} }}]";
}

/// <summary>Expected bucket keys, computed by hand from <see cref="ParitySeed.Sales"/> (UTC, ISO Monday week start, Dec/Jan ISO week boundary; the Berlin week cell equals the UTC one, the zone-discriminating cells are the Berlin MONTH and New York WEEK cells).</summary>
public static class TimeBucketCells
{
    private const string Boundary = "id: { in: [10, 11] }";

    public static readonly IReadOnlyList<TimeBucketCell> All =
    [
        new("tb_week_sale_date", string.Empty, "sale_date", "WEEK", null,
        [
            ["2023-03-13", 1], ["2023-05-29", 1], ["2024-01-08", 2], ["2024-02-26", 1], ["2024-03-25", 1], ["2024-04-29", 1], ["2024-07-15", 2],
            ["2024-12-30", 1], ["2025-01-13", 1], ["2025-02-24", 3], ["2025-03-03", 1], ["2025-03-31", 2], ["2025-05-05", 1],
        ]),
        new("tb_quarter_sale_date", string.Empty, "sale_date", "QUARTER", null,
        [
            ["2023-01-01", 1], ["2023-04-01", 1], ["2024-01-01", 4], ["2024-04-01", 1], ["2024-07-01", 2], ["2024-10-01", 1], ["2025-01-01", 5], ["2025-04-01", 3],
        ]),
        new("tb_isoweek_year_boundary_utc", Boundary, "sold_at", "WEEK", "UTC", [["2024-12-30", 1], ["2025-01-13", 1]]),
        new("tb_isoweek_year_boundary_berlin", Boundary, "sold_at", "WEEK", "Europe/Berlin", [["2024-12-30", 1], ["2025-01-13", 1]]),
        // Row 10 is 2024-12-31T23:30Z = Wednesday 2025-01-01T00:30 in Berlin: the MONTH key moves into 2025 (UTC would give 2024-12-01).
        new("tb_month_year_boundary_berlin", "id: { in: [10] }", "sold_at", "MONTH", "Europe/Berlin", [["2025-01-01", 1]]),
        // Rows 13-15 are Sat/Sun/Mon 2025-03-01..03 at 00:00Z. In UTC the Monday row opens week 2025-03-03 (2025-02-24:2, 2025-03-03:1); in
        // America/New_York it is Sunday 2025-03-02 19:00, so all three rows fall into week 2025-02-24.
        new("tb_isoweek_zone_shift_new_york", "id: { in: [13, 14, 15] }", "sold_at", "WEEK", "America/New_York", [["2025-02-24", 3]]),
        new("tb_hour_sold_at", "id: { in: [1, 8] }", "sold_at", "HOUR", "UTC", [["2023-03-15T10:00:00", 1], ["2024-07-20T15:00:00", 1]]),
    ];

    public static IEnumerable<object[]> Ids() => All.Select(c => new object[] { c.Id });

    public static async Task RunAsync(IParityHost host, string schema, string cellId)
    {
        var cell = All.Single(c => c.Id == cellId);
        var field = $"default_{schema}_{ParitySeed.SalesTable}_aggregate";
        var query = "{ " + field + "(" + cell.Arguments + ") { groups { key { " + cell.Column + " } rowCount aggregates { " + RobinClient.AggregateSelection + " } } } }";
        var result = await new RobinClient(host).GraphQlAsync(query, field);
        result.RowsShouldBe([cell.Column, "n"], cell.Expected, ordered: true);
    }
}

/// <summary>GraphQL time-bucket cells (week, quarter, ISO week at the Dec/Jan boundary, hour) against a SQL Server Testcontainers host. A 5xx is a hard failure (SEC-PAR-05); no cell is baselined as a gap.</summary>
[Trait("Category", "Parity")]
[Trait("Dialect", "Container")]
[Collection(ParitySqlServerCollection.Name)]
public sealed class DialectTimeBucketSqlServerTests(ParitySqlServerHostFixture host) : ParityTestBase
{
    [ParityTheory]
    [MemberData(nameof(TimeBucketCells.Ids), MemberType = typeof(TimeBucketCells))]
    public Task TimeBucket_Cell_SqlServer(string cellId) => ParityCell.RunAsync(
        new ParityCellInfo("timebucket", "GraphQl", cellId, "cell", "sqlserver"),
        () => TimeBucketCells.RunAsync(host, ParitySqlServerHostFixture.Schema, cellId));
}

/// <summary>The same time-bucket cells and expected values against the PostgreSQL container host.</summary>
[Trait("Category", "Parity")]
[Trait("Dialect", "Container")]
[Collection(ParityPostgresCollection.Name)]
public sealed class DialectTimeBucketPostgresTests(ParityPostgresHostFixture host) : ParityTestBase
{
    [ParityTheory]
    [MemberData(nameof(TimeBucketCells.Ids), MemberType = typeof(TimeBucketCells))]
    public Task TimeBucket_Cell_Postgres(string cellId) => ParityCell.RunAsync(
        new ParityCellInfo("timebucket", "GraphQl", cellId, "cell", "postgres"),
        () => TimeBucketCells.RunAsync(host, ParityPostgresHostFixture.Schema, cellId));
}
