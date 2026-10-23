using Autheris.Tests.Parity.Matrix;
using Autheris.Tests.Parity.Pairwise;

namespace Autheris.Tests.Parity.Differential;

/// <summary>
/// Query corpus of block E, derived from the matrix (block A) and the pairwise generator (block B): every question the WebSQL channel
/// answers is also posed to SQLite directly; GraphQL is compared where the matrix expresses the same question.
/// </summary>
public static class DifferentialCorpus
{
    /// <summary>Matrix capabilities whose WebSQL text is not valid SQLite (so there is no reference); reason documented here.</summary>
    public static readonly IReadOnlyDictionary<string, string> NoReference = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["group_date_part_standard_sql"] = "SQLite has no EXTRACT; the year grouping is covered by group_date_year",
    };

    /// <summary>SQLite spelling where the WebSQL text differs (paging clause order).</summary>
    private static readonly IReadOnlyDictionary<string, string> ReferenceSql = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["paging_offset_limit"] = "SELECT id FROM parity_sales ORDER BY id LIMIT 5 OFFSET 5",
    };

    public static IReadOnlyList<DifferentialCase> Cases() => [.. Matrix(), .. Pairwise()];

    private static IEnumerable<DifferentialCase> Matrix()
    {
        foreach (var capability in ParityMatrix.Capabilities)
        {
            if (NoReference.ContainsKey(capability) || ParityMatrix.Cells[(capability, MatrixChannel.WebSql)] is not Supported sql)
            {
                continue;
            }

            var request = (SqlRequest)sql.Request;
            var reference = ReferenceSql.TryGetValue(capability, out var text) ? text : request.Sql;
            yield return new DifferentialCase(
                $"matrix.{capability}", capability, "WebSql", reference, request.Parameters, request, sql.Expected.Ordered, null, MatrixKnownGaps.For(capability, MatrixChannel.WebSql), DateGrouping: capability.StartsWith("group_date_", StringComparison.Ordinal));

            if (ParityMatrix.Cells[(capability, MatrixChannel.GraphQl)] is Supported gql && (gql.ActualColumns ?? gql.Expected.Columns).Length == sql.Expected.Columns.Length)
            {
                yield return new DifferentialCase(
                    $"matrix.{capability}", capability, "GraphQl", reference, request.Parameters, gql.Request, sql.Expected.Ordered && gql.Expected.Ordered, gql.ActualColumns ?? gql.Expected.Columns, MatrixKnownGaps.For(capability, MatrixChannel.GraphQl), DateGrouping: capability.StartsWith("group_date_", StringComparison.Ordinal));
            }
        }
    }

    private static bool IsDateGrouping(string grouping) => grouping is not ("none" or "column" or "expression");

    private static IEnumerable<DifferentialCase> Pairwise()
    {
        foreach (var c in PairwiseModel.Cases().Where(x => x.Dialect == PairwiseModel.Sqlite))
        {
            var (sql, parameters) = PairwiseExecutor.RenderWebSql(c);
            yield return new DifferentialCase(
                $"pairwise.{c.Id}", "pairwise", "WebSql", sql, parameters, new SqlRequest(sql, parameters), false, null, null, PairwiseModel.IsInvalidCombination(c.Aggregate, c.Type), IsDateGrouping(c.Grouping));

            if (c.Grouping == "expression")
            {
                continue;
            }

            var (query, field, variables) = PairwiseExecutor.RenderGraphQl(c);
            string[] actual = c.Grouping switch
            {
                "none" => ["v"],
                "column" => ["region", "v"],
                _ => ["sale_date", "v"],
            };
            yield return new DifferentialCase(
                $"pairwise.{c.Id}", "pairwise", "GraphQl", sql, parameters, new GraphQlRequest(query, field, variables), false, actual, null, PairwiseModel.IsInvalidCombination(c.Aggregate, c.Type), IsDateGrouping(c.Grouping));
        }
    }
}
