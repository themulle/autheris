using System.Globalization;
using System.Text.RegularExpressions;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Autheris.Tests.Parity.Seed;
using Shouldly;
using Xunit.Sdk;

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
    [InlineData("GraphQl")]
    public Task Robin_130_HourBucketBerlinWinterSummer(string channel) => Run(channel, "hour_bucket_time_zone", async () =>
    {
        // #1 is 2023-03-15T10:00Z (CET, +1 -> 11); #8 is 2024-07-20T15:45Z (CEST, +2 -> 17). WebSQL time zones on SQLite: Robin_130_TimeZoneConversionGroupByLocalDay.
        var g = await GqlAggregate(
            $"where: {{ id: {{ in: [1, 8] }} }}, timeBuckets: [{{ column: sold_at, bucket: HOUR, timeZone: \"Europe/Berlin\" }}], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ column: sold_at }}]",
            "sold_at");
        g.Ok().Rows.Select(x => HourOf(x["sold_at"])).ToArray().ShouldBe([11, 17], g.RawBody);
    });

    // R45
    [ParityTheory]
    [InlineData("GraphQl")]
    public Task Robin_130_LocalDayBoundaryNorth(string channel) => Run(channel, "day_bucket_utc", async () =>
    {
        // In UTC the days are 2024-03-31 (#6) and 2024-12-31 (#10); Berlin would give 2025-01-01 for #10 (see Robin_130_TimeZoneConversionGroupByLocalDay).
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
        Denied(r, "unknown time zone", "INVALID_QUERY");
    });

    // R47
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_36_TimestampFilterIso(string channel) => Run(channel, "timestamp_filter", async () =>
        (await IdsBy(channel, "sold_at >= '2025-01-01T00:00:00Z'", "sold_at: { gte: \"2025-01-01T00:00:00Z\" }", "sold_at ge '2025-01-01T00:00:00Z'"))
            .IdsShouldBe(11, 12, 13, 14, 15, 16, 17, 18));

    // R49
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    [InlineData("Rest", "translator")]
    public Task Robin_Proactive_IsNullAmount(string channel, string variant = "") => Run(channel, "is_null", async () =>
        (await IdsBy(channel, "amount IS NULL", "amount: { isNull: true }", variant == "translator" ? "amount is null" : "amount eq null")).IdsShouldBe(13, 14, 15), variant.Length == 0 ? null : variant);

    // R50
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    [InlineData("Rest", "translator")]
    public Task Robin_Proactive_IsNotNullRegion(string channel, string variant = "") => Run(channel, "is_not_null", async () =>
        (await IdsBy(channel, "region IS NOT NULL", "region: { isNull: false }", variant == "translator" ? "region is not null" : "region ne null")).IdsShouldBe(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 18), variant.Length == 0 ? null : variant);

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

    // R56
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_128_InListStrings(string channel) => Run(channel, "in_list_strings", async () =>
        (await IdsBy(channel, "product IN ('Widget', 'Gizmo')", "product: { in: [\"Widget\", \"Gizmo\"] }", "product in ('Widget','Gizmo')"))
            .IdsShouldBe(1, 2, 5, 6, 7, 10, 11, 12, 13, 15, 16, 18));

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
    [InlineData("Rest", "translator")]
    public Task Robin_Proactive_LikePrefix(string channel, string variant = "") => Run(channel, "like_prefix", async () =>
        (await IdsBy(channel, "note LIKE 'emoji%'", "note: { startsWith: \"emoji\" }", variant == "translator" ? "note like 'emoji%'" : "startswith(note,'emoji')")).IdsShouldBe(8), variant.Length == 0 ? null : variant);

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

    // ---------------------------------------------------------------------------------------------------------------------
    // WP-V2a: rework v2 scenarios v2-04..v2-17 (plan section 5.2). Expected values are hand-computed from the seed literals.
    // ---------------------------------------------------------------------------------------------------------------------

    // v2-04: stable codes for aggregate misuse
    [ParityTheory]
    [InlineData("WebSql", "sum_string")]
    [InlineData("GraphQl", "sum_string")]
    [InlineData("WebSql", "unknown_function")]
    [InlineData("GraphQl", "unknown_function")]
    [InlineData("WebSql", "unknown_group_column")]
    [InlineData("GraphQl", "unknown_group_column")]
    public Task Robin_Proactive_AggregateErrorsStableCode(string channel, string variant) => Run(channel, "aggregate_errors", async () =>
    {
        var count = Agg("COUNT", null, "n");
        var (r, code) = (channel, variant) switch
        {
            ("WebSql", "sum_string") => (await Sql("SELECT SUM(product) AS t FROM parity_sales"), "AGGREGATION_INVALID_FUNCTION_FOR_TYPE"),
            ("GraphQl", "sum_string") => (await GqlAggregate($"aggregates: [{Agg("SUM", "product", "t")}]"), "AGGREGATION_INVALID_FUNCTION_FOR_TYPE"),
            ("WebSql", "unknown_function") => (await Sql("SELECT NOPE_FN(amount) AS t FROM parity_sales"), "SQL_POLICY_VIOLATION"),
            ("GraphQl", "unknown_function") => (await GqlAggregate($"aggregates: [{Agg("NOPE", "amount", "t")}]"), "GRAPHQL_VALIDATION_FAILED"),
            ("WebSql", _) => (await Sql("SELECT COUNT(*) AS n FROM parity_sales GROUP BY nope"), "SQL_POLICY_DENIED"),
            _ => (await GqlAggregate($"groupBy: [nope], aggregates: [{count}]"), "GRAPHQL_VALIDATION_FAILED"),
        };
        Denied(r, "aggregate misuse", code);
    }, variant);

    // v2-05: CASE WHEN bucket grouped by the same expression
    [ParityFact]
    public Task Robin_112_CaseWhenBucketGroupBy() => Run("WebSql", "case_when_group_by", async () =>
    {
        const string bucket = "CASE WHEN amount IS NULL THEN 'none' WHEN amount < 100 THEN 'small' ELSE 'large' END";
        var r = await Sql($"SELECT {bucket} AS b, COUNT(*) AS n, SUM(amount) AS s FROM parity_sales GROUP BY {bucket} ORDER BY b");
        r.RowsShouldBe(["b", "n", "s"], [["large", 6, 1435.75m], ["none", 3, null], ["small", 9, 445.75m]], ordered: true);
    });

    // v2-06: NOT LIKE drops NULL notes (3VL); a pattern without a match gives zero rows
    [ParityTheory]
    [InlineData("WebSql", "not_like")]
    [InlineData("GraphQl", "not_like")]
    [InlineData("WebSql", "no_match")]
    [InlineData("GraphQl", "no_match")]
    public Task Robin_Proactive_NotLikeAndNoMatchLike(string channel, string variant) => Run(channel, "not_like", async () =>
    {
        if (variant == "not_like")
        {
            (await IdsBy(channel, "note NOT LIKE 'emoji%'", "not: { note: { startsWith: \"emoji\" } }", string.Empty)).IdsShouldBe(1, 2, 3, 4, 5, 6);
            return;
        }

        (await IdsBy(channel, "note LIKE '%zzz%'", "note: { contains: \"zzz\" }", string.Empty)).IdsShouldBe();
    }, variant);

    // v2-07: arithmetic, CAST and a sum over a product of two columns (NULL propagates)
    [ParityFact]
    public Task Robin_Proactive_ArithmeticAndCast() => Run("WebSql", "arithmetic_cast", async () =>
    {
        var rows = await Sql("SELECT id, qty * amount AS qa, CAST(qty AS VARCHAR) AS qs, amount + 0.25 AS a2 FROM parity_sales WHERE id IN (1, 12, 13) ORDER BY id");
        rows.RowsShouldBe(["id", "qa", "qs", "a2"], [[1, 1000.00m, "10", 100.25m], [12, 1221.75m, "9", 136.00m], [13, null, null, null]], ordered: true);
        (await Sql("SELECT SUM(qty * amount) AS total FROM parity_sales")).RowsShouldBe(["total"], [[8599.50m]]);
    });

    // v2-08: set operations (multiset and set semantics) and a column-count mismatch
    [ParityTheory]
    [InlineData("union_all")]
    [InlineData("union")]
    [InlineData("except")]
    [InlineData("intersect")]
    [InlineData("mismatch")]
    public Task Robin_Proactive_SetOperations(string variant) => Run("WebSql", "set_operations", async () =>
    {
        const string a = "SELECT id FROM parity_sales WHERE region = 'North'";
        const string b = "SELECT id FROM parity_sales WHERE product = 'Widget'";
        switch (variant)
        {
            case "union_all":
                (await Sql($"{a} UNION ALL {b}")).IdsShouldBe(1, 1, 2, 2, 3, 4, 5, 5, 6, 7, 11, 13, 16, 18, 18);
                break;
            case "union":
                (await Sql($"{a} UNION {b}")).IdsShouldBe(1, 2, 3, 4, 5, 6, 7, 11, 13, 16, 18);
                break;
            case "except":
                (await Sql($"{a} EXCEPT {b}")).IdsShouldBe(3, 4, 6);
                break;
            case "intersect":
                (await Sql($"{a} INTERSECT {b}")).IdsShouldBe(1, 2, 5, 18);
                break;
            default:
                Denied(await Sql($"{a} UNION SELECT id, qty FROM parity_sales"), "set operation column mismatch", "SQL_SYNTAX_ERROR");
                break;
        }
    }, variant);

    // v2-09: month bucket across the year end and an hour bucket across the spring DST switch
    [ParityTheory]
    [InlineData("berlin_month")]
    [InlineData("utc_month")]
    [InlineData("dst_hour")]
    public Task Robin_130_MonthBucketBerlinYearEndAndDstHour(string variant) => Run("GraphQl", "month_bucket_time_zone", async () =>
    {
        string Doc(string ids, string bucket, string zone) =>
            $"where: {{ id: {{ in: [{ids}] }} }}, timeBuckets: [{{ column: sold_at, bucket: {bucket}, timeZone: \"{zone}\" }}], aggregates: [{Agg("COUNT", null, "n")}], orderBy: [{{ column: sold_at }}]";
        switch (variant)
        {
            case "berlin_month":
                (await GqlAggregate(Doc("10, 11", "MONTH", "Europe/Berlin"), "sold_at")).RowsShouldBe(["sold_at", "n"], [["2025-01", 2]], ordered: true);
                break;
            case "utc_month":
                (await GqlAggregate(Doc("10, 11", "MONTH", "UTC"), "sold_at")).RowsShouldBe(["sold_at", "n"], [["2024-12", 1], ["2025-01", 1]], ordered: true);
                break;
            default:
                // #6 is 2024-03-31T00:30Z: still CET (+1) before the 01:00Z switch, so hour 01 of 2024-03-31.
                var r = await GqlAggregate(Doc("6", "HOUR", "Europe/Berlin"), "sold_at");
                r.Ok().Rows.Count.ShouldBe(1, r.RawBody);
                HourOf(r.Rows[0]["sold_at"]).ShouldBe(1, r.RawBody);
                RobinAssert.Day(r.Rows[0]["sold_at"]).ShouldBe("2024-03-31", r.RawBody);
                AsInt(r.Rows[0]["n"]).ShouldBe(1);
                break;
        }
    }, variant);

    // v2-10: a type mismatch or a dangling filter is a stable code, not an engine error
    [ParityTheory]
    [InlineData("GraphQl", "first_string")]
    [InlineData("GraphQl", "id_string")]
    [InlineData("Rest", "dangling_filter")]
    public Task Robin_Proactive_TypeMismatchStableCode(string channel, string variant) => Run(channel, "type_mismatch", async () =>
    {
        switch (variant)
        {
            case "first_string":
                Denied(await GqlList("first: \"10\""), "string page size", "GRAPHQL_VALIDATION_FAILED");
                break;
            case "id_string":
                Denied(await GqlList("where: { id: { eq: \"x\" } }, " + AllRows), "string id filter", "GRAPHQL_VALIDATION_FAILED");
                break;
            default:
                Denied(await RestFilter("region eq"), "dangling filter", "INVALID_QUERY");
                break;
        }
    }, variant);

    // v2-11: NOT IN with a NULL member matches no row (three-valued logic)
    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_NotInWithNullIsEmpty(string channel) => Run(channel, "not_in_null_member", async () =>
        (await IdsBy(channel, "region NOT IN ('North', NULL)", "region: { nin: [\"North\", null] }", string.Empty)).IdsShouldBe());

    // v2-12: window function per partition; a window ordered by a masked column is denied
    [ParityTheory]
    [InlineData("partition")]
    [InlineData("masked")]
    public Task Robin_Proactive_RowNumberOverPartition(string variant) => Run("WebSql", "row_number_window", async () =>
    {
        if (variant == "masked")
        {
            var denied = await Sql("SELECT id, ROW_NUMBER() OVER (ORDER BY amount) AS rn FROM parity_masked");
            if (!ParityAssert.IsError(denied))
            {
                throw new DeniedRequestSucceededException("a window over a masked column was answered instead of rejected.");
            }

            // Designed guard order (AstSecurityVisitor.MaskGuards.EnsureNoMaskedColumnsInAggregatesAndGrouping): a window function is checked
            // as an aggregation scope, so a masked ORDER BY column answers AGGREGATION_ON_MASKED_COLUMN (403).
            denied.Rejected("AGGREGATION_ON_MASKED_COLUMN");
            denied.Status.ShouldBe(403);
            return;
        }

        int[] rn = [1, 2, 3, 4, 5, 6, 1, 2, 3, 4, 5, 6, 1, 2, 3, 1, 2, 7];
        var r = await Sql("SELECT id, ROW_NUMBER() OVER (PARTITION BY region ORDER BY id) AS rn FROM parity_sales ORDER BY id");
        r.RowsShouldBe(["id", "rn"], rn.Select((n, i) => new object?[] { i + 1, n }).ToArray(), ordered: true);
    }, variant);

    // v2-13: null-safe equality with a bound NULL parameter
    [ParityTheory]
    [InlineData("not_distinct_null")]
    [InlineData("not_distinct_value")]
    [InlineData("distinct_null")]
    public Task Robin_Proactive_NullSafeEquality(string variant) => Run("WebSql", "null_safe_equality", async () =>
    {
        var (sql, region, expected) = variant switch
        {
            "not_distinct_null" => ("region IS NOT DISTINCT FROM @r", (string?)null, new[] { 16, 17 }),
            "not_distinct_value" => ("region IS NOT DISTINCT FROM @r", "South", new[] { 7, 8, 9, 10, 11, 12 }),
            _ => ("region IS DISTINCT FROM @r", (string?)null, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 18 }),
        };
        (await Sql($"SELECT id FROM parity_sales WHERE {sql} ORDER BY id", new { r = region })).IdsShouldBe(expected);
    }, variant);

    // v2-14 (#318): Int variables for first and offset
    [ParityTheory]
    [InlineData("five_ten")]
    [InlineData("nulls")]
    [InlineData("zero")]
    public Task Robin_318_IntVariablesFirstOffset(string variant) => Run("GraphQl", "int_variables_first_offset", async () =>
    {
        const string query = "query($f: Int, $o: Int) { default_main_parity_sales(first: $f, offset: $o, orderBy: [{ id: ASC }]) { id } }";
        const string field = "default_main_parity_sales";
        switch (variant)
        {
            case "five_ten":
                (await _robin.GraphQlAsync(query, field, new { f = 5, o = 10 })).IdsShouldBeInOrder(11, 12, 13, 14, 15);
                break;
            case "nulls":
                var unbounded = await GqlList("orderBy: [{ id: ASC }]");
                unbounded.IdsShouldBeInOrder(Enumerable.Range(1, 18).ToArray());
                (await _robin.GraphQlAsync(query, field, new { f = (int?)null, o = (int?)null })).IdsShouldBeInOrder(Enumerable.Range(1, 18).ToArray());
                break;
            default:
                (await _robin.GraphQlAsync(query, field, new { f = 0, o = (int?)null })).IdsShouldBe();
                break;
        }
    }, variant);

    // v2-15 (#319): empty logical members of a where object
    [ParityTheory]
    [InlineData("or_empty_member")]
    [InlineData("and_empty_list")]
    [InlineData("not_empty")]
    public Task Robin_319_EmptyLogicalMembers(string variant) => Run("GraphQl", "empty_logical_members", async () =>
    {
        var all = Enumerable.Range(1, 18).ToArray();
        switch (variant)
        {
            case "or_empty_member":
                (await GqlList("where: { or: [{}, { region: { eq: \"North\" } }] }, " + AllRows)).IdsShouldBe(all);
                break;
            case "and_empty_list":
                (await GqlList("where: { and: [] }, " + AllRows)).IdsShouldBe(all);
                break;
            default:
                (await GqlList("where: { not: {} }, " + AllRows)).IdsShouldBe();
                break;
        }
    }, variant);

    // v2-16: string functions
    [ParityFact]
    public Task Robin_Proactive_StringFunctions() => Run("WebSql", "string_functions", async () =>
    {
        var r = await Sql(
            "SELECT id, UPPER(note) AS u, LENGTH(note) AS l, SUBSTR(product, 1, 3) AS p3, product || '-' || region AS pr, TRIM('  ' || product || '  ') AS t " +
            "FROM parity_sales WHERE id IN (1, 8) ORDER BY id");
        r.RowsShouldBe(
            ["id", "u", "l", "p3", "pr", "t"],
            [[1, "PLAIN", 5, "Wid", "Widget-North", "Widget"], [8, "EMOJI \U0001F600", 7, "Gad", "Gadget-South", "Gadget"]],
            ordered: true);
    });

    // v2-17 (#302): offset at and above the cap, and a zero page size
    [ParityTheory]
    [InlineData("WebSql", "offset_max")]
    [InlineData("GraphQl", "offset_max")]
    [InlineData("Rest", "offset_max")]
    [InlineData("WebSql", "offset_over")]
    [InlineData("GraphQl", "offset_over")]
    [InlineData("Rest", "offset_over")]
    [InlineData("WebSql", "first_zero")]
    [InlineData("GraphQl", "first_zero")]
    [InlineData("Rest", "first_zero")]
    public Task Robin_302_OffsetBoundaryAndFirstZero(string channel, string variant) => Run(channel, "offset_boundary", async () =>
    {
        var (offset, limit) = variant switch
        {
            "offset_max" => (10000, 10),
            "offset_over" => (10001, 10),
            _ => (0, 0),
        };
        var r = channel switch
        {
            "WebSql" => await Sql($"SELECT id FROM parity_big ORDER BY id OFFSET {offset} LIMIT {limit}"),
            "GraphQl" => await GqlList($"first: {limit}, offset: {offset}, orderBy: [{{ id: ASC }}]", "id", Big),
            _ => await _robin.RestAsync($"/api/v1/data/default/main/{Big}?select=id&orderBy=id&limit={limit}&offset={offset}"),
        };
        if (variant == "offset_over" && channel != "WebSql")
        {
            // An offset above the cap is not a security cell: a 200 is a functional deviation (gap 054), a 5xx stays a hard failure in RobinClient.
            if (!ParityAssert.IsError(r))
            {
                // SEC-R100-19: gap 054 holds only while the body has zero rows; any row beyond the cap is a hard failure.
                if (r.Rows.Count > 0)
                {
                    throw new DeniedRequestSucceededException($"offset above the cap returned {r.Rows.Count} rows.");
                }

                throw new XunitException($"offset above the cap: expected 400 INVALID_QUERY, got HTTP {r.Status} with {r.Rows.Count} rows.");
            }

            Denied(r, "offset above the cap", "INVALID_QUERY");
            return;
        }

        r.IdsShouldBe();
    }, variant);
}
