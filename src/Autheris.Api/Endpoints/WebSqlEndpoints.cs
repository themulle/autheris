namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Serialization;
using Autheris.Application.Serialization;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class WebSqlEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public const string GenericForbiddenMessage = "The SQL statement was rejected by the gateway security policy.";
    public const string GenericBadRequestMessage = "The SQL request is invalid (syntax error, empty statement, invalid parameter or size limit exceeded). " +
        "WebSQL accepts ANSI/Trino-style SELECT statements: use LIMIT n instead of TOP n and name tables as <dataSource>.<schema>.<table> or <schema>.<table>.";
    public const string GenericTimeoutMessage = "The SQL statement exceeded the execution time limit. Narrow the query (filter, fewer columns, LIMIT) or contact support with the trace id.";
    public const string GenericServerErrorMessage = "The SQL statement could not be executed. Contact support with the trace id.";

    public sealed record WebSqlRequestDto(
        string? Sql,
        Dictionary<string, object?>? Parameters,
        string? DataSource);

    public static IEndpointRouteBuilder MapWebSqlEndpoints(this IEndpointRouteBuilder app, GatewayOptions? gatewayOptions = null)
    {
        // OpenAPI description of the endpoint (documentation only, no data). Like the other specs it is anonymous only
        // in OpenSchema mode.
        var openApi = app.MapGet("/api/v1/sql/openapi.json", () => Results.Json(BuildOpenApiSpec(gatewayOptions)))
            .WithName("GetWebSqlOpenApiSpec");
        if (gatewayOptions?.IsOpenSchemaAllowed == true)
        {
            openApi.AllowAnonymous();
        }
        else
        {
            openApi.RequireAuthorization();
        }

        app.MapPost("/api/v1/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSqlV1")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization();

        app.MapPost("/api/sql", HandleWebSqlRequest)
           .WithName("ExecuteGovernedWebSql")
           .WithMetadata(new ParquetOutputSupportedMetadata())
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization();

        app.MapPost("/v1/statement", HandleWebSqlRequest)
           .WithName("ExecuteTrinoStatementV1")
           .WithRequestBodyLimit(2 * 1024 * 1024)
           .RequireAuthorization();

        app.MapGet("/v1/statement/queued/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetTrinoQueuedStatementV1")
           .RequireAuthorization();

        app.MapGet("/v1/statement/executing/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetTrinoExecutingStatementV1")
           .RequireAuthorization();

        app.MapDelete("/v1/statement/{statementId}", HandleTrinoCancelStatementRequest)
           .WithName("CancelTrinoStatementV1")
           .RequireAuthorization();

        app.MapGet("/api/v1/sql/statements/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetWebSqlStatementV1")
           .RequireAuthorization();

        app.MapGet("/api/sql/statements/{statementId}", HandleTrinoQueuedStatementRequest)
           .WithName("GetWebSqlStatement")
           .RequireAuthorization();

        app.MapDelete("/api/v1/sql/statements/{statementId}", HandleTrinoCancelStatementRequest)
           .WithName("CancelWebSqlStatementV1")
           .RequireAuthorization();

        app.MapDelete("/api/sql/statements/{statementId}", HandleTrinoCancelStatementRequest)
           .WithName("CancelWebSqlStatement")
           .RequireAuthorization();

        return app;
    }

    internal static async Task HandleWebSqlRequest(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.WebSql");
        var ct = httpContext.RequestAborted;
        var statementManager = httpContext.RequestServices?.GetService<IWebSqlStatementManager>();

        // Content Length validation
        var maxBodySizeFeature = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (maxBodySizeFeature != null && !maxBodySizeFeature.IsReadOnly)
        {
            maxBodySizeFeature.MaxRequestBodySize = 2 * 1024 * 1024;
        }

        if (httpContext.Request.ContentLength > 2 * 1024 * 1024)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Request body exceeds maximum size limit (2 MB)." }, ct);
            return;
        }

        string? sql = null;
        Dictionary<string, object?>? parameters = null;
        string? dataSource = null;

        var contentType = httpContext.Request.ContentType ?? string.Empty;
        if (contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("application/sql", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(httpContext.Request.Body);
            sql = await reader.ReadToEndAsync(ct);
        }
        else
        {
            try
            {
                var bodyDto = await JsonSerializer.DeserializeAsync<WebSqlRequestDto>(
                    httpContext.Request.Body,
                    JsonOptions,
                    ct);

                sql = bodyDto?.Sql;
                parameters = bodyDto?.Parameters;
                dataSource = bodyDto?.DataSource;
            }
            catch (JsonException ex)
            {
                // SEC M-10: Parser details stay in the server log
                logger.LogWarning(ex, "WebSQL request body is not valid JSON. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsJsonAsync(new { error = "Invalid JSON payload.", traceId = httpContext.TraceIdentifier }, ct);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Missing 'sql' query parameter in request body." }, ct);
            return;
        }

        // Resolve Tenant canonically
        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        // Check if array format requested (?format=arrays)
        bool formatArrays = httpContext.Request.Query.TryGetValue("format", out var formatVal) &&
                            string.Equals(formatVal.ToString(), "arrays", StringComparison.OrdinalIgnoreCase);

        // Check if Trino protocol or wait_timeout requested
        bool isTrinoRoute = httpContext.Request.Path.StartsWithSegments("/v1/statement", StringComparison.OrdinalIgnoreCase);
        var trinoWaitTimeout = TryParseWaitTimeout(httpContext.Request);

        // Check for X-Trino-Catalog if dataSource was not explicitly passed in body
        if (string.IsNullOrWhiteSpace(dataSource) &&
            httpContext.Request.Headers.TryGetValue("X-Trino-Catalog", out var trinoCatalogHeader) &&
            !string.IsNullOrWhiteSpace(trinoCatalogHeader))
        {
            dataSource = trinoCatalogHeader.ToString().Trim();
        }

        var governedRequest = new GovernedSqlQueryRequest(sql, parameters, dataSource);

        if (isTrinoRoute || trinoWaitTimeout != null)
        {
            if (statementManager == null)
            {
                throw new GatewayNotImplementedException("WebSQL statement manager is not configured.");
            }

            var timeout = trinoWaitTimeout ?? TimeSpan.FromSeconds(5);
            try
            {
                var status = await statementManager.SubmitOrWaitAsync(governedRequest, user, tenantId, timeout, ct);
                await WriteTrinoStatementResponseAsync(httpContext, status, ct);
                return;
            }
            catch (Exception ex)
            {
                await WriteWebSqlErrorAsync(httpContext, ex, logger, ct);
                return;
            }
        }

        // F-DATA-01: Parquet output (Accept: application/vnd.apache.parquet) of the fully governed result set
        if (ParquetContentNegotiation.IsParquetRequested(httpContext.Request))
        {
            await HandleParquetWebSqlRequestAsync(httpContext, sqlService, gatewayOptions, logger, governedRequest, user, tenantId, ct);
            return;
        }

        // SEC M-10: The JSON writer is created lazily when the first result arrives. Policy/parse errors raised while the
        // statement is governed therefore never start the response, so the 4xx/5xx status and the curated body can still be sent.
        Utf8JsonWriter? writer = null;
        try
        {
            int rowCount = 0;
            string[] columnNames = Array.Empty<string>();

            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, token) =>
                {
                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                    httpContext.Response.ContentType = "application/json; charset=utf-8";

                    var w = new Utf8JsonWriter(httpContext.Response.Body);
                    writer = w;
                    w.WriteStartObject();

                    int fieldCount = reader.FieldCount;
                    columnNames = new string[fieldCount];
                    for (int i = 0; i < fieldCount; i++)
                    {
                        columnNames[i] = reader.GetName(i);
                    }

                    // Write "columns": [...]
                    w.WriteStartArray("columns");
                    for (int i = 0; i < fieldCount; i++)
                    {
                        w.WriteStringValue(columnNames[i]);
                    }
                    w.WriteEndArray();

                    // Write "rows": [...]
                    w.WriteStartArray("rows");

                    while (await reader.ReadAsync(token))
                    {
                        rowCount++;
                        if (formatArrays)
                        {
                            w.WriteStartArray();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                WriteDbValue(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            w.WriteEndArray();
                        }
                        else
                        {
                            w.WriteStartObject();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                w.WritePropertyName(columnNames[i]);
                                WriteDbValue(w, reader.IsDBNull(i) ? null : reader.GetValue(i));
                            }
                            w.WriteEndObject();
                        }

                        // Flush writer periodically to maintain streaming response for large row sets
                        if (rowCount % 250 == 0)
                        {
                            await w.FlushAsync(token);
                        }
                    }

                    w.WriteEndArray();
                },
                ct);

            if (writer == null)
            {
                // No result set was produced: emit an empty, well-formed result
                httpContext.Response.StatusCode = StatusCodes.Status200OK;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                writer = new Utf8JsonWriter(httpContext.Response.Body);
                writer.WriteStartObject();
                writer.WriteStartArray("columns");
                writer.WriteEndArray();
                writer.WriteStartArray("rows");
                writer.WriteEndArray();
            }

            writer.WriteNumber("rowCount", rowCount);
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
            await httpContext.Response.Body.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            await WriteWebSqlErrorAsync(httpContext, ex, logger, ct);
        }
        finally
        {
            if (writer != null)
            {
                await writer.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// F-DATA-01: Executes the governed statement and returns the result set as Apache Parquet. The rows are taken from the
    /// same governed reader as the JSON path (RLS, masking, consent applied in the rewritten SQL); the conversion is a pure
    /// output transformation. SEC M-10: nothing is written before the result arrives, so policy errors keep their status.
    /// </summary>
    private static async Task HandleParquetWebSqlRequestAsync(
        HttpContext httpContext,
        IGovernedSqlExecutionService sqlService,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger logger,
        GovernedSqlQueryRequest governedRequest,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct)
    {
        var parquetService = httpContext.RequestServices?.GetService<IParquetExportService>();
        if (await ParquetResponseWriter.TryRejectUnavailableAsync(httpContext, parquetService, ct))
        {
            return;
        }

        var configuredMaxRows = gatewayOptions.Value.ParquetEgress.MaxRowsPerFile;
        var maxRows = configuredMaxRows > 0 ? configuredMaxRows : 100000;

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        string[] columnNames = Array.Empty<string>();

        try
        {
            await sqlService.ExecuteGovernedQueryAsync(
                governedRequest,
                user,
                tenantId,
                async (reader, token) =>
                {
                    int fieldCount = reader.FieldCount;
                    columnNames = BuildUniqueColumnNames(reader);

                    // At most MaxRowsPerFile + 1 rows are read: the extra row only marks the export as truncated
                    // (X-Export-Truncated: true); the remaining result set is never materialized.
                    while (rows.Count <= maxRows && await reader.ReadAsync(token))
                    {
                        var row = new Dictionary<string, object?>(fieldCount, StringComparer.Ordinal);
                        for (int i = 0; i < fieldCount; i++)
                        {
                            row[columnNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }
                        rows.Add(row);
                    }
                },
                ct);

            // An empty result (or no result set) is a Parquet file with zero rows and the result columns.
            await ParquetResponseWriter.WriteAsync(httpContext, parquetService!, "websql", rows, columnNames, ct);
        }
        catch (Exception ex)
        {
            await WriteWebSqlErrorAsync(httpContext, ex, logger, ct);
        }
    }

    internal static async Task WriteWebSqlErrorAsync(
        HttpContext httpContext,
        Exception ex,
        ILogger logger,
        CancellationToken ct)
    {
        if (httpContext.Response.HasStarted)
        {
            return;
        }

        switch (ex)
        {
            case GatewayThrottledException throttledEx:
                logger.LogWarning(throttledEx, "WebSQL concurrency limit reached. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                httpContext.Response.Headers.RetryAfter = throttledEx.RetryAfterSeconds.ToString();
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "TooManyRequests",
                    message = "Too many concurrent requests. Retry later.",
                    retryAfterSeconds = throttledEx.RetryAfterSeconds,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case GatewayNotImplementedException notImplEx:
                logger.LogWarning(notImplEx, "WebSQL Not Implemented. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status501NotImplemented;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "NotImplemented",
                    message = "The requested feature or data source capability is not implemented.",
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case Antlr4.Runtime.Misc.ParseCanceledException parseEx:
                logger.LogWarning(parseEx, "WebSQL SQL Syntax Error. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "BadRequest",
                    message = "Invalid SQL syntax.",
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case Antlr4.Runtime.RecognitionException recogEx:
                logger.LogWarning(recogEx, "WebSQL SQL Syntax Error. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "BadRequest",
                    message = "Invalid SQL syntax.",
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case Exception exWithParse when exWithParse.InnerException is Antlr4.Runtime.Misc.ParseCanceledException or Antlr4.Runtime.RecognitionException:
                logger.LogWarning(exWithParse, "WebSQL SQL Syntax Error. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "BadRequest",
                    message = "Invalid SQL syntax.",
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case NotSupportedException notSuppEx:
                logger.LogWarning(notSuppEx, "WebSQL Not Supported. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status501NotImplemented;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "NotImplemented",
                    message = "The requested operation is not supported.",
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case SecurityException secEx:
                logger.LogWarning(secEx, "WebSQL Security Violation. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                var isProduction = httpContext.RequestServices?.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.IsProduction() ?? false;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Forbidden",
                    message = (!isProduction && secEx is WebSqlPolicyException) ? secEx.Message : GenericForbiddenMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case Exception timeoutEx when IsExecutionTimeout(timeoutEx):
                logger.LogWarning(timeoutEx, "WebSQL execution timed out. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "GatewayTimeout",
                    message = GenericTimeoutMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            case ArgumentException argEx:
                logger.LogWarning(argEx, "WebSQL Bad Request. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "BadRequest",
                    message = GenericBadRequestMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;

            default:
                // SEC M-10: Never echo exception/database messages to the client
                logger.LogError(ex, "WebSQL Execution Failed. TraceId={TraceId}", httpContext.TraceIdentifier);
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                httpContext.Response.ContentType = "application/json; charset=utf-8";
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "InternalServerError",
                    message = GenericServerErrorMessage,
                    traceId = httpContext.TraceIdentifier
                }, ct);
                break;
        }
    }

    /// <summary>Database command timeouts (e.g. SqlException "Execution Timeout Expired") and TimeoutExceptions.</summary>
    private static bool IsExecutionTimeout(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException ||
                (e is DbException && e.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>OpenAPI 3.0 description of POST /api/v1/sql (request, result shape, dialect notes).</summary>
    private static Dictionary<string, object> BuildOpenApiSpec(GatewayOptions? gatewayOptions)
    {
        var webSql = gatewayOptions?.WebSql;
        var dataSource = webSql?.DefaultDataSourceName ?? "default";
        var maxRows = webSql?.DefaultMaxRows ?? 1000;
        var timeout = webSql?.ExecutionTimeoutSeconds ?? 30;

        Dictionary<string, object> Response(string description) => new() { ["description"] = description };

        var requestSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["required"] = new[] { "sql" },
            ["properties"] = new Dictionary<string, object>
            {
                ["sql"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "A single SELECT statement.",
                    ["example"] = $"SELECT client_id, ts FROM {dataSource}.tem.crane_state WHERE client_id = @id ORDER BY ts DESC LIMIT 10"
                },
                ["parameters"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["description"] = "Named scalar parameters (string, number, boolean, null) bound to @name placeholders.",
                    ["additionalProperties"] = true,
                    ["example"] = new Dictionary<string, object> { ["id"] = 27110722 }
                },
                ["dataSource"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = $"Data source to run on. Default: {dataSource}."
                }
            }
        };

        var resultSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["columns"] = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" } },
                ["rows"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["description"] = "Objects (column -> value), or arrays with ?format=arrays.",
                    ["items"] = new Dictionary<string, object> { ["type"] = "object", ["additionalProperties"] = true }
                },
                ["rowCount"] = new Dictionary<string, object> { ["type"] = "integer" }
            }
        };

        var errorSchema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object> { ["type"] = "string" },
                ["message"] = new Dictionary<string, object> { ["type"] = "string" },
                ["traceId"] = new Dictionary<string, object> { ["type"] = "string" }
            }
        };

        Dictionary<string, object> Error(string description) => new()
        {
            ["description"] = description,
            ["content"] = new Dictionary<string, object>
            {
                ["application/json"] = new Dictionary<string, object> { ["schema"] = errorSchema }
            }
        };

        var post = new Dictionary<string, object>
        {
            ["summary"] = "Run a governed, read-only SQL statement",
            ["operationId"] = "executeGovernedWebSql",
            ["description"] =
                "Ad-hoc SELECT on the governed catalog. Consent, row filters and masking of the caller are applied to every table. " +
                "Dialect: ANSI/Trino-style (use LIMIT n, not TOP n or FETCH). Tables: <dataSource>.<schema>.<table>, or <schema>.<table> " +
                $"in the data source '{dataSource}'. Only SELECT is allowed (DML is off by default), at least one catalog table is required. " +
                $"At most {maxRows} rows are returned; statements running longer than {timeout} s are aborted (504). " +
                "The body is JSON, or plain text (Content-Type text/plain or application/sql) with the statement only.",
            ["parameters"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["name"] = "format",
                    ["in"] = "query",
                    ["required"] = false,
                    ["schema"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "objects", "arrays" } }
                }
            },
            ["requestBody"] = new Dictionary<string, object>
            {
                ["required"] = true,
                ["content"] = new Dictionary<string, object>
                {
                    ["application/json"] = new Dictionary<string, object> { ["schema"] = requestSchema },
                    ["text/plain"] = new Dictionary<string, object>
                    {
                        ["schema"] = new Dictionary<string, object> { ["type"] = "string" },
                        ["example"] = $"SELECT serial_number, crane_type FROM {dataSource}.md.crane LIMIT 5"
                    }
                }
            },
            ["responses"] = new Dictionary<string, object>
            {
                ["200"] = new Dictionary<string, object>
                {
                    ["description"] = "Result set",
                    ["content"] = new Dictionary<string, object>
                    {
                        ["application/json"] = new Dictionary<string, object> { ["schema"] = resultSchema }
                    }
                },
                ["400"] = Error("Invalid request (syntax, parameter, size)"),
                ["401"] = Response("Not authenticated"),
                ["403"] = Error("Rejected by the security policy (table not permitted, DML, ...)"),
                ["504"] = Error("Execution time limit exceeded")
            },
            ["security"] = new[] { new Dictionary<string, object> { ["basicAuth"] = Array.Empty<string>() } }
        };

        return new Dictionary<string, object>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object>
            {
                ["title"] = "Autheris WebSQL (ad-hoc SQL)",
                ["version"] = "v1"
            },
            ["paths"] = new Dictionary<string, object>
            {
                ["/api/v1/sql"] = new Dictionary<string, object> { ["post"] = post }
            },
            ["components"] = new Dictionary<string, object>
            {
                ["securitySchemes"] = new Dictionary<string, object>
                {
                    ["basicAuth"] = new Dictionary<string, object> { ["type"] = "http", ["scheme"] = "basic" }
                }
            }
        };
    }

    private static string[] BuildUniqueColumnNames(DbDataReader reader)
    {
        var names = new string[reader.FieldCount];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            var baseName = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "column" + (i + 1);
            }

            var name = baseName;
            var suffix = 1;
            while (!seen.Add(name))
            {
                name = baseName + "_" + suffix;
                suffix++;
            }

            names[i] = name;
        }

        return names;
    }

    private static void WriteDbValue(Utf8JsonWriter writer, object? val)
    {
        if (val is null or DBNull)
        {
            writer.WriteNullValue();
            return;
        }

        switch (val)
        {
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case byte by:
                writer.WriteNumberValue(by);
                break;
            case short s:
                writer.WriteNumberValue(s);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("o"));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("o"));
                break;
            case Guid g:
                writer.WriteStringValue(g.ToString());
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                writer.WriteStringValue(val.ToString());
                break;
        }
    }

    internal static async Task HandleTrinoQueuedStatementRequest(
        string statementId,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Autheris.Api.WebSql");
        var ct = httpContext.RequestAborted;

        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        var waitTimeout = TryParseWaitTimeout(httpContext.Request) ?? TimeSpan.FromSeconds(5);

        try
        {
            var status = await statementManager.GetStatusOrWaitAsync(statementId, user, tenantId, waitTimeout, ct);
            await WriteTrinoStatementResponseAsync(httpContext, status, ct);
        }
        catch (KeyNotFoundException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Statement not found or expired.", id = statementId }, ct);
        }
        catch (SecurityException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Forbidden", message = GenericForbiddenMessage }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to poll statement {StatementId}", statementId);
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await httpContext.Response.WriteAsJsonAsync(new { error = "InternalServerError", message = GenericServerErrorMessage }, ct);
        }
    }

    internal static async Task HandleTrinoCancelStatementRequest(
        string statementId,
        HttpContext httpContext,
        IWebSqlStatementManager statementManager)
    {
        var ct = httpContext.RequestAborted;
        var user = httpContext.User;
        var securityContext = EndpointSecurity.GetSecurityContext(httpContext);
        var tenantId = securityContext?.TenantId ?? EndpointSecurity.GetRequestTenant(httpContext);

        try
        {
            bool canceled = await statementManager.CancelStatementAsync(statementId, user, tenantId, ct);
            if (!canceled)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (SecurityException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }

    internal static TimeSpan? TryParseWaitTimeout(HttpRequest request)
    {
        string? val = null;
        if (request.Headers.TryGetValue("X-Trino-Wait-Timeout", out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
        {
            val = headerVal.ToString().Trim();
        }
        else if (request.Query.TryGetValue("wait_timeout", out var queryVal) && !string.IsNullOrWhiteSpace(queryVal))
        {
            val = queryVal.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(val))
        {
            return null;
        }

        return ParseDuration(val);
    }

    internal static TimeSpan ParseDuration(string text)
    {
        text = text.Trim();
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(text[..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }
        if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(text[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            return TimeSpan.FromSeconds(s);
        }
        if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(text[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m))
        {
            return TimeSpan.FromMinutes(m);
        }
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rawNum))
        {
            return rawNum > 100 ? TimeSpan.FromMilliseconds(rawNum) : TimeSpan.FromSeconds(rawNum);
        }

        return TimeSpan.FromSeconds(5);
    }

    internal static async Task WriteTrinoStatementResponseAsync(
        HttpContext httpContext,
        StatementExecutionStatus status,
        CancellationToken ct)
    {
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        var trinoColumns = status.Columns != null
            ? status.Columns.Select(c => new { name = c, type = "varchar" }).ToList()
            : null;

        var trinoStats = new
        {
            state = status.State,
            queued = status.State == "QUEUED",
            scheduled = true,
            nodes = 1,
            totalSplits = 1,
            queuedSplits = 0,
            runningSplits = status.State == "RUNNING" ? 1 : 0,
            completedSplits = status.State == "FINISHED" ? 1 : 0,
            cpuTimeMillis = status.ElapsedTimeMillis,
            wallTimeMillis = status.ElapsedTimeMillis,
            queuedTimeMillis = 0,
            elapsedTimeMillis = status.ElapsedTimeMillis
        };

        object? trinoError = null;
        if (status.State == "FAILED")
        {
            trinoError = new
            {
                message = status.ErrorMessage ?? "Statement execution failed.",
                errorCode = 1,
                errorName = "SYNTAX_ERROR",
                errorType = "USER_ERROR"
            };
        }

        var responseObj = new Dictionary<string, object?>
        {
            ["id"] = status.StatementId,
            ["infoUri"] = $"/ui/query.html?{status.StatementId}",
            ["stats"] = trinoStats
        };

        if (!string.IsNullOrWhiteSpace(status.NextUri))
        {
            responseObj["nextUri"] = status.NextUri;
        }

        if (trinoColumns != null)
        {
            responseObj["columns"] = trinoColumns;
        }

        if (status.Data != null)
        {
            responseObj["data"] = status.Data;
        }

        if (trinoError != null)
        {
            responseObj["error"] = trinoError;
        }

        await httpContext.Response.WriteAsJsonAsync(responseObj, ct);
    }
}
