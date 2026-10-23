using System.Globalization;
using System.Text.RegularExpressions;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Seed;
using Shouldly;

namespace Autheris.Tests.Parity.Robin;

// WP-R1: scenarios R31..R65 (plan docs/plans/2026-10-23-plan-robin-100-addendum.md). Expected values are hand-computed from the seed literals.
public sealed partial class RobinScenarios
{
    private const string Big = ParitySeed.BigTable;

    private static string Esc(string filter) => Uri.EscapeDataString(filter);

    private Task<ChannelResult> RestFilter(string filter, string table = Sales) =>
        _robin.RestAsync($"/api/v1/data/default/main/{table}?select=id&filter={Esc(filter)}&orderBy=id&limit=100");

    /// <summary>Selects ids of the seed table through the channel with the same logical predicate rendered per channel.</summary>
    private Task<ChannelResult> IdsBy(string channel, string sqlWhere, string gqlWhere, string restFilter) => channel switch
    {
        "WebSql" => Sql($"SELECT id FROM parity_sales WHERE {sqlWhere} ORDER BY id"),
        "GraphQl" => GqlList($"where: {{ {gqlWhere} }}, {AllRows}"),
        _ => RestFilter(restFilter),
    };

    private static string Agg(string fn, string? column, string alias) =>
        "{ fn: " + fn + (column is null ? string.Empty : ", column: " + column) + ", alias: \"" + alias + "\" }";

    private static int AsInt(object? v) => Convert.ToInt32(v, CultureInfo.InvariantCulture);

    private static int HourOf(object? key)
    {
        var text = key?.ToString() ?? string.Empty;
        var m = Regex.Match(text, @"[T ](\d{2}):");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : int.Parse(text, CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, int> CountsBy(ChannelResult r, string keyColumn, Func<object?, string> norm)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in r.Ok().Rows)
        {
            var k = norm(row[keyColumn]);
            map[k] = map.GetValueOrDefault(k) + AsInt(row["n"]);
        }

        return map;
    }

    // R31
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_28_MinMaxAmount(string channel) => Run(channel, "min_max", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT MIN(amount) AS lo, MAX(amount) AS hi FROM parity_sales")
            : await GqlAggregate($"aggregates: [{Agg("MIN", "amount", "lo")}, {Agg("MAX", "amount", "hi")}]");
        r.RowsShouldBe(["lo", "hi"], [[10.00m, 400.00m]]);
    });

    // R32
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_28_CountStarVsCountColumn(string channel) => Run(channel, "count_star_vs_column", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT COUNT(*) AS n, COUNT(amount) AS na, COUNT(note) AS nn FROM parity_sales")
            : await GqlAggregate($"aggregates: [{Agg("COUNT", null, "n")}, {Agg("COUNT", "amount", "na")}, {Agg("COUNT", "note", "nn")}]");
        r.RowsShouldBe(["n", "na", "nn"], [[18, 15, 7]]);
    });

    // R33
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_112_SumByProduct(string channel) => Run(channel, "sum_by_product", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT product, SUM(amount) AS total FROM parity_sales GROUP BY product")
            : await GqlAggregate($"groupBy: [product], aggregates: [{Agg("SUM", "amount", "total")}]", "product");
        r.RowsShouldBe(["product", "total"], [["Widget", 380.50m], ["Gadget", 1260.00m], ["Gizmo", 241.00m]]);
    });

    // R34
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_112_CountByProductOrdered(string channel) => Run(channel, "count_by_product_ordered", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT product, COUNT(*) AS n FROM parity_sales GROUP BY product ORDER BY n DESC")
            : await GqlAggregate($"groupBy: [product], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ alias: \"n\", direction: DESC }}]", "product");
        r.RowsShouldBe(["product", "n"], [["Widget", 8], ["Gadget", 6], ["Gizmo", 4]], ordered: true);
    });

    // R35
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_112_SumByRegionNullGroup(string channel) => Run(channel, "null_region_group", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT COUNT(*) AS n, SUM(amount) AS total FROM parity_sales WHERE region IS NULL")
            : await GqlAggregate($"where: {{ region: {{ isNull: true }} }}, {CountSum}");
        r.RowsShouldBe(["n", "total"], [[2, 70.00m]]);
    });

    // R36
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_112_SumByRegionAndProduct(string channel) => Run(channel, "group_by_two_columns", async () =>
    {
        object?[][] expected =
        [
            ["North", "Widget", 4, 220.50m], ["North", "Gadget", 2, 400.00m], ["North", "Gizmo", 1, 15.25m],
            ["South", "Widget", 2, 150.00m], ["South", "Gadget", 2, 800.00m], ["South", "Gizmo", 2, 225.75m],
            ["Empty", "Widget", 1, null], ["Empty", "Gadget", 1, null], ["Empty", "Gizmo", 1, null],
            [null, "Widget", 1, 10.00m], [null, "Gadget", 1, 60.00m],
        ];
        var r = channel == "WebSql"
            ? await Sql("SELECT region, product, COUNT(*) AS n, SUM(amount) AS total FROM parity_sales GROUP BY region, product")
            : await GqlAggregate($"groupBy: [region, product], {CountSum}", "region product");
        r.RowsShouldBe(["region", "product", "n", "total"], expected);
        r.Rows.Count.ShouldBe(11, r.RawBody);
    });

    // R37
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_280_AggregationWhereIsReturn(string channel) => Run(channel, "aggregation_where_boolean", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT COUNT(*) AS n, SUM(amount) AS total FROM parity_sales WHERE is_return = 1")
            : await GqlAggregate($"where: {{ is_return: {{ eq: true }} }}, {CountSum}");
        r.RowsShouldBe(["n", "total"], [[3, 490.00m]]);
    });

    // R38
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_280_AggregationWhereBetweenDates(string channel) => Run(channel, "aggregation_where_dates", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT COUNT(*) AS n, SUM(amount) AS total FROM parity_sales WHERE sale_date >= '2024-01-01' AND sale_date < '2025-01-01'")
            : await GqlAggregate($"where: {{ sale_date: {{ gte: \"2024-01-01\", lt: \"2025-01-01\" }} }}, {CountSum}");
        r.RowsShouldBe(["n", "total"], [[8, 1405.25m]]);
    });

    // R39
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_SumQtyByProduct(string channel) => Run(channel, "sum_qty_by_product", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT product, SUM(qty) AS q FROM parity_sales GROUP BY product")
            : await GqlAggregate($"groupBy: [product], aggregates: [{Agg("SUM", "qty", "q")}]", "product");
        r.RowsShouldBe(["product", "q"], [["Widget", 38], ["Gadget", 15], ["Gizmo", 16]]);
    });

    // R40
    [ParityFact]
    public Task Robin_Proactive_HavingCountGreaterThan() => Run("WebSql", "having", async () =>
    {
        var r = await Sql("SELECT region, COUNT(*) AS n FROM parity_sales GROUP BY region HAVING COUNT(*) > 5");
        r.RowsShouldBe(["region", "n"], [["North", 7], ["South", 6]]);
    });

    // R41
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_GroupByQuarter(string channel) => Run(channel, "group_by_quarter", async () =>
    {
        if (channel == "WebSql")
        {
            var w = await Sql(
                "SELECT strftime('%Y', sale_date) AS y, (CAST(strftime('%m', sale_date) AS INTEGER) + 2) / 3 AS q, COUNT(*) AS n FROM parity_sales GROUP BY y, q ORDER BY y, q");
            w.RowsShouldBe(
                ["y", "q", "n"],
                [[2023, 1, 1], [2023, 2, 1], [2024, 1, 4], [2024, 2, 1], [2024, 3, 2], [2024, 4, 1], [2025, 1, 5], [2025, 2, 3]],
                ordered: true);
            return;
        }

        var g = await GqlAggregate(
            $"timeBuckets: [{{ column: sale_date, bucket: QUARTER }}], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ column: sale_date }}]",
            "sale_date");
        g.RowsShouldBe(
            ["sale_date", "n"],
            [["2023-01-01", 1], ["2023-04-01", 1], ["2024-01-01", 4], ["2024-04-01", 1], ["2024-07-01", 2], ["2024-10-01", 1], ["2025-01-01", 5], ["2025-04-01", 3]],
            ordered: true);
    });

    // R42
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_GroupByDay(string channel) => Run(channel, "group_by_day", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT sale_date AS d, COUNT(*) AS n FROM parity_sales GROUP BY sale_date")
            : await GqlAggregate($"timeBuckets: [{{ column: sale_date, bucket: DAY }}], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ column: sale_date }}]", "sale_date");
        var map = CountsBy(r, channel == "WebSql" ? "d" : "sale_date", v => RobinAssert.Day(v));
        map.Count.ShouldBe(16, r.RawBody);
        map.Values.Sum().ShouldBe(18, r.RawBody);
        map["2024-01-10"].ShouldBe(2);
        map["2024-07-20"].ShouldBe(2);
        map.Where(kv => kv.Key is not ("2024-01-10" or "2024-07-20")).ShouldAllBe(kv => kv.Value == 1);
    });

    // R43
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_GroupByHour(string channel) => Run(channel, "group_by_hour", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT strftime('%H', sold_at) AS h, COUNT(*) AS n FROM parity_sales GROUP BY h")
            : await GqlAggregate($"timeBuckets: [{{ column: sold_at, bucket: HOUR, timeZone: \"UTC\" }}], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ column: sold_at }}]", "sold_at");
        var map = CountsBy(r, channel == "WebSql" ? "h" : "sold_at", v => HourOf(v).ToString(CultureInfo.InvariantCulture));
        var expected = new Dictionary<string, int>
        {
            ["0"] = 4, ["5"] = 1, ["6"] = 2, ["7"] = 1, ["8"] = 1, ["9"] = 1, ["10"] = 1, ["11"] = 1, ["12"] = 2, ["15"] = 2, ["23"] = 2,
        };
        map.OrderBy(kv => kv.Key, StringComparer.Ordinal).ShouldBe(expected.OrderBy(kv => kv.Key, StringComparer.Ordinal), r.RawBody);
    });

    // R44
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_130_HourBucketBerlinWinterSummer(string channel) => Run(channel, "hour_bucket_time_zone", async () =>
    {
        // #1 is 2023-03-15T10:00Z (CET, +1 -> 11); #8 is 2024-07-20T15:45Z (CEST, +2 -> 17). The UTC strftime hours are 10 and 15.
        if (channel == "WebSql")
        {
            var w = await Sql("SELECT id, strftime('%H', sold_at) AS h FROM parity_sales WHERE id IN (1, 8) ORDER BY id");
            w.RowsShouldBe(["id", "h"], [[1, 10], [8, 15]], ordered: true);
            return;
        }

        var g = await GqlAggregate(
            $"where: {{ id: {{ in: [1, 8] }} }}, timeBuckets: [{{ column: sold_at, bucket: HOUR, timeZone: \"Europe/Berlin\" }}], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ column: sold_at }}]",
            "sold_at");
        g.Ok().Rows.Select(x => HourOf(x["sold_at"])).ToArray().ShouldBe([11, 17], g.RawBody);
    });

    // R45
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_130_LocalDayBoundaryNorth(string channel) => Run(channel, "day_bucket_utc", async () =>
    {
        // In UTC the days are 2024-03-31 (#6) and 2024-12-31 (#10); Berlin would give 2025-01-01 for #10 (see Robin_130_TimeZoneConversionGroupByLocalDay).
        if (channel == "WebSql")
        {
            var w = await Sql("SELECT strftime('%Y-%m-%d', sold_at) AS d FROM parity_sales WHERE id IN (6, 10) ORDER BY id");
            w.RowsShouldBe(["d"], [["2024-03-31"], ["2024-12-31"]], ordered: true);
            return;
        }

        var g = await GqlAggregate(
            $"where: {{ id: {{ in: [6, 10] }} }}, timeBuckets: [{{ column: sold_at, bucket: DAY, timeZone: \"UTC\" }}], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ column: sold_at }}]",
            "sold_at");
        g.RowsShouldBe(["sold_at", "n"], [["2024-03-31", 1], ["2024-12-31", 1]], ordered: true);
    });

    // R46
    [ParityFact]
    public Task Robin_130_UnknownTimeZoneRejected() => Run("GraphQl", "unknown_time_zone", async () =>
    {
        var r = await GqlAggregate(
            $"timeBuckets: [{{ column: sold_at, bucket: DAY, timeZone: \"Mars/Olympus\" }}], aggregates: [{Agg("COUNT", null, "n")}]",
            "sold_at");
        r.Rejected();
    });

    // R47
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_36_TimestampFilterIso(string channel) => Run(channel, "timestamp_filter", async () =>
        (await IdsBy(channel, "sold_at >= '2025-01-01T00:00:00Z'", "sold_at: { gte: \"2025-01-01T00:00:00Z\" }", "sold_at ge 2025-01-01T00:00:00Z"))
            .IdsShouldBe(11, 12, 13, 14, 15, 16, 17, 18));

    // R48
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_Proactive_DateEqualsSingleDay(string channel) => Run(channel, "date_equals", async () =>
        (await IdsBy(channel, "sale_date = '2024-02-29'", "sale_date: { eq: \"2024-02-29\" }", "sale_date eq 2024-02-29")).IdsShouldBe(5));

    // R49
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_Proactive_IsNullAmount(string channel) => Run(channel, "is_null", async () =>
        (await IdsBy(channel, "amount IS NULL", "amount: { isNull: true }", "amount eq null")).IdsShouldBe(13, 14, 15));

    // R50
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_Proactive_IsNotNullRegion(string channel) => Run(channel, "is_not_null", async () =>
        (await IdsBy(channel, "region IS NOT NULL", "region: { isNull: false }", "region ne null")).IdsShouldBe(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 18));

    // R51
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_280_NeComparisonDropsNullRows(string channel) => Run(channel, "ne_drops_null", async () =>
        (await IdsBy(channel, "region <> 'North'", "region: { neq: \"North\" }", string.Empty)).IdsShouldBe(7, 8, 9, 10, 11, 12, 13, 14, 15));

    // R52
    [ParityFact]
    public Task Robin_Proactive_CoalesceAmountZero() => Run("WebSql", "coalesce", async () =>
    {
        var rows = await Sql("SELECT id, COALESCE(amount, 0) AS a FROM parity_sales WHERE id IN (12, 13, 14, 15) ORDER BY id");
        rows.RowsShouldBe(["id", "a"], [[12, 135.75m], [13, 0], [14, 0], [15, 0]], ordered: true);
        var sum = await Sql("SELECT SUM(COALESCE(amount, 0)) AS total FROM parity_sales");
        sum.RowsShouldBe(["total"], [[1881.50m]]);
    });

    // R53
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_NotInWithNullRows(string channel) => Run(channel, "not_in_null_rows", async () =>
        (await IdsBy(channel, "region NOT IN ('North')", "region: { nin: [\"North\"] }", string.Empty)).IdsShouldBe(7, 8, 9, 10, 11, 12, 13, 14, 15));

    // R54
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_AvgIgnoresNull(string channel) => Run(channel, "avg_ignores_null", async () =>
    {
        var r = channel == "WebSql"
            ? await Sql("SELECT AVG(amount) AS av FROM parity_sales")
            : await GqlAggregate($"aggregates: [{Agg("AVG", "amount", "av")}]");
        r.RowsShouldBe(["av"], [[ParityExpected.AvgAmount]]);
    });

    // R55
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_Proactive_NullBoolIsReturn(string channel) => Run(channel, "null_boolean", async () =>
        (await IdsBy(channel, "is_return IS NULL", "is_return: { isNull: true }", "is_return eq null")).IdsShouldBe(12, 13, 14, 15));

    // R56
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_128_InListStrings(string channel) => Run(channel, "in_list_strings", async () =>
        (await IdsBy(channel, "product IN ('Widget', 'Gizmo')", "product: { in: [\"Widget\", \"Gizmo\"] }", "product in ('Widget','Gizmo')"))
            .IdsShouldBe(1, 2, 5, 6, 7, 10, 11, 12, 13, 15, 16, 18));

    // R57
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_128_InListIntegers(string channel) => Run(channel, "in_list_integers", async () =>
        (await IdsBy(channel, "id IN (1, 5, 18, 99)", "id: { in: [1, 5, 18, 99] }", "id in (1,5,18,99)")).IdsShouldBe(1, 5, 18));

    // R58
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_128_InListDuplicatesCollapse(string channel) => Run(channel, "in_list_duplicates", async () =>
        (await IdsBy(channel, "id IN (1, 1, 1)", "id: { in: [1, 1, 1] }", string.Empty)).IdsShouldBe(1));

    // R59
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_128_NotInListParameter(string channel) => Run(channel, "not_in_parameter", async () =>
    {
        var p = new[] { "Widget", "Gizmo" };
        var r = channel == "WebSql"
            ? await Sql("SELECT id FROM parity_sales WHERE product NOT IN (@p) ORDER BY id", new { p })
            : await _robin.GraphQlAsync(
                "query($p: [String!]) { default_main_parity_sales(where: { product: { nin: $p } }, " + AllRows + ") { id } }",
                "default_main_parity_sales",
                new { p });
        r.IdsShouldBe(3, 4, 8, 9, 14, 17);
    });

    // R60
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_128_InListWithApostropheValue(string channel) => Run(channel, "in_list_apostrophe", async () =>
    {
        var n = new[] { "it's" };
        var r = channel == "WebSql"
            ? await Sql("SELECT id FROM parity_sales WHERE note IN (@n) ORDER BY id", new { n })
            : await _robin.GraphQlAsync(
                "query($n: [String!]) { default_main_parity_sales(where: { note: { in: $n } }, " + AllRows + ") { id } }",
                "default_main_parity_sales",
                new { n });
        r.IdsShouldBe(4);
    });

    // R61
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_Proactive_LikePrefix(string channel) => Run(channel, "like_prefix", async () =>
        (await IdsBy(channel, "note LIKE 'emoji%'", "note: { startsWith: \"emoji\" }", "startswith(note,'emoji')")).IdsShouldBe(8));

    // R62
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_LikeCaseSensitivity(string channel) => Run(channel, "like_case", async () =>
        // SQLite LIKE is ASCII case-insensitive, so 'PLAIN' matches note "plain" (#1); GraphQL startsWith must agree with WebSQL.
        (await IdsBy(channel, "note LIKE 'PLAIN'", "note: { startsWith: \"PLAIN\" }", string.Empty)).IdsShouldBe(1));

    // R63
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_ContainsBackslash(string channel) => Run(channel, "contains_backslash", async () =>
    {
        // A literal backslash in WebSQL text is rejected (SQL_LITERAL_NOT_PERMITTED), so the value is bound as a parameter.
        var r = channel == "WebSql"
            ? await Sql("SELECT id FROM parity_sales WHERE instr(note, @b) > 0 ORDER BY id", new { b = "\\" })
            : await GqlList("where: { note: { contains: \"\\\\\" } }, " + AllRows);
        r.IdsShouldBe(5);
    });

    // R64
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_Proactive_CombinedAndOrNesting(string channel) => Run(channel, "and_or_nesting", async () =>
        (await IdsBy(
            channel,
            "(region = 'North' AND qty >= 5) OR product = 'Gizmo'",
            "or: [{ and: [{ region: { eq: \"North\" } }, { qty: { gte: 5 } }] }, { product: { eq: \"Gizmo\" } }]",
            "(region eq 'North' and qty ge 5) or product eq 'Gizmo'")).IdsShouldBe(1, 2, 6, 10, 12, 15));

    // R65
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_39_FirstPageOfBig(string channel) => Run(channel, "first_page_of_big", async () =>
    {
        var r = channel switch
        {
            "WebSql" => await Sql("SELECT id FROM parity_big ORDER BY id OFFSET 0 LIMIT 100"),
            "GraphQl" => await GqlList("first: 100, offset: 0, orderBy: [{ id: ASC }]", "id", Big),
            _ => await _robin.RestAsync($"/api/v1/data/default/main/{Big}?select=id&orderBy=id&limit=100&offset=0"),
        };
        r.IdsShouldBeInOrder(Enumerable.Range(1, 100).ToArray());
    });
}
