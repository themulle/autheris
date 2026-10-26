namespace Autheris.Api.Endpoints;

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// WP-ARC-C-08: Canonical <c>/api/v1</c> route group helpers (the deprecated legacy route aliases were removed).
/// </summary>
public static class VersionedEndpointRouteBuilderExtensions
{
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
}
