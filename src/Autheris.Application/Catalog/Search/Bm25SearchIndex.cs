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
/// Thread-sicherer In-Memory Okapi BM25F Index für Schema-Metadaten: gewichtete Termfrequenzen je Feld,
/// eine Sättigung und eine Längennormierung je Feld gegen den Feld-Durchschnitt.
/// Verwendet die Robertson-Spärck-Jones IDF-Formel mit Glättung.
/// </summary>
public sealed class Bm25SearchIndex
{
    private const double K1 = 1.5;
    private const double B = 0.75;
    private const int FirstColumnField = (int)Bm25Field.ColumnNames;

    /// <summary>Query tokens shorter than this match whole words only.</summary>
    public const int MinPartialMatchLength = 4;

    /// <summary>Indexed tokens one query token may expand to (bounds the cost of a query).</summary>
    public const int MaxPartialExpansions = 32;

    /// <summary>Query tokens scored per search; further tokens are ignored (availability bound).</summary>
    public const int MaxQueryTerms = 64;

    /// <summary>A partial match scores this fraction of a whole-word match of the same field statistics.</summary>
    private const double PartialMatchFactor = 0.4;

    private sealed record IndexedDocument(
        TableIdentifier TableId,
        Dictionary<string, int>?[] FieldTermFrequencies,
        int[] FieldLengths);

    private readonly List<IndexedDocument> _documents;
    private readonly Dictionary<string, double> _idfMap;
    private readonly double[] _avgFieldLength = new double[CatalogSearchFieldWeights.FieldCount];
    private readonly double[] _weights;

    // Distinct indexed tokens ordered by (length, ordinal): partial matches prefer the tokens closest to the whole word.
    // The table-level vocabulary excludes tokens that occur only in column fields, so a caller with hidden columns
    // never gets an expansion (or a cap slot) taken by hidden column text.
    private string[] _vocabulary = [];
    private string[] _tableLevelVocabulary = [];

    /// <summary>Single-field convenience: the whole text is indexed as the short description.</summary>
    public Bm25SearchIndex(IEnumerable<(TableIdentifier TableId, string Content)> source)
        : this(source.Select(x => new Bm25Document(x.TableId, ShortDescription: x.Content)))
    {
    }

    public Bm25SearchIndex(IEnumerable<Bm25Document> source, IReadOnlyList<double>? fieldWeights = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _weights = ValidateWeights(fieldWeights ?? CatalogSearchFieldWeights.Default);
        _documents = [];
        _idfMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var totalLengths = new long[CatalogSearchFieldWeights.FieldCount];
        foreach (var entry in source)
        {
            var tfs = new Dictionary<string, int>?[CatalogSearchFieldWeights.FieldCount];
            var lengths = new int[CatalogSearchFieldWeights.FieldCount];
            var any = false;

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
                totalLengths[f] += tokens.Length;
                any = true;
            }

            if (any)
            {
                _documents.Add(new IndexedDocument(entry.TableId, tfs, lengths));
            }
        }

        if (_documents.Count == 0)
        {
            return;
        }

        for (int f = 0; f < _avgFieldLength.Length; f++)
        {
            _avgFieldLength[f] = (double)totalLengths[f] / _documents.Count;
        }

        // Robertson-Spärck-Jones IDF: ln(1 + (N - n + 0.5) / (n + 0.5)); n counts documents with the term in any field.
        int totalDocs = _documents.Count;
        var termDocCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in _documents)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tf in doc.FieldTermFrequencies)
            {
                if (tf is null) continue;
                foreach (var term in tf.Keys)
                {
                    if (seen.Add(term))
                    {
                        termDocCounts[term] = termDocCounts.GetValueOrDefault(term) + 1;
                    }
                }
            }
        }

        foreach (var (term, count) in termDocCounts)
        {
            _idfMap[term] = Math.Log(1.0 + (totalDocs - count + 0.5) / (count + 0.5));
        }

        var tableLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in _documents)
        {
            for (int f = 0; f < FirstColumnField; f++)
            {
                if (doc.FieldTermFrequencies[f] is { } tf)
                {
                    tableLevel.UnionWith(tf.Keys);
                }
            }
        }

        _vocabulary = SortVocabulary(_idfMap.Keys);
        _tableLevelVocabulary = SortVocabulary(tableLevel);
    }

    private static string[] SortVocabulary(IEnumerable<string> tokens)
    {
        var array = tokens.ToArray();
        Array.Sort(array, static (a, b) =>
        {
            int byLength = a.Length.CompareTo(b.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a, b);
        });
        return array;
    }

    /// <summary>
    /// Indexed tokens that contain <paramref name="term"/> (ordinal; tokens are already lower-cased invariantly by the tokenizer),
    /// shortest first, at most <see cref="MaxPartialExpansions"/>. Empty for tokens below <see cref="MinPartialMatchLength"/>.
    /// </summary>
    private static List<string> Expand(string term, string[] vocabulary)
    {
        var result = new List<string>();
        if (term.Length < MinPartialMatchLength) return result;

        foreach (var candidate in vocabulary)
        {
            if (candidate.Length <= term.Length) continue;
            if (candidate.Contains(term, StringComparison.Ordinal))
            {
                result.Add(candidate);
                if (result.Count >= MaxPartialExpansions) break;
            }
        }

        return result;
    }

    private sealed record QueryTermPlan(string Term, double? Idf, List<(string Token, double Idf)> Expansions);

    private QueryTermPlan[] BuildPlan(string[] queryTerms, string[] vocabulary)
    {
        int count = Math.Min(queryTerms.Length, MaxQueryTerms);
        var plan = new QueryTermPlan[count];
        for (int i = 0; i < count; i++)
        {
            var term = queryTerms[i];
            double? idf = _idfMap.TryGetValue(term, out var v) ? v : null;
            var expansions = Expand(term, vocabulary)
                .Select(e => (e, _idfMap[e]))
                .ToList();
            plan[i] = new QueryTermPlan(term, idf, expansions);
        }

        return plan;
    }

    private double PseudoTf(IndexedDocument doc, string token, int fieldLimit)
    {
        double pseudoTf = 0.0;
        for (int f = 0; f < fieldLimit; f++)
        {
            var tfMap = doc.FieldTermFrequencies[f];
            if (tfMap is null || _avgFieldLength[f] <= 0.0) continue;
            if (!tfMap.TryGetValue(token, out var tf)) continue;

            double norm = 1.0 - B + B * (doc.FieldLengths[f] / _avgFieldLength[f]);
            pseudoTf += _weights[f] * tf / norm;
        }

        return double.IsFinite(pseudoTf) ? pseudoTf : 0.0;
    }

    private static double Saturate(double idf, double pseudoTf) => idf * (pseudoTf * (K1 + 1.0) / (pseudoTf + K1));

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
        if (queryTerms.Length == 0 || _documents.Count == 0) return [];

        var fullPlan = BuildPlan(queryTerms, _vocabulary);
        var tableLevelPlan = isColumnRestricted != null ? BuildPlan(queryTerms, _tableLevelVocabulary) : fullPlan;

        var results = new List<(TableIdentifier TableId, double Score, IReadOnlyList<string> MatchedTerms)>();

        foreach (var doc in _documents)
        {
            if (isAllowed != null && !isAllowed(doc.TableId))
            {
                continue;
            }

            bool restricted = isColumnRestricted != null && isColumnRestricted(doc.TableId);
            int fieldLimit = restricted ? FirstColumnField : CatalogSearchFieldWeights.FieldCount;
            var plan = restricted ? tableLevelPlan : fullPlan;

            double score = 0.0;
            List<string>? matched = null;

            foreach (var entry in plan)
            {
                // BM25F: weighted, per-field length-normalized pseudo term frequency
                double pseudoTf = entry.Idf is not null ? PseudoTf(doc, entry.Term, fieldLimit) : 0.0;
                if (pseudoTf > 0.0)
                {
                    score += Saturate(entry.Idf!.Value, pseudoTf);
                    matched ??= [];
                    matched.Add(entry.Term);
                    continue;
                }

                // No whole-word match in this document: the best partial match counts, at a lower weight.
                double bestPartial = 0.0;
                string? bestToken = null;
                foreach (var (token, expansionIdf) in entry.Expansions)
                {
                    double partialTf = PseudoTf(doc, token, fieldLimit);
                    if (partialTf <= 0.0) continue;

                    double partial = PartialMatchFactor * Saturate(expansionIdf, partialTf);
                    if (partial > bestPartial)
                    {
                        bestPartial = partial;
                        bestToken = token;
                    }
                }

                if (bestToken is not null)
                {
                    score += bestPartial;
                    matched ??= [];
                    matched.Add(bestToken);
                }
            }

            if (double.IsFinite(score) && score > 0.001)
            {
                results.Add((doc.TableId, score, matched ?? (IReadOnlyList<string>)[]));
            }
        }

        return results.OrderByDescending(x => x.Score).Take(topK).ToList();
    }
}
