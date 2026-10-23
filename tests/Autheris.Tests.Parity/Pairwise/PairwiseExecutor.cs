using System.Text;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Matrix;
using Autheris.Tests.Parity.Robin;
using Autheris.Tests.Parity.Seed;

namespace Autheris.Tests.Parity.Pairwise;

/// <summary>Renders a <see cref="PairwiseCase"/> for WebSQL or GraphQL, runs it and compares with <see cref="ParityOracle"/> (or expects a stable rejection).</summary>
public sealed class PairwiseExecutor(IParityHost host, string schema = "main")
{
    private const string Table = ParitySeed.SalesTable;
    private const string Invalid = "AGGREGATION_INVALID_FUNCTION_FOR_TYPE";

    private readonly RobinClient _client = new(host);

    public static string CellCapability => "pairwise";

    public async Task RunAsync(PairwiseCase c, string channel)
    {
        if (c.Dialect == PairwiseModel.SqlServer)
        {
            throw new PendingCellException(channel, "pairwise_sqlserver_host_pending");
        }

        var web = channel == "WebSql";
        if (!web && c.Grouping == "expression")
        {
            throw new PendingCellException(channel, "group_by_expression_not_expressible");
        }

        if (web && c.Grouping is "year" or "month" or "day" && c.Dialect != PairwiseModel.Sqlite)
        {
            throw new PendingCellException(channel, "date_part_function_not_verified_for_dialect");
        }

        var result = web ? await ExecuteWebSqlAsync(c) : await ExecuteGraphQlAsync(c);

        if (PairwiseModel.IsInvalidCombination(c.Aggregate, c.Type))
        {
            result.Rejected(web ? null : Invalid);
            return;
        }

        if (web && c.Filter == "in_empty")
        {
            result.Rejected("SQL_SYNTAX_ERROR");
            return;
        }

        var (_, actual, rows) = Expected(c, web);
        result.RowsShouldBe(actual, rows);
    }

    /// <summary>Expected result rows of a valid case: <c>[key, v]</c> per group or one <c>[v]</c> row for an ungrouped aggregate.</summary>
    public static (string[] Columns, string[] ActualColumns, object?[][] Rows) Expected(PairwiseCase c, bool web)
    {
        var rows = ParityOracle.Rows.Where(PairwiseModel.Predicate(c.Filter)).ToList();
        var column = PairwiseModel.Column(c.Type);
        var fn = PairwiseModel.Fn(c.Aggregate);
        var key = PairwiseModel.GroupKey(c.Grouping);
        if (key is null)
        {
            return (["v"], ["v"], [[ParityOracle.Aggregate(rows, fn, column)]]);
        }

        var keyName = web ? "k" : c.Grouping is "year" or "month" or "day" ? "sale_date" : "region";
        return (["k", "v"], [keyName, "v"], ParityOracle.Grouped(rows, key, (fn, column)));
    }

    private Task<ChannelResult> ExecuteWebSqlAsync(PairwiseCase c)
    {
        var column = PairwiseModel.Column(c.Type);
        var agg = c.Aggregate switch
        {
            "count" => $"COUNT({column})",
            "count_distinct" => $"COUNT(DISTINCT {column})",
            _ => $"{c.Aggregate.ToUpperInvariant()}({column})",
        };
        var key = c.Grouping switch
        {
            "none" => null,
            "column" => "region",
            "year" => "strftime('%Y', sale_date)",
            "month" => "strftime('%Y-%m', sale_date)",
            "day" => "strftime('%Y-%m-%d', sale_date)",
            _ => "LOWER(region)",
        };
        var (where, parameters) = c.Filter switch
        {
            "none" => (string.Empty, null),
            "empty" => (" WHERE 1 = 1", null),
            "eq" => (" WHERE region = 'North'", null),
            "range" => (" WHERE qty >= 3 AND qty <= 7", null),
            "like" => (" WHERE note LIKE '%off%'", null),
            "is_null" => (" WHERE region IS NULL", null),
            "in_list" => (" WHERE product IN ('Widget', 'Gizmo')", null),
            "in_empty" => (" WHERE product IN ()", null),
            _ => (" WHERE region = @p", (object?)new { p = "South" }),
        };
        var sql = key is null
            ? $"SELECT {agg} AS v FROM {Table}{where}"
            : $"SELECT {key} AS k, {agg} AS v FROM {Table}{where} GROUP BY {key}";
        return _client.WebSqlAsync(sql, parameters);
    }

    private Task<ChannelResult> ExecuteGraphQlAsync(PairwiseCase c)
    {
        var field = $"default_{schema}_{Table}_aggregate";
        var column = PairwiseModel.Column(c.Type);
        var fn = c.Aggregate.ToUpperInvariant();
        var args = new List<string>();
        var keys = string.Empty;
        switch (c.Grouping)
        {
            case "column":
                args.Add("groupBy: [region]");
                keys = "region";
                break;
            case "year" or "month" or "day":
                args.Add($"timeBuckets: [{{ column: sale_date, bucket: {c.Grouping.ToUpperInvariant()} }}]");
                keys = "sale_date";
                break;
        }

        string? declaration = null;
        object? variables = null;
        var where = c.Filter switch
        {
            "none" => null,
            "empty" => string.Empty,
            "eq" => "region: { eq: \"North\" }",
            "range" => "qty: { gte: 3, lte: 7 }",
            "like" => "note: { contains: \"off\" }",
            "is_null" => "region: { isNull: true }",
            "in_list" => "product: { in: [\"Widget\", \"Gizmo\"] }",
            "in_empty" => "product: { in: [] }",
            _ => "region: { eq: $p }",
        };
        if (c.Filter == "param")
        {
            declaration = "($p: String)";
            variables = new { p = "South" };
        }

        if (where is not null)
        {
            args.Add($"where: {{ {where} }}");
        }

        args.Add($"aggregates: [{{ fn: {fn}, column: {column}, alias: \"v\" }}]");
        var query = new StringBuilder("query").Append(declaration).Append(" { ").Append(field).Append('(').Append(string.Join(", ", args)).Append(") { groups { ")
            .Append(keys.Length > 0 ? $"key {{ {keys} }} " : string.Empty)
            .Append("rowCount aggregates { ").Append(RobinClient.AggregateSelection).Append(" } } } }");
        return _client.GraphQlAsync(query.ToString(), field, variables);
    }
}
