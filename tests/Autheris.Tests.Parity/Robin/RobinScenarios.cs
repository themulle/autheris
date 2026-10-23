using System.Runtime.CompilerServices;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Autheris.Tests.Parity.Seed;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Robin;

/// <summary>
/// Block G: scenarios of the app developer persona Robin (rgruebel). Each scenario is an end-user request over WebSQL, GraphQL and/or REST
/// and asserts the exact hand-computed result from <see cref="ParityExpected"/> (not merely "no 5xx"). The method name is
/// <c>Robin_&lt;issue&gt;_&lt;slug&gt;</c>; "Proactive" marks a scenario without a source issue. A functional deviation is registered in
/// <see cref="RobinKnownGaps"/> (cell-level, strict xfail); a 5xx or leak is a hard failure and never a known gap (SEC-PAR-05).
/// A new Robin issue adds one scenario here (plan section 7).
/// </summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed partial class RobinScenarios(ParityHostFixture host) : ParityTestBase
{
    private const string Sales = ParitySeed.SalesTable;
    private const string AllRows = "first: 100, orderBy: [{ id: ASC }]";

    private readonly RobinClient _robin = new(host);

    private Task Run(string channel, string capability, Func<Task> body, string? variant = null, [CallerMemberName] string caseId = "")
    {
        var id = variant is null ? caseId : $"{caseId}.{variant}";
        var gap = RobinKnownGaps.For(caseId, channel, variant);
        return ParityCell.RunAsync(new ParityCellInfo("robin", channel, capability, id, GapId: gap), body);
    }

    private static string List(string table, string args, string selection) => "{ default_main_" + table + "(" + args + ") { " + selection + " } }";

    private static string Aggregate(string table, string args, string keys = "") =>
        "{ default_main_" + table + "_aggregate(" + args + ") { groups { " + (keys.Length > 0 ? "key { " + keys + " } " : string.Empty) +
        "rowCount aggregates { " + RobinClient.AggregateSelection + " } } } }";

    private static string Field(string table) => "default_main_" + table + "_aggregate";

    private Task<ChannelResult> GqlAggregate(string args, string keys = "", string table = Sales) => _robin.GraphQlAsync(Aggregate(table, args, keys), Field(table));

    private Task<ChannelResult> GqlList(string args, string selection = "id", string table = Sales) => _robin.GraphQlAsync(List(table, args, selection), "default_main_" + table);

    private Task<ChannelResult> Sql(string sql, object? parameters = null) => _robin.WebSqlAsync(sql, parameters);

    private const string CountSum = "aggregates: [{ fn: COUNT, alias: \"n\" }, { fn: SUM, column: amount, alias: \"total\" }]";

    // ---------------------------------------------------------------------------------------------------------------------
    // 1. Sum/count/avg/min/max per group with a read-only token [#28, #112]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("GraphQl")]
    [InlineData("WebSql")]
    public Task Robin_28_AggregatesPerRegion(string channel) => Run(channel, "aggregate_per_group", async () =>
    {
        object?[][] expected =
        [
            ["North", 7, 635.75m, 635.75m / 7m, 15.25m, 200.00m],
            ["South", 6, 1175.75m, 1175.75m / 6m, 70.00m, 400.00m],
            ["Empty", 3, null, null, null, null],
            [null, 2, 70.00m, 35.00m, 10.00m, 60.00m],
        ];
        var r = channel == "GraphQl"
            ? await GqlAggregate(
                "groupBy: [region], aggregates: [{ fn: COUNT, alias: \"n\" }, { fn: SUM, column: amount, alias: \"total\" }, { fn: AVG, column: amount, alias: \"av\" }, " +
                "{ fn: MIN, column: amount, alias: \"lo\" }, { fn: MAX, column: amount, alias: \"hi\" }], orderBy: [{ column: region }]",
                "region")
            : await Sql("SELECT region, COUNT(*) AS n, SUM(amount) AS total, AVG(amount) AS av, MIN(amount) AS lo, MAX(amount) AS hi FROM parity_sales GROUP BY region ORDER BY region");
        r.RowsShouldBe(["region", "n", "total", "av", "lo", "hi"], expected);
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 3. Aggregation with where column = value [#280]; where with a bound parameter [#280]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("GraphQl")]
    [InlineData("WebSql")]
    [InlineData("Rest")]
    public Task Robin_280_AggregationWhereEqualsValue(string channel) => Run(channel, channel == "Rest" ? "list_where_eq" : "aggregate_where_eq", async () =>
    {
        switch (channel)
        {
            case "GraphQl":
                (await GqlAggregate("where: { region: { eq: \"North\" } }, " + CountSum)).RowsShouldBe(["n", "total"], [[ParityExpected.RowsByRegion["North"], ParityExpected.SumAmountByRegion["North"]]]);
                break;
            case "WebSql":
                (await Sql("SELECT COUNT(*) AS n, SUM(amount) AS total FROM parity_sales WHERE region = 'North'")).RowsShouldBe(["n", "total"], [[7, 635.75m]]);
                break;
            default:
                (await _robin.RestAsync(RobinClient.SalesRest + "?select=id&filter=" + Uri.EscapeDataString("region eq 'North'") + "&orderBy=id&limit=100")).IdsShouldBe(1, 2, 3, 4, 5, 6, 18);
                break;
        }
    });

    [ParityTheory]
    [InlineData("GraphQl")]
    [InlineData("WebSql")]
    public Task Robin_280_AggregationWhereParameter(string channel) => Run(channel, "aggregate_where_param", async () =>
    {
        var r = channel == "GraphQl"
            ? await _robin.GraphQlAsync(
                "query($r: String) { " + Field(Sales) + "(where: { region: { eq: $r } }, " + CountSum + ") { groups { rowCount aggregates { " + RobinClient.AggregateSelection + " } } } }",
                Field(Sales),
                new { r = "South" })
            : await Sql("SELECT COUNT(*) AS n, SUM(amount) AS total FROM parity_sales WHERE region = @region", new { region = "South" });
        r.RowsShouldBe(["n", "total"], [[ParityExpected.RowsByRegion["South"], ParityExpected.SumAmountByRegion["South"]]]);
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 4-6. Group by a date part and group by in WebSQL and GraphQL [proactive]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("strftime", "strftime('%Y', sale_date)")]
    [InlineData("extract", "EXTRACT(YEAR FROM sale_date)")]
    [InlineData("year_function", "year(sale_date)")]
    [InlineData("date_trunc", "date_trunc('year', sale_date)")]
    public Task Robin_Proactive_GroupByYearWebSql(string variant, string yearExpression) => Run("WebSql", "group_by_year", async () =>
    {
        var r = await Sql($"SELECT {yearExpression} AS y, COUNT(*) AS n, SUM(amount) AS total FROM parity_sales GROUP BY {yearExpression} ORDER BY y");
        r.RowsShouldBe(
            ["y", "n", "total"],
            ParityExpected.RowsByYear.OrderBy(p => p.Key).Select(p => new object?[] { p.Key, p.Value, ParityExpected.SumAmountByYear[p.Key] }).ToArray(),
            ordered: true);
    }, variant);

    [ParityTheory]
    [InlineData("strftime")]
    [InlineData("extract")]
    public Task Robin_Proactive_GroupByYearAndMonthWebSql(string variant) => Run("WebSql", "group_by_month", async () =>
    {
        var (y, m) = variant == "strftime"
            ? ("strftime('%Y', sale_date)", "strftime('%m', sale_date)")
            : ("EXTRACT(YEAR FROM sale_date)", "EXTRACT(MONTH FROM sale_date)");
        var r = await Sql($"SELECT {y} AS y, {m} AS m, COUNT(*) AS n FROM parity_sales GROUP BY {y}, {m} ORDER BY y, m");
        r.RowsShouldBe(
            ["y", "m", "n"],
            YearMonthGroups.Select(g => new object?[] { g.Year, g.Month, g.Count }).ToArray(),
            ordered: true);
    }, variant);

    /// <summary>Hand-computed (year, month, rows) groups of <c>sale_date</c> in ascending order (v2 B7).</summary>
    private static readonly (int Year, int Month, int Count)[] YearMonthGroups =
    [
        (2023, 3, 1), (2023, 6, 1), (2024, 1, 2), (2024, 2, 1), (2024, 3, 1), (2024, 5, 1), (2024, 7, 2),
        (2024, 12, 1), (2025, 1, 1), (2025, 2, 1), (2025, 3, 3), (2025, 4, 2), (2025, 5, 1),
    ];

    [ParityTheory]
    [InlineData("YEAR")]
    [InlineData("MONTH")]
    [InlineData("DAY")]
    public Task Robin_Proactive_GroupByDatePartGraphQl(string bucket) => Run("GraphQl", "group_by_date_part", async () =>
    {
        var r = await GqlAggregate(
            $"timeBuckets: [{{ column: sale_date, bucket: {bucket} }}], aggregates: [{{ fn: COUNT, alias: \"n\" }}], orderBy: [{{ column: sale_date }}]",
            "sale_date");
        object?[][] expected = bucket switch
        {
            "YEAR" => [["2023", 2], ["2024", 8], ["2025", 8]],
            "MONTH" => YearMonthGroups.Select(g => new object?[] { $"{g.Year}-{g.Month:D2}-01", g.Count }).ToArray(),
            _ => DistinctSaleDayGroups.Select(g => new object?[] { g.Day, g.Count }).ToArray(),
        };
        r.RowsShouldBe(["sale_date", "n"], expected, ordered: true);
    }, bucket);

    /// <summary>Hand-computed distinct <c>sale_date</c> values with their row counts in ascending order (v2 B7); 2024-01-10 and 2024-07-20 hold two rows.</summary>
    private static readonly (string Day, int Count)[] DistinctSaleDayGroups =
    [
        ("2023-03-15", 1), ("2023-06-01", 1), ("2024-01-10", 2), ("2024-02-29", 1), ("2024-03-31", 1), ("2024-05-05", 1), ("2024-07-20", 2),
        ("2024-12-31", 1), ("2025-01-15", 1), ("2025-02-28", 1), ("2025-03-01", 1), ("2025-03-02", 1), ("2025-03-03", 1), ("2025-04-01", 1),
        ("2025-04-02", 1), ("2025-05-05", 1),
    ];

    // v2-01 (SEC-R100-15): fragments and aliases on a masked column keep the masking; the clear amounts never appear in the body.
    [ParityTheory]
    [InlineData("named_fragment")]
    [InlineData("inline_fragment")]
    [InlineData("alias")]
    public Task Robin_27_FragmentsKeepMasking(string variant) => Run("GraphQl", "masked_fragments", async () =>
    {
        const string masked = ParitySeed.MaskedTable;
        var field = "default_main_" + masked;
        var typeProbe = await GqlList(AllRows, "__typename id", masked);
        var typeName = typeProbe.Ok().Rows[0]["__typename"] as string ?? throw new XunitException("__typename missing in the type probe.");
        var (document, amountKey) = variant switch
        {
            "named_fragment" => ("{ " + field + "(" + AllRows + ") { ...F } } fragment F on " + typeName + " { id amount }", "amount"),
            "inline_fragment" => ("{ " + field + "(" + AllRows + ") { ... on " + typeName + " { id amount } } }", "amount"),
            _ => ("{ " + field + "(" + AllRows + ") { id a: amount } }", "a"),
        };
        var r = await _robin.GraphQlAsync(document, field);
        RobinMaskedLeakScan.AssertNoClearAmount(r.RawBody, [amountKey]);
        r.Ok();
        r.Rows.Count.ShouldBe(ParityExpected.TotalRows, RobinAssert.Snippet(r.RawBody));
        r.RowsShouldBe(
            ["id", amountKey],
            Enumerable.Range(1, ParityExpected.TotalRows).Select(i => new object?[] { i, null }).ToArray(),
            ordered: true);
    }, variant);
    [ParityFact]
    public Task Robin_Proactive_GroupByExpressionWebSql() => Run("WebSql", "group_by_expression", async () =>
    {
        var r = await Sql("SELECT LOWER(product) AS p, COUNT(*) AS n FROM parity_sales GROUP BY LOWER(product) ORDER BY p");
        r.RowsShouldBe(["p", "n"], ParityExpected.RowsByProduct.Select(p => new object?[] { p.Key.ToLowerInvariant(), p.Value }).ToArray());
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 7. Empty where / empty filter object on every channel [proactive, user report]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("GraphQl", "list")]
    [InlineData("GraphQl", "aggregate")]
    [InlineData("Rest", "list")]
    [InlineData("WebSql", "list")]
    public Task Robin_Proactive_EmptyFilter(string channel, string variant) => Run(channel, "empty_filter", async () =>
    {
        var ids = Enumerable.Range(1, ParityExpected.TotalRows).ToArray();
        switch (channel, variant)
        {
            case ("GraphQl", "list"):
                (await GqlList("where: {}, " + AllRows)).IdsShouldBe(ids);
                break;
            case ("GraphQl", _):
                (await GqlAggregate("where: {}, aggregates: [{ fn: COUNT, alias: \"n\" }]")).RowsShouldBe(["n"], [[ParityExpected.TotalRows]]);
                break;
            case ("Rest", _):
                (await _robin.RestAsync(RobinClient.SalesRest + "?select=id&filter=&orderBy=id&limit=100")).IdsShouldBe(ids);
                break;
            default:
                (await Sql("SELECT id FROM parity_sales WHERE 1 = 1 ORDER BY id")).IdsShouldBe(ids);
                break;
        }
    }, variant);

    // ---------------------------------------------------------------------------------------------------------------------
    // 8-9. IN (@list) with 0, 1, 3 and 1000 elements [#128]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql", 1)]
    [InlineData("WebSql", 3)]
    [InlineData("WebSql", 1000)]
    [InlineData("GraphQl", 1)]
    [InlineData("GraphQl", 3)]
    [InlineData("GraphQl", 1000)]
    public Task Robin_128_InListParameter(string channel, int size) => Run(channel, "in_list_parameter", async () =>
    {
        var list = Enumerable.Range(1, size).ToArray();
        var expected = list.Where(i => i <= ParityExpected.TotalRows).ToArray();
        var r = channel == "WebSql"
            ? await Sql("SELECT id FROM parity_sales WHERE id IN (@ids) ORDER BY id", new { ids = list })
            : await _robin.GraphQlAsync(
                "query($ids: [Int!]) { default_main_parity_sales(where: { id: { in: $ids } }, " + AllRows + ") { id } }",
                "default_main_parity_sales",
                new { ids = list });
        r.IdsShouldBe(expected);
    }, size.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_128_EmptyInListParameter(string channel) => Run(channel, "in_list_empty", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT id FROM parity_sales WHERE id IN (@ids) ORDER BY id", new { ids = Array.Empty<int>() })
            : await _robin.GraphQlAsync(
                "query($ids: [Int!]) { default_main_parity_sales(where: { id: { in: $ids } }, " + AllRows + ") { id } }",
                "default_main_parity_sales",
                new { ids = Array.Empty<int>() });
        // An empty list matches nothing: 200 with zero rows on every channel.
        r.IdsShouldBe();
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 10. Time zone conversion, then group by the local calendar day [#130]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_130_TimeZoneConversionGroupByLocalDay(string channel) => Run(channel, "time_zone_group_by_day", async () =>
    {
        // Row 10 is 2024-12-31T23:30:00Z (Berlin 2025-01-01 00:30); row 6 is 2024-03-31T00:30:00Z (Berlin 01:30, same day).
        if (channel == "WebSql")
        {
            // convert_timezone is not advertised on SQLite (PARITY-GAP-019 resolved by removal): a documented fail-closed rejection.
            (await Sql("SELECT CAST(convert_timezone('UTC', 'Europe/Berlin', CAST(sold_at AS timestamp)) AS date) AS d FROM parity_sales WHERE id IN (6, 10)"))
                .Rejected("SQL_POLICY_VIOLATION");
            return;
        }

        var r = await GqlAggregate(
            "where: { id: { in: [6, 10] } }, timeBuckets: [{ column: sold_at, bucket: DAY, timeZone: \"Europe/Berlin\" }], aggregates: [{ fn: COUNT, alias: \"n\" }], orderBy: [{ column: sold_at }]",
            "sold_at");
        r.RowsShouldBe(["sold_at", "n"], [["2024-03-31", 1], ["2025-01-01", 1]], ordered: true);
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 11-12. Federated queries with bound parameters, aggregation and group by [#131]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityFact]
    public Task Robin_131_FederatedQueryWithBoundParameter() => Run("WebSql", "federated_parameter", async () =>
    {
        // Source "second" holds rows 1..4 (all North).
        var r = await Sql(
            "SELECT COUNT(*) AS n FROM default.main.parity_sales a JOIN second.main.parity_sales b ON a.id = b.id WHERE a.region = @region",
            new { region = "North" });
        r.RowsShouldBe(["n"], [[4]]);
    });

    [ParityFact]
    public Task Robin_131_FederatedAggregationGroupBy() => Run("WebSql", "federated_group_by", async () =>
    {
        var r = await Sql(
            "SELECT a.product AS product, COUNT(*) AS n, SUM(b.amount) AS total FROM default.main.parity_sales a JOIN second.main.parity_sales b ON a.id = b.id " +
            "GROUP BY a.product ORDER BY a.product");
        r.RowsShouldBe(["product", "n", "total"], [["Gadget", 2, 400.00m], ["Widget", 2, 150.50m]], ordered: true);
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 13, 28. Paging 1000 rows with a truncation signal; page size above the cap [#39]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_39_PagingThousandRows(string channel) => Run(channel, "paging_1000_rows", async () =>
    {
        var seen = new List<int>();
        for (var page = 0; page < 10; page++)
        {
            var r = channel == "GraphQl"
                ? await GqlList($"first: 100, offset: {page * 100}, orderBy: [{{ id: ASC }}]", "id", ParitySeed.BigTable)
                : await _robin.RestAsync($"/api/v1/data/default/main/{ParitySeed.BigTable}?select=id&orderBy=id&limit=100&offset={page * 100}");
            r.Ok();
            seen.AddRange(r.Rows.Select(row => Convert.ToInt32(row["id"], System.Globalization.CultureInfo.InvariantCulture)));
        }

        seen.ShouldBe(Enumerable.Range(1, ParitySeed.BigRowCount).ToList());
    });

    [ParityTheory]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    [InlineData("WebSql")]
    public Task Robin_39_PageAboveCapIsRejectedOrSignalled(string channel) => Run(channel, "page_size_above_cap", async () =>
    {
        const int requested = 100000;
        var all = Enumerable.Range(1, ParitySeed.BigRowCount).ToArray();
        switch (channel)
        {
            case "GraphQl":
                // A page size above MaxAllowedLimit is a stable INVALID_QUERY (never a silent cut).
                (await GqlList($"first: {requested}, orderBy: [{{ id: ASC }}]", "id", ParitySeed.BigTable)).Rejected("INVALID_QUERY");
                break;
            case "Rest":
                var rest = await _robin.RestAsync($"/api/v1/data/default/main/{ParitySeed.BigTable}?select=id&orderBy=id&limit={requested}");
                rest.IdsShouldBeInOrder(all);
                RobinClient.IsTruncatedSignal(rest.RawBody).ShouldBeFalse(RobinAssert.Snippet(rest.RawBody));
                break;
            default:
                // WebSQL MaxRows (5000) is above the 1000 seeded rows: every row, not truncated.
                var sql = await Sql($"SELECT id FROM parity_big ORDER BY id LIMIT {requested}");
                sql.IdsShouldBeInOrder(all);
                using (var doc = System.Text.Json.JsonDocument.Parse(sql.RawBody))
                {
                    doc.RootElement.TryGetProperty("truncated", out var truncated).ShouldBeTrue("truncated flag missing");
                    truncated.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.False);
                }

                break;
        }
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 14. Dashboard: revenue per region and month, order by month, limit [#112]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql", "strftime")]
    [InlineData("WebSql", "extract")]
    [InlineData("GraphQl", "timebucket")]
    public Task Robin_112_DashboardRevenuePerRegionAndMonth(string channel, string variant) => Run(channel, "dashboard_region_month", async () =>
    {
        object?[][] expected =
        [
            ["North", 2023, 3, 100.00m],
            ["North", 2023, 6, 50.50m],
            ["North", 2024, 1, 400.00m],
            ["North", 2024, 2, 30.00m],
            ["North", 2024, 3, 15.25m],
        ];
        if (channel == "WebSql")
        {
            var (y, m) = variant == "strftime"
                ? ("strftime('%Y', sale_date)", "strftime('%m', sale_date)")
                : ("EXTRACT(YEAR FROM sale_date)", "EXTRACT(MONTH FROM sale_date)");
            var sql = await Sql(
                $"SELECT region, {y} AS y, {m} AS m, SUM(amount) AS revenue FROM parity_sales WHERE region IS NOT NULL GROUP BY region, {y}, {m} ORDER BY y, m, region LIMIT 5");
            sql.RowsShouldBe(["region", "y", "m", "revenue"], expected, ordered: true);
            return;
        }

        var r = await GqlAggregate(
            "where: { region: { eq: \"North\" } }, groupBy: [region], timeBuckets: [{ column: sale_date, bucket: MONTH }], aggregates: [{ fn: SUM, column: amount, alias: \"revenue\" }], " +
            "orderBy: [{ column: sale_date }], limit: 5",
            "region sale_date");
        r.RowsShouldBe(
            ["region", "sale_date", "revenue"],
            expected.Select(e => new object?[] { e[0], $"{e[1]}-{(int)e[2]!:D2}", e[3] }).ToArray(),
            ordered: true);
    }, variant);

    // ---------------------------------------------------------------------------------------------------------------------
    // 15-16. Required filter column missing / satisfied on every read path [#163, #236]
    // ---------------------------------------------------------------------------------------------------------------------

    private Task<ChannelResult> ScopedRead(string channel, string variant, bool filtered)
    {
        const string scoped = ParitySeed.ScopedTable;
        return (channel, variant) switch
        {
            ("WebSql", "federated") => Sql(
                "SELECT s.id AS id FROM default.main.parity_scoped s JOIN second.main.parity_sales b ON s.id = b.id" + (filtered ? " WHERE s.region = 'North'" : string.Empty) + " ORDER BY s.id"),
            ("WebSql", _) => Sql("SELECT id FROM parity_scoped" + (filtered ? " WHERE region = 'North'" : string.Empty) + " ORDER BY id"),
            ("GraphQl", "aggregate") => GqlAggregate((filtered ? "where: { region: { eq: \"North\" } }, " : string.Empty) + "aggregates: [{ fn: COUNT, alias: \"n\" }]", table: scoped),
            ("GraphQl", _) => GqlList((filtered ? "where: { region: { eq: \"North\" } }, " : string.Empty) + AllRows, "id", scoped),
            ("Rest", _) => _robin.RestAsync($"/api/v1/data/default/main/{scoped}?select=id&orderBy=id&limit=100" + (filtered ? "&filter=" + Uri.EscapeDataString("region eq 'North'") : string.Empty)),
            _ => _robin.PostAsync($"/api/v1/export/arrow?table=default.main.{scoped}"),
        };
    }

    [ParityTheory]
    [InlineData("WebSql", "sql")]
    [InlineData("WebSql", "federated")]
    [InlineData("GraphQl", "list")]
    [InlineData("GraphQl", "aggregate")]
    [InlineData("Rest", "list")]
    [InlineData("Export", "arrow")]
    public Task Robin_163_RequiredFilterMissingGivesStableCode(string channel, string variant) => Run(channel, "required_filter_missing", async () =>
    {
        var r = await ScopedRead(channel, variant, filtered: false);
        r.Rejected("REQUIRED_FILTER_COLUMN_MISSING");
    }, variant);

    [ParityTheory]
    [InlineData("WebSql", "sql")]
    [InlineData("WebSql", "federated")]
    [InlineData("GraphQl", "list")]
    [InlineData("GraphQl", "aggregate")]
    [InlineData("Rest", "list")]
    public Task Robin_163_RequiredFilterSatisfied(string channel, string variant) => Run(channel, "required_filter_satisfied", async () =>
    {
        var r = await ScopedRead(channel, variant, filtered: true);
        switch (channel, variant)
        {
            case ("WebSql", "federated"):
                r.IdsShouldBe(1, 2, 3, 4);
                break;
            case ("GraphQl", "aggregate"):
                r.RowsShouldBe(["n"], [[7]]);
                break;
            default:
                r.IdsShouldBe(1, 2, 3, 4, 5, 6, 18);
                break;
        }
    }, variant);

    // ---------------------------------------------------------------------------------------------------------------------
    // v2-02, v2-03. Unregistered physical tables and a masked column reached through a join [proactive, SEC-R100-13, SEC-R100-16]
    // ---------------------------------------------------------------------------------------------------------------------

    private static string BodyShape(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                ? string.Join(",", doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
                : doc.RootElement.ValueKind.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return "<not-json>";
        }
    }

    private static ChannelResult Denied(ChannelResult r, string what, string code)
    {
        if (!ParityAssert.IsError(r))
        {
            throw new DeniedRequestSucceededException($"{what}: a request that must be rejected was answered with HTTP {r.Status}.");
        }

        return r.Rejected(code);
    }

    [ParityTheory]
    [InlineData("WebSql", "sqlite_master")]
    [InlineData("WebSql", "pragma_table_function")]
    [InlineData("WebSql", "pragma_bare_call")]
    [InlineData("Rest", "sqlite_master")]
    public Task Robin_30_UnregisteredSourceTableDenied(string channel, string variant) => Run(channel, "unregistered_source_table", async () =>
    {
        switch (channel, variant)
        {
            case ("WebSql", "sqlite_master"):
                var probe = Denied(await Sql("SELECT name FROM sqlite_master"), "sqlite_master", "SQL_POLICY_DENIED");
                var unknown = Denied(await Sql("SELECT id FROM parity_nope"), "unknown table", "SQL_POLICY_DENIED");
                probe.Status.ShouldBe(unknown.Status);
                BodyShape(probe.RawBody).ShouldBe(BodyShape(unknown.RawBody));
                System.Text.RegularExpressions.Regex.IsMatch(probe.RawBody, @"(?i)\b(does not exist|doesn't exist|no such|unknown table)\b").ShouldBeFalse("SEC-R100-13: the message must not state whether a table exists.");
                break;
            case ("WebSql", "pragma_table_function"):
                // The Trino table-function form is the one the AST builder refuses with a SecurityException (SqlAstBuilder.VisitTableFunctionInvocation).
                Denied(await Sql("SELECT * FROM TABLE(pragma_table_info('parity_sales'))"), "pragma_table_info", "SQL_POLICY_VIOLATION");
                break;
            case ("WebSql", _):
                // A bare table-valued call is not Trino grammar: a syntax error (400), no row either way.
                Denied(await Sql("SELECT * FROM pragma_table_info('parity_sales')"), "pragma_table_info", "SQL_SYNTAX_ERROR");
                break;
            default:
                var restProbe = Denied(await _robin.RestAsync("/api/v1/data/default/main/sqlite_master?select=name"), "sqlite_master", "RESOURCE_NOT_FOUND");
                var restUnknown = Denied(await _robin.RestAsync("/api/v1/data/default/main/parity_nope?select=name"), "unknown table", "RESOURCE_NOT_FOUND");
                restProbe.Status.ShouldBe(restUnknown.Status);
                BodyShape(restProbe.RawBody).ShouldBe(BodyShape(restUnknown.RawBody));
                break;
        }
    }, variant);

    [ParityTheory]
    [InlineData("projection")]
    [InlineData("on_masked")]
    public Task Robin_28_MaskedColumnThroughJoin(string variant) => Run("WebSql", "masked_column_join", async () =>
    {
        if (variant == "projection")
        {
            var r = await Sql("SELECT s.id, m.amount FROM parity_sales s JOIN parity_masked m ON s.id = m.id WHERE s.id IN (3, 8) ORDER BY s.id");
            RobinMaskedLeakScan.AssertNoClearAmount(r.RawBody, ["amount"]);
            r.RowsShouldBe(["id", "amount"], [[3, null], [8, null]], ordered: true);
            return;
        }

        // A join key on a masked column would let a caller probe the clear values: rejected, never a 200.
        var on = await Sql("SELECT s.id, m.id AS mid FROM parity_sales s JOIN parity_masked m ON s.amount = m.amount WHERE s.id IN (3, 8) ORDER BY s.id");
        Denied(on, "join on a masked column", "SQL_POLICY_VIOLATION");
    }, variant);

    /// <summary>SEC-R100-15: a clear masked amount anywhere in a response body is a hard failure (ids and keys only in the message, SEC-R100-10).</summary>
    internal static class RobinMaskedLeakScan
    {
        /// <summary>Fractional spellings of the clear amounts of the seed ("50.5", "50.50", "100.0"); whole numbers are covered by the key walk.</summary>
        private static readonly System.Text.RegularExpressions.Regex ClearAmountToken = BuildToken();

        private static System.Text.RegularExpressions.Regex BuildToken()
        {
            var alternatives = ParitySeed.Sales
                .Where(s => s.Amount is not null)
                .Select(s => s.Amount!.Value)
                .Distinct()
                .Select(a =>
                {
                    var text = a.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    var whole = text[..text.IndexOf('.', StringComparison.Ordinal)];
                    var fraction = text[(text.IndexOf('.', StringComparison.Ordinal) + 1)..].TrimEnd('0');
                    return fraction.Length == 0
                        ? System.Text.RegularExpressions.Regex.Escape(whole) + @"\.0+"
                        : System.Text.RegularExpressions.Regex.Escape(whole + "." + fraction) + "0*";
                });
            return new System.Text.RegularExpressions.Regex(@"(?<![\d.])(?:" + string.Join("|", alternatives) + @")(?![\d])", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }

        public static void AssertNoClearAmount(string body, string[] amountKeys)
        {
            if (ClearAmountToken.IsMatch(body))
            {
                throw new DeniedRequestSucceededException("a clear masked amount appears in the response body.");
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                Walk(doc.RootElement, amountKeys);
            }
            catch (System.Text.Json.JsonException)
            {
                // Not JSON: the token scan above is the only check.
            }
        }

        private static void Walk(System.Text.Json.JsonElement element, string[] amountKeys)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var p in element.EnumerateObject())
                    {
                        if (amountKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind != System.Text.Json.JsonValueKind.Null)
                        {
                            throw new DeniedRequestSucceededException($"masked column key '{p.Name}' carries a value.");
                        }

                        Walk(p.Value, amountKeys);
                    }

                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, amountKeys);
                    }

                    break;
            }
        }
    }
    // ---------------------------------------------------------------------------------------------------------------------
    // 19. Aggregate over a masked column: a stable 4xx, never the clear-text total [#28, proactive]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_28_AggregateOverMaskedColumn(string channel) => Run(channel, "aggregate_masked_column", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT SUM(amount) AS total FROM parity_masked")
            : await GqlAggregate("aggregates: [{ fn: SUM, column: amount, alias: \"total\" }]", table: ParitySeed.MaskedTable);
        if (!ParityAssert.IsError(r))
        {
            throw new DeniedRequestSucceededException("SUM over a masked column was answered instead of rejected.");
        }

        r.Rejected("AGGREGATION_ON_MASKED_COLUMN");
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // 20-23. Aggregate semantics [proactive]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_CountDistinctOnDateAndString(string channel) => Run(channel, "count_distinct", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT COUNT(DISTINCT sale_date) AS days, COUNT(DISTINCT product) AS products, COUNT(DISTINCT region) AS regions FROM parity_sales")
            : await GqlAggregate("aggregates: [{ fn: COUNT_DISTINCT, column: sale_date, alias: \"days\" }, { fn: COUNT_DISTINCT, column: product, alias: \"products\" }, { fn: COUNT_DISTINCT, column: region, alias: \"regions\" }]");
        r.RowsShouldBe(["days", "products", "regions"], [[ParityExpected.DistinctSaleDates, ParityExpected.DistinctProducts, ParityExpected.DistinctRegionsNonNull]]);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_SumOfNullOnlyGroupIsNull(string channel) => Run(channel, "sum_null_only_group", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT region, SUM(amount) AS total, COUNT(*) AS n FROM parity_sales WHERE region = 'Empty' GROUP BY region")
            : await GqlAggregate("where: { region: { eq: \"Empty\" } }, groupBy: [region], " + CountSum, "region");
        r.RowsShouldBe(["region", "total", "n"], [["Empty", null, 3]]);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_AvgOfIntegerColumnIsFractional(string channel) => Run(channel, "avg_integer", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT AVG(qty) AS av FROM parity_sales")
            : await GqlAggregate("aggregates: [{ fn: AVG, column: qty, alias: \"av\" }]");
        r.RowsShouldBe(["av"], [[ParityExpected.AvgQty]]);
    });

    [ParityTheory]
    [InlineData("WebSql", "alias_desc")]
    [InlineData("WebSql", "group_key_asc")]
    [InlineData("GraphQl", "alias_desc")]
    [InlineData("GraphQl", "group_key_asc")]
    public Task Robin_Proactive_OrderByAggregateAliasAndGroupKey(string channel, string variant) => Run(channel, "order_by_aggregate", async () =>
    {
        var byAlias = variant == "alias_desc";
        ChannelResult r;
        if (channel == "WebSql")
        {
            r = byAlias
                ? await Sql("SELECT region, SUM(amount) AS total FROM parity_sales WHERE amount IS NOT NULL GROUP BY region ORDER BY total DESC")
                : await Sql("SELECT product, SUM(amount) AS total FROM parity_sales GROUP BY product ORDER BY product");
        }
        else
        {
            r = byAlias
                ? await GqlAggregate("where: { amount: { gt: 0 } }, groupBy: [region], aggregates: [{ fn: SUM, column: amount, alias: \"total\" }], orderBy: [{ alias: \"total\", direction: DESC }]", "region")
                : await GqlAggregate("groupBy: [product], aggregates: [{ fn: SUM, column: amount, alias: \"total\" }], orderBy: [{ column: product }]", "product");
        }

        if (byAlias)
        {
            r.RowsShouldBe(["region", "total"], [["South", 1175.75m], ["North", 635.75m], [null, 70.00m]], ordered: true);
        }
        else
        {
            r.RowsShouldBe(["product", "total"], [["Gadget", 1260.00m], ["Gizmo", 241.00m], ["Widget", 380.50m]], ordered: true);
        }
    }, variant);

    // ---------------------------------------------------------------------------------------------------------------------
    // 24-26. Filters: date ranges, LIKE escaping, NULL parameter [proactive, #280]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql", "half_open")]
    [InlineData("WebSql", "between")]
    [InlineData("WebSql", "leap_day")]
    [InlineData("GraphQl", "half_open")]
    [InlineData("GraphQl", "between")]
    [InlineData("GraphQl", "leap_day")]
    public Task Robin_Proactive_DateRangeBoundsAndLeapDay(string channel, string variant) => Run(channel, "date_range", async () =>
    {
        var (sql, where, ids) = variant switch
        {
            "half_open" => ("sale_date >= '2024-02-29' AND sale_date < '2024-03-31'", "sale_date: { gte: \"2024-02-29\", lt: \"2024-03-31\" }", new[] { 5 }),
            "between" => ("sale_date BETWEEN '2024-02-29' AND '2024-03-31'", "sale_date: { gte: \"2024-02-29\", lte: \"2024-03-31\" }", new[] { 5, 6 }),
            _ => ("sale_date = '2024-02-29'", "sale_date: { eq: \"2024-02-29\" }", new[] { 5 }),
        };
        var r = channel == "WebSql"
            ? await Sql($"SELECT id FROM parity_sales WHERE {sql} ORDER BY id")
            : await GqlList($"where: {{ {where} }}, {AllRows}");
        r.IdsShouldBe(ids);
    }, variant);

    [ParityTheory]
    [InlineData("WebSql", "percent")]
    [InlineData("WebSql", "underscore")]
    [InlineData("GraphQl", "percent")]
    [InlineData("GraphQl", "underscore")]
    public Task Robin_Proactive_LikeWithPercentAndUnderscoreLiterals(string channel, string variant) => Run(channel, "like_escaping", async () =>
    {
        // Row 2 note "50% off" holds the only literal percent sign, row 3 "under_score" the only literal underscore.
        var percent = variant == "percent";
        var r = channel == "WebSql"
            ? await Sql(percent
                ? "SELECT id FROM parity_sales WHERE note LIKE '%!%%' ESCAPE '!' ORDER BY id"
                : "SELECT id FROM parity_sales WHERE note LIKE '%!_%' ESCAPE '!' ORDER BY id")
            : await GqlList($"where: {{ note: {{ contains: \"{(percent ? "%" : "_")}\" }} }}, {AllRows}");
        r.IdsShouldBe(percent ? 2 : 3);
    }, variant);

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_280_NullParameterComparison(string channel) => Run(channel, "null_parameter", async () =>
    {
        // "= @p" with NULL is unknown in SQL three-valued logic: a 200 with zero rows, not an error.
        var r = channel == "WebSql"
            ? await Sql("SELECT id FROM parity_sales WHERE region = @p", new { p = (string?)null })
            : await _robin.GraphQlAsync(
                "query($p: String) { default_main_parity_sales(where: { region: { eq: $p } }, " + AllRows + ") { id } }",
                "default_main_parity_sales",
                new { p = (string?)null });
        r.IdsShouldBe();
    });
}
