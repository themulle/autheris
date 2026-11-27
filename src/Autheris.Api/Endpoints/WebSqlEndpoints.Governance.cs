namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using Autheris.Application.Common;
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
    /// <summary>Stable problem type URI of the WebSQL execution timeout (RFC 9457).</summary>
    /// <summary>Same value as <c>GovernedSqlErrorCodes.Timeout</c> (stable); used when the mapper did not classify the timeout.</summary>
    private const string FallbackTimeoutCode = "SQL_TIMEOUT";

    internal const string ExecutionTimeoutProblemType = "urn:autheris:problem:websql-execution-timeout";

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

        var failure = SqlFailureClassifier.Classify(ex);
        switch (failure.Kind)
        {
            case SqlFailureKind.Throttled:
                int retryAfter = (ex as ScopeConcurrencyExceededException)?.RetryAfterSeconds
                    ?? (ex as GatewayThrottledException)?.RetryAfterSeconds
                    ?? 5;
                logger.Warning(ex, "WebSQL concurrency limit reached. TraceId={TraceId}", httpContext.TraceIdentifier);
                await ApiProblems.WriteTooManyRequests(httpContext, failure.Code ?? ApiErrorCodes.TooManyRequests, failure.FixedMessage, retryAfter, ct);
                break;

            case SqlFailureKind.NotSupported:
                logger.Warning(ex, "WebSQL Not Supported. TraceId={TraceId}", httpContext.TraceIdentifier);
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status501NotImplemented, failure.Code ?? ApiErrorCodes.NotImplemented, failure.FixedMessage, ct);
                break;

            case SqlFailureKind.PolicyDenied when ex is GatewaySecurityException gatewayDenied:
                await Middleware.GatewayDenialStatusMapper.WriteWebSqlResponseAsync(httpContext, gatewayDenied, logger, ct);
                break;

            case SqlFailureKind.PolicyDenied:
                logger.Warning(ex, "WebSQL Policy Denied. TraceId={TraceId}", httpContext.TraceIdentifier);
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status403Forbidden, failure.Code ?? ApiErrorCodes.AccessDenied, failure.FixedMessage, ct);
                break;

            case SqlFailureKind.Timeout:
                logger.Warning(ex, "WebSQL execution timed out. TraceId={TraceId}", httpContext.TraceIdentifier);
                await WriteTimeoutProblemAsync(httpContext, FallbackTimeoutCode, ct);
                break;

            case SqlFailureKind.InvalidQuery:
                logger.Warning(ex, "WebSQL Bad Request. TraceId={TraceId}", httpContext.TraceIdentifier);
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status400BadRequest, failure.Code ?? ApiErrorCodes.BadRequest, failure.FixedMessage, ct);
                break;

            case SqlFailureKind.Canceled:
                logger.Warning(ex, "WebSQL execution cancelled by client. TraceId={TraceId}", httpContext.TraceIdentifier);
                await ApiProblems.WriteAsync(httpContext, 499, ApiErrorCodes.BadRequest, "The client closed the request.", CancellationToken.None);
                break;

            default:
                // SEC M-10: Never echo exception/database messages to the client
                logger.Error(ex, "WebSQL Execution Failed. TraceId={TraceId}", httpContext.TraceIdentifier);
                await ApiProblems.WriteAsync(httpContext, StatusCodes.Status500InternalServerError, ApiErrorCodes.InternalError, GenericServerErrorMessage, ct);
                break;
        }
    }

    private static async Task WriteMappedErrorAsync(
        HttpContext httpContext, Exception ex, SqlErrorMapping mapped, ILogger logger, CancellationToken ct)
    {
        if (mapped.Status != StatusCodes.Status504GatewayTimeout)
        {
            await SqlErrorResponse.WriteAsync(httpContext, ex, mapped, logger, "WebSQL", ct);
            return;
        }

        SqlCompilerErrorMapper.RecordOutcome(mapped);
        logger.Warning(ex, "WebSQL rejected with {Code}. TraceId={TraceId}", mapped.Code, httpContext.TraceIdentifier);
        await WriteTimeoutProblemAsync(httpContext, mapped.Code, ct);
    }

    /// <summary>
    /// D-11 / the execution timeout as RFC 9457 problem details (<c>application/problem+json</c>), written explicitly
    /// so that no problem-details service or developer exception page can add the query, the exception type or a stack trace.
    /// <c>code</c> is the stable extension member of the former body.
    /// </summary>
    private static Task WriteTimeoutProblemAsync(HttpContext httpContext, string code, CancellationToken ct)
    {
        // NL-SC-37: the same governed-code check as ApiProblems.WriteAsync; an unregistered code falls back to the status default.
        code = ApiErrorCodes.Resolve(StatusCodes.Status504GatewayTimeout, code);
        httpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
        httpContext.Response.ContentType = "application/problem+json; charset=utf-8";
        return httpContext.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = ExecutionTimeoutProblemType,
            ["title"] = "Gateway Timeout",
            ["status"] = StatusCodes.Status504GatewayTimeout,
            ["detail"] = GenericTimeoutMessage,
            ["code"] = code,
            ["traceId"] = httpContext.TraceIdentifier
        }), ct);
    }

    /// <summary>Database command timeouts (e.g. SqlException "Execution Timeout Expired") and TimeoutExceptions.</summary>
    private static bool IsExecutionTimeout(Exception ex) => SqlFailureClassifier.IsExecutionTimeout(ex);

    internal static TimeSpan? TryParseWaitTimeout(HttpRequest request)
    {
        if (request.Query.TryGetValue("wait_timeout", out var queryVal) && !string.IsNullOrWhiteSpace(queryVal))
        {
            return ParseDuration(queryVal.ToString().Trim());
        }

        return null;
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

    internal static async Task WriteWebSqlStatementResponseAsync(
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
            queuedTimeMillis = 0L,
            elapsedTimeMillis = status.ElapsedTimeMillis
        };

        var webSqlError = BuildWebSqlStatementError(status);

        var responseObj = new Dictionary<string, object?>
        {
            ["id"] = status.StatementId,
            ["infoUri"] = $"/ui/query.html?{status.StatementId}",
            ["stats"] = trinoStats
        };

        if (status.State is "QUEUED" or "RUNNING")
        {
            responseObj["nextUri"] = $"/api/v1/sql/statements/{status.StatementId}";
        }

        if (trinoColumns != null)
        {
            responseObj["columns"] = trinoColumns;
        }

        if (status.Data != null)
        {
            responseObj["data"] = status.Data;
        }

        if (webSqlError != null)
        {
            responseObj["error"] = webSqlError;
        }

        await httpContext.Response.WriteAsJsonAsync(responseObj, /* Status200OK */ ct);
    }

    private static object? BuildWebSqlStatementError(StatementExecutionStatus status)
    {
        if (status.State != "FAILED")
        {
            return null;
        }

        if (status.Failure?.Kind == SqlFailureKind.TransportDenied)
        {
            return new
            {
                message = "Access to the data source is not allowed over this transport.",
                errorCode = 3,
                errorName = Autheris.Domain.Exceptions.TransportNotAllowedException.Code,
                errorType = "USER_ERROR"
            };
        }

        bool isInternalError = status.Failure == null || status.Failure.Kind == SqlFailureKind.Internal;
        return new
        {
            message = isInternalError ? GenericServerErrorMessage : "The SQL statement could not be executed.",
            errorCode = isInternalError ? 2 : 1,
            errorName = isInternalError ? "INTERNAL_ERROR" : "SYNTAX_ERROR",
            errorType = isInternalError ? "INTERNAL_ERROR" : "USER_ERROR"
        };
    }
}
