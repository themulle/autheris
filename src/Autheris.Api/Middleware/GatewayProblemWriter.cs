using System.Globalization;
using Autheris.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Autheris.Api.Middleware;

/// <summary>
/// The one problem body of a gateway failure (status, fixed title, governed code, optional retry and limit kind, trace id), used by the global
/// <see cref="GatewayExceptionHandler"/> and by <see cref="GatewayDenialStatusMapper"/> so both answer the same denial with the same shape (NL-T-30).
/// The code is checked against the governed set (status default otherwise); nothing but fixed texts reaches the body.
/// </summary>
internal static class GatewayProblemWriter
{
    internal static ProblemDetails Create(int status, string title, string? code, int? retryAfterSeconds, string? limit, string traceId)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpwg.org/specs/rfc9110.html#status.{status}"
        };
        if (code is not null)
        {
            problem.Extensions["code"] = ApiErrorCodes.Resolve(status, code);
        }

        if (retryAfterSeconds.HasValue)
        {
            problem.Extensions["retryAfterSeconds"] = retryAfterSeconds.Value;
        }

        if (limit is not null)
        {
            problem.Extensions["limit"] = limit;
        }

        problem.Extensions["traceId"] = traceId;
        return problem;
    }

    internal static Task WriteAsync(
        HttpContext httpContext, int status, string title, string? code, int? retryAfterSeconds, string? limit, string traceId, CancellationToken ct)
    {
        httpContext.Response.StatusCode = status;
        if (retryAfterSeconds.HasValue)
        {
            httpContext.Response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        return httpContext.Response.WriteAsJsonAsync(
            Create(status, title, code, retryAfterSeconds, limit, traceId),
            (System.Text.Json.JsonSerializerOptions?)null,
            "application/problem+json",
            ct);
    }
}
