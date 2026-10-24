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

    // --- WP-V2b ---
    private static readonly string[] EngineTextMarkers =
        ["StackTrace", " at Autheris.", " at System.", " at HotChocolate.", "Exception:", "no such", "SQLite", "mismatched input", "extraneous input", "Antlr"];

    /// <summary>SEC-R100-17: an error response carries no stack frame, parser text or engine text.</summary>
    private static void NoStackOrEngineText(ChannelResult r)
    {
        foreach (var marker in EngineTextMarkers)
        {
            r.RawBody.Contains(marker, StringComparison.Ordinal).ShouldBeFalse($"the response carries a stack frame or engine text ('{marker}').");
        }
    }

    private static bool HasColumn(ChannelResult r, string column)
    {
        using var doc = JsonDocument.Parse(r.RawBody);
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("columns", out var cols)
               && cols.EnumerateArray().Any(c => string.Equals(c.GetString(), column, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The sorted set of property paths of a JSON body without any value (SEC-R100-13 body shape).</summary>
    private static string Shape(string body)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(body);
        Walk(doc.RootElement, string.Empty);
        return string.Join("|", paths);

        void Walk(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    var child = path + "/" + p.Name;
                    paths.Add(child);
                    Walk(p.Value, child);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    Walk(item, path + "[]");
                }
            }
        }
    }

    private static readonly string[] ExistenceStatements = ["does not exist", "not found", "no such", "unknown source", "unknown table", "unknown schema", "unknown catalog"];

    private static void NoExistenceStatement(ChannelResult r)
    {
        foreach (var phrase in ExistenceStatements)
        {
            r.RawBody.Contains(phrase, StringComparison.OrdinalIgnoreCase).ShouldBeFalse($"the message states whether an object exists ('{phrase}', SEC-R100-13).");
        }
    }

    private static JsonElement? GraphQlAlias(string body, string alias)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(alias, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            return value.Clone();
        }

        return null;
    }

    private static List<int> AliasIds(string body, string alias) =>
        GraphQlAlias(body, alias) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToList()
            : throw new XunitException($"alias '{alias}' carries no list: {RobinAssert.Snippet(body)}");

    private async Task<string> WhereTypeNameAsync(string field)
    {
        var r = await _robin.GraphQlAsync("{ __type(name: \"Query\") { fields { name args { name type { name } } } } }", "__type");
        r.Status.ShouldBe(200);
        using var doc = JsonDocument.Parse(r.RawBody);
        var fields = doc.RootElement.GetProperty("data").GetProperty("__type").GetProperty("fields");
        foreach (var f in fields.EnumerateArray())
        {
            if (f.GetProperty("name").GetString() != field)
            {
                continue;
            }

            foreach (var arg in f.GetProperty("args").EnumerateArray())
            {
                if (arg.GetProperty("name").GetString() == "where" && arg.GetProperty("type").GetProperty("name").GetString() is { Length: > 0 } name)
                {
                    return name;
                }
            }
        }

        throw new XunitException($"no 'where' argument type found for {field}.");
    }
    // --- end WP-V2b ---

    // ---------------------------------------------------------------------------------------------------------------------
    // v2-18..v2-21. Malformed filters, IN list cap, GraphQL variables and directives, unmasked columns of a masked table
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("GraphQl", "unbalanced_document")]
    [InlineData("GraphQl", "like_on_string_filter")]
    [InlineData("Rest", "unbalanced_filter")]
    [InlineData("Rest", "unknown_function")]
    public Task Robin_Proactive_MalformedFilterStableCode(string channel, string variant) => Run(channel, "malformed_filter_stable_code", async () =>
    {
        var (r, code) = variant switch
        {
            "unbalanced_document" => (await _robin.GraphQlAsync("{ default_main_parity_sales(first: 1) { id }", "default_main_parity_sales"), "GRAPHQL_PARSE_FAILED"),
            "like_on_string_filter" => (await GqlList("where: { region: { like: \"x\" } }, first: 1"), "GRAPHQL_VALIDATION_FAILED"),
            "unbalanced_filter" => (await RestIds(Sales, "(region eq 'North'"), "INVALID_QUERY"),
            _ => (await RestIds(Sales, "nope(region)"), "INVALID_QUERY"),
        };
        r.Rejected(code);
        if (channel == "Rest")
        {
            r.Status.ShouldBe(400);
        }

        NoStackOrEngineText(r);
    }, variant);

    [ParityFact]
    public Task Robin_128_InListOverCapStableCode() => Run("WebSql", "in_list_over_cap", async () =>
    {
        var sql = $"SELECT id FROM {Big} WHERE id IN (@ids) ORDER BY id";
        (await Sql(sql, new { ids = Range(1, 1000) })).IdsShouldBeInOrder(Range(1, 1000));

        // SEC-R100-17: one value above MaxListParameterValues is rejected; a 200 is a failure (DoS bound), never a known gap.
        var over = await Sql(sql, new { ids = Range(1, 1001) });
        if (!ParityAssert.IsError(over))
        {
            throw new DeniedRequestSucceededException("An IN list above MaxListParameterValues (1000) was accepted.");
        }

        over.Rejected("SQL_LIST_PARAM_TOO_LARGE").Status.ShouldBe(400);
        NoStackOrEngineText(over);
    });

    [ParityTheory]
    [InlineData("variable")]
    [InlineData("include")]
    [InlineData("skip")]
    public Task Robin_Proactive_GraphQlVariablesAndDirectives(string variant) => Run("GraphQl", "variables_and_directives", async () =>
    {
        if (variant == "variable")
        {
            var whereType = await WhereTypeNameAsync("default_main_parity_sales");
            var r = await _robin.GraphQlAsync(
                "query($w: " + whereType + ") { default_main_parity_sales(where: $w, first: 100, orderBy: [{ id: ASC }]) { id } }",
                "default_main_parity_sales",
                new { w = new { region = new { eq = "North" } } });
            r.IdsShouldBeInOrder(1, 2, 3, 4, 5, 6, 18);
            return;
        }

        var directive = variant == "include" ? "@include(if: true)" : "@skip(if: true)";
        var masked = await GqlList(AllRows, "id amount " + directive, Masked);
        masked.Ok().Rows.Count.ShouldBe(18);
        RobinMaskedLeakScan.AssertNoClearAmount(masked.RawBody, ["amount"]);
        NoClearAmountInRows(masked, "masked column with " + directive);
        if (variant == "include")
        {
            masked.Rows.All(row => row.TryGetValue("amount", out var v) && v is null).ShouldBeTrue("an included masked amount is not null in every row");
        }
        else
        {
            masked.Rows.Any(row => row.ContainsKey("amount")).ShouldBeFalse("a skipped field is present in the rows");
        }
    }, variant);

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    public Task Robin_116_UnmaskedColumnsOnMaskedTable(string channel) => Run(channel, "unmasked_columns_on_masked_table", async () =>
    {
        var picked = channel switch
        {
            "GraphQl" => await GqlList("where: { id: { in: [1, 2] } }, " + AllRows, "id region product qty", Masked),
            "Rest" => await _robin.RestAsync(RestPath(Masked, "select=id,region,product,qty&orderBy=id&limit=100&filter=" + Uri.EscapeDataString("id in (1, 2)"))),
            _ => await Sql("SELECT id, region, product, qty FROM parity_masked WHERE id IN (1, 2) ORDER BY id"),
        };
        picked.RowsShouldBe(["id", "region", "product", "qty"], [[1, "North", "Widget", 10], [2, "North", "Widget", 5]], ordered: true);

        var south = channel switch
        {
            "GraphQl" => await GqlList("where: { region: { eq: \"South\" } }, " + AllRows, "id", Masked),
            "Rest" => await RestIds(Masked, "region eq 'South'"),
            _ => await Sql("SELECT id FROM parity_masked WHERE region = 'South' ORDER BY id"),
        };
        south.IdsShouldBeInOrder(7, 8, 9, 10, 11, 12);
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
        other.Result.Rejected("INVALID_CURSOR").Status.ShouldBe(400);
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

        // Channel parity: both channels push ORDER BY to the same SQLite source, NULLs first for ASC.
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

    [ParityTheory]
    [InlineData("local")]
    [InlineData("federated")]
    public Task Robin_Proactive_JoinWithZeroMatches(string variant) => Run("WebSql", "join_zero_matches", async () =>
    {
        var r = await Sql(variant == "local"
            ? "SELECT s.id AS id FROM parity_sales s JOIN parity_regions r ON s.region_id = r.id WHERE r.name = 'Nope'"
            : "SELECT a.id AS id FROM default.main.parity_sales a JOIN second.main.parity_sales b ON a.id = b.id WHERE b.id > 4");
        r.Ok().Rows.Count.ShouldBe(0);
        HasColumn(r, "id").ShouldBeTrue("a join without matches must still describe its columns");
    }, variant);
    [ParityFact]
    public Task Robin_123_FederatedSameSourceSubqueryPart() => Run("WebSql", "federated_same_source_part", async () =>
    {
        var r = await Sql($"SELECT a.id AS id, r.name AS name FROM default.main.parity_sales a JOIN default.main.parity_regions r ON a.region_id = r.id JOIN second.main.parity_sales b ON a.id = b.id");
        r.RowsShouldBe(["id", "name"], [[1, "North"], [2, "North"], [3, "North"], [4, "North"]]);
    });

    [ParityTheory]
    [InlineData("join")]
    [InlineData("single")]
    public Task Robin_30_FederatedUnknownSourceSameCode(string variant) => Run("WebSql", "federated_unknown_source_same_code", async () =>
    {
        var (unknownSource, unknownTable) = variant == "join"
            ? (await Sql("SELECT a.id AS id FROM default.main.parity_sales a JOIN nope.main.parity_sales b ON a.id = b.id"),
               await Sql("SELECT a.id AS id FROM default.main.parity_sales a JOIN second.main.parity_nope b ON a.id = b.id"))
            : (await Sql("SELECT id FROM nope.main.parity_sales"), await Sql("SELECT id FROM parity_nope"));

        // SEC-R100-13: no existence oracle between an unknown source and an unknown table.
        foreach (var r in new[] { unknownSource, unknownTable })
        {
            r.Rejected("SQL_POLICY_DENIED").Status.ShouldBe(403);
            r.Rows.Count.ShouldBe(0);
            NoExistenceStatement(r);
            NoStackOrEngineText(r);
        }

        unknownSource.Status.ShouldBe(unknownTable.Status);
        unknownSource.Code.ShouldBe(unknownTable.Code);
        Shape(unknownSource.RawBody).ShouldBe(Shape(unknownTable.RawBody));
    }, variant);
    [ParityFact]
    public Task Robin_131_FederatedParameterNullSafe() => Run("WebSql", "federated_null_parameter", async () =>
        (await Sql($"SELECT a.id AS id {FederatedJoin} WHERE b.region = @r", new { r = (string?)null })).IdsShouldBe());

    // ---------------------------------------------------------------------------------------------------------------------
    // v2-24..v2-27, v2-29. Subqueries, CTE gate, unauthenticated requests, scoped aliases, OData
    // ---------------------------------------------------------------------------------------------------------------------

    [ParityTheory]
    [InlineData("scalar")]
    [InlineData("correlated")]
    public Task Robin_Proactive_ScalarAndCorrelatedSubquery(string variant) => Run("WebSql", "scalar_correlated_subquery", async () =>
    {
        if (variant == "scalar")
        {
            // AVG(amount) over the 15 non-null amounts = 1881.50 / 15 = 125.4333...
            (await Sql("SELECT id FROM parity_sales WHERE amount > (SELECT AVG(amount) FROM parity_sales) ORDER BY id")).IdsShouldBeInOrder(3, 4, 8, 9, 12);
            return;
        }

        (await Sql("SELECT s.id AS id FROM parity_sales s WHERE EXISTS (SELECT 1 FROM parity_regions r WHERE r.id = s.region_id AND r.tz = 'UTC') ORDER BY s.id"))
            .IdsShouldBeInOrder(7, 8, 9, 10, 11, 12, 13, 14, 15);
    }, variant);

    [ParityTheory]
    [InlineData("cte")]
    [InlineData("recursive")]
    [InlineData("self_reference")]
    public Task Robin_277_CteAndRecursiveGate(string variant) => Run("WebSql", "cte_and_recursive_gate", async () =>
    {
        if (variant == "cte")
        {
            (await Sql("WITH n AS (SELECT id FROM parity_sales WHERE region = 'North') SELECT id FROM n ORDER BY id")).IdsShouldBeInOrder(1, 2, 3, 4, 5, 6, 18);
            return;
        }

        // The recursive gate is off in the parity host (SEC-R100-18): the rejection is a security cell.
        var sql = variant == "recursive"
            ? "WITH RECURSIVE s(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM s WHERE x < 5) SELECT x FROM s"
            : "WITH s(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM s WHERE x < 5) SELECT x FROM s";
        var r = await Sql(sql);
        if (!ParityAssert.IsError(r))
        {
            throw new DeniedRequestSucceededException("A recursive CTE ran although the gate is off.");
        }

        r.Rejected("SQL_CONSTRUCT_NOT_SUPPORTED").Status.ShouldBe(422);
        r.Rows.Count.ShouldBe(0);
    }, variant);

    [ParityTheory]
    [InlineData("WebSql")]
    [InlineData("GraphQl")]
    [InlineData("Rest")]
    [InlineData("OData")]
    public Task Robin_Proactive_UnauthenticatedRequestRejected(string channel) => Run(channel, "unauthenticated_rejected", () => channel switch
    {
        "GraphQl" => _robin.AssertUnauthorizedAsync(HttpMethod.Post, "/graphql", "{\"query\":\"{ default_main_parity_sales(first: 1) { id } }\"}"),
        "Rest" => _robin.AssertUnauthorizedAsync(HttpMethod.Get, RestPath(Sales, "select=id")),
        "OData" => _robin.AssertUnauthorizedAsync(HttpMethod.Get, "/odata/default/main/parity_sales?$select=id"),
        _ => _robin.AssertUnauthorizedAsync(HttpMethod.Post, "/api/v1/sql", "{\"sql\":\"SELECT id FROM parity_sales\"}"),
    });

    [ParityTheory]
    [InlineData("two_filtered")]
    [InlineData("unfiltered_alias")]
    [InlineData("single_unfiltered")]
    public Task Robin_163_GraphQlAliasesOnScopedTable(string variant) => Run("GraphQl", "scoped_aliases", async () =>
    {
        const string north = "north: default_main_parity_scoped(where: { region: { eq: \"North\" } }, first: 100) { id }";
        if (variant == "two_filtered")
        {
            var ok = await _robin.GraphQlAsync("{ " + north + " south: default_main_parity_scoped(where: { region: { eq: \"South\" } }, first: 100) { id } }", "north");
            ok.Ok();
            AliasIds(ok.RawBody, "north").Order().ToList().ShouldBe([1, 2, 3, 4, 5, 6, 18]);
            AliasIds(ok.RawBody, "south").Order().ToList().ShouldBe([7, 8, 9, 10, 11, 12]);
            return;
        }

        // SEC-R100-14: one alias without the required filter rejects the whole request.
        var query = variant == "unfiltered_alias"
            ? "{ " + north + " all: default_main_parity_scoped(first: 100) { id } }"
            : "{ all: default_main_parity_scoped(first: 100) { id } }";
        var r = await _robin.GraphQlAsync(query, "all");
        if (GraphQlAlias(r.RawBody, "all") is not null)
        {
            throw new DeniedRequestSucceededException("An alias without the required filter column returned data.");
        }

        if (!ParityAssert.IsError(r))
        {
            throw new DeniedRequestSucceededException("A request with an unfiltered scoped alias succeeded without an error.");
        }

        r.Rejected("REQUIRED_FILTER_COLUMN_MISSING");
        if (GraphQlAlias(r.RawBody, "north") is not null)
        {
            throw new XunitException("The filtered alias returned partial data although the request was rejected.");
        }
    }, variant);

    [ParityTheory]
    [InlineData("OData", "region_eq")]
    [InlineData("OData", "amount_gt_top")]
    [InlineData("Rest", "region_eq")]
    [InlineData("Rest", "amount_gt_top")]
    public Task Robin_Proactive_ODataFilterMatchesRest(string channel, string variant) => Run(channel, "odata_filter_matches_rest", async () =>
    {
        var byRegion = variant == "region_eq";
        var r = channel == "OData"
            ? await _robin.ODataAsync("/odata/default/main/parity_sales?$select=id&$orderby=id&$filter=" +
                Uri.EscapeDataString(byRegion ? "region eq 'North'" : "amount gt 100") + (byRegion ? string.Empty : "&$top=3"))
            : byRegion
                ? await RestIds(Sales, "region eq 'North'")
                : await _robin.RestAsync(RestPath(Sales, "select=id&orderBy=id&limit=3&filter=" + Uri.EscapeDataString("amount gt 100")));
        if (byRegion)
        {
            r.IdsShouldBeInOrder(1, 2, 3, 4, 5, 6, 18);
        }
        else
        {
            r.IdsShouldBeInOrder(3, 4, 8);
        }
    }, variant);

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

        // Designed codes: WebSQL 403 SQL_POLICY_DENIED (no existence oracle), REST 404 RESOURCE_NOT_FOUND, GraphQL GRAPHQL_VALIDATION_FAILED.
        var (expectedCode, expectedStatus) = channel switch
        {
            "WebSql" => ("SQL_POLICY_DENIED", 403),
            "Rest" => ("RESOURCE_NOT_FOUND", 404),
            _ => ("GRAPHQL_VALIDATION_FAILED", 400),
        };
        table.Rejected(expectedCode).Status.ShouldBe(expectedStatus);
        schema.Rejected(expectedCode).Status.ShouldBe(expectedStatus);
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
        // #312 (SEC-PG-10, SEC-X1-05): an unknown column is answered like a denied one (no column-name oracle), so W is SQL_POLICY_DENIED.
        r.Rejected(channel switch { "GraphQl" => "GRAPHQL_VALIDATION_FAILED", "Rest" => "INVALID_QUERY", _ => "SQL_POLICY_DENIED" });
        if (channel != "GraphQl")
        {
            r.Status.ShouldBe(channel == "Rest" ? 400 : 403);
        }

        r.RawBody.Contains("no such column", StringComparison.OrdinalIgnoreCase).ShouldBeFalse("the response carries engine text (SEC-PAR-07)");
    });

    // ---------------------------------------------------------------------------------------------------------------------
    // R84-R86. Query analysis [#79, #175]
    // ---------------------------------------------------------------------------------------------------------------------

    private Task<RobinHttpResponse> Analyze(string sql) =>
        _robin.PostJsonAsync(AnalyzePath, new { queries = new[] { new { sqlQuery = sql, dataSource = "default" } } });

    private static bool ContainsString(JsonElement e, string value) => e.ValueKind switch
    {
        JsonValueKind.String => string.Equals(e.GetString(), value, StringComparison.Ordinal),
        JsonValueKind.Object => e.EnumerateObject().Any(p => ContainsString(p.Value, value)),
        JsonValueKind.Array => e.EnumerateArray().Any(i => ContainsString(i, value)),
        _ => false,
    };

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
        (FindProperty(doc.RootElement, "isValid") ?? throw new XunitException("isValid missing")).ValueKind.ShouldBe(JsonValueKind.True);
        (FindProperty(doc.RootElement, "sourceDatabaseReadsExecuted") ?? throw new XunitException("sourceDatabaseReadsExecuted missing")).GetInt64().ShouldBe(0);
        NoRowValuesOrStackFrames(r.Body);
    });

    [ParityFact]
    public Task Robin_79_AnalyzeInvalidSqlStableCode() => Run("WebSql", "analyze_invalid", async () =>
    {
        var r = await Analyze("SELEC id FRM parity_sales");
        NoRowValuesOrStackFrames(r.Body);
        r.Result.Ok();
        using var doc = JsonDocument.Parse(r.Body);
        (FindProperty(doc.RootElement, "isValid") ?? throw new XunitException("isValid missing")).ValueKind.ShouldBe(JsonValueKind.False);
        ContainsString(doc.RootElement, "SYNTAX_ERROR").ShouldBeTrue("an invalid query is reported without a SYNTAX_ERROR diagnostic");
    });

    [ParityFact]
    public Task Robin_175_AnalyzeReportsRowEstimate() => Run("WebSql", "analyze_row_estimate", async () =>
    {
        var r = await Analyze("SELECT id FROM parity_sales LIMIT 10");
        r.Result.Ok();
        using var doc = JsonDocument.Parse(r.Body);
        (FindProperty(doc.RootElement, "estimatedRows") ?? throw new XunitException("estimatedRows missing")).GetInt64().ShouldBe(10);
        (FindProperty(doc.RootElement, "sourceDatabaseReadsExecuted") ?? throw new XunitException("sourceDatabaseReadsExecuted missing")).GetInt64().ShouldBe(0);
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
        // Lead decision 2026-10-23: a write is pinned to 403 SQL_POLICY_VIOLATION (security cell, never split, never a gap).
        var rejected = (await Sql(sql)).Rejected("SQL_POLICY_VIOLATION");
        rejected.Status.ShouldBe(403);
        (await Sql("SELECT COUNT(*) AS n FROM parity_sales")).RowsShouldBe(["n"], [[18]]);
    }, variant);

    [ParityTheory]
    [InlineData("select", "SELECT 1; SELECT 2", "SQL_SYNTAX_ERROR")]
    [InlineData("write", "SELECT 1; DELETE FROM parity_sales", "SQL_SYNTAX_ERROR")]
    public Task Robin_Proactive_MultiStatementRejected(string variant, string sql, string code) => Run("WebSql", "multi_statement_rejected", async () =>
    {
        var r = (await Sql(sql)).Rejected(code);
        r.Status.ShouldBe(400);
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
        r.Rejected(channel == "WebSql" ? "SQL_SYNTAX_ERROR" : "INVALID_QUERY");
        if (channel != "GraphQl")
        {
            r.Status.ShouldBe(400);
        }
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
        if (!ParityAssert.IsError(r))
        {
            throw new DeniedRequestSucceededException(IdsOf(r).Intersect(new[] { 3, 4, 8, 9, 12 }).Any()
                ? "A filter on a masked column selected rows by their clear value."
                : "A filter on a masked column was not rejected.");
        }

        r.Rejected(channel switch { "WebSql" => "SQL_POLICY_VIOLATION", _ => "INVALID_QUERY" });
        if (channel != "GraphQl")
        {
            r.Status.ShouldBe(channel == "Rest" ? 400 : 403);
        }

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
        if (!ParityAssert.IsError(r))
        {
            var ids = IdsOf(r);
            throw new DeniedRequestSucceededException(ids.SequenceEqual(ClearOrderNullsFirst) || ids.SequenceEqual(ClearOrderNullsLast)
                ? "ORDER BY a masked column returned the clear order."
                : "ORDER BY a masked column was not rejected.");
        }

        r.Rejected(channel == "WebSql" ? "SQL_POLICY_VIOLATION" : "INVALID_QUERY");
        if (channel == "WebSql")
        {
            r.Status.ShouldBe(403);
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
        if (!ParityAssert.IsError(r))
        {
            NoClearAmountInRows(r, "group by masked column");
            throw new DeniedRequestSucceededException("GROUP BY a masked column was not rejected.");
        }

        r.Rejected("AGGREGATION_ON_MASKED_COLUMN");
        if (channel == "WebSql")
        {
            r.Status.ShouldBe(403);
        }

        ShouldNotLeakAmounts(r, "group by masked column");
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
