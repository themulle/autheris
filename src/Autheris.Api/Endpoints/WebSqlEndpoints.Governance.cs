namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

public static partial class WebSqlEndpoints
{
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

        // WP-X0a: compiler, policy and DML errors answer with a stable code and the fixed message of that code, never Exception.Message.
        if (SqlCompilerErrorMapper.TryMap(ex, out var mapped))
        {
            await WriteMappedErrorAsync(httpContext, ex, mapped, logger, ct);
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

            case GatewaySecurityException gatewayDenied:
                await Middleware.GatewayDenialStatusMapper.WriteWebSqlResponseAsync(httpContext, gatewayDenied, logger, GenericForbiddenMessage, ct);
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

    private static Task WriteMappedErrorAsync(
        HttpContext httpContext, Exception ex, SqlErrorMapping mapped, ILogger logger, CancellationToken ct) =>
        SqlErrorResponse.WriteAsync(httpContext, ex, mapped, logger, "WebSQL", ct);

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
            double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }
        if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return TimeSpan.FromSeconds(s);
        }
        if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
        {
            return TimeSpan.FromMinutes(m);
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawNum))
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

        // WebSQL findings 2.2: the type (and client type signature) of every column; varchar when unknown.
        var trinoColumns = status.Columns?.Select((name, i) =>
        {
            var type = status.ColumnTypes != null && i < status.ColumnTypes.Count ? status.ColumnTypes[i] : TrinoColumnType.Varchar;
            return new
            {
                name,
                type = type.Name,
                typeSignature = new
                {
                    rawType = type.RawType,
                    arguments = type.Arguments.Select(value => new { kind = "LONG", value }).ToList()
                }
            };
        }).ToList();

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
            var msg = status.ErrorMessage ?? GenericServerErrorMessage;
            bool isInternalError = msg.Contains("Contact support", StringComparison.OrdinalIgnoreCase) ||
                                   msg.Equals(GenericServerErrorMessage, StringComparison.OrdinalIgnoreCase) ||
                                   msg.Equals("Statement execution failed.", StringComparison.OrdinalIgnoreCase);

            trinoError = new
            {
                message = msg,
                errorCode = isInternalError ? 2 : 1,
                errorName = isInternalError ? "INTERNAL_ERROR" : "SYNTAX_ERROR",
                errorType = isInternalError ? "INTERNAL_ERROR" : "USER_ERROR"
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
