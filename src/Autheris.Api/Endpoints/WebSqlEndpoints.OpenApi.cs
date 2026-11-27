namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using Autheris.Application.Sql;
using Autheris.Domain.Options;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

public static partial class WebSqlEndpoints
{
    private static readonly string[] ScalarTypes = ["string", "number", "boolean"];

    /// <summary>
    /// Serves the WebSQL OpenAPI document. While WebSQL is disabled (or its options are absent) the answer is the plain
    /// not-found result of an unknown route, so the document never describes a disabled endpoint.
    /// </summary>
    internal static IResult GetOpenApiSpec(GatewayOptions? options) =>
        options?.WebSql is { Enabled: true }
            ? Results.Json(BuildOpenApiSpec(options))
            : Results.NotFound();

    /// <summary>Marks the OpenAPI route so <see cref="DisabledOpenApiGuardAsync"/> can answer for it while WebSQL is disabled.</summary>
    internal sealed class OpenApiRouteMetadata
    {
        internal static readonly OpenApiRouteMetadata Instance = new();

        private OpenApiRouteMetadata()
        {
        }
    }

    /// <summary>
    /// Runs after authorization. While WebSQL is disabled an authenticated caller gets the bare 404 an unmatched route gets,
    /// ending the request before the governance, resource-group and egress headers that only matched endpoints add.
    /// </summary>
    internal static Task DisabledOpenApiGuardAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<OpenApiRouteMetadata>() is not null
            && context.RequestServices.GetService(typeof(IOptionsMonitor<GatewayOptions>)) is IOptionsMonitor<GatewayOptions> live
            && live.CurrentValue.WebSql is not { Enabled: true })
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static Dictionary<string, object> Obj(params (string Key, object Value)[] members)
    {
        var map = new Dictionary<string, object>(members.Length);
        foreach (var (key, value) in members)
        {
            map[key] = value;
        }

        return map;
    }

    private static Dictionary<string, object> Ref(string schema) => Obj(("$ref", "#/components/schemas/" + schema));

    private static Dictionary<string, object> Prim(string type, string? description = null)
    {
        var map = Obj(("type", type));
        if (description is not null)
        {
            map["description"] = description;
        }

        return map;
    }

    private static Dictionary<string, object> ScalarSchemas()
    {
        var one = new List<object>(ScalarTypes.Length);
        foreach (var type in ScalarTypes)
        {
            one.Add(Prim(type));
        }

        return Obj(("oneOf", one));
    }

    /// <summary>Paging schemas of the request and the result. The only place that describes paging in this document.</summary>
    private static void AddPagingSchemas(Dictionary<string, object> schemas, WebSqlOptions webSql)
    {
        schemas["WebSqlPageRequest"] = Obj(
            ("type", "object"),
            ("required", new[] { "size" }),
            ("properties", Obj(("size", Obj(
                ("type", "integer"),
                ("minimum", 1),
                ("maximum", webSql.MaxAllowedRows),
                ("description", "Rows per page. A paged statement needs a top-level ORDER BY ending in a unique column and no LIMIT or OFFSET of its own."))))));

        schemas["WebSqlNext"] = Obj(
            ("type", "string"),
            ("maxLength", 512),
            ("description",
                "Opaque signed token from a previous answer. Send it with the same sql, parameters and dataSource to get the following page. " +
                $"It is bound to the statement, the parameter values, the caller, the tenant, the data source and the transport, and expires after {webSql.Paging.TokenLifetimeMinutes} minutes. " +
                "page may be left out; if sent, its size must match."));

        schemas["WebSqlPage"] = Obj(
            ("type", "object"),
            ("required", new[] { "number", "offset", "size" }),
            ("properties", Obj(
                ("number", Prim("integer", "1-based page number.")),
                ("offset", Prim("integer", "Rows skipped before this page.")),
                ("size", Prim("integer", "Rows per page.")))));

        schemas["WebSqlPagingUnavailable"] = Obj(
            ("type", "string"),
            ("enum", new[] { "federated", "statement_has_limit", "order_by_required", "offset_limit", "caller_not_bindable" }),
            ("description",
                "Why the answer has no next token. order_by_required, statement_has_limit and federated: the rows came back unpaged under the normal row limit. " +
                $"offset_limit: the next offset would exceed {webSql.Paging.MaxOffset}. caller_not_bindable: the caller has no stable identity."));
    }

    private static Dictionary<string, object> BuildSchemas(WebSqlOptions webSql)
    {
        var schemas = new Dictionary<string, object>();
        AddPagingSchemas(schemas, webSql);

        var listItems = Obj(
            ("type", "array"),
            ("maxItems", webSql.MaxListParameterValues),
            ("items", ScalarSchemas()));

        var parameterValue = Obj(("oneOf", new List<object>
        {
            Prim("string"), Prim("number"), Prim("boolean"), listItems
        }));
        parameterValue["nullable"] = true;

        schemas["WebSqlRequest"] = Obj(
            ("type", "object"),
            ("required", new[] { "sql" }),
            ("properties", Obj(
                ("sql", Obj(
                    ("type", "string"),
                    ("description", "A single SELECT statement. Name tables <dataSource>.<schema>.<table>. Parameters are @name. LIMIT takes an integer literal."),
                    ("example", WebSqlGuideExample.Sql))),
                ("parameters", Obj(
                    ("type", "object"),
                    ("description",
                        "Values for the @name placeholders, keyed by name or @name; names ignore case and two spellings of one name are refused. " +
                        "A value is a string, number, boolean or null. A JSON array is a list parameter, allowed only as the single item of IN (@name) or NOT IN (@name), " +
                        $"with one element type, no null, at most {webSql.MaxListParameterValues} values and {webSql.MaxListParameters} lists per request. " +
                        "Names starting with a double underscore or another gateway-internal prefix are reserved and refused (403 SQL_POLICY_VIOLATION)."),
                    ("additionalProperties", parameterValue),
                    ("example", System.Text.Json.Nodes.JsonNode.Parse(WebSqlGuideExample.ParametersJson)!))),
                ("dataSource", Prim("string", "Data source to run on. Default: the gateway's default data source.")),
                ("page", Ref("WebSqlPageRequest")),
                ("next", Ref("WebSqlNext")),
                ("resultCache", Obj(
                    ("type", "string"),
                    ("enum", new[] { "default", "bypass" }),
                    ("description", "Result cache use (only effective when the gateway result cache is enabled). 'bypass' skips the lookup and refreshes the caller's own entry."))))));

        schemas["WebSqlResult"] = Obj(
            ("type", "object"),
            ("required", new[] { "columns", "rows", "rowCount", "truncated" }),
            ("properties", Obj(
                ("columns", Obj(("type", "array"), ("items", Obj(
                    ("type", "object"),
                    ("required", new[] { "name", "type", "nullable", "masked" }),
                    ("properties", Obj(
                        ("name", Prim("string")),
                        ("type", Prim("string", "Trino type name, for example integer, varchar, decimal(12,2) or timestamp(3) with time zone.")),
                        ("nullable", Prim("boolean")),
                        ("masked", Prim("boolean", "True when the caller's column mask applies to the values.")))))))),
                ("rows", Obj(
                    ("type", "array"),
                    ("description", "Objects (column -> value), or arrays in column order with ?format=arrays. Date-time values are ISO 8601 with a Z suffix."),
                    ("items", Obj(("oneOf", new List<object>
                    {
                        Obj(("type", "object"), ("additionalProperties", true)),
                        Obj(("type", "array"), ("items", new Dictionary<string, object>()))
                    }))))),
                ("rowCount", Prim("integer")),
                ("truncated", Prim("boolean", "True when more rows existed than were returned.")),
                ("page", Ref("WebSqlPage")),
                ("next", Ref("WebSqlNext")),
                ("pagingUnavailable", Ref("WebSqlPagingUnavailable")),
                ("resultCache", Obj(
                    ("type", "object"),
                    ("description", "Present only when the gateway result cache is enabled and took part in the request."),
                    ("required", new[] { "status" }),
                    ("properties", Obj(
                        ("status", Obj(("type", "string"), ("enum", new[] { "hit", "miss", "store", "bypass", "ineligible" }))),
                        ("ageSeconds", Prim("integer")),
                        ("reason", Prim("string")))))),
                ("indexHints", Obj(("type", "array"), ("items", Obj(
                    ("type", "object"),
                    ("required", new[] { "table", "reason", "helpfulIndexColumns" }),
                    ("properties", Obj(
                        ("table", Prim("string")),
                        ("reason", Obj(("type", "string"), ("example", "NO_USABLE_INDEX"))),
                        ("helpfulIndexColumns", Obj(("type", "array"), ("items", Prim("string")))))))))))));

        // Members: the problem fields plus the allow-listed extension members (see ApiProblems). Optional members are not required.
        schemas["Problem"] = Obj(
            ("type", "object"),
            ("description", "RFC 9457 problem details. Branch on code, never on detail."),
            ("required", new[] { "type", "title", "status", "detail", "code" }),
            ("properties", Obj(
                ("type", Obj(("type", "string"), ("example", "urn:autheris:error:SQL_SYNTAX_ERROR"))),
                ("title", Prim("string")),
                ("status", Prim("integer")),
                ("detail", Prim("string")),
                ("instance", Prim("string")),
                ("code", Prim("string", "Stable error code.")),
                ("correlationId", Prim("string")),
                ("traceId", Prim("string")),
                ("limit", Prim("integer", "Optional: the limit that was exceeded.")),
                ("line", Prim("integer", "Optional: line of a syntax error.")),
                ("column", Prim("integer", "Optional: column of a syntax error.")),
                ("hint", Prim("string", "Optional: remediation hint.")),
                ("retryAfterSeconds", Prim("integer", "Optional: seconds to wait before a retry (429).")))));

        return schemas;
    }

    private static Dictionary<string, object> ProblemResponse(string description, params (string Name, object Schema)[] headers)
    {
        var response = Obj(
            ("description", description),
            ("content", Obj(("application/problem+json", Obj(("schema", Ref("Problem")))))));
        if (headers.Length > 0)
        {
            var map = new Dictionary<string, object>();
            foreach (var (name, schema) in headers)
            {
                map[name] = Obj(("schema", schema));
            }

            response["headers"] = map;
        }

        return response;
    }

    /// <summary>OpenAPI 3.0 description of POST /api/v1/sql (request, result shape, dialect notes).</summary>
    internal static Dictionary<string, object> BuildOpenApiSpec(GatewayOptions? gatewayOptions)
    {
        var webSql = gatewayOptions?.WebSql ?? new WebSqlOptions();

        var post = Obj(
            ("summary", "Run a governed, read-only SQL statement"),
            ("operationId", "executeGovernedWebSql"),
            ("description",
                "Ad-hoc SELECT on the governed catalog. Consent, row filters and masking of the caller are applied to every table. " +
                "Dialect: ANSI/Trino-style (use LIMIT n, not TOP n; LIMIT takes an integer literal; parameters are @name). " +
                "Tables: <dataSource>.<schema>.<table>, or <schema>.<table> in the request's data source (default: the gateway's default data source). " +
                "Only SELECT is allowed unless the instance enables DML. " +
                $"Without LIMIT at most {webSql.DefaultMaxRows} rows are returned and a LIMIT above {webSql.MaxAllowedRows} is capped; statements are at most {webSql.MaxQueryLength} characters; " +
                $"statements running longer than {webSql.ExecutionTimeoutSeconds} s are aborted (504). " +
                "The body is JSON, or plain text (Content-Type text/plain or application/sql) with the statement only."),
            ("parameters", new[]
            {
                Obj(
                    ("name", "format"),
                    ("in", "query"),
                    ("required", false),
                    ("schema", Obj(("type", "string"), ("enum", new[] { "objects", "arrays", "parquet" }))))
            }),
            ("requestBody", Obj(
                ("required", true),
                ("content", Obj(
                    ("application/json", Obj(("schema", Ref("WebSqlRequest")))),
                    ("text/plain", Obj(
                        ("schema", Prim("string")),
                        ("example", "SELECT id, status FROM finance.public.invoices ORDER BY id"))))))),
            ("responses", Obj(
                ("200", Obj(
                    ("description", "Result set"),
                    ("headers", Obj(("X-Autheris-Truncated", Obj(("schema", Prim("string")), ("description", "true when more rows existed."))))),
                    ("content", Obj(
                        ("application/json", Obj(("schema", Ref("WebSqlResult")))),
                        ("application/vnd.apache.parquet", Obj(("schema", Obj(("type", "string"), ("format", "binary")))))))))
                ,
                ("400", ProblemResponse("Invalid request. Codes include SQL_SYNTAX_ERROR, SQL_PARAMETER_MISSING, SQL_LIST_PARAM_*, SQL_PAGING_TOKEN_INVALID, SQL_PAGING_UNSUPPORTED, BAD_REQUEST.")),
                ("401", Obj(("description", "Not authenticated"))),
                ("403", ProblemResponse("Rejected by the security policy. Codes include SQL_POLICY_DENIED, SQL_POLICY_VIOLATION, FILTER_ON_MASKED_COLUMN, AGGREGATION_ON_MASKED_COLUMN.")),
                ("406", Obj(("description", "The Accept header names no supported format (no body)."))),
                ("409", ProblemResponse("Conflict (DML only).")),
                ("413", ProblemResponse("Request body too large.")),
                ("422", ProblemResponse("Construct not supported or limit exceeded. Codes include SQL_CONSTRUCT_NOT_SUPPORTED, SQL_LIMIT_EXCEEDED, SQL_TIMEZONE_ARGUMENT.")),
                ("429", ProblemResponse("Rate limit or quota exceeded (RATE_LIMIT_EXCEEDED, QUERY_COST_QUOTA_EXCEEDED).", ("Retry-After", Prim("integer")))),
                ("500", ProblemResponse("The statement could not be executed.")),
                ("501", ProblemResponse("Not implemented.")),
                ("502", ProblemResponse("The data source failed.")),
                ("503", ProblemResponse("Temporarily unavailable.")),
                ("504", ProblemResponse("Execution time limit exceeded (SQL_TIMEOUT).")))),
            ("security", new[] { Obj(("basicAuth", Array.Empty<string>())) }));

        return Obj(
            ("openapi", "3.0.3"),
            ("info", Obj(("title", "Autheris WebSQL (ad-hoc SQL)"), ("version", "v1"))),
            ("paths", Obj(("/api/v1/sql", Obj(("post", post))))),
            ("components", Obj(
                ("securitySchemes", Obj(("basicAuth", Obj(("type", "http"), ("scheme", "basic"))))),
                ("schemas", BuildSchemas(webSql)))));
    }
}
