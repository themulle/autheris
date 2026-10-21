namespace Autheris.Application.Catalog.Profiling;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Issue #80: Configuration options for column value profiling and privacy coarsening.
/// </summary>
public sealed record ColumnProfileOptions
{
    /// <summary>When true, allows <see cref="ColumnValueProfile.AllowedValues"/> to be exposed for non-sensitive columns.</summary>
    public bool ReleaseAllowedValues { get; init; }

    /// <summary>Minimum table row count required before exact min/max boundaries are published without small-table bucketing (default 20).</summary>
    public int MinRowsForExactRange { get; init; } = 20;
}

/// <summary>
/// Issue #80: Cached profile snapshot computed by a background or scheduled profiling job (never during <c>describe_dataset</c>).
/// </summary>
public sealed record ColumnProfileSnapshot(
    TableIdentifier Table,
    string ColumnName,
    string? FormatPattern = null,
    string? MinValue = null,
    string? MaxValue = null,
    double EmptyShare = 0.0,
    int? TypicalLength = null,
    long? ApproximateDistinctCount = null,
    IReadOnlyList<string>? ObservedAllowedValues = null,
    long TableRowCount = 100,
    DateTimeOffset? ComputedAt = null)
{
    public DateTimeOffset EffectiveComputedAt => ComputedAt ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// Issue #80: Provides coarsened column value profiles for <c>describe_dataset</c> from declared catalog metadata (dbt)
/// and precomputed background snapshots without ever querying the underlying data source during catalog inspection.
/// </summary>
public interface IColumnValueProfileService
{
    /// <summary>
    /// Computes and stores a column profile snapshot from a sample of values (used by scheduled/background profiling).
    /// </summary>
    Task<ColumnProfileSnapshot> ComputeAndStoreProfileAsync(
        TableMetadata table,
        string columnName,
        IEnumerable<object?> values,
        ColumnProfileOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Computes and stores profile snapshots for all columns of a dataset from a batch of rows (used by scheduled/background profiling).
    /// </summary>
    Task<IReadOnlyList<ColumnProfileSnapshot>> ComputeAndStoreDatasetProfilesAsync(
        TableMetadata table,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        ColumnProfileOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Stores a precomputed column profile snapshot in the profile store.
    /// </summary>
    void StoreProfileSnapshot(ColumnProfileSnapshot snapshot);

    /// <summary>
    /// Retrieves the raw stored snapshot for a column, if any.
    /// </summary>
    ColumnProfileSnapshot? GetStoredSnapshot(TableIdentifier table, string columnName);

    /// <summary>
    /// Builds the privacy-coarsened <see cref="ColumnValueProfile"/> for a single column without querying any data source.
    /// </summary>
    ColumnValueProfile? GetColumnProfile(
        TableMetadata table,
        TableColumn column,
        ColumnAccessLevel effectiveAccessLevel = ColumnAccessLevel.Clear,
        bool hasActiveRowFilters = false,
        ColumnProfileOptions? options = null);

    /// <summary>
    /// Builds privacy-coarsened <see cref="ColumnValueProfile"/> entries for all columns of a dataset without querying any data source.
    /// </summary>
    IReadOnlyDictionary<string, ColumnValueProfile> GetDatasetProfiles(
        TableMetadata table,
        IReadOnlyDictionary<string, ColumnAccessLevel>? columnAccessLevels = null,
        bool hasActiveRowFilters = false,
        ColumnProfileOptions? options = null);
}

/// <summary>
/// Issue #80: Generates deterministic, profile-conforming synthetic sample rows without reading real data from the source.
/// </summary>
public interface ISyntheticSampleRowGenerator
{
    IReadOnlyList<IReadOnlyDictionary<string, object?>> GenerateRows(
        TableMetadata table,
        int count,
        IReadOnlyDictionary<string, ColumnAccessLevel>? columnAccessLevels = null,
        bool hasActiveRowFilters = false,
        ColumnProfileOptions? options = null);

    /// <summary>Registers pre-defined or dbt-ingested synthetic sample rows for a table.</summary>
    void RegisterSyntheticSamples(TableIdentifier table, IReadOnlyList<IReadOnlyDictionary<string, object?>> samples);

    /// <summary>Gets registered synthetic sample rows for a table, if any.</summary>
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? GetRegisteredSyntheticSamples(TableIdentifier table);
}
