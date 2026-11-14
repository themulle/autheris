namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State.Caching;
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
    /// <summary>Fallback when <see cref="CatalogSearchOptions.MaxQueryLength"/> is not a positive value.</summary>
    public const int DefaultMaxQueryLength = 256;

    /// <summary>Maximum number of cached query embeddings (LRU; a miss only recomputes the vector).</summary>
    public const int QueryEmbeddingCacheCapacity = 1024;

    // Bounded LRU: arbitrary distinct queries must not grow the process memory without limit.
    private readonly BoundedCache<string, ReadOnlyMemory<float>> _embeddingCache = new(QueryEmbeddingCacheCapacity, comparer: StringComparer.Ordinal);

    private CatalogSearchSnapshot? _currentSnapshot;

    public bool IsIndexReady => _currentSnapshot != null;
    public int IndexedTableCount => _currentSnapshot?.MetadataMap.Count ?? 0;
    public DateTimeOffset? LastIndexedAt => _currentSnapshot?.CreatedAt;

    /// <summary>Number of query embeddings currently cached (never above <see cref="QueryEmbeddingCacheCapacity"/>).</summary>
    public int CachedQueryEmbeddingCount => _embeddingCache.Count;

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

        var bm25Docs = new List<Bm25Document>(tables.Count);
        var vectorTexts = new List<string>(tables.Count);
        var metadataMap = new Dictionary<TableIdentifier, TableMetadata>(tables.Count);
        var fkGraph = new Dictionary<TableIdentifier, List<TableRelationship>>();

        // 1. Metadaten-Extraktion, Relation-Building & Pre-Scrubbing
        foreach (var t in tables)
        {
            ct.ThrowIfCancellationRequested();
            metadataMap[t.Identifier] = t;

            var fields = new StringBuilder[CatalogSearchFieldWeights.FieldCount];
            for (int i = 0; i < fields.Length; i++) fields[i] = new StringBuilder(64);
            var combined = new StringBuilder(1024);

            void Add(Bm25Field field, string? text, bool scrub = true)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var value = scrub ? SecretScrubber.Redact(text) : text;
                fields[(int)field].Append(value).Append(' ');
                combined.Append(value).Append(' ');
            }

            // Technical names
            Add(Bm25Field.TechnicalName, $"{t.Identifier.Domain} {t.Identifier.Schema} {t.Identifier.TableName}", scrub: false);

            // Default language texts (scrubbed)
            Add(Bm25Field.DisplayName, t.Table.DisplayName);
            Add(Bm25Field.ShortDescription, t.Table.Description);
            Add(Bm25Field.LongDescription, t.Table.LongDescription);

            // All table translation texts (scrubbed), each in the field of its source text
            foreach (var (_, entry) in t.Table.Texts.Entries)
            {
                Add(Bm25Field.DisplayName, entry.DisplayName);
                Add(Bm25Field.ShortDescription, entry.Description);
                Add(Bm25Field.LongDescription, entry.LongDescription);
            }

            // Columns: technical name, types and display names are names; descriptions are descriptions (scrubbed)
            foreach (var c in t.Columns)
            {
                Add(Bm25Field.ColumnNames, $"{c.ColumnName} {c.DataType}", scrub: false);
                Add(Bm25Field.ColumnNames, c.DisplayName);
                Add(Bm25Field.ColumnDescriptions, c.Description);

                foreach (var (_, colEntry) in c.Texts.Entries)
                {
                    Add(Bm25Field.ColumnNames, colEntry.DisplayName);
                    Add(Bm25Field.ColumnDescriptions, colEntry.Description);
                }
            }

            bm25Docs.Add(new Bm25Document(
                t.Identifier,
                TechnicalName: fields[(int)Bm25Field.TechnicalName].ToString(),
                DisplayName: fields[(int)Bm25Field.DisplayName].ToString(),
                TagsAndOwner: fields[(int)Bm25Field.TagsAndOwner].ToString(),
                ShortDescription: fields[(int)Bm25Field.ShortDescription].ToString(),
                LongDescription: fields[(int)Bm25Field.LongDescription].ToString(),
                ColumnNames: fields[(int)Bm25Field.ColumnNames].ToString(),
                ColumnDescriptions: fields[(int)Bm25Field.ColumnDescriptions].ToString()));
            vectorTexts.Add(combined.ToString());

            var declaredKey = t.PrimaryKeyColumns;

            // Relationen registrieren via Meta oder Konvention (z.B. customer_id -> customers)
            foreach (var col in t.Columns)
            {
                if (declaredKey.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase))
                    continue;

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

                if (string.IsNullOrWhiteSpace(targetTable) || 
                    string.Equals(targetTable, t.Identifier.TableName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
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

        // 2. Vektorisierung aller Tabellendokumente (falls Honest Embedding Provider aktiv)
        bool vectorAvailable = _embeddingGenerator is not ICatalogEmbeddingGenerator ceg || ceg.IsAvailable;
        if (_options.Value.EmbeddingProvider == CatalogEmbeddingProvider.None)
        {
            vectorAvailable = false;
        }

        var vectorDocs = new List<(TableIdentifier, ReadOnlyMemory<float>)>(tables.Count);
        if (vectorAvailable)
        {
            var texts = vectorTexts;
            var vectors = await _embeddingGenerator.GenerateNormalizedEmbeddingsAsync(texts, ct).ConfigureAwait(false);
            for (int i = 0; i < bm25Docs.Count; i++)
            {
                vectorDocs.Add((bm25Docs[i].TableId, vectors[i]));
            }
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
        Func<TableIdentifier, bool> isVisiblePredicate,
        Func<TableIdentifier, bool>? isColumnRestricted = null) =>
        SearchCore(query, isVisiblePredicate, precomputedQueryVector: null, isColumnRestricted);

    /// <summary>
    /// Asynchronous variant of <see cref="Search"/>: the query embedding is computed without blocking a thread and honours
    /// <paramref name="ct"/>. Results are identical to <see cref="Search"/>.
    /// </summary>
    public async Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(
        CatalogSearchQuery query,
        Func<TableIdentifier, bool> isVisiblePredicate,
        Func<TableIdentifier, bool>? isColumnRestricted = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(isVisiblePredicate);
        EnsureQueryLength(query.QueryText);
        ct.ThrowIfCancellationRequested();

        var queryVector = ReadOnlyMemory<float>.Empty;
        if (query.Mode != CatalogSearchMode.KeywordBm25 && IsVectorSearchAvailable() && Volatile.Read(ref _currentSnapshot) != null)
        {
            queryVector = await GetOrComputeQueryEmbeddingAsync(query.QueryText, ct).ConfigureAwait(false);
        }

        return SearchCore(query, isVisiblePredicate, queryVector, isColumnRestricted);
    }

    /// <summary>
    /// Rejects over-long query text at the engine boundary, before it reaches BM25 tokenization or the embedding provider
    /// (a 400 for REST callers via <see cref="GatewayInvalidQueryException"/>; the message never echoes the query).
    /// </summary>
    private void EnsureQueryLength(string? queryText)
    {
        var maxLength = _options.Value.MaxQueryLength > 0 ? _options.Value.MaxQueryLength : DefaultMaxQueryLength;
        if (queryText != null && queryText.Length > maxLength)
        {
            throw new GatewayInvalidQueryException(
                $"The catalog search query exceeds the maximum length of {maxLength} characters.",
                "CATALOG_SEARCH_QUERY_TOO_LONG");
        }
    }

    private bool IsVectorSearchAvailable() =>
        _options.Value.EmbeddingProvider != CatalogEmbeddingProvider.None &&
        (_embeddingGenerator is not ICatalogEmbeddingGenerator ceg || ceg.IsAvailable);

    private IReadOnlyList<CatalogSearchHit> SearchCore(
        CatalogSearchQuery query,
        Func<TableIdentifier, bool> isVisiblePredicate,
        ReadOnlyMemory<float>? precomputedQueryVector,
        Func<TableIdentifier, bool>? isColumnRestricted = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(isVisiblePredicate);
        EnsureQueryLength(query.QueryText);

        // Sperrenfreier Lesezugriff via Volatile.Read
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot == null)
        {
            throw new CatalogSearchIndexNotReadyException();
        }

        bool isDomainMatch(TableIdentifier id) =>
            string.IsNullOrWhiteSpace(query.DomainFilter) || 
            string.Equals(id.Domain, query.DomainFilter.Trim(), StringComparison.OrdinalIgnoreCase);

        bool isAllowed(TableIdentifier id) =>
            isDomainMatch(id) && isVisiblePredicate(id);

        bool vectorAvailable = _embeddingGenerator is not ICatalogEmbeddingGenerator ceg || ceg.IsAvailable;
        if (_options.Value.EmbeddingProvider == CatalogEmbeddingProvider.None)
        {
            vectorAvailable = false;
        }

        string effectiveMode = (query.Mode, vectorAvailable) switch
        {
            (CatalogSearchMode.KeywordBm25, _) => "keyword",
            (_, false) => "keyword",
            (CatalogSearchMode.SemanticVector, true) => "semantic",
            (CatalogSearchMode.Hybrid, true) => "hybrid",
            _ => "keyword"
        };

        int rrfK = _options.Value.RrfConstantK;
        int topK = Math.Max(query.Limit * 3, 50);

        // 1. BM25 Ausführung mit Pre-Filtering
        var bm25Hits = (effectiveMode == "keyword" || effectiveMode == "hybrid")
            ? snapshot.Bm25Index.Search(query.QueryText, topK, isAllowed: isAllowed, isColumnRestricted: isColumnRestricted)
            : [];

        // 2. Vektor Ausführung mit Pre-Filtering
        ReadOnlyMemory<float> queryVec = ReadOnlyMemory<float>.Empty;
        if (effectiveMode == "semantic" || effectiveMode == "hybrid")
        {
            queryVec = precomputedQueryVector ?? GetOrComputeQueryEmbedding(query.QueryText);
        }

        var vectorHits = !queryVec.IsEmpty
            ? snapshot.VectorIndex.Search(queryVec, _options.Value.MinVectorSimilarity, topK, isAllowed: isAllowed)
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

        // 4. Governance-Filterung (bereits vorab angewendet) & Join-Graph-Expansion
        var finalHits = scoreMap
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
                    .Where(c => kvp.Value.Terms.Any(term =>
                        c.ColumnName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(c.DisplayName) && c.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(c.Description) && c.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                        c.Texts.Entries.Values.Any(entry =>
                            (!string.IsNullOrEmpty(entry.DisplayName) && entry.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(entry.Description) && entry.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))))
                    .Select(c => c.ColumnName)
                    .Take(5)
                    .ToList() ?? [];

                var defaultDisplayName = !string.IsNullOrWhiteSpace(meta?.Table.DisplayName)
                    ? meta.Table.DisplayName
                    : $"{kvp.Key.Domain}.{kvp.Key.TableName}";

                string resolvedDisplayName = defaultDisplayName;
                string? resolvedDescription = meta?.Table.Description;

                if (!string.IsNullOrWhiteSpace(query.Language) && meta?.Table != null)
                {
                    var resolved = meta.Table.Texts.Resolve(
                        [query.Language],
                        defaultDisplayName,
                        meta.Table.Description,
                        meta.Table.LongDescription);
                    resolvedDisplayName = resolved.DisplayName;
                    resolvedDescription = resolved.Description;
                }

                return new CatalogSearchHit(
                    TableIdentifier: kvp.Key,
                    DisplayName: resolvedDisplayName,
                    Description: resolvedDescription,
                    Domain: kvp.Key.Domain,
                    Sensitivity: meta?.Table.Sensitivity ?? "NORMAL",
                    CombinedScore: kvp.Value.Score,
                    Bm25Score: kvp.Value.Bm25,
                    VectorScore: kvp.Value.Vec,
                    MatchedTerms: kvp.Value.Terms,
                    RelevantColumns: matchedCols,
                    RelatedJoinPaths: relations,
                    // The root field is known only to the GraphQL catalog schema; CatalogDiscoveryService sets it (#682).
                    SuggestedGraphQlField: null,
                    EffectiveMode: effectiveMode,
                    Language: query.Language
                );
            })
            .ToList();

        return finalHits;
    }

    private ReadOnlyMemory<float> GetOrComputeQueryEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ReadOnlyMemory<float>.Empty;
        }

        var hashKey = QueryEmbeddingCacheKey(text);
        if (_embeddingCache.TryGet(hashKey, out var cached))
        {
            return cached;
        }

        var vector = _embeddingGenerator.GenerateNormalizedEmbedding(text);
        CacheQueryEmbedding(hashKey, vector);
        return vector;
    }

    private async Task<ReadOnlyMemory<float>> GetOrComputeQueryEmbeddingAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ReadOnlyMemory<float>.Empty;
        }

        var hashKey = QueryEmbeddingCacheKey(text);
        if (_embeddingCache.TryGet(hashKey, out var cached))
        {
            return cached;
        }

        var vectors = await _embeddingGenerator.GenerateNormalizedEmbeddingsAsync([text], ct).ConfigureAwait(false);
        var vector = vectors.Count > 0 ? vectors[0] : ReadOnlyMemory<float>.Empty;
        CacheQueryEmbedding(hashKey, vector);
        return vector;
    }

    private void CacheQueryEmbedding(string hashKey, ReadOnlyMemory<float> vector)
    {
        // An empty vector means the provider failed or was unavailable: do not pin that degradation in the cache.
        if (!vector.IsEmpty)
        {
            _embeddingCache.Set(hashKey, vector);
        }
    }

    private static string QueryEmbeddingCacheKey(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
