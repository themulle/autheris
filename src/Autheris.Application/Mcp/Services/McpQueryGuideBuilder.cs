namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autheris.Application.Analytics;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Options;

/// <summary>
/// Builds the agent query guide dynamically from the active startup switches and GraphQL options,
/// matching <see cref="McpAccessHints"/>: null switches fail closed for tool names, and disabled
/// features or tools are never named.
/// </summary>
internal static partial class McpQueryGuideBuilder
{
    public static string Build(IReadPathSwitches? switches, GraphQLOptions? graphQlOptions = null, AggregationOptions? aggregationOptions = null, WebSqlOptions? webSqlOptions = null)
    {
        var gql = graphQlOptions ?? new GraphQLOptions();
        var agg = aggregationOptions ?? new AggregationOptions();
        var sb = new StringBuilder();

        sb.AppendLine("# Autheris Agent Query Guide");
        sb.AppendLine();
        sb.AppendLine("This guide describes the gateway's GraphQL query dialect and the query features currently enabled on this instance.");
        sb.AppendLine();

        AppendExecutionPaths(sb, switches, gql);
        AppendPagingAndLimits(sb, gql);
        AppendOrdering(sb);
        AppendFilterOperators(sb);

        AppendAggregationAndTimeBuckets(sb, gql, agg);
        AppendMaskedColumnsAndRestrictedScopes(sb, switches);
        AppendMetrics(sb);
        AppendToolErrors(sb);

        if (gql.Subscriptions.Enabled)
        {
            AppendSubscriptions(sb, gql);
        }

        AppendSql(sb, switches, webSqlOptions);

        return sb.ToString();
    }

    private static bool Tool(IReadPathSwitches? switches, string name) =>
        switches is not null && switches.IsToolEnabled(name);

    private static void AppendExecutionPaths(StringBuilder sb, IReadPathSwitches? switches, GraphQLOptions gql)
    {
        sb.AppendLine("## 1. Execution Paths & Discovery");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **GraphQL Endpoint:** `POST {gql.EndpointPath}`"));

        if (Tool(switches, McpDatasetTools.QueryGraphQl))
        {
            sb.AppendLine($"- **MCP GraphQL Tool:** `{McpDatasetTools.QueryGraphQl}` executes governed GraphQL queries directly.");
        }

        var discoveryTools = new List<string>();
        if (Tool(switches, McpDatasetTools.FindRelevantTables)) discoveryTools.Add($"`{McpDatasetTools.FindRelevantTables}`");
        if (Tool(switches, McpDatasetTools.SearchCatalog)) discoveryTools.Add($"`{McpDatasetTools.SearchCatalog}`");
        if (Tool(switches, McpDatasetTools.ListDatasets)) discoveryTools.Add($"`{McpDatasetTools.ListDatasets}`");
        if (discoveryTools.Count > 0)
        {
            sb.AppendLine($"- **Dataset Discovery:** Use {string.Join(", ", discoveryTools)} to find datasets and their GraphQL field names.");
        }

        var schemaTools = new List<string>();
        if (Tool(switches, McpDatasetTools.GetTableSchema)) schemaTools.Add($"`{McpDatasetTools.GetTableSchema}`");
        if (Tool(switches, McpDatasetTools.DescribeDataset)) schemaTools.Add($"`{McpDatasetTools.DescribeDataset}`");
        if (schemaTools.Count > 0)
        {
            sb.AppendLine($"- **Schema Inspection:** Use {string.Join(", ", schemaTools)} to inspect columns, filter/sort types, and relations.");
        }

        if (Tool(switches, McpDatasetTools.QuerySql))
        {
            sb.AppendLine($"- **SQL Query Tool:** `{McpDatasetTools.QuerySql}` executes governed SQL queries. Pass values in `parameters` bound to `@name` placeholders (`IN (@ids)` for a list); a statement rejected with `SQL_LITERAL_NOT_PERMITTED` is resent with the value as a parameter; a `cursor` call repeats the same `parameters`.");
            sb.AppendLine("- **Optional Filters:** `(@p IS NULL OR col = @p)` is an optional filter: JSON `null` drops it, an absent parameter is an error.");
            sb.AppendLine("- **Timestamp Parameters:** Date-time strings may end in `Z` or `+HH:mm`; they are converted to UTC.");
        }

        if (switches is not null && switches.RestDataEndpointEnabled && Tool(switches, McpDatasetTools.QueryDataset))
        {
            sb.AppendLine($"- **REST Data Tool:** `{McpDatasetTools.QueryDataset}` queries individual datasets over the REST data contract.");
        }

        if (Tool(switches, McpDatasetTools.CallProcedure))
        {
            sb.AppendLine($"- **Stored Procedures:** `{McpDatasetTools.CallProcedure}` invokes governed stored procedures.");
        }

        sb.AppendLine();
    }

    private static void AppendPagingAndLimits(StringBuilder sb, GraphQLOptions gql)
    {
        sb.AppendLine("## 2. Paging, Connections & Operation Limits");
        sb.AppendLine();
        sb.AppendLine("Collection queries use Relay-style keyset cursor connections (`<tableField>Connection`) for O(1) seek pagination without full sorts:");
        sb.AppendLine();
        sb.AppendLine("```graphql");
        sb.AppendLine("query ($after: String) {");
        sb.AppendLine("  ordersConnection(first: 100, after: $after, orderBy: [{ id: ASC }]) {");
        sb.AppendLine("    edges {");
        sb.AppendLine("      cursor");
        sb.AppendLine("      node { id status amount }");
        sb.AppendLine("    }");
        sb.AppendLine("    pageInfo { hasNextPage hasPreviousPage startCursor endCursor }");
        sb.AppendLine("    totalCount");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **Page Size (`first`):** Defaults to `100`; maximum `{gql.MaxResponseRows}` rows per response."));
        sb.AppendLine("- **Total (`totalCount`):** Rows matching `where` under your row rules, independent of `first`/`after`. It runs an extra count statement and adds to the query cost, so select it only when needed (for example on the first page). It is `null` when the count could not be determined.");
        sb.AppendLine("- **Keyset Cursor (`after`):** Pass `pageInfo.endCursor` from the previous response. Cursors are HMAC-signed and bound to the query's `orderBy` key.");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **Offset Bound:** `MaxAllowedOffset` is `{gql.MaxAllowedOffset}`."));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **Operation Bounds:** Max depth `{gql.MaxAllowedExecutionDepth}`, max complexity `{gql.MaxAllowedComplexity}`, max root fields `{gql.MaxRootFieldsPerOperation}`."));
        sb.AppendLine(gql.PersistedQueriesOnly
            ? "- **Persisted Queries Only:** Enabled — only registered persisted queries / trusted documents are accepted."
            : "- **Ad-Hoc Queries:** Enabled — ad-hoc GraphQL operations are accepted within complexity and depth bounds.");
        sb.AppendLine(gql.EnableIntrospection
            ? "- **Schema Introspection:** Enabled (`__schema`, `__type`)."
            : "- **Schema Introspection:** Disabled (`__schema` and `__type` are blocked).");
        sb.AppendLine();
    }

    private static void AppendOrdering(StringBuilder sb)
    {
        sb.AppendLine("## 3. Key Order & Sorting (`orderBy`)");
        sb.AppendLine();
        sb.AppendLine("- Pass `orderBy` as a list of single-field sort objects using `AutherisSortDirection` (`ASC` or `DESC`), e.g. `orderBy: [{ createdAt: DESC }, { id: ASC }]`.");
        sb.AppendLine("- On `*Connection` fields, `orderBy` is validated against the table's keyset/index column prefix so seek pagination remains deterministic.");
        sb.AppendLine("- Always include a unique tie-breaker column (such as the primary key) at the end of `orderBy` when paging with `after`.");
        sb.AppendLine();
    }

    private static void AppendFilterOperators(StringBuilder sb)
    {
        sb.AppendLine("## 4. Filter Operators (`where`)");
        sb.AppendLine();
        sb.AppendLine("Filter predicates use typed `where` input objects with the following operators:");
        sb.AppendLine();
        sb.AppendLine("- **Comparison:** `eq`, `neq`, `gt`, `gte`, `lt`, `lte`");
        sb.AppendLine("- **Set Membership:** `in`, `nin` (pass a non-null list)");
        sb.AppendLine("- **Nullability:** `isNull` (`true` or `false`)");
        sb.AppendLine("- **String Matching:** `contains`, `startsWith`, `endsWith`");
        sb.AppendLine("- **Logical Combinators:** `and: [...]`, `or: [...]`, `not: { ... }`");
        sb.AppendLine();
        sb.AppendLine("```graphql");
        sb.AppendLine("where: {");
        sb.AppendLine("  and: [");
        sb.AppendLine("    { status: { in: [\"PAID\", \"SHIPPED\"] } }");
        sb.AppendLine("    { amount: { gte: 100 } }");
        sb.AppendLine("    { cancelledAt: { isNull: true } }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void AppendAggregationAndTimeBuckets(StringBuilder sb, GraphQLOptions gql, AggregationOptions agg)
    {
        sb.AppendLine("## 5. Aggregate Fields & Time Buckets (`_aggregate`)");
        sb.AppendLine();
        sb.AppendLine("Typed governed aggregation is exposed as `<tableField>_aggregate(groupBy, timeBuckets, aggregates, where, orderBy, limit)` with k-anonymity enforcement:");
        sb.AppendLine();
        sb.AppendLine("- **Aggregate Functions (`AggregateFn`):** `COUNT`, `COUNT_DISTINCT`, `SUM`, `AVG`, `MIN`, `MAX`");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **Time Buckets (`timeBuckets`):** `{{ column: <timestampCol>, bucket: HOUR | DAY | WEEK | MONTH | QUARTER | YEAR, timeZone: \"{agg.DefaultTimeZone}\" }}`; `timeZone` defaults to `\"{agg.DefaultTimeZone}\"` when omitted."));
        sb.AppendLine("- **Aggregate Ordering (`orderBy`):** `{ column: <col> | alias: \"<alias>\", direction: ASC | DESC }`");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **Aggregation Bounds:** Max `{gql.Aggregation.MaxFieldsPerOperation}` aggregate selections per operation; `MaxAggregateRowBudget` is `{gql.MaxAggregateRowBudget}` rows."));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- **Time-Bucket Limit:** With `timeBuckets`, `limit` must not exceed `{AggregationOptions.MaxTimeBucketedLimit}` groups; a larger value is refused with `{AggregationErrorCodes.TimeBucketLimitExceeded}` before any SQL runs. Lower `limit`, narrow `where`, or use a coarser bucket."));
        sb.AppendLine("- **K-Anonymity Metadata:** Results return `groups { key { ... } rowCount isMergedOther aggregates { alias kind intValue decimalValue floatValue stringValue dateTimeValue } }`, plus `count`, `suppressed`, `suppressedGroupsCount`, `minimumGroupSize`, `minimumGroupSizeSource`, `mergedIntoOther`, and `truncated`.");
        sb.AppendLine();
        sb.AppendLine("```graphql");
        sb.AppendLine("query {");
        sb.AppendLine("  orders_aggregate(");
        sb.AppendLine("    groupBy: [status]");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"    timeBuckets: [{{ column: createdAt, bucket: DAY, timeZone: \"{agg.DefaultTimeZone}\" }}]"));
        sb.AppendLine("    aggregates: [{ fn: SUM, column: amount, alias: \"total_amount\" }, { fn: COUNT, alias: \"order_count\" }]");
        sb.AppendLine("    orderBy: [{ alias: \"total_amount\", direction: DESC }]");
        sb.AppendLine("    limit: 50");
        sb.AppendLine("  ) {");
        sb.AppendLine("    groups { key { status createdAt } rowCount isMergedOther aggregates { alias kind decimalValue intValue } }");
        sb.AppendLine("    suppressed minimumGroupSize mergedIntoOther truncated");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void AppendMaskedColumnsAndRestrictedScopes(StringBuilder sb, IReadPathSwitches? switches)
    {
        sb.AppendLine("## 6. Masked Columns, Keys & Restricted Scopes");
        sb.AppendLine();
        sb.AppendLine("- **Masked Columns (`access: mask`):** Values come back masked. A masked column cannot be used in `where`, `orderBy` or a join (`FILTER_ON_MASKED_COLUMN`), nor grouped or aggregated (`AGGREGATION_ON_MASKED_COLUMN`).");
        sb.AppendLine("- **Keys Over Masked Columns:** The primary key, key order and indexes are listed even when a key column is masked, so a query can be planned along the key (for example a query that an app later runs under a scope where those columns are clear). Under this token, filter, order and join only on clear columns; a key or index over a denied or hidden column is not listed.");
        if (Tool(switches, McpDatasetTools.DescribeMyAccess))
        {
            sb.AppendLine($"- **Restricted Scopes:** `{McpDatasetTools.DescribeMyAccess}` returns `restrictedScope: true` when this token carries a restricted scope (for example an agent scope). A column in `scopeClearRequiredColumns` is masked only for the restricted scope of this token; the same person reads it as the base rule says (for example in an app that runs with the person's own scope), so no access request is needed for it. Do not ask the person to request access for these columns.");
        }

        sb.AppendLine();
    }

    private static void AppendMetrics(StringBuilder sb)
    {
        sb.AppendLine("## Semantic Metrics (`queryMetrics`)");
        sb.AppendLine();
        sb.AppendLine("- The `queryMetrics` root field is enabled for querying governed semantic metrics.");
        sb.AppendLine();
    }

    private static void AppendToolErrors(StringBuilder sb)
    {
        sb.AppendLine("## Tool Errors");
        sb.AppendLine();
        sb.AppendLine("A failed tool call is a result with `isError: true`. Its text and `structuredContent` are `{\"error\":{\"code\",\"message\"}}` with the optional members `detail`, `hint`, `line`, `column`, `limit`, `estimatedCost`, `maxAllowedCost` and `retryAfterSeconds`. The `message` is the fixed text of the `code`; act on the code:");
        sb.AppendLine();
        foreach (var (code, action) in McpErrorCodes.AgentActionable)
        {
            sb.Append("- `").Append(code).Append("`: ").AppendLine(action);
        }

        sb.AppendLine();
    }

    private static void AppendSubscriptions(StringBuilder sb, GraphQLOptions gql)
    {
        sb.AppendLine("## GraphQL Subscriptions");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"- GraphQL subscriptions are enabled (max `{gql.MaxSubscriptionsPerSubject}` per subject, `{gql.MaxSubscriptionsPerTenant}` per tenant)."));
        sb.AppendLine();
    }
}
