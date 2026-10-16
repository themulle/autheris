namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// The JSON answer of a governed SQL error (<see cref="SqlCompilerErrorMapper"/>): the status, the stable code and the fixed message of
/// that code, the limit kind of a limit error and the trace id. It never carries <c>Exception.Message</c>, driver text, values or counts.
/// Shared by WebSQL and the dataset endpoints (Phase 5 M4), so both surfaces answer the same error the same way.
/// </summary>
internal static class SqlErrorResponse
{
    public static async Task WriteAsync(
        HttpContext httpContext, Exception ex, SqlErrorMapping mapped, ILogger logger, string surface, CancellationToken ct)
    {
        SqlCompilerErrorMapper.RecordOutcome(mapped);
        if (mapped.Status >= StatusCodes.Status500InternalServerError && mapped.Status != StatusCodes.Status503ServiceUnavailable &&
            mapped.Status != StatusCodes.Status504GatewayTimeout)
        {
            logger.LogError("{Surface} failed with {Code} ({ExceptionType}). TraceId={TraceId}", surface, mapped.Code, ex.GetType().Name, httpContext.TraceIdentifier);
            logger.LogDebug(ex, "{Surface} failed with {Code} (full exception). TraceId={TraceId}", surface, mapped.Code, httpContext.TraceIdentifier);
        }
        else
        {
            logger.LogWarning("{Surface} rejected with {Code} ({ExceptionType}). TraceId={TraceId}", surface, mapped.Code, ex.GetType().Name, httpContext.TraceIdentifier);
            logger.LogDebug(ex, "{Surface} rejected with {Code} (full exception). TraceId={TraceId}", surface, mapped.Code, httpContext.TraceIdentifier);
        }

        httpContext.Response.StatusCode = mapped.Status;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        if (mapped.Status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "5";
        }

        // `limit` carries the limit kind only, never a count.
        var body = new Dictionary<string, object?>
        {
            ["error"] = ErrorName(mapped.Status),
            ["code"] = mapped.Code,
            ["message"] = mapped.Title
        };
        if (mapped.Limit is not null)
        {
            body["limit"] = mapped.Limit;
        }

        body["traceId"] = httpContext.TraceIdentifier;
        await httpContext.Response.WriteAsJsonAsync(body, ct).ConfigureAwait(false);
    }

    internal static string ErrorName(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "BadRequest",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status422UnprocessableEntity => "UnprocessableEntity",
        StatusCodes.Status501NotImplemented => "NotImplemented",
        StatusCodes.Status502BadGateway => "BadGateway",
        StatusCodes.Status503ServiceUnavailable => "ServiceUnavailable",
        StatusCodes.Status504GatewayTimeout => "GatewayTimeout",
        _ => "InternalServerError"
    };
}
