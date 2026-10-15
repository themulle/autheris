namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Autheris.Domain.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// WP-ARC-C-08: Canonical <c>/api/v1</c> route group helpers and deprecated legacy route aliases.
/// </summary>
public static class VersionedEndpointRouteBuilderExtensions
{
    public const string DefaultSunsetHttpDate = "Wed, 31 Dec 2026 23:59:59 GMT";

    private static readonly Counter<long> DeprecatedRouteHitsCounter =
        GatewayDiagnostics.Meter.CreateCounter<long>(
            "gateway_deprecated_route_hits_total",
            description: "Total requests routed through a deprecated unversioned legacy route alias");

    /// <summary>
    /// Maps a canonical versioned route group under <c>/api/v1/{resourcePrefix}</c>.
    /// </summary>
    public static RouteGroupBuilder MapVersionedApiGroup(
        this IEndpointRouteBuilder endpoints,
        string resourcePrefix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var trimmed = resourcePrefix.Trim('/');
        var prefix = string.IsNullOrEmpty(trimmed) ? "/api/v1" : $"/api/v1/{trimmed}";
        return endpoints.MapGroup(prefix);
    }

    /// <summary>
    /// Maps a deprecated legacy route group under <paramref name="legacyPrefix"/> that attaches RFC 8594
    /// <c>Deprecation: true</c>, <c>Sunset</c>, and <c>Link: &lt;canonical&gt;; rel="successor-version"</c> response headers.
    /// </summary>
    public static RouteGroupBuilder MapLegacyAliasGroup(
        this IEndpointRouteBuilder endpoints,
        string legacyPrefix,
        string? canonicalSuccessorPrefix = null,
        string sunsetHttpDate = DefaultSunsetHttpDate)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPrefix);

        var group = endpoints.MapGroup(legacyPrefix);
        group.WithDeprecationHeaders(canonicalSuccessorPrefix, sunsetHttpDate);
        return group;
    }

    /// <summary>
    /// Registers a legacy route alias alongside a canonical <c>/api/v1/...</c> route, adding RFC 8594
    /// <c>Deprecation: true</c> and <c>Sunset</c> response headers on the legacy route.
    /// </summary>
    public static RouteHandlerBuilder MapLegacyAlias(
        this IEndpointRouteBuilder endpoints,
        string legacyPattern,
        Delegate handler,
        string? canonicalSuccessorPath = null,
        IEnumerable<string>? httpMethods = null,
        string sunsetHttpDate = DefaultSunsetHttpDate)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPattern);
        ArgumentNullException.ThrowIfNull(handler);

        var builder = httpMethods is null
            ? endpoints.Map(legacyPattern, handler)
            : endpoints.MapMethods(legacyPattern, httpMethods, handler);

        return builder.WithDeprecationHeaders(canonicalSuccessorPath, sunsetHttpDate);
    }

    /// <summary>
    /// Adds an endpoint filter that emits <c>Deprecation: true</c>, <c>Sunset</c>, and optional <c>Link</c> headers.
    /// </summary>
    public static TBuilder WithDeprecationHeaders<TBuilder>(
        this TBuilder builder,
        string? canonicalSuccessorPath = null,
        string sunsetHttpDate = DefaultSunsetHttpDate)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter(async (invocationContext, next) =>
        {
            var httpContext = invocationContext.HttpContext;
            var routePattern = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? httpContext.Request.Path.Value
                ?? "unknown";

            DeprecatedRouteHitsCounter.Add(1, new KeyValuePair<string, object?>("route", routePattern));

            httpContext.Response.OnStarting(static state =>
            {
                var (ctx, successor, sunset) = ((HttpContext, string?, string))state;
                ctx.Response.Headers["Deprecation"] = "true";
                ctx.Response.Headers["Sunset"] = sunset;
                if (!string.IsNullOrWhiteSpace(successor))
                {
                    ctx.Response.Headers["Link"] = $"<{successor}>; rel=\"successor-version\"";
                }
                return Task.CompletedTask;
            }, (httpContext, canonicalSuccessorPath, sunsetHttpDate));

            return await next(invocationContext).ConfigureAwait(false);
        });

        return builder;
    }
}
