namespace Autheris.Domain.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Bereitstellung von lokalen Embedding-Vektoren für Schema-Texte und Suchanfragen.
/// </summary>
public interface ILocalEmbeddingGenerator
{
    /// <summary>Dimension des Embedding-Vektors (z. B. 384 bei all-MiniLM-L6-v2 / bge-small).</summary>
    int EmbeddingDimensions { get; }

    /// <summary>Name oder Kennung des genutzten Embedding-Modells.</summary>
    string ModelIdentifier { get; }

    /// <summary>Berechnet den L2-normierten Vektor für einen Text synchron auf der CPU.</summary>
    ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text);

    /// <summary>Stapelvektorisierung mehrerer Texte mit CPU-Parallelisierung.</summary>
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
/// In-Memory Hybrid Search Engine für den Datensatz- und Tabellenkatalog.
/// </summary>
/// <remarks>
/// <c>isColumnRestricted</c> (both search members): marks tables of which the caller cannot see every column. For those tables only the
/// table-level fields (names, display names, descriptions) contribute to the ranking, so hidden column text cannot influence the order
/// of hits. When omitted, every table is scored with all fields (full column visibility).
/// </remarks>
public interface ICatalogSearchEngine
{
    /// <summary>Prüft, ob der Suchindex aufgewärmt und abfragebereit ist.</summary>
    bool IsIndexReady { get; }

    /// <summary>Anzahl aktuell im Snapshot indizierter Tabellen.</summary>
    int IndexedTableCount { get; }

    /// <summary>Zeitpunkt des letzten erfolgreichen Index-Builds.</summary>
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

    /// <summary>Erzeugt atomar einen neuen unveränderlichen Such-Snapshot im Speicher.</summary>
    Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default);

    /// <summary>Applies an incremental delta of updated/inserted tables and removed or inactive identifiers.</summary>
    Task ApplyDeltaAsync(
        IReadOnlyList<TableMetadata> upsertedTables,
        IReadOnlyCollection<TableIdentifier> removedOrInactiveIds,
        CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support incremental delta indexing.");

    /// <summary>Führt eine hochoptimierte, sperrenfreie Suche unter Berücksichtigung der Governance aus.</summary>
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
