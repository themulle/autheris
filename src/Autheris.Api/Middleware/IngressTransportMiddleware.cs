namespace Autheris.Api.Middleware;

using System.Net;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Quota;
using Autheris.Application.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Issue #447: stamps the scoped<see cref="IIngressTransportContext"/> from the matched endpoint's
/// <see cref="IngressTransportMetadata"/>. An endpoint without metadata leaves the ingress <c>None</c>, which the transport gate denies.
/// Nothing from the request (headers, query, body, path text) is read. A conflicting second stamp throws and fails the request.
/// </summary>
public sealed class IngressTransportMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IngressTransportMetadata>();
        if (metadata is not null)
        {
            context.RequestServices.GetRequiredService<IIngressTransportContext>().Set(metadata.Transport);
        }

        var ip = context.RequestServices?.GetService<IClientIpResolver>()?.ResolveClientIp()
            ?? context.Connection.RemoteIpAddress;
        var clientIp = ip is not null && !ip.Equals(IPAddress.None) ? ip.ToString() : null;
        var apiKey = context.Request.Headers.TryGetValue("X-API-Key", out var k) && !string.IsNullOrWhiteSpace(k)
            ? k.ToString()
            : null;
        using var _ = ScopeQueryExecutionGuard.BeginCallerContext(clientIp, apiKey);
        await next(context).ConfigureAwait(false);
    }
}
