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

        // SEC (Low): a token "ip" claim is caller-influenced and never used as a fallback (fail closed to IPAddress.None).
        return IPAddress.None;
    }
}
