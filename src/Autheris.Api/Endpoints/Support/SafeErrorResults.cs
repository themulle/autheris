namespace Autheris.Api.Endpoints.Support;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thin helper that produces client-safe <c>application/problem+json</c> error responses with a fixed detail text,
/// a stable machine-readable <c>code</c> and the <c>traceId</c>. Exception text never reaches the client; the original
/// exception is logged server-side. This is an interim wrapper so that a later central ProblemDetails factory can
/// replace the call sites with a one-line change.
/// </summary>
internal static class SafeErrorResults
{
    private const string LogCategory = "Autheris.Api.Endpoints.SafeErrors";

    public static IResult BadRequest(string code, string generic, HttpContext context) =>
        Problem(StatusCodes.Status400BadRequest, code, generic, context);

    public static IResult Conflict(string code, string generic, HttpContext context) =>
        Problem(StatusCodes.Status409Conflict, code, generic, context);

    public static IResult TooManyRequests(string code, string generic, HttpContext context) =>
        Problem(StatusCodes.Status429TooManyRequests, code, generic, context);

    public static IResult Problem(int statusCode, string code, string generic, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Results.Problem(
            detail: generic,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = ResolveTraceId(context)
            });
    }

    /// <summary>Logs the original exception server-side (never returned to the client).</summary>
    public static void LogFailure(HttpContext context, Exception exception, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);
        var logger = context.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        logger?.LogWarning(exception, "Request failed with code {ErrorCode} (trace {TraceId}).", code, ResolveTraceId(context));
    }

    private static string ResolveTraceId(HttpContext context) =>
        Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
}
