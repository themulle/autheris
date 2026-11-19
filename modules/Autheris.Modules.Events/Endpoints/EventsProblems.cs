namespace Autheris.Api.Endpoints;

using System;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Problem results of the events module endpoints. Every result is produced by the host through
/// <see cref="IModuleRequestContext"/>, so the module cannot drift from the problem format of the host.
/// </summary>
internal static class EventsProblems
{
    internal static IModuleRequestContext RequestContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RequestServices.GetRequiredService<IModuleRequestContext>();
    }

    internal static IResult BadRequest(string code, string detail, HttpContext context) =>
        RequestContext(context).BadRequest(code, detail, context);

    internal static IResult Problem(int statusCode, string code, string detail, HttpContext context) =>
        RequestContext(context).Problem(statusCode, code, detail, context);

    internal static IResult TooManyRequests(string code, string detail, HttpContext context) =>
        Problem(StatusCodes.Status429TooManyRequests, code, detail, context);

    /// <summary>Logs the original exception server-side (never returned to the client).</summary>
    internal static void LogFailure(HttpContext context, Exception exception, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);
        context.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger("Autheris.Modules.Events")
            .LogWarning(exception, "Request failed with code {ErrorCode} (trace {TraceId}).", code, context.TraceIdentifier);
    }
}
