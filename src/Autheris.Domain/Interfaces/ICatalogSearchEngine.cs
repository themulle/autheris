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
/// In-Memory Hybrid Search Engine für den Datensatz- und Tabellenkatalog.
/// </summary>
public interface ICatalogSearchEngine
{
    /// <summary>Prüft, ob der Suchindex aufgewärmt und abfragebereit ist.</summary>
    bool IsIndexReady { get; }

    /// <summary>Anzahl aktuell im Snapshot indizierter Tabellen.</summary>
    int IndexedTableCount { get; }

    /// <summary>Zeitpunkt des letzten erfolgreichen Index-Builds.</summary>
    DateTimeOffset? LastIndexedAt { get; }

    /// <summary>Erzeugt atomar einen neuen unveränderlichen Such-Snapshot im Speicher.</summary>
    Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default);

    /// <summary>Führt eine hochoptimierte, sperrenfreie Suche unter Berücksichtigung von ReBAC aus.</summary>
    IReadOnlyList<CatalogSearchHit> Search(
        CatalogSearchQuery query, 
        Func<TableIdentifier, bool> isVisiblePredicate);

    /// <summary>
    /// Asynchronous variant of <see cref="Search"/> (MCP-07): the query embedding is computed without blocking a thread and honours
    /// <paramref name="ct"/>. When the embedding provider fails or times out the search degrades to keyword-only and caches nothing.
    /// Callers on an async path use this member.
    /// </summary>
    Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(
        CatalogSearchQuery query,
        Func<TableIdentifier, bool> isVisiblePredicate,
        CancellationToken ct = default);
}
