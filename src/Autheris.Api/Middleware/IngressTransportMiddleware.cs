namespace Autheris.Api.Middleware;

using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Issue #447 (REQ-ATR-06, SEC-ATR-02): stamps the scoped <see cref="IIngressTransportContext"/> from the matched endpoint's
/// <see cref="IngressTransportMetadata"/>. An endpoint without metadata leaves the ingress <c>None</c>, which the transport gate denies.
/// Nothing from the request (headers, query, body, path text) is read. A conflicting second stamp throws and fails the request.
/// </summary>
public sealed class IngressTransportMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IngressTransportMetadata>();
        if (metadata is not null)
        {
            context.RequestServices.GetRequiredService<IIngressTransportContext>().Set(metadata.Transport);
        }

        return next(context);
    }
}
