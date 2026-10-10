namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

/// <summary>
/// Thread-sicherer In-Memory Okapi BM25 Index für Schema-Metadaten.
/// Verwendet die Robertson-Spärck-Jones IDF-Formel mit Glättung.
/// </summary>
public sealed class Bm25SearchIndex
{
    private const double K1 = 1.5;
    private const double B = 0.75;

    public sealed record IndexedDocument(
        TableIdentifier TableId, 
        string[] Tokens, 
        Dictionary<string, int> TermFrequencies);

    private readonly List<IndexedDocument> _documents;
    private readonly Dictionary<string, double> _idfMap;
    private readonly double _avgDocLength;

    public Bm25SearchIndex(IEnumerable<(TableIdentifier TableId, string Content)> source)
    {
        _documents = [];
        _idfMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        long totalTokens = 0;
        foreach (var (tableId, content) in source)
        {
            var tokens = SmartSchemaTokenizer.Tokenize(content, expandAbbreviations: true);
            if (tokens.Length == 0) continue;

            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tokens)
            {
                tf[t] = tf.GetValueOrDefault(t) + 1;
            }

            _documents.Add(new IndexedDocument(tableId, tokens, tf));
            totalTokens += tokens.Length;
        }

        if (_documents.Count == 0)
        {
            _avgDocLength = 0;
            return;
        }

        _avgDocLength = (double)totalTokens / _documents.Count;

        // Robertson-Spärck-Jones IDF: ln(1 + (N - n + 0.5) / (n + 0.5))
        int totalDocs = _documents.Count;
        var termDocCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in _documents)
        {
            foreach (var term in doc.TermFrequencies.Keys)
            {
                termDocCounts[term] = termDocCounts.GetValueOrDefault(term) + 1;
            }
        }

        foreach (var (term, count) in termDocCounts)
        {
            _idfMap[term] = Math.Log(1.0 + (totalDocs - count + 0.5) / (count + 0.5));
        }
    }

    public IReadOnlyList<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)> Search(string query, int topK = 25)
    {
        var queryTerms = SmartSchemaTokenizer.Tokenize(query, expandAbbreviations: false);
        if (queryTerms.Length == 0 || _documents.Count == 0) return [];

        var results = new List<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)>();

        foreach (var doc in _documents)
        {
            double score = 0.0;
            List<string>? matched = null;

            foreach (var term in queryTerms)
            {
                if (!_idfMap.TryGetValue(term, out var idf)) continue;
                if (!doc.TermFrequencies.TryGetValue(term, out var tf)) continue;

                // BM25 Term-Gewichtung mit Längennormierung
                double numerator = tf * (K1 + 1.0);
                double denominator = tf + K1 * (1.0 - B + B * (doc.Tokens.Length / _avgDocLength));
                score += idf * (numerator / denominator);

                matched ??= [];
                matched.Add(term);
            }

            if (score > 0.001)
            {
                results.Add((doc.TableId, score, matched ?? (IReadOnlyList<string>)[]));
            }
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }
}
