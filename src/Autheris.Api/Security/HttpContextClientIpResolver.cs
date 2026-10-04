namespace Autheris.Api.Security;

using System.Net;
using Autheris.Application.Interfaces;
using Microsoft.AspNetCore.Http;

public sealed class HttpContextClientIpResolver : IClientIpResolver
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextClientIpResolver(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public IPAddress ResolveClientIp()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context == null)
        {
            return IPAddress.None;
        }

        // RemoteIpAddress after UseForwardedHeaders() is the canonical client IP.
        if (context.Connection.RemoteIpAddress != null)
        {
            return context.Connection.RemoteIpAddress;
        }

        if (context.User?.FindFirst("ip")?.Value is { Length: > 0 } ipClaim &&
            IPAddress.TryParse(ipClaim, out var parsedClaim))
        {
            return parsedClaim;
        }

        return IPAddress.None;
    }
}
