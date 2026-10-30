namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using Autheris.Application.Common;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// The JSON answer of a governed SQL error (<see cref="SqlCompilerErrorMapper"/>): the status, the stable code and the fixed message of
/// that code (as a governed problem body), the limit kind of a limit error and the trace id. It never carries <c>Exception.Message</c>, driver text, values or counts.
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
            logger.Error(ex, "{Surface} failed with {Code}. TraceId={TraceId}", surface, mapped.Code, httpContext.TraceIdentifier);
        }
        else
        {
            logger.Warning(ex, "{Surface} rejected with {Code}. TraceId={TraceId}", surface, mapped.Code, httpContext.TraceIdentifier);
        }

        if (mapped.Status == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.RetryAfter = "5";
        }

        // `limit` carries the limit kind only, never a count; the title is the fixed message of the code (never exception text).
        var ext = mapped.Limit is null ? null : new Dictionary<string, object?> { ["limit"] = mapped.Limit };
        await ApiProblems.WriteAsync(httpContext, mapped.Status, mapped.Code, mapped.Title, ct, ext).ConfigureAwait(false);
    }
}
