namespace Autheris.Domain.Options;

/// <summary>
/// Configuration options for documentation ingestion, quotas, and indexing.
/// Bound to the <c>Docs</c> configuration section.
/// </summary>
public sealed class DocsOptions
{
    public const string SectionName = "Docs";

    /// <summary>Maximum size in bytes of a single ingested documentation file.</summary>
    public int MaxDocumentBytes { get; set; } = 1_048_576;

    /// <summary>Maximum character length of a documentation entry title.</summary>
    public int MaxTitleLength { get; set; } = 200;

    /// <summary>Maximum number of tags permitted per documentation entry.</summary>
    public int MaxTags { get; set; } = 20;

    /// <summary>Maximum character length of a single documentation tag.</summary>
    public int MaxTagLength { get; set; } = 64;

    /// <summary>Maximum compressed size in bytes of an uploaded documentation bundle.</summary>
    public long MaxBundleBytes { get; set; } = 52_428_800;

    /// <summary>Maximum number of files permitted inside a documentation bundle.</summary>
    public int MaxBundleEntries { get; set; } = 1_000;

    /// <summary>Maximum total uncompressed size in bytes of a documentation bundle.</summary>
    public long MaxBundleUncompressedBytes { get; set; } = 104_857_600;

    /// <summary>Per-tenant documentation storage quota settings (<c>Docs:Quota</c>).</summary>
    public DocsQuotaOptions Quota { get; set; } = new();

    /// <summary>Interval in seconds between documentation search index refreshes.</summary>
    public int IndexRefreshSeconds { get; set; } = 30;

    /// <summary>Retention window in hours for deleted documentation tombstones.</summary>
    public int TombstoneRetentionHours { get; set; } = 24;

    /// <summary>Legacy filesystem documentation import settings (<c>Docs:LegacyImport</c>).</summary>
    public DocsLegacyImportOptions LegacyImport { get; set; } = new();
}

/// <summary>
/// One-shot import of the former file layout; disabled while <see cref="SourcePath"/> is empty.
/// </summary>
public sealed class DocsLegacyImportOptions
{
    /// <summary>Filesystem path to the legacy documentation directory to import at startup.</summary>
    public string? SourcePath { get; set; }
}

/// <summary>
/// Tenant-level documentation quota limits.
/// </summary>
public sealed class DocsQuotaOptions
{
    /// <summary>Maximum number of documentation entries permitted per tenant.</summary>
    public int MaxDocumentsPerTenant { get; set; } = 1_000;

    /// <summary>Maximum total documentation storage in bytes permitted per tenant.</summary>
    public long MaxBytesPerTenant { get; set; } = 52_428_800;
}
