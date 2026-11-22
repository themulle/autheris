using System;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Autheris.Api.Security;

public sealed class HealthProbeMetadata
{
    public static readonly HealthProbeMetadata Instance = new();
    private HealthProbeMetadata() { }
}

public sealed class MetricsEndpointMetadata
{
    public static readonly MetricsEndpointMetadata Instance = new();
    private MetricsEndpointMetadata() { }
}

public sealed class CsrfLoginExemptMetadata
{
    public static readonly CsrfLoginExemptMetadata Instance = new();
    private CsrfLoginExemptMetadata() { }
}

public sealed record ReadOnlyTokenAccessMetadata(string Method, bool StrictRawPath = false);

public static class GatewayEndpointMetadata
{
    public static TBuilder WithHealthProbe<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(HealthProbeMetadata.Instance);

    public static TBuilder WithMetricsEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(MetricsEndpointMetadata.Instance);

    public static TBuilder WithCsrfLoginExempt<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(CsrfLoginExemptMetadata.Instance);

    public static TBuilder WithReadOnlyTokenAccess<TBuilder>(this TBuilder builder, string method, bool strictRawPath = false)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ReadOnlyTokenAccessMetadata(method, strictRawPath));

    public static bool IsHealthProbe(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<HealthProbeMetadata>() is not null ||
        context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

    public static bool IsMetricsEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<MetricsEndpointMetadata>() is not null ||
        context.Request.Path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);

    public static bool IsCsrfLoginExempt(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<CsrfLoginExemptMetadata>() is not null ||
        context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase);

    public static bool IsPublicDocument(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IPublicDocumentMetadata>() is not null;

    public static bool HasTransport(HttpContext context, DataSourceTransportFlags transport) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IngressTransportMetadata>()?.Transport == transport;

    public static bool HasUnsafeRawDelimiters(string? path) =>
        string.IsNullOrEmpty(path) ||
        path.EndsWith("/", StringComparison.Ordinal) ||
        path.EndsWith("/.", StringComparison.Ordinal) ||
        path.Contains("/./", StringComparison.Ordinal) ||
        path.Contains("//", StringComparison.Ordinal) ||
        path.Contains("..", StringComparison.Ordinal) ||
        path.Contains('\\', StringComparison.Ordinal) ||
        path.Contains(';', StringComparison.Ordinal) ||
        path.Contains('?', StringComparison.Ordinal) ||
        path.Contains("%2F", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("%5C", StringComparison.OrdinalIgnoreCase);
}
