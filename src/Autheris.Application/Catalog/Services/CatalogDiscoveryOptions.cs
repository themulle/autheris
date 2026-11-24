namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;

/// <summary>Options bound to <c>Gateway:Catalog</c>.</summary>
public sealed class CatalogDiscoveryOptions
{
    public const string SectionName = "Gateway:Catalog";

    /// <summary>SR-L-16: maximum size of a remotely downloaded OpenAPI spec (default 10 MiB).</summary>
    public long MaxSpecDownloadBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>Authorized discovery access control, rate limits, and artifact caps bound to <c>Gateway:Catalog:Discovery</c>.</summary>
    public CatalogDiscoverySettings Discovery { get; set; } = new();
}

/// <summary>Discovery gate, rate limits, and artifact bounds under <c>Gateway:Catalog:Discovery</c>.</summary>
public sealed class CatalogDiscoverySettings
{
    public const int HardMaxSampleRows = 50;
    public const long HardMaxArtifactBytes = 1024L * 1024L;

    public IReadOnlyList<string> RequiredRoles { get; set; } = [];
    public IReadOnlyList<string> RequiredScopes { get; set; } = [];
    public IReadOnlyDictionary<string, TenantDiscoverySettings> TenantOverrides { get; set; } =
        new Dictionary<string, TenantDiscoverySettings>(StringComparer.OrdinalIgnoreCase);

    public int RequestsPerMinute { get; set; } = 120;
    public int AccessRequestsPerHour { get; set; } = 30;
    public int MaxSampleRows { get; set; } = 20;
    public long MaxArtifactBytes { get; set; } = 262_144;

    /// <summary>When true, catalog text and example fields of columns masked for a restricted-scope caller are omitted across all catalog paths.</summary>
    public bool HideMaskedColumnCatalogTextForRestrictedScopes { get; set; }

    /// <summary>Cross-request cache of the caller's access view used by the <c>accessStatus</c> filter (labels only, invalidation-keyed).</summary>
    public CatalogAccessViewCacheSettings AccessViewCache { get; set; } = new();
}

/// <summary>Per-tenant overrides for discovery required roles and scopes.</summary>
/// <summary>
/// Issue #1044: the cached view only selects discovery set membership (list, facets, search of <c>accessStatus</c>); page items and data
/// requests are always decided per request. Local invalidations apply before the next request; <see cref="TtlSeconds"/> bounds an
/// invalidation lost between replicas.
/// </summary>
public sealed class CatalogAccessViewCacheSettings
{
    public const int MaxTtlSeconds = 600;

    /// <summary>Longest lifetime of a cached caller access view in seconds (1 to 600); consent expiries and scheduled denies end it earlier.</summary>
    public int TtlSeconds { get; set; } = 300;

    /// <summary>Upper bound of the memory of all cached caller access views in megabytes; least recently used views are evicted.</summary>
    public int SizeLimitMegabytes { get; set; } = 64;

    /// <summary>Largest single caller access view in megabytes; a larger view is recomputed per request instead of cached.</summary>
    public int MaxEntryMegabytes { get; set; } = 8;
}

public sealed class TenantDiscoverySettings
{
    public IReadOnlyList<string>? RequiredRoles { get; set; }
    public IReadOnlyList<string>? RequiredScopes { get; set; }
}

public sealed class CatalogDiscoveryOptionsValidator : IValidateOptions<CatalogDiscoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogDiscoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxSpecDownloadBytes <= 0)
        {
            return ValidateOptionsResult.Fail("Gateway:Catalog:MaxSpecDownloadBytes must be greater than zero.");
        }

        var d = options.Discovery ?? new CatalogDiscoverySettings();
        if (d.RequestsPerMinute < 1)
        {
            return ValidateOptionsResult.Fail("Gateway:Catalog:Discovery:RequestsPerMinute must be at least 1.");
        }

        if (d.AccessRequestsPerHour < 1)
        {
            return ValidateOptionsResult.Fail("Gateway:Catalog:Discovery:AccessRequestsPerHour must be at least 1.");
        }

        if (d.MaxSampleRows is < 1 or > CatalogDiscoverySettings.HardMaxSampleRows)
        {
            return ValidateOptionsResult.Fail($"Gateway:Catalog:Discovery:MaxSampleRows must be between 1 and {CatalogDiscoverySettings.HardMaxSampleRows}.");
        }

        if (d.MaxArtifactBytes is < 1 or > CatalogDiscoverySettings.HardMaxArtifactBytes)
        {
            return ValidateOptionsResult.Fail($"Gateway:Catalog:Discovery:MaxArtifactBytes must be between 1 and {CatalogDiscoverySettings.HardMaxArtifactBytes}.");
        }

        var v = d.AccessViewCache ?? new CatalogAccessViewCacheSettings();
        if (v.TtlSeconds is < 1 or > CatalogAccessViewCacheSettings.MaxTtlSeconds || v.SizeLimitMegabytes < 1 || v.MaxEntryMegabytes is < 1 || v.MaxEntryMegabytes > v.SizeLimitMegabytes)
        {
            return ValidateOptionsResult.Fail(
                $"Gateway:Catalog:Discovery:AccessViewCache: TtlSeconds must be between 1 and {CatalogAccessViewCacheSettings.MaxTtlSeconds}, SizeLimitMegabytes at least 1 and MaxEntryMegabytes between 1 and SizeLimitMegabytes.");
        }

        return ValidateOptionsResult.Success;
    }
}
