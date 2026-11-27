namespace Autheris.Application.Mcp.Services;

using System.Globalization;
using System.Text;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Sql;
using Autheris.Domain.Options;

/// <summary>The WebSQL section of the agent query guide. Rendered only while WebSQL is enabled.</summary>
internal static partial class McpQueryGuideBuilder
{
    /// <summary>The sentence that states the single supported form of a row limit; tests pin it verbatim.</summary>
    internal const string LimitLiteralSentence =
        "`LIMIT`, `OFFSET` and `FETCH` take integer literals only; a parameter in their place is not supported. Use `page.size` to choose the rows per call.";

    private static void AppendSql(StringBuilder sb, IReadPathSwitches? switches, WebSqlOptions? webSql)
    {
        if (webSql is not { Enabled: true })
        {
            return;
        }

        sb.AppendLine("## SQL Queries (WebSQL)");
        sb.AppendLine();
        sb.AppendLine("Governed SQL runs under the caller's row filters, column masks and consent, like GraphQL.");
        sb.AppendLine();

        AppendSqlEntryPoints(sb, switches);
        AppendSqlDialect(sb, webSql);
        AppendSqlParameters(sb, webSql);
        AppendSqlPaging(sb, webSql);
        AppendSqlRefusals(sb);
        AppendSqlTimeStamps(sb);
        AppendSqlLimitsAndResponse(sb, webSql);
        AppendSqlErrors(sb);
        AppendSqlExample(sb);
    }

    private static void AppendSqlEntryPoints(StringBuilder sb, IReadPathSwitches? switches)
    {
        sb.AppendLine("### Entry Points");
        sb.AppendLine("- **HTTP:** `POST /api/v1/sql` with a JSON body `{ \"sql\", \"parameters\", \"dataSource\", \"page\", \"next\", \"resultCache\" }`, or the statement alone as `text/plain` or `application/sql`. The OpenAPI document is `GET /api/v1/sql/openapi.json`.");
        if (Tool(switches, McpDatasetTools.QuerySql))
        {
            sb.AppendLine($"- **MCP Tool:** `{McpDatasetTools.QuerySql}` takes `query` and optional `limit` and `cursor`; it takes no `parameters`. While `hasMore` is true, call it again with the returned `cursor`.");
        }

        if (Tool(switches, McpDatasetTools.ListDatasets))
        {
            sb.AppendLine($"- **Table Names:** `{McpDatasetTools.ListDatasets}` returns each table's `sqlName` (`<catalog>.<schema>.<table>`).");
        }

        sb.AppendLine();
    }

    private static void AppendSqlDialect(StringBuilder sb, WebSqlOptions webSql)
    {
        sb.AppendLine("### Dialect & Names");
        sb.Append("- Trino grammar, one statement per request. Only `SELECT` is accepted.");
        if (webSql.AllowDml)
        {
            sb.Append(" `INSERT`, `UPDATE` and `DELETE` are enabled on this instance and are governed like reads.");
        }

        sb.AppendLine();
        sb.AppendLine("- Use `LIMIT n`, not `TOP n`. Name tables `<dataSource>.<schema>.<table>`, or `<schema>.<table>` in the request's `dataSource` (or the gateway's default data source). Physical database- or server-qualified names are refused (`INVALID_IDENTIFIER_QUALIFICATION`).");
        sb.AppendLine();
    }

    private static void AppendSqlParameters(StringBuilder sb, WebSqlOptions webSql)
    {
        sb.AppendLine("### Parameters");
        sb.AppendLine("- Write `@name` in the statement and pass the value in `parameters` under `name` or `@name`. Names ignore case; two spellings of one name are refused (400 `BAD_REQUEST`). Inside a string literal or a quoted identifier, `@name` is text.");
        sb.AppendLine("- Values are string, number, boolean or null; an object is refused (400). Every `@name` in the statement needs a value.");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- **Lists:** a JSON array is a list parameter. It is allowed only as the single item of `IN (@ids)` or `NOT IN (@ids)` (`SQL_LIST_PARAM_POSITION`). All elements have one type: string, number or boolean (`SQL_LIST_PARAM_TYPE`). No element is null (`SQL_LIST_PARAM_NULL`). At most {webSql.MaxListParameterValues} distinct values (fewer on some data sources) and {webSql.MaxListParameters} lists per request (`SQL_LIST_PARAM_TOO_LARGE`).");
        sb.AppendLine("- **Reserved names:** parameter names starting with `__` or with another gateway-internal prefix are reserved and refused (403 `SQL_POLICY_VIOLATION`).");
        sb.Append("- ").AppendLine(LimitLiteralSentence);
        sb.AppendLine();
    }

    private static void AppendSqlPaging(StringBuilder sb, WebSqlOptions webSql)
    {
        sb.AppendLine("### Paging (`page`, `next`)");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Request a page with `\"page\": {{ \"size\": n }}` (1 to {webSql.MaxAllowedRows}). The answer carries `page` (`number`, `offset`, `size`) and, while more rows exist, `next`.");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- For the following page send the same `sql`, `parameters` and `dataSource` with `\"next\": \"<token>\"`. `page` may be left out; if sent, its `size` must match. The token is signed and bound to the statement, the parameter values, the caller, the tenant, the data source and the transport, and it expires after {webSql.Paging.TokenLifetimeMinutes} minutes. Otherwise: 400 `SQL_PAGING_TOKEN_INVALID`.");
        sb.AppendLine("- A paged statement needs a top-level `ORDER BY` and no `LIMIT` or `OFFSET` of its own. End the `ORDER BY` with a unique column (for example the primary key); the gateway adds no ordering.");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- `pagingUnavailable` says why there is no `next`: `order_by_required`, `statement_has_limit` or `federated` (the rows come back unpaged under the normal row limit); `offset_limit` (the next offset would exceed {webSql.Paging.MaxOffset}); `caller_not_bindable` (the caller has no stable identity).");
        sb.AppendLine("- Paging is refused together with `wait_timeout` and for statements other than `SELECT` (400 `SQL_PAGING_UNSUPPORTED`).");
        sb.AppendLine();
    }

    private static void AppendSqlRefusals(StringBuilder sb)
    {
        sb.AppendLine("### What Is Refused");
        sb.AppendLine("- **Masked columns** come back masked (`columns[].masked: true`). They cannot be used in `WHERE`, `JOIN ... ON` or `ORDER BY` (403 `FILTER_ON_MASKED_COLUMN`), nor grouped or aggregated (403 `AGGREGATION_ON_MASKED_COLUMN`).");
        sb.AppendLine("- **Functions:** only functions the gateway permits for the data source's dialect; any other function is refused (403 `SQL_POLICY_VIOLATION`).");
        sb.AppendLine("- **Comments:** `--` and `/* */` are accepted and never sent to the data source; a nested block comment is a syntax error. Optimizer hints are not passed on.");
        sb.AppendLine();
    }

    private static void AppendSqlTimeStamps(StringBuilder sb)
    {
        sb.AppendLine("### Time Stamps & Time Zones");
        sb.AppendLine("- Date-time values are written in ISO 8601 with a `Z` suffix, for example `2026-03-01T12:00:00Z`. Values of `timestamp with time zone` columns are converted to UTC; values without a zone are written as stored, also with `Z`.");
        sb.AppendLine("- `convert_timezone(from_zone, to_zone, ts)` and `convert_timezone(to_zone, zoned_ts)` return the wall-clock time in `to_zone`. Zones are IANA names (`Europe/Berlin`), given as string literals or scalar parameters. The result is a `timestamp` without zone, so it is also written with `Z`, although it is local time in `to_zone`. At most 8 calls per statement. Errors: `SQL_TIMEZONE_INVALID` (400), `SQL_TIMEZONE_ARGUMENT` (422), `SQL_FUNCTION_UNSUPPORTED_DIALECT` (422).");
        sb.AppendLine();
    }

    private static void AppendSqlLimitsAndResponse(StringBuilder sb, WebSqlOptions webSql)
    {
        sb.AppendLine("### Limits & Response");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Statements up to {webSql.MaxQueryLength} characters; request bodies up to 2 MB. Execution stops after {webSql.ExecutionTimeoutSeconds} s (504 `SQL_TIMEOUT`).");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Without `LIMIT` at most {webSql.DefaultMaxRows} rows; a `LIMIT` above {webSql.MaxAllowedRows} is capped. `truncated: true` (and the header `X-Autheris-Truncated: true`) means that more rows existed.");
        sb.AppendLine("- Response: `columns` (`name`, `type` as a Trino type name such as `integer`, `varchar`, `decimal(12,2)` or `timestamp(3) with time zone`, `nullable`, `masked`), `rows` (objects by column name; arrays in column order with `?format=arrays`), `rowCount`, `truncated`, and when present `page`, `next`, `pagingUnavailable`, `resultCache` and `indexHints`. `indexHints` lists tables read without a usable index (`reason: NO_USABLE_INDEX`) and the columns an index would help with.");
        sb.AppendLine();
    }

    private static void AppendSqlErrors(StringBuilder sb)
    {
        sb.AppendLine("### Errors");
        sb.AppendLine("- Errors are `application/problem+json`: `type` (`urn:autheris:error:<CODE>`), `title`, `status`, `detail`, `code`, `traceId`, `correlationId`, `instance`; some also carry the optional members `line`, `column`, `limit`, `hint` or `retryAfterSeconds`. Branch on `code`, never on `detail`.");
        sb.AppendLine("- Common codes: 400 `SQL_SYNTAX_ERROR`, `SQL_PARAMETER_MISSING`, `SQL_LIST_PARAM_*`, `SQL_PAGING_TOKEN_INVALID`; 403 `SQL_POLICY_DENIED`, `SQL_POLICY_VIOLATION`, `FILTER_ON_MASKED_COLUMN`, `AGGREGATION_ON_MASKED_COLUMN`; 422 `SQL_CONSTRUCT_NOT_SUPPORTED`, `SQL_LIMIT_EXCEEDED`; 429 `RATE_LIMIT_EXCEEDED` (with `Retry-After`); 504 `SQL_TIMEOUT`.");
        sb.AppendLine();
    }

    private static void AppendSqlExample(StringBuilder sb)
    {
        sb.AppendLine("### Example: A Stored Statement Run With Values (HTTP)");
        sb.AppendLine("The statement text stays fixed; only `parameters` change between runs (demo data set):");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(WebSqlGuideExample.RequestJson);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("For the next page send the same body with `\"next\": \"<value of next>\"` instead of `page`.");
    }
}
