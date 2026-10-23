using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Robin;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Matrix;

/// <summary>
/// Block A as data (D-13): every capability x channel cell is <see cref="Supported"/>, <see cref="Unsupported"/> or <see cref="Pending"/>.
/// Expected values come from <see cref="ParityOracle"/> over the seed literals. <c>Matrix_IsComplete</c> fails when a cell is missing.
/// </summary>
public static class ParityMatrix
{
    private const string Sales = ParitySeed.SalesTable;
    private const string ListField = "default_main_" + Sales;
    private const string AggField = "default_main_" + Sales + "_aggregate";
    private const string InvalidForType = "AGGREGATION_INVALID_FUNCTION_FOR_TYPE";
    private const string RestPending = "inexpressible: the OData filter grammar of the REST data endpoint cannot express this capability";
    private const string RestNoAggregation = "inexpressible: the REST data endpoint has no aggregation";
    private const string McpPending = "MCP channel driver deferred (PARITY-FU-001)";

    public static IReadOnlyList<MatrixChannel> Channels { get; } = [MatrixChannel.WebSql, MatrixChannel.GraphQl, MatrixChannel.Rest, MatrixChannel.Mcp];

    public static IReadOnlyList<string> Capabilities { get; }

    public static IReadOnlyDictionary<(string Capability, MatrixChannel Channel), MatrixCell> Cells { get; }

    static ParityMatrix()
    {
        var cells = new Dictionary<(string, MatrixChannel), MatrixCell>();
        var caps = new List<string>();

        void Cap(string id, MatrixCell webSql, MatrixCell graphQl, MatrixCell? rest = null, MatrixCell? mcp = null)
        {
            caps.Add(id);
            cells.Add((id, MatrixChannel.WebSql), webSql);
            cells.Add((id, MatrixChannel.GraphQl), graphQl);
            cells.Add((id, MatrixChannel.Rest), rest ?? new Pending(RestPending));
            cells.Add((id, MatrixChannel.Mcp), mcp ?? new Pending(McpPending));
        }

        // ---- aggregates over the whole table ----
        ExpectedSpec One(object? value) => new(["v"], [[value]]);
        MatrixCell Scalar(string sqlExpr, object? expected, string? where = null) => new Supported(new SqlRequest($"SELECT {sqlExpr} AS v FROM {Sales}{where}"), One(expected));
        MatrixCell ScalarGql(string fn, string? column, object? expected, string? gqlWhere = null) =>
            new Supported(Agg((gqlWhere is null ? string.Empty : $"where: {{ {gqlWhere} }}, ") + $"aggregates: [{{ fn: {fn}{(column is null ? string.Empty : $", column: {column}")}, alias: \"v\" }}]"), One(expected));
        var noAgg = new Pending(RestNoAggregation);

        Cap("agg_sum", Scalar("SUM(amount)", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Sum, "amount")), ScalarGql("SUM", "amount", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Sum, "amount")), noAgg);
        Cap("agg_avg", Scalar("AVG(amount)", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Avg, "amount")), ScalarGql("AVG", "amount", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Avg, "amount")), noAgg);
        Cap("agg_count", Scalar("COUNT(*)", ParityOracle.Rows.Count), ScalarGql("COUNT", null, ParityOracle.Rows.Count), noAgg);
        Cap("agg_min", Scalar("MIN(amount)", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Min, "amount")), ScalarGql("MIN", "amount", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Min, "amount")), noAgg);
        Cap("agg_max", Scalar("MAX(amount)", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Max, "amount")), ScalarGql("MAX", "amount", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Max, "amount")), noAgg);
        Cap("agg_count_distinct", Scalar("COUNT(DISTINCT product)", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.CountDistinct, "product")), ScalarGql("COUNT_DISTINCT", "product", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.CountDistinct, "product")), noAgg);

        // Aggregates that are invalid for the column type: a stable 4xx (GraphQL) is the contract; WebSQL on SQLite accepts them (PARITY-GAP-021).
        MatrixCell RejectedGql(string fn, string column) => new Unsupported(Agg($"aggregates: [{{ fn: {fn}, column: {column}, alias: \"v\" }}]"), InvalidForType, "aggregate");
        Cap("agg_sum_text", new Unsupported(new SqlRequest($"SELECT SUM(product) AS v FROM {Sales}"), null, "SUM"), RejectedGql("SUM", "product"), noAgg);
        Cap("agg_avg_date", new Unsupported(new SqlRequest($"SELECT AVG(sale_date) AS v FROM {Sales}"), null, "AVG"), RejectedGql("AVG", "sale_date"), noAgg);
        Cap("agg_sum_bool", new Unsupported(new SqlRequest($"SELECT SUM(is_return) AS v FROM {Sales}"), null, "SUM"), RejectedGql("SUM", "is_return"), noAgg);

        // ---- group by ----
        var byRegion = new ExpectedSpec(["region", "n"], ParityOracle.Grouped(ParityOracle.Rows, r => r.Region, (ParityFn.Count, null)));
        Cap(
            "group_column",
            new Supported(new SqlRequest($"SELECT region, COUNT(*) AS n FROM {Sales} GROUP BY region"), byRegion),
            new Supported(Agg("groupBy: [region], aggregates: [{ fn: COUNT, alias: \"n\" }]", "region"), byRegion),
            noAgg);
        var sumByRegion = new ExpectedSpec(["region", "total"], ParityOracle.Grouped(ParityOracle.Rows, r => r.Region, (ParityFn.Sum, "amount")));
        Cap(
            "group_column_sum_null",
            new Supported(new SqlRequest($"SELECT region, SUM(amount) AS total FROM {Sales} GROUP BY region"), sumByRegion),
            new Supported(Agg("groupBy: [region], aggregates: [{ fn: SUM, column: amount, alias: \"total\" }]", "region"), sumByRegion),
            noAgg);

        void DatePart(string id, string strftime, string bucket, Func<SaleRow, object?> key)
        {
            var spec = new ExpectedSpec(["d", "n"], ParityOracle.Grouped(ParityOracle.Rows, key, (ParityFn.Count, null)));
            Cap(
                id,
                new Supported(new SqlRequest($"SELECT strftime('{strftime}', sale_date) AS d, COUNT(*) AS n FROM {Sales} GROUP BY strftime('{strftime}', sale_date)"), spec),
                new Supported(Agg($"timeBuckets: [{{ column: sale_date, bucket: {bucket} }}], aggregates: [{{ fn: COUNT, alias: \"n\" }}], orderBy: [{{ column: sale_date }}]", "sale_date"), spec, ["sale_date", "n"]),
                noAgg);
        }

        DatePart("group_date_year", "%Y", "YEAR", ParityOracle.Year);
        DatePart("group_date_month", "%Y-%m", "MONTH", ParityOracle.Month);
        DatePart("group_date_day", "%Y-%m-%d", "DAY", ParityOracle.Day);

        var yearSpec = new ExpectedSpec(["d", "n"], ParityOracle.Grouped(ParityOracle.Rows, ParityOracle.Year, (ParityFn.Count, null)));
        Cap(
            "group_date_part_standard_sql",
            new Supported(new SqlRequest($"SELECT EXTRACT(YEAR FROM sale_date) AS d, COUNT(*) AS n FROM {Sales} GROUP BY EXTRACT(YEAR FROM sale_date)"), yearSpec),
            new Pending("inexpressible: the typed aggregate has no SQL expression text; date parts are covered by group_date_year (timeBuckets)"),
            noAgg);

        var byLowerProduct = new ExpectedSpec(["p", "n"], ParityOracle.Grouped(ParityOracle.Rows, r => r.Product.ToLowerInvariant(), (ParityFn.Count, null)));
        Cap(
            "group_expression",
            new Supported(new SqlRequest($"SELECT LOWER(product) AS p, COUNT(*) AS n FROM {Sales} GROUP BY LOWER(product)"), byLowerProduct),
            new Pending("inexpressible: the typed aggregate groups by columns and time buckets only; a groupBy expression is a GraphQL validation error without extensions.code (PARITY-GAP-003)"),
            noAgg);

        // ---- filters (list of ids) ----
        ExpectedSpec Ids(Func<SaleRow, bool> predicate, bool ordered = false) => new(["id"], ParityOracle.Ids(ParityOracle.Rows.Where(predicate)), ordered);

        void Filter(string id, Func<SaleRow, bool> predicate, string sqlWhere, string gqlWhere, string? restFilter, object? sqlParameters = null, string? gqlDeclaration = null, object? gqlVariables = null)
        {
            var spec = Ids(predicate);
            Cap(
                id,
                new Supported(new SqlRequest($"SELECT id FROM {Sales}{sqlWhere} ORDER BY id", sqlParameters), spec),
                new Supported(ListGql($"where: {{ {gqlWhere} }}, ", gqlDeclaration, gqlVariables), spec),
                restFilter is null ? null : new Supported(RestList(restFilter), spec));
        }

        Cap(
            "filter_none",
            new Supported(new SqlRequest($"SELECT id FROM {Sales} ORDER BY id"), Ids(_ => true)),
            new Supported(ListGql(string.Empty), Ids(_ => true)),
            new Supported(RestList(null), Ids(_ => true)));
        Cap(
            "filter_empty_object",
            new Supported(new SqlRequest($"SELECT id FROM {Sales} WHERE 1 = 1 ORDER BY id"), Ids(_ => true)),
            new Supported(ListGql("where: {}, "), Ids(_ => true)),
            new Supported(RestList(string.Empty), Ids(_ => true)));
        Filter("filter_eq", r => r.Region == "North", " WHERE region = 'North'", "region: { eq: \"North\" }", "region eq 'North'");
        Filter("filter_eq_date", r => r.SaleDate == "2024-02-29", " WHERE sale_date = '2024-02-29'", "sale_date: { eq: \"2024-02-29\" }", "sale_date eq '2024-02-29'");
        Filter("filter_neq", r => r.Region is not null && r.Region != "North", " WHERE region <> 'North'", "region: { neq: \"North\" }", "region ne 'North'");
        Filter("filter_compare_numeric", r => r.Qty > 6, " WHERE qty > 6", "qty: { gt: 6 }", "qty gt 6");
        Filter("filter_range", r => r.Amount >= 100m && r.Amount < 300m, " WHERE amount >= 100 AND amount < 300", "amount: { gte: 100, lt: 300 }", "amount ge 100 and amount lt 300");
        Filter(
            "filter_range_date",
            r => string.CompareOrdinal(r.SaleDate, "2024-02-29") >= 0 && string.CompareOrdinal(r.SaleDate, "2024-03-31") < 0,
            " WHERE sale_date >= '2024-02-29' AND sale_date < '2024-03-31'",
            "sale_date: { gte: \"2024-02-29\", lt: \"2024-03-31\" }",
            "sale_date ge '2024-02-29' and sale_date lt '2024-03-31'");
        Cap(
            "filter_like",
            new Supported(new SqlRequest($"SELECT id FROM {Sales} WHERE note LIKE '%off%' ORDER BY id"), Ids(r => r.Note?.Contains("off", StringComparison.Ordinal) == true)),
            new Supported(ListGql("where: { note: { contains: \"off\" } }, "), Ids(r => r.Note?.Contains("off", StringComparison.Ordinal) == true)),
            new Pending("inexpressible: the OData filter grammar of the REST data endpoint has no contains/LIKE function (PARITY-GAP-004 family: 400 without a stable code)"));
        Filter("filter_in_list", r => r.Region is "North" or "Empty", " WHERE region IN ('North', 'Empty')", "region: { in: [\"North\", \"Empty\"] }", "region in ('North','Empty')");
        Cap(
            "filter_in_empty",
            new Supported(new SqlRequest($"SELECT id FROM {Sales} WHERE region IN () ORDER BY id"), Ids(_ => false)),
            new Supported(ListGql("where: { region: { in: [] } }, "), Ids(_ => false)),
            new Supported(RestList("region in ()"), Ids(_ => false)));
        Cap(
            "filter_is_null",
            new Supported(new SqlRequest($"SELECT id FROM {Sales} WHERE region IS NULL ORDER BY id"), Ids(r => r.Region is null)),
            new Supported(ListGql("where: { region: { isNull: true } }, "), Ids(r => r.Region is null)),
            new Pending("inexpressible: the REST filter grammar has no IS NULL operator; `region eq null` is SQL equality and returns no rows (PARITY-GAP-009 is resolved: `eq: null` is three-valued logic)"));

        // ---- order and paging ----
        var newest = new ExpectedSpec(["id"], ParityOracle.Rows.OrderByDescending(r => r.SaleDate, StringComparer.Ordinal).ThenBy(r => r.Id).Take(5).Select(r => new object?[] { r.Id }).ToArray(), true);
        Cap(
            "order_desc_then_id",
            new Supported(new SqlRequest($"SELECT id FROM {Sales} ORDER BY sale_date DESC, id ASC LIMIT 5"), newest),
            new Supported(new GraphQlRequest("{ " + ListField + "(first: 5, orderBy: [{ sale_date: DESC }, { id: ASC }]) { id } }", ListField), newest),
            new Supported(new RestRequest(RobinClient.SalesRest + "?select=id&limit=5&orderBy=" + Uri.EscapeDataString("sale_date desc,id asc")), newest));
        var page = new ExpectedSpec(["id"], ParityOracle.Rows.OrderBy(r => r.Id).Skip(5).Take(5).Select(r => new object?[] { r.Id }).ToArray(), true);
        Cap(
            "paging_offset_limit",
            new Supported(new SqlRequest($"SELECT id FROM {Sales} ORDER BY id OFFSET 5 LIMIT 5"), page),
            new Supported(new GraphQlRequest("{ " + ListField + "(first: 5, offset: 5, orderBy: [{ id: ASC }]) { id } }", ListField), page),
            new Supported(new RestRequest(RobinClient.SalesRest + "?select=id&orderBy=id&limit=5&offset=5"), page));

        // ---- NULL handling ----
        Cap(
            "null_count_column",
            Scalar("COUNT(amount)", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Count, "amount")),
            ScalarGql("COUNT", "amount", ParityOracle.Aggregate(ParityOracle.Rows, ParityFn.Count, "amount")),
            noAgg);
        var emptyRows = ParityOracle.Rows.Where(r => r.Region == "Empty").ToList();
        Cap(
            "null_sum_of_only_nulls",
            Scalar("SUM(amount)", ParityOracle.Aggregate(emptyRows, ParityFn.Sum, "amount"), " WHERE region = 'Empty'"),
            ScalarGql("SUM", "amount", ParityOracle.Aggregate(emptyRows, ParityFn.Sum, "amount"), "region: { eq: \"Empty\" }"),
            noAgg);

        // ---- parameterised comparisons (#280 shape) ----
        var south = ParityOracle.Rows.Where(r => r.Region == "South").ToList();
        Cap(
            "param_aggregate_eq",
            new Supported(new SqlRequest($"SELECT SUM(amount) AS v FROM {Sales} WHERE region = @region", new { region = "South" }), One(ParityOracle.Aggregate(south, ParityFn.Sum, "amount"))),
            new Supported(
                new GraphQlRequest(
                    "query($r: String) { " + AggField + "(where: { region: { eq: $r } }, aggregates: [{ fn: SUM, column: amount, alias: \"v\" }]) { groups { rowCount aggregates { " + RobinClient.AggregateSelection + " } } } }",
                    AggField,
                    new { r = "South" }),
                One(ParityOracle.Aggregate(south, ParityFn.Sum, "amount"))),
            noAgg);
        Filter("param_eq", r => r.Region == "South", " WHERE region = @region", "region: { eq: $r }", null, new { region = "South" }, "($r: String)", new { r = "South" });
        Filter("param_compare_numeric", r => r.Qty > 6, " WHERE qty > @q", "qty: { gt: $q }", null, new { q = 6 }, "($q: Int)", new { q = 6 });
        Filter("param_in_list", r => r.Region is "North" or "Empty", " WHERE region IN (@a, @b)", "region: { in: $l }", null, new { a = "North", b = "Empty" }, "($l: [String!])", new { l = new[] { "North", "Empty" } });

        Capabilities = caps;
        Cells = cells;
    }

    /// <summary>The cells of the capability x channel product that the data lacks (empty when the matrix is complete).</summary>
    public static IReadOnlyList<(string Capability, MatrixChannel Channel)> Missing(
        IEnumerable<string> capabilities,
        IEnumerable<MatrixChannel> channels,
        IReadOnlyDictionary<(string Capability, MatrixChannel Channel), MatrixCell> cells) =>
        capabilities.SelectMany(_ => channels, (c, ch) => (c, ch)).Where(k => !cells.ContainsKey(k)).ToList();

    private static GraphQlRequest Agg(string args, string keys = "") =>
        new("{ " + AggField + "(" + args + ") { groups { " + (keys.Length > 0 ? "key { " + keys + " } " : string.Empty) + "rowCount aggregates { " + RobinClient.AggregateSelection + " } } } }", AggField);

    private static GraphQlRequest ListGql(string where, string? declaration = null, object? variables = null) =>
        new("query" + declaration + " { " + ListField + "(" + where + "first: 100, orderBy: [{ id: ASC }]) { id } }", ListField, variables);

    private static RestRequest RestList(string? filter) =>
        new(RobinClient.SalesRest + "?select=id&orderBy=id&limit=100" + (filter is null ? string.Empty : "&filter=" + Uri.EscapeDataString(filter)));
}
