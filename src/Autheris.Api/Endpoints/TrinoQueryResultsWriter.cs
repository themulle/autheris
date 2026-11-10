namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Exceptions;
using Microsoft.AspNetCore.Http;

public static class TrinoQueryResultsWriter
{
    public const int PageRows = 4096;

    private static readonly ConcurrentDictionary<string, long> StatementTokens = new(StringComparer.Ordinal);

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

        return TimeSpan.FromSeconds(1);
    }

    internal static TimeSpan ParseWaitTimeout(HttpRequest request, TimeSpan? defaultWait = null)
    {
        var def = defaultWait ?? TimeSpan.FromSeconds(1);
        string? val = request.Query.TryGetValue("maxWait", out var maxWaitVal) && !string.IsNullOrWhiteSpace(maxWaitVal)
            ? maxWaitVal.ToString().Trim()
            : request.Headers.TryGetValue("X-Trino-Wait-Timeout", out var headerVal) && !string.IsNullOrWhiteSpace(headerVal)
                ? headerVal.ToString().Trim()
                : null;

        if (string.IsNullOrWhiteSpace(val)) return def;
        var duration = ParseDuration(val);
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration > TimeSpan.FromSeconds(300) ? TimeSpan.FromSeconds(300) : duration;
    }

    public static Task WriteAsync(
        HttpContext httpContext,
        StatementExecutionStatus status,
        long token,
        CancellationToken ct = default) =>
        WriteAsync(httpContext, status, token, null, ct);

    public static async Task WriteAsync(
        HttpContext httpContext,
        StatementExecutionStatus status,
        long token,
        Exception? exception,
        CancellationToken ct = default)
    {
        string reportedState;
        string? nextUri = null;
        IReadOnlyList<IReadOnlyList<object?>>? pageData = null;
        bool isLastPage = false;

        var baseUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host.Value}{httpContext.Request.PathBase.Value}";
        var infoUri = $"{baseUri}/v1/statement/info/{status.StatementId}";

        if (status.State is "FAILED" or "CANCELED")
        {
            reportedState = "FAILED";
        }
        else
        {
            var currentToken = StatementTokens.GetOrAdd(status.StatementId, 0);
            if (token != currentToken && token != currentToken + 1)
            {
                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (token == currentToken + 1)
            {
                StatementTokens[status.StatementId] = token;
            }

            if (status.State is "QUEUED" or "RUNNING")
            {
                reportedState = status.State;
                nextUri = $"{baseUri}/v1/statement/executing/{status.StatementId}/{token}";
            }
            else // FINISHED
            {
                int totalRows = status.Data?.Count ?? 0;
                int totalPages = totalRows == 0 ? 1 : (totalRows + PageRows - 1) / PageRows;

                if (token < 0 || token >= totalPages)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                isLastPage = token >= totalPages - 1;
                reportedState = isLastPage ? "FINISHED" : "RUNNING";

                if (!isLastPage)
                {
                    nextUri = $"{baseUri}/v1/statement/executing/{status.StatementId}/{token + 1}";
                }

                int startIndex = (int)(token * PageRows);
                int count = Math.Min(PageRows, Math.Max(0, totalRows - startIndex));
                pageData = status.Data != null ? status.Data.Skip(startIndex).Take(count).ToList() : [];
            }
        }

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "application/json; charset=utf-8";

        var trinoStats = new
        {
            state = reportedState,
            queued = reportedState == "QUEUED",
            scheduled = true,
            nodes = 1,
            totalSplits = 1,
            queuedSplits = 0,
            runningSplits = reportedState == "RUNNING" ? 1 : 0,
            completedSplits = reportedState == "FINISHED" ? 1 : 0,
            cpuTimeMillis = status.ElapsedTimeMillis,
            wallTimeMillis = status.ElapsedTimeMillis,
            queuedTimeMillis = 0L,
            elapsedTimeMillis = status.ElapsedTimeMillis,
            processedRows = (long)(status.Data?.Count ?? 0),
            processedBytes = 0L,
            physicalInputBytes = 0L,
            peakMemoryBytes = 0L,
            spilledBytes = 0L
        };

        var responseObj = new Dictionary<string, object?>
        {
            ["id"] = status.StatementId,
            ["infoUri"] = infoUri,
            ["stats"] = trinoStats
        };

        if (nextUri != null)
        {
            responseObj["nextUri"] = nextUri;
        }

        if (pageData != null)
        {
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

            if (trinoColumns != null)
            {
                responseObj["columns"] = trinoColumns;
            }
            responseObj["data"] = pageData;
        }

        if (reportedState == "FAILED")
        {
            var failure = status.Failure;
            var kind = failure?.Kind ?? (status.State == "CANCELED" ? SqlFailureKind.Canceled : SqlFailureKind.Internal);
            var (errorType, errorCode, errorName) = kind switch
            {
                SqlFailureKind.InvalidQuery => ("USER_ERROR", 1, "SYNTAX_ERROR"),
                SqlFailureKind.PolicyDenied => ("USER_ERROR", 4, "PERMISSION_DENIED"),
                SqlFailureKind.TransportDenied => ("USER_ERROR", 4, TransportNotAllowedException.Code),
                SqlFailureKind.NotSupported => ("USER_ERROR", 13, "NOT_SUPPORTED"),
                SqlFailureKind.Canceled => ("USER_ERROR", 3, "USER_CANCELED"),
                SqlFailureKind.Timeout => ("INSUFFICIENT_RESOURCES", 131074, "EXCEEDED_TIME_LIMIT"),
                SqlFailureKind.Throttled => ("INSUFFICIENT_RESOURCES", 131073, "QUERY_QUEUE_FULL"),
                _ => ("INTERNAL_ERROR", 65536, "GENERIC_INTERNAL_ERROR")
            };

            string message = failure?.FixedMessage ?? (kind == SqlFailureKind.Canceled ? SqlFailureClassifier.GenericCanceledMessage : SqlFailureClassifier.GenericServerErrorMessage);

            var trinoError = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["errorCode"] = errorCode,
                ["errorName"] = errorName,
                ["errorType"] = errorType
            };

            var location = ResolveErrorLocation(status, failure, exception);
            if (location is not null)
            {
                trinoError["errorLocation"] = new
                {
                    lineNumber = location.Value.LineNumber,
                    columnNumber = location.Value.ColumnNumber
                };
            }
            responseObj["error"] = trinoError;
        }

        if (status.Truncated && isLastPage)
        {
            responseObj["warnings"] = new[]
            {
                new
                {
                    warningCode = new
                    {
                        code = 0x0F00_0001,
                        name = "AUTHERIS_ROW_LIMIT"
                    },
                    message = "The result was truncated at the gateway row limit."
                }
            };
        }

        await httpContext.Response.WriteAsJsonAsync(responseObj, /* Status200OK */ ct);
    }

    internal static (int LineNumber, int ColumnNumber)? ResolveErrorLocation(
        StatementExecutionStatus? status,
        SqlFailure? failure,
        Exception? exception = null)
    {
        if (failure?.Line is not null && failure?.Column is not null && failure.Line.Value > 0 && failure.Column.Value > 0)
        {
            return (failure.Line.Value, failure.Column.Value);
        }

        if (status?.ErrorLine is not null && status?.ErrorColumn is not null && status.ErrorLine.Value > 0 && status.ErrorColumn.Value > 0)
        {
            return (status.ErrorLine.Value, status.ErrorColumn.Value);
        }

        if (exception is not null)
        {
            var loc = SqlFailureClassifier.ExtractErrorLocation(exception);
            if (loc is not null) return loc;
        }

        if (failure?.Exception is not null)
        {
            var loc = SqlFailureClassifier.ExtractErrorLocation(failure.Exception);
            if (loc is not null) return loc;
        }

        return null;
    }
}
