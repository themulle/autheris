namespace Autheris.Api.Endpoints.Support;

using System;
using Autheris.Api.Errors;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Thin compatibility wrapper that delegates directly to <see cref="ApiProblems"/> (WP-ARC-C-05).
/// </summary>
internal static class SafeErrorResults
{
    public static IResult BadRequest(string code, string generic, HttpContext context) =>
        Problem(StatusCodes.Status400BadRequest, code, generic, context);

    public static IResult Conflict(string code, string generic, HttpContext context) =>
        Problem(StatusCodes.Status409Conflict, code, generic, context);

    public static IResult TooManyRequests(string code, string generic, HttpContext context) =>
        Problem(StatusCodes.Status429TooManyRequests, code, generic, context);

    public static IResult Problem(int statusCode, string code, string generic, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ApiProblems.Problem(statusCode, code, generic, context);
    }

    /// <summary>Logs the original exception server-side (never returned to the client).</summary>
    public static void LogFailure(HttpContext context, Exception exception, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);
        ApiProblems.LogFailure(context, exception, code);
    }
}

