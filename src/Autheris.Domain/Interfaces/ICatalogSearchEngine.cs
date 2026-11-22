namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Provides local embedding vectors for schema texts and search queries.
/// </summary>
public interface ILocalEmbeddingGenerator
{
    /// <summary>Dimension of the embedding vector (e.g. 384 for all-MiniLM-L6-v2 / bge-small).</summary>
    int EmbeddingDimensions { get; }

    /// <summary>Name or identifier of the embedding model in use.</summary>
    string ModelIdentifier { get; }

    /// <summary>Computes the L2-normalized vector for a single text synchronously on the CPU.</summary>
    ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text);

    /// <summary>Batch-embeds multiple texts with CPU parallelization.</summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(
        IReadOnlyList<string> texts, 
        CancellationToken ct = default);
}

/// <summary>
/// Closed error codes surfaced by catalog search index rebuild and health status reporting.
/// </summary>
public static class CatalogSearchErrorCodes
{
    public const string RebuildFailed = "REBUILD_FAILED";
    public const string VersionReadFailed = "VERSION_READ_FAILED";
    public const string EmbeddingFailed = "EMBEDDING_FAILED";
    public const string ErrorCodeDataKey = "CatalogSearchErrorCode";
}

/// <summary>
/// Point-in-time operational status of the catalog search index.
/// </summary>
public sealed record CatalogSearchIndexStatus(
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastAttemptUtc,
    string? LastErrorCode,
    TimeSpan? IndexAge);

/// <summary>
/// Auxiliary reader exposing the catalog version tuple currently reflected by an index instance.
/// </summary>
public interface ICatalogSearchStatusReader
{
    (long StructureVersion, long ContentVersion)? LastIndexedVersion { get; }
}

/// <summary>
/// Auxiliary recorder updated by background warmup orchestration when rebuild attempts finish.
/// </summary>
public interface ICatalogSearchStatusRecorder : ICatalogSearchStatusReader
{
    void RecordRebuildOutcome(
        DateTimeOffset? lastSuccessUtc,
        DateTimeOffset lastAttemptUtc,
        string? lastErrorCode,
        (long StructureVersion, long ContentVersion)? indexedVersion);
}

/// <summary>
/// In-memory hybrid search engine for the dataset and table catalog.
/// </summary>
/// <remarks>
/// <c>isColumnRestricted</c> (both search members): marks tables of which the caller cannot see every column. For those tables only the
/// table-level fields (names, display names, descriptions) contribute to the ranking, so hidden column text cannot influence the order
/// of hits. When omitted, every table is scored with all fields (full column visibility).
/// </remarks>
public interface ICatalogSearchEngine
{
    /// <summary>Checks whether the search index is warmed up and ready to query.</summary>
    bool IsIndexReady { get; }

    /// <summary>Number of tables currently indexed in the snapshot.</summary>
    int IndexedTableCount { get; }

    /// <summary>Returns whether the current search snapshot contains the specified table.</summary>
    bool ContainsTable(TableIdentifier id) => true;

    /// <summary>Timestamp of the last successful index build.</summary>
    DateTimeOffset? LastIndexedAt { get; }

    /// <summary>Returns the current rebuild status and computed index age.</summary>
    CatalogSearchIndexStatus GetStatus(DateTimeOffset? utcNow = null) =>
        new(
            LastIndexedAt,
            LastIndexedAt,
            null,
            LastIndexedAt.HasValue && utcNow.HasValue ? utcNow.Value - LastIndexedAt.Value : null);

    /// <summary>Returns the effective search mode string ("keyword", "semantic", or "hybrid") for the requested mode.</summary>
    string ResolveEffectiveMode(CatalogSearchMode requestedMode) => "keyword";

    /// <summary>Atomically creates a new immutable search snapshot in memory.</summary>
    Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default);

    /// <summary>Applies an incremental delta of updated/inserted tables and removed or inactive identifiers.</summary>
    Task ApplyDeltaAsync(
        IReadOnlyList<TableMetadata> upsertedTables,
        IReadOnlyCollection<TableIdentifier> removedOrInactiveIds,
        CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support incremental delta indexing.");

    /// <summary>Executes a high-throughput, lock-free search with governance filtering applied.</summary>
    IReadOnlyList<CatalogSearchHit> Search(
        CatalogSearchQuery query, 
        Func<TableIdentifier, bool> isVisiblePredicate,
        Func<TableIdentifier, bool>? isColumnRestricted = null);

    /// <summary>
    /// Asynchronous variant of <see cref="Search"/> (MCP-07): the query embedding is computed without blocking a thread and honours
    /// <paramref name="ct"/>. When the embedding provider fails or times out the search degrades to keyword-only and caches nothing.
    /// Callers on an async path use this member.
    /// </summary>
    Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(
        CatalogSearchQuery query,
        Func<TableIdentifier, bool> isVisiblePredicate,
        Func<TableIdentifier, bool>? isColumnRestricted = null,
        CancellationToken ct = default);
}
