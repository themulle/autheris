namespace Autheris.Domain.Options;

/// <summary>
/// Configuration options for documentation ingestion, quotas, and indexing.
/// Bound to the <c>Docs</c> configuration section.
/// </summary>
public sealed class DocsOptions
{
    public const string SectionName = "Docs";

    public int MaxDocumentBytes { get; set; } = 1_048_576;

    public int MaxTitleLength { get; set; } = 200;

    public int MaxTags { get; set; } = 20;

    public int MaxTagLength { get; set; } = 64;

    public long MaxBundleBytes { get; set; } = 52_428_800;

    public int MaxBundleEntries { get; set; } = 1_000;

    public long MaxBundleUncompressedBytes { get; set; } = 104_857_600;

    public DocsQuotaOptions Quota { get; set; } = new();

    public int IndexRefreshSeconds { get; set; } = 30;

    public int TombstoneRetentionHours { get; set; } = 24;
}

/// <summary>
/// Tenant-level documentation quota limits.
/// </summary>
public sealed class DocsQuotaOptions
{
    public int MaxDocumentsPerTenant { get; set; } = 1_000;

    public long MaxBytesPerTenant { get; set; } = 52_428_800;
}
