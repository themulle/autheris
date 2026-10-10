namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hochperformante In-Process Hybrid Search Engine für den Datensatz- und Tabellenkatalog.
/// Kombiniert Okapi BM25 mit SIMD-beschleunigter Vektorsuche über Reciprocal Rank Fusion (RRF).
/// </summary>
public sealed class CatalogSearchEngine : ICatalogSearchEngine
{
    private readonly ILocalEmbeddingGenerator _embeddingGenerator;
    private readonly IOptions<CatalogSearchOptions> _options;
    private readonly ILogger<CatalogSearchEngine> _logger;

    private CatalogSearchSnapshot? _currentSnapshot;

    public bool IsIndexReady => _currentSnapshot != null;
    public int IndexedTableCount => _currentSnapshot?.MetadataMap.Count ?? 0;
    public DateTimeOffset? LastIndexedAt => _currentSnapshot?.CreatedAt;

    public CatalogSearchEngine(
        ILocalEmbeddingGenerator embeddingGenerator,
        IOptions<CatalogSearchOptions> options,
        ILogger<CatalogSearchEngine> logger)
    {
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _logger.LogInformation("Starte Index-Neuaufbau für {Count} Katalog-Tabellen...", tables.Count);

        var bm25Docs = new List<(TableIdentifier, string)>(tables.Count);
        var metadataMap = new Dictionary<TableIdentifier, TableMetadata>(tables.Count);
        var fkGraph = new Dictionary<TableIdentifier, List<TableRelationship>>();

        // 1. Metadaten-Extraktion, Relation-Building & Pre-Scrubbing
        foreach (var t in tables)
        {
            ct.ThrowIfCancellationRequested();
            metadataMap[t.Identifier] = t;

            // Pre-Scrubbing gegen Secret Leaks in Beschreibungen
            var cleanDesc = SecretScrubber.Redact(t.Table.Description ?? string.Empty);
            var colDetails = string.Join(" ", t.Columns.Select(c => 
                $"{c.ColumnName} {c.DataType} {SecretScrubber.Redact(c.Description ?? string.Empty)}"));

            var docContent = $"{t.Identifier.Domain} {t.Identifier.Schema} {t.Identifier.TableName} {cleanDesc} {colDetails}";
            bm25Docs.Add((t.Identifier, docContent));

            // Relationen registrieren via Meta oder Konvention (z.B. customer_id -> customers)
            foreach (var col in t.Columns)
            {
                string? targetTable = null;
                string? targetCol = null;

                if (col.Meta != null && col.Meta.TryGetValue("foreign_key_table", out var fkt))
                {
                    targetTable = fkt;
                    col.Meta.TryGetValue("foreign_key_column", out targetCol);
                }
                else if (col.ColumnName.EndsWith("_id", StringComparison.OrdinalIgnoreCase) && 
                         !string.Equals(col.ColumnName, "id", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(col.ColumnName, "tenant_id", StringComparison.OrdinalIgnoreCase))
                {
                    var entityName = col.ColumnName[..^3];
                    targetTable = entityName.EndsWith('s') ? entityName : entityName + "s";
                    targetCol = col.ColumnName;
                }

                if (!string.IsNullOrWhiteSpace(targetTable))
                {
                    var refTable = new TableIdentifier(t.Identifier.Domain, t.Identifier.Schema, targetTable);
                    var rel = new TableRelationship(
                        FromTable: t.Identifier,
                        FromColumn: col.ColumnName,
                        ToTable: refTable,
                        ToColumn: targetCol ?? col.ColumnName,
                        Type: TableRelationshipType.ForeignKey);

                    if (!fkGraph.TryGetValue(t.Identifier, out var list))
                    {
                        list = [];
                        fkGraph[t.Identifier] = list;
                    }
                    list.Add(rel);
                }
            }
        }

        // 2. Vektorisierung aller Tabellendokumente
        var texts = bm25Docs.Select(x => x.Item2).ToList();
        var vectors = await _embeddingGenerator.GenerateNormalizedEmbeddingsAsync(texts, ct).ConfigureAwait(false);

        var vectorDocs = new List<(TableIdentifier, ReadOnlyMemory<float>)>(tables.Count);
        for (int i = 0; i < bm25Docs.Count; i++)
        {
            vectorDocs.Add((bm25Docs[i].Item1, vectors[i]));
        }

        // 3. Atomarer Snapshot-Austausch (Zero-Downtime)
        var newSnapshot = new CatalogSearchSnapshot(
            Bm25Index: new Bm25SearchIndex(bm25Docs),
            VectorIndex: new VectorSearchIndex(vectorDocs),
            MetadataMap: metadataMap,
            ForeignKeysGraph: fkGraph,
            CreatedAt: DateTimeOffset.UtcNow);

        Interlocked.Exchange(ref _currentSnapshot, newSnapshot);
        _logger.LogInformation("Katalog-Suchindex erfolgreich aktualisiert. {Count} Tabellen aktiv.", tables.Count);
    }

    public IReadOnlyList<CatalogSearchHit> Search(
        CatalogSearchQuery query, 
        Func<TableIdentifier, bool> isVisiblePredicate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(isVisiblePredicate);

        // Sperrenfreier Lesezugriff via Volatile.Read
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot == null)
        {
            throw new CatalogSearchIndexNotReadyException();
        }

        int rrfK = _options.Value.RrfConstantK;
        int candidatePoolSize = Math.Clamp(query.Limit * 5, 20, 100);

        // 1. BM25 Ausführung
        var bm25Hits = query.Mode != CatalogSearchMode.SemanticVector
            ? snapshot.Bm25Index.Search(query.QueryText, candidatePoolSize)
            : [];

        // 2. Vektor Ausführung
        var queryVec = query.Mode != CatalogSearchMode.KeywordBm25
            ? _embeddingGenerator.GenerateNormalizedEmbedding(query.QueryText)
            : ReadOnlyMemory<float>.Empty;

        var vectorHits = query.Mode != CatalogSearchMode.KeywordBm25
            ? snapshot.VectorIndex.Search(queryVec, _options.Value.MinVectorSimilarity, candidatePoolSize)
            : [];

        // 3. Reciprocal Rank Fusion (RRF)
        var scoreMap = new Dictionary<TableIdentifier, (double Score, double? Bm25, double? Vec, List<string> Terms)>();

        for (int rank = 0; rank < bm25Hits.Count; rank++)
        {
            var hit = bm25Hits[rank];
            double rrf = 1.0 / (rrfK + rank + 1);

            if (!scoreMap.TryGetValue(hit.TableId, out var cur))
                cur = (0, hit.Score, null, new List<string>(hit.MatchedTerms));
            else
                cur = (cur.Score, hit.Score, cur.Vec, cur.Terms);

            scoreMap[hit.TableId] = (cur.Score + rrf, cur.Bm25, cur.Vec, cur.Terms);
        }

        for (int rank = 0; rank < vectorHits.Count; rank++)
        {
            var hit = vectorHits[rank];
            double rrf = 1.0 / (rrfK + rank + 1);

            if (!scoreMap.TryGetValue(hit.TableId, out var cur))
                cur = (0, null, hit.Score, []);
            else
                cur = (cur.Score, cur.Bm25, hit.Score, cur.Terms);

            scoreMap[hit.TableId] = (cur.Score + rrf, cur.Bm25, cur.Vec, cur.Terms);
        }

        // 4. Governance ReBAC-Filterung & Join-Graph-Expansion
        var finalHits = scoreMap
            .Where(kvp => isVisiblePredicate(kvp.Key))
            .Where(kvp => string.IsNullOrWhiteSpace(query.DomainFilter) || 
                          string.Equals(kvp.Key.Domain, query.DomainFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kvp => kvp.Value.Score)
            .Take(query.Limit)
            .Select(kvp =>
            {
                snapshot.MetadataMap.TryGetValue(kvp.Key, out var meta);

                // Relationen auflösen (Bridge Tables / Joins)
                IReadOnlyList<TableRelationship> relations = [];
                if (query.ExpandRelations && snapshot.ForeignKeysGraph.TryGetValue(kvp.Key, out var relList))
                {
                    relations = relList.Where(r => isVisiblePredicate(r.ToTable))
                                       .Take(query.MaxRelationsPerHit)
                                       .ToList();
                }

                var matchedCols = meta?.Columns
                    .Where(c => kvp.Value.Terms.Any(term => c.ColumnName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => c.ColumnName)
                    .Take(5)
                    .ToList() ?? [];

                return new CatalogSearchHit(
                    TableIdentifier: kvp.Key,
                    DisplayName: $"{kvp.Key.Domain}.{kvp.Key.TableName}",
                    Description: meta?.Table.Description,
                    Domain: kvp.Key.Domain,
                    Sensitivity: meta?.Table.Sensitivity ?? "NORMAL",
                    CombinedScore: kvp.Value.Score,
                    Bm25Score: kvp.Value.Bm25,
                    VectorScore: kvp.Value.Vec,
                    MatchedTerms: kvp.Value.Terms,
                    RelevantColumns: matchedCols,
                    RelatedJoinPaths: relations,
                    SuggestedGraphQlField: $"{kvp.Key.Domain}_{kvp.Key.TableName}"
                );
            })
            .ToList();

        return finalHits;
    }
}
