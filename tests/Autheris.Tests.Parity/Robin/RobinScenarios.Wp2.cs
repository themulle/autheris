using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Autheris.Tests.Parity.Seed;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Robin;

// WP-R2 (R66-R100) of the Robin-100 plan. Second part of the same class: no own [Trait]/[Collection] (inherited from the attributed part).
public sealed partial class RobinScenarios
{
    private const string Masked = ParitySeed.MaskedTable;
    private const string Scoped = ParitySeed.ScopedTable;
    private const string AnalyzePath = "/api/v1/governance/queries/analyze";

    private static readonly int[] ClearOrderNullsFirst = [13, 14, 15, 16, 6, 5, 18, 2, 17, 7, 11, 10, 1, 12, 3, 4, 8, 9];
    private static readonly int[] ClearOrderNullsLast = [16, 6, 5, 18, 2, 17, 7, 11, 10, 1, 12, 3, 4, 8, 9, 13, 14, 15];

    private static string RestPath(string table, string query) => $"/api/v1/data/default/main/{table}?{query}";

    private Task<ChannelResult> RestIds(string table, string filter, string orderBy = "id") =>
        _robin.RestAsync(RestPath(table, "select=id&filter=" + Uri.EscapeDataString(filter) + "&orderBy=" + orderBy + "&limit=100"));

    private static int[] Range(int from, int count) => Enumerable.Range(from, count).ToArray();

    private static List<int> IdsOf(ChannelResult r) => r.Ok().Rows.Select(row => Convert.ToInt32(row["id"], System.Globalization.CultureInfo.InvariantCulture)).ToList();

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value;
                }

                if (FindProperty(p.Value, name) is { } inner)
                {
                    return inner;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindProperty(item, name) is { } inner)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    private static readonly Regex AmountField = new("\"amount\"\\s*:\\s*\"?(?<v>\\d+(\\.\\d+)?)\"?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when the body carries a clear seed amount (SEC-R100-03). Reports are written with ids only.</summary>
    private static bool LeaksClearAmount(string body)
    {
        var clear = ParitySeed.Sales.Where(s => s.Amount is not null).Select(s => s.Amount!.Value).Distinct().ToList();
        foreach (Match m in AmountField.Matches(body))
        {
            if (decimal.TryParse(m.Groups["v"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && clear.Contains(v))
            {
                return true;
            }
        }

        return body.Contains("135.75", StringComparison.Ordinal) || body.Contains("15.25", StringComparison.Ordinal) || body.Contains("50.5", StringComparison.Ordinal);
    }

    private static void ShouldNotLeakAmounts(ChannelResult r, string what)
    {
        if (LeaksClearAmount(r.RawBody))
        {
            throw new DeniedRequestSucceededException($"{what}: a clear masked amount is present in the response body.");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------
    // R66-R69. Paging [#39]
    // ---------------------------------------------------------------------------------------------------------------------

    private Task<ChannelResult> BigPage(string channel, int offset, int limit) => channel switch
    {
        "GraphQl" => GqlList($"first: {limit}, offset: {offset}, orderBy: [{{ id: ASC }}]", "id", Big),
        "Rest" => _robin.RestAsync(RestPath(Big, $"select=id&orderBy=id&limit={limit}&offset={offset}")),
        _ => Sql($"SELECT id FROM parity_big ORDER BY id OFFSET {offset} LIMIT {limit}"),
    };

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_39_LastPageOfBig(string channel) => Run(channel, "paging_last_page", async () =>
        (await BigPage(channel, 900, 100)).IdsShouldBeInOrder(Range(901, 100)));

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_39_OffsetBeyondEndEmpty(string channel) => Run(channel, "paging_offset_beyond_end", async () =>
        (await BigPage(channel, 1000, 100)).IdsShouldBe());

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_39_PagesAreDisjointAndComplete(string channel) => Run(channel, "paging_disjoint_complete", async () =>
    {
        var seen = new List<int>();
        foreach (var offset in new[] { 0, 250, 500, 750 })
        {
            seen.AddRange(IdsOf(await BigPage(channel, offset, 250)));
        }

        seen.ShouldBe(Range(1, 1000).ToList());
        seen.Distinct().Count().ShouldBe(1000);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_39_OrderByDescPaging(string channel) => Run(channel, "paging_order_desc", async () =>
    {
        var r = channel == "GraphQl"
            ? await GqlList("first: 5, orderBy: [{ id: DESC }]", "id", Big)
            : await Sql("SELECT id FROM parity_big ORDER BY id DESC LIMIT 5");
        r.IdsShouldBeInOrder(1000, 999, 998, 997, 996);
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R70-R71. Bulk cursor continuation and tampered cursor [#178, #222]
    // ---------------------------------------------------------------------------------------------------------------------

    private static string BulkPath(string table) => $"/api/v1/data/default/main/{table}/bulk";

    private static Dictionary<string, object?> BulkBody(string? cursor)
    {
        var body = new Dictionary<string, object?> { ["select"] = new[] { "id" }, ["limit"] = 400 };
        if (cursor is not null)
        {
            body["cursor"] = cursor;
        }

        return body;
    }

    private static (List<int> Ids, string? Next, bool? HasMore) ParseBulk(RobinHttpResponse r)
    {
        r.Result.Ok();
        var ids = new List<int>();
        string? next = null;
        bool? more = null;
        foreach (var line in r.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (root.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    next = c.GetString();
                }

                if (meta.TryGetProperty("hasMore", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    more = h.GetBoolean();
                }

                continue;
            }

            if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var i))
            {
                ids.Add(i);
            }
        }

        if (string.IsNullOrEmpty(next) && r.Headers.TryGetValue("X-Next-Cursor", out var header) && header.Length > 0)
        {
            next = header;
        }

        return (ids, string.IsNullOrEmpty(next) ? null : next, more);
    }

    [ParityFact]
    public Task Robin_178_BulkCursorContinuation() => Run("Rest", "bulk_cursor_continuation", async () =>
    {
        var all = new List<int>();
        var sizes = new List<int>();
        string? cursor = null;
        bool? lastHasMore = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var r = await _robin.PostJsonAsync(BulkPath(Big), BulkBody(cursor));
            var (ids, next, more) = ParseBulk(r);
            sizes.Add(ids.Count);
            all.AddRange(ids);
            lastHasMore = more;
            if (next is null)
            {
                break;
            }

            cursor = next;
        }

        sizes.ShouldBe([400, 400, 200]);
        all.ShouldBe(Range(1, 1000).ToList());
        lastHasMore.ShouldNotBe(true);
    });

    [ParityFact]
    public Task Robin_222_TamperedCursorRejected() => Run("Rest", "bulk_tampered_cursor", async () =>
    {
        var first = ParseBulk(await _robin.PostJsonAsync(BulkPath(Big), new { select = new[] { "id" }, limit = 400 }));
        var cursor = first.Next ?? throw new XunitException("No cursor returned for the first bulk slice.");
        var chars = cursor.ToCharArray();
        var mid = chars.Length / 2;
        chars[mid] = chars[mid] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        var r = await _robin.PostJsonAsync(BulkPath(Big), new { select = new[] { "id" }, limit = 400, cursor = tampered });
        r.Status.ShouldBe(400);
        r.Result.Rejected("INVALID_CURSOR");

        // A valid cursor of one table is not accepted on another table's route.
        var other = await _robin.PostJsonAsync(BulkPath(Sales), new { select = new[] { "id" }, limit = 400, cursor });
        other.Result.Rejected();
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R72-R73. Multi-key ordering and null placement [proactive]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_OrderByTwoKeys(string channel) => Run(channel, "order_two_keys", async () =>
    {
        var r = channel == "GraphQl"
            ? await GqlList("where: { region: { in: [\"North\", \"South\"] } }, first: 100, orderBy: [{ region: ASC }, { amount: DESC }, { id: ASC }]")
            : await Sql("SELECT id FROM parity_sales WHERE region IN ('North','South') ORDER BY region, amount DESC, id");
        r.IdsShouldBeInOrder(3, 4, 1, 2, 18, 5, 6, 8, 9, 12, 10, 11, 7);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_OrderByNullPlacement(string channel) => Run(channel, "order_null_placement", async () =>
    {
        if (channel == "WebSql")
        {
            (await Sql("SELECT id FROM parity_sales ORDER BY amount, id")).IdsShouldBeInOrder(ClearOrderNullsFirst);
            return;
        }

        // Observed: GraphQL places NULL amounts first, like WebSQL on SQLite.
        IdsOf(await GqlList("first: 100, orderBy: [{ amount: ASC }, { id: ASC }]")).ShouldBe(ClearOrderNullsFirst.ToList());
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R74-R75. Joins to the regions table [proactive]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityFact]
    public Task Robin_Proactive_JoinSalesToRegions() => Run("WebSql", "join_regions", async () =>
    {
        var r = await Sql("SELECT s.id AS id, r.tz AS tz FROM parity_sales s JOIN parity_regions r ON s.region_id = r.id");
        r.RowsShouldBe(["id", "tz"], Enumerable.Range(1, 15).Concat([18]).Select(i => new object?[] { i, i is <= 6 or 18 ? "Europe/Berlin" : "UTC" }).ToArray());
    });

    [ParityFact]
    public Task Robin_Proactive_LeftJoinKeepsNullRegion() => Run("WebSql", "left_join_regions", async () =>
    {
        var r = await Sql("SELECT s.id AS id, r.tz AS tz FROM parity_sales s LEFT JOIN parity_regions r ON s.region_id = r.id");
        r.RowsShouldBe(
            ["id", "tz"],
            Enumerable.Range(1, 18).Select(i => new object?[] { i, i is <= 6 or 18 ? "Europe/Berlin" : i is 16 or 17 ? null : "UTC" }).ToArray());
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R76-R80. Federation across the second source [#131, #122, #123, #124]
    // ---------------------------------------------------------------------------------------------------------------------

    private const string FederatedJoin = "FROM default.main.parity_sales a JOIN second.main.parity_sales b ON a.id = b.id";

    [ParityFact]
    public Task Robin_131_FederatedInListParameter() => Run("WebSql", "federated_in_list", async () =>
        (await Sql($"SELECT a.id AS id {FederatedJoin} WHERE a.id IN (@ids) ORDER BY a.id", new { ids = new[] { 2, 4, 6, 8 } })).IdsShouldBe(2, 4));

    [ParityFact]
    public Task Robin_122_FederatedFilterPushdownSameResult() => Run("WebSql", "federated_pushdown", async () =>
        (await Sql($"SELECT a.id AS id {FederatedJoin} WHERE b.product = 'Widget' ORDER BY a.id")).IdsShouldBe(1, 2));

    [ParityFact]
    public Task Robin_123_FederatedSameSourceSubqueryPart() => Run("WebSql", "federated_same_source_part", async () =>
    {
        var r = await Sql($"SELECT a.id AS id, r.name AS name FROM default.main.parity_sales a JOIN default.main.parity_regions r ON a.region_id = r.id JOIN second.main.parity_sales b ON a.id = b.id");
        r.RowsShouldBe(["id", "name"], [[1, "North"], [2, "North"], [3, "North"], [4, "North"]]);
    });

    [ParityFact]
    public Task Robin_124_FederatedKeySetJoin() => Run("WebSql", "federated_key_set", async () =>
        (await Sql("SELECT a.id AS id FROM default.main.parity_sales a JOIN default.main.parity_regions r ON a.region_id = r.id JOIN second.main.parity_sales b ON a.id = b.id ORDER BY a.id"))
        .IdsShouldBeInOrder(1, 2, 3, 4));

    [ParityFact]
    public Task Robin_131_FederatedParameterNullSafe() => Run("WebSql", "federated_null_parameter", async () =>
        (await Sql($"SELECT a.id AS id {FederatedJoin} WHERE b.region = @r", new { r = (string?)null })).IdsShouldBe());

    // ---------------------------------------------------------------------------------------------------------------------
    // R81, R87, R100. Sums over lists, years and scoped regions [proactive]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_SumAmountForProductList(string channel) => Run(channel, "sum_product_list", async () =>
    {
        var r = channel == "GraphQl"
            ? await GqlAggregate("where: { product: { in: [\"Widget\", \"Gizmo\"] } }, aggregates: [{ fn: SUM, column: amount, alias: \"total\" }]")
            : await Sql("SELECT SUM(amount) AS total FROM parity_sales WHERE product IN ('Widget','Gizmo')");
        r.RowsShouldBe(["total"], [[ParityExpected.SumAmountWidgetAndGizmo]]);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_SumAmountByYear(string channel) => Run(channel, "sum_by_year", async () =>
    {
        var expected = ParityExpected.SumAmountByYear.OrderBy(p => p.Key).Select(p => new object?[] { p.Key, p.Value }).ToArray();
        if (channel == "WebSql")
        {
            (await Sql("SELECT strftime('%Y', sale_date) AS y, SUM(amount) AS total FROM parity_sales GROUP BY strftime('%Y', sale_date) ORDER BY y"))
                .RowsShouldBe(["y", "total"], expected, ordered: true);
            return;
        }

        (await GqlAggregate(
            "timeBuckets: [{ column: sale_date, bucket: YEAR }], aggregates: [{ fn: SUM, column: amount, alias: \"total\" }], orderBy: [{ column: sale_date }]",
            "sale_date")).RowsShouldBe(["sale_date", "total"], expected, ordered: true);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_Proactive_SumAmountScopedTwoRegions(string channel) => Run(channel, "sum_scoped_two_regions", async () =>
    {
        var r = channel == "GraphQl"
            ? await GqlAggregate("where: { region: { in: [\"North\", \"South\"] } }, aggregates: [{ fn: SUM, column: amount, alias: \"total\" }]", table: Scoped)
            : await Sql("SELECT SUM(amount) AS total FROM parity_scoped WHERE region IN ('North','South')");
        r.RowsShouldBe(["total"], [[ParityExpected.SumAmountNorthAndSouth]]);
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R82-R83. Unknown objects give stable codes without an existence oracle [#30]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_30_UnknownTableSameCodeAsUnknownSchema(string channel) => Run(channel, "unknown_table_vs_schema", async () =>
    {
        ChannelResult table, schema;
        switch (channel)
        {
            case "WebSql":
                table = await Sql("SELECT id FROM parity_nope");
                schema = await Sql("SELECT id FROM default.nope.parity_sales");
                break;
            case "GraphQl":
                table = await GqlList("first: 1", "id", "parity_nope");
                schema = await _robin.GraphQlAsync("{ default_nope_parity_sales(first: 1) { id } }", "default_nope_parity_sales");
                table.Rejected("GRAPHQL_VALIDATION_FAILED");
                schema.Rejected("GRAPHQL_VALIDATION_FAILED");
                break;
            default:
                table = await _robin.RestAsync("/api/v1/data/default/main/parity_nope?select=id");
                schema = await _robin.RestAsync("/api/v1/data/default/nope/parity_sales?select=id");
                break;
        }

        table.Rejected();
        schema.Rejected();
        schema.Status.ShouldBe(table.Status);
        schema.Code.ShouldBe(table.Code);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_30_UnknownColumnStableCode(string channel) => Run(channel, "unknown_column", async () =>
    {
        var r = channel switch
        {
            "GraphQl" => await GqlList("first: 1", "nope"),
            "Rest" => await _robin.RestAsync(RestPath(Sales, "select=nope")),
            _ => await Sql("SELECT nope FROM parity_sales"),
        };
        r.Rejected(channel == "GraphQl" ? "GRAPHQL_VALIDATION_FAILED" : null);
        r.RawBody.Contains("no such column", StringComparison.OrdinalIgnoreCase).ShouldBeFalse("the response carries engine text (SEC-PAR-07)");
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R84-R86. Query analysis [#79, #175]
    // ---------------------------------------------------------------------------------------------------------------------

    private Task<RobinHttpResponse> Analyze(string sql) =>
        _robin.PostJsonAsync(AnalyzePath, new { queries = new[] { new { sqlQuery = sql, dataSource = "default" } } });

    private static void NoRowValuesOrStackFrames(string body)
    {
        foreach (var marker in new[] { "Widget", "Gadget", "Gizmo", "under_score", "50% off", "StackTrace", " at Autheris." })
        {
            body.Contains(marker, StringComparison.Ordinal).ShouldBeFalse("the analysis response carries a row value or a stack frame");
        }
    }

    [ParityFact]
    public Task Robin_79_AnalyzeValidQuery() => Run("WebSql", "analyze_valid", async () =>
    {
        var r = await Analyze("SELECT id FROM default.main.parity_sales WHERE region = 'North'");
        r.Result.Ok();
        using var doc = JsonDocument.Parse(r.Body);
        FindProperty(doc.RootElement, "isValid")?.ValueKind.ShouldBe(JsonValueKind.True);
        FindProperty(doc.RootElement, "sourceDatabaseReadsExecuted")?.GetInt64().ShouldBe(0);
        NoRowValuesOrStackFrames(r.Body);
    });

    [ParityFact]
    public Task Robin_79_AnalyzeInvalidSqlStableCode() => Run("WebSql", "analyze_invalid", async () =>
    {
        var r = await Analyze("SELEC id FRM parity_sales");
        NoRowValuesOrStackFrames(r.Body);
        if (ParityAssert.IsError(r.Result))
        {
            r.Result.Rejected();
            return;
        }

        r.Result.Ok();
        using var doc = JsonDocument.Parse(r.Body);
        FindProperty(doc.RootElement, "isValid")?.ValueKind.ShouldBe(JsonValueKind.False);
        var diagnostics = new[] { "diagnostics", "errors", "issues", "messages" }
            .Select(n => FindProperty(doc.RootElement, n))
            .Any(e => e is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0);
        diagnostics.ShouldBeTrue("an invalid query is reported without a diagnostic");
    });

    [ParityFact]
    public Task Robin_175_AnalyzeReportsRowEstimate() => Run("WebSql", "analyze_row_estimate", async () =>
    {
        var r = await Analyze("SELECT id FROM parity_sales LIMIT 10");
        r.Result.Ok();
        using var doc = JsonDocument.Parse(r.Body);
        FindProperty(doc.RootElement, "estimatedRows")?.GetInt64().ShouldBe(10);
        FindProperty(doc.RootElement, "sourceDatabaseReadsExecuted")?.GetInt64().ShouldBe(0);
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R88-R90. Rejected statements [proactive]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("update", "UPDATE parity_sales SET qty = 0")]
    [InlineData("delete", "DELETE FROM parity_sales")]
    [InlineData("drop", "DROP TABLE parity_sales")]
    public Task Robin_Proactive_WriteStatementRejected(string variant, string sql) => Run("WebSql", "write_statement_rejected", async () =>
    {
        // Observed stable rejection of a write by the gateway policy: 403 SQL_POLICY_VIOLATION.
        var rejected = (await Sql(sql)).Rejected("SQL_POLICY_VIOLATION");
        rejected.Status.ShouldBe(403);
        (await Sql("SELECT COUNT(*) AS n FROM parity_sales")).RowsShouldBe(["n"], [[18]]);
    }, variant);

    [ParityTheory]
    [InlineData("select", "SELECT 1; SELECT 2", "SQL_SYNTAX_ERROR")]
    [InlineData("write", "SELECT 1; DELETE FROM parity_sales", null)]
    public Task Robin_Proactive_MultiStatementRejected(string variant, string sql, string? code) => Run("WebSql", "multi_statement_rejected", async () =>
    {
        var r = (await Sql(sql)).Rejected(code);
        r.Rows.Count.ShouldBe(0);
        // The second statement did not run.
        (await Sql("SELECT COUNT(*) AS n FROM parity_sales")).RowsShouldBe(["n"], [[18]]);
    }, variant);

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_Proactive_NegativeLimitRejected(string channel) => Run(channel, "negative_limit_rejected", async () =>
    {
        var r = channel switch
        {
            "GraphQl" => await GqlList("first: -1"),
            "Rest" => await _robin.RestAsync(RestPath(Sales, "select=id&limit=-1")),
            _ => await Sql("SELECT id FROM parity_sales LIMIT -1"),
        };
        r.Rejected();
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R91-R95. Masked column [#27, #28, #116]
    // ---------------------------------------------------------------------------------------------------------------------

    private static void NoClearAmountInRows(ChannelResult r, string what)
    {
        foreach (var row in r.Rows)
        {
            if (row.TryGetValue("amount", out var v) && v is not null)
            {
                throw new DeniedRequestSucceededException($"{what}: a row carries a non-null amount for a masked column.");
            }
        }

        ShouldNotLeakAmounts(r, what);
    }

    [ParityFact]
    public Task Robin_27_MaskedValueMarkedInGraphQl() => Run("GraphQl", "masked_value_graphql", async () =>
    {
        var r = await GqlList(AllRows, "id amount", Masked);
        r.Ok().Rows.Count.ShouldBe(18);
        NoClearAmountInRows(r, "GraphQL masked list");
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_28_FilterOnMaskedColumnDenied(string channel) => Run(channel, "filter_on_masked", async () =>
    {
        var r = channel switch
        {
            "GraphQl" => await GqlList("where: { amount: { gt: 100 } }, " + AllRows, "id", Masked),
            "Rest" => await RestIds(Masked, "amount gt 100"),
            _ => await Sql("SELECT id FROM parity_masked WHERE amount > 100 ORDER BY id"),
        };
        if (ParityAssert.IsError(r))
        {
            r.Rejected();
            return;
        }

        var ids = IdsOf(r);
        if (ids.Intersect(new[] { 3, 4, 8, 9, 12 }).Any())
        {
            throw new DeniedRequestSucceededException("A filter on a masked column selected rows by their clear value.");
        }

        ids.ShouldBeEmpty();
        ShouldNotLeakAmounts(r, "filter on masked column");
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_28_OrderByMaskedColumnDenied(string channel) => Run(channel, "order_by_masked", async () =>
    {
        var r = channel == "GraphQl"
            ? await GqlList("first: 100, orderBy: [{ amount: ASC }, { id: ASC }]", "id", Masked)
            : await Sql("SELECT id FROM parity_masked ORDER BY amount, id");
        if (ParityAssert.IsError(r))
        {
            r.Rejected();
            return;
        }

        var ids = IdsOf(r);
        if (ids.SequenceEqual(ClearOrderNullsFirst) || ids.SequenceEqual(ClearOrderNullsLast))
        {
            throw new DeniedRequestSucceededException("ORDER BY a masked column returned the clear order.");
        }

        ShouldNotLeakAmounts(r, "order by masked column");
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_28_GroupByMaskedColumnDenied(string channel) => Run(channel, "group_by_masked", async () =>
    {
        var r = channel == "GraphQl"
            ? await GqlAggregate("groupBy: [amount], aggregates: [{ fn: COUNT, alias: \"n\" }]", "amount", Masked)
            : await Sql("SELECT amount, COUNT(*) AS n FROM parity_masked GROUP BY amount");
        if (ParityAssert.IsError(r))
        {
            r.Rejected();
            return;
        }

        r.Ok();
        NoClearAmountInRows(r, "group by masked column");
        r.RowsShouldBe(["amount", "n"], [[null, 18]]);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("Rest")]
    public Task Robin_116_SelectStarOmitsOrMasksColumn(string channel) => Run(channel, "select_star_masked", async () =>
    {
        var r = channel == "Rest"
            ? await _robin.RestAsync(RestPath(Masked, "orderBy=id&limit=100"))
            : await Sql("SELECT * FROM parity_masked ORDER BY id");
        r.Ok().Rows.Count.ShouldBe(18);
        NoClearAmountInRows(r, "select star on masked table");
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R96-R99. Required filter column [#163, #236, #221]
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_163_RequiredFilterOnAggregate(string channel) => Run(channel, "required_filter_aggregate", async () =>
    {
        const string aggregates = "aggregates: [{ fn: COUNT, alias: \"n\" }, { fn: SUM, column: amount, alias: \"total\" }]";
        var missing = channel == "GraphQl"
            ? await GqlAggregate("groupBy: [product], " + aggregates, "product", Scoped)
            : await Sql("SELECT product, COUNT(*) AS n, SUM(amount) AS total FROM parity_scoped GROUP BY product");
        missing.Rejected("REQUIRED_FILTER_COLUMN_MISSING");

        var satisfied = channel == "GraphQl"
            ? await GqlAggregate("where: { region: { eq: \"North\" } }, groupBy: [product], " + aggregates, "product", Scoped)
            : await Sql("SELECT product, COUNT(*) AS n, SUM(amount) AS total FROM parity_scoped WHERE region = 'North' GROUP BY product");
        satisfied.RowsShouldBe(["product", "n", "total"], [["Widget", 4, 220.50m], ["Gadget", 2, 400.00m], ["Gizmo", 1, 15.25m]]);
    });

    [ParityFact]
    public Task Robin_236_RequiredFilterRestOtherColumnNotEnough() => Run("Rest", "required_filter_other_column", async () =>
    {
        (await RestIds(Scoped, "product eq 'Widget'")).Rejected("REQUIRED_FILTER_COLUMN_MISSING");
        (await RestIds(Scoped, "region eq 'North' and product eq 'Widget'")).IdsShouldBe(1, 2, 5, 18);
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    public Task Robin_221_RequiredFilterInOrChainRejected(string channel) => Run(channel, "required_filter_or_chain", async () =>
    {
        var r = channel == "GraphQl"
            ? await GqlList("where: { or: [{ region: { eq: \"North\" } }, { product: { eq: \"Widget\" } }] }, " + AllRows, "id", Scoped)
            : await Sql("SELECT id FROM parity_scoped WHERE region = 'North' OR product = 'Widget' ORDER BY id");
        if (!ParityAssert.IsError(r))
        {
            throw new DeniedRequestSucceededException("An OR chain satisfied the required filter column.");
        }

        r.Rejected("REQUIRED_FILTER_COLUMN_MISSING");
    });

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_236_RequiredFilterSatisfiedByInList(string channel) => Run(channel, "required_filter_in_list", async () =>
    {
        var r = channel switch
        {
            "GraphQl" => await GqlList("where: { region: { in: [\"North\"] } }, " + AllRows, "id", Scoped),
            "Rest" => await RestIds(Scoped, "region in ('North')"),
            _ => await Sql("SELECT id FROM parity_scoped WHERE region IN ('North') ORDER BY id"),
        };
        r.IdsShouldBe(1, 2, 3, 4, 5, 6, 18);
    });
}
