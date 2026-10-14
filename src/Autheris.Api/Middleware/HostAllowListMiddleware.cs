namespace Autheris.Api.Middleware;

using System.Threading.Tasks;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// SR-L-02: rejects requests whose <c>Host</c> (after forwarded-header processing) is not on the allow-list with 400,
/// no body. Health probes are exempt because Kubernetes/compose probes send the pod IP as Host. Only the registered
/// <see cref="HostAllowList"/> is used; the ASP.NET <c>AllowedHosts</c> configuration key is deliberately not involved.
/// </summary>
public sealed class HostAllowListMiddleware(RequestDelegate next, HostAllowList allowList)
{
    public Task InvokeAsync(HttpContext context)
    {
        System.ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.StartsWithSegments("/health", System.StringComparison.OrdinalIgnoreCase)
            || allowList.IsAllowed(context.Request.Host.Value))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return Task.CompletedTask;
    }
}
