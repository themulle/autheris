namespace Autheris.Api.Middleware;

using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Synchronizes ASP.NET Core HttpContext.TraceIdentifier with the ambient W3C Activity trace id.
/// Enforces AC-2 correlation integrity across logs, audit, and problem details.
/// </summary>
public sealed class TraceIdentifierMiddleware
{
    private readonly RequestDelegate _next;

    public TraceIdentifierMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var currentActivity = Activity.Current;
        if (currentActivity != null && currentActivity.TraceId != default)
        {
            context.TraceIdentifier = currentActivity.TraceId.ToHexString();
        }

        await _next(context);
    }
}
