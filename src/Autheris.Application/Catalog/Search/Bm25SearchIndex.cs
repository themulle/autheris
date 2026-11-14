namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

/// <summary>
/// Thread-safe immutable Okapi BM25 index with incremental delta segments and tombstones.
/// Uses the Robertson-Sparck Jones IDF formula with smoothing.
/// </summary>
public sealed class Bm25SearchIndex
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    public sealed record IndexedDocument(
        TableIdentifier TableId,
        string[] Tokens,
        Dictionary<string, int> TermFrequencies);

    private readonly IReadOnlyList<IndexedDocument> _baseDocuments;
    private readonly IReadOnlyDictionary<TableIdentifier, int> _baseTableToIndex;
    private readonly IReadOnlyDictionary<string, int> _baseTermDocCounts;
    private readonly IReadOnlySet<int> _baseTombstones;
    private readonly IReadOnlyDictionary<TableIdentifier, IndexedDocument> _deltaDocuments;
    private readonly IReadOnlyDictionary<string, int> _termDocCountDeltas;
    private readonly int _activeDocCount;
    private readonly long _totalTokens;
    private readonly double _avgDocLength;

    public Bm25SearchIndex(IEnumerable<(TableIdentifier TableId, string Content)> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var documents = new List<IndexedDocument>();
        var baseTableToIndex = new Dictionary<TableIdentifier, int>();
        var termDocCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long totalTokens = 0;

        foreach (var (tableId, content) in source)
        {
            if (baseTableToIndex.TryGetValue(tableId, out var priorIdx))
            {
                // Replace duplicate identifier cleanly so base segment never holds stale duplicate postings.
                var prior = documents[priorIdx];
                totalTokens -= prior.Tokens.Length;
                foreach (var term in prior.TermFrequencies.Keys)
                {
                    var remaining = termDocCounts[term] - 1;
                    if (remaining <= 0)
                        termDocCounts.Remove(term);
                    else
                        termDocCounts[term] = remaining;
                }

                var replacement = TokenizeDocument(tableId, content);
                if (replacement is null)
                {
                    baseTableToIndex.Remove(tableId);
                    documents[priorIdx] = new IndexedDocument(tableId, [], []);
                    continue;
                }

                documents[priorIdx] = replacement;
                totalTokens += replacement.Tokens.Length;
                foreach (var term in replacement.TermFrequencies.Keys)
                {
                    termDocCounts[term] = termDocCounts.GetValueOrDefault(term) + 1;
                }
                continue;
            }

            var doc = TokenizeDocument(tableId, content);
            if (doc is null) continue;

            baseTableToIndex[tableId] = documents.Count;
            documents.Add(doc);
            totalTokens += doc.Tokens.Length;

            foreach (var term in doc.TermFrequencies.Keys)
            {
                termDocCounts[term] = termDocCounts.GetValueOrDefault(term) + 1;
            }
        }

        _baseDocuments = documents;
        _baseTableToIndex = baseTableToIndex;
        _baseTermDocCounts = termDocCounts;
        _baseTombstones = new HashSet<int>();
        _deltaDocuments = new Dictionary<TableIdentifier, IndexedDocument>();
        _termDocCountDeltas = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _activeDocCount = baseTableToIndex.Count;
        _totalTokens = totalTokens;
        _avgDocLength = _activeDocCount > 0 ? (double)totalTokens / _activeDocCount : 0.0;
    }

    private Bm25SearchIndex(
        IReadOnlyList<IndexedDocument> baseDocuments,
        IReadOnlyDictionary<TableIdentifier, int> baseTableToIndex,
        IReadOnlyDictionary<string, int> baseTermDocCounts,
        IReadOnlySet<int> baseTombstones,
        IReadOnlyDictionary<TableIdentifier, IndexedDocument> deltaDocuments,
        IReadOnlyDictionary<string, int> termDocCountDeltas,
        int activeDocCount,
        long totalTokens)
    {
        _baseDocuments = baseDocuments;
        _baseTableToIndex = baseTableToIndex;
        _baseTermDocCounts = baseTermDocCounts;
        _baseTombstones = baseTombstones;
        _deltaDocuments = deltaDocuments;
        _termDocCountDeltas = termDocCountDeltas;
        _activeDocCount = activeDocCount;
        _totalTokens = totalTokens;
        _avgDocLength = activeDocCount > 0 ? (double)totalTokens / activeDocCount : 0.0;
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
        ArgumentNullException.ThrowIfNull(removals);

        var removalSet = removals as HashSet<TableIdentifier> ?? [.. removals];
        var tombstones = new HashSet<int>(_baseTombstones);
        var deltaDocuments = new Dictionary<TableIdentifier, IndexedDocument>(_deltaDocuments);
        var termDeltas = new Dictionary<string, int>(_termDocCountDeltas, StringComparer.OrdinalIgnoreCase);
        int activeDocCount = _activeDocCount;
        long totalTokens = _totalTokens;

        void RemoveActive(TableIdentifier id)
        {
            if (deltaDocuments.Remove(id, out var existingDelta))
            {
                SubtractDoc(existingDelta, termDeltas, ref activeDocCount, ref totalTokens);
                return;
            }

            if (_baseTableToIndex.TryGetValue(id, out int baseIdx) && tombstones.Add(baseIdx))
            {
                SubtractDoc(_baseDocuments[baseIdx], termDeltas, ref activeDocCount, ref totalTokens);
            }
        }

        foreach (var (tableId, content) in upserts)
        {
            if (removalSet.Contains(tableId))
            {
                continue;
            }

            RemoveActive(tableId);

            var doc = TokenizeDocument(tableId, content);
            if (doc is null)
            {
                continue;
            }

            deltaDocuments[tableId] = doc;
            activeDocCount++;
            totalTokens += doc.Tokens.Length;
            foreach (var term in doc.TermFrequencies.Keys)
            {
                AdjustTermDelta(termDeltas, term, +1);
            }
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
            totalTokens);
    }

    public IReadOnlyList<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)> Search(
        string query,
        int topK = 25,
        Func<TableIdentifier, bool>? isAllowed = null)
    {
        var queryTerms = SmartSchemaTokenizer.Tokenize(query, expandAbbreviations: false);
        if (queryTerms.Length == 0 || _activeDocCount == 0 || _avgDocLength <= 0.0) return [];

        var activeQueryTerms = new List<(string Term, double Idf)>(queryTerms.Length);
        foreach (var term in queryTerms)
        {
            int df = _baseTermDocCounts.GetValueOrDefault(term) + _termDocCountDeltas.GetValueOrDefault(term);
            if (df <= 0) continue;

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

            var doc = _baseDocuments[i];
            if (doc.Tokens.Length == 0 || (isAllowed != null && !isAllowed(doc.TableId)))
            {
                continue;
            }

            ScoreDocument(doc, activeQueryTerms, _avgDocLength, results);
        }

        foreach (var doc in _deltaDocuments.Values)
        {
            if (isAllowed != null && !isAllowed(doc.TableId))
            {
                continue;
            }

            ScoreDocument(doc, activeQueryTerms, _avgDocLength, results);
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }

    private static void ScoreDocument(
        IndexedDocument doc,
        List<(string Term, double Idf)> activeQueryTerms,
        double avgDocLength,
        List<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)> results)
    {
        double score = 0.0;
        List<string>? matched = null;

        foreach (var (term, idf) in activeQueryTerms)
        {
            if (!doc.TermFrequencies.TryGetValue(term, out var tf)) continue;

            double numerator = tf * (K1 + 1.0);
            double denominator = tf + K1 * (1.0 - B + B * (doc.Tokens.Length / avgDocLength));
            score += idf * (numerator / denominator);

            matched ??= [];
            matched.Add(term);
        }

        if (score > 0.001)
        {
            results.Add((doc.TableId, score, matched ?? (IReadOnlyList<string>)[]));
        }
    }

    private static IndexedDocument? TokenizeDocument(TableIdentifier tableId, string content)
    {
        var tokens = SmartSchemaTokenizer.Tokenize(content, expandAbbreviations: true);
        if (tokens.Length == 0) return null;

        var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            tf[t] = tf.GetValueOrDefault(t) + 1;
        }

        return new IndexedDocument(tableId, tokens, tf);
    }

    private static void SubtractDoc(
        IndexedDocument doc,
        Dictionary<string, int> termDeltas,
        ref int activeDocCount,
        ref long totalTokens)
    {
        activeDocCount--;
        totalTokens -= doc.Tokens.Length;
        foreach (var term in doc.TermFrequencies.Keys)
        {
            AdjustTermDelta(termDeltas, term, -1);
        }
    }

    private static void AdjustTermDelta(Dictionary<string, int> termDeltas, string term, int delta)
    {
        var updated = termDeltas.GetValueOrDefault(term) + delta;
        if (updated == 0)
        {
            termDeltas.Remove(term);
        }
        else
        {
            termDeltas[term] = updated;
        }
    }
}
