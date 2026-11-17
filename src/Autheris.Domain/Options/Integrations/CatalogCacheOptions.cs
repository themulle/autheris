using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// Size and freshness limits of the in-memory catalog caches (<c>Gateway:Catalog:Cache</c>). Eviction only ever produces a miss that is
/// recomputed from the authoritative source; no setting can extend how long a stale entry is served beyond the epoch validation.
/// </summary>
public sealed class CatalogCacheOptions
{
    public const int DefaultMetadataMaxEntries = 20_000;
    public const int DefaultMetadataTtlSeconds = 30;
    public const int DefaultProviderMaxEntries = 10_000;

    /// <summary>Maximum number of table metadata entries held by the governance repository. Valid range 1000-1000000.</summary>
    [Range(1_000, 1_000_000)] public int MetadataMaxEntries { get; init; } = DefaultMetadataMaxEntries;

    /// <summary>Lifetime of a table metadata entry in seconds, before the epoch check. Valid range 1-300.</summary>
    [Range(1, 300)] public int MetadataTtlSeconds { get; init; } = DefaultMetadataTtlSeconds;

    /// <summary>Maximum number of entries of the governed catalog provider and the tenant catalog lookup. Valid range 100-1000000.</summary>
    [Range(100, 1_000_000)] public int ProviderMaxEntries { get; init; } = DefaultProviderMaxEntries;
}
