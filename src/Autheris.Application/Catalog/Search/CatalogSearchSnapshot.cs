namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Unveränderlicher, atomar austauschbarer Schnappschuss des gesamten Katalog-Suchindex.
/// Ermöglicht sperrenfreie gleichzeitige Lesezugriffe (Volatile.Read) bei maximalem Durchsatz.
/// </summary>
public sealed record CatalogSearchSnapshot(
    Bm25SearchIndex Bm25Index,
    VectorSearchIndex VectorIndex,
    IReadOnlyDictionary<TableIdentifier, TableMetadata> MetadataMap,
    IReadOnlyDictionary<TableIdentifier, List<TableRelationship>> ForeignKeysGraph,
    DateTimeOffset CreatedAt);
