namespace Autheris.Api.Endpoints;

using System;
using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;

/// <summary>
/// Issue #447 (SEC-ATR-02): endpoint metadata that declares the ingress transport of a route. It is the only source of the ingress
/// stamped by <see cref="Autheris.Api.Middleware.IngressTransportMiddleware"/>; headers, query and body never influence it.
/// </summary>
public sealed record IngressTransportMetadata
{
    public IngressTransportMetadata(DataSourceTransportFlags transport)
    {
        if (!IngressTransportContext.IsSingleKnownFlag(transport))
        {
            throw new ArgumentOutOfRangeException(nameof(transport), "An endpoint declares exactly one defined ingress transport.");
        }

        Transport = transport;
    }

    public DataSourceTransportFlags Transport { get; }
}

public static class IngressTransportEndpointExtensions
{
    /// <summary>Declares the ingress transport of the endpoint (or group). Exactly one defined transport.</summary>
    public static TBuilder WithIngressTransport<TBuilder>(this TBuilder builder, DataSourceTransportFlags transport)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var metadata = new IngressTransportMetadata(transport);
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(metadata));
        return builder;
    }
}
