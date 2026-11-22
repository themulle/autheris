namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Immutable, atomically swappable snapshot of the entire catalog search index.
/// Enables lock-free concurrent reads (Volatile.Read) at maximum throughput.
/// </summary>
public sealed record CatalogSearchSnapshot(
    Bm25SearchIndex Bm25Index,
    VectorSearchIndex VectorIndex,
    IReadOnlyDictionary<TableIdentifier, TableMetadata> MetadataMap,
    IReadOnlyDictionary<TableIdentifier, List<TableRelationship>> ForeignKeysGraph,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>? HiddenTextColumns = null)
{
    public int BaseDocumentCount => Bm25Index.BaseDocumentCount;

    public int TombstoneCount => Math.Max(Bm25Index.TombstoneCount, VectorIndex.TombstoneCount);

    public int DeltaOperationCount => Math.Max(Bm25Index.DeltaOperationCount, VectorIndex.DeltaOperationCount);
}
