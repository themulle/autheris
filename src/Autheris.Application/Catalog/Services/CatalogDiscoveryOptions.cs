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
}

/// <summary>Per-tenant overrides for discovery required roles and scopes.</summary>
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

        return ValidateOptionsResult.Success;
    }
}
