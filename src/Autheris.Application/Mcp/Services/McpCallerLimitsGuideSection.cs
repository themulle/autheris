namespace Autheris.Application.Mcp.Services;

using System.Globalization;
using System.Text;
using Autheris.Domain.Model;

/// <summary>The "Your limits" section of the query guide: the caller's own effective limits and how to behave within them.</summary>
internal static class McpCallerLimitsGuideSection
{
    public static string Render(CallerQueryLimits limits)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Your limits");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"These are the effective limits of this token (tier `{limits.Tier}`). The source says why a value differs from the installation default: `installation`, `tier` or `scope`. The same values are in `{McpDatasetTools.DescribeMyAccess}` without arguments and in `GET /api/v1/me/limits`."));
        sb.AppendLine();
        Line(sb, "Concurrent reads per table", limits.Concurrency.ReadsPerTable, " (a further read waits, then answers 429)");
        Line(sb, "Wait for a free read slot (ms)", limits.Concurrency.TableReadWaitMs, "");
        Line(sb, "Concurrent queries", limits.Concurrency.QueriesAtOnce, "");
        Line(sb, "Query duration (s)", limits.MaxQueryDurationSeconds, "");
        Line(sb, "Result rows of the scope", limits.MaxResultRows, "");
        if (limits.Sql is { } sql)
        {
            Line(sb, "SQL rows without LIMIT", sql.DefaultRows, "");
            Line(sb, "SQL rows with LIMIT (maximum)", sql.MaxRowsWithLimit, "");
            Line(sb, "SQL offset (maximum)", sql.MaxOffset, "");
            Line(sb, "Values per list parameter", sql.MaxListValues, "");
            Line(sb, "List parameters per statement", sql.MaxListParameters, "");
            Line(sb, "SQL execution timeout (s)", sql.ExecutionTimeoutSeconds, "");
            Line(sb, "Page token lifetime (min)", sql.PageTokenLifetimeMinutes, "");
        }

        var gql = limits.GraphQl;
        Line(sb, "GraphQL rows per response", gql.MaxResponseRows, "");
        Line(sb, "GraphQL response bytes", gql.MaxResponseBytes, "");
        Line(sb, "GraphQL offset (maximum)", gql.MaxOffset, "");
        Line(sb, "GraphQL cost per query", gql.MaxCostPerQuery, "");

        sb.AppendLine();
        sb.AppendLine("How to behave:");
        sb.AppendLine("- Keep at most the concurrent-read value of requests per table in flight; queue the rest.");
        sb.AppendLine("- On `429` wait for `Retry-After` before the next attempt.");
        sb.AppendLine("- Page with cursors instead of a large `LIMIT` or offset.");
        sb.AppendLine("- Split key lists into batches of at most the list-parameter value.");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string name, LimitValue value, string note) =>
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **{name}:** {(value.Value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "no limit")} (source: {value.Source}){note}"));
}
