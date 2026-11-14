namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

/// <summary>The closed set of fields the index scores separately (BM25F). Not user-extensible.</summary>
public enum Bm25Field
{
    TechnicalName = 0,
    DisplayName = 1,
    TagsAndOwner = 2,
    ShortDescription = 3,
    LongDescription = 4,
    ColumnNames = 5,
    ColumnDescriptions = 6,
}

/// <summary>Internal field weights of the catalog search ranking (values of the removed <c>FieldWeights</c> option).</summary>
public static class CatalogSearchFieldWeights
{
    /// <summary>Number of scored fields; the memory per document is bounded by this constant.</summary>
    public const int FieldCount = 7;

    /// <summary>Default weights in <see cref="Bm25Field"/> order.</summary>
    public static IReadOnlyList<double> Default { get; } = [10.0, 8.0, 5.0, 3.0, 2.0, 2.0, 1.0];
}

/// <summary>One table as per-field texts for <see cref="Bm25SearchIndex"/>.</summary>
public sealed record Bm25Document(
    TableIdentifier TableId,
    string TechnicalName = "",
    string DisplayName = "",
    string TagsAndOwner = "",
    string ShortDescription = "",
    string LongDescription = "",
    string ColumnNames = "",
    string ColumnDescriptions = "")
{
    internal string TextOf(int field) => (Bm25Field)field switch
    {
        Bm25Field.TechnicalName => TechnicalName,
        Bm25Field.DisplayName => DisplayName,
        Bm25Field.TagsAndOwner => TagsAndOwner,
        Bm25Field.ShortDescription => ShortDescription,
        Bm25Field.LongDescription => LongDescription,
        Bm25Field.ColumnNames => ColumnNames,
        Bm25Field.ColumnDescriptions => ColumnDescriptions,
        _ => string.Empty,
    };
}

/// <summary>
/// Thread-safe immutable Okapi BM25F index with weighted per-field scoring, incremental delta segments, and tombstones.
/// Uses the Robertson-Sparck Jones IDF formula with smoothing.
/// </summary>
public sealed class Bm25SearchIndex
{
    private const double K1 = 1.5;
    private const double B = 0.75;
    private const int FirstColumnField = (int)Bm25Field.ColumnNames;

    private sealed record IndexedDocument(
        TableIdentifier TableId,
        Dictionary<string, int>?[] FieldTermFrequencies,
        int[] FieldLengths,
        bool HasTokens);

    private readonly IReadOnlyList<IndexedDocument> _baseDocuments;
    private readonly IReadOnlyDictionary<TableIdentifier, int> _baseTableToIndex;
    private readonly IReadOnlyDictionary<string, int> _baseTermDocCounts;
    private readonly IReadOnlySet<int> _baseTombstones;
    private readonly IReadOnlyDictionary<TableIdentifier, IndexedDocument> _deltaDocuments;
    private readonly IReadOnlyDictionary<string, int> _termDocCountDeltas;
    private readonly int _activeDocCount;
    private readonly long[] _totalFieldLengths;
    private readonly double[] _weights;

    /// <summary>Single-field convenience: the whole text is indexed as the short description.</summary>
    public Bm25SearchIndex(IEnumerable<(TableIdentifier TableId, string Content)> source)
        : this(source.Select(static x => new Bm25Document(x.TableId, ShortDescription: x.Content)))
    {
    }

    public Bm25SearchIndex(IEnumerable<Bm25Document> source, IReadOnlyList<double>? fieldWeights = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _weights = ValidateWeights(fieldWeights ?? CatalogSearchFieldWeights.Default);

        var documents = new List<IndexedDocument>();
        var baseTableToIndex = new Dictionary<TableIdentifier, int>();
        var termDocCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totalFieldLengths = new long[CatalogSearchFieldWeights.FieldCount];

        foreach (var entry in source)
        {
            if (baseTableToIndex.TryGetValue(entry.TableId, out var priorIdx))
            {
                // Replace duplicate identifier cleanly so base segment never holds stale duplicate postings.
                var prior = documents[priorIdx];
                AdjustStats(prior, termDocCounts, totalFieldLengths, delta: -1);

                var replacement = TokenizeDocument(entry);
                if (replacement is null)
                {
                    baseTableToIndex.Remove(entry.TableId);
                    documents[priorIdx] = new IndexedDocument(
                        entry.TableId,
                        new Dictionary<string, int>?[CatalogSearchFieldWeights.FieldCount],
                        new int[CatalogSearchFieldWeights.FieldCount],
                        HasTokens: false);
                    continue;
                }

                documents[priorIdx] = replacement;
                AdjustStats(replacement, termDocCounts, totalFieldLengths, delta: +1);
                continue;
            }

            var doc = TokenizeDocument(entry);
            if (doc is null) continue;

            baseTableToIndex[entry.TableId] = documents.Count;
            documents.Add(doc);
            AdjustStats(doc, termDocCounts, totalFieldLengths, delta: +1);
        }

        _baseDocuments = documents;
        _baseTableToIndex = baseTableToIndex;
        _baseTermDocCounts = termDocCounts;
        _baseTombstones = new HashSet<int>();
        _deltaDocuments = new Dictionary<TableIdentifier, IndexedDocument>();
        _termDocCountDeltas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _activeDocCount = baseTableToIndex.Count;
        _totalFieldLengths = totalFieldLengths;
    }

    private Bm25SearchIndex(
        IReadOnlyList<IndexedDocument> baseDocuments,
        IReadOnlyDictionary<TableIdentifier, int> baseTableToIndex,
        IReadOnlyDictionary<string, int> baseTermDocCounts,
        IReadOnlySet<int> baseTombstones,
        IReadOnlyDictionary<TableIdentifier, IndexedDocument> deltaDocuments,
        IReadOnlyDictionary<string, int> termDocCountDeltas,
        int activeDocCount,
        long[] totalFieldLengths,
        double[] weights)
    {
        _baseDocuments = baseDocuments;
        _baseTableToIndex = baseTableToIndex;
        _baseTermDocCounts = baseTermDocCounts;
        _baseTombstones = baseTombstones;
        _deltaDocuments = deltaDocuments;
        _termDocCountDeltas = termDocCountDeltas;
        _activeDocCount = activeDocCount;
        _totalFieldLengths = totalFieldLengths;
        _weights = weights;
    }

    public int ActiveDocumentCount => _activeDocCount;

    public int BaseDocumentCount => _baseTableToIndex.Count;

    public int TombstoneCount => _baseTombstones.Count;

    public int DeltaOperationCount => _baseTombstones.Count + _deltaDocuments.Count;

    public Bm25SearchIndex ApplyDelta(
        IEnumerable<(TableIdentifier TableId, string Content)> upserts,
        IEnumerable<TableIdentifier> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        return ApplyDelta(
            upserts.Select(static x => new Bm25Document(x.TableId, ShortDescription: x.Content)),
            removals);
    }

    public Bm25SearchIndex ApplyDelta(
        IEnumerable<Bm25Document> upserts,
        IEnumerable<TableIdentifier> removals)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);

        var removalSet = removals as HashSet<TableIdentifier> ?? [.. removals];
        var tombstones = new HashSet<int>(_baseTombstones);
        var deltaDocuments = new Dictionary<TableIdentifier, IndexedDocument>(_deltaDocuments);
        var termDeltas = new Dictionary<string, int>(_termDocCountDeltas, StringComparer.OrdinalIgnoreCase);
        int activeDocCount = _activeDocCount;
        var totalFieldLengths = (long[])_totalFieldLengths.Clone();

        void RemoveActive(TableIdentifier id)
        {
            if (deltaDocuments.Remove(id, out var existingDelta))
            {
                activeDocCount--;
                AdjustStats(existingDelta, termDeltas, totalFieldLengths, delta: -1);
                return;
            }

            if (_baseTableToIndex.TryGetValue(id, out int baseIdx) && tombstones.Add(baseIdx))
            {
                activeDocCount--;
                AdjustStats(_baseDocuments[baseIdx], termDeltas, totalFieldLengths, delta: -1);
            }
        }

        foreach (var entry in upserts)
        {
            if (removalSet.Contains(entry.TableId))
            {
                continue;
            }

            RemoveActive(entry.TableId);

            var doc = TokenizeDocument(entry);
            if (doc is null)
            {
                continue;
            }

            deltaDocuments[entry.TableId] = doc;
            activeDocCount++;
            AdjustStats(doc, termDeltas, totalFieldLengths, delta: +1);
        }

        foreach (var removedId in removalSet)
        {
            RemoveActive(removedId);
        }

        return new Bm25SearchIndex(
            _baseDocuments,
            _baseTableToIndex,
            _baseTermDocCounts,
            tombstones,
            deltaDocuments,
            termDeltas,
            activeDocCount,
            totalFieldLengths,
            _weights);
    }

    private static double[] ValidateWeights(IReadOnlyList<double> weights)
    {
        if (weights.Count != CatalogSearchFieldWeights.FieldCount)
        {
            throw new ArgumentException($"Exactly {CatalogSearchFieldWeights.FieldCount} field weights are required.", nameof(weights));
        }

        var copy = new double[weights.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            if (!double.IsFinite(weights[i]) || weights[i] <= 0.0)
            {
                throw new ArgumentException("Field weights must be positive finite numbers.", nameof(weights));
            }

            copy[i] = weights[i];
        }

        return copy;
    }

    /// <param name="isAllowed">Table visibility filter.</param>
    /// <param name="isColumnRestricted">Tables for which the caller cannot see every column: only the table-level fields
    /// (not column names or column descriptions) contribute to the score, so hidden column text cannot move the ranking.</param>
    public IReadOnlyList<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)> Search(
        string query,
        int topK = 25,
        Func<TableIdentifier, bool>? isAllowed = null,
        Func<TableIdentifier, bool>? isColumnRestricted = null)
    {
        var queryTerms = SmartSchemaTokenizer.Tokenize(query, expandAbbreviations: false);
        if (queryTerms.Length == 0 || _activeDocCount == 0) return [];

        var avgFieldLength = new double[CatalogSearchFieldWeights.FieldCount];
        bool anyNonZeroField = false;
        for (int f = 0; f < avgFieldLength.Length; f++)
        {
            avgFieldLength[f] = (double)_totalFieldLengths[f] / _activeDocCount;
            if (avgFieldLength[f] > 0.0)
            {
                anyNonZeroField = true;
            }
        }

        if (!anyNonZeroField) return [];

        var activeQueryTerms = new List<(string Term, double Idf)>(queryTerms.Length);
        foreach (var term in queryTerms)
        {
            int df = _baseTermDocCounts.GetValueOrDefault(term) + _termDocCountDeltas.GetValueOrDefault(term);
            if (df <= 0) continue;

            // Robertson-Sparck Jones IDF with smoothing
            double idf = Math.Log(1.0 + (_activeDocCount - df + 0.5) / (df + 0.5));
            activeQueryTerms.Add((term, idf));
        }

        if (activeQueryTerms.Count == 0) return [];

        var results = new List<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)>();

        for (int i = 0; i < _baseDocuments.Count; i++)
        {
            // Skip tombstoned entries before predicate evaluation or scoring.
            if (_baseTombstones.Contains(i))
            {
                continue;
            }

            ScoreDocument(_baseDocuments[i], activeQueryTerms, avgFieldLength, _weights, isAllowed, isColumnRestricted, results);
        }

        foreach (var doc in _deltaDocuments.Values)
        {
            ScoreDocument(doc, activeQueryTerms, avgFieldLength, _weights, isAllowed, isColumnRestricted, results);
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }

    private static void ScoreDocument(
        IndexedDocument doc,
        List<(string Term, double Idf)> activeQueryTerms,
        double[] avgFieldLength,
        double[] weights,
        Func<TableIdentifier, bool>? isAllowed,
        Func<TableIdentifier, bool>? isColumnRestricted,
        List<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)> results)
    {
        if (!doc.HasTokens || (isAllowed != null && !isAllowed(doc.TableId)))
        {
            return;
        }

        int fieldLimit = isColumnRestricted != null && isColumnRestricted(doc.TableId)
            ? FirstColumnField
            : CatalogSearchFieldWeights.FieldCount;

        double score = 0.0;
        List<string>? matched = null;

        foreach (var (term, idf) in activeQueryTerms)
        {
            // BM25F: weighted, per-field length-normalized pseudo term frequency
            double pseudoTf = 0.0;
            for (int f = 0; f < fieldLimit; f++)
            {
                var tfMap = doc.FieldTermFrequencies[f];
                if (tfMap is null || avgFieldLength[f] <= 0.0) continue;
                if (!tfMap.TryGetValue(term, out var tf)) continue;

                double norm = 1.0 - B + B * (doc.FieldLengths[f] / avgFieldLength[f]);
                pseudoTf += weights[f] * tf / norm;
            }

            if (pseudoTf <= 0.0 || !double.IsFinite(pseudoTf)) continue;

            score += idf * (pseudoTf * (K1 + 1.0) / (pseudoTf + K1));

            matched ??= [];
            matched.Add(term);
        }

        if (double.IsFinite(score) && score > 0.001)
        {
            results.Add((doc.TableId, score, matched ?? (IReadOnlyList<string>)[]));
        }
    }

    private static IndexedDocument? TokenizeDocument(Bm25Document entry)
    {
        var tfs = new Dictionary<string, int>?[CatalogSearchFieldWeights.FieldCount];
        var lengths = new int[CatalogSearchFieldWeights.FieldCount];
        bool any = false;

        for (int f = 0; f < CatalogSearchFieldWeights.FieldCount; f++)
        {
            var text = entry.TextOf(f);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var tokens = SmartSchemaTokenizer.Tokenize(text, expandAbbreviations: true);
            if (tokens.Length == 0) continue;

            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tokens)
            {
                tf[t] = tf.GetValueOrDefault(t) + 1;
            }

            tfs[f] = tf;
            lengths[f] = tokens.Length;
            any = true;
        }

        return any ? new IndexedDocument(entry.TableId, tfs, lengths, HasTokens: true) : null;
    }

    private static void AdjustStats(
        IndexedDocument doc,
        Dictionary<string, int> termCounts,
        long[] totalFieldLengths,
        int delta)
    {
        if (!doc.HasTokens) return;

        for (int f = 0; f < CatalogSearchFieldWeights.FieldCount; f++)
        {
            totalFieldLengths[f] += delta * (long)doc.FieldLengths[f];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in doc.FieldTermFrequencies)
        {
            if (tf is null) continue;
            foreach (var term in tf.Keys)
            {
                if (!seen.Add(term)) continue;

                var updated = termCounts.GetValueOrDefault(term) + delta;
                if (updated == 0)
                {
                    termCounts.Remove(term);
                }
                else
                {
                    termCounts[term] = updated;
                }
            }
        }
    }
}
